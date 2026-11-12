using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace RealtimeInk.Editor
{
    public static class InkEditingValidation
    {
        static int checks;
        static void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Editing validation: " + message);
        }
        static void Same(Color[] a, Color[] b, string message)
        {
            Check(a.Length == b.Length, message + " dimensions");
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) throw new InvalidOperationException(message + " pixel " + i + ": " + a[i] + " != " + b[i]);
            checks++;
        }
        static void Restore(RealtimeInkCanvas ink)
        {
            int frames = 0;
            while (ink.IsRestoringHistory && frames++ < 10000) ink.Tick(0);
            Check(!ink.IsRestoringHistory, "Replay did not finish.");
        }
        static void Stroke(RealtimeInkCanvas ink, float y, double time, bool varySettings = false)
        {
            for (int i = 0; i <= 28; i++)
            {
                if (varySettings && i == 14)
                {
                    ink.brush.noiseFrequency = 0.27f;
                    ink.paper.inkDiffusion = 17; ink.paper.fiberAngle = 74;
                }
                ink.SubmitCanvasSample(new Vector2(0.12f + 0.72f * i / 28, y + 0.025f * Mathf.Sin(i * 0.2f)),
                    time + i / 90.0, i == 0 ? InkPointerPhase.Begin : InkPointerPhase.Move, 0.7f);
                ink.Tick(1f / 90f);
            }
            ink.SubmitCanvasSample(new Vector2(0.84f, y), time + 0.4, InkPointerPhase.End, 0.4f);
            ink.Tick(1f / 60f);
            for (int i = 0; i < 180; i++) ink.Tick(1f / 60f);
        }
        static Color32[] LoadPNG(string path, int expectedSize)
        {
            var png = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                Check(png.LoadImage(File.ReadAllBytes(path)), "PNG decode failed.");
                Check(png.width == expectedSize && png.height == expectedSize, "Export resolution changed.");
                return png.GetPixels32();
            }
            finally { UnityEngine.Object.DestroyImmediate(png); }
        }
        [UnityEditor.MenuItem("Tools/Realtime Ink/Validate Editing and PNG")]
        public static void Run()
        {
            checks = 0;
            string directory = Path.GetFullPath(Path.Combine("Validation", "Editing", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")));
            var go = new GameObject("Editing Validation");
            var view = new GameObject("Editing Display", typeof(RectTransform), typeof(RawImage));
            var ink = go.AddComponent<RealtimeInkCanvas>();
            InkDemoBuilder.AssignReferences(ink, view.GetComponent<RawImage>());
            ink.canvasResolution = new Vector2Int(512, 512); ink.diffusionResolution = new Vector2Int(128, 128);
            ink.ExportDirectoryOverride = directory;
            try
            {
                Check(ink.Initialize(), "Initialization failed.");
                ink.brush.refillPerSecond = ink.brush.consumptionPer1000Pixels = 0;
                Check(!ink.CanUndo && !ink.CanRedo && !ink.UndoStroke() && !ink.RedoStroke(), "Empty history can undo/redo.");
                ink.ApplyStyle(InkStyle.Regular); Stroke(ink, 0.72f, 0);
                Color[] first = InkValidation.Read(ink.PigmentCanvas), firstCA = InkValidation.Read(ink.DiffusionState);
                Check(ink.StrokeCount == 1, "Gesture was not recorded as one stroke.");
                ink.ApplyStyle(InkStyle.Cursive); Stroke(ink, 0.32f, 4, true);
                Color[] second = InkValidation.Read(ink.PigmentCanvas), secondCA = InkValidation.Read(ink.DiffusionState);
                Color[] secondWet = InkValidation.Read(ink.Wetness);
                int nodes = 0; ink.PathNodeGenerated += _ => nodes++;
                ink.ApplyStyle(InkStyle.Stele); ink.brush.noiseFrequency = 0.49f; ink.paper.dryingRate = 2;
                float tank = ink.InkRemaining;
                Check(ink.UndoStroke(), "Undo rejected."); Restore(ink);
                Same(first, InkValidation.Read(ink.PigmentCanvas), "Undo failed to restore fixed ink.");
                Same(firstCA, InkValidation.Read(ink.DiffusionState), "Undo failed to restore CA.");
                for (int i = 0; i < 30; i++) ink.Tick(1f / 60f);
                Check(ink.CanRedo && ink.StrokeCount == 1 && ink.RedoCount == 1, "Wrong history cursor.");
                Check(ink.RedoStroke(), "Redo rejected."); Restore(ink);
                Same(second, InkValidation.Read(ink.PigmentCanvas), "Redo used current style/noise settings.");
                Same(secondCA, InkValidation.Read(ink.DiffusionState), "Redo used current CA settings.");
                Same(secondWet, InkValidation.Read(ink.Wetness), "Redo lost drying state.");
                Check(nodes == 0 && ink.InkRemaining == tank && ink.LastLoadedStyle == InkStyle.Stele, "Replay changed user trajectory/brush controls.");
                Check(ink.UndoStroke(), "Undo B."); Restore(ink);
                Check(ink.UndoStroke(), "Undo A."); Restore(ink);
                Check(ink.StrokeCount == 0 && ink.RedoCount == 2, "Undo all cursor.");
                foreach (Color c in InkValidation.Read(ink.PigmentCanvas)) Check(c == Color.clear, "Undo all left pigment.");
                Check(ink.RedoStroke(), "Redo A."); Restore(ink);
                Check(ink.RedoStroke(), "Redo B."); Restore(ink);
                Same(second, InkValidation.Read(ink.PigmentCanvas), "Multi-step redo failed.");
                Check(ink.UndoStroke(), "Undo before branch."); Restore(ink);
                Stroke(ink, 0.5f, 8);
                Check(ink.StrokeCount == 2 && ink.RedoCount == 0 && !ink.CanRedo, "New stroke retained obsolete redo branch.");
                Check(ink.UndoStroke(), "Undo branch."); Restore(ink);
                Same(first, InkValidation.Read(ink.PigmentCanvas), "New branch damaged earlier stroke.");

                ink.appearance.debugView = InkDebugView.Wetness;
                Check(ink.SaveTransparentPng(), "Export failed.");
                string exported = ink.LastSavedPath;
                Color32[] png = LoadPNG(exported, 512);
                int clear = 0, partial = 0, inkPixels = 0;
                long topAlpha = 0, bottomAlpha = 0;
                for (int i = 0; i < png.Length; i++)
                {
                    Color32 c = png[i];
                    if (c.a == 0) clear++;
                    if (c.a > 0 && c.a < 255) partial++;
                    if (c.a > 64) inkPixels++;
                    if (i / 512 > 256) topAlpha += c.a; else bottomAlpha += c.a;
                }
                Check(clear > png.Length / 2 && partial > 100 && inkPixels > 100, "PNG alpha missing/opaque/empty.");
                Check(topAlpha > 1000 && bottomAlpha == 0, "Export flipped or included UI/paper pixels.");
                ink.appearance.paperColor = Color.magenta; ink.appearance.paperTextureStrength = 0.3f;
                ink.appearance.debugView = InkDebugView.Composite;
                Check(ink.SaveTransparentPng(), "Second export failed.");
                Color32[] recolored = LoadPNG(ink.LastSavedPath, 512);
                for (int i = 0; i < png.Length; i++) Check(png[i].Equals(recolored[i]), "Paper/debug leaked into export.");
                Same(first, InkValidation.Read(ink.PigmentCanvas), "Export modified source ink.");
                Check(File.Exists(exported) && exported != ink.LastSavedPath, "Save overwrote earlier export.");

                ink.ClearCanvas();
                Check(ink.StrokeCount == 0 && ink.RedoCount == 0 && !ink.CanUndo, "Rewrite left history.");
                Check(ink.SaveTransparentPng(), "Empty export failed.");
                foreach (Color32 c in LoadPNG(ink.LastSavedPath, 512)) Check(c.a == 0, "Empty page exports opaque paper.");
                // 同帧两次点按必须分别撤销；未抬笔就撤销也要完整结束当前笔。
                for (int i = 0; i < 2; i++)
                {
                    Vector2 uv = new Vector2(0.2f + i * 0.5f, 0.5f);
                    ink.SubmitCanvasSample(uv, 12 + i, InkPointerPhase.Begin, 0.7f);
                    ink.SubmitCanvasSample(uv, 12.01 + i, InkPointerPhase.End, 0.7f);
                }
                ink.Tick(0); Check(ink.StrokeCount == 2, "Same-frame taps merged.");
                Check(ink.UndoStroke(), "Undo tap."); Restore(ink); Check(ink.StrokeCount == 1, "Tap undo cursor.");
                ink.SubmitCanvasSample(new Vector2(0.4f, 0.6f), 15, InkPointerPhase.Begin, 0.7f); ink.Tick(0);
                Check(ink.UndoStroke(), "Undo active gesture."); Restore(ink);
                Check(ink.StrokeCount == 1, "Active gesture undo failed.");
                string report = "PASS: " + checks + " assertions (includes pixel checks).\n" +
                    "Per-gesture undo/redo; fixed/CA/wet GPU state restored with changed style and simulation settings; multi-step/branch/clear; same-frame taps and active stroke.\n" +
                    "RGBA PNG: dimensions, transparent blank pixels, fractional edge alpha, ink presence, orientation, paper/debug independence, unique filenames, unchanged source.\nExports: " + directory;
                Directory.CreateDirectory("Validation"); File.WriteAllText("Validation/editing-results.txt", report);
                Debug.Log(report);
            }
            finally
            {
                ink.ReleaseResources(); UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(view);
            }
        }
    }
}
