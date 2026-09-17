// RealismOverhaul - what the mod itself costs on the main thread, measured with a stopwatch around every module call of Mod.OnUpdate.
//  Nothing of the game is measured or changed: only the mod's own frames are timed, so a slow module can be found from a log.
//  Per module: calls, total time, longest call, calls over 1 ms. Per frame: total mod time, longest frame (with the module that took
//  most of it and when it happened), frames over 2 ms and over 4 ms. Plus the managed collections and the bytes allocated in the window.
//  One compact "[PERF] bilan" line every 2 minutes of a mission and one at the end of the battle (kept by the PUBLIC log filter).
//  Cost: two stopwatch reads and one dictionary lookup per module call, about a microsecond per frame.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RealismOverhaul
{
    static class Perf
    {
        const float BilanEvery = 120f;

        sealed class Slot
        {
            internal string Name;
            internal long Total, Max;
            internal int Calls, Over1;
        }

        static readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
        static readonly List<Slot> _order = new();
        static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        static readonly long T1 = Math.Max(1, Stopwatch.Frequency / 1000);          // 1 ms in stopwatch ticks
        static readonly long T2 = T1 * 2, T4 = T1 * 4;

        static long _frameTicks, _framePart, _sum, _max;
        static string _framePartName, _maxName;
        static float _maxAt, _next, _windowStart, _lastAt, _battleStart = -1f;
        static int _frames, _over2, _over4;
        static int _gc0, _gc1, _gc2;
        static long _alloc;
        static bool _baseTaken, _ended;

        static string Ms(double ms) => ms.ToString("0.00", CultureInfo.InvariantCulture);

        /// Runs one module call under the exception guard and adds its time to the current frame.
        internal static void Run(string what, Action act)
        {
            long t0 = Stopwatch.GetTimestamp();
            Guard.Run(what, act);
            Add(what, Stopwatch.GetTimestamp() - t0);
        }

        static void Add(string what, long ticks)
        {
            if (ticks < 0) return;
            if (!_slots.TryGetValue(what, out var s)) { _slots[what] = s = new Slot { Name = what }; _order.Add(s); }
            s.Calls++;
            s.Total += ticks;
            if (ticks > s.Max) s.Max = ticks;
            if (ticks >= T1) s.Over1++;
            _frameTicks += ticks;
            if (ticks > _framePart) { _framePart = ticks; _framePartName = what; }
        }

        /// End of one Mod.OnUpdate: closes the frame and writes the periodic line. Only the frames of a mission are counted.
        internal static void EndFrame(float now, bool mission)
        {
            long t = _frameTicks;
            _frameTicks = 0; _framePart = 0;
            string part = _framePartName; _framePartName = null;
            if (!mission) return;
            _frames++;
            _sum += t;
            if (t >= T2) _over2++;
            if (t >= T4) _over4++;
            if (t > _max) { _max = t; _maxName = part; _maxAt = _battleStart >= 0f ? now - _battleStart : -1f; }
            if (!_baseTaken) { _baseTaken = true; Base(); }
            // frames are only counted inside a mission: a long stay in the menus starts a new window instead of stretching this one
            bool gap = _lastAt > 0f && now - _lastAt > 5f;
            _lastAt = now;
            if (_next <= 0f || gap) { Clear(now); return; }
            if (now < _next) return;
            _next = now + BilanEvery;
            Bilan(now, false);
        }

        /// End of the battle (end screen or game closing): the battle gets its line now, once.
        internal static void EndBattle()
        {
            if (_ended) return;
            _ended = true;
            if (_frames > 0) Bilan(UnityEngine.Time.realtimeSinceStartup, true);
        }

        /// A new battle: the counters start again (a battle the end screen never closed gets its line here).
        internal static void ResetSession()
        {
            EndBattle();
            _ended = false;
            float now = UnityEngine.Time.realtimeSinceStartup;
            Clear(now);                    // the window starts with the battle: the time spent in the menus stays out of the rates
            _battleStart = now;
            _lastAt = 0f;
        }

        static void Base()
        {
            try
            {
                _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
                _alloc = GC.GetTotalAllocatedBytes(false);
            }
            catch { }
        }

        static void Bilan(float now, bool end)
        {
            float span = Math.Max(0.001f, now - _windowStart);
            int frames = Math.Max(1, _frames);
            var sb = new StringBuilder(256);
            sb.Append("[PERF] bilan ").Append(end ? "de la bataille" : "sur " + span.ToString("0", CultureInfo.InvariantCulture) + " s")
              .Append(" : ").Append(frames.ToString(CultureInfo.InvariantCulture)).Append(" images, mod ")
              .Append(Ms(_sum * MsPerTick / frames)).Append(" ms par image en moyenne, la plus longue ").Append(Ms(_max * MsPerTick));
            if (_maxName != null) sb.Append(" (").Append(_maxName).Append(_maxAt >= 0f ? ", à " + _maxAt.ToString("0", CultureInfo.InvariantCulture) + " s de bataille" : "").Append(')');
            sb.Append(" ; images au-dessus de 2 ms ").Append(_over2.ToString(CultureInfo.InvariantCulture))
              .Append(", au-dessus de 4 ms ").Append(_over4.ToString(CultureInfo.InvariantCulture));
            try
            {
                int g0 = GC.CollectionCount(0) - _gc0, g1 = GC.CollectionCount(1) - _gc1, g2 = GC.CollectionCount(2) - _gc2;
                double kb = Math.Max(0L, GC.GetTotalAllocatedBytes(false) - _alloc) / 1024.0 / span;
                sb.Append(" ; ").Append(kb.ToString("0", CultureInfo.InvariantCulture)).Append(" ko/s alloués, ramasse-miettes ")
                  .Append(g0.ToString(CultureInfo.InvariantCulture)).Append('/').Append(g1.ToString(CultureInfo.InvariantCulture)).Append('/').Append(g2.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
            _order.Sort(ByMax);
            int n = 0;
            for (int i = 0; i < _order.Count && n < 6; i++)
            {
                var s = _order[i];
                if (s.Calls == 0) continue;
                sb.Append(n == 0 ? " ; les plus longs : " : ", ").Append(s.Name).Append(' ').Append(Ms(s.Max * MsPerTick))
                  .Append(" ms (moyenne ").Append(Ms(s.Total * MsPerTick / s.Calls)).Append(", au-dessus de 1 ms ").Append(s.Over1.ToString(CultureInfo.InvariantCulture)).Append(')');
                n++;
            }
            Mod.Log.Msg(sb.ToString());
            Clear(now);
        }

        static int ByMax(Slot a, Slot b) => b.Max.CompareTo(a.Max);

        static void Clear(float now)
        {
            foreach (var s in _order) { s.Calls = 0; s.Total = 0; s.Max = 0; s.Over1 = 0; }
            _frames = 0; _over2 = 0; _over4 = 0; _sum = 0; _max = 0; _maxName = null; _maxAt = -1f;
            _windowStart = now; _next = now + BilanEvery;
            Base();
        }
    }
}
