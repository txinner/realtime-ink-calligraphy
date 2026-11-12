using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace RealtimeInk
{
    /// <summary>原生 UGUI 演示面板；编辑的是同一组通用字段，不维持另一份运行参数。</summary>
    public sealed class InkDemoPanel : MonoBehaviour
    {
        public RealtimeInkCanvas ink;
        public bool IsReady => panel != null;
        public Button[] StyleButtons { get; private set; }
        public Button UndoButton { get; private set; }
        public Button RedoButton { get; private set; }
        public Button RewriteButton { get; private set; }
        public Button SaveButton { get; private set; }
        readonly List<Action> refreshers = new List<Action>();
        Font font;
        RectTransform panel, content, paperMargin;
        Text currentStyle, inkLevel, historyLevel, editStatus;
        string lastEditStatus;
        int lastStrokeCount = -1, lastRedoCount = -1;
        int lastInkPercent = -1;
        bool shown = true;
        static readonly Color Accent = new Color(0.55f, 0.74f, 0.65f);
        static readonly Color Foreground = new Color(0.93f, 0.91f, 0.85f);

        void Start()
        {
            if (!ink || !ink.display || !ink.display.canvas) return;
            font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "Noto Sans CJK SC", "Arial" }, 18);
            Canvas canvas = ink.display.canvas;
            if (!canvas.GetComponent<GraphicRaycaster>()) canvas.gameObject.AddComponent<GraphicRaycaster>();
            if (!EventSystem.current)
            {
                var events = new GameObject("Ink EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                events.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
            }
            paperMargin = ink.display.rectTransform.parent as RectTransform;
            panel = Rect("Ink Parameters", canvas.transform);
            panel.anchorMin = new Vector2(1, 0); panel.anchorMax = Vector2.one;
            panel.offsetMin = new Vector2(-366, 16); panel.offsetMax = new Vector2(-16, -16);
            panel.gameObject.AddComponent<Image>().color = new Color(0.075f, 0.095f, 0.09f, 0.98f);
            var viewport = Rect("Viewport", panel); Stretch(viewport, 14);
            viewport.gameObject.AddComponent<RectMask2D>();
            content = Rect("Content", viewport);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1); content.sizeDelta = Vector2.zero;
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6; layout.childControlHeight = layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            var fit = content.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = panel.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 28;

            Label(content, "实时毛笔", 27, 38);
            Label(content, "通用参数 · 风格快照 · 独立笔法", 13, 22, Accent);
            StyleButtons = new Button[6];
            for (int row = 0; row < 2; row++)
            {
                RectTransform line = Row(content, 36);
                for (int col = 0; col < 3; col++)
                {
                    InkStyle style = (InkStyle)(row * 3 + col);
                    StyleButtons[(int)style] = Button(line, InkStyleLibrary.Name(style), () => ink.ApplyStyle(style));
                }
            }
            currentStyle = Label(content, "", 13, 24, Accent);
            Label(content, "笔画操作", 17, 26);
            RectTransform historyActions = Row(content, 36);
            UndoButton = Button(historyActions, "撤销（上一笔）", () => ink.UndoStroke());
            RedoButton = Button(historyActions, "重复（下一笔）", () => ink.RedoStroke());
            RectTransform documentActions = Row(content, 36);
            RewriteButton = Button(documentActions, "重写（清空）", () => ink.ClearCanvas());
            SaveButton = Button(documentActions, "保存（透明 PNG）", () => ink.SaveTransparentPng());
            historyLevel = Label(content, "", 12, 20, Accent);
            editStatus = Label(content, "", 12, 48, Accent);
            editStatus.horizontalOverflow = HorizontalWrapMode.Wrap;
            RectTransform actions = Row(content, 34);
            Button(actions, "蘸墨 R", () => ink.RefillInk());
            Button(actions, "通道 D", () =>
                ink.appearance.debugView = (InkDebugView)(((int)ink.appearance.debugView + 1) % 5));
            inkLevel = Label(content, "", 14, 24);
            Label(content, "独立笔法（可叠加）", 17, 28);
            Toggle("蚕头燕尾辅助  H", () => ink.techniques.silkwormSwallowtail, value => ink.techniques.silkwormSwallowtail = value);
            Toggle("均匀线条模式  U", () => ink.techniques.uniformLine, value => ink.techniques.uniformLine = value);
            Toggle("碑刻硬边模式  B", () => ink.techniques.stoneEdges, value => ink.techniques.stoneEdges = value);
            Toggle("游丝保护", () => ink.behavior.protectHairlines, value => ink.behavior.protectHairlines = value);
            Label(content, "当前通用参数（即时生效）", 17, 28);
            Slider("最大笔宽", 4, 100, () => ink.brush.maximumRadius * 2, value => ink.brush.maximumRadius = value * 0.5f, "F0", " px");
            Slider("耗墨 / 千像素", 0, 0.8f, () => ink.brush.consumptionPer1000Pixels, value => ink.brush.consumptionPer1000Pixels = value);
            Slider("飞白噪声", 0, 1, () => ink.brush.noiseStrength, value => ink.brush.noiseStrength = value);
            Slider("洇墨系数", 0, 100, () => ink.paper.inkDiffusion, value => ink.paper.inkDiffusion = value, "F0");
            Slider("压力敏感度", 0, 1, () => ink.behavior.pressureSensitivity, value => ink.behavior.pressureSensitivity = value);
            Slider("停顿判定", 0.04f, 0.5f, () => ink.behavior.pauseDuration, value => ink.behavior.pauseDuration = value, "F2", " s");
            Slider("连笔超时", 0, 0.8f, () => ink.behavior.connectionTimeout, value => ink.behavior.connectionTimeout = value, "F2", " s");
            Slider("游丝最小宽度", 0.1f, 4, () => ink.behavior.hairlineMinimumWidth, value => ink.behavior.hairlineMinimumWidth = value, "F2", " px");
            RectTransform advancedRow = Row(content, 32);
            RectTransform mainContent = content;
            var advanced = Rect("Advanced Parameters", mainContent);
            var advancedLayout = advanced.gameObject.AddComponent<VerticalLayoutGroup>();
            advancedLayout.spacing = 6; advancedLayout.childForceExpandHeight = false;
            advancedLayout.childControlHeight = advancedLayout.childControlWidth = true;
            advancedLayout.childForceExpandWidth = true;
            Button(advancedRow, "高级通用参数 ▾", () => advanced.gameObject.SetActive(!advanced.gameObject.activeSelf));
            content = advanced;
            Slider("回墨速度", 0, 0.3f, () => ink.brush.refillPerSecond, value => ink.brush.refillPerSecond = value);
            Slider("回墨等待", 0, 2, () => ink.brush.refillDelay, value => ink.brush.refillDelay = value, "F2", " s");
            Slider("侧锋触发角速度", 100, 1800, () => ink.brush.turnRateForSideBrush, value => ink.brush.turnRateForSideBrush = value, "F0", " °/s");
            Slider("侧锋阻尼", 0.005f, 0.2f, () => ink.brush.turnSmoothing, value => ink.brush.turnSmoothing = value, "F3", " s");
            Slider("水滴纵向倍率", 0.6f, 3, () => ink.brush.dropletLength, value => ink.brush.dropletLength = value);
            Slider("水滴横向倍率", 0.5f, 2, () => ink.brush.dropletWidth, value => ink.brush.dropletWidth = value);
            Slider("轮廓圆润度", 0, 1, () => ink.brush.contourRoundness, value => ink.brush.contourRoundness = value);
            Slider("水滴尖端指数", 0.5f, 3, () => ink.brush.dropletTipPower, value => ink.brush.dropletTipPower = value);
            Slider("崩口强度", 0, 1, () => ink.brush.chipStrength, value => ink.brush.chipStrength = value);
            var noiseRow = Row(content, 32);
            var noiseButton = Button(noiseRow, "", () => { ink.brush.edgeNoise = ink.brush.edgeNoise == InkEdgeNoise.PaperFiber ?
                InkEdgeNoise.ChippedStone : InkEdgeNoise.PaperFiber; Refresh(); });
            refreshers.Add(() => noiseButton.GetComponentInChildren<Text>().text = "基础噪声：" +
                (ink.brush.edgeNoise == InkEdgeNoise.PaperFiber ? "宣纸纤维" : "硬质崩口"));
            Slider("压力宽度基准", 0.05f, 1, () => ink.behavior.uniformPressure, value => ink.behavior.uniformPressure = value);
            Slider("停顿速度阈值", 0, 60, () => ink.behavior.pauseSpeedThreshold, value => ink.behavior.pauseSpeedThreshold = value, "F1", " px/s");
            Slider("停顿距离阈值", 0.2f, 6, () => ink.behavior.pauseDistanceThreshold, value => ink.behavior.pauseDistanceThreshold = value, "F2", " px");
            Slider("连笔距离", 0, 180, () => ink.behavior.connectionDistance, value => ink.behavior.connectionDistance = value, "F0", " px");
            Slider("端点铺毫增益", 0, 2, () => ink.behavior.endpointWidthGain, value => ink.behavior.endpointWidthGain = value);
            Slider("铺毫落墨剂量", 0.01f, 1, () => ink.behavior.endpointDeposit, value => ink.behavior.endpointDeposit = value);
            Slider("宽笔耗墨指数", 0.1f, 3, () => ink.brush.consumptionWidthExponent, value => ink.brush.consumptionWidthExponent = value);
            Slider("剩墨浓度指数", 0.1f, 3, () => ink.brush.pigmentInkExponent, value => ink.brush.pigmentInkExponent = value);
            Slider("耗墨曲线低墨倍率", 0, 2, () => CurveEndpoint(false), value => SetCurveEndpoint(false, value));
            Slider("耗墨曲线满墨倍率", 0, 2, () => CurveEndpoint(true), value => SetCurveEndpoint(true, value));
            content = mainContent; advanced.gameObject.SetActive(false);
            Label(content, "1–6 加载快照 · Tab 隐藏面板\n短抬笔只延续笔势，不补画空白。", 12, 44, new Color(0.66f, 0.71f, 0.67f));

            var handle = Rect("Panel Toggle", canvas.transform);
            handle.anchorMin = handle.anchorMax = Vector2.one; handle.pivot = Vector2.one;
            handle.anchoredPosition = new Vector2(-22, -20); handle.sizeDelta = new Vector2(70, 28);
            var hide = Button(handle, "参数 ◂", TogglePanel);
            Stretch(hide.GetComponent<RectTransform>(), 0);
            ink.SettingsChanged += Refresh;
            ApplyPaperMargin(); Refresh();
        }

        void Update()
        {
            if (!panel) return;
            UndoButton.interactable = ink.CanUndo;
            RedoButton.interactable = ink.CanRedo;
            RewriteButton.interactable = ink.IsInitialized;
            SaveButton.interactable = ink.IsInitialized && !ink.IsRestoringHistory;
            if (lastStrokeCount != ink.StrokeCount || lastRedoCount != ink.RedoCount)
            {
                lastStrokeCount = ink.StrokeCount; lastRedoCount = ink.RedoCount;
                historyLevel.text = "当前 " + lastStrokeCount + " 笔 · 可重复 " + lastRedoCount + " 笔";
            }
            if (lastEditStatus != ink.EditStatus)
            {
                lastEditStatus = ink.EditStatus;
                editStatus.text = lastEditStatus;
                editStatus.gameObject.SetActive(!string.IsNullOrEmpty(lastEditStatus));
            }
            if (Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame) TogglePanel();
            int percent = Mathf.RoundToInt(ink.InkRemaining * 100);
            if (percent != lastInkPercent)
            {
                lastInkPercent = percent;
                inkLevel.text = "笔头墨量  " + percent + "%";
            }
        }
        public void TogglePanel() { shown = !shown; panel.gameObject.SetActive(shown); ApplyPaperMargin(); }
        void ApplyPaperMargin()
        {
            if (paperMargin) paperMargin.offsetMax = new Vector2(shown ? -390 : -24, -24);
        }
        public void Refresh()
        {
            if (!panel) return;
            currentStyle.text = "最近加载：" + InkStyleLibrary.Name(ink.LastLoadedStyle) + "   ·   参数可自由调整";
            foreach (Action refresh in refreshers) refresh();
            for (int i = 0; i < StyleButtons.Length; i++)
                StyleButtons[i].GetComponent<Image>().color = i == (int)ink.LastLoadedStyle ?
                    new Color(0.24f, 0.39f, 0.31f) : new Color(0.14f, 0.19f, 0.17f);
        }
        float CurveEndpoint(bool last)
        {
            var curve = ink.brush.consumptionCurve;
            return curve != null && curve.length > 0 ? curve[last ? curve.length - 1 : 0].value : 1f;
        }
        void SetCurveEndpoint(bool last, float value)
        {
            var curve = ink.brush.consumptionCurve;
            if (curve == null || curve.length < 2) curve = ink.brush.consumptionCurve = AnimationCurve.Linear(0, 0.35f, 1, 1);
            int index = last ? curve.length - 1 : 0;
            Keyframe key = curve[index]; key.value = value; curve.MoveKey(index, key);
        }
        void OnDestroy()
        {
            if (ink) ink.SettingsChanged -= Refresh;
            if (font) Destroy(font);
        }

        static RectTransform Rect(string name, Transform parent)
        {
            var result = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            result.SetParent(parent, false); return result;
        }
        static void Stretch(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.one * inset; rect.offsetMax = -Vector2.one * inset;
        }
        static void Height(RectTransform rect, float height)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = height; element.flexibleWidth = 1;
        }
        Text Label(Transform parent, string text, int size, float height, Color? color = null)
        {
            var rect = Rect("Label", parent); Height(rect, height);
            var label = rect.gameObject.AddComponent<Text>();
            label.font = font; label.fontSize = size; label.text = text;
            label.color = color ?? Foreground; label.alignment = TextAnchor.MiddleLeft;
            label.raycastTarget = false;
            return label;
        }
        static RectTransform Row(Transform parent, float height)
        {
            var row = Rect("Row", parent); Height(row, height);
            var group = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.spacing = 6; group.childControlHeight = group.childControlWidth = true;
            group.childForceExpandWidth = true;
            return row;
        }
        Button Button(Transform parent, string text, Action action)
        {
            var rect = Rect(text, parent);
            var element = rect.gameObject.AddComponent<LayoutElement>(); element.flexibleWidth = 1;
            var image = rect.gameObject.AddComponent<Image>(); image.color = new Color(0.14f, 0.19f, 0.17f);
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
            var label = Label(rect, text, 16, 28); Stretch(label.rectTransform, 0); label.alignment = TextAnchor.MiddleCenter;
            button.onClick.AddListener(() => action());
            return button;
        }
        void Toggle(string title, Func<bool> read, Action<bool> write)
        {
            var row = Rect(title, content); Height(row, 28);
            var toggle = row.gameObject.AddComponent<Toggle>();
            var box = Rect("Box", row); box.anchorMin = box.anchorMax = new Vector2(0, 0.5f);
            box.pivot = new Vector2(0, 0.5f); box.sizeDelta = new Vector2(20, 20);
            var background = box.gameObject.AddComponent<Image>(); background.color = new Color(0.25f, 0.29f, 0.26f);
            var check = Rect("Check", box); Stretch(check, 4);
            var mark = check.gameObject.AddComponent<Image>(); mark.color = Accent;
            var label = Label(row, title, 14, 28); Stretch(label.rectTransform, 0);
            label.rectTransform.offsetMin = new Vector2(30, 0);
            toggle.targetGraphic = background; toggle.graphic = mark;
            toggle.onValueChanged.AddListener(value => write(value));
            refreshers.Add(() => toggle.SetIsOnWithoutNotify(read()));
        }
        void Slider(string title, float min, float max, Func<float> read, Action<float> write, string format = "F2", string suffix = "")
        {
            var row = Rect(title, content); Height(row, 45);
            var label = Label(row, "", 14, 22);
            label.rectTransform.anchorMin = new Vector2(0, 1); label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.pivot = new Vector2(0.5f, 1); label.rectTransform.sizeDelta = new Vector2(0, 23);
            var track = Rect("Slider", row); track.anchorMin = Vector2.zero; track.anchorMax = new Vector2(1, 0);
            track.pivot = new Vector2(0.5f, 0); track.sizeDelta = new Vector2(-12, 20);
            var slider = track.gameObject.AddComponent<Slider>(); slider.minValue = min; slider.maxValue = max;
            var bar = Rect("Background", track); Stretch(bar, 0);
            bar.offsetMin = new Vector2(0, 7); bar.offsetMax = new Vector2(0, -7);
            bar.gameObject.AddComponent<Image>().color = new Color(0.23f, 0.29f, 0.26f);
            var fillArea = Rect("Fill Area", track); Stretch(fillArea, 0);
            var fill = Rect("Fill", fillArea); Stretch(fill, 0);
            fill.offsetMin = new Vector2(0, 7); fill.offsetMax = new Vector2(0, -7);
            fill.gameObject.AddComponent<Image>().color = Accent;
            var handleArea = Rect("Handle Area", track); Stretch(handleArea, 0);
            var handle = Rect("Handle", handleArea); handle.sizeDelta = new Vector2(12, 0);
            var handleImage = handle.gameObject.AddComponent<Image>(); handleImage.color = Foreground;
            slider.fillRect = fill; slider.handleRect = handle; slider.targetGraphic = handleImage;
            slider.onValueChanged.AddListener(value => { write(value); label.text = title + "    " + value.ToString(format) + suffix; });
            refreshers.Add(() => { slider.SetValueWithoutNotify(read()); label.text = title + "    " + read().ToString(format) + suffix; });
        }
    }
}
