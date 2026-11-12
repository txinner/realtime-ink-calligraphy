using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealtimeInk
{
    /// <summary>只有显式命令行 --ink-smoke-output 路径 才运行；普通启动是一张空白纸。</summary>
    public sealed class InkPlayerSmoke : MonoBehaviour
    {
        string outputDirectory;
        readonly StringBuilder errors = new StringBuilder();
        int generatedNodes;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void StartWhenRequested()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--ink-smoke-output")
                {
                    var smoke = new GameObject("Player Smoke Test").AddComponent<InkPlayerSmoke>();
                    smoke.outputDirectory = Path.GetFullPath(args[i + 1]);
                    Application.runInBackground = true;
                    QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
                    break;
                }
        }
        void OnEnable() { Application.logMessageReceived += Log; }
        void OnDisable() { Application.logMessageReceived -= Log; }
        void Log(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                errors.AppendLine(message).AppendLine(stack);
        }
        IEnumerator Start()
        {
            Directory.CreateDirectory(outputDirectory);
            for (int i = 0; i < 8; i++) yield return null;
            var ink = FindFirstObjectByType<RealtimeInkCanvas>();
            var panel = FindFirstObjectByType<InkDemoPanel>();
            if (!ink || !ink.IsInitialized || !panel || !panel.IsReady)
            {
                Finish(false, "Player scene/UI/GPU initialization failed."); yield break;
            }
            ink.SetInputEnabled(false); panel.RewriteButton.onClick.Invoke();
            ink.ExportDirectoryOverride = Path.Combine(outputDirectory, "Exports");
            ink.PathNodeGenerated += CountNode;
            for (int styleIndex = 0; styleIndex < 6; styleIndex++)
            {
                ink.techniques.uniformLine = styleIndex == 3;
                ink.techniques.silkwormSwallowtail = styleIndex == 4;
                ink.techniques.stoneEdges = styleIndex == 5;
                bool h = ink.techniques.silkwormSwallowtail, u = ink.techniques.uniformLine, b = ink.techniques.stoneEdges;
                panel.StyleButtons[styleIndex].onClick.Invoke();
                if (ink.LastLoadedStyle != (InkStyle)styleIndex || h != ink.techniques.silkwormSwallowtail ||
                    u != ink.techniques.uniformLine || b != ink.techniques.stoneEdges)
                {
                    Finish(false, "Player style button/independent helper failed."); yield break;
                }
                ink.RefillInk();
                for (int i = 0; i <= 45; i++)
                {
                    float t = i / 45f;
                    Vector2 uv = new Vector2(0.14f + 0.72f * t,
                        0.89f - styleIndex * 0.15f + Mathf.Sin(t * Mathf.PI * 2) * 0.025f);
                    ink.SubmitCanvasSample(uv, Time.realtimeSinceStartupAsDouble,
                        i == 0 ? InkPointerPhase.Begin : InkPointerPhase.Move,
                        0.2f + 0.65f * Mathf.Sin(t * Mathf.PI));
                    yield return null;
                    if (i == 0 && h) yield return new WaitForSeconds(0.4f);
                    if (i == 45)
                    {
                        if (h) yield return new WaitForSeconds(0.4f);
                        ink.SubmitCanvasSample(uv, Time.realtimeSinceStartupAsDouble, InkPointerPhase.End, 0.4f);
                        yield return null;
                    }
                }
            }
            for (int i = 0; i < 120; i++) yield return null;
            if (ink.StrokeCount != 6 || !ink.CanUndo)
            { Finish(false, "Player did not record six gestures."); yield break; }
            panel.UndoButton.onClick.Invoke();
            for (int i = 0; i < 1800 && ink.IsRestoringHistory; i++) yield return null;
            if (ink.IsRestoringHistory || ink.StrokeCount != 5 || !ink.CanRedo)
            { Finish(false, "Player undo button failed."); yield break; }
            panel.RedoButton.onClick.Invoke();
            for (int i = 0; i < 1800 && ink.IsRestoringHistory; i++) yield return null;
            if (ink.IsRestoringHistory || ink.StrokeCount != 6 || ink.CanRedo)
            { Finish(false, "Player redo button failed."); yield break; }
            panel.SaveButton.onClick.Invoke();
            if (!File.Exists(ink.LastSavedPath))
            { Finish(false, "Player save button failed: " + ink.EditStatus); yield break; }
            int transparentPixels = 0, partialAlphaPixels = 0;
            var exported = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            bool pngLoaded = exported.LoadImage(File.ReadAllBytes(ink.LastSavedPath));
            if (pngLoaded)
                foreach (Color32 pixel in exported.GetPixels32())
                {
                    if (pixel.a == 0) transparentPixels++;
                    if (pixel.a > 0 && pixel.a < 255) partialAlphaPixels++;
                }
            bool validPNG = pngLoaded && exported.width == ink.canvasResolution.x && exported.height == ink.canvasResolution.y &&
                transparentPixels > exported.width * exported.height / 2 && partialAlphaPixels > 100;
            Destroy(exported);
            if (!validPNG) { Finish(false, "Player PNG is not a transparent full-resolution ink export."); yield break; }
            var read = AsyncGPUReadback.Request(ink.Output, 0, TextureFormat.RGBAFloat);
            while (!read.done) yield return null;
            if (read.hasError) { Finish(false, "Player GPU readback failed."); yield break; }
            int darkPixels = 0;
            var pixels = read.GetData<Color>();
            for (int i = 0; i < pixels.Length; i++) if (pixels[i].grayscale < 0.3f) darkPixels++;
            if (generatedNodes < 100 || darkPixels < 1000) { Finish(false, "Player did not render the submitted strokes."); yield break; }
            string screenshot = Path.Combine(outputDirectory, "player-preview.png");
            ScreenCapture.CaptureScreenshot(screenshot);
            for (int i = 0; i < 60 && !File.Exists(screenshot); i++) yield return null;
            // 给异步 PNG 写入留两个渲染帧；不把截图请求成功当作完成。
            yield return null; yield return null;
            ink.PathNodeGenerated -= CountNode;
            int visiblePixels = 0;
            if (File.Exists(screenshot))
            {
                var captured = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (captured.LoadImage(File.ReadAllBytes(screenshot)))
                    foreach (Color32 pixel in captured.GetPixels32())
                        if (pixel.r > 30 || pixel.g > 30 || pixel.b > 30) visiblePixels++;
                Destroy(captured);
            }
            bool ok = errors.Length == 0 && visiblePixels > Screen.width * Screen.height / 10;
            panel.RewriteButton.onClick.Invoke();
            ok &= ink.StrokeCount == 0 && ink.RedoCount == 0 && !ink.CanUndo && !ink.CanRedo;
            Finish(ok, "Windows player | " + Application.unityVersion + " | " + SystemInfo.graphicsDeviceType +
                " | nodes=" + generatedNodes + " | darkPixels=" + darkPixels + " | visiblePixels=" + visiblePixels +
                " | screen=" + Screen.width + "x" + Screen.height + " | undo/redo/rewrite/save=PASS" +
                " | transparentPixels=" + transparentPixels + " | partialAlphaPixels=" + partialAlphaPixels +
                "\nPNG: " + ink.LastSavedPath);
        }
        void CountNode(InkPathNode node) { generatedNodes++; }
        void Finish(bool ok, string message)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "runtime-smoke.txt"),
                (ok ? "PASS" : "FAIL") + "\n" + message + "\n" + errors);
            Debug.Log("INK_PLAYER_SMOKE " + (ok ? "PASS " : "FAIL ") + message);
            Application.Quit(ok ? 0 : 1);
        }
    }
}
