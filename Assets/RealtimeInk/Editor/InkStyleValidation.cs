using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace RealtimeInk.Editor
{
    public static class InkStyleValidation
    {
        static int assertions;
        static void Check(bool value, string message)
        {
            assertions++;
            if (!value) throw new InvalidOperationException("Style validation: " + message);
        }
        public static void Run()
        {
            assertions = 0;
            var go = new GameObject("Style Validation");
            var display = new GameObject("Style Display", typeof(RectTransform), typeof(RawImage));
            var ink = go.AddComponent<RealtimeInkCanvas>();
            InkDemoBuilder.AssignReferences(ink, display.GetComponent<RawImage>());
            ink.canvasResolution = new Vector2Int(512, 512); ink.diffusionResolution = new Vector2Int(128, 128);
            var nodes = new List<InkPathNode>(); ink.PathNodeGenerated += nodes.Add;
            try
            {
                Check(ink.Initialize(), "Initialization failed.");
                var brushReference = ink.brush;
                var curveReference = ink.brush.consumptionCurve;
                ink.brush.refillPerSecond = 0.033f; ink.brush.maximumRadius = 17;
                ink.input.nativePressureMaximum = 2.2f; ink.paper.saturationLimit = 0.77f;
                for (int mask = 0; mask < 8; mask++)
                    for (int i = 0; i < 6; i++)
                    {
                        ink.techniques.silkwormSwallowtail = (mask & 1) != 0;
                        ink.techniques.uniformLine = (mask & 2) != 0;
                        ink.techniques.stoneEdges = (mask & 4) != 0;
                        float tank = ink.InkRemaining;
                        ink.ApplyStyle((InkStyle)i);
                        Check(ReferenceEquals(brushReference, ink.brush), "Preset replaced authoritative shared settings.");
                        Check(ReferenceEquals(curveReference, ink.brush.consumptionCurve), "Preset replaced shared consumption curve.");
                        Check(ink.brush.refillPerSecond == 0.033f && ink.brush.maximumRadius == 17 &&
                            ink.input.nativePressureMaximum == 2.2f && ink.paper.saturationLimit == 0.77f, "Preset overwrote unrelated tuning.");
                        Check(ink.techniques.silkwormSwallowtail == ((mask & 1) != 0) &&
                            ink.techniques.uniformLine == ((mask & 2) != 0) && ink.techniques.stoneEdges == ((mask & 4) != 0),
                            "Preset overwrote independent helpers.");
                        Check(ink.InkRemaining == tank, "Preset silently refilled ink.");
                    }
                ink.ApplyStyle(InkStyle.Regular); float regularConsumption = ink.brush.consumptionPer1000Pixels;
                ink.ApplyStyle(InkStyle.Running); Check(ink.brush.consumptionPer1000Pixels > regularConsumption && ink.behavior.protectHairlines, "Running snapshot mismatch.");
                float runningConsumption = ink.brush.consumptionPer1000Pixels;
                ink.ApplyStyle(InkStyle.Cursive); Check(ink.brush.consumptionPer1000Pixels > runningConsumption && ink.behavior.hairlineMinimumWidth < 0.3f, "Cursive snapshot mismatch.");
                ink.ApplyStyle(InkStyle.Seal); Check(ink.brush.noiseStrength == 0 && ink.behavior.pressureSensitivity < 0.2f, "Seal snapshot mismatch.");
                ink.ApplyStyle(InkStyle.Clerical); Check(ink.brush.dropletWidth > ink.brush.dropletLength && ink.behavior.pauseDuration < 0.1f, "Clerical snapshot mismatch.");
                ink.ApplyStyle(InkStyle.Stele); Check(ink.brush.edgeNoise == InkEdgeNoise.ChippedStone && ink.paper.inkDiffusion < 5, "Stele snapshot mismatch.");
                ink.ApplyStyle(InkStyle.Regular); Check(ink.brush.edgeNoise == InkEdgeNoise.PaperFiber && ink.brush.dropletWidth == 1, "Style fields leaked between snapshots.");

                ink.techniques.silkwormSwallowtail = ink.techniques.uniformLine = ink.techniques.stoneEdges = false;
                ink.input.nativePressureMaximum = 1; ink.brush.refillPerSecond = 0;
                ink.SubmitCanvasSample(new Vector2(0.1f, 0.5f), 0, InkPointerPhase.Begin, 0.5f);
                ink.SubmitCanvasSample(new Vector2(0.45f, 0.5f), 0.3, InkPointerPhase.Move, 0.7f); ink.Tick(1f / 60f);
                Color[] before = InkValidation.Read(ink.PigmentCanvas);
                ink.ApplyStyle(InkStyle.Cursive); ink.brush.noiseStrength = 0.31f;
                ink.Tick(0); Color[] after = InkValidation.Read(ink.PigmentCanvas);
                for (int i = 0; i < before.Length; i++) Check(before[i] == after[i], "Preset modified existing main ink.");
                Check(ink.brush.noiseStrength == 0.31f, "Post-preset edit was locked/reapplied.");
                ink.SubmitCanvasSample(new Vector2(0.8f, 0.5f), 0.7, InkPointerPhase.End, 0.7f); ink.Tick(1f / 60f);
                Check(Vector2.Distance(nodes[nodes.Count - 1].canvasPosition, new Vector2(409.6f, 256)) < 0.01f, "Preset during stroke lost endpoint.");
                foreach (var node in nodes) Check(Mathf.Abs(node.canvasPosition.y - 256) < 0.01f, "Preset bent user path.");

                ink.ClearCanvas(); nodes.Clear(); ink.ApplyStyle(InkStyle.Clerical);
                ink.techniques.silkwormSwallowtail = true;
                ink.SubmitCanvasSample(new Vector2(0.2f, 0.4f), 2, InkPointerPhase.Begin, 0.6f); ink.Tick(0.016f);
                for (int i = 0; i < 120; i++) ink.Tick(1f / 60f);
                int headDabs = nodes.FindAll(n => n.isDwell).Count;
                Check(headDabs > 0 && headDabs <= 4, "Start pause spreading missing or unbounded.");
                for (int i = 0; i < 120; i++) ink.Tick(1f / 60f);
                Check(nodes.FindAll(n => n.isDwell).Count == headDabs, "Long hold repeatedly deposits unlimited ink.");
                ink.SubmitCanvasSample(new Vector2(0.6f, 0.4f), 7, InkPointerPhase.Move, 0.6f); ink.Tick(0.016f);
                for (int i = 0; i < 20; i++) ink.Tick(1f / 60f);
                Check(nodes.FindAll(n => n.isDwell).Count == headDabs, "Interior pause incorrectly treated as a tail before release.");
                ink.SubmitCanvasSample(new Vector2(0.6f, 0.4f), 7.4, InkPointerPhase.End, 0.6f); ink.Tick(0.016f);
                Check(nodes.FindAll(n => n.isDwell).Count == headDabs + 1, "End pause did not broaden actual endpoint.");

                ink.ClearCanvas(); nodes.Clear(); ink.techniques.silkwormSwallowtail = false; ink.ApplyStyle(InkStyle.Running);
                Tap(ink, new Vector2(0.3f, 0.4f), 10);
                ink.SubmitCanvasSample(new Vector2(0.35f, 0.4f), 10.15, InkPointerPhase.Begin, 0.6f); ink.Tick(0.016f);
                Check(ink.LastStrokeContinued, "Short nearby lift did not preserve brush continuity.");
                foreach (var node in nodes) Check(node.canvasPosition.x <= 153.61f || node.canvasPosition.x >= 179.19f, "Pen-up gap was automatically drawn.");
                ink.SubmitCanvasSample(new Vector2(0.35f, 0.4f), 10.2, InkPointerPhase.End, 0.6f); ink.Tick(0.016f);
                ink.SubmitCanvasSample(new Vector2(0.36f, 0.4f), 12, InkPointerPhase.Begin, 0.6f); ink.Tick(0.016f);
                Check(!ink.LastStrokeContinued, "Connection timeout not enforced.");

                ValidateUniformAndHairline();
                ink.ClearCanvas(); ink.ApplyStyle(InkStyle.Regular); ink.techniques.stoneEdges = true;
                Stroke(ink, 0.5f, 20);
                foreach (Color pixel in InkValidation.Read(ink.DiffusionState)) Check(pixel.r == 0f, "Hard-edge stroke injected diffusing pigment.");
                foreach (Color pixel in InkValidation.Read(ink.Wetness)) Check(pixel.r == 0f, "Hard-edge stroke injected water.");
                Check(ink.LastFrameCASteps == 0, "Dry hard-edge strokes unnecessarily wake CA.");
                Color[] chipped = InkValidation.Read(ink.PigmentCanvas);
                ink.ClearCanvas(); ink.techniques.stoneEdges = false; Stroke(ink, 0.5f, 23);
                Color[] soft = InkValidation.Read(ink.PigmentCanvas); int different = 0;
                for (int i = 0; i < soft.Length; i++) if (Mathf.Abs(soft[i].r - chipped[i].r) > 0.03f) different++;
                Check(different > 200, "Hard edge did not change actual GPU footprint.");
                float mobile = 0;
                foreach (Color pixel in InkValidation.Read(ink.DiffusionState)) mobile += pixel.r;
                Check(mobile > 0, "Disabling hard-edge helper failed to restore wet ink.");
                ink.ClearCanvas();
                for (int i = 0; i < 6; i++)
                {
                    ink.ApplyStyle((InkStyle)i); ink.RefillInk();
                    ink.techniques.silkwormSwallowtail = i == 4;
                    Stroke(ink, 0.9f - i * 0.15f, 30 + i * 2);
                }
                for (int i = 0; i < 120; i++) ink.Tick(1f / 60f);
                InkValidation.SavePNG(ink.Output, "Validation/styles-preview.png");
                string report = "PASS: " + assertions + " assertions.\nSix sparse presets; no trajectory or helper lock; shared curve retention; " +
                    "bounded start/end dwell; pen-up continuity without bridging; uniform width; hairline floor; GPU chipped edges; no-CA hard mode.\n";
                Directory.CreateDirectory("Validation"); File.WriteAllText("Validation/style-results.txt", report); Debug.Log(report);
            }
            finally { ink.ReleaseResources(); UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(display); }
        }
        static void Tap(RealtimeInkCanvas ink, Vector2 uv, double time)
        {
            ink.SubmitCanvasSample(uv, time, InkPointerPhase.Begin, 0.6f);
            ink.SubmitCanvasSample(uv, time + 0.01, InkPointerPhase.End, 0.6f); ink.Tick(0.016f);
        }
        static void Stroke(RealtimeInkCanvas ink, float y, double time)
        {
            for (int i = 0; i <= 60; i++)
            {
                float t = i / 60f;
                var uv = new Vector2(0.12f + t * 0.75f, y + Mathf.Sin(t * Mathf.PI * 2) * 0.025f);
                ink.SubmitCanvasSample(uv, time + t, i == 0 ? InkPointerPhase.Begin : i == 60 ? InkPointerPhase.End : InkPointerPhase.Move,
                    0.25f + 0.6f * Mathf.Sin(t * Mathf.PI)); ink.Tick(1f / 60f);
            }
        }
        static void ValidateUniformAndHairline()
        {
            var input = new InkInputSettings { pressureSmoothing = 0.001f };
            var brush = new InkBrushSettings(); var behavior = new InkBehaviorSettings();
            var techniques = new InkTechniqueSettings { uniformLine = true };
            var nodes = new List<InkPathNode>();
            var path = new InkPathSampler(input, brush, nodes.Add, behavior, techniques);
            path.Begin(Vector2.zero, 0, 0.1f, true);
            for (int i = 1; i <= 100; i++) path.Move(new Vector2(i * 5, 0), i * 0.01, i / 100f, true);
            path.End(new Vector2(500, 0), 1.1, 1, true);
            float min = float.MaxValue, max = 0;
            foreach (var n in nodes) if (n.arcLength > 30 && n.arcLength < 470) { min = Mathf.Min(min, n.radius); max = Mathf.Max(max, n.radius); }
            Check(max - min < 0.001f, "Uniform mode still changes width with native pressure.");
            techniques.uniformLine = false; behavior.protectHairlines = true; behavior.hairlineMinimumWidth = 1.2f;
            brush.minimumRadius = 0.05f; brush.maximumRadius = 1f; nodes.Clear();
            path.Begin(Vector2.zero, 2, 0.01f, true); path.End(new Vector2(80, 0), 2.05, 0.01f, true);
            foreach (var n in nodes) Check(n.radius >= 0.6f, "Hairline protection fell below configured width.");
        }
    }
}
