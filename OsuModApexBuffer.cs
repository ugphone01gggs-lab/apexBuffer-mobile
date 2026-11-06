// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.UI;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;

namespace osu.Game.Rulesets.Osu.Mods
{
    /// <summary>
    /// Practice assist: adaptively retimes early presses toward Perfect, and soft-spaces
    /// stream spam. Unranked — offline/practice on this fork.
    /// </summary>
    public partial class OsuModApexBuffer : Mod, IApplicableToDrawableRuleset<OsuHitObject>, IUpdatableByPlayfield
    {
        public override string Name => "ApexBuffer";
        public override string Acronym => "AB";
        public override LocalisableString Description => "Auto-adaptive early-tap delay + stream pacing. Practice assist — unranked.";
        public override double ScoreMultiplier => 1;
        public override bool Ranked => false;
        public override ModType Type => ModType.Fun;

        public override Type[] IncompatibleMods => new[]
        {
            typeof(ModAutoplay),
            typeof(ModRelax),
            typeof(OsuModCinema),
            typeof(OsuModAutopilot),
        };

        [SettingSource("Early timing delay", "Delay early presses toward better timing (auto strength).", 0)]
        public BindableBool EarlyTimingEnabled { get; } = new BindableBool(true);

        [SettingSource("Max early window (ms)", "Maximum early offset that may be corrected.", 1)]
        public BindableInt MaxEarlyMs { get; } = new BindableInt(50)
        {
            MinValue = 15,
            MaxValue = 120,
        };

        [SettingSource("Stream pace assist", "When tapping a stream too fast, soft-delay presses toward note spacing.", 2)]
        public BindableBool StreamAssistEnabled { get; } = new BindableBool(true);

        [SettingSource("Stream min gap scale", "1.0 = full note spacing. Lower = allow slightly faster taps.", 3)]
        public BindableFloat StreamGapScale { get; } = new BindableFloat(0.90f)
        {
            MinValue = 0.6f,
            MaxValue = 1.0f,
            Precision = 0.05f,
        };

        private DrawableOsuRuleset ruleset = null!;
        private OsuInputManager inputManager = null!;
        private readonly List<PendingTap> pending = new List<PendingTap>();
        private double lastAcceptedPressTime = double.NegativeInfinity;
        private bool isInjecting;

        private readonly struct PendingTap
        {
            public readonly OsuAction Action;
            public readonly double FireAt;
            public readonly bool IsPress;

            public PendingTap(OsuAction action, double fireAt, bool isPress)
            {
                Action = action;
                FireAt = fireAt;
                IsPress = isPress;
            }
        }

        public void ApplyToDrawableRuleset(DrawableRuleset<OsuHitObject> drawableRuleset)
        {
            ruleset = (DrawableOsuRuleset)drawableRuleset;
            inputManager = ruleset.KeyBindingInputManager;
            inputManager.Add(new InputInterceptor(this));
            pending.Clear();
            lastAcceptedPressTime = double.NegativeInfinity;
        }

        public void Update(Playfield playfield)
        {
            if (pending.Count == 0)
                return;

            double time = playfield.Clock.CurrentTime;

            // Drop stale queued taps that are hopelessly late
            pending.RemoveAll(t => t.IsPress && t.FireAt < time - 30);

            for (int i = 0; i < pending.Count;)
            {
                var tap = pending[i];
                if (time + 0.01 < tap.FireAt)
                {
                    i++;
                    continue;
                }

                pending.RemoveAt(i);
                inject(tap.Action, tap.IsPress);

                if (tap.IsPress)
                    lastAcceptedPressTime = Math.Max(lastAcceptedPressTime, time);
            }
        }

        private void inject(OsuAction action, bool press)
        {
            isInjecting = true;
            try
            {
                if (press)
                    inputManager.KeyBindingContainer.TriggerPressed(action);
                else
                    inputManager.KeyBindingContainer.TriggerReleased(action);
            }
            finally
            {
                isInjecting = false;
            }
        }

