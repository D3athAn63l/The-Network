using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace TheNetwork.Kernel
{
    /// <summary>
    /// Optional timing instrumentation (DEBUGGING § 5). Off by default; when off, <see cref="Record"/>
    /// is one bool check. Aggregates count, total, max and a p95 estimate per key (job kind, consumer).
    /// </summary>
    public static class NetProfiler
    {
        private sealed class Stat
        {
            public long count;
            public long totalTicks;
            public long maxTicks;
            public readonly long[] recent = new long[256];
            public int recentCount;
            public int recentNext;
        }

        public static bool Enabled;
        public const double WarnSingleMs = 5.0;

        private static readonly Dictionary<string, Stat> stats = new Dictionary<string, Stat>();

        public static void Record(string key, long elapsedStopwatchTicks)
        {
            if (!Enabled) return;
            Stat s;
            if (!stats.TryGetValue(key, out s))
            {
                s = new Stat();
                stats[key] = s;
            }
            s.count++;
            s.totalTicks += elapsedStopwatchTicks;
            if (elapsedStopwatchTicks > s.maxTicks) s.maxTicks = elapsedStopwatchTicks;
            s.recent[s.recentNext] = elapsedStopwatchTicks;
            s.recentNext = (s.recentNext + 1) % s.recent.Length;
            if (s.recentCount < s.recent.Length) s.recentCount++;
            double ms = ToMs(elapsedStopwatchTicks);
            if (ms > WarnSingleMs)
            {
                NetLog.WarnOnce(LogCategory.Kernel, "slow:" + key, "Slow Network work '" + key + "': " + ms.ToString("0.00") + " ms (profiling).");
            }
        }

        public static double ToMs(long stopwatchTicks)
        {
            return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
        }

        public static void Reset()
        {
            stats.Clear();
        }

        public static string Report()
        {
            List<KeyValuePair<string, Stat>> rows = new List<KeyValuePair<string, Stat>>(stats);
            rows.Sort((a, b) => b.Value.totalTicks.CompareTo(a.Value.totalTicks));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] Timing report (profiling " + (Enabled ? "on" : "off") + ")");
            sb.AppendLine(string.Format("{0,-40} {1,8} {2,10} {3,9} {4,9} {5,9}", "key", "count", "total ms", "mean ms", "p95 ms", "max ms"));
            for (int i = 0; i < rows.Count; i++)
            {
                Stat s = rows[i].Value;
                long[] copy = new long[s.recentCount];
                System.Array.Copy(s.recent, copy, s.recentCount);
                System.Array.Sort(copy);
                long p95 = copy.Length == 0 ? 0 : copy[System.Math.Min(copy.Length - 1, (int)(copy.Length * 0.95))];
                sb.AppendLine(string.Format("{0,-40} {1,8} {2,10:0.000} {3,9:0.0000} {4,9:0.0000} {5,9:0.0000}",
                    rows[i].Key, s.count, ToMs(s.totalTicks), s.count == 0 ? 0 : ToMs(s.totalTicks) / s.count, ToMs(p95), ToMs(s.maxTicks)));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// A global counter bumped by every mutation. Read models rebuild only when it changes
    /// (ARCHITECTURE § 9).
    /// </summary>
    public static class StateVersion
    {
        public static int Current { get; private set; }

        public static void Bump()
        {
            unchecked { Current++; }
        }
    }
}
