using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.Profiling;
using UnityEngine.UI;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace RealtimeInk
{
    [DisallowMultipleComponent]
    public sealed partial class RealtimeInkCanvas : MonoBehaviour
    {
        [Header("References / 原生 GPU 管线")]
        public RawImage display;
        public ComputeShader dropletCompute;
        public ComputeShader diffusionCompute;
        public Shader compositeShader;
        [Header("Allocation / 修改后需重新启用组件")]
        public Vector2Int canvasResolution = new Vector2Int(2048, 2048);
        public Vector2Int diffusionResolution = new Vector2Int(512, 512);
        [Header("Prototype tuning / 可实时调节")]
        public InkInputSettings input = new InkInputSettings();
        public InkBrushSettings brush = new InkBrushSettings();
        public InkPaperSettings paper = new InkPaperSettings();
        public InkAppearanceSettings appearance = new InkAppearanceSettings();
        public InkBehaviorSettings behavior = new InkBehaviorSettings();
        [Header("Independent techniques / 预设不会覆盖这些开关")]
        public InkTechniqueSettings techniques = new InkTechniqueSettings();
        [SerializeField] InkStyle lastLoadedStyle = InkStyle.Regular;

        public InkStyle LastLoadedStyle => lastLoadedStyle;
        public bool IsInitialized => initialized;
        public bool LastStrokeContinued { get; private set; }
        public event Action SettingsChanged;

        public void ApplyStyle(InkStyle style)
        {
            InkStyleLibrary.Apply(style, input, brush, paper, behavior);
            lastLoadedStyle = style;
            // 不清纸、不补墨、不重置 path，也不更改 techniques。
            SettingsChanged?.Invoke();
        }

        public float InkRemaining { get; private set; }
        public int LastFrameStampCount { get; private set; }
        public int LastFrameCASteps { get; private set; }
        public RenderTexture PigmentCanvas => pigmentCanvas;
        public RenderTexture DiffusionState => state[readIndex];
        public RenderTexture Wetness => wet[readIndex];
        public RenderTexture Output => output;
        public event Action<InkPathNode> PathNodeGenerated;

        enum Source { Mouse, Touch, External }
        struct PointerSample
        {
            public Source source;
            public int id;
            public InkPointerPhase phase;
            public Vector2 position;
            public double time;
            public float pressure;
            public bool canvasUV;
        }

        readonly Queue<PointerSample> pendingInput = new Queue<PointerSample>(2048);
        readonly List<DropletStamp> pendingStamps = new List<DropletStamp>(4096);
        readonly RenderTexture[] state = new RenderTexture[2];
        readonly RenderTexture[] wet = new RenderTexture[2];
        readonly int[] shaderCanvasSize = new int[2];
        readonly int[] shaderCASize = new int[2];
        RenderTexture pigmentCanvas, output;
        Material composite;
        ComputeShader drawCS, caCS; // 每个画布独立实例，避免多个组件共享 shader 参数。
        InkTileBatch tiles;
        InkPathSampler path;
        InkStrokeDynamics dynamics;
        InputAction mousePosition, mousePress;
        bool initialized, inputAttached, pointerDown;
        Source owner;
        int ownerId, readIndex, drawKernel, clearKernel, injectKernel, stepKernel;
        int width, height, caWidth, caHeight;
        float simulationDebt, remainingWetBudget, secondsSinceLift, turnAmount;
        float strokeSeed;
        uint strokeNumber;
        double lastNodeTime;
        Vector2 lastNodeTangent, lastPointerPosition;
        float lastPointerPressure;
        bool lastPressureAvailable;
        double lastPointerTime;
        bool continuationValid;
        Source previousStrokeSource;
        Vector2 previousStrokeEnd;
        double previousStrokeEndTime;
        Texture previousDisplayTexture;

        void OnEnable()
        {
            if (!Application.isPlaying) return;
            if (Initialize()) AttachInput();
        }

        void OnDisable() { DetachInput(); ReleaseResources(); }
        void LateUpdate() { if (initialized) Tick(Time.unscaledDeltaTime); }

        /// <summary>通常由 OnEnable 调用；也供编辑器 GPU 验证使用。</summary>
        public bool Initialize()
        {
            if (initialized) return true;
            try
            {
                if (!SystemInfo.supportsComputeShaders) throw new NotSupportedException("设备不支持 ComputeShader。");
                if (!display || !dropletCompute || !diffusionCompute || !compositeShader)
                    throw new InvalidOperationException("请指定 RawImage、两个 ComputeShader 和 Composite Shader。");
                width = canvasResolution.x; height = canvasResolution.y;
                caWidth = diffusionResolution.x; caHeight = diffusionResolution.y;
                shaderCanvasSize[0] = width; shaderCanvasSize[1] = height;
                shaderCASize[0] = caWidth; shaderCASize[1] = caHeight;
                if (caWidth < 32 || caHeight < 32 || width > 4096 || height > 4096 ||
                    width % caWidth != 0 || height % caHeight != 0 ||
                    width / caWidth < 2 || height / caHeight < 2 || width / caWidth > 8 || height / caHeight > 8)
                    throw new ArgumentException("主画布每个维度必须是 CA 对应维度的 2~8 倍整数，CA 至少 32，主画布最大 4096。");
                var rgba = SupportedFormat(RenderTextureFormat.ARGBHalf, RenderTextureFormat.ARGBFloat);
                var rg = SupportedFormat(RenderTextureFormat.RGHalf, RenderTextureFormat.RGFloat);
                // 湿度用 RFloat：很慢的蒸发在 RHalf 的 ULP 以下可能永远减不动。
                var r = SupportedFormat(RenderTextureFormat.RFloat, RenderTextureFormat.RFloat);
                pigmentCanvas = CreateRT("Ink.FixedAndPending", width, height, rgba, true);
                output = CreateRT("Ink.Output", width, height, rgba, false);
                for (int i = 0; i < 2; i++)
                {
                    state[i] = CreateRT("Ink.CA." + i, caWidth, caHeight, rg, true);
                    wet[i] = CreateRT("Ink.Wet." + i, caWidth, caHeight, r, true);
                }
                drawCS = Instantiate(dropletCompute); caCS = Instantiate(diffusionCompute);
                SetKeyword(drawCS, "INK_MAIN_FLOAT32", rgba == RenderTextureFormat.ARGBFloat);
                SetKeyword(caCS, "INK_CA_FLOAT32", rg == RenderTextureFormat.RGFloat);
                SetKeyword(caCS, "INK_WET_FLOAT32", r == RenderTextureFormat.RFloat);
                drawKernel = drawCS.FindKernel("DrawDroplets"); clearKernel = drawCS.FindKernel("ClearPending");
                injectKernel = caCS.FindKernel("InjectNewInk"); stepKernel = caCS.FindKernel("IterateCA");
                if (!drawCS.IsSupported(drawKernel) || !drawCS.IsSupported(clearKernel) ||
                    !caCS.IsSupported(injectKernel) || !caCS.IsSupported(stepKernel))
                    throw new NotSupportedException("Compute kernel 不可用，请检查 shader 编译错误及 GPU API。");
                composite = new Material(compositeShader) { hideFlags = HideFlags.HideAndDontSave };
                tiles = new InkTileBatch(width, height);
                path = new InkPathSampler(input, brush, EmitNode, behavior, techniques);
                dynamics = new InkStrokeDynamics(behavior);
                previousDisplayTexture = display.texture;
                display.texture = output;
                initialized = true;
                ClearCanvas(); RefillInk();
                return true;
            }
            catch (Exception error)
            {
                Debug.LogError("RealtimeInk initialization: " + error.Message, this);
                ReleaseResources();
                return false;
            }
        }

        static RenderTextureFormat SupportedFormat(RenderTextureFormat preferred, RenderTextureFormat fallback)
        {
            if (SystemInfo.SupportsRenderTextureFormat(preferred) && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(preferred))
                return preferred;
            if (SystemInfo.SupportsRenderTextureFormat(fallback) && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(fallback))
                return fallback;
            throw new NotSupportedException("缺少所需的 UAV RenderTexture 格式：" + preferred);
        }

        static void SetKeyword(ComputeShader shader, string keyword, bool enabled)
        {
            if (enabled) shader.EnableKeyword(keyword); else shader.DisableKeyword(keyword);
        }

        static RenderTexture CreateRT(string label, int w, int h, RenderTextureFormat format, bool randomWrite)
        {
            var rt = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear)
            {
                name = label, enableRandomWrite = randomWrite, antiAliasing = 1,
                useMipMap = false, autoGenerateMips = false, wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave
            };
            if (!rt.Create()) { DestroyResource(rt); throw new InvalidOperationException("RT 创建失败：" + label); }
            return rt;
        }

        void AttachInput()
        {
            if (inputAttached) return;
            EnhancedTouchSupport.Enable();
            ETouch.onFingerDown += FingerDown; ETouch.onFingerMove += FingerMove; ETouch.onFingerUp += FingerUp;
            mousePosition = new InputAction("Ink.MousePosition", InputActionType.PassThrough, "<Mouse>/position");
            mousePress = new InputAction("Ink.MousePress", InputActionType.Button, "<Mouse>/leftButton");
            mousePosition.performed += MouseMoved;
            mousePress.performed += MouseBegan; mousePress.canceled += MouseEnded;
            mousePosition.Enable(); mousePress.Enable();
            inputAttached = true;
        }

        void DetachInput()
        {
            if (!inputAttached) return;
            ETouch.onFingerDown -= FingerDown; ETouch.onFingerMove -= FingerMove; ETouch.onFingerUp -= FingerUp;
            mousePosition.performed -= MouseMoved;
            mousePress.performed -= MouseBegan; mousePress.canceled -= MouseEnded;
            mousePosition.Dispose(); mousePress.Dispose();
            EnhancedTouchSupport.Disable();
            inputAttached = false;
        }

        /// <summary>回放或 UI 模式切换时可关闭设备采集，SubmitCanvasSample 仍可使用。</summary>
        public void SetInputEnabled(bool enabled)
        {
            if (enabled && initialized) AttachInput();
            else
            {
                DetachInput(); pendingInput.Clear();
                if (pointerDown) path.End(lastPointerPosition, lastPointerTime, lastPointerPressure, lastPressureAvailable);
                EndHistoryStroke();
                pointerDown = false; secondsSinceLift = 0f; continuationValid = false;
            }
        }

        void MouseMoved(InputAction.CallbackContext context)
        {
            if (Mouse.current != null && Mouse.current.leftButton.isPressed)
                CaptureMouse(context, InkPointerPhase.Move);
        }
        void MouseBegan(InputAction.CallbackContext context) => CaptureMouse(context, InkPointerPhase.Begin);
        void MouseEnded(InputAction.CallbackContext context) => CaptureMouse(context, InkPointerPhase.End);
        void CaptureMouse(InputAction.CallbackContext context, InkPointerPhase phase)
        {
            if (Mouse.current == null) return;
            pendingInput.Enqueue(new PointerSample { source = Source.Mouse, phase = phase,
                position = Mouse.current.position.ReadValue(), time = context.time });
        }
        void FingerDown(Finger finger) => CaptureTouch(finger, InkPointerPhase.Begin);
        void FingerMove(Finger finger) => CaptureTouch(finger, InkPointerPhase.Move);
        void FingerUp(Finger finger) => CaptureTouch(finger,
            finger.currentTouch.phase == UnityEngine.InputSystem.TouchPhase.Canceled ? InkPointerPhase.Cancel : InkPointerPhase.End);
        void CaptureTouch(Finger finger, InkPointerPhase phase)
        {
            var touch = finger.currentTouch;
            pendingInput.Enqueue(new PointerSample { source = Source.Touch, id = touch.touchId, phase = phase,
                position = touch.screenPosition, time = touch.time, pressure = touch.pressure });
        }

        /// <summary>可选回放/测试入口。UV 左下为 (0,0)，time 为单调递增秒数。</summary>
        public void SubmitCanvasSample(Vector2 uv, double time, InkPointerPhase phase, float nativePressure = 0f)
        {
            pendingInput.Enqueue(new PointerSample { source = Source.External, canvasUV = true,
                phase = phase, position = uv, time = time, pressure = nativePressure });
        }

        void ProcessInput()
        {
            while (pendingInput.Count > 0)
            {
                PointerSample sample = pendingInput.Dequeue();
                bool nativeAvailable = sample.source != Source.Mouse && input.touchPressure != TouchPressureMode.VirtualOnly &&
                    (input.touchPressure == TouchPressureMode.NativeTouch || sample.pressure > 0.0001f);
                Vector2 uv;
                bool inside = sample.canvasUV ? ValidUV(uv = sample.position) : ScreenToUV(sample.position, out uv);
                if (sample.phase == InkPointerPhase.Begin)
                {
                    if (pointerDown || !inside) continue; // 锁定首根手指，第二根手指不会造成跳线。
                    pointerDown = true; owner = sample.source; ownerId = sample.id;
                    lastPointerPosition = uv * new Vector2(width, height);
                    LastStrokeContinued = continuationValid && previousStrokeSource == sample.source &&
                        InkStrokeDynamics.CanContinue(behavior, sample.time - previousStrokeEndTime,
                            Vector2.Distance(lastPointerPosition, previousStrokeEnd));
                    if (!LastStrokeContinued)
                    {
                        lastNodeTangent = Vector2.zero; turnAmount = 0f;
                        strokeSeed = (++strokeNumber * 0.6180339887f) % 1000f;
                    }
                    lastNodeTime = sample.time;
                    dynamics.Begin(lastPointerPosition, sample.time, LastStrokeContinued);
                    BeginHistoryStroke();
                    path.Begin(lastPointerPosition, sample.time, sample.pressure, nativeAvailable, LastStrokeContinued);
                }
                else
                {
                    if (!pointerDown || sample.source != owner || sample.id != ownerId) continue;
                    if (!inside || sample.phase == InkPointerPhase.Cancel)
                    {
                        path.End(lastPointerPosition, lastPointerTime, lastPointerPressure, lastPressureAvailable);
                        EndHistoryStroke();
                        pointerDown = false; secondsSinceLift = 0f; continuationValid = false;
                        continue;
                    }
                    lastPointerPosition = uv * new Vector2(width, height);
                    dynamics.Observe(lastPointerPosition, sample.time);
                    if (sample.phase == InkPointerPhase.End)
                    {
                        path.End(lastPointerPosition, sample.time, sample.pressure, nativeAvailable);
                        if (techniques.silkwormSwallowtail && dynamics.SpreadStrength > 0f &&
                            !(dynamics.AtStart && dynamics.HasStartSpread))
                            path.EmitEndpointDab(lastPointerPosition, sample.time,
                                nativeAvailable ? sample.pressure : lastPointerPressure,
                                nativeAvailable || lastPressureAvailable, dynamics.SpreadStrength);
                        EndHistoryStroke();
                        continuationValid = true; previousStrokeEnd = lastPointerPosition;
                        previousStrokeEndTime = sample.time; previousStrokeSource = sample.source;
                        pointerDown = false; secondsSinceLift = 0f;
                    }
                    else path.Move(lastPointerPosition, sample.time, sample.pressure, nativeAvailable);
                }
                lastPointerTime = sample.time; lastPointerPressure = sample.pressure; lastPressureAvailable = nativeAvailable;
            }
        }

        static bool ValidUV(Vector2 uv) => uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;
        bool ScreenToUV(Vector2 screen, out Vector2 uv)
        {
            uv = Vector2.zero;
            Canvas canvas = display.canvas;
            Camera camera = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(display.rectTransform, screen, camera, out Vector2 local))
                return false;
            Rect rect = display.rectTransform.rect;
            if (rect.width <= 0f || rect.height <= 0f) return false;
            uv = new Vector2((local.x - rect.xMin) / rect.width, (local.y - rect.yMin) / rect.height);
            // 此原型要求 RawImage.uvRect=(0,0,1,1)，不支持通过 uvRect 镜像输入。
            return ValidUV(uv);
        }

        Vector3 PixelToWorld(Vector2 position)
        {
            Rect rect = display.rectTransform.rect;
            return display.rectTransform.TransformPoint(new Vector3(rect.xMin + rect.width * position.x / width,
                rect.yMin + rect.height * position.y / height, 0f));
        }

        void EmitNode(InkPathNode node)
        {
            node.worldPosition = PixelToWorld(node.canvasPosition);
            node.worldTangent = (PixelToWorld(node.canvasPosition + node.canvasTangent) - node.worldPosition).normalized;
            PathNodeGenerated?.Invoke(node);
            float dt = Mathf.Max(0.0001f, (float)(node.time - lastNodeTime));
            float turnRate = !node.isDwell && lastNodeTangent.sqrMagnitude > 0.1f ? Vector2.Angle(lastNodeTangent, node.canvasTangent) / dt : 0f;
            float targetTurn = Mathf.Clamp01(turnRate / Mathf.Max(1f, brush.turnRateForSideBrush));
            turnAmount = Mathf.Lerp(turnAmount, targetTurn, 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, brush.turnSmoothing)));
            lastNodeTime = node.time; lastNodeTangent = node.canvasTangent;
            float speed01 = Mathf.Clamp01(node.speed / Mathf.Max(1f, input.speedForThinStroke));
            float ink = InkRemaining;
            float length = Mathf.Max(0f, node.representedLength);
            float dose = brush.pigmentLoad * length / Mathf.Max(1f, 2f * node.radius) * Mathf.Pow(ink, brush.pigmentInkExponent);
            float dryness = Mathf.Clamp01((1f - ink) * 0.85f + speed01 * 0.3f);
            bool chips = techniques.stoneEdges || brush.edgeNoise == InkEdgeNoise.ChippedStone;
            if (dose > 0.000001f)
                pendingStamps.Add(new DropletStamp
                {
                    shape = new Vector4(node.canvasPosition.x, node.canvasPosition.y, node.radius,
                        node.isDwell ? Mathf.Min(1.2f, brush.dropletLength) : Mathf.Lerp(brush.dropletLength, brush.sideLength, turnAmount)),
                    deposit = new Vector4(node.canvasTangent.x, node.canvasTangent.y, dose,
                        techniques.stoneEdges ? 0f : brush.waterLoad * length / Mathf.Max(1f, 2f * node.radius) * ink * ink),
                    variation = new Vector4(brush.noiseStrength * dryness, strokeSeed,
                        techniques.stoneEdges ? 0f : brush.solubleFraction * ink * ink * (1f - 0.5f * speed01),
                        brush.dropletWidth * Mathf.Lerp(1f, brush.sideWidth, turnAmount)),
                    profile = new Vector4(techniques.stoneEdges ? Mathf.Min(0.2f, brush.contourRoundness) : brush.contourRoundness,
                        brush.dropletTipPower, brush.chipStrength, chips ? 1f : 0f)
                });
            // 路径长度积分 + 非线性宽度/墨量依赖，和帧率、脚印数量解耦。
            float consumption = brush.consumptionPer1000Pixels * length / 1000f *
                Mathf.Pow(node.radius / Mathf.Max(1f, brush.maximumRadius), brush.consumptionWidthExponent) *
                Mathf.Max(0f, brush.consumptionCurve != null ? brush.consumptionCurve.Evaluate(ink) : 1f);
            InkRemaining = Mathf.Max(0f, ink - consumption);
        }

        public void Tick(float deltaTime)
        {
            if (!initialized) return;
            if (IsRestoringHistory)
            {
                pendingInput.Clear(); // 恢复时不缓存鼠标轨迹，避免完成后出现跳线。
                PumpHistoryReplay();
                return;
            }
            LastFrameStampCount = 0;
            float frameTime = Mathf.Clamp(deltaTime, 0f, 0.1f);
            Profiler.BeginSample("Ink.InputAndSpline");
            ProcessInput();
            if (pointerDown)
            {
                dynamics.Advance(frameTime);
                if (techniques.silkwormSwallowtail && dynamics.TakeStartSpread(out float strength))
                    path.EmitEndpointDab(lastPointerPosition, lastPointerTime + dynamics.PauseSeconds,
                        lastPointerPressure, lastPressureAvailable, strength);
            }
            if (!pointerDown)
            {
                secondsSinceLift += frameTime;
                if (secondsSinceLift >= brush.refillDelay)
                    InkRemaining = Mathf.Min(1f, InkRemaining + brush.refillPerSecond * frameTime);
            }
            Profiler.EndSample();
            Profiler.BeginSample("Ink.GPUSchedule");
            DrawPendingStamps();
            // 浏览撤销记录时冻结 CA，保证下一次重做仍是原来的墨迹。
            if (historyCursor == history.Count) Simulate(frameTime);
            else LastFrameCASteps = 0;
            CaptureHistoryClock();
            Composite();
            Profiler.EndSample();
        }

        void SetCADimensions(float saturationLimit)
        {
            caCS.SetInts("_CanvasSize", shaderCanvasSize);
            caCS.SetInts("_CASize", shaderCASize);
            caCS.SetFloat("_SaturationLimit", Mathf.Max(0.05f, saturationLimit));
        }

        void BindCAPair(int kernel)
        {
            caCS.SetTexture(kernel, "_StateRead", state[readIndex]);
            caCS.SetTexture(kernel, "_WetRead", wet[readIndex]);
            caCS.SetTexture(kernel, "_StateWrite", state[1 - readIndex]);
            caCS.SetTexture(kernel, "_WetWrite", wet[1 - readIndex]);
        }

        void DrawPendingStamps()
        {
            if (pendingStamps.Count == 0) return;
            var settings = new GpuInkSettings(brush, paper);
            RecordStampBatch(settings);
            DrawStampBatch(pendingStamps, settings);
            pendingStamps.Clear();
        }

        void DrawStampBatch(System.Collections.Generic.IList<DropletStamp> stamps, GpuInkSettings settings)
        {
            LastFrameStampCount += stamps.Count;
            SetCADimensions(settings.saturationLimit);
            caCS.SetFloat("_SaturationFromDeposit", Mathf.Max(0f, settings.saturationFromDeposit));
            drawCS.SetInts("_CanvasSize", shaderCanvasSize);
            drawCS.SetInt("_TileColumns", tiles.TileColumns);
            drawCS.SetFloat("_NoiseFrequency", settings.noiseFrequency);
            drawCS.SetTexture(drawKernel, "_Canvas", pigmentCanvas);
            drawCS.SetTexture(clearKernel, "_Canvas", pigmentCanvas);
            drawCS.SetBuffer(drawKernel, "_Stamps", tiles.Stamps);
            drawCS.SetBuffer(drawKernel, "_TileOffsets", tiles.Offsets);
            drawCS.SetBuffer(drawKernel, "_ActiveTiles", tiles.ActiveTiles);
            drawCS.SetBuffer(clearKernel, "_ActiveTiles", tiles.ActiveTiles);
            bool addedWetInk = false;
            for (int first = 0; first < stamps.Count; first += InkTileBatch.BatchCapacity)
            {
                int count = Mathf.Min(InkTileBatch.BatchCapacity, stamps.Count - first);
                bool wetBatch = false;
                for (int i = first; i < first + count; i++)
                    wetBatch |= stamps[i].deposit.w > 0f || stamps[i].variation.z > 0f;
                tiles.Upload(stamps, first, count);
                if (tiles.ActiveCount == 0) continue;
                drawCS.SetBuffer(drawKernel, "_TileStampIndices", tiles.Indices);
                drawCS.SetInt("_ActiveTileCount", tiles.ActiveCount);
                int groupsX = Mathf.Min(65535, tiles.ActiveCount);
                int groupsY = (tiles.ActiveCount + groupsX - 1) / groupsX;
                drawCS.SetInt("_GroupsPerRow", groupsX);
                // 1. 高分辨率主墨迹；每个像素仅有一个线程写入。
                drawCS.Dispatch(drawKernel, groupsX, groupsY, 1);
                // 2. 精确 area average 下采样 GB 新墨/水，不会漏掉亚 CA 像素的细笔画。
                if (wetBatch)
                {
                    BindCAPair(injectKernel);
                    caCS.SetTexture(injectKernel, "_MainRead", pigmentCanvas);
                    caCS.Dispatch(injectKernel, (caWidth + 7) / 8, (caHeight + 7) / 8, 1);
                    readIndex = 1 - readIndex;
                    addedWetInk = true;
                }
                // 每批注入后清空临时 GB，旧墨不会在下一帧被再次注入。
                drawCS.Dispatch(clearKernel, groupsX, groupsY, 1);
            }
            // 最大值原理：湿度上限为 1；按实际蒸发预算休眠，实时修改 dryingRate 仍正确。
            if (addedWetInk) remainingWetBudget = 1.001f;
        }

        void Simulate(float frameTime)
        {
            LastFrameCASteps = 0;
            if (remainingWetBudget <= 0f) { simulationDebt = 0f; return; }
            float cellX = width / (float)caWidth, cellY = height / (float)caHeight;
            float maxRate = Mathf.Max(paper.inkDiffusion, paper.waterDiffusion) /
                Mathf.Max(1f, Mathf.Min(cellX * cellX, cellY * cellY));
            // 显式四邻域：每条边的系数 <= 0.24，四条流出之和 < 1。
            float step = Mathf.Min(1f / Mathf.Max(15, paper.simulationHz), 0.24f / Mathf.Max(0.0001f, maxRate));
            int budget = Mathf.Clamp(paper.maximumSubsteps, 1, 12);
            simulationDebt = Mathf.Min(simulationDebt + frameTime, step * budget);
            var settings = new GpuInkSettings(brush, paper);
            ConfigureCAStep(settings, step);
            while (simulationDebt + 0.000001f >= step && LastFrameCASteps < budget)
            {
                DispatchCAStep();
                simulationDebt -= step;
                remainingWetBudget -= Mathf.Max(0.01f, paper.dryingRate) * step;
                LastFrameCASteps++;
            }
            RecordSimulation(settings, step, LastFrameCASteps);
        }

        void ConfigureCAStep(GpuInkSettings settings, float step)
        {
            SetCADimensions(settings.saturationLimit);
            float cellX = width / (float)caWidth, cellY = height / (float)caHeight;
            caCS.SetFloat("_DeltaTime", step);
            caCS.SetVector("_InkCoefficients", new Vector4(settings.inkDiffusion * step / (cellX * cellX),
                settings.inkDiffusion * step / (cellY * cellY), 0f, 0f));
            caCS.SetVector("_WaterCoefficients", new Vector4(settings.waterDiffusion * step / (cellX * cellX),
                settings.waterDiffusion * step / (cellY * cellY), 0f, 0f));
            caCS.SetFloat("_DryingRate", Mathf.Max(0.01f, settings.dryingRate));
            caCS.SetFloat("_FreezeWetness", settings.freezeWetness);
            caCS.SetFloat("_SaturationFromInflux", settings.saturationFromInflux);
            caCS.SetFloat("_AbsorptionRate", settings.absorptionRate);
            caCS.SetFloat("_Anisotropy", settings.anisotropy);
            caCS.SetFloat("_FiberAngle", settings.fiberAngle * Mathf.Deg2Rad);
            caCS.SetFloat("_FiberRandomness", settings.fiberRandomness);
        }

        void DispatchCAStep()
        {
            BindCAPair(stepKernel);
            caCS.Dispatch(stepKernel, (caWidth + 7) / 8, (caHeight + 7) / 8, 1);
            readIndex = 1 - readIndex;
        }

        void Composite()
        {
            composite.SetTexture("_DiffusionTex", state[readIndex]);
            composite.SetTexture("_WetnessTex", wet[readIndex]);
            composite.SetTexture("_PaperTex", appearance.paperFiberTexture ? appearance.paperFiberTexture : Texture2D.grayTexture);
            composite.SetVector("_CanvasDimensions", new Vector4(width, height, 1f / width, 1f / height));
            composite.SetColor("_PaperLinear", appearance.paperColor.linear);
            composite.SetVector("_InkAbsorption", new Vector4(appearance.inkAbsorption.x,
                appearance.inkAbsorption.y, appearance.inkAbsorption.z, 0f));
            composite.SetFloat("_InkScattering", Mathf.Max(0.001f, appearance.inkScattering));
            composite.SetFloat("_PaperTiling", appearance.paperTextureTiling);
            composite.SetFloat("_PaperStrength", appearance.paperTextureStrength);
            composite.SetFloat("_EdgeWarpPixels", techniques.stoneEdges || brush.edgeNoise == InkEdgeNoise.ChippedStone ? 0f : appearance.edgeWarpPixels);
            composite.SetFloat("_DiffusionVisibility", appearance.diffusionVisibility);
            composite.SetFloat("_SaturationLimit", Mathf.Max(0.05f, paper.saturationLimit));
            composite.SetInt("_DebugView", (int)appearance.debugView);
            composite.SetInt("_ExportTransparent", 0);
            // 源数据 RT 与输出 RT 不同，绝不把上一帧的复合颜色反馈进墨量。
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(pigmentCanvas, output, composite, 0);
            RenderTexture.active = previous;
        }

        [ContextMenu("Clear Canvas / 清纸")]
        public void ClearCanvas()
        {
            if (!initialized) return;
            pointerDown = false; path.Cancel(); pendingInput.Clear(); pendingStamps.Clear();
            continuationValid = false; LastStrokeContinued = false;
            ResetHistory();
            ResetGpuState();
            Composite();
            EditStatus = "已重写：所有笔画已清除";
        }

        void ResetGpuState()
        {
            simulationDebt = remainingWetBudget = 0f; readIndex = 0;
            ClearRT(pigmentCanvas);
            for (int i = 0; i < 2; i++) { ClearRT(state[i]); ClearRT(wet[i]); }
        }

        [ContextMenu("Refill Ink / 蘸墨")]
        public void RefillInk() => InkRemaining = Mathf.Clamp01(brush.initialInk);

        static void ClearRT(RenderTexture rt)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt; GL.Clear(false, true, Color.clear); RenderTexture.active = previous;
        }

        void OnApplicationFocus(bool focus)
        {
            if (focus || !initialized) return;
            pendingInput.Clear();
            if (pointerDown) path.End(lastPointerPosition, lastPointerTime, lastPointerPressure, lastPressureAvailable);
            EndHistoryStroke();
            pointerDown = false; secondsSinceLift = 0f; continuationValid = false;
        }

        public void ReleaseResources()
        {
            DetachInput(); continuationValid = false;
            initialized = false; pointerDown = false;
            pendingInput.Clear(); pendingStamps.Clear(); path?.Cancel();
            ResetHistory();
            if (display && display.texture == output) display.texture = previousDisplayTexture;
            tiles?.Dispose(); tiles = null;
            ReleaseRT(ref pigmentCanvas); ReleaseRT(ref output);
            for (int i = 0; i < 2; i++) { ReleaseRT(ref state[i]); ReleaseRT(ref wet[i]); }
            DestroyResource(composite); DestroyResource(drawCS); DestroyResource(caCS);
            composite = null; drawCS = null; caCS = null;
        }

        static void ReleaseRT(ref RenderTexture rt)
        {
            if (!rt) return;
            if (RenderTexture.active == rt) RenderTexture.active = null;
            rt.Release(); DestroyResource(rt); rt = null;
        }
        static void DestroyResource(UnityEngine.Object value)
        {
            if (!value) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }
    }
}