        private bool tryInterceptPress(OsuAction action)
        {
            if (isInjecting)
                return false;

            if (action is not (OsuAction.LeftButton or OsuAction.RightButton))
                return false;

            if (inputManager.ReplayInputHandler != null)
                return false;

            // Avoid stacking long queues (main cause of "delay until miss")
            if (pending.Any(t => t.IsPress))
            {
                lastAcceptedPressTime = ruleset.FrameStableClock.CurrentTime;
                return false;
            }

            double now = ruleset.FrameStableClock.CurrentTime;
            var target = findNextTarget(now);
            if (target == null)
            {
                lastAcceptedPressTime = now;
                return false;
            }

            double objectTime = target.HitObject.StartTime;
            double offset = now - objectTime; // negative = early
            double greatWindow = target.HitObject.HitWindows?.WindowFor(HitResult.Great) ?? 40;
            if (greatWindow < 10)
                greatWindow = 10;

            // --- Stream pace assist (soft) ---
            if (StreamAssistEnabled.Value)
            {
                double minGap = estimateStreamGap(now) * StreamGapScale.Value;
                double sinceLast = now - lastAcceptedPressTime;

                if (minGap > 12 && sinceLast < minGap)
                {
                    // Only delay the excess, not a full note period
                    double excess = minGap - sinceLast;
                    // Auto scale down large corrections
                    double streamWait = excess * 0.55;
                    // Hard caps
                    streamWait = Math.Min(streamWait, Math.Min(24, greatWindow * 0.5));

                    double fireAt = now + streamWait;
                    // Never intentionally fire after the note time
                    fireAt = Math.Min(fireAt, objectTime);
                    // Keep inside early-Great band
                    fireAt = Math.Max(fireAt, objectTime - greatWindow * 0.8);

                    if (fireAt > now + 1.0 && fireAt - now <= 24)
                    {
                        queueTap(action, fireAt);
                        return true;
                    }
                }
            }

            // --- Early timing delay (auto-adaptive) ---
            if (EarlyTimingEnabled.Value && offset < -1)
            {
                double earlyMs = -offset;
                double maxEarly = MaxEarlyMs.Value;

                // Ignore absurdly early presses (likely wrong target / previous note)
                if (earlyMs > maxEarly)
                {
                    lastAcceptedPressTime = now;
                    return false;
                }

                // Already close enough to Great center → do not delay
                if (earlyMs <= Math.Max(6, greatWindow * 0.12))
                {
                    lastAcceptedPressTime = now;
                    return false;
                }

                double fireAt = computeAdaptiveFireAt(now, objectTime, greatWindow, earlyMs, maxEarly);

                if (fireAt > now + 1.0)
                {
                    queueTap(action, fireAt);
                    return true;
                }
            }

            lastAcceptedPressTime = now;
            return false;
        }

        /// <summary>
        /// Auto delay: small early errors are corrected more strongly; large early errors
        /// only partially, with a hard wait cap so we do not miss.
        /// </summary>
        private static double computeAdaptiveFireAt(double now, double objectTime, double greatWindow, double earlyMs, double maxEarly)
        {
            // 0 at tiny early, 1 near maxEarly
            double t = Math.Clamp(earlyMs / Math.Max(maxEarly, 1), 0, 1);

            // Strength: ~0.92 for small early, ~0.50 for large early
            double strength = 0.92 - 0.42 * (t * t);

            double desiredWait = earlyMs * strength;

            // Absolute hard cap: never wait longer than this (ms)
            double hardCap = Math.Min(maxEarly, Math.Max(14, greatWindow * 0.9));
            hardCap = Math.Min(hardCap, 36); // global safety: 36ms max wait
            desiredWait = Math.Min(desiredWait, hardCap);

            double fireAt = now + desiredWait;

            // Prefer not after note center
            fireAt = Math.Min(fireAt, objectTime);

            // Must remain within Great window if clock drifts slightly
            fireAt = Math.Max(fireAt, objectTime - greatWindow * 0.75);

            // If clamp removed the wait, skip assist
            if (fireAt <= now + 1.0)
                return now;

            return fireAt;
        }

        private void queueTap(OsuAction action, double fireAt)
        {
            pending.Clear(); // one shot at a time
            pending.Add(new PendingTap(action, fireAt, true));
            pending.Add(new PendingTap(action, fireAt + 12, false));
        }

        private DrawableOsuHitObject? findNextTarget(double time)
        {
            DrawableOsuHitObject? best = null;
            double bestScore = double.MaxValue;

            foreach (var h in ruleset.Playfield.HitObjectContainer.AliveObjects.OfType<DrawableOsuHitObject>())
            {
                if (h.IsHit)
                    continue;

                DrawableHitCircle? circle = h switch
                {
                    DrawableHitCircle c => c,
                    DrawableSlider s when !s.HeadCircle.IsHit => s.HeadCircle,
                    _ => null
                };

                if (circle?.HitObject.HitWindows == null)
                    continue;

                double start = circle.HitObject.StartTime;
                double meh = circle.HitObject.HitWindows.WindowFor(HitResult.Meh);
                if (time > start + meh)
                    continue;

                // Strongly prefer the nearest upcoming note; ignore far-future objects
                double delta = start - time;
                if (delta > MaxEarlyMs.Value + 30)
                    continue;

                double score = Math.Abs(delta);
                if (delta < 0)
                    score += 2; // slight penalty for already-late targets

                if (score < bestScore)
                {
                    bestScore = score;
                    best = circle;
                }
            }

            return best;
        }

        private double estimateStreamGap(double time)
        {
            var starts = ruleset.Objects
                .Where(o => o is HitCircle or Slider)
                .Select(o => o.StartTime)
                .Where(t => t >= time - 30)
                .OrderBy(t => t)
                .Take(6)
                .ToList();

            if (starts.Count < 2)
                return 0;

            var gaps = new List<double>();
            for (int i = 1; i < starts.Count; i++)
            {
                double g = starts[i] - starts[i - 1];
                if (g > 12 && g < 160)
                    gaps.Add(g);
            }

            if (gaps.Count == 0)
                return 0;

            gaps.Sort();
            return gaps[gaps.Count / 2];
        }

        private partial class InputInterceptor : Component, IKeyBindingHandler<OsuAction>
        {
            private readonly OsuModApexBuffer mod;

            public InputInterceptor(OsuModApexBuffer mod) => this.mod = mod;

            public bool OnPressed(KeyBindingPressEvent<OsuAction> e)
                => mod.tryInterceptPress(e.Action);

            public void OnReleased(KeyBindingReleaseEvent<OsuAction> e)
            {
            }
        }
    }
}
