using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace RealtimeInk.Editor
{
    /// <summary>手动验证工具，无 NUnit/第三方依赖。CPU 读取像素仅用于验证/截图，运行时没有 readback。</summary>
    public static class InkValidation
    {
        static readonly StringBuilder report = new StringBuilder();
        static int checks;

        [MenuItem("Tools/Realtime Ink/Run Validation")]
        public static void Run()
        {
            report.Clear(); checks = 0;
            try
            {
                Log("Unity " + Application.unityVersion + " | " + SystemInfo.graphicsDeviceType + " | " + SystemInfo.graphicsDeviceName);
                ValidatePath();
                InkDemoBuilder.EnsurePipeline();
                ValidateGPU();
                InkStyleValidation.Run();
                InkEditingValidation.Run();
                BenchmarkGPU(1024);
                BenchmarkGPU(2048);
                Log("PASS: " + checks + " assertions.");
                Directory.CreateDirectory("Validation");
                File.WriteAllText("Validation/results.txt", report.ToString());
                File.WriteAllText("Validation/results-" + SystemInfo.graphicsDeviceType + ".txt", report.ToString());
                if (Application.isBatchMode)
                {
                    ConfigureDemoProject();
                    if (!File.Exists(InkDemoBuilder.Root + "Demo/InkDemo.unity")) InkDemoBuilder.CreateDemoScene();
                    AssetDatabase.ExportPackage("Assets/RealtimeInk", "RealtimeInk.unitypackage", ExportPackageOptions.Recurse);
                }
            }
            catch (Exception error)
            {
                Log("FAIL: " + error);
                Directory.CreateDirectory("Validation");
                File.WriteAllText("Validation/results.txt", report.ToString());
                Debug.LogException(error);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        static void ConfigureDemoProject()
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            var serialized = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
            SerializedProperty handler = serialized.FindProperty("activeInputHandler") ?? serialized.FindProperty("m_ActiveInputHandler");
            if (handler != null) { handler.intValue = 1; serialized.ApplyModifiedPropertiesWithoutUndo(); }
        }

        static void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
        static void Log(string text) { report.AppendLine(text); Debug.Log(text); }

        static void ValidatePath()
        {
            var input = new InkInputSettings();
            var brush = new InkBrushSettings { minimumRadius = 10, maximumRadius = 10 };
            var nodes = new List<InkPathNode>();
            var path = new InkPathSampler(input, brush, nodes.Add);
            path.Begin(new Vector2(10, 100), 0, 0.7f, true);
            for (int i = 1; i <= 100; i++) path.Move(new Vector2(10 + i * 8, 100), i / 120.0, 0.7f, true);
            path.End(new Vector2(810.3f, 100), 1, 0.7f, true);
            Check(nodes.Count > 200, "Insufficient arc samples.");
            Check(Vector2.Distance(nodes[nodes.Count - 1].canvasPosition, new Vector2(810.3f, 100)) < 0.005f, "Release endpoint lost.");
            for (int i = 1; i < nodes.Count; i++)
            {
                Check(nodes[i].canvasPosition.x > nodes[i - 1].canvasPosition.x, "Duplicate/backward node on straight line.");
                Check(Mathf.Abs(nodes[i].canvasPosition.y - 100) < 0.005f, "Straight line deviates.");
                Check(Vector2.Distance(nodes[i].canvasPosition, nodes[i - 1].canvasPosition) <= brush.maximumSpacing + 0.01f, "Arc spacing gap.");
            }
            int oldCount = nodes.Count;
            path.Begin(Vector2.zero, 2, 0.5f, true); path.End(Vector2.zero, 2.01, 0.5f, true);
            Check(nodes.Count == oldCount + 1, "Tap duplicates ink or vanishes.");
            nodes.Clear();
            path.Begin(Vector2.zero, 3, 0, false);
            path.Move(new Vector2(0.1f, 0.1f), 4, 0, false);
            Check(nodes.Count == 1, "Jitter creates extra stamps.");
            path.Move(new Vector2(500, 200), 14, 0, false);
            path.End(new Vector2(500, 200), 14.01, 0, false);
            Check(nodes.Count < 3000, "Stationary interval causes unbounded resampling.");
            Log("Path: endpoints, tap, jitter, long pause, monotonic arc cursor passed.");
        }

        static void DrawLine(RealtimeInkCanvas ink, float y, double time, bool tickEach = true)
        {
            ink.SubmitCanvasSample(new Vector2(0.1f, y), time, InkPointerPhase.Begin, 0.75f);
            for (int i = 1; i <= 50; i++)
            {
                ink.SubmitCanvasSample(new Vector2(0.1f + 0.8f * i / 50f, y), time + i / 90.0, InkPointerPhase.Move, 0.75f);
                if (tickEach) ink.Tick(1f / 90f);
            }
            ink.SubmitCanvasSample(new Vector2(0.9f, y), time + 0.6, InkPointerPhase.End, 0.75f);
            ink.Tick(1f / 60f);
        }

        static void ValidateGPU()
        {
            var go = new GameObject("Ink Validation");
            var displayObject = new GameObject("Validation Display", typeof(RectTransform), typeof(RawImage));
            var displayRect = displayObject.GetComponent<RectTransform>();
            displayRect.sizeDelta = new Vector2(512, 512);
            displayRect.position = new Vector3(256, 256, 0);
            var ink = go.AddComponent<RealtimeInkCanvas>();
            InkDemoBuilder.AssignReferences(ink, displayObject.GetComponent<RawImage>());
            ink.canvasResolution = new Vector2Int(512, 512);
            ink.diffusionResolution = new Vector2Int(128, 128);
            ink.brush.minimumRadius = 10; ink.brush.maximumRadius = 14;
            ink.brush.refillPerSecond = 0; ink.brush.consumptionPer1000Pixels = 0;
            try
            {
                Check(ink.Initialize(), "GPU initialization failed.");
                var shaderMessages = ShaderUtil.GetShaderMessages(ink.compositeShader);
                foreach (var message in shaderMessages)
                    Check(message.severity != UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error, message.message);
                DrawLine(ink, 0.72f, 0);
                Color[] main = Read(ink.PigmentCanvas);
                Color[] ca = Read(ink.DiffusionState);
                double total = 0, fixedMass = 0, mobileMass = 0, transient = 0;
                foreach (Color pixel in main) { total += pixel.a; fixedMass += pixel.r; transient += pixel.g + pixel.b; }
                foreach (Color pixel in ca) mobileMass += pixel.r * 16;
                double error = Math.Abs((fixedMass + mobileMass) - total) / Math.Max(1, total);
                Check(total > 100, "Droplet compute produced no ink.");
                Check(transient == 0, "Pending GB channels not cleared; would inject old ink again.");
                Check(error < 0.035, "Pigment conservation error >3.5%: " + error);
                Log("GPU mass: total=" + total.ToString("F3") + ", fixed+CA=" + (fixedMass + mobileMass).ToString("F3") +
                    ", relative error=" + error.ToString("P3"));
                Color[] firstOutput = Read(ink.Output);
                float firstValue = firstOutput[368 * 512 + 256].grayscale;
                Check(firstValue < 0.7f && firstValue > 0.001f, "Composite center is not ink (or shader is black).");
                Check(firstOutput[30 * 512 + 256].grayscale > firstValue + 0.1f, "Composite lost blank paper.");

                for (int i = 0; i < 240; i++) ink.Tick(1f / 60f);
                Color[] dry = Read(ink.DiffusionState);
                foreach (Color pixel in Read(ink.Wetness)) Check(pixel.r < 0.0001f, "Wetness did not reach exact freeze threshold.");
                for (int i = 0; i < 12; i++) ink.Tick(1f / 60f);
                Check(ink.LastFrameCASteps == 0, "Idle CA does not sleep.");
                Color[] stable = Read(ink.DiffusionState);
                for (int i = 0; i < dry.Length; i++) Check(dry[i] == stable[i], "Dry ink changes during idle frames.");
                DrawLine(ink, 0.20f, 8, false);
                Color[] afterDistant = Read(ink.DiffusionState);
                for (int y = 65; y < 128; y++)
                    for (int x = 0; x < 128; x++)
                        Check(dry[y * 128 + x] == afterDistant[y * 128 + x], "New distant stroke rewets old dry ink.");
                Log("GPU: dry freeze, idle sleep, and local-only rewet passed.");

                // 浓墨会到达 K-M 的厚层极限；用淡墨验证可见的叠加加深。
                ink.ClearCanvas(); ink.brush.pigmentLoad = 0.12f;
                DrawLine(ink, 0.72f, 9);
                firstValue = Read(ink.Output)[368 * 512 + 256].grayscale;
                DrawLine(ink, 0.72f, 10);
                float secondValue = Read(ink.Output)[368 * 512 + 256].grayscale;
                Log("K-M overlap luminance: " + firstValue.ToString("F6") + " -> " + secondValue.ToString("F6"));
                Check(secondValue < firstValue - 0.01f, "K-M repeated dilute deposition does not darken.");
                ink.brush.pigmentLoad = 1.4f;

                ink.ClearCanvas();
                ink.paper.saturationLimit = 0.05f; ink.paper.saturationFromDeposit = 100;
                DrawLine(ink, 0.5f, 12, false);
                Color[] saturated = Read(ink.DiffusionState);
                for (int i = 0; i < 12; i++) ink.Tick(1f / 60f);
                Color[] saturatedAfter = Read(ink.DiffusionState);
                int locked = 0;
                for (int i = 0; i < saturated.Length; i++)
                    if (saturated[i].g >= 0.0499f && saturated[i].r > 0.01f)
                    {
                        Check(Mathf.Abs(saturated[i].r - saturatedAfter[i].r) < 0.001f, "Saturated pixel leaked pigment."); locked++;
                    }
                Check(locked > 100, "Saturation test did not hit enough cells.");
                Log("GPU: saturated-cell no-flux passed (" + locked + " cells).");

                ink.ClearCanvas(); ink.paper = new InkPaperSettings();
                ink.brush.minimumRadius = 1; ink.brush.maximumRadius = 1;
                DrawLine(ink, 0.5f, 16, false);
                Check(ink.LastFrameStampCount > 256, "Overflow test did not exercise multiple stamp batches.");
                Color[] thinLine = Read(ink.PigmentCanvas);
                for (int x = 70; x < 445; x++) Check(thinLine[256 * 512 + x].r > 0.01f, "Chunked thin stroke has a gap.");
                double thinMobile = 0;
                foreach (Color pixel in Read(ink.DiffusionState)) thinMobile += pixel.r;
                Check(thinMobile > 0.1, "Sub-cell thin line was missed during CA downsampling.");
                Log("GPU: " + ink.LastFrameStampCount + " stamps in one frame; chunk continuity and sub-cell injection passed.");
                ink.brush.minimumRadius = 10; ink.brush.maximumRadius = 14;
                ValidateInputEvents(ink);

                // 另存视觉样张：满墨弧线、侧锋转弯、枯笔及叠墨。
                ink.ClearCanvas(); ink.paper = new InkPaperSettings();
                ink.brush.initialInk = 1; ink.RefillInk();
                for (int stroke = 0; stroke < 4; stroke++)
                {
                    ink.brush.initialInk = 1f - stroke * 0.23f; ink.RefillInk();
                    for (int i = 0; i <= 100; i++)
                    {
                        float t = i / 100f;
                        var uv = new Vector2(0.1f + 0.8f * t, 0.82f - stroke * 0.20f + Mathf.Sin(t * Mathf.PI * 2) * 0.045f);
                        ink.SubmitCanvasSample(uv, 20 + stroke * 2 + t, i == 0 ? InkPointerPhase.Begin :
                            i == 100 ? InkPointerPhase.End : InkPointerPhase.Move, 0.35f + 0.6f * Mathf.Sin(t * Mathf.PI));
                        ink.Tick(1f / 100f);
                    }
                }
                for (int i = 0; i < 180; i++) ink.Tick(1f / 60f);
                SavePNG(ink.Output, "Validation/ink-preview.png");
                Log("Saved Validation/ink-preview.png.");
            }
            finally
            {
                ink.ReleaseResources();
                UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(displayObject);
            }
        }

        internal static Color[] Read(RenderTexture rt)
        {
            var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                texture.Apply(false, false);
                return texture.GetPixels();
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(texture); }
        }

        static void ValidateInputEvents(RealtimeInkCanvas ink)
        {
            Mouse mouse = null;
            Touchscreen screen = null;
            InputSettings originalSettings = InputSystem.settings;
            // InputManager 会销毁被替换的临时默认设置；先保留其副本用于还原。
            if (originalSettings.hideFlags == HideFlags.HideAndDontSave)
                originalSettings = UnityEngine.Object.Instantiate(originalSettings);
            InputSettings testSettings = UnityEngine.Object.Instantiate(originalSettings);
            var nodes = new List<InkPathNode>();
            Action<InkPathNode> handler = nodes.Add;
            try
            {
                // Editor 批处理没有 PlayerLoop；只在此临时设置上允许玩家输入更新。
                // 运行时代码不使用此内部测试开关，也不修改用户 InputSettings 资产。
                testSettings.SetInternalFeatureFlag("RUN_PLAYER_UPDATES_IN_EDIT_MODE", true);
                testSettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                testSettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                InputSystem.settings = testSettings;
                ink.ClearCanvas(); ink.PathNodeGenerated += handler;
                ink.SetInputEnabled(true);
                mouse = InputSystem.AddDevice<Mouse>("InkValidationMouse");
                for (int i = 0; i <= 15; i++)
                {
                    var state = new MouseState { position = new Vector2(60 + i * 20, 260) };
                    if (i < 15) state = state.WithButton(MouseButton.Left);
                    InputSystem.QueueStateEvent(mouse, state);
                    InputSystem.Update(); ink.Tick(1f / 60f);
                }
                Check(nodes.Count > 25, "Real mouse InputAction events did not generate a path. Nodes=" + nodes.Count);
                Check(Vector2.Distance(nodes[nodes.Count - 1].canvasPosition, new Vector2(360, 260)) < 0.1f,
                    "Mouse screen-to-canvas mapping or release endpoint failed.");
                ink.ClearCanvas(); nodes.Clear();
                screen = InputSystem.AddDevice<Touchscreen>("InkValidationTouchscreen");
                QueueTouch(screen, 1, UnityEngine.InputSystem.TouchPhase.Began, new Vector2(70, 160), ink);
                QueueTouch(screen, 2, UnityEngine.InputSystem.TouchPhase.Began, new Vector2(400, 430), ink);
                for (int i = 1; i <= 14; i++)
                    QueueTouch(screen, 1, UnityEngine.InputSystem.TouchPhase.Moved, new Vector2(70 + i * 20, 160), ink);
                QueueTouch(screen, 1, UnityEngine.InputSystem.TouchPhase.Ended, new Vector2(360, 160), ink);
                int primaryCount = nodes.Count;
                QueueTouch(screen, 2, UnityEngine.InputSystem.TouchPhase.Moved, new Vector2(460, 430), ink);
                QueueTouch(screen, 2, UnityEngine.InputSystem.TouchPhase.Ended, new Vector2(460, 430), ink);
                Check(primaryCount > 25, "EnhancedTouch events did not generate a path.");
                Check(primaryCount == nodes.Count, "Secondary finger took over an ended primary stroke.");
                foreach (InkPathNode node in nodes)
                {
                    Check(Mathf.Abs(node.canvasPosition.y - 160) < 0.1f, "Secondary finger caused a connecting stroke.");
                    Check(node.pressure > 0.5f && node.pressure < 0.7f, "Native touch pressure was not used.");
                }
                Log("Input System: queued native mouse/touch events, coordinates, release, pressure and multi-finger locking passed.");
            }
            finally
            {
                ink.SetInputEnabled(false);
                ink.PathNodeGenerated -= handler;
                if (mouse != null) InputSystem.RemoveDevice(mouse);
                if (screen != null) InputSystem.RemoveDevice(screen);
                InputSystem.settings = originalSettings;
                if (testSettings) UnityEngine.Object.DestroyImmediate(testSettings);
            }
        }

        static void QueueTouch(Touchscreen screen, int id, UnityEngine.InputSystem.TouchPhase phase, Vector2 position, RealtimeInkCanvas ink)
        {
            InputSystem.QueueStateEvent(screen, new TouchState { touchId = id, phase = phase, position = position, pressure = 0.6f });
            InputSystem.Update(); ink.Tick(1f / 60f);
        }

        static void BenchmarkGPU(int resolution)
        {
            var go = new GameObject("Ink Throughput Benchmark");
            var displayObject = new GameObject("Benchmark Display", typeof(RectTransform), typeof(RawImage));
            var ink = go.AddComponent<RealtimeInkCanvas>();
            InkDemoBuilder.AssignReferences(ink, displayObject.GetComponent<RawImage>());
            ink.canvasResolution = new Vector2Int(resolution, resolution);
            ink.diffusionResolution = new Vector2Int(resolution / 4, resolution / 4);
            ink.brush.consumptionPer1000Pixels = 0;
            float scale = resolution / 2048f;
            ink.brush.minimumRadius *= scale; ink.brush.maximumRadius *= scale;
            ink.paper.inkDiffusion *= scale * scale; ink.paper.waterDiffusion *= scale * scale;
            var onePixel = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            try
            {
                Check(ink.Initialize(), "Benchmark initialization failed.");
                for (int i = 0; i < 30; i++) BenchmarkFrame(ink, i);
                FenceGPU(ink.Output, onePixel);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                const int frames = 180;
                for (int i = 30; i < 30 + frames; i++) BenchmarkFrame(ink, i);
                FenceGPU(ink.Output, onePixel);
                timer.Stop();
                Log("Synthetic pipeline throughput " + resolution + " / " + resolution / 4 + ": " +
                    (timer.Elapsed.TotalMilliseconds / frames).ToString("F3") +
                    " ms/frame (180 offscreen frames, includes CPU submission + GPU completion; excludes URP camera/UI/present).");
            }
            finally
            {
                ink.ReleaseResources(); UnityEngine.Object.DestroyImmediate(onePixel);
                UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(displayObject);
            }
        }

        static void BenchmarkFrame(RealtimeInkCanvas ink, int frame)
        {
            float angle = frame * 0.04f;
            var uv = new Vector2(0.5f + 0.35f * Mathf.Cos(angle), 0.5f + 0.3f * Mathf.Sin(angle * 1.7f));
            ink.SubmitCanvasSample(uv, frame / 60.0, frame == 0 ? InkPointerPhase.Begin : InkPointerPhase.Move, 0.65f);
            ink.Tick(1f / 60f);
        }

        static void FenceGPU(RenderTexture source, Texture2D onePixel)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;
            onePixel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
            RenderTexture.active = previous;
        }

        internal static void SavePNG(RenderTexture rt, string path)
        {
            Color[] colors = Read(rt);
            // 仅导出时做线性->sRGB 编码；不是运行时 CPU 绘制。
            for (int i = 0; i < colors.Length; i++) colors[i] = colors[i].gamma;
            var image = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, false);
            image.SetPixels(colors); image.Apply();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, image.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(image);
        }
    }
}
