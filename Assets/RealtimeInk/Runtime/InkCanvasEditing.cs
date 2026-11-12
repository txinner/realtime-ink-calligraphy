using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RealtimeInk
{
    public sealed partial class RealtimeInkCanvas
    {
        // 存最终脚印和实际 CA 步骤，不存每笔一张 2048² RT，也不按当前参数重新拟合历史轨迹。
        // 每次撤销/重做分帧重放 GPU 命令；恢复期间保留上一张完整输出，防止闪白。
        readonly List<StrokeRecord> history = new List<StrokeRecord>();
        int historyCursor, replayStroke, replayCommand, replayStep;
        bool recordingStroke;
        StrokeRecord activeRecord;
        const int ReplayWorkPerFrame = 12;

        public bool IsRestoringHistory { get; private set; }
        public int StrokeCount => historyCursor;
        public int RedoCount => history.Count - historyCursor;
        public bool CanUndo => initialized && !IsRestoringHistory && (historyCursor > 0 || pendingStamps.Count > 0);
        public bool CanRedo => initialized && !IsRestoringHistory && !pointerDown && RedoCount > 0;
        public string EditStatus { get; private set; } = "";
        public string LastSavedPath { get; private set; } = "";
        /// <summary>空值使用系统“图片/RealtimeInk”；测试或宿主可显式设置目录。</summary>
        public string ExportDirectoryOverride { get; set; }

        readonly struct GpuInkSettings
        {
            public readonly float noiseFrequency, saturationLimit, saturationFromDeposit;
            public readonly float inkDiffusion, waterDiffusion, dryingRate, freezeWetness;
            public readonly float saturationFromInflux, absorptionRate, anisotropy, fiberAngle, fiberRandomness;
            public GpuInkSettings(InkBrushSettings brushSettings, InkPaperSettings paperSettings)
            {
                noiseFrequency = brushSettings.noiseFrequency;
                saturationLimit = paperSettings.saturationLimit;
                saturationFromDeposit = paperSettings.saturationFromDeposit;
                inkDiffusion = paperSettings.inkDiffusion; waterDiffusion = paperSettings.waterDiffusion;
                dryingRate = paperSettings.dryingRate; freezeWetness = paperSettings.freezeWetness;
                saturationFromInflux = paperSettings.saturationFromInflux; absorptionRate = paperSettings.absorptionRate;
                anisotropy = paperSettings.anisotropy; fiberAngle = paperSettings.fiberAngle;
                fiberRandomness = paperSettings.fiberRandomness;
            }
        }
        sealed class HistoryCommand
        {
            public DropletStamp[] stamps; // 非 null：一批实际落墨；null：CA 固定步长迭代。
            public GpuInkSettings settings;
            public float step;
            public int steps;
        }
        sealed class StrokeRecord
        {
            public readonly List<HistoryCommand> commands = new List<HistoryCommand>();
            public float wetBudget, debt;
        }

        void BeginHistoryStroke()
        {
            recordingStroke = true; activeRecord = null;
        }
        void EndHistoryStroke()
        {
            if (!initialized || IsRestoringHistory) return;
            // 先提交收锋，防止同一帧内两次点击的脚印混入同一个历史单位。
            DrawPendingStamps(); CaptureHistoryClock();
            recordingStroke = false; activeRecord = null;
        }
        void RecordStampBatch(GpuInkSettings settings)
        {
            if (IsRestoringHistory || !recordingStroke) return;
            if (activeRecord == null)
            {
                // 真正落下第一滴墨才建立新分支；空触摸不破坏重做列表。
                if (historyCursor < history.Count) history.RemoveRange(historyCursor, history.Count - historyCursor);
                activeRecord = new StrokeRecord(); history.Add(activeRecord); historyCursor++;
                EditStatus = "";
            }
            activeRecord.commands.Add(new HistoryCommand { stamps = pendingStamps.ToArray(), settings = settings });
        }
        void RecordSimulation(GpuInkSettings settings, float step, int steps)
        {
            if (steps == 0 || historyCursor == 0 || IsRestoringHistory || historyCursor != history.Count) return;
            history[historyCursor - 1].commands.Add(new HistoryCommand { settings = settings, step = step, steps = steps });
        }
        void CaptureHistoryClock()
        {
            if (historyCursor == 0 || IsRestoringHistory || historyCursor != history.Count) return;
            StrokeRecord record = history[historyCursor - 1];
            record.wetBudget = remainingWetBudget; record.debt = simulationDebt;
        }
        void ResetHistory()
        {
            history.Clear(); historyCursor = 0; recordingStroke = false; activeRecord = null;
            IsRestoringHistory = false; replayStroke = replayCommand = replayStep = 0;
        }
        void FinishEditingInput()
        {
            ProcessInput();
            if (pointerDown) path.End(lastPointerPosition, lastPointerTime, lastPointerPressure, lastPressureAvailable);
            EndHistoryStroke();
            pointerDown = false; pendingInput.Clear(); path.Cancel();
            continuationValid = false; LastStrokeContinued = false; secondsSinceLift = 0f;
        }

        public bool UndoStroke()
        {
            if (!initialized || IsRestoringHistory) return false;
            FinishEditingInput();
            if (historyCursor == 0) return false;
            historyCursor--; StartHistoryReplay("已撤销上一笔"); return true;
        }
        public bool RedoStroke()
        {
            if (!CanRedo) return false;
            FinishEditingInput();
            // 队列中的新笔可能已建立新分支，必须重新检查。
            if (historyCursor == history.Count) return false;
            historyCursor++; StartHistoryReplay("已重复下一笔"); return true;
        }
        string replayFinishedMessage;
        void StartHistoryReplay(string completedMessage)
        {
            replayFinishedMessage = completedMessage;
            IsRestoringHistory = true; replayStroke = replayCommand = replayStep = 0;
            EditStatus = "正在恢复笔画…";
            ResetGpuState();
            if (historyCursor == 0) CompleteHistoryReplay();
        }
        void PumpHistoryReplay()
        {
            int budget = ReplayWorkPerFrame;
            LastFrameStampCount = LastFrameCASteps = 0;
            while (replayStroke < historyCursor && budget > 0)
            {
                List<HistoryCommand> commands = history[replayStroke].commands;
                if (replayCommand >= commands.Count)
                {
                    replayStroke++; replayCommand = replayStep = 0; continue;
                }
                HistoryCommand command = commands[replayCommand];
                if (command.stamps != null)
                {
                    DrawStampBatch(command.stamps, command.settings);
                    budget -= Mathf.Max(1, (command.stamps.Length + InkTileBatch.BatchCapacity - 1) / InkTileBatch.BatchCapacity);
                    replayCommand++;
                }
                else
                {
                    ConfigureCAStep(command.settings, command.step);
                    int count = Mathf.Min(budget, command.steps - replayStep);
                    for (int i = 0; i < count; i++) DispatchCAStep();
                    replayStep += count; budget -= count; LastFrameCASteps += count;
                    if (replayStep == command.steps) { replayCommand++; replayStep = 0; }
                }
            }
            if (replayStroke == historyCursor) CompleteHistoryReplay();
        }
        void CompleteHistoryReplay()
        {
            if (historyCursor > 0)
            {
                StrokeRecord record = history[historyCursor - 1];
                remainingWetBudget = record.wetBudget; simulationDebt = record.debt;
            }
            IsRestoringHistory = false;
            Composite();
            EditStatus = replayFinishedMessage;
        }

        public string ExportDirectory
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ExportDirectoryOverride)) return Path.GetFullPath(ExportDirectoryOverride);
                string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                return Path.Combine(string.IsNullOrEmpty(pictures) ? Application.persistentDataPath : pictures, "RealtimeInk");
            }
        }

        /// <summary>仅点击保存时读回 GPU。RGBA PNG 为 straight alpha，不包含宣纸、UI 或调试通道。</summary>
        public bool SaveTransparentPng(string filePath = null)
        {
            if (!initialized || IsRestoringHistory) return false;
            RenderTexture export = null;
            Texture2D pixels = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                FinishEditingInput();
                // 更新材质绑定，不推进模拟时间，导出点击这一刻的已提交墨迹。
                Composite();
                string destination = string.IsNullOrEmpty(filePath)
                    ? Path.Combine(ExportDirectory, "Ink-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                        Guid.NewGuid().ToString("N").Substring(0, 6) + ".png")
                    : Path.GetFullPath(filePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                export = CreateRT("Ink.TransparentExport", width, height, RenderTextureFormat.ARGB32, false);
                pixels = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                composite.SetInt("_ExportTransparent", 1);
                Graphics.Blit(pigmentCanvas, export, composite, 0);
                RenderTexture.active = export;
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                pixels.Apply(false, false);
                byte[] png = pixels.EncodeToPNG();
                if (png == null || png.Length == 0) throw new IOException("PNG 编码失败。");
                // CreateNew 防止连续点击或同名导出覆盖已有作品。
                using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                    stream.Write(png, 0, png.Length);
                LastSavedPath = destination;
                EditStatus = "已保存透明 PNG：\n" + destination;
                Debug.Log("INK_PNG " + destination, this);
                return true;
            }
            catch (Exception error)
            {
                EditStatus = "保存失败：" + error.Message;
                Debug.LogWarning(EditStatus, this);
                return false;
            }
            finally
            {
                if (composite) composite.SetInt("_ExportTransparent", 0);
                RenderTexture.active = previous;
                ReleaseRT(ref export); DestroyResource(pixels);
            }
        }
    }
}
