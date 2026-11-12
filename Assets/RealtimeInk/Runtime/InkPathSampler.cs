using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealtimeInk
{
    public struct InkPathNode
    {
        public Vector2 canvasPosition;
        public Vector2 canvasTangent;
        public Vector3 worldPosition;
        public Vector3 worldTangent;
        public float pressure;
        public float speed;
        public double time;
        public float radius;
        public float arcLength;
        public float representedLength; // 此脚印负责的路径长度，用于与采样密度无关的墨量积分。
        public bool isDwell; // 起收笔辅助的原地脚印，不增加路径弧长。
    }

    /// <summary>
    /// 距离去抖 + 时间/距离混合重采样 + 向心 Catmull-Rom + 跨段弧长游标。
    /// 保留一个控制点的前瞻；不反复重画已提交的整条曲线。
    /// </summary>
    public sealed class InkPathSampler
    {
        struct Point
        {
            public Vector2 p;
            public float pressure, speed;
            public double time;
            public static Point Lerp(Point a, Point b, float t) => new Point
            {
                p = Vector2.LerpUnclamped(a.p, b.p, t),
                pressure = Mathf.Lerp(a.pressure, b.pressure, t),
                speed = Mathf.Lerp(a.speed, b.speed, t),
                time = a.time + (b.time - a.time) * t
            };
        }

        readonly InkInputSettings input;
        readonly InkBrushSettings brush;
        readonly InkBehaviorSettings behavior;
        readonly InkTechniqueSettings techniques;
        readonly Action<InkPathNode> emit;
        readonly List<Point> controls = new List<Point>(4);
        Point lastAccepted, lastControl, a, b, c, d;
        Vector2 lastTangent = Vector2.right;
        float filteredSpeed, filteredPressure, arcLength, untilNext, sinceLastStamp;
        float segmentLength;
        bool active, finalSegment, continuing;
        public bool IsActive => active;

        public InkPathSampler(InkInputSettings input, InkBrushSettings brush, Action<InkPathNode> emit,
            InkBehaviorSettings behavior = null, InkTechniqueSettings techniques = null)
        {
            this.input = input;
            this.brush = brush;
            this.emit = emit;
            this.behavior = behavior ?? new InkBehaviorSettings();
            this.techniques = techniques ?? new InkTechniqueSettings();
        }

        float Pressure(float native, bool nativeAvailable, float speed)
        {
            if (nativeAvailable)
                return Mathf.Clamp(native / Mathf.Max(0.01f, input.nativePressureMaximum), input.minimumPressure, 1f);
            float slow = 1f / (1f + Mathf.Pow(speed / Mathf.Max(1f, input.speedForThinStroke),
                Mathf.Max(0.1f, input.virtualPressureExponent)));
            return Mathf.Lerp(input.minimumPressure, 1f, slow);
        }

        public void Begin(Vector2 position, double time, float native, bool nativeAvailable, bool continueBrush = false)
        {
            active = true;
            controls.Clear();
            continuing = continueBrush;
            if (!continuing) filteredSpeed = 0f;
            arcLength = sinceLastStamp = 0f;
            float pressure = Pressure(native, nativeAvailable, filteredSpeed);
            filteredPressure = continuing ? Mathf.Lerp(filteredPressure, pressure, 0.35f) : pressure;
            lastAccepted = lastControl = new Point { p = position, time = time, pressure = filteredPressure };
            controls.Add(lastControl); // 首段会用镜像外推，避免重复点的零长度 knot。
            controls.Add(lastControl);
            if (!continuing) lastTangent = Vector2.right;
            var node = MakeNode(lastControl, lastTangent, continuing ? 1f : brush.tipRadiusFraction);
            node.representedLength = Spacing(node.radius);
            emit(node); // 按下即有细小起笔；不必等待第三个控制点。
            untilNext = Spacing(node.radius);
        }

        public void Move(Vector2 position, double time, float native, bool nativeAvailable)
        {
            if (!active) return;
            float distance = Vector2.Distance(lastAccepted.p, position);
            if (distance < Mathf.Max(0.1f, input.jitterDistance)) return;
            double monotonicTime = Math.Max(time, lastAccepted.time + 0.00001);
            float dt = (float)(monotonicTime - lastAccepted.time);
            float instantaneousSpeed = distance / Mathf.Max(0.0001f, dt);
            filteredSpeed = Mathf.Lerp(filteredSpeed, instantaneousSpeed,
                1f - Mathf.Exp(-dt / Mathf.Max(0.001f, input.speedSmoothing)));
            float targetPressure = Pressure(native, nativeAvailable, filteredSpeed);
            filteredPressure = Mathf.Lerp(filteredPressure, targetPressure,
                1f - Mathf.Exp(-dt / Mathf.Max(0.001f, input.pressureSmoothing)));
            var next = new Point { p = position, pressure = filteredPressure, speed = filteredSpeed, time = monotonicTime };
            int count = Mathf.CeilToInt(Mathf.Max(distance / Mathf.Max(0.5f, input.controlPointDistance),
                dt / Mathf.Max(0.001f, input.controlPointInterval)));
            // 长时间停笔后的首个移动不能产生数万个重合的时间样本。
            count = Mathf.Clamp(count, 1, Mathf.Max(1, Mathf.FloorToInt(distance / Mathf.Max(0.1f, input.jitterDistance))));
            for (int i = 1; i <= count; i++)
            {
                var point = Point.Lerp(lastAccepted, next, i / (float)count);
                if (Vector2.Distance(lastControl.p, point.p) >= Mathf.Max(0.1f, input.jitterDistance) * 0.99f)
                    AddControl(point, false);
            }
            lastAccepted = next;
        }

        public void End(Vector2 position, double time, float native, bool nativeAvailable)
        {
            if (!active) return;
            Move(position, time, native, nativeAvailable);
            // 抬笔坐标必须保留，不能被距离去抖吃掉。
            if (Vector2.Distance(lastControl.p, position) > 0.001f)
            {
                var end = lastAccepted;
                end.p = position;
                end.time = Math.Max(time, lastControl.time + 0.00001);
                AddControl(end, false);
            }
            if (controls.Count == 3)
            {
                var phantom = controls[2];
                phantom.p = 2f * controls[2].p - controls[1].p;
                phantom.time += Math.Max(0.0001, controls[2].time - controls[1].time);
                AddControl(phantom, true);
                // 补齐残余弧长，端点仍停在用户实际抬笔位置。
                if (sinceLastStamp > 0.001f)
                {
                    var endNode = MakeNode(c, lastTangent, brush.tipRadiusFraction);
                    endNode.representedLength = sinceLastStamp;
                    emit(endNode);
                }
            }
            active = false;
            controls.Clear();
        }

        public void Cancel() { active = false; controls.Clear(); }

        public void EmitEndpointDab(Vector2 position, double time, float native, bool nativeAvailable, float strength)
        {
            Point point = lastAccepted;
            point.p = position; point.time = time; point.speed = 0f;
            point.pressure = Pressure(native, nativeAvailable, 0f);
            var node = MakeNode(point, lastTangent, 1f);
            node.radius *= 1f + behavior.endpointWidthGain * Mathf.Clamp01(strength);
            node.representedLength = 2f * node.radius * behavior.endpointDeposit;
            node.isDwell = true;
            emit(node);
        }

        void AddControl(Point point, bool isLast)
        {
            controls.Add(point);
            lastControl = point;
            if (controls.Count < 4) return;
            a = controls[0]; b = controls[1]; c = controls[2]; d = controls[3];
            if ((a.p - b.p).sqrMagnitude < 0.000001f) a.p = 2f * b.p - c.p;
            if ((d.p - c.p).sqrMagnitude < 0.000001f) d.p = 2f * c.p - b.p;
            finalSegment = isLast;
            // 只用于收锋的近似段长；实际节点位置由下面的自适应弧长积分决定。
            segmentLength = EstimateSegmentLength();
            Flatten(0f, b.p, 1f, c.p, 0);
            controls.RemoveAt(0);
        }

        // Barry-Goldman 向心参数化，alpha=0.5。避免非均匀点间距下的尖环。
        public static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float u)
        {
            float t0 = 0f;
            float t1 = t0 + Mathf.Max(0.001f, Mathf.Sqrt(Vector2.Distance(p0, p1)));
            float t2 = t1 + Mathf.Max(0.001f, Mathf.Sqrt(Vector2.Distance(p1, p2)));
            float t3 = t2 + Mathf.Max(0.001f, Mathf.Sqrt(Vector2.Distance(p2, p3)));
            float t = Mathf.Lerp(t1, t2, u);
            Vector2 a1 = Vector2.LerpUnclamped(p0, p1, (t - t0) / (t1 - t0));
            Vector2 a2 = Vector2.LerpUnclamped(p1, p2, (t - t1) / (t2 - t1));
            Vector2 a3 = Vector2.LerpUnclamped(p2, p3, (t - t2) / (t3 - t2));
            Vector2 b1 = Vector2.LerpUnclamped(a1, a2, (t - t0) / (t2 - t0));
            Vector2 b2 = Vector2.LerpUnclamped(a2, a3, (t - t1) / (t3 - t1));
            return Vector2.LerpUnclamped(b1, b2, (t - t1) / (t2 - t1));
        }

        Vector2 Eval(float t) => CatmullRom(a.p, b.p, c.p, d.p, t);

        float EstimateSegmentLength()
        {
            float length = 0f;
            Vector2 prev = b.p;
            for (int i = 1; i <= 8; i++)
            {
                Vector2 next = Eval(i / 8f);
                length += Vector2.Distance(prev, next);
                prev = next;
            }
            return length;
        }

        void Flatten(float t0, Vector2 p0, float t1, Vector2 p1, int depth)
        {
            float mid = (t0 + t1) * 0.5f;
            Vector2 pm = Eval(mid);
            float chord = Vector2.Distance(p0, p1);
            float error = Vector2.Distance(pm, (p0 + p1) * 0.5f);
            if (depth < 14 && (chord > Mathf.Max(0.25f, input.arcChordLength) || error > input.arcError))
            {
                Flatten(t0, p0, mid, pm, depth + 1);
                Flatten(mid, pm, t1, p1, depth + 1);
                return;
            }
            if (chord < 0.000001f) return;
            float consumed = 0f;
            while (chord - consumed + 0.000001f >= untilNext)
            {
                consumed += untilNext;
                arcLength += untilNext;
                sinceLastStamp += untilNext;
                float t = Mathf.Lerp(t0, t1, Mathf.Clamp01(consumed / chord));
                var sample = Point.Lerp(b, c, t);
                sample.p = Vector2.Lerp(p0, p1, Mathf.Clamp01(consumed / chord));
                Vector2 tangent = Eval(Mathf.Min(1f, t + 0.001f)) - Eval(Mathf.Max(0f, t - 0.001f));
                if (tangent.sqrMagnitude > 0.000001f) lastTangent = tangent.normalized;
                float start = continuing ? 1f : Mathf.Lerp(brush.tipRadiusFraction, 1f,
                    Mathf.Clamp01(arcLength / Mathf.Max(0.1f, brush.startTaperLength)));
                float end = finalSegment ? Mathf.Lerp(brush.tipRadiusFraction, 1f,
                    Mathf.Clamp01(segmentLength * (1f - t) / Mathf.Max(0.1f, brush.endTaperLength))) : 1f;
                var node = MakeNode(sample, lastTangent, Mathf.Min(start, end));
                node.representedLength = sinceLastStamp;
                emit(node);
                sinceLastStamp = 0f;
                untilNext = Spacing(node.radius);
            }
            float residual = Mathf.Max(0f, chord - consumed);
            arcLength += residual;
            sinceLastStamp += residual;
            untilNext = Mathf.Max(0.00001f, untilNext - residual);
        }

        InkPathNode MakeNode(Point point, Vector2 tangent, float taper)
        {
            float sensitivity = techniques.uniformLine ? 0f : behavior.pressureSensitivity;
            float widthPressure = Mathf.Lerp(behavior.uniformPressure, point.pressure, Mathf.Clamp01(sensitivity));
            float radius = Mathf.Lerp(brush.minimumRadius, Mathf.Max(brush.minimumRadius, brush.maximumRadius),
                Mathf.Pow(Mathf.Clamp01(widthPressure), Mathf.Max(0.2f, brush.pressureExponent))) * taper;
            float minimum = behavior.protectHairlines ? Mathf.Max(0.05f, behavior.hairlineMinimumWidth * 0.5f) : 0.05f;
            return new InkPathNode
            {
                canvasPosition = point.p, canvasTangent = tangent, pressure = point.pressure,
                speed = point.speed, time = point.time, radius = Mathf.Max(minimum, radius), arcLength = arcLength
            };
        }

        float Spacing(float radius)
        {
            // 按最短轴的保守下界确定间距；侧锋变扁也不会断线。
            float minor = Mathf.Min(1f, Mathf.Max(0.5f, brush.sideLength));
            return Mathf.Clamp(2f * radius * minor * brush.spacingFraction,
                Mathf.Max(0.25f, brush.minimumSpacing), Mathf.Max(1f, brush.maximumSpacing));
        }
    }
}
