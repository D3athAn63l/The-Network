using System;
using System.Collections.Generic;
using System.Reflection;
using TheNetwork.Core;
using TheNetwork.Diagnostics;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Consequences;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.Domain.Spatial;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Settings;
using TheNetwork.UI;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>
    /// Phase 2.5 abstract spatial continuity (SPATIAL): initialization, coarse lazy movement, routing,
    /// persistence, operation integration, Last Known Location placement, migration, and the privacy
    /// boundary. Pure logic against the synthetic grid graph; no RimWorld world is launched.
    /// </summary>
    public static class SpatialTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Spatial.DeterministicInitialization", DeterministicInit));
            t.Add(new KeyValuePair<string, Action>("Spatial.AnchorsSpreadWithoutPlayerBias", AnchorsSpread));
            t.Add(new KeyValuePair<string, Action>("Spatial.OriginFactionThenFallbacks", OriginThenFallbacks));
            t.Add(new KeyValuePair<string, Action>("Spatial.WorldGeneratedAndLateWorldData", WorldGeneratedAndLate));
            t.Add(new KeyValuePair<string, Action>("Spatial.RoutesSameLayerUnreachableInvalid", Routes));
            t.Add(new KeyValuePair<string, Action>("Spatial.CoarseMovementAndCatchUp", CoarseMovement));
            t.Add(new KeyValuePair<string, Action>("Spatial.StationaryDoesNoWork", Stationary));
            t.Add(new KeyValuePair<string, Action>("Spatial.MobilitySpeedAndRange", MobilityBands));
            t.Add(new KeyValuePair<string, Action>("Spatial.SaveLoadNoRerollAndCacheRebuild", SaveLoadJourney));
            t.Add(new KeyValuePair<string, Action>("Spatial.RouteCacheRebuildSameResult", CacheRebuildTwin));
            t.Add(new KeyValuePair<string, Action>("Spatial.OperationStartsFromRealAnchor", OperationOrigin));
            t.Add(new KeyValuePair<string, Action>("Spatial.ArrivalAndReturnFollowCheckpoints", ArrivalAndReturn));
            t.Add(new KeyValuePair<string, Action>("Spatial.DelayMovesTheReturnNotTheOutcome", DelayFollows));
            t.Add(new KeyValuePair<string, Action>("Spatial.AbortStopsWhereTheyAre", AbortStops));
            t.Add(new KeyValuePair<string, Action>("Spatial.DeathAndDissolutionKeepLastTruth", DeathKeepsTruth));
            t.Add(new KeyValuePair<string, Action>("Spatial.LastKnownLocationNearIncident", LklNearIncident));
            t.Add(new KeyValuePair<string, Action>("Spatial.LastKnownLocationFallsBackSoftly", LklFallback));
            t.Add(new KeyValuePair<string, Action>("Spatial.InvalidDestinationRecovers", InvalidDestination));
            t.Add(new KeyValuePair<string, Action>("Spatial.NoCrossLayerTravel", NoCrossLayer));
            t.Add(new KeyValuePair<string, Action>("Spatial.AmbientIsNotHistory", AmbientSilent));
            t.Add(new KeyValuePair<string, Action>("Spatial.NoPawnNoWorldObjectNoUiLeak", Boundaries));
            t.Add(new KeyValuePair<string, Action>("Spatial.FaultNeverStallsAContract", FaultNeverStalls));
            t.Add(new KeyValuePair<string, Action>("Migration.PhaseTwoContractorAnchored", MigrationAnchor));
            t.Add(new KeyValuePair<string, Action>("Migration.EndedAndQuarantinedSafe", MigrationEnded));
            t.Add(new KeyValuePair<string, Action>("Migration.LegacyOperationUnchanged", MigrationLegacyOp));
            t.Add(new KeyValuePair<string, Action>("Migration.SaveVersionThreeAndOldShape", MigrationVersion));
        }

        // ================================================================== helpers

        private static SpatialState S(NetworkActor a) => a.Get<ContractorSimulation>().spatial;

        private static bool Same(TileRef a, TileRef b) => a != null && b != null && a.tileId == b.tileId && a.layerId == b.layerId;

        /// <summary>An idle contractor that will not relocate on its own during the test.</summary>
        private static NetworkActor Still(TestNet n, ContractorForm form = ContractorForm.Team)
        {
            NetworkActor a = ProcurementTests.Reliable(n, form);
            S(a).nextAmbientTick = int.MaxValue / 2;
            a.Get<ContractorSimulation>().mobility.speedBand = Band.Medium;
            return a;
        }

        private static TileRef Away(TestNet n, TileRef from, int min, int max)
        {
            TileRef t;
            T.Check(n.graph.TryFindPassableNear(from, min, max, 777, out t), "a destination " + min + "–" + max + " tiles away");
            return t;
        }

        private static List<int> RouteOf(TestNet n, TileRef from, TileRef to)
        {
            List<int> steps = new List<int>();
            string f;
            n.graph.TryRoute(from, to, SpatialPolicy.MaxRouteSteps, steps, out f);
            return steps;
        }

        // ================================================================== initialization

        private static void DeterministicInit()
        {
            TestNet a = ContractorTests.WorldWithCast(25, 999);
            TestNet b = ContractorTests.WorldWithCast(25, 999);
            List<NetworkActor> ca = ContractorTests.Contractors(a), cb = ContractorTests.Contractors(b);
            T.Eq(ca.Count, cb.Count, "same cast");
            int same = 0;
            for (int i = 0; i < ca.Count; i++)
            {
                SpatialState sa = S(ca[i]), sb = S(cb[i]);
                T.Check(sa.IsInitialized && a.graph.IsPassable(sa.anchor), ca[i].name.Display + ": a valid passable anchor");
                if (Same(sa.anchor, sb.anchor) && sa.nextAmbientTick == sb.nextAmbientTick) same++;
            }
            T.Eq(ca.Count, same, "the same world and seed give the same anchors, before anything is saved");
            SpatialState first = S(ca[0]);
            TileRef before = first.anchor.Copy();
            T.Check(a.ctx.Spatial.EnsureInitialized(ca[0]), "already initialized");
            T.Check(Same(before, first.anchor), "initialization is committed once and never redone");
        }

        private static void AnchorsSpread()
        {
            TestNet n = ContractorTests.WorldWithCast(40, 31);
            List<NetworkActor> cs = ContractorTests.Contractors(n);
            HashSet<int> tiles = new HashSet<int>();
            int nearPlayer = 0, onSettlement = 0;
            TileRef colony = n.graph.Tile(8, 8);
            foreach (NetworkActor a in cs)
            {
                TileRef t = S(a).anchor;
                tiles.Add(t.tileId);
                if (n.graph.ApproxDistance(colony, t) <= 6) nearPlayer++;
                foreach (SettlementFacts s in n.graph.settlements) if (Same(s.tile, t)) onSettlement++;
            }
            T.Check(tiles.Count >= cs.Count / 2, "different seeds spread over the world (" + tiles.Count + " distinct anchors for " + cs.Count + ")");
            T.Check(nearPlayer <= cs.Count / 4, "no bias toward the player's colony (" + nearPlayer + " of " + cs.Count + " within 6 tiles)");
            T.Eq(0, onSettlement, "never on a settlement tile: around it, owning nothing");
        }

        private static void OriginThenFallbacks()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = ProcurementTests.Reliable(n);
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            sim.origin = new FactionRef { loadId = 103, name = "Test" };
            sim.spatial = new SpatialState();
            T.Check(n.ctx.Spatial.EnsureInitialized(a), "initialized");
            bool nearOrigin = false;
            foreach (SettlementFacts s in n.graph.settlements) if (s.factionLoadId == 103 && n.graph.ApproxDistance(s.tile, sim.spatial.anchor) <= 3) nearOrigin = true;
            T.Check(nearOrigin, "near a settlement of its live origin faction");

            sim.origin = new FactionRef { loadId = 9999, name = "Gone" };
            sim.spatial = new SpatialState();
            T.Check(n.ctx.Spatial.EnsureInitialized(a), "origin faction gone: still initialized");
            bool nearAny = false;
            foreach (SettlementFacts s in n.graph.settlements) if (!s.player && n.graph.ApproxDistance(s.tile, sim.spatial.anchor) <= 3) nearAny = true;
            T.Check(nearAny, "near another plausible settlement");

            n.graph.settlements.Clear();
            sim.spatial = new SpatialState();
            T.Check(n.ctx.Spatial.EnsureInitialized(a) && n.graph.IsPassable(sim.spatial.anchor), "no settlements at all: a deterministic passable tile");
        }

        private static void WorldGeneratedAndLate()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor w = n.ctx.Contractors.CreateWorldGenerated(0, "Test");
            T.Check(w != null && S(w).IsInitialized && n.graph.IsPassable(S(w).anchor), "a world-generated newcomer is anchored");
            n.graph.ready = false;
            NetworkActor late = ProcurementTests.Reliable(n);
            T.Eq(SpatialStatus.Uninitialized, S(late).status, "no world data yet: Uninitialized, nothing invented");
            n.ctx.Spatial.CatchUp(late);
            T.Eq(SpatialStatus.Uninitialized, S(late).status, "catch-up without world data does nothing");
            n.graph.ready = true;
            T.Eq(1, n.ctx.Spatial.InitializeAll(), "initialized once world data exists");
            T.Check(S(late).IsInitialized, "anchored");
        }

        // ================================================================== routing and movement

        private static void Routes()
        {
            TestNet n = ProcurementTests.World(0);
            GridWorldGraph g = n.graph;
            List<int> steps = new List<int>();
            string f;
            T.Check(g.TryRoute(g.Tile(20, 10), g.Tile(45, 10), 300, steps, out f), "across the sea by the land bridge (" + f + ")");
            T.Check(steps.Count > 25, "the route goes around the sea (" + steps.Count + " steps, straight line 25)");
            T.Eq(g.Tile(45, 10).tileId, steps[steps.Count - 1], "the route ends at the destination");
            g.Block(50, 30, 58, 30);
            g.Block(50, 38, 58, 38);
            g.Block(50, 30, 50, 38);
            g.Block(58, 30, 58, 38);
            T.Check(!g.TryRoute(g.Tile(20, 10), g.Tile(54, 34), 300, steps, out f) && f == "Unreachable", "an enclosed tile is unreachable (" + f + ")");
            T.Check(!g.TryRoute(g.Tile(20, 10), new TileRef { tileId = -5, layerDef = "Surface" }, 300, steps, out f) && f == "InvalidTile", "an invalid tile");
            T.Check(!g.TryRoute(g.Tile(2, 2), g.Tile(60, 36), 10, steps, out f) && f == "TooFar", "too far for the step limit");

            NetworkActor a = Still(n);
            TileRef here = S(a).anchor.Copy();
            T.Check(!n.ctx.Spatial.DevSendTo(a, g.Tile(54, 34)), "a journey to an unreachable place is never started");
            T.Check(Same(here, S(a).anchor) && S(a).destination == null, "the contractor stays where it is");
        }

        private static void CoarseMovement()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Still(n);
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            sim.mobility.speedBand = Band.Medium;
            TileRef start = S(a).anchor.Copy();
            TileRef dest = Away(n, start, 15, 18);
            T.Check(n.ctx.Spatial.DevSendTo(a, dest), "sets out");
            List<int> route = RouteOf(n, start, dest);
            T.Eq(n.clock.Now + route.Count * SpatialPolicy.TicksPerTile(Band.Medium), S(a).arrivalTick, "arrival from the route length and the speed band");
            n.Advance(Ticks.PerDay);
            n.ctx.Spatial.CatchUp(a);
            int idx = route.IndexOf(S(a).anchor.tileId);
            T.Check(idx >= 7 && idx <= 9, "about a day's travel along the route (step " + (idx + 1) + " of " + route.Count + ", 9 tiles a day)");
            T.Check(S(a).status == SpatialStatus.Travelling && Same(S(a).destination, dest), "still travelling to the committed destination");
            n.Advance(Ticks.PerDay / 3);
            n.ctx.Spatial.CatchUp(a);
            int idx2 = route.IndexOf(S(a).anchor.tileId);
            T.Check(idx2 >= idx, "never backward");
            n.Advance(Ticks.PerDay * 3);
            n.ctx.Spatial.CatchUp(a);
            T.Check(Same(S(a).anchor, dest) && S(a).destination == null && S(a).status == SpatialStatus.Idle, "catching up after several days: arrived, idle");
            T.Eq(1, S(a).journeys, "one journey");
        }

        private static void Stationary()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Still(n);
            TileRef here = S(a).anchor.Copy();
            int routes = n.ctx.Spatial.counters.routesBuilt, queries = n.graph.routeQueries;
            n.Advance(Ticks.PerDay * 10);
            T.Check(Same(here, S(a).anchor), "no movement while stationary");
            T.Eq(routes, n.ctx.Spatial.counters.routesBuilt, "no route built for a stationary contractor");
            T.Eq(queries, n.graph.routeQueries, "no route query either");
            T.Check(n.ctx.Spatial.counters.catchUps > 0, "its daily upkeep still catches it up (cheaply)");
        }

        private static void MobilityBands()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor slow = Still(n), fast = Still(n);
            slow.Get<ContractorSimulation>().mobility.speedBand = Band.VeryLow;
            fast.Get<ContractorSimulation>().mobility.speedBand = Band.VeryHigh;
            TileRef dest = Away(n, S(slow).anchor, 12, 14);
            fast.Get<ContractorSimulation>().spatial.anchor = S(slow).anchor.Copy();
            T.Check(n.ctx.Spatial.DevSendTo(slow, dest) && n.ctx.Spatial.DevSendTo(fast, dest), "both set out on the same route");
            T.Check(S(fast).arrivalTick < S(slow).arrivalTick, "the faster band arrives sooner (" + (S(fast).arrivalTick - n.clock.Now) + " vs " + (S(slow).arrivalTick - n.clock.Now) + " ticks)");

            NetworkActor homebody = ProcurementTests.Reliable(n);
            ContractorSimulation hs = homebody.Get<ContractorSimulation>();
            hs.mobility.rangeBand = Band.VeryLow;
            int maxRange = Math.Max(3, (int)(SpatialPolicy.RangeTiles(Band.VeryLow) * 0.6f));
            for (int i = 0; i < 6; i++)
            {
                TileRef from = S(homebody).anchor.Copy();
                if (!n.ctx.Spatial.DevRelocateNow(homebody)) continue;
                T.Check(n.graph.ApproxDistance(from, S(homebody).destination) <= maxRange, "ambient moves stay within the range band (" + n.graph.ApproxDistance(from, S(homebody).destination) + " ≤ " + maxRange + ")");
                n.Advance(Ticks.PerDay * 5);
                n.ctx.Spatial.CatchUp(homebody);
            }
            T.Check(S(homebody).journeys > 0, "it did move");
        }

        private static void SaveLoadJourney()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Still(n);
            TileRef dest = Away(n, S(a).anchor, 16, 20);
            n.ctx.Spatial.DevSendTo(a, dest);
            n.Advance(Ticks.PerDay);
            n.ctx.Spatial.CatchUp(a);
            SpatialState before = S(a);
            TileRef anchor = before.anchor.Copy();
            int arrival = before.arrivalTick, journeys = before.journeys, updated = before.lastUpdateTick;

            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            n.ctx.Spatial.ClearRouteCache();
            NetworkActor la = n.ctx.actors.Get(a.id);
            SpatialState after = S(la);
            T.Check(Same(anchor, after.anchor) && Same(dest, after.destination) && after.arrivalTick == arrival && after.journeys == journeys && after.lastUpdateTick == updated && after.status == SpatialStatus.Travelling,
                "the journey survives save/load exactly (anchor, destination, arrival, progress)");
            int rebuilt = n.ctx.Spatial.counters.routesRebuilt;
            n.Advance(Ticks.PerDay / 2);
            n.ctx.Spatial.CatchUp(la);
            T.Eq(rebuilt + 1, n.ctx.Spatial.counters.routesRebuilt, "the route is rebuilt from the saved anchor");
            T.Check(Same(dest, after.destination), "the destination is never rerolled");
            n.AdvanceTo(arrival + 1);
            n.ctx.Spatial.CatchUp(la);
            T.Check(Same(dest, after.anchor), "and it arrives where it was going");
        }

        private static void CacheRebuildTwin()
        {
            TestNet x = ProcurementTests.World(0), y = ProcurementTests.World(0);
            NetworkActor ax = Still(x), ay = Still(y);
            TileRef dx = Away(x, S(ax).anchor, 18, 22), dy = Away(y, S(ay).anchor, 18, 22);
            T.Check(Same(dx, dy), "twins");
            x.ctx.Spatial.DevSendTo(ax, dx);
            y.ctx.Spatial.DevSendTo(ay, dy);
            for (int day = 0; day < 3; day++)
            {
                x.Advance(Ticks.PerDay / 2);
                y.Advance(Ticks.PerDay / 2);
                y.ctx.Spatial.ClearRouteCache();
                x.ctx.Spatial.CatchUp(ax);
                y.ctx.Spatial.CatchUp(ay);
                T.Check(Same(S(ax).anchor, S(ay).anchor), "day " + day + ": dropping the route cache changes nothing (" + S(ax).anchor + " / " + S(ay).anchor + ")");
            }
            T.Check(y.ctx.Spatial.counters.routesRebuilt >= 2, "the dropped caches were rebuilt");
        }

        // ================================================================== operations

        private static void OperationOrigin()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = Still(n);
            TileRef anchor = S(team).anchor.Copy();
            Contract c = ProcurementTests.Awarded(n, fixer, team);
            Operation op = ProcurementTests.Op(n, c);
            T.Check(op.spatial != null, "a new operation has a hidden spatial plan");
            T.Check(Same(anchor, op.spatial.origin), "its origin is the contractor's real anchor");
            T.Check(op.spatial.workRegion != null && n.graph.IsPassable(op.spatial.workRegion), "a committed, valid work region");
            T.Check(n.graph.ApproxDistance(anchor, op.spatial.workRegion) <= SpatialPolicy.RangeTiles(team.Get<ContractorSimulation>().mobility.rangeBand), "within the contractor's range");
            T.Check(!op.spatial.detached && S(team).operation == op.id, "the main body is on it");
            T.Eq(op.Find(Checkpoint.Prep).dueTick, S(team).journeyStartTick, "they set out at the Prep checkpoint");
            T.Eq(op.Find(Checkpoint.Arrive).dueTick, S(team).arrivalTick, "and are there by Arrive (the committed timeline)");
            T.Check(Same(op.spatial.workRegion, S(team).destination), "heading for the work region");
            TileRef work = op.spatial.workRegion.Copy();
            Operation copy = ProcurementTests.RoundTrip(op);
            T.Check(copy.spatial != null && Same(work, copy.spatial.workRegion) && Same(anchor, copy.spatial.origin), "the plan survives save/load (no reroll)");
        }

        private static void ArrivalAndReturn()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor team = Still(n);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
            Operation op = ProcurementTests.Op(n, c);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Arrive).done || c.IsTerminal);
            T.Check(Same(op.spatial.workRegion, S(team).anchor) && S(team).status == SpatialStatus.OnAssignment, "at the Arrive checkpoint they are at the work region");
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Resolve).done || c.IsTerminal);
            T.Check(S(team).purpose == SpatialPurpose.Return && Same(op.spatial.returnTo, S(team).destination), "after the work they head back");
            T.Eq(op.Find(Checkpoint.Return).dueTick, S(team).arrivalTick, "arriving when the Return checkpoint is due");
            T.Check(op.outcome.band == OutcomeBand.Triumph, "the resolver still decided what happened");
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Return).done || c.IsTerminal);
            T.Check(Same(op.spatial.returnTo, S(team).anchor), "back at the Return checkpoint");
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            T.Eq(ContractStatus.Fulfilled, c.status, "the drop-pod delivery path is unchanged");
            T.Check(!S(team).operation.IsValid && S(team).status == SpatialStatus.Idle && S(team).destination == null, "idle again when the operation ends");
        }

        private static void DelayFollows()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor team = Still(n);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
            Operation op = ProcurementTests.Op(n, c);
            int plannedReturn = op.Find(Checkpoint.Return).dueTick;
            ProcurementDevOverrides.forceBand = OutcomeBand.Success;
            ProcurementDevOverrides.forceDelayTicks = 3 * Ticks.PerDay;
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Resolve).done || c.IsTerminal);
            T.Eq(ContractStatus.Delayed, c.status, "the Phase 2 delay applies");
            T.Eq(plannedReturn + 3 * Ticks.PerDay, op.Find(Checkpoint.Return).dueTick, "the Return checkpoint moved by the delay");
            T.Eq(op.Find(Checkpoint.Return).dueTick, S(team).arrivalTick, "the return journey follows the delayed checkpoint (no second delay system)");
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            T.Check(c.IsTerminal, "not stuck (" + c.status + "; what happened is the resolver's call)");
        }

        private static void AbortStops()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor team = Still(n);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
            Operation op = ProcurementTests.Op(n, c);
            TileRef origin = op.spatial.origin.Copy(), work = op.spatial.workRegion.Copy();
            List<int> route = RouteOf(n, origin, work);
            n.AdvanceTo((op.Find(Checkpoint.Prep).dueTick + op.Find(Checkpoint.Arrive).dueTick) / 2);
            n.ctx.Procurement.Void(c, Causes.DefMissing);
            T.Eq(OpStatus.Aborted, op.status, "the operation aborted");
            SpatialState s = S(team);
            T.Check(!s.operation.IsValid && s.destination == null && s.status == SpatialStatus.Idle, "released and idle");
            T.Check(Same(s.anchor, origin) || route.Contains(s.anchor.tileId), "stopped on the way, where they were");
            T.Check(!Same(s.anchor, work) || route.Count <= 1, "not teleported to the work region");
        }

        private static void DeathKeepsTruth()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor solo = Still(n, ContractorForm.Solo);
            TileRef dest = Away(n, S(solo).anchor, 14, 16);
            n.ctx.Spatial.DevSendTo(solo, dest);
            n.Advance(Ticks.PerDay);
            n.ctx.Contractors.EndActor(solo, "Died");
            SpatialState s = S(solo);
            T.Check(s.IsInitialized && s.destination == null && !Same(s.anchor, dest), "death while travelling: the last position stays as truth");
            TileRef last = s.anchor.Copy();
            n.Advance(Ticks.PerDay * 5);
            T.Check(Same(last, s.anchor), "an ended contractor never moves again");

            NetworkActor org = Still(n);
            TileRef orgAt = S(org).anchor.Copy();
            n.ctx.Contractors.EndActor(org, "Dissolved");
            T.Check(Same(orgAt, S(org).anchor), "dissolution keeps the last anchor");
        }

        private static Contract MissingAt(TestNet n, NetworkActor team)
        {
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team, "TestSteel", 150);
            ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
            ProcurementDevOverrides.forceSecured = 0;
            ProcurementDevOverrides.forceTroubled = SubStatus.Missing;
            ProcurementDevOverrides.forceFollowUp = true;
            ProcurementTests.RunUntil(n, () => ProcurementTests.Op(n, c)?.outcome != null || c.IsTerminal);
            return c;
        }

        private static Opportunity FollowUp(TestNet n, Contract c)
        {
            foreach (Opportunity o in n.ctx.opportunities.opportunities) if (o.origin == OpportunityOrigin.ConsequenceRule && o.originRef.Equals(c.id.Ref)) return o;
            return null;
        }

        private static void LklNearIncident()
        {
            TestNet n = ProcurementTests.World(0);
            n.world.factions.Add(FakeWorld.Faction(21, "Blood Hawks", "ludeon.rimworld", 4, true, true, true));
            NetworkActor team = Still(n);
            Contract c = MissingAt(n, team);
            Operation op = ProcurementTests.Op(n, c);
            T.Eq(ContractStatus.Troubled, c.status, "missing");
            T.Check(op.spatial.incident != null && Same(op.spatial.incident, op.spatial.workRegion), "the incident is where they were working");
            T.Check(Same(op.spatial.incident, S(team).anchor), "and the contractor's last truth stays there (not reset)");
            ProcurementTests.RunUntil(n, () => FollowUp(n, c) != null, 2);
            Opportunity lkl = FollowUp(n, c);
            T.Check(lkl != null, "a Last Known Location");
            if (lkl == null) return;
            int d = n.graph.ApproxDistance(op.spatial.incident, lkl.location);
            T.Check(d <= SpatialPolicy.IncidentSiteRadius, "placed near where it happened (" + d + " tiles)");
            T.Eq(1, n.ctx.Spatial.counters.lklNear, "placed by spatial truth");
            T.Eq(0, n.sites.tileSeeds.Count, "the old placement was not needed");
            T.Check(lkl.TargetCount <= op.outcome.secured, "Phase 2 cargo truth unchanged");
        }

        private static void LklFallback()
        {
            TestNet n = ProcurementTests.World(0);
            n.world.factions.Add(FakeWorld.Faction(21, "Blood Hawks", "ludeon.rimworld", 4, true, true, true));
            n.sites.noNearTile = true;
            NetworkActor team = Still(n);
            Contract c = MissingAt(n, team);
            ProcurementTests.RunUntil(n, () => FollowUp(n, c) != null, 2);
            T.Check(FollowUp(n, c) != null, "no valid tile near the incident: the ordinary placement still makes the site");
            T.Eq(1, n.ctx.Spatial.counters.lklFallback, "counted as a fallback");
            T.Check(n.sites.tileSeeds.Count > 0 && n.sites.nearRequests.Count > 0, "near first, then the old search");
            T.Check(!c.IsTerminal || c.status == ContractStatus.Failed, "the contract is not harmed (" + c.status + ")");
        }

        private static void InvalidDestination()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Still(n);
            n.ctx.Spatial.DevSendTo(a, Away(n, S(a).anchor, 12, 14));
            n.Advance(Ticks.PerDay / 2);
            n.ctx.Spatial.CatchUp(a);
            TileRef here = S(a).anchor.Copy();
            n.ctx.Spatial.DevInvalidateDestination(a);
            n.Advance(Ticks.PerDay / 2);
            n.ctx.Spatial.CatchUp(a);
            T.Check(S(a).destination == null && S(a).status == SpatialStatus.Blocked && Same(here, S(a).anchor), "an invalid destination is dropped; the last valid anchor is kept");
            T.Eq(1, n.ctx.Spatial.counters.invalidDestinations, "recorded");

            NetworkActor team = Still(n);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
            Operation op = ProcurementTests.Op(n, c);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            n.AdvanceTo(op.Find(Checkpoint.Prep).dueTick + 10);
            n.ctx.Spatial.DevInvalidateDestination(team);
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            T.Eq(ContractStatus.Fulfilled, c.status, "the operation's timeline and result are unaffected");
            T.Check(op.spatial.fallbackKey != null, "the plan records the degradation (" + op.spatial.fallbackKey + ")");
            T.Check(n.graph.IsValid(S(team).anchor), "the contractor keeps a valid anchor");
        }

        private static void NoCrossLayer()
        {
            TestNet n = ProcurementTests.World(0);
            n.graph.layers[1] = "Orbit";
            NetworkActor a = Still(n);
            TileRef here = S(a).anchor.Copy();
            TileRef orbit = n.graph.Tile(10, 10, 1);
            List<int> steps = new List<int>();
            string f;
            T.Check(!n.graph.TryRoute(here, orbit, 300, steps, out f) && f == "CrossLayer", "no route between layers");
            T.Check(!n.ctx.Spatial.DevSendTo(a, orbit), "no journey to another layer");
            S(a).destination = orbit;
            S(a).status = SpatialStatus.Travelling;
            S(a).journeyStartTick = n.clock.Now;
            S(a).lastUpdateTick = n.clock.Now;
            S(a).arrivalTick = n.clock.Now + Ticks.PerDay;
            n.Advance(Ticks.PerDay / 2);
            n.ctx.Spatial.CatchUp(a);
            T.Check(S(a).status == SpatialStatus.Blocked && S(a).blockedReason == "CrossLayer" && Same(here, S(a).anchor), "a cross-layer destination is refused; the anchor stays");
        }

        private static void AmbientSilent()
        {
            TestNet with = ContractorTests.WorldWithCast(30, 5), without = ContractorTests.WorldWithCast(30, 5);
            without.graph.ready = false;
            foreach (NetworkActor a in ContractorTests.Contractors(without)) a.Get<ContractorSimulation>().spatial = new SpatialState();
            int jw = with.journal.entries.Count, jo = without.journal.entries.Count;
            int hw = with.ledger.records.Count, ho = without.ledger.records.Count;
            with.Advance(Ticks.PerDay * 90);
            without.Advance(Ticks.PerDay * 90);
            T.Check(with.ctx.Spatial.counters.ambientJourneys > 5, "contractors moved around (" + with.ctx.Spatial.counters.ambientJourneys + " ambient journeys)");
            T.Eq(without.journal.entries.Count - jo, with.journal.entries.Count - jw, "ambient movement publishes no event");
            T.Eq(without.ledger.records.Count - ho, with.ledger.records.Count - hw, "and writes no history");
            T.Eq(without.ctx.relations.Count, with.ctx.relations.Count, "and changes no relationship");
            T.Eq(without.pay.charged, with.pay.charged, "and moves no money");
            T.Eq(0, with.ctx.contracts.contracts.Count, "and creates no hidden contract");
        }

        private static void Boundaries()
        {
            string[] leaks = { "tile", "anchor", "route", "destination", "spatial", "location", "coord" };
            foreach (Type t in new[] { typeof(ContractorCardView), typeof(ContractRowView), typeof(OfferView) })
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    T.Check(f.FieldType != typeof(TileRef), t.Name + "." + f.Name + " is not a location");
                    foreach (string w in leaks) T.Check(f.Name.ToLowerInvariant().IndexOf(w, StringComparison.Ordinal) < 0, t.Name + "." + f.Name + " does not look like spatial data");
                }
            }
            foreach (Type t in new[] { typeof(SpatialState), typeof(OperationSpatialPlan), typeof(SpatialService) })
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    string tn = f.FieldType.FullName ?? "";
                    T.Check(tn.IndexOf("Pawn", StringComparison.Ordinal) < 0 && tn.IndexOf("WorldObject", StringComparison.Ordinal) < 0 && tn.IndexOf("Caravan", StringComparison.Ordinal) < 0 && tn.IndexOf("WorldPath", StringComparison.Ordinal) < 0,
                        t.Name + "." + f.Name + ": no pawn, world object, caravan or persisted path");
                }
            }
            TestNet n = ContractorTests.WorldWithCast(30, 8);
            n.Advance(Ticks.PerDay * 60);
            T.Check(n.ctx.Spatial.counters.ambientJourneys > 0, "contractors travelled");
            T.Eq(0, n.sites.sites.Count, "no world object was created for any of them");
        }

        // ================================================================== migration (Phase 2 → 2.5)

        /// <summary>A world graph whose every answer throws: a broken adapter, or a bug in the spatial layer.</summary>
        private sealed class FaultyGraph : ISpatialWorld
        {
            private static Exception Boom() => new InvalidOperationException("test: spatial fault");
            public bool Ready => true;
            public bool IsValid(TileRef t) { throw Boom(); }
            public bool IsPassable(TileRef t) { throw Boom(); }
            public List<SettlementFacts> Settlements() { throw Boom(); }
            public bool TryFindPassableNear(TileRef center, int minDist, int maxDist, int seed, out TileRef tile) { throw Boom(); }
            public bool TryFindAnyPassable(int seed, out TileRef tile) { throw Boom(); }
            public bool TryRoute(TileRef from, TileRef to, int maxSteps, List<int> steps, out string failureKey) { throw Boom(); }
            public TileRef OnLayerOf(TileRef sameLayer, int tileId) { throw Boom(); }
            public int ApproxDistance(TileRef a, TileRef b) { throw Boom(); }
        }

        private static void FaultNeverStalls()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor team = Still(n);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
            Operation op = ProcurementTests.Op(n, c);
            T.Check(op.spatial != null && S(team).operation == op.id, "planned and bound before the fault");
            n.ctx.graph = new FaultyGraph();
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            T.Eq(ContractStatus.Fulfilled, c.status, "every checkpoint still ran: the contract completed on its Phase 2 timeline");
            T.Check(op.Find(Checkpoint.Return).done, "through the Return checkpoint");
            T.Check(n.ctx.Spatial.counters.faults > 0, "the faults were caught and counted (" + n.ctx.Spatial.counters.faults + ")");
            T.Check(c.fieldLog.Count == 0, "the Field Log was closed with the contract as usual");
            n.ctx.graph = n.graph;
            n.ctx.Spatial.Validate(null);
            T.Check(!S(team).operation.IsValid && n.graph.IsValid(S(team).anchor), "the load validator repairs what the fault left (binding released, anchor valid)");

            // A Missing outcome whose incident cannot be read: the ordinary placement still makes the site.
            TestNet m = ProcurementTests.World(0);
            m.world.factions.Add(FakeWorld.Faction(21, "Blood Hawks", "ludeon.rimworld", 4, true, true, true));
            NetworkActor lost = Still(m);
            Contract c2 = ProcurementTests.Awarded(m, ProcurementTests.Fixer(m), lost, "TestSteel", 150);
            m.ctx.graph = new FaultyGraph();
            ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
            ProcurementDevOverrides.forceSecured = 0;
            ProcurementDevOverrides.forceTroubled = SubStatus.Missing;
            ProcurementDevOverrides.forceFollowUp = true;
            ProcurementTests.RunUntil(m, () => FollowUp(m, c2) != null || c2.IsTerminal);
            T.Eq(ContractStatus.Troubled, c2.status, "the resolver's Missing outcome stands");
            T.Check(FollowUp(m, c2) != null && m.sites.tileSeeds.Count > 0, "the Last Known Location was placed the Phase 2 way");
        }

        private static void MigrationAnchor()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = ProcurementTests.Reliable(n);
            TileRef expected = S(a).anchor.Copy();
            a.Get<ContractorSimulation>().spatial = new SpatialState();
            T.Eq(SpatialStatus.Uninitialized, S(a).status, "a Phase 2 contractor loads Uninitialized");
            List<string> findings = new List<string>();
            T.Check(n.ctx.Spatial.Validate(findings) >= 1, "the load reconciliation anchors it (" + string.Join("; ", findings.ToArray()) + ")");
            T.Check(Same(expected, S(a).anchor), "deterministically: the same anchor a fresh start would give");
            T.Eq(0, S(a).journeys, "no past journey invented");
            T.Check(S(a).destination == null && S(a).status == SpatialStatus.Idle, "idle where it is");
        }

        private static void MigrationEnded()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor ended = ProcurementTests.Reliable(n), quarantined = ProcurementTests.Reliable(n);
            n.ctx.Contractors.EndActor(ended, "Test");
            ended.Get<ContractorSimulation>().spatial = new SpatialState();
            quarantined.Get<ContractorSimulation>().spatial = new SpatialState();
            quarantined.quarantinedReason = "Test";
            n.ctx.Spatial.InitializeAll();
            n.ctx.Spatial.Validate(null);
            T.Eq(SpatialStatus.Uninitialized, S(ended).status, "an ended contractor is not moved or anchored");
            T.Eq(SpatialStatus.Uninitialized, S(quarantined).status, "a quarantined one is left alone");
            n.ctx.Spatial.CatchUp(ended);
            n.Advance(Ticks.PerDay * 3);
            T.Check(true, "no crash on either");
        }

        /// <summary>A Phase 2 run with no spatial layer at all: the reference a legacy operation must match.</summary>
        private static Operation RunWithoutSpatial(out Contract c)
        {
            TestNet y = ProcurementTests.World(0);
            y.graph.ready = false;
            NetworkActor team = ProcurementTests.Reliable(y);
            team.Get<ContractorSimulation>().spatial = new SpatialState();
            c = ProcurementTests.Awarded(y, ProcurementTests.Fixer(y), team);
            Operation op = ProcurementTests.Op(y, c);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            Contract cc = c;
            ProcurementTests.RunUntil(y, () => cc.IsTerminal);
            return op;
        }

        private static void MigrationLegacyOp()
        {
            Contract refC;
            Operation refOp = RunWithoutSpatial(out refC);
            T.Check(refOp.spatial == null, "the reference ran without spatial");

            TestNet n = ProcurementTests.World(0);
            NetworkActor team = ProcurementTests.Reliable(n);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
            Operation op = ProcurementTests.Op(n, c);
            // As loaded from a Phase 2 save: no plan on the running operation, the contractor Uninitialized.
            op.spatial = null;
            team.Get<ContractorSimulation>().spatial = new SpatialState();
            n.ctx.Spatial.ClearRouteCache();
            int eta = c.terms.etaTicks, price = c.terms.price;
            T.Eq(1, n.ctx.Spatial.InitializeAll(), "the contractor is anchored after the upgrade");
            S(team).nextAmbientTick = int.MaxValue / 2;
            TileRef at = S(team).anchor.Copy();
            T.Check(!S(team).operation.IsValid, "but not bound to the old operation (no journey invented)");
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            T.Eq(refC.status, c.status, "the Phase 2 operation completes as it would have (" + c.status + ")");
            T.Check(op.spatial == null, "it never gained a plan");
            T.Eq(refOp.checkpoints.Count, op.checkpoints.Count, "same checkpoints");
            for (int i = 0; i < op.checkpoints.Count && i < refOp.checkpoints.Count; i++) T.Eq(refOp.checkpoints[i].dueTick, op.checkpoints[i].dueTick, "checkpoint " + op.checkpoints[i].key + " exactly as without spatial");
            T.Check(op.outcome.band == refOp.outcome.band && op.outcome.secured == refOp.outcome.secured && op.outcome.Killed == refOp.outcome.Killed, "the same outcome, cargo and casualties");
            T.Check(c.terms.etaTicks == eta && c.terms.price == price && c.ExternalCharged() == refC.ExternalCharged(), "quote, ETA and money unchanged");
            T.Check(Same(at, S(team).anchor), "the contractor did not move during the legacy operation");

            Contract next = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
            T.Check(ProcurementTests.Op(n, next).spatial != null, "a new operation after the upgrade is spatially integrated");
        }

        private static void MigrationVersion()
        {
            T.Eq(3, SaveMigrations.Current, "save format 3");
            NetworkState state = new NetworkState();
            MigrationContext mc = new MigrationContext();
            T.Eq(3, SaveMigrations.Run(state, 2, mc, 0), "2 → 3 runs");
            T.Check(mc.log.Exists(l => l.Contains("SpatialContinuity")), "the spatial migration is logged");
            T.Eq(0, state.diagnostics.failedMigrations.Count, "and does not fail");

            // A Phase 2 ContractorSimulation has no spatial node: it loads Uninitialized.
            ContractorSimulation sim = new ContractorSimulation();
            sim.spatial = null;
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "thenetwork-sim-" + Guid.NewGuid().ToString("N") + ".xml");
            Scribe.saver.InitSaving(path, "sim");
            try { sim.ExposeData(); } finally { Scribe.saver.FinalizeSaving(); }
            ContractorSimulation loaded = new ContractorSimulation();
            loaded.spatial = null;
            Scribe.loader.InitLoading(path);
            try { loaded.ExposeData(); } finally { Scribe.loader.FinalizeLoading(); }
            System.IO.File.Delete(path);
            T.Check(loaded.spatial != null && loaded.spatial.status == SpatialStatus.Uninitialized, "no spatial node → Uninitialized");
        }
    }
}
