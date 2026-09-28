using System;
using System.Collections.Generic;
using System.Reflection;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.Domain.Spatial;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Tests
{
    /// <summary>
    /// Abstract charter transport (ADR-045), matrix A–N: an operation leg that cannot be walked in the
    /// time it has may cross same-layer geography by a reusable two-way charter from a high-tech
    /// provider. Ground first; never ambient; never for an invalid or cross-layer destination; no craft,
    /// no money, one Field Log beat. Synthetic grid, no RimWorld.
    /// </summary>
    public static class CharterTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Spatial.CharterBridgesDisconnectedGeography", Lifecycle));
            t.Add(new KeyValuePair<string, Action>("Charter.A_GroundFitsNoCharter", A));
            t.Add(new KeyValuePair<string, Action>("Charter.B_IslandPlanCommitted", B));
            t.Add(new KeyValuePair<string, Action>("Charter.C_LongDetourMayCharter", C));
            t.Add(new KeyValuePair<string, Action>("Charter.D_NoProviderNoCharter", D));
            t.Add(new KeyValuePair<string, Action>("Charter.E_InvalidDestinationNoCharter", E));
            t.Add(new KeyValuePair<string, Action>("Charter.F_CrossLayerNoCharter", F));
            t.Add(new KeyValuePair<string, Action>("Charter.G_SaveLoadMidCharterNoReroll", G));
            t.Add(new KeyValuePair<string, Action>("Charter.H_RouteCacheDroppedCharterSurvives", H));
            t.Add(new KeyValuePair<string, Action>("Charter.I_ProviderGoneReconcilesSafely", I));
            t.Add(new KeyValuePair<string, Action>("Charter.J_AmbientNeverCharters", J));
            t.Add(new KeyValuePair<string, Action>("Charter.K_NoTransportCharge", K));
            t.Add(new KeyValuePair<string, Action>("Charter.L_NoHiddenEconomy", L));
            t.Add(new KeyValuePair<string, Action>("Charter.M_OneFieldLogBeat", M));
            t.Add(new KeyValuePair<string, Action>("Charter.N_NoPhysicalThings", N));
        }

        private static SpatialState S(NetworkActor a) => SpatialTests.S(a);

        private static bool Same(TileRef a, TileRef b) => SpatialTests.Same(a, b);

        private static bool OnIsland(TestNet n, TileRef t)
        {
            int x = n.graph.X(t), y = n.graph.Y(t);
            return x >= 46 && x <= 54 && y >= 6 && y <= 14;
        }

        /// <summary>
        /// The island geography: a sealed island (x 46–54, y 6–14) holding the only place to work, at
        /// (47, 8); a fast contractor on the facing shore at (42, 8); optionally a high-tech provider's
        /// settlement right beside it at (43, 9) (too close to be a work candidate itself).
        /// </summary>
        internal static TestNet IslandWorld(int seed, bool provider, out NetworkActor team)
        {
            TestNet n = ProcurementTests.World(0, seed);
            n.graph.settlements.Clear();
            n.graph.CarveIsland();
            n.graph.AddSettlement(8, 20, 1, true);
            if (provider) n.graph.AddSettlement(43, 9, 100, false, true);
            n.graph.AddSettlement(47, 8, 101);
            team = SpatialTests.Still(n);
            team.Get<ContractorSimulation>().mobility.speedBand = Band.VeryHigh;
            S(team).anchor = n.graph.Tile(42, 8);
            return n;
        }

        /// <summary>An accepted contract whose operation plan crosses to the island by charter (the first world seed that plans one).</summary>
        internal static Operation CharterOp(out TestNet n, out NetworkActor team, out Contract c)
        {
            for (int seed = 1; seed <= 20; seed++)
            {
                n = IslandWorld(seed, true, out team);
                c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
                Operation op = ProcurementTests.Op(n, c);
                if (op?.spatial != null && op.spatial.Charter) return op;
            }
            T.Check(false, "some world seed plans a charter to the island");
            n = null;
            team = null;
            c = null;
            return null;
        }

        private static int Window(Operation op)
        {
            return Math.Min(op.Find(Checkpoint.Arrive).dueTick - op.Find(Checkpoint.Prep).dueTick, op.Find(Checkpoint.Return).dueTick - op.Find(Checkpoint.Resolve).dueTick);
        }

        /// <summary>The committed plan never asks for more walking than the window allows.</summary>
        private static void CheckBudget(TestNet n, NetworkActor team, Operation op, string label)
        {
            int tpt = SpatialPolicy.TicksPerTile(team.Get<ContractorSimulation>().mobility.speedBand);
            OperationSpatialPlan p = op.spatial;
            if (p.Charter)
            {
                int walk = SpatialCorrectionTests.Walk(n, p.origin, p.hub) + SpatialCorrectionTests.Walk(n, p.landing, p.workRegion);
                T.Check(walk >= 0 && walk * tpt + SpatialPolicy.CharterTicks <= Window(op), label + ": the walking legs plus the crossing fit the window (" + walk + " steps)");
            }
            else
            {
                int walk = SpatialCorrectionTests.Walk(n, p.origin, p.workRegion);
                T.Check(walk >= 0 && walk * tpt <= Window(op), label + ": the ground route fits the window (" + walk + " steps)");
            }
        }

        // ================================================================== the whole story

        private static void Lifecycle()
        {
            TestNet n;
            NetworkActor team;
            Contract c;
            Operation op = CharterOp(out n, out team, out c);
            if (op == null) return;
            OperationSpatialPlan p = op.spatial;
            T.Check(OnIsland(n, p.workRegion) && OnIsland(n, p.landing) && Same(p.hub, n.graph.Tile(43, 9)), "the work is on the island; the charter embarks at the provider's settlement and lands on the island");
            T.Check(SpatialCorrectionTests.Walk(n, p.origin, p.workRegion) < 0, "there is no ground route at all");
            T.Check(Same(S(team).bridgeFrom, p.hub) && Same(S(team).bridgeTo, p.landing) && !S(team).bridged && S(team).purpose == SpatialPurpose.Outbound, "the outbound leg: walk to the hub, fly to the landing, walk on");
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Arrive).done || c.IsTerminal);
            T.Check(Same(S(team).anchor, p.workRegion) && S(team).status == SpatialStatus.OnAssignment, "they are at the island work region at Arrive, on the Phase 2 timeline");
            T.Eq(1, n.ctx.Spatial.counters.charterCrossings, "one crossing so far");
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Resolve).done || c.IsTerminal);
            T.Check(op.outcome.band == OutcomeBand.Triumph, "the resolver still decided what happened");
            T.Check(S(team).purpose == SpatialPurpose.Return && Same(S(team).bridgeFrom, p.landing) && Same(S(team).bridgeTo, p.hub) && Same(S(team).destination, p.returnTo),
                "two-way: the same charter picks them up at the landing and sets them down at the hub");
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Return).done || c.IsTerminal);
            T.Check(Same(S(team).anchor, p.returnTo), "back home by the Return checkpoint");
            T.Eq(2, n.ctx.Spatial.counters.charterCrossings, "crossed out and back");
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            T.Eq(ContractStatus.Fulfilled, c.status, "the contract completes as in Phase 2");
        }

        // ================================================================== A–N

        private static void A()
        {
            // The same shore, but an ordinary settlement reachable on foot: never a charter while walking works.
            int plans = 0;
            for (int seed = 1; seed <= 6; seed++)
            {
                NetworkActor team;
                TestNet n = IslandWorld(seed, true, out team);
                n.graph.settlements.RemoveAll(f => n.graph.X(f.tile) == 47);
                n.graph.AddSettlement(38, 4, 101);
                Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
                Operation op = ProcurementTests.Op(n, c);
                if (op?.spatial == null) continue;
                plans++;
                T.Check(!op.spatial.Charter && S(team).bridgeFrom == null, "seed " + seed + ": a walkable plan uses no charter");
                CheckBudget(n, team, op, "seed " + seed);
            }
            T.Check(plans > 0, "plans were made");
        }

        private static void B()
        {
            int charters = 0, ground = 0;
            for (int seed = 1; seed <= 8; seed++)
            {
                NetworkActor team;
                TestNet n = IslandWorld(seed, true, out team);
                Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
                Operation op = ProcurementTests.Op(n, c);
                if (op?.spatial == null) continue;
                CheckBudget(n, team, op, "seed " + seed);
                if (op.spatial.Charter)
                {
                    charters++;
                    T.Check(OnIsland(n, op.spatial.workRegion) && OnIsland(n, op.spatial.landing), "seed " + seed + ": charter to the island, landing on it");
                    T.Check(n.graph.IsPassable(op.spatial.landing) && op.spatial.landing.layerId == op.spatial.origin.layerId, "seed " + seed + ": a valid, passable, same-layer landing");
                    T.Check(SpatialCorrectionTests.Walk(n, op.spatial.landing, op.spatial.workRegion) <= SpatialPolicy.LandingRadius * 2, "seed " + seed + ": near enough for the local leg");
                }
                else
                {
                    ground++;
                    T.Check(!OnIsland(n, op.spatial.workRegion), "seed " + seed + ": a ground plan never lands on the island");
                }
            }
            T.Check(charters > 0, "the island is reached by charter (" + charters + " charter plans, " + ground + " ground)");
        }

        private static void C()
        {
            int charters = 0;
            for (int seed = 1; seed <= 8; seed++)
            {
                NetworkActor team;
                TestNet n = SpatialCorrectionTests.DetourWorld(seed, true, out team);
                Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
                Operation op = ProcurementTests.Op(n, c);
                if (op?.spatial == null) continue;
                CheckBudget(n, team, op, "seed " + seed);
                if (!op.spatial.Charter)
                {
                    T.Check(n.graph.X(op.spatial.workRegion) >= 34, "seed " + seed + ": the far shore is never a ground plan (no hidden super-speed)");
                    continue;
                }
                charters++;
                T.Check(n.graph.X(op.spatial.workRegion) < 30 && SpatialCorrectionTests.Walk(n, op.spatial.origin, op.spatial.workRegion) > 40, "seed " + seed + ": a route exists but is far too long, so a charter crosses the bay");
                T.Check(Same(op.spatial.hub, n.graph.Tile(36, 6)), "seed " + seed + ": from the provider on their side");
            }
            T.Check(charters > 0, "a long detour may be bridged by charter (" + charters + ")");
        }

        private static void D()
        {
            int failures = 0;
            for (int seed = 1; seed <= 6; seed++)
            {
                NetworkActor team;
                TestNet n = IslandWorld(seed, false, out team);
                Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), team);
                Operation op = ProcurementTests.Op(n, c);
                if (op?.spatial == null) continue;
                T.Check(!op.spatial.Charter && !OnIsland(n, op.spatial.workRegion), "seed " + seed + ": no provider, no charter invented; the island is not reached");
                CheckBudget(n, team, op, "seed " + seed);
                failures += n.ctx.Spatial.counters.charterFailures;
                ProcurementDevOverrides.forceBand = OutcomeBand.Success;
                ProcurementTests.RunUntil(n, () => c.IsTerminal);
                T.Check(c.IsTerminal, "seed " + seed + ": the operation completes on its Phase 2 timeline (" + c.status + ")");
            }
            T.Check(failures > 0, "a charter was looked for and not found (" + failures + ")");
        }

        private static void E()
        {
            TestNet n;
            NetworkActor team;
            Contract c;
            Operation op = CharterOp(out n, out team, out c);
            if (op == null) return;
            // An invalid tile id, and a valid tile that is sea: neither is ever "reached" by charter.
            foreach (bool sea in new[] { false, true })
            {
                n.ctx.Spatial.CatchUp(team);
                int journeys = n.ctx.Spatial.counters.charterJourneys;
                TileRef before = S(team).anchor.Copy();
                S(team).destination = sea ? n.graph.Tile(45, 10) : new TileRef { tileId = -1, layerId = 0, layerDef = "Surface" };
                n.ctx.Spatial.ClearRouteCache();
                n.Advance(Ticks.PerHour);
                n.ctx.Spatial.CatchUp(team);
                T.Check(S(team).destination == null && S(team).bridgeFrom == null && S(team).status == SpatialStatus.Blocked, (sea ? "sea" : "invalid") + " destination: dropped, Blocked, no charter");
                T.Check(Same(before, S(team).anchor) || SpatialCorrectionTests.Walk(n, before, S(team).anchor) >= 0, (sea ? "sea" : "invalid") + ": no jump");
                T.Eq(journeys, n.ctx.Spatial.counters.charterJourneys, (sea ? "sea" : "invalid") + ": no charter journey was started");
                if (!sea)
                {
                    // Start over with a fresh charter world for the second case.
                    op = CharterOp(out n, out team, out c);
                    if (op == null) return;
                    n.AdvanceTo(op.Find(Checkpoint.Prep).dueTick + Ticks.PerHour);
                }
            }
        }

        private static void F()
        {
            TestNet n;
            NetworkActor team;
            Contract c;
            Operation op = CharterOp(out n, out team, out c);
            if (op == null) return;
            n.graph.layers[1] = "Orbit";
            n.AdvanceTo(op.Find(Checkpoint.Prep).dueTick + Ticks.PerHour);
            n.ctx.Spatial.CatchUp(team);
            int journeys = n.ctx.Spatial.counters.charterJourneys;
            S(team).destination = n.graph.Tile(47, 8, 1);
            n.ctx.Spatial.ClearRouteCache();
            n.Advance(Ticks.PerHour);
            n.ctx.Spatial.CatchUp(team);
            T.Check(S(team).destination == null && S(team).bridgeFrom == null && S(team).blockedReason == "CrossLayer", "a cross-layer destination is refused, never chartered (" + S(team).blockedReason + ")");
            T.Eq(journeys, n.ctx.Spatial.counters.charterJourneys, "no charter journey");
            T.Eq(0, S(team).anchor.layerId, "still on the surface");
        }

        private static void G()
        {
            TestNet n;
            NetworkActor team;
            Contract c;
            Operation op = CharterOp(out n, out team, out c);
            if (op == null) return;
            n.AdvanceTo(op.Find(Checkpoint.Prep).dueTick + 1);
            n.ctx.Spatial.CatchUp(team);
            SpatialState before = S(team);
            TileRef hub = op.spatial.hub.Copy(), landing = op.spatial.landing.Copy(), work = op.spatial.workRegion.Copy(), anchor = before.anchor.Copy();
            int arrival = before.arrivalTick;
            T.Check(before.bridgeFrom != null && !before.bridged, "saved before the crossing");

            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            n.ctx.Spatial.ClearRouteCache();
            int built = n.ctx.Spatial.counters.routesBuilt;
            NetworkActor la = n.ctx.actors.Get(team.id);
            Operation lop = n.ctx.operations.Get(op.id);
            SpatialState after = S(la);
            T.Check(Same(hub, lop.spatial.hub) && Same(landing, lop.spatial.landing) && Same(work, lop.spatial.workRegion), "the committed hub, landing and work region survive (no reroll)");
            T.Check(Same(anchor, after.anchor) && Same(hub, after.bridgeFrom) && Same(landing, after.bridgeTo) && !after.bridged && after.arrivalTick == arrival && Same(work, after.destination),
                "the charter leg survives exactly: anchor, both charter ends, not yet crossed, same arrival");
            n.AdvanceTo(arrival + 1);
            n.ctx.Spatial.CatchUp(la);
            T.Check(Same(work, after.anchor) && after.destination == null, "after the reload they still cross and arrive, on time");
            T.Check(n.ctx.Spatial.counters.routesBuilt > built, "the runtime route was built again from the saved charter truth");
        }

        private static void H()
        {
            TestNet x, y;
            NetworkActor tx, ty;
            Contract cx, cy;
            Operation ox = CharterOp(out x, out tx, out cx), oy = CharterOp(out y, out ty, out cy);
            if (ox == null || oy == null) return;
            T.Check(Same(ox.spatial.landing, oy.spatial.landing) && Same(ox.spatial.workRegion, oy.spatial.workRegion), "twins");
            int end = ox.Find(Checkpoint.Arrive).dueTick;
            while (x.clock.Now < end)
            {
                x.Advance(Ticks.PerHour * 3);
                y.Advance(Ticks.PerHour * 3);
                y.ctx.Spatial.ClearRouteCache();
                x.ctx.Spatial.CatchUp(tx);
                y.ctx.Spatial.CatchUp(ty);
                T.Check(Same(S(tx).anchor, S(ty).anchor) && S(tx).bridged == S(ty).bridged, "tick " + x.clock.Now + ": dropping the route cache changes nothing (" + S(tx).anchor + " / " + S(ty).anchor + ")");
            }
            T.Check(Same(ox.spatial.workRegion, S(ty).anchor), "the charter truth survived every drop and they arrived");
        }

        private static void I()
        {
            // The provider goes while they walk to it. With another provider in reach: a new charter from where they are.
            TestNet n;
            NetworkActor team;
            Contract c;
            Operation op = CharterOp(out n, out team, out c);
            if (op == null) return;
            n.graph.AddSettlement(40, 12, 102, false, true);
            n.AdvanceTo(op.Find(Checkpoint.Prep).dueTick + 1);
            n.ctx.Spatial.CatchUp(team);
            TileRef oldHub = op.spatial.hub.Copy();
            n.graph.settlements.RemoveAll(f => Same(f.tile, oldHub));
            n.Advance(Ticks.PerHour);
            n.ctx.Spatial.CatchUp(team);
            T.Check(op.spatial.Charter && !Same(op.spatial.hub, oldHub) && Same(op.spatial.hub, n.graph.Tile(40, 12)), "reconciled to the other provider (" + op.spatial.hub + ")");
            T.Check(Same(S(team).bridgeFrom, op.spatial.hub) && !S(team).bridged, "the leg now embarks there");
            T.Check(n.ctx.Spatial.counters.charterReplans > 0, "counted as a charter replan");
            T.Check(!OnIsland(n, S(team).anchor), "no teleport onto the island");
            ProcurementDevOverrides.forceBand = OutcomeBand.Success;
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            T.Check(c.IsTerminal, "the operation is not corrupted (" + c.status + ")");

            // No provider left at all: they stay where they are; the operation still completes.
            Operation op2 = CharterOp(out n, out team, out c);
            if (op2 == null) return;
            n.AdvanceTo(op2.Find(Checkpoint.Prep).dueTick + 1);
            n.ctx.Spatial.CatchUp(team);
            TileRef here = S(team).anchor.Copy();
            n.graph.settlements.RemoveAll(f => f.canProvideCharterTransport);
            n.Advance(Ticks.PerHour);
            n.ctx.Spatial.CatchUp(team);
            T.Check(S(team).status == SpatialStatus.Blocked && S(team).destination == null && Same(here, S(team).anchor), "no provider: Blocked where they stood (" + S(team).blockedReason + ")");
            ProcurementDevOverrides.forceBand = OutcomeBand.Success;
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            T.Check(c.IsTerminal, "the operation still completes (" + c.status + ")");
            T.Check(!OnIsland(n, S(team).anchor), "and they never appeared on the island");
        }

        private static void J()
        {
            NetworkActor a;
            TestNet n = IslandWorld(3, true, out a);
            for (int round = 0; round < 15; round++)
            {
                if (n.ctx.Spatial.DevRelocateNow(a))
                {
                    T.Check(S(a).bridgeFrom == null && !OnIsland(n, S(a).destination), "round " + round + ": ambient movement is on foot, never to the island");
                    n.AdvanceTo(S(a).arrivalTick + 1);
                    n.ctx.Spatial.CatchUp(a);
                }
            }
            T.Eq(0, n.ctx.Spatial.counters.charterJourneys, "no ambient charter");
            T.Check(!OnIsland(n, S(a).anchor), "never on the island");
        }

        private static void K()
        {
            TestNet n;
            NetworkActor team;
            Contract c;
            Operation op = CharterOp(out n, out team, out c);
            if (op == null) return;
            int silver = n.pay.silver, charged = n.pay.charged, chargeCalls = n.pay.chargeCalls, records = c.ledger.Count;
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            // Both chartered legs, out and back, up to the moment the Return checkpoint settles the job.
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Resolve).done || c.IsTerminal);
            n.AdvanceTo(op.Find(Checkpoint.Return).dueTick - 1);
            n.ctx.Spatial.CatchUp(team);
            T.Eq(2, n.ctx.Spatial.counters.charterCrossings, "they crossed out and back");
            T.Eq(silver, n.pay.silver, "no silver left the player's stockpile for the charter");
            T.Eq(charged, n.pay.charged, "no charge");
            T.Eq(chargeCalls, n.pay.chargeCalls, "not even an attempted charge");
            T.Eq(records, c.ledger.Count, "no money record on the contract during the chartered legs");
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            long paid = 0, back = 0;
            foreach (MoneyRecord m in c.ledger)
            {
                if (m.direction == MoneyDirection.PlayerPaid) paid += m.silver;
                if (m.direction == MoneyDirection.PlayerRefunded) back += m.silver;
            }
            T.Eq((long)n.pay.charged, paid, "every silver the player paid is on the contract's own ledger (the Phase 2 terms), nothing for transport");
            T.Eq((long)n.pay.refunded, back, "and every refund");
        }

        private static void L()
        {
            TestNet n;
            NetworkActor team;
            Contract c;
            Operation op = CharterOp(out n, out team, out c);
            if (op == null) return;
            n.AdvanceTo(op.Find(Checkpoint.Prep).dueTick + 1);
            int contracts = n.ctx.contracts.contracts.Count, offers = n.ctx.contracts.offers.Count, opps = n.ctx.opportunities.opportunities.Count;
            int history = n.ledger.records.Count, sites = n.sites.sites.Count, characters = n.ctx.characters.characters.Count;
            n.AdvanceTo(op.Find(Checkpoint.Arrive).dueTick - 1);
            n.ctx.Spatial.CatchUp(team);
            T.Check(S(team).bridged || S(team).bridgeFrom != null, "during the chartered leg");
            n.AdvanceTo(op.Find(Checkpoint.Arrive).dueTick);
            T.Eq(contracts, n.ctx.contracts.contracts.Count, "no hidden contract");
            T.Eq(offers, n.ctx.contracts.offers.Count, "no hidden offer or vendor");
            T.Eq(opps, n.ctx.opportunities.opportunities.Count, "no opportunity");
            T.Eq(history, n.ledger.records.Count, "no history record");
            T.Eq(sites, n.sites.sites.Count, "no site");
            T.Eq(characters, n.ctx.characters.characters.Count, "no pilot, crew or passenger records");
        }

        private static void M()
        {
            TestNet n;
            NetworkActor team;
            Contract c;
            Operation op = CharterOp(out n, out team, out c);
            if (op == null) return;
            ProcurementDevOverrides.forceBand = OutcomeBand.Success;
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Arrive).done || c.IsTerminal);
            int beats = 0;
            foreach (FieldLogEntry e in c.fieldLog)
            {
                if (e.key != FieldLogKeys.TransportArranged) continue;
                beats++;
                T.Eq(1, e.args.Count, "only the contractor's name");
                T.Eq(op.contractorName, e.args[0], "the name");
            }
            T.Eq(1, beats, "exactly one transport beat");
            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            Contract lc = n.ctx.contracts.Get(c.id);
            ProcurementTests.RunUntil(n, () => lc.IsTerminal || n.ctx.operations.Get(op.id).Find(Checkpoint.Return).done);
            beats = 0;
            foreach (FieldLogEntry e in lc.fieldLog) if (e.key == FieldLogKeys.TransportArranged) beats++;
            T.Check(beats <= 1, "still one after a reload and the return pickup (" + beats + ")");
            for (int i = 1; i < lc.fieldLog.Count; i++) T.Check(!lc.fieldLog[i].SameAs(lc.fieldLog[i - 1]), "no duplicated line");
        }

        private static void N()
        {
            TestNet n;
            NetworkActor team;
            Contract c;
            Operation op = CharterOp(out n, out team, out c);
            if (op == null) return;
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementTests.RunUntil(n, () => op.Find(Checkpoint.Return).done || c.IsTerminal);
            T.Eq(2, n.ctx.Spatial.counters.charterCrossings, "a full chartered round trip");
            T.Eq(0, n.sites.sites.Count, "no world object, site, shuttle or map marker was created");
            foreach (Type t in new[] { typeof(SpatialState), typeof(OperationSpatialPlan), typeof(SettlementFacts) })
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    string tn = f.FieldType.FullName ?? "";
                    foreach (string w in new[] { "Pawn", "WorldObject", "Caravan", "Thing", "Shuttle", "Skyfaller", "WorldPath", "Transporter" })
                        T.Check(tn.IndexOf(w, StringComparison.Ordinal) < 0, t.Name + "." + f.Name + ": no " + w);
                }
            }
        }
    }
}
