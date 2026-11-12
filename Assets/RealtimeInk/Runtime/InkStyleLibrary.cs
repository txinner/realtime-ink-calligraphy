namespace RealtimeInk
{
    /// <summary>
    /// 一份通用默认值 + 六组稀疏差异。只复制风格拥有的字段，不复制整套设备/纸张配置。
    /// 渲染器不检查 InkStyle；切换后普通字段可继续编辑，辅助开关完全不受影响。
    /// </summary>
    public static class InkStyleLibrary
    {
        public static string Name(InkStyle style)
        {
            switch (style)
            {
                case InkStyle.Regular: return "楷书";
                case InkStyle.Running: return "行书";
                case InkStyle.Cursive: return "草书";
                case InkStyle.Seal: return "篆书";
                case InkStyle.Clerical: return "隶书";
                case InkStyle.Stele: return "魏碑";
                default: return "自定义";
            }
        }

        public static void Apply(InkStyle style, InkInputSettings input, InkBrushSettings brush,
            InkPaperSettings paper, InkBehaviorSettings behavior)
        {
            // 默认值只定义在公共设置类；不维护六份完整参数对象/资产。
            var b = new InkBrushSettings();
            var p = new InkPaperSettings();
            var h = new InkBehaviorSettings();
            float minPressure = new InkInputSettings().minimumPressure;
            switch (style)
            {
                case InkStyle.Regular:
                    b.consumptionPer1000Pixels = 0.12f; b.noiseStrength = 0.2f;
                    p.inkDiffusion = 32f; h.connectionTimeout = 0.10f;
                    break;
                case InkStyle.Running:
                    b.consumptionPer1000Pixels = 0.28f; b.noiseStrength = 0.6f;
                    b.minimumRadius = 0.4f; minPressure = 0.07f;
                    h.protectHairlines = true; h.hairlineMinimumWidth = 1.1f; h.connectionTimeout = 0.36f;
                    break;
                case InkStyle.Cursive:
                    b.consumptionPer1000Pixels = 0.52f; b.noiseStrength = 1f;
                    b.minimumRadius = 0.1f; b.pressureExponent = 1.35f; minPressure = 0.025f;
                    b.tipRadiusFraction = 0.08f; h.protectHairlines = true;
                    h.hairlineMinimumWidth = 0.18f; h.connectionTimeout = 0.55f;
                    break;
                case InkStyle.Seal:
                    h.pressureSensitivity = 0.12f; b.noiseStrength = 0f;
                    p.inkDiffusion = 20f; b.solubleFraction = 0.12f;
                    b.consumptionPer1000Pixels = 0.10f; b.turnRateForSideBrush = 1100f;
                    break;
                case InkStyle.Clerical:
                    b.dropletLength = 1.15f; b.dropletWidth = 1.4f;
                    b.sideLength = 0.65f; b.sideWidth = 1.6f;
                    h.pauseDuration = 0.09f; h.endpointWidthGain = 1.15f;
                    b.noiseStrength = 0.4f; b.consumptionPer1000Pixels = 0.17f;
                    break;
                case InkStyle.Stele:
                    p.inkDiffusion = 4f; b.solubleFraction = 0.025f; b.waterLoad = 0.2f;
                    b.edgeNoise = InkEdgeNoise.ChippedStone; b.contourRoundness = 0.18f;
                    b.chipStrength = 0.8f; b.noiseStrength = 0.8f; b.dropletTipPower = 0.8f;
                    break;
            }
            // 唯一的风格字段清单。没有出现的字段保持用户当前值，例如墨曲线、回墨和设备标定。
            brush.consumptionPer1000Pixels = b.consumptionPer1000Pixels;
            brush.noiseStrength = b.noiseStrength;
            brush.minimumRadius = b.minimumRadius;
            brush.pressureExponent = b.pressureExponent;
            brush.tipRadiusFraction = b.tipRadiusFraction;
            brush.dropletLength = b.dropletLength;
            brush.dropletWidth = b.dropletWidth;
            brush.sideLength = b.sideLength;
            brush.sideWidth = b.sideWidth;
            brush.turnRateForSideBrush = b.turnRateForSideBrush;
            brush.solubleFraction = b.solubleFraction;
            brush.waterLoad = b.waterLoad;
            brush.edgeNoise = b.edgeNoise;
            brush.contourRoundness = b.contourRoundness;
            brush.chipStrength = b.chipStrength;
            brush.dropletTipPower = b.dropletTipPower;
            paper.inkDiffusion = p.inkDiffusion;
            behavior.pressureSensitivity = h.pressureSensitivity;
            behavior.protectHairlines = h.protectHairlines;
            behavior.hairlineMinimumWidth = h.hairlineMinimumWidth;
            behavior.connectionTimeout = h.connectionTimeout;
            behavior.pauseDuration = h.pauseDuration;
            behavior.endpointWidthGain = h.endpointWidthGain;
            input.minimumPressure = minPressure;
        }
    }
}
