using System;
using System.Collections.Generic;
using System.Diagnostics;
using Verse;

namespace TheNetwork.Kernel
{
    /// <summary>A persisted unit of deferred work (DATA_MODEL § 15, SIMULATION § 1.1).</summary>
    public sealed class ScheduledJob : IExposable
    {
        public long seq;
        public int dueTick;
        public string kind;
        public int target;
        public int arg;
        public int createdTick;
        public int attempts;

        // Runtime only.
        internal bool cancelled;
        internal int heapIndex = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref seq, "seq", 0L);
            Scribe_Values.Look(ref dueTick, "due", 0);
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref target, "target", 0);
            Scribe_Values.Look(ref arg, "arg", 0);
            Scribe_Values.Look(ref createdTick, "created", 0);
            Scribe_Values.Look(ref attempts, "attempts", 0);
        }

        public override string ToString()
        {
            return kind + "(" + target + (arg != 0 ? "," + arg : "") + ")@" + dueTick + "#" + seq;
        }
    }

    public sealed class SchedulerState : IExposable
    {
        public List<ScheduledJob> jobs = new List<ScheduledJob>();

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref jobs, "jobs", "scheduler");
        }
    }

    public delegate void JobHandler(ScheduledJob job);

    /// <summary>
    /// Due-tick job queue with a per-tick budget (SIMULATION § 1, ADR-022).
    ///
    /// The idle cost is one integer comparison (<see cref="NextDueTick"/>). Entities store their own
    /// next due tick; the persisted job list is an index that validation can rebuild. Periodic kinds
    /// are singletons per (kind, target): scheduling again replaces the existing job.
    /// </summary>
    public sealed class NetScheduler
    {
        private sealed class KindInfo
        {
            public JobHandler handler;
            public bool singleton;
            public bool retryable;
        }

        public const int DefaultBudgetJobs = 16;
        public const double DefaultBudgetMs = 1.5;
        public const int MaxAttempts = 3;
        public const int RetryBackoffTicks = Ticks.PerHour;

        private readonly IdAllocator ids;
        private readonly IClock clock;
        private readonly Dictionary<string, KindInfo> kinds = new Dictionary<string, KindInfo>();
        private readonly Dictionary<long, ScheduledJob> bySeq = new Dictionary<long, ScheduledJob>();
        private readonly Dictionary<string, ScheduledJob> singletons = new Dictionary<string, ScheduledJob>();
        private readonly List<ScheduledJob> heap = new List<ScheduledJob>();

        public int BudgetJobs = DefaultBudgetJobs;
        public double BudgetMs = DefaultBudgetMs;

        /// <summary>Called when a job throws and is not retried: the owning service repairs or quarantines.</summary>
        public Action<ScheduledJob, Exception> OnJobFailed;

        public NetScheduler(IdAllocator ids, IClock clock)
        {
            this.ids = ids;
            this.clock = clock;
        }

        public int Count => bySeq.Count;

        /// <summary>The single idle check of the tick path.</summary>
        public int NextDueTick
        {
            get
            {
                while (heap.Count > 0 && heap[0].cancelled) PopMin();
                return heap.Count > 0 ? heap[0].dueTick : int.MaxValue;
            }
        }

        public void RegisterKind(string kind, JobHandler handler, bool singletonPerTarget, bool retryable)
        {
            kinds[kind] = new KindInfo { handler = handler, singleton = singletonPerTarget, retryable = retryable };
        }

        public bool IsKnownKind(string kind)
        {
            return kind != null && kinds.ContainsKey(kind);
        }

        public ScheduledJob Schedule(string kind, int dueTick, int target, int arg = 0)
        {
            KindInfo info;
            if (kinds.TryGetValue(kind, out info) && info.singleton)
            {
                Cancel(kind, target);
            }
            ScheduledJob job = new ScheduledJob
            {
                seq = ids.NextJobSeq(),
                dueTick = dueTick,
                kind = kind,
                target = target,
                arg = arg,
                createdTick = clock.Now
            };
            Add(job);
            return job;
        }

        /// <summary>
        /// Staggered first due tick for periodic per-entity work (SIMULATION § 1.3):
        /// now + (Hash(seed, kind) mod period).
        /// </summary>
        public static int StaggeredDue(int now, int entitySeed, string kind, int period)
        {
            if (period <= 0) return now;
            int h = NetHash.Combine(entitySeed, kind) & 0x7fffffff;
            return now + (h % period);
        }

        public bool Cancel(string kind, int target)
        {
            bool any = false;
            ScheduledJob existing;
            if (singletons.TryGetValue(Key(kind, target), out existing))
            {
                Remove(existing);
                any = true;
            }
            else
            {
                List<ScheduledJob> matches = null;
                foreach (ScheduledJob j in bySeq.Values)
                {
                    if (j.target == target && j.kind == kind)
                    {
                        if (matches == null) matches = new List<ScheduledJob>();
                        matches.Add(j);
                    }
                }
                if (matches != null)
                {
                    for (int i = 0; i < matches.Count; i++) Remove(matches[i]);
                    any = true;
                }
            }
            return any;
        }

        public bool Has(string kind, int target)
        {
            if (singletons.ContainsKey(Key(kind, target))) return true;
            foreach (ScheduledJob j in bySeq.Values)
            {
                if (j.target == target && j.kind == kind) return true;
            }
            return false;
        }

        public ScheduledJob Find(string kind, int target)
        {
            ScheduledJob j;
            if (singletons.TryGetValue(Key(kind, target), out j)) return j;
            foreach (ScheduledJob x in bySeq.Values)
            {
                if (x.target == target && x.kind == kind) return x;
            }
            return null;
        }

        public IEnumerable<ScheduledJob> AllJobs => bySeq.Values;

        /// <summary>
        /// Runs due jobs in (dueTick, seq) order under the job and time budgets. Jobs still due when a
        /// budget runs out stay in the heap and run next tick. Returns the number of jobs run.
        /// </summary>
        public int RunDue()
        {
            int now = clock.Now;
            if (NextDueTick > now) return 0;
            long start = Stopwatch.GetTimestamp();
            double tickToMs = 1000.0 / Stopwatch.Frequency;
            int ran = 0;
            while (ran < BudgetJobs)
            {
                if (NextDueTick > now) break;
                if (ran > 0 && (Stopwatch.GetTimestamp() - start) * tickToMs >= BudgetMs) break;
                ScheduledJob job = PopMin();
                ForgetIndexes(job);
                Execute(job);
                ran++;
            }
            return ran;
        }

        private void Execute(ScheduledJob job)
        {
            KindInfo info;
            if (!kinds.TryGetValue(job.kind ?? "", out info))
            {
                NetLog.WarnOnce(LogCategory.Scheduler, "unknownKind:" + job.kind,
                    "Dropping scheduled job of unknown kind '" + job.kind + "' (removed in this version?).");
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                info.handler(job);
            }
            catch (Exception ex)
            {
                job.attempts++;
                if (info.retryable && job.attempts < MaxAttempts)
                {
                    NetLog.ErrorOnce(LogCategory.Scheduler, "retry:" + job.kind + ":" + job.target,
                        "Job " + job + " failed (attempt " + job.attempts + "), retrying later: " + ex);
                    job.dueTick = clock.Now + RetryBackoffTicks * job.attempts;
                    job.cancelled = false;
                    Add(job);
                }
                else
                {
                    NetLog.ErrorOnce(LogCategory.Scheduler, "fail:" + job.kind + ":" + job.target,
                        "Job " + job + " failed and was removed: " + ex);
                    OnJobFailed?.Invoke(job, ex);
                }
            }
            finally
            {
                NetProfiler.Record("job:" + job.kind, Stopwatch.GetTimestamp() - t0);
            }
        }

        // ------------------------------------------------------------------ persistence

        public void WriteTo(SchedulerState state)
        {
            List<ScheduledJob> list = new List<ScheduledJob>(bySeq.Values);
            list.Sort((a, b) => a.seq.CompareTo(b.seq));
            state.jobs = list;
        }

        /// <summary>Rebuilds the runtime heap and indexes from the loaded job list.</summary>
        public void LoadFrom(SchedulerState state)
        {
            bySeq.Clear();
            singletons.Clear();
            heap.Clear();
            if (state.jobs == null) return;
            for (int i = 0; i < state.jobs.Count; i++)
            {
                ScheduledJob job = state.jobs[i];
                if (job == null || job.kind == null) continue;
                if (bySeq.ContainsKey(job.seq))
                {
                    job.seq = ids.NextJobSeq();
                }
                ids.EnsureJobSeqAbove(job.seq);
                job.cancelled = false;
                Add(job);
            }
        }

        public void Clear()
        {
            bySeq.Clear();
            singletons.Clear();
            heap.Clear();
        }

        // ------------------------------------------------------------------ internals

        private static string Key(string kind, int target)
        {
            return kind + "|" + target;
        }

        private void Add(ScheduledJob job)
        {
            bySeq[job.seq] = job;
            KindInfo info;
            if (kinds.TryGetValue(job.kind, out info) && info.singleton)
            {
                ScheduledJob old;
                string key = Key(job.kind, job.target);
                if (singletons.TryGetValue(key, out old) && old != job)
                {
                    Remove(old);
                }
                singletons[key] = job;
            }
            HeapPush(job);
        }

        private void Remove(ScheduledJob job)
        {
            job.cancelled = true;
            ForgetIndexes(job);
        }

        private void ForgetIndexes(ScheduledJob job)
        {
            bySeq.Remove(job.seq);
            ScheduledJob s;
            string key = Key(job.kind, job.target);
            if (singletons.TryGetValue(key, out s) && s == job) singletons.Remove(key);
        }

        private static bool Less(ScheduledJob a, ScheduledJob b)
        {
            if (a.dueTick != b.dueTick) return a.dueTick < b.dueTick;
            return a.seq < b.seq;
        }

        private void HeapPush(ScheduledJob job)
        {
            heap.Add(job);
            int i = heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (!Less(heap[i], heap[parent])) break;
                Swap(i, parent);
                i = parent;
            }
        }

        private ScheduledJob PopMin()
        {
            ScheduledJob top = heap[0];
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            int i = 0;
            int n = heap.Count;
            while (true)
            {
                int l = 2 * i + 1;
                int r = l + 1;
                int m = i;
                if (l < n && Less(heap[l], heap[m])) m = l;
                if (r < n && Less(heap[r], heap[m])) m = r;
                if (m == i) break;
                Swap(i, m);
                i = m;
            }
            return top;
        }

        private void Swap(int a, int b)
        {
            ScheduledJob t = heap[a];
            heap[a] = heap[b];
            heap[b] = t;
        }
    }
}
