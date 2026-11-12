using System;
using UnityEngine;

namespace RealtimeInk
{
    // 全部距离、速度以主画布 texel 为单位，时间以秒为单位。
    [Serializable]
    public sealed class InkInputSettings
    {
        [Min(0.1f)] public float jitterDistance = 0.65f;
        [Min(0.5f)] public float controlPointDistance = 6f;
        [Min(0.001f)] public float controlPointInterval = 1f / 120f;
        [Min(0.001f)] public float speedSmoothing = 0.035f;
        [Min(0.001f)] public float pressureSmoothing = 0.025f;
        [Min(1f)] public float speedForThinStroke = 1500f;
        [Range(0.01f, 1f)] public float minimumPressure = 0.12f;
        [Range(0.1f, 3f)] public float virtualPressureExponent = 1.25f;
        public TouchPressureMode touchPressure = TouchPressureMode.Auto;
        [Min(0.01f)] public float nativePressureMaximum = 1f;
        [Range(0.01f, 0.5f)] public float arcError = 0.08f;
        [Range(0.25f, 4f)] public float arcChordLength = 1f;
    }

    public enum TouchPressureMode { Auto, VirtualOnly, NativeTouch }
    public enum InkPointerPhase { Begin, Move, End, Cancel }
    public enum InkDebugView { Composite, FixedPigment, DiffusedPigment, Saturation, Wetness }
    public enum InkEdgeNoise { PaperFiber, ChippedStone }
    public enum InkStyle { Regular, Running, Cursive, Seal, Clerical, Stele }

    [Serializable]
    public sealed class InkBehaviorSettings
    {
        [Range(0f, 1f)] public float pressureSensitivity = 1f;
        [Range(0.05f, 1f)] public float uniformPressure = 0.65f;
        public bool protectHairlines;
        [Min(0.1f)] public float hairlineMinimumWidth = 0.6f;
        [Min(0f)] public float pauseSpeedThreshold = 18f;
        [Min(0.1f)] public float pauseDistanceThreshold = 1.25f;
        [Min(0.02f)] public float pauseDuration = 0.16f;
        [Min(0.05f)] public float pauseSpreadDuration = 0.4f;
        [Range(0f, 2f)] public float endpointWidthGain = 0.75f;
        [Range(0.01f, 1f)] public float endpointDeposit = 0.45f;
        [Min(0f)] public float connectionTimeout = 0.16f;
        [Min(0f)] public float connectionDistance = 64f;
    }

    [Serializable]
    public sealed class InkTechniqueSettings
    {
        [Tooltip("只在实际起收笔停顿点铺毫，不补画轨迹。")]
        public bool silkwormSwallowtail;
        [Tooltip("覆盖压力敏感度；保留速度、耗墨、侧锋和其他独立辅助。")]
        public bool uniformLine;
        [Tooltip("新脚印使用硬质崩口并禁止新墨进入 CA；不删除旧墨。")]
        public bool stoneEdges;
    }

    [Serializable]
    public sealed class InkBrushSettings
    {
        [Min(0.05f)] public float minimumRadius = 2f;
        [Min(1f)] public float maximumRadius = 22f;
        [Range(0.2f, 3f)] public float pressureExponent = 0.85f;
        [Range(0.06f, 0.35f)] public float spacingFraction = 0.18f;
        [Range(0.25f, 2f)] public float minimumSpacing = 0.5f;
        [Range(1f, 24f)] public float maximumSpacing = 10f;
        [Range(1f, 3f)] public float dropletLength = 1.85f;
        [Range(0.5f, 2f)] public float dropletWidth = 1f;
        [Range(0f, 1f)] public float contourRoundness = 1f;
        [Range(0.5f, 3f)] public float dropletTipPower = 1.12f;
        public InkEdgeNoise edgeNoise = InkEdgeNoise.PaperFiber;
        [Range(0f, 1f)] public float chipStrength = 0.45f;
        [Range(0.5f, 1.5f)] public float sideLength = 0.85f;
        [Range(1f, 2f)] public float sideWidth = 1.35f;
        [Min(1f)] public float turnRateForSideBrush = 600f; // degrees/second
        [Min(0.001f)] public float turnSmoothing = 0.04f;
        [Min(0.1f)] public float startTaperLength = 18f;
        [Min(0.1f)] public float endTaperLength = 14f;
        [Range(0.05f, 1f)] public float tipRadiusFraction = 0.22f;
        [Min(0f)] public float pigmentLoad = 1.4f;
        [Range(0f, 1f)] public float solubleFraction = 0.22f;
        [Range(0f, 2f)] public float waterLoad = 0.65f;
        [Range(0f, 1f)] public float initialInk = 1f;
        [Min(0f)] public float consumptionPer1000Pixels = 0.20f;
        [Tooltip("横轴为剩墨量，纵轴为耗墨倍率；所有风格共用同一条可编辑曲线。")]
        public AnimationCurve consumptionCurve = new AnimationCurve(new Keyframe(0f, 0.35f, 0f, 0f),
            new Keyframe(1f, 1f, 1.3f, 1.3f));
        [Range(0.1f, 3f)] public float consumptionWidthExponent = 1.2f;
        [Range(0.1f, 3f)] public float pigmentInkExponent = 1.35f;
        [Range(0f, 1f)] public float refillPerSecond = 0.07f;
        [Min(0f)] public float refillDelay = 0.5f;
        [Range(0f, 1f)] public float noiseStrength = 0.8f;
        [Range(0.01f, 0.5f)] public float noiseFrequency = 0.12f;
    }

    [Serializable]
    public sealed class InkPaperSettings
    {
        [Tooltip("主画布像素平方/秒；修改 CA 分辨率不会直接改变物理扩散尺度。")]
        [Min(0f)] public float inkDiffusion = 42f;
        [Min(0f)] public float waterDiffusion = 85f;
        [Min(0.05f)] public float saturationLimit = 1f;
        [Min(0f)] public float saturationFromDeposit = 0.12f;
        [Min(0f)] public float saturationFromInflux = 0.5f;
        [Min(0f)] public float absorptionRate = 0.10f;
        [Min(0.01f)] public float dryingRate = 0.65f;
        [Range(0.0001f, 0.1f)] public float freezeWetness = 0.008f;
        [Range(0f, 0.95f)] public float anisotropy = 0.45f;
        [Range(0f, 180f)] public float fiberAngle = 15f;
        [Range(0f, 1f)] public float fiberRandomness = 0.22f;
        [Range(15, 120)] public int simulationHz = 60;
        [Range(1, 12)] public int maximumSubsteps = 4;
    }

    [Serializable]
    public sealed class InkAppearanceSettings
    {
        public Color paperColor = new Color(0.94f, 0.91f, 0.83f, 1f);
        public Texture2D paperFiberTexture;
        [Range(1f, 32f)] public float paperTextureTiling = 4f;
        [Range(0f, 0.3f)] public float paperTextureStrength = 0.075f;
        [Range(0f, 2f)] public float edgeWarpPixels = 0.45f;
        public Vector3 inkAbsorption = new Vector3(2.4f, 2.6f, 2.8f);
        [Min(0.001f)] public float inkScattering = 0.35f;
        [Range(0f, 2f)] public float diffusionVisibility = 1f;
        public InkDebugView debugView;
    }
}
