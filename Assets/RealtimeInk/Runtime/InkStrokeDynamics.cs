using System;
using UnityEngine;

namespace RealtimeInk
{
    /// <summary>时间/停顿判定，不改变任何路径位置；首部铺毫最多四级，不随按住时间无限加墨。</summary>
    public sealed class InkStrokeDynamics
    {
        readonly InkBehaviorSettings settings;
        Vector2 anchor, previous;
        double previousTime, lastMotionTime;
        bool movedThisTick;
        int startSpreadStage;
        public float PauseSeconds { get; private set; }
        public bool AtStart { get; private set; }
        public bool HasStartSpread => startSpreadStage > 0;
        public float SpreadStrength => PauseSeconds < settings.pauseDuration ? 0f :
            Mathf.Lerp(0.25f, 1f, Mathf.Clamp01((PauseSeconds - settings.pauseDuration) /
                Mathf.Max(0.05f, settings.pauseSpreadDuration)));

        public InkStrokeDynamics(InkBehaviorSettings settings) { this.settings = settings; }
        public void Begin(Vector2 position, double time, bool continuation)
        {
            anchor = previous = position; previousTime = lastMotionTime = time;
            PauseSeconds = 0f; AtStart = !continuation; startSpreadStage = 0; movedThisTick = true;
        }
        public void Observe(Vector2 position, double time)
        {
            double dt = Math.Max(0.0001, time - previousTime);
            float step = Vector2.Distance(position, previous);
            bool motion = Vector2.Distance(position, anchor) > settings.pauseDistanceThreshold ||
                (step > settings.pauseDistanceThreshold * 0.25f && step / dt > settings.pauseSpeedThreshold);
            if (motion)
            {
                anchor = position; lastMotionTime = time; PauseSeconds = 0f;
                AtStart = false; movedThisTick = true;
            }
            else PauseSeconds = Mathf.Max(PauseSeconds, (float)Math.Max(0, time - lastMotionTime));
            previous = position; previousTime = time;
        }
        public void Advance(float seconds)
        {
            if (!movedThisTick) PauseSeconds += Mathf.Max(0f, seconds);
            movedThisTick = false;
        }
        public bool TakeStartSpread(out float strength)
        {
            strength = 0f;
            if (!AtStart || PauseSeconds < settings.pauseDuration) return false;
            int target = Mathf.Clamp(1 + Mathf.FloorToInt((PauseSeconds - settings.pauseDuration) /
                Mathf.Max(0.05f, settings.pauseSpreadDuration) * 3f), 1, 4);
            if (target <= startSpreadStage) return false;
            startSpreadStage = target; strength = target / 4f;
            return true;
        }
        public static bool CanContinue(InkBehaviorSettings settings, double gap, float distance) =>
            gap >= 0 && gap <= settings.connectionTimeout && settings.connectionTimeout > 0f &&
            distance <= settings.connectionDistance;
    }
}
