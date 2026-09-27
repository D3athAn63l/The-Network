using System;
using System.Diagnostics;
using System.Text;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist.Events;

namespace TheNetwork.Diagnostics
{
    /// <summary>
    /// Spike S18 harness (PERFORMANCE § 7): drives a scratch scheduler and a scratch history ledger
    /// with synthetic load, entirely in memory. It never touches the live save. Usable from a dev
    /// action in-game and from the headless test runner.
    /// </summary>
    public static class PerfHarness
    {
        public sealed class Result
        {
            public int jobs;
            public int records;
            public double scheduleMs;
            public double idleCheckNs;
            public int ticksToDrain;
            public int maxJobsInOneTick;
            public double maxTickMs;
            public double medianTickMs;
            public double p95TickMs;
            public double historyInsertMs;
            public double sweepMs;
            public int recordsAfterSweep;
            public string text;
        }

        public static Result Run(int jobCount, int recordCount)
        {
            Result res = new Result { jobs = jobCount, records = recordCount };
            WarmUp();
            IdAllocator ids = new IdAllocator();
            ManualClock clock = new ManualClock { Now = 1000 };
            NetScheduler sch = new NetScheduler(ids, clock);
            int ran = 0;
            sch.RegisterKind("s18.job", j => { ran++; }, false, false);

            // 1. Schedule N jobs spread over 60 days.
            long t0 = Stopwatch.GetTimestamp();
            NetRng rng = new NetRng(18, "s18");
            for (int i = 0; i < jobCount; i++) sch.Schedule("s18.job", clock.Now + 1 + rng.Range(0, Ticks.PerDay * 60), i + 1);
            res.scheduleMs = Ms(Stopwatch.GetTimestamp() - t0);

            // 2. Idle cost: the per-tick check when nothing is due.
            clock.Now = 1000;
            const int idleIterations = 2000000;
            int sink = 0;
            t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < idleIterations; i++)
            {
                if (clock.Now >= sch.NextDueTick) sink++;
            }
            res.idleCheckNs = Ms(Stopwatch.GetTimestamp() - t0) * 1e6 / idleIterations;
            if (sink > 0) res.idleCheckNs = -1;

            // 3. Worst case: everything overdue at once; the budget spreads it across ticks.
            clock.Now = int.MaxValue / 2;
            int ticks = 0;
            System.Collections.Generic.List<double> tickMs = new System.Collections.Generic.List<double>();
            while (sch.Count > 0 && ticks < 100000)
            {
                long tt = Stopwatch.GetTimestamp();
                int n = sch.RunDue();
                double ms = Ms(Stopwatch.GetTimestamp() - tt);
                tickMs.Add(ms);
                if (n > res.maxJobsInOneTick) res.maxJobsInOneTick = n;
                if (ms > res.maxTickMs) res.maxTickMs = ms;
                ticks++;
                clock.Now++;
            }
            res.ticksToDrain = ticks;
            tickMs.Sort();
            if (tickMs.Count > 0)
            {
                res.medianTickMs = tickMs[tickMs.Count / 2];
                res.p95TickMs = tickMs[Math.Min(tickMs.Count - 1, (int)(tickMs.Count * 0.95))];
            }

            // 4. History: insert N records through the real consumer, then a full retention sweep.
            ActorStore actors = new ActorStore();
            NetworkActor player = new NetworkActor { id = new ActorId(ids.NextId()), kind = ActorKind.PlayerProxy };
            NetworkActor source = new NetworkActor { id = new ActorId(ids.NextId()), kind = ActorKind.Individual };
            actors.Add(player);
            actors.Add(source);
            HistoryLedger ledger = new HistoryLedger();
            SummaryStore summaries = new SummaryStore();
            ManualClock hclock = new ManualClock { Now = 0 };
            HistoryService history = new HistoryService(ledger, summaries, actors, ids, hclock, 18);
            t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < recordCount; i++)
            {
                hclock.Now = i * (Ticks.PerYear * 3 / Math.Max(1, recordCount));
                IntelEvent e = EventFactory.Make<IntelEvent>(EventKeys.IntelConcluded, Importance.Notable, player.id.Ref, source.id.Ref);
                e.seq = ids.NextEventSeq();
                e.tick = hclock.Now;
                e.requester = player.id;
                e.source = source.id;
                e.topicDefName = "S18Item";
                e.topicLabel = "test item";
                e.sourceName = "S18 source";
                history.Handle(e);
            }
            res.historyInsertMs = Ms(Stopwatch.GetTimestamp() - t0);
            t0 = Stopwatch.GetTimestamp();
            bool finished = false;
            int guard = 0;
            while (!finished && guard++ < 1000) history.Sweep(hclock.Now, out finished);
            res.sweepMs = Ms(Stopwatch.GetTimestamp() - t0);
            res.recordsAfterSweep = ledger.records.Count;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] S18 harness (synthetic, in memory; not the live save)");
            sb.AppendLine("  scheduled " + jobCount + " jobs in " + res.scheduleMs.ToString("0.0") + " ms");
            sb.AppendLine("  idle check: " + res.idleCheckNs.ToString("0.00") + " ns per tick (one comparison)");
            sb.AppendLine("  all overdue: drained in " + res.ticksToDrain + " ticks, at most " + res.maxJobsInOneTick + " jobs per tick (budget " + NetScheduler.DefaultBudgetJobs + " jobs / " + NetScheduler.DefaultBudgetMs + " ms)");
            sb.AppendLine("  per busy tick: median " + res.medianTickMs.ToString("0.0000") + " ms, p95 " + res.p95TickMs.ToString("0.0000") + " ms, max " + res.maxTickMs.ToString("0.000") + " ms (the time budget is checked between jobs)");
            sb.AppendLine("  history: " + recordCount + " records inserted in " + res.historyInsertMs.ToString("0.0") + " ms; full sweep " + res.sweepMs.ToString("0.0") + " ms; " + res.recordsAfterSweep + " kept");
            res.text = sb.ToString();
            return res;
        }

        /// <summary>JIT-compiles the paths once so the numbers measure steady state, not first-call compilation.</summary>
        private static void WarmUp()
        {
            ManualClock c = new ManualClock { Now = 0 };
            NetScheduler s = new NetScheduler(new IdAllocator(), c);
            s.RegisterKind("s18.job", j => { }, false, false);
            for (int i = 0; i < 64; i++) s.Schedule("s18.job", 1 + i, i + 1);
            c.Now = 1000;
            while (s.Count > 0) s.RunDue();
        }

        private static double Ms(long stopwatchTicks)
        {
            return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
        }
    }
}
