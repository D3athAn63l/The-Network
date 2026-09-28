using System;
using System.Collections.Generic;
using TheNetwork.Diagnostics;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.Domain.Spatial;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using TheNetwork.Settings;

namespace TheNetwork.Tests
{
    /// <summary>
    /// Phase 2.5 correction pass: a journey never arrives without proof or faster than the contractor
    /// can walk; returns follow the Phase 2 lifecycle; an ended contractor never moves; land is always
    /// found for an anchor; anchors sit around settlements, not on them. Synthetic grid, no RimWorld.
    /// </summary>
    public static class SpatialCorrectionTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Spatial.TopologyChangePastArrivalDoesNotTeleport", TopologyChange));
            t.Add(new KeyValuePair<string, Action>("Spatial.GroundBudgetUsesActualRouteSteps", GroundBudget));
            t.Add(new KeyValuePair<string, Action>("Spatial.AmbientRangeUsesActualRouteSteps", AmbientRange));
            t.Add(new KeyValuePair<string, Action>("Spatial.NonTroubledDisasterReturnsFromIncident", DisasterReturns));
            t.Add(new KeyValuePair<string, Action>("Spatial.TroubledRecoveredReconcilesReturn", TroubledRecovered));
            t.Add(new KeyValuePair<string, Action>("Spatial.TroubledWrittenOffKeepsIncidentTruth", TroubledWrittenOff));
            t.Add(new KeyValuePair<string, Action>("Spatial.DeadSoloNeverTravelsHome", DeadSolo));
            t.Add(new KeyValuePair<string, Action>("Spatial.DissolvedOrganizationNeverMovesAgain", DissolvedOrg));
            t.Add(new KeyValuePair<string, Action>("Spatial.SparseLandInitializationIsGuaranteed", SparseLand));
            t.Add(new KeyValuePair<string, Action>("Spatial.AnchorsAroundSettlementsNotOnThem", AroundNotOn));
            t.Add(new KeyValuePair<string, Action>("FieldLog.CapturedWordingFitsForm", CapturedWording));
        }

        private static SpatialState S(NetworkActor a) => SpatialTests.S(a);

        private static bool Same(TileRef a, TileRef b) => SpatialTests.Same(a, b);

        /// <summary>Real ground route steps between two tiles on the synthetic grid, or -1.</summary>
        internal static int Walk(TestNet n, TileRef from, TileRef to)
        {
            List<int> steps = new List<int>();
            string f;
            return n.graph.TryRoute(from, to, SpatialPolicy.MaxRouteSteps, steps, out f) ? steps.Count : -1;
        }

        /// <summary>Seals a tile into a one-tile islet: still a valid, passable tile, but nothing can walk to it.</summary>
        internal static void Seal(TestNet n, TileRef t)
        {
            int x = n.graph.X(t), y = n.graph.Y(t);
            n.graph.Block(x - 2, y - 2, x + 2, y + 2);
            n.graph.Open(x, y, x, y);
        }

        /// <summary>
        /// The long-detour geography (Finding 2): a fast contractor stands at (35, 5) just east of the sea
        /// band; the only settlement is at (29, 5), six tiles away across the water, but the only land route
        /// runs down to the land bridge at y 27–31 and back up (about fifty steps). Optionally a high-tech
        /// provider's settlement stands right beside the contractor at (36, 6).
        /// </summary>
        internal static TestNet DetourWorld(int seed, bool provider, out NetworkActor team)
        {
            TestNet n = ProcurementTests.World(0, seed);
            n.graph.settlements.Clear();
            n.graph.AddSettlement(8, 20, 1, true);
            if (provider) n.graph.AddSettlement(36, 6, 100, false, true);
            n.graph.AddSettlement(29, 5, 101);
            team = SpatialTests.Still(n);
            team.Get<ContractorSimulation>().mobility.speedBand = Band.VeryHigh;
            S(team).anchor = n.graph.Tile(35, 5);
            return n;
        }

        // ================================================================== Finding 1

        private static void TopologyChange()
        {
            // An ambient journey whose destination stays a valid tile but can no longer be reached.
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = SpatialTests.Still(n);
            TileRef dest = SpatialTests.Away(n, S(a).anchor, 12, 14);
            T.Check(n.ctx.Spatial.DevSendTo(a, dest), "a journey starts");
            int arrival = S(a).arrivalTick;
            n.Advance(Ticks.PerDay / 2);
            n.ctx.Spatial.CatchUp(a);
            TileRef midway = S(a).anchor.Copy();
            T.Check(!Same(midway, dest) && S(a).status == SpatialStatus.Travelling, "part of the way there");
            // As a load does: the runtime route is gone. Then the world changes under the saved truth.
            n.ctx.Spatial.ClearRouteCache();
            Seal(n, dest);
            T.Check(n.graph.IsValid(dest) && n.graph.IsPassable(dest) && Walk(n, midway, dest) < 0, "the destination is still a valid, passable tile, but unreachable");
            // The first catch-up after the load comes only after the committed arrival (no upkeep ran in between).
            n.clock.Now = arrival + Ticks.PerDay;
            n.ctx.Spatial.CatchUp(a);
            T.Check(!Same(S(a).anchor, dest), "past its arrival tick, it is NOT at the destination (no teleport)");
            T.Check(Same(S(a).anchor, midway) && S(a).destination == null && S(a).status == SpatialStatus.Blocked, "it stays at its last valid anchor, Blocked");
            T.Check(S(a).bridgeFrom == null, "an ambient journey is never turned into a charter");

            // An operation leg in the same situation: the operation carries on, fail-soft.
            TestNet m = ProcurementTests.World(0);
            NetworkActor team = SpatialTests.Still(m);
            Contract c = ProcurementTests.Awarded(m, ProcurementTests.Fixer(m), team);
            Operation op = ProcurementTests.Op(m, c);
            TileRef work = op.spatial.workRegion.Copy();
            if (Same(work, op.spatial.origin)) return;
            m.AdvanceTo(op.Find(Checkpoint.Prep).dueTick + Ticks.PerHour);
            m.ctx.Spatial.CatchUp(team);
            TileRef before = S(team).anchor.Copy();
            m.ctx.Spatial.ClearRouteCache();
            Seal(m, work);
            ProcurementDevOverrides.forceBand = OutcomeBand.Success;
            ProcurementTests.RunUntil(m, () => op.Find(Checkpoint.Arrive).done || c.IsTerminal);
            T.Check(!Same(S(team).anchor, work), "the operation's Arrive checkpoint passed, but the unreachable work region was not reached");
            T.Check(S(team).status == SpatialStatus.Blocked && op.spatial.fallbackKey != null, "the leg is Blocked and the plan records the degradation (" + op.spatial.fallbackKey + ")");
            T.Check(Same(S(team).anchor, before) || Walk(m, before, S(team).anchor) >= 0, "no jump: still where it could walk to");
            ProcurementTests.RunUntil(m, () => c.IsTerminal);
            T.Check(c.IsTerminal, "the Phase 2 operation still completes on its own timeline (" + c.status + ")");
        }

        // ================================================================== Finding 2

        private static void GroundBudget()
        {
            int plans = 0;
            for (int seed = 1; seed <= 6; seed++)
            {
                NetworkActor team;
                TestNet n = DetourWorld(seed, false, out team);
                T.Check(Walk(n, S(team).anchor, n.graph.Tile(28, 5)) > 40 && n.graph.ApproxDistance(S(team).anchor, n.graph.Tile(28, 5)) <= 7, "the geography: short as the crow flies, a long detour on land");
                Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
                Operation op = ProcurementTests.Op(n, c);
                if (op?.spatial == null) continue;
                plans++;
                int tpt = SpatialPolicy.TicksPerTile(team.Get<ContractorSimulation>().mobility.speedBand);
                int window = Math.Min(op.Find(Checkpoint.Arrive).dueTick - op.Find(Checkpoint.Prep).dueTick, op.Find(Checkpoint.Return).dueTick - op.Find(Checkpoint.Resolve).dueTick);
                int steps = Walk(n, op.spatial.origin, op.spatial.workRegion);
                T.Check(!op.spatial.Charter, "seed " + seed + ": no provider, no charter");
                T.Check(n.graph.X(op.spatial.workRegion) >= 34, "seed " + seed + ": the far shore is rejected on foot (work region " + op.spatial.workRegion + ")");
                T.Check(steps >= 0 && steps * tpt <= window, "seed " + seed + ": the committed ground route (" + steps + " steps) fits what they can walk in the window");
                T.Check(steps <= SpatialPolicy.RangeTiles(team.Get<ContractorSimulation>().mobility.rangeBand), "seed " + seed + ": and their range");
                if (!Same(op.spatial.workRegion, op.spatial.origin)) T.Eq(op.Find(Checkpoint.Arrive).dueTick, S(team).arrivalTick, "seed " + seed + ": they make it by Arrive without hurrying");
            }
            T.Check(plans > 0, "plans were made");
        }

        private static void AmbientRange()
        {
            NetworkActor a;
            TestNet n = DetourWorld(5, true, out a);
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            int range = Math.Max(3, (int)(SpatialPolicy.RangeTiles(sim.mobility.rangeBand) * 0.6f));
            int journeys = 0;
            for (int round = 0; round < 12; round++)
            {
                TileRef from = S(a).anchor.Copy();
                if (!n.ctx.Spatial.DevRelocateNow(a)) continue;
                journeys++;
                TileRef dest = S(a).destination;
                int steps = Walk(n, from, dest);
                T.Check(steps >= 0 && steps <= range, "round " + round + ": an ambient destination is within the ambient range in REAL steps (" + steps + " ≤ " + range + ")");
                T.Check(S(a).bridgeFrom == null, "round " + round + ": ambient movement never charters");
                T.Check(n.graph.X(dest) >= 34 || n.graph.X(from) < 30, "round " + round + ": the settlement across the water is never an ambient hop");
                n.AdvanceTo(S(a).arrivalTick + 1);
                n.ctx.Spatial.CatchUp(a);
                T.Check(Same(S(a).anchor, dest), "round " + round + ": arrived on foot");
            }
            T.Check(journeys > 3, "it did move (" + journeys + " journeys)");
            T.Eq(0, n.ctx.Spatial.counters.charterJourneys, "no chartered journey at all");
        }

        // ================================================================== Finding 3

        private static void DisasterReturns()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor team = SpatialTests.Still(n);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
            Operation op = ProcurementTests.Op(n, c);
            ProcurementDevOverrides.forceBand = OutcomeBand.Disaster;
            ProcurementDevOverrides.forceNotTroubled = true;
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Resolve).done || c.IsTerminal);
            T.Check(op.outcome != null && op.outcome.band == OutcomeBand.Disaster && op.outcome.troubledKey == null, "a Disaster that is not Troubled");
            T.Check(team.status == ActorStatus.Active, "with survivors: the contractor is still there");
            T.Check(op.spatial.incident != null && Same(op.spatial.incident, op.spatial.workRegion), "the incident is recorded where it happened");
            TileRef incident = op.spatial.incident.Copy();
            int committed = op.outcome.committedTick;
            T.Check(S(team).purpose == SpatialPurpose.Return && Same(S(team).destination, op.spatial.returnTo), "the survivors head back (the incident does not keep them out)");
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Return).done || c.IsTerminal);
            T.Check(Same(S(team).anchor, op.spatial.returnTo), "and reach where they return to");
            T.Check(op.outcome.band == OutcomeBand.Disaster && op.outcome.committedTick == committed, "the outcome was never rerolled");
            T.Check(Same(incident, op.spatial.incident) && Same(incident, n.ctx.Spatial.IncidentTile(op)), "the incident stays available to consequence logic");
        }

        private static void TroubledRecovered()
        {
            TestNet n = ProcurementTests.World(0);
            n.world.factions.Add(FakeWorld.Faction(21, "Blood Hawks", "ludeon.rimworld", 4, true, true, true));
            NetworkActor team = SpatialTests.Still(n);
            Contract c = SpatialTests.MissingAt(n, team);
            Operation op = ProcurementTests.Op(n, c);
            T.Eq(OpStatus.Troubled, op.status, "Troubled (missing)");
            T.Check(Same(S(team).anchor, op.spatial.incident) && S(team).destination == null, "out at the incident; no return journey while Troubled");
            TileRef incident = op.spatial.incident.Copy();
            ProcurementDevOverrides.forceTroubledFound = true;
            n.ctx.Operations.DevAdvance(op);
            T.Check(op.Find(Checkpoint.Return).done && op.status != OpStatus.Troubled, "Phase 2 declared the group found and returned");
            T.Check(Same(S(team).anchor, op.spatial.returnTo), "Spatial agrees: they are where they return to, not at the incident");
            T.Check(S(team).destination == null && S(team).bridgeFrom == null && S(team).status == SpatialStatus.Idle, "no new journey after the return was declared done");
            T.Eq(1, n.ctx.Spatial.counters.reconciled, "one lifecycle reconciliation");
            T.Check(Same(incident, op.spatial.incident), "the incident stays recorded");
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            T.Check(c.IsTerminal, "the recovered contract goes on to its end (" + c.status + ")");
        }

        private static void TroubledWrittenOff()
        {
            foreach (ContractorForm form in new[] { ContractorForm.Team, ContractorForm.Solo })
            {
                TestNet n = ProcurementTests.World(0);
                n.world.factions.Add(FakeWorld.Faction(21, "Blood Hawks", "ludeon.rimworld", 4, true, true, true));
                NetworkActor a = SpatialTests.Still(n, form);
                Contract c = SpatialTests.MissingAt(n, a);
                Operation op = ProcurementTests.Op(n, c);
                TileRef incident = op.spatial?.incident?.Copy();
                T.Check(incident != null && Same(S(a).anchor, incident), form + ": out at the incident");
                ProcurementDevOverrides.forceTroubledFound = false;
                n.ctx.Operations.DevAdvance(op);
                T.Check(op.IsFinished, form + ": written off");
                T.Check(Same(S(a).anchor, incident) && S(a).destination == null, form + ": the last truth is the incident; nobody brought them home");
                T.Eq(0, n.ctx.Spatial.counters.reconciled, form + ": no reconciliation for a write-off");
                if (a.status != ActorStatus.Active)
                {
                    n.Advance(Ticks.PerDay * 30);
                    T.Check(Same(S(a).anchor, incident), form + ": an ended contractor stays at the incident");
                }
            }
        }

        // ================================================================== Finding 4

        /// <summary>Kills the Solo the moment its operation's outcome is published (during Resolve, before the return).</summary>
        private sealed class KillOnResolve : IEventConsumer
        {
            public TestNet n;
            public NetworkActor solo;
            public bool fired;

            public string Name => "test.killOnResolve";

            public void Handle(NetworkEvent evt)
            {
                if (fired || solo.status != ActorStatus.Active) return;
                fired = true;
                n.ctx.Contractors.EndActor(solo, "Died");
            }
        }

        private static void DeadSolo()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor solo = SpatialTests.Still(n, ContractorForm.Solo);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), solo);
            Operation op = ProcurementTests.Op(n, c);
            KillOnResolve kill = new KillOnResolve { n = n, solo = solo };
            n.bus.Register(1000, kill, EventKeys.OperationResolved);
            ProcurementDevOverrides.forceBand = OutcomeBand.Success;
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Resolve).done || c.IsTerminal);
            T.Check(kill.fired && solo.status != ActorStatus.Active, "the Solo died during Resolve");
            T.Check(Same(S(solo).anchor, op.spatial.workRegion), "its last position is where it was working");
            T.Check(S(solo).destination == null && S(solo).purpose == SpatialPurpose.None && S(solo).bridgeFrom == null, "no journey home was started for the dead Solo");
            TileRef last = S(solo).anchor.Copy();
            int journeys = S(solo).journeys;
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            n.Advance(Ticks.PerDay * 40);
            n.ctx.Spatial.CatchUp(solo);
            T.Check(Same(last, S(solo).anchor) && S(solo).journeys == journeys && S(solo).destination == null, "many days later it has not moved");
            T.Check(c.IsTerminal, "the operation itself ended as Phase 2 decided (" + c.status + ")");
        }

        private static void DissolvedOrg()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor org = SpatialTests.Still(n);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), org);
            Operation op = ProcurementTests.Op(n, c);
            n.AdvanceTo(op.Find(Checkpoint.Prep).dueTick + Ticks.PerDay / 4);
            n.ctx.Spatial.CatchUp(org);
            n.ctx.Contractors.EndActor(org, "Dissolved");
            T.Check(org.status != ActorStatus.Active, "dissolved mid-journey");
            TileRef last = S(org).anchor.Copy();
            T.Check(S(org).destination == null && S(org).purpose == SpatialPurpose.None, "frozen: no destination, no purpose");
            ProcurementTests.RunUntil(n, () => c.IsTerminal || op.IsFinished, 30);
            n.Advance(Ticks.PerDay * 30);
            n.ctx.Spatial.CatchUp(org);
            T.Check(Same(last, S(org).anchor) && S(org).destination == null, "no later checkpoint or upkeep ever moves it");
        }

        // ================================================================== Finding 5

        private static void SparseLand()
        {
            // The search itself: the seeded probes all miss; the guaranteed scan still finds the one tile.
            const int count = 10000;
            const int seed = 424242;
            HashSet<int> probed = new HashSet<int>();
            int calls = 0;
            SpatialSearch.FirstPassable(count, seed, i =>
            {
                if (calls++ < SpatialSearch.Probes) probed.Add(i);
                return false;
            });
            T.Eq(SpatialSearch.Probes + count, calls, "nothing passable: every probe and every tile was tried, once");
            int target = -1;
            for (int i = count - 1; i >= 0 && target < 0; i--) if (!probed.Contains(i)) target = i;
            int looked = 0;
            int found = SpatialSearch.FirstPassable(count, seed, i =>
            {
                looked++;
                return i == target;
            });
            T.Eq(target, found, "the one passable tile, missed by all " + SpatialSearch.Probes + " probes, is found");
            T.Check(looked > SpatialSearch.Probes, "found by the scan after the probes");
            T.Eq(found, SpatialSearch.FirstPassable(count, seed, i => i == target), "deterministic");

            // A world of water with one passable tile and no settlements: the contractor is anchored there.
            TileRef only = null;
            for (int run = 0; run < 2; run++)
            {
                TestNet n = ProcurementTests.World(0);
                n.graph.settlements.Clear();
                n.graph.Block(0, 0, n.graph.width - 1, n.graph.height - 1);
                n.graph.Open(61, 37, 61, 37);
                NetworkActor a = SpatialTests.Still(n);
                T.Check(S(a).IsInitialized && Same(S(a).anchor, n.graph.Tile(61, 37)), "run " + run + ": anchored on the only land there is");
                if (only != null) T.Check(Same(only, S(a).anchor), "deterministic across worlds");
                only = S(a).anchor;
            }
        }

        private static void AroundNotOn()
        {
            TestNet n = ContractorWorld();
            int checkedAnchors = 0;
            foreach (NetworkActor a in n.ctx.actors.actors)
            {
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                if (sim == null || !sim.spatial.IsInitialized) continue;
                checkedAnchors++;
                foreach (SettlementFacts f in n.graph.settlements) T.Check(!Same(f.tile, sim.spatial.anchor), a.name.Display + " is anchored around a settlement, never on it");
            }
            T.Check(checkedAnchors > 10, "many anchors checked (" + checkedAnchors + ")");
        }

        private static TestNet ContractorWorld()
        {
            return ContractorTests.WorldWithCast(30, 8);
        }

        // ================================================================== Field Log wording

        private static void CapturedWording()
        {
            foreach (ContractorForm form in new[] { ContractorForm.Solo, ContractorForm.Team })
            {
                TestNet n = ProcurementTests.World(0);
                NetworkActor a = SpatialTests.Still(n, form);
                Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), a, "TestSteel", 150);
                ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
                ProcurementDevOverrides.forceTroubled = SubStatus.Captured;
                ProcurementTests.RunUntil(n, () => ProcurementTests.Op(n, c)?.outcome != null || c.IsTerminal);
                string expected = form == ContractorForm.Solo ? FieldLogKeys.CapturedSolo : FieldLogKeys.Captured;
                bool has = false;
                foreach (FieldLogEntry e in c.fieldLog) has |= e.key == expected;
                T.Check(has, form + ": the capture is told as " + expected);
                foreach (FieldLogEntry e in c.fieldLog) T.Check(form != ContractorForm.Solo || e.key != FieldLogKeys.Captured, "a Solo is never told as \"people have been taken\"");
            }
        }
    }
}
