using System;
using System.Collections.Generic;
using System.Reflection;
using TheNetwork.Diagnostics;
using TheNetwork.Kernel;
using TheNetwork.Persist.Events;

namespace TheNetwork.Tests
{
    public static class KernelTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Kernel.TypedIds", TypedIds));
            t.Add(new KeyValuePair<string, Action>("Kernel.IdAllocator", IdAllocatorTest));
            t.Add(new KeyValuePair<string, Action>("Kernel.NetRngDeterminism", NetRngDeterminism));
            t.Add(new KeyValuePair<string, Action>("Kernel.SchedulerOrdering", SchedulerOrdering));
            t.Add(new KeyValuePair<string, Action>("Kernel.SchedulerBudget", SchedulerBudget));
            t.Add(new KeyValuePair<string, Action>("Kernel.SchedulerSingletonAndStagger", SchedulerSingleton));
            t.Add(new KeyValuePair<string, Action>("Kernel.SchedulerRetryAndUnknownKinds", SchedulerRetry));
            t.Add(new KeyValuePair<string, Action>("Kernel.SchedulerPersistRoundTrip", SchedulerPersist));
            t.Add(new KeyValuePair<string, Action>("Kernel.EventBusOrderAndContainment", EventBus));
            t.Add(new KeyValuePair<string, Action>("Kernel.JournalBounds", JournalBounds));
            t.Add(new KeyValuePair<string, Action>("Kernel.S18Harness", S18));
            t.Add(new KeyValuePair<string, Action>("Kernel.NoHarmonyReference", NoHarmony));
        }

        private static void TypedIds()
        {
            ActorId a = new ActorId(17);
            T.Check(a.IsValid, "ActorId 17 valid");
            T.Check(!ActorId.None.IsValid, "ActorId.None invalid");
            T.Eq("A17", a.ToString(), "actor id prints with prefix");
            T.Check(a == new ActorId(17) && a != new ActorId(18), "actor id equality");
            EntityRef r = new OpportunityId(301).Ref;
            T.Eq("O301", r.ToString(), "entity ref prints");
            T.Check(EntityRef.Parse("O301") == r, "entity ref parses back");
            T.Eq(301, EntityRef.Parse("O301").AsOpportunity.Value, "AsOpportunity");
            T.Check(!EntityRef.Parse("Q12").IsValid && !EntityRef.Parse("A0").IsValid && !EntityRef.Parse("").IsValid, "bad refs invalid");
            foreach (EntityKind k in Enum.GetValues(typeof(EntityKind)))
            {
                if (k == EntityKind.None) continue;
                T.Eq(k, EntityKindUtility.FromPrefix(EntityKindUtility.Prefix(k)), "prefix round trip " + k);
            }
        }

        private static void IdAllocatorTest()
        {
            IdAllocator ids = new IdAllocator();
            int a = ids.NextId(), b = ids.NextId();
            T.Check(a >= 1 && b == a + 1, "ids increase from 1");
            T.Check(ids.EnsureAbove(100), "EnsureAbove raises");
            T.Check(ids.NextId() > 100, "next id above repaired max");
            T.Check(!ids.EnsureAbove(5), "EnsureAbove no-op when already above");
        }

        private static void NetRngDeterminism()
        {
            NetRng a = new NetRng(42, "intel.round", 3);
            NetRng b = new NetRng(42, "intel.round", 3);
            bool same = true;
            for (int i = 0; i < 1000; i++) same &= a.NextULong() == b.NextULong();
            T.Check(same, "same seed/stream/index gives the same sequence");
            NetRng c = new NetRng(42, "intel.round", 4);
            NetRng d = new NetRng(42, "opp.quantity", 3);
            NetRng e = new NetRng(42, "intel.round", 3);
            ulong first = e.NextULong();
            T.Check(c.NextULong() != first && d.NextULong() != first, "other index/stream differ");
            NetRng r = new NetRng(7, "range");
            bool inRange = true;
            for (int i = 0; i < 10000; i++)
            {
                int v = r.Range(3, 9);
                float f = r.Range(0.5f, 1.5f);
                inRange &= v >= 3 && v < 9 && f >= 0.5f && f <= 1.5f;
            }
            T.Check(inRange, "ranges respected");
            NetRng w = new NetRng(9, "weights");
            bool neverZero = true;
            for (int i = 0; i < 5000; i++) neverZero &= w.WeightedIndex(new[] { 0f, 1f, 0f, 3f }) % 2 == 1;
            T.Check(neverZero, "zero weights never drawn");
        }

        private static NetScheduler NewScheduler(ManualClock clock, List<string> ran)
        {
            NetScheduler s = new NetScheduler(new IdAllocator(), clock);
            s.RegisterKind("a", j => ran.Add("a" + j.target), false, false);
            s.RegisterKind("b", j => ran.Add("b" + j.target), false, false);
            return s;
        }

        private static void SchedulerOrdering()
        {
            ManualClock clock = new ManualClock { Now = 0 };
            List<string> ran = new List<string>();
            NetScheduler s = NewScheduler(clock, ran);
            s.Schedule("a", 50, 1);
            s.Schedule("b", 10, 2);
            s.Schedule("a", 10, 3);
            s.Schedule("b", 30, 4);
            T.Eq(10, s.NextDueTick, "next due is the minimum");
            clock.Now = 9;
            T.Eq(0, s.RunDue(), "nothing due before its tick");
            clock.Now = 100;
            int guard = 0;
            while (s.Count > 0 && guard++ < 10) s.RunDue(); // the time budget may split this across calls
            T.Eq("b2,a3,b4,a1", string.Join(",", ran.ToArray()), "(dueTick, seq) order");
            T.Eq(int.MaxValue, s.NextDueTick, "empty heap reports MaxValue");
            s.Schedule("a", 200, 5);
            s.Cancel("a", 5);
            T.Eq(int.MaxValue, s.NextDueTick, "cancelled jobs are skipped by the idle check");
        }

        private static void SchedulerBudget()
        {
            ManualClock clock = new ManualClock { Now = 0 };
            List<string> ran = new List<string>();
            NetScheduler s = NewScheduler(clock, ran);
            for (int i = 0; i < 100; i++) s.Schedule("a", 1, i + 1);
            clock.Now = 5;
            int first = s.RunDue();
            T.Eq(NetScheduler.DefaultBudgetJobs, first, "one tick runs at most the job budget");
            int ticks = 1;
            while (s.Count > 0 && ticks < 100)
            {
                clock.Now++;
                s.RunDue();
                ticks++;
            }
            T.Eq(100, ran.Count, "every job eventually runs");
            T.Eq(7, ticks, "100 overdue jobs spread over ceil(100/16) ticks");
        }

        private static void SchedulerSingleton()
        {
            ManualClock clock = new ManualClock { Now = 0 };
            int count = 0;
            NetScheduler s = new NetScheduler(new IdAllocator(), clock);
            s.RegisterKind("periodic", j => count++, true, true);
            s.Schedule("periodic", 100, 7);
            s.Schedule("periodic", 50, 7);
            s.Schedule("periodic", 70, 8);
            T.Eq(2, s.Count, "singleton per (kind, target)");
            T.Eq(50, s.Find("periodic", 7).dueTick, "rescheduling replaces");
            clock.Now = 1000;
            s.RunDue();
            T.Eq(2, count, "replaced job does not run");
            int d1 = NetScheduler.StaggeredDue(0, 1234, "upkeep", 60000);
            int d2 = NetScheduler.StaggeredDue(0, 1234, "upkeep", 60000);
            int d3 = NetScheduler.StaggeredDue(0, 1235, "upkeep", 60000);
            T.Check(d1 == d2 && d1 >= 0 && d1 < 60000, "stagger is deterministic and within the period");
            T.Check(d1 != d3, "different entities get different phases");
        }

        private static void SchedulerRetry()
        {
            ManualClock clock = new ManualClock { Now = 0 };
            int attempts = 0;
            bool failedCalled = false;
            NetScheduler s = new NetScheduler(new IdAllocator(), clock);
            s.RegisterKind("flaky", j => { attempts++; throw new InvalidOperationException("boom"); }, true, true);
            s.RegisterKind("once", j => { throw new InvalidOperationException("once"); }, true, false);
            s.OnJobFailed = (j, ex) => failedCalled = true;
            s.Schedule("flaky", 1, 1);
            for (int i = 0; i < 10; i++)
            {
                clock.Now = s.NextDueTick == int.MaxValue ? clock.Now : s.NextDueTick;
                s.RunDue();
            }
            T.Eq(NetScheduler.MaxAttempts, attempts, "retryable kind retried up to the max");
            T.Check(failedCalled, "OnJobFailed after the last attempt");
            failedCalled = false;
            s.Schedule("once", clock.Now + 1, 2);
            clock.Now += 1;
            s.RunDue();
            T.Check(failedCalled && s.Count == 0, "non-retryable kind fails once and is removed");
            // Unknown kinds (from a newer version) are dropped with one warning, never crash.
            SchedulerState state = new SchedulerState();
            state.jobs.Add(new ScheduledJob { seq = 5, dueTick = clock.Now, kind = "removed.kind", target = 1 });
            s.LoadFrom(state);
            s.RunDue();
            T.Eq(0, s.Count, "unknown kind dropped");
        }

        private static void SchedulerPersist()
        {
            ManualClock clock = new ManualClock { Now = 0 };
            IdAllocator ids = new IdAllocator();
            NetScheduler s = new NetScheduler(ids, clock);
            s.RegisterKind("a", j => { }, true, true);
            s.Schedule("a", 30, 1);
            s.Schedule("a", 10, 2);
            s.Schedule("a", 20, 3, 9);
            SchedulerState state = new SchedulerState();
            s.WriteTo(state);
            T.Eq(3, state.jobs.Count, "persisted every job");
            NetScheduler s2 = new NetScheduler(ids, clock);
            s2.RegisterKind("a", j => { }, true, true);
            s2.LoadFrom(state);
            T.Eq(10, s2.NextDueTick, "heap rebuilt");
            T.Eq(9, s2.Find("a", 3).arg, "args kept");
            T.Check(ids.PeekNextJobSeq > state.jobs[2].seq, "job seq counter stays above loaded jobs");
        }

        private sealed class Consumer : IEventConsumer
        {
            public readonly string name;
            public readonly List<string> log;
            public bool throws;
            public NetworkEventBus republishTo;

            public Consumer(string name, List<string> log) { this.name = name; this.log = log; }
            public string Name => name;

            public void Handle(NetworkEvent evt)
            {
                log.Add(name + ":" + evt.typeKey);
                if (throws) throw new InvalidOperationException("consumer failure");
                if (republishTo != null && evt.typeKey == "Test.Root") republishTo.Publish(EventFactory.Make<SystemEvent>("Test.Child", Importance.Minor));
            }
        }

        private static void EventBus()
        {
            ManualClock clock = new ManualClock { Now = 5 };
            IdAllocator ids = new IdAllocator();
            EventJournal journal = new EventJournal();
            DiagnosticsState diag = new DiagnosticsState();
            NetworkEventBus bus = new NetworkEventBus(ids, clock, journal, diag);
            List<string> log = new List<string>();
            Consumer late = new Consumer("presentation", log);
            Consumer failing = new Consumer("history", log) { throws = true };
            Consumer early = new Consumer("owners", log) { republishTo = bus };
            bus.Register(ConsumerOrder.Presentation, late);
            bus.Register(ConsumerOrder.History, failing);
            bus.Register(ConsumerOrder.DomainOwners, early);
            bus.Publish(EventFactory.Make<SystemEvent>("Test.Root", Importance.Minor));
            T.Eq("owners:Test.Root,history:Test.Root,presentation:Test.Root,owners:Test.Child,history:Test.Child,presentation:Test.Child", string.Join(",", log.ToArray()),
                "fixed global order; cascade breadth-first after the root");
            T.Eq(2, diag.failedConsumers.Count, "failing consumer recorded per event, later consumers still ran");
            T.Check(journal.entries[0].hadErrors, "event flagged hadErrors");
            T.Eq(2, journal.entries.Count, "both events journaled (never replayed)");
            T.Check(journal.entries[0].seq < journal.entries[1].seq, "sequence numbers ordered");
        }

        private static void JournalBounds()
        {
            EventJournal j = new EventJournal();
            for (int i = 0; i < 2000; i++)
            {
                SystemEvent e = EventFactory.Make<SystemEvent>("Test", Importance.Minor);
                e.seq = i;
                e.tick = i * 10;
                j.Append(e, e.tick);
            }
            T.Check(j.entries.Count <= EventJournal.HardCap, "journal never exceeds its hard cap");
            T.Check(j.entries.Count >= EventJournal.KeepCount, "journal keeps at least the recent count");
            T.Check(j.droppedCount > 0, "dropped entries counted");
            EventJournal old = new EventJournal();
            for (int i = 0; i < 300; i++)
            {
                SystemEvent e = EventFactory.Make<SystemEvent>("Test", Importance.Minor);
                e.tick = i * Ticks.PerDay;
                old.Append(e, e.tick);
            }
            T.Eq(EventJournal.KeepCount, old.entries.Count, "old entries beyond the recent count are pruned");
        }

        private static void S18()
        {
            PerfHarness.Result r = PerfHarness.Run(10000, 5000);
            Console.WriteLine(r.text.TrimEnd());
            T.Check(r.maxJobsInOneTick <= NetScheduler.DefaultBudgetJobs, "no tick exceeds the job budget");
            T.Check(r.ticksToDrain >= 10000 / NetScheduler.DefaultBudgetJobs, "overdue jobs spread across ticks");
            T.Check(r.idleCheckNs >= 0 && r.idleCheckNs < 1000, "idle check is one comparison (well under a microsecond)");
            T.Check(r.recordsAfterSweep < 5000, "retention sweep prunes old Notable records");
        }

        private static void NoHarmony()
        {
            Assembly net = typeof(NetScheduler).Assembly;
            foreach (AssemblyName n in net.GetReferencedAssemblies())
            {
                T.Check(n.Name.IndexOf("Harmony", StringComparison.OrdinalIgnoreCase) < 0, "TheNetwork.dll references " + n.Name);
            }
            T.Check(net.GetReferencedAssemblies().Length > 0, "referenced assemblies listed");
        }
    }
}
