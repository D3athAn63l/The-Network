using System;
using System.Collections.Generic;
using System.Reflection;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Operations;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Tests
{
    /// <summary>
    /// Phase 2.5 Field Log (SPATIAL § 8): a temporary, contract-scoped activity journal that exists only
    /// while a contractor works a player-issued contract. Test matrix A–N.
    /// </summary>
    public static class FieldLogTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("FieldLog.A_B_NoLogBeforeAcceptance", BeforeAcceptance));
            t.Add(new KeyValuePair<string, Action>("FieldLog.C_D_E_StartsAndReportsBeatsNotDays", Beats));
            t.Add(new KeyValuePair<string, Action>("FieldLog.F_DelayOnce", DelayOnce));
            t.Add(new KeyValuePair<string, Action>("FieldLog.G_WorseThanExpected", WorseThanExpected));
            t.Add(new KeyValuePair<string, Action>("FieldLog.H_PartialQuantity", Partial));
            t.Add(new KeyValuePair<string, Action>("FieldLog.I_Missing", Missing));
            t.Add(new KeyValuePair<string, Action>("FieldLog.J_SaveLoadExactNoDuplicates", SaveLoad));
            t.Add(new KeyValuePair<string, Action>("FieldLog.K_L_EndsWithContractFreshNextTime", EndsAndFresh));
            t.Add(new KeyValuePair<string, Action>("FieldLog.M_NonPlayerIssuerHasNone", NonPlayer));
            t.Add(new KeyValuePair<string, Action>("FieldLog.N_OnlyThePlayersContract", OnlyOne));
            t.Add(new KeyValuePair<string, Action>("FieldLog.NoCoordinatesNoPermanentJournal", NoCoordinates));
        }

        private static int Count(Contract c, string key)
        {
            int n = 0;
            foreach (FieldLogEntry e in c.fieldLog) if (e.key == key) n++;
            return n;
        }

        private static string Keys(Contract c)
        {
            List<string> k = new List<string>();
            foreach (FieldLogEntry e in c.fieldLog) k.Add(e.key);
            return string.Join(",", k.ToArray());
        }

        private static void NoConsecutiveDuplicates(Contract c, string label)
        {
            for (int i = 1; i < c.fieldLog.Count; i++) T.Check(!c.fieldLog[i].SameAs(c.fieldLog[i - 1]), label + ": no line repeats the one before (" + Keys(c) + ")");
        }

        private static void BeforeAcceptance()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            n.Advance(Ticks.PerDay * 3);
            foreach (Contract x in n.ctx.contracts.contracts) T.Eq(0, x.fieldLog.Count, "A: a contractor not hired has no Field Log anywhere");
            Contract c = ProcurementTests.Post(n, fixer, "TestSteel", 150, team);
            Offer o = ProcurementTests.Bid(n, c);
            T.Check(o != null, "a quote exists");
            T.Eq(0, c.fieldLog.Count, "B: a quote not yet accepted has no Field Log");
            T.Eq(0, n.ctx.FieldLog.Visible(c).Count, "B: nothing visible");
        }

        private static void Beats()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor team = ProcurementTests.Reliable(n);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
            T.Eq(1, c.fieldLog.Count, "C: accepting the quote starts the Field Log");
            T.Eq(FieldLogKeys.Accepted, c.fieldLog[0].key, "C: with the acceptance");
            T.Eq(team.name.Display, c.fieldLog[0].args[0], "C: naming the contractor from its snapshot");
            Operation op = ProcurementTests.Op(n, c);
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Prep).done || c.IsTerminal);
            T.Check(Count(c, FieldLogKeys.SetOut) + Count(c, FieldLogKeys.WorkingNearby) == 1, "D: the operation beginning is one entry (" + Keys(c) + ")");
            int beforeWait = c.fieldLog.Count;
            n.AdvanceTo(op.Find(Checkpoint.Arrive).dueTick - Ticks.PerHour);
            T.Eq(beforeWait, c.fieldLog.Count, "E: days of travel write nothing (no per-day spam)");
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Arrive).done || c.IsTerminal);
            T.Eq(1, Count(c, FieldLogKeys.Arrived), "reaching the area is one entry");
            int engaged = c.fieldLog.Count;
            n.AdvanceTo(op.Find(Checkpoint.Resolve).dueTick - Ticks.PerHour);
            T.Eq(engaged, c.fieldLog.Count, "E: work under way writes nothing more by itself");
            T.Check(c.fieldLog.Count <= 4, "a handful of beats, not a diary (" + Keys(c) + ")");
            NoConsecutiveDuplicates(c, "beats");
        }

        private static void DelayOnce()
        {
            TestNet n = ProcurementTests.World(0);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), ProcurementTests.Reliable(n));
            ProcurementDevOverrides.forceBand = OutcomeBand.Success;
            ProcurementDevOverrides.forceDelayTicks = 3 * Ticks.PerDay;
            ProcurementTests.RunUntil(n, () => c.status == ContractStatus.Delayed || c.IsTerminal);
            T.Eq(ContractStatus.Delayed, c.status, "delayed");
            T.Eq(1, Count(c, FieldLogKeys.Delayed), "F: one delay entry");
            n.Advance(Ticks.PerDay * 2);
            T.Eq(1, Count(c, FieldLogKeys.Delayed), "F: still one, days later");
        }

        private static void WorseThanExpected()
        {
            TestNet n = ProcurementTests.World(0);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), ProcurementTests.Reliable(n), "TestSteel", 200);
            ProcurementDevOverrides.forceWorseThanExpected = true;
            ProcurementTests.RunUntil(n, () => c.status == ContractStatus.Renegotiating || c.IsTerminal);
            T.Eq(1, Count(c, FieldLogKeys.WorseThanExpected), "G: one report of a much harder job");
            FieldLogEntry e = c.fieldLog[c.fieldLog.Count - 1];
            T.Eq(c.renegotiation.extraSilver.ToString(), e.args[1], "with the silver asked");
            T.Check(n.ctx.Procurement.RespondWorse(c.id, WorseChoice.PayMore).ok, "pay more");
            T.Eq(1, Count(c, FieldLogKeys.PaidMore), "the answer is logged once");
        }

        private static void Partial()
        {
            TestNet n = ProcurementTests.World(0);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), ProcurementTests.Reliable(n), "TestSteel", 200);
            ProcurementDevOverrides.forceBand = OutcomeBand.Partial;
            ProcurementTests.RunUntil(n, () => c.status == ContractStatus.Renegotiating || c.IsTerminal);
            int secured = c.Acquire.secured;
            T.Eq(1, Count(c, FieldLogKeys.SecuredPart), "H: one partial-result report");
            FieldLogEntry e = null;
            foreach (FieldLogEntry x in c.fieldLog) if (x.key == FieldLogKeys.SecuredPart) e = x;
            T.Check(e != null && e.args[1] == secured.ToString() && e.args[2] == "200" && e.args[3] == c.ItemLabel, "H: the secured and requested quantities, as the player sees them (" + (e == null ? "-" : string.Join("/", e.args.ToArray())) + ")");
            T.Check(n.ctx.Procurement.RespondPartial(c.id, PartialChoice.AcceptPartial).ok, "accept");
            // Delivery may complete at once (the contract closes and the live log with it); otherwise it records the choice.
            T.Check(c.IsTerminal || Count(c, FieldLogKeys.PartialAccepted) == 1, "the choice is logged while the job runs");
        }

        private static void Missing()
        {
            TestNet n = ProcurementTests.World(0);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), ProcurementTests.Reliable(n));
            ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
            ProcurementDevOverrides.forceTroubled = SubStatus.Missing;
            ProcurementTests.RunUntil(n, () => c.status == ContractStatus.Troubled || c.IsTerminal);
            T.Eq(1, Count(c, FieldLogKeys.Missing), "I: contact lost, one entry");
            T.Eq(0, Count(c, FieldLogKeys.SecuredAll) + Count(c, FieldLogKeys.SecuredPart), "no result is reported before the contractor reports it (ADR-037)");
        }

        private static void SaveLoad()
        {
            TestNet n = ProcurementTests.World(0);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), ProcurementTests.Reliable(n));
            Operation op = ProcurementTests.Op(n, c);
            ProcurementDevOverrides.forceBand = OutcomeBand.Success;
            ProcurementDevOverrides.forceDelayTicks = 2 * Ticks.PerDay;
            ProcurementTests.RunUntil(n, () => c.status == ContractStatus.Delayed || c.IsTerminal);
            List<FieldLogEntry> before = new List<FieldLogEntry>(c.fieldLog);
            T.Check(before.Count >= 3, "a few entries (" + Keys(c) + ")");
            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            Contract lc = n.ctx.contracts.Get(c.id);
            T.Eq(before.Count, lc.fieldLog.Count, "J: the same number of entries after loading");
            bool exact = true;
            for (int i = 0; i < before.Count && i < lc.fieldLog.Count; i++) if (!before[i].SameAs(lc.fieldLog[i]) || before[i].tick != lc.fieldLog[i].tick) exact = false;
            T.Check(exact, "J: every entry exactly (tick, key, words)");
            n.Advance(Ticks.PerDay / 2);
            T.Eq(1, Count(lc, FieldLogKeys.Accepted), "J: nothing is written again by loading");
            T.Eq(1, Count(lc, FieldLogKeys.Delayed), "J: no duplicate report");
            NoConsecutiveDuplicates(lc, "after load");
        }

        private static void EndsAndFresh()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            Contract first = ProcurementTests.Awarded(n, fixer, team);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementTests.RunUntil(n, () => first.IsTerminal);
            T.Eq(ContractStatus.Fulfilled, first.status, "done");
            T.Eq(0, n.ctx.FieldLog.Visible(first).Count, "K: a terminal contract shows no live Field Log");
            T.Eq(0, first.fieldLog.Count, "K: and keeps none (History and letters keep the record)");
            Contract second = ProcurementTests.Awarded(n, fixer, team);
            T.Eq(1, second.fieldLog.Count, "L: the same contractor hired again: a new contract, a new log");
            T.Eq(FieldLogKeys.Accepted, second.fieldLog[0].key, "L: starting from the acceptance");
            T.Eq(0, first.fieldLog.Count, "L: the old log does not bleed into it");
        }

        private static void NonPlayer()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            NetworkActor someoneElse = ProcurementTests.Reliable(n);
            Contract c = ProcurementTests.Post(n, fixer, "TestSteel", 150, team);
            Offer o = ProcurementTests.Bid(n, c);
            // A hypothetical contract issued by someone else (Phase 4): the player must never see its log.
            c.parties.issuer = someoneElse.id;
            T.Check(o != null && n.ctx.Procurement.Accept(o.id, false).ok, "accepted and under way");
            ProcurementTests.RunUntil(n, () => ProcurementTests.Op(n, c).Find(Checkpoint.Arrive).done || c.IsTerminal);
            T.Eq(0, c.fieldLog.Count, "M: no Field Log is written for a non-player issuer");
            T.Eq(0, n.ctx.FieldLog.Visible(c).Count, "M: and none is visible");
        }

        private static void OnlyOne()
        {
            TestNet n = ProcurementTests.World(100);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), ProcurementTests.Reliable(n));
            n.Advance(Ticks.PerDay * 2);
            int withLog = 0;
            foreach (Contract x in n.ctx.contracts.contracts) if (x.fieldLog.Count > 0) withLog++;
            T.Eq(1, withLog, "N: with about 100 contractors, only the player's one contract holds Field Log data");
            T.Check(c.fieldLog.Count > 0, "and it is that one");
        }

        private static void NoCoordinates()
        {
            foreach (FieldInfo f in typeof(ContractorSimulation).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                T.Check(f.FieldType != typeof(List<FieldLogEntry>), "no permanent journal on the contractor (" + f.Name + ")");
            }
            foreach (FieldInfo f in typeof(FieldLogEntry).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                T.Check(f.FieldType != typeof(TileRef), "a log entry holds no location (" + f.Name + ")");
            }
            TestNet n = ProcurementTests.World(0);
            NetworkActor team = ProcurementTests.Reliable(n);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
            Operation op = ProcurementTests.Op(n, c);
            ProcurementDevOverrides.forceBand = OutcomeBand.Partial;
            ProcurementTests.RunUntil(n, () => c.status == ContractStatus.Renegotiating || c.IsTerminal);
            string[] hidden = { op.spatial.workRegion.tileId.ToString(), op.spatial.origin.tileId.ToString(), op.seed.ToString() };
            foreach (FieldLogEntry e in c.fieldLog)
            {
                foreach (string a in e.args)
                {
                    T.Check(a.IndexOf(',') < 0 && a.IndexOf(':') < 0, "no coordinate-like text (" + a + ")");
                    foreach (string h in hidden) T.Check(a != h || a == c.Quantity.ToString() || a == c.Acquire.secured.ToString(), "no hidden tile id or number (" + a + ")");
                }
            }
        }
    }
}
