using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist.Events;

namespace TheNetwork.Tests
{
    public static class HistoryTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("History.RetentionPolicyTable", PolicyTable));
            t.Add(new KeyValuePair<string, Action>("History.SweepKeepsRecentPerPrincipal", SweepPerPrincipal));
            t.Add(new KeyValuePair<string, Action>("History.SweepBudgetAndCursor", SweepBudget));
            t.Add(new KeyValuePair<string, Action>("History.SummariesDecayLazily", SummaryDecay));
        }

        private static void PolicyTable()
        {
            T.Check(RetentionPolicy.ShouldKeep(Importance.Notable, Ticks.PerYear - 1, false, false), "Notable younger than a year kept");
            T.Check(!RetentionPolicy.ShouldKeep(Importance.Notable, Ticks.PerYear + 1, false, true), "old Notable not among a principal's recent 25 dropped");
            T.Check(RetentionPolicy.ShouldKeep(Importance.Notable, Ticks.PerYear * 3, true, false), "old Notable kept while among a principal's 25 most recent");
            T.Check(RetentionPolicy.ShouldKeep(Importance.Major, Ticks.PerYear * 10, false, true), "Major kept while a participant is active");
            T.Check(RetentionPolicy.ShouldKeep(Importance.Major, Ticks.PerYear * 4, false, false), "Major kept for five years");
            T.Check(!RetentionPolicy.ShouldKeep(Importance.Major, Ticks.PerYear * 6, false, false), "old Major with no active participant dropped");
            T.Check(RetentionPolicy.ShouldKeep(Importance.Legendary, int.MaxValue, false, false), "Legendary forever");
            T.Check(!RetentionPolicy.ShouldKeep(Importance.Minor, 0, true, true), "Minor never becomes a record");
        }

        private static HistoryService NewHistory(out ManualClock clock, out ActorId player, out ActorId sourceA, out ActorId sourceB, out HistoryLedger ledger, out IdAllocator ids)
        {
            ids = new IdAllocator();
            clock = new ManualClock { Now = 0 };
            ActorStore actors = new ActorStore();
            NetworkActor p = new NetworkActor { id = new ActorId(ids.NextId()), kind = ActorKind.PlayerProxy };
            NetworkActor a = new NetworkActor { id = new ActorId(ids.NextId()), kind = ActorKind.Individual };
            NetworkActor b = new NetworkActor { id = new ActorId(ids.NextId()), kind = ActorKind.Individual };
            actors.Add(p); actors.Add(a); actors.Add(b);
            player = p.id; sourceA = a.id; sourceB = b.id;
            ledger = new HistoryLedger();
            return new HistoryService(ledger, new SummaryStore(), actors, ids, clock, 1);
        }

        private static void Emit(HistoryService h, IdAllocator ids, int tick, ActorId player, ActorId source)
        {
            IntelEvent e = EventFactory.Make<IntelEvent>(EventKeys.IntelConcluded, Importance.Notable);
            e.seq = ids.NextEventSeq();
            e.tick = tick;
            e.requester = player;
            e.source = source;
            e.topicLabel = "thing";
            h.Handle(e);
        }

        private static void SweepPerPrincipal()
        {
            ManualClock clock; ActorId player, a, b; HistoryLedger ledger; IdAllocator ids;
            HistoryService h = NewHistory(out clock, out player, out a, out b, out ledger, out ids);
            // 5 old records with source B, then 40 with source A (the player is principal in all).
            for (int i = 0; i < 5; i++) Emit(h, ids, i, player, b);
            for (int i = 0; i < 40; i++) Emit(h, ids, 100 + i, player, a);
            bool finished;
            int dropped = h.Sweep(Ticks.PerYear * 2, out finished);
            T.Check(finished, "sweep finished in one run");
            // Kept: the player's 25 most recent (all with A) ∪ A's 25 most recent (same set) ∪ B's 5 (B's own recent).
            T.Eq(25 + 5, ledger.records.Count, "records kept by any principal's recent 25 survive (" + dropped + " dropped)");
            T.Check(ledger.records.TrueForAll(r => r.Involves(b, true) || r.tick >= 100 + 15), "the survivors are the principals' most recent");
        }

        private static void SweepBudget()
        {
            ManualClock clock; ActorId player, a, b; HistoryLedger ledger; IdAllocator ids;
            HistoryService h = NewHistory(out clock, out player, out a, out b, out ledger, out ids);
            for (int i = 0; i < 1200; i++) Emit(h, ids, i, player, (i % 2 == 0) ? a : b);
            bool finished;
            h.Sweep(Ticks.PerYear * 2, out finished);
            T.Check(!finished && ledger.sweepCursor > 0, "a run examines at most the budget and remembers where it stopped");
            int guard = 0;
            while (!finished && guard++ < 10) h.Sweep(Ticks.PerYear * 2, out finished);
            T.Check(finished && ledger.sweepCursor == 0, "later runs continue from the cursor and finish");
            T.Check(ledger.records.Count <= 75, "only the principals' recent records remain (" + ledger.records.Count + ")");
            T.Check(ledger.droppedCount > 1000, "dropped count kept");
        }

        private static void SummaryDecay()
        {
            ActorRecordSummary s = new ActorRecordSummary { actor = new ActorId(1) };
            s.Add("intel.leads", 4, 0);
            T.Eq(4, s.Lifetime("intel.leads"), "lifetime counter");
            float after = s.Recent("intel.leads", Ticks.PerYear);
            T.Check(Math.Abs(after - 2f) < 0.01f, "recent counter halves after a year (" + after + ")");
            s.Add("intel.leads", 1, Ticks.PerYear);
            T.Check(Math.Abs(s.Recent("intel.leads", Ticks.PerYear) - 3f) < 0.01f, "decay applied on write");
            T.Eq(5, s.Lifetime("intel.leads"), "lifetime never decays");
        }
    }
}
