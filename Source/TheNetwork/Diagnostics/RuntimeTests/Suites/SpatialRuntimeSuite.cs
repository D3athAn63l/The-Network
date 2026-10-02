using System;
using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.Domain.Spatial;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Diagnostics.RuntimeTests.Suites
{
    /// <summary>
    /// RT-SPAT: the Phase 2.5 spatial continuity and charter transport inside the sandbox, on the synthetic grid world: integration
    /// smoke regressions (the exhaustive matrix stays in the headless suite). It never touches the player's real contractors.
    /// </summary>
    public static class SpatialRuntimeSuite
    {
        public const string Suite = "SPAT";

        public static IEnumerable<RuntimeTestCase> Cases(IRuntimeTestHost host)
        {
            yield return RuntimeTestCase.Immediate(Suite, "RT-SPAT-001", "Every contractor receives valid initial hidden spatial truth", InitialTruth, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SPAT-002", "A reachable procurement goes origin, work region, return", OriginWorkReturn, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SPAT-003", "The Field Log says 'reached the area' when the contractor actually arrived", ArrivedWhenTrue, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SPAT-004", "A blocked or late arrival never creates a false arrival", NoFalseArrival, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SPAT-005", "An unreachable-by-ground destination plans a committed charter", CharterPlanned, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SPAT-006", "The used outbound charter is the committed way back", CharterReturns, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SPAT-007", "Losing the provider after the outbound charter degrades safely", ProviderLost, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SPAT-008", "The Last Known Location agrees with the hidden spatial truth", LklAgrees, true);
        }

        private static SpatialState S(NetworkActor a) { return RuntimeTestSandbox.Sim(a).spatial; }

        private static bool Same(TileRef a, TileRef b) { return a != null && b != null && a.tileId == b.tileId && a.layerId == b.layerId; }

        private static int Beats(Contract c, string key)
        {
            int n = 0;
            foreach (FieldLogEntry e in c.fieldLog) if (e.key == key) n++;
            return n;
        }

        private static int Walk(RuntimeTestSandbox sb, TileRef from, TileRef to)
        {
            List<int> steps = new List<int>();
            string f;
            return sb.Graph.TryRoute(from, to, SpatialPolicy.MaxRouteSteps, steps, out f) ? steps.Count : -1;
        }

        private static void Force(OutcomeBand band, bool notTroubled = true)
        {
            ProcurementDevOverrides.forceBand = band;
            ProcurementDevOverrides.forceNotTroubled = notTroubled;
        }

        // ------------------------------------------------------------------ RT-SPAT-001

        private static void InitialTruth(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            int made = sb.AddGeneratedCast(25);
            ctx.Assert.AtLeast(20, made, "a generated cast was instantiated (" + made + ")");
            RuntimeTestSandbox twin = new RuntimeTestSandbox(ctx.Id);
            try
            {
                twin.AddGeneratedCast(25);
                List<NetworkActor> a = Contractors(sb), b = Contractors(twin);
                ctx.Assert.Equal(a.Count, b.Count, "two worlds with the same seed have the same cast size");
                int same = 0;
                for (int i = 0; i < a.Count; i++)
                {
                    SpatialState sa = S(a[i]), sbb = S(b[i]);
                    ctx.Assert.True(sa.IsInitialized && sb.Graph.IsPassable(sa.anchor), a[i].name.Display + " has a valid passable hidden anchor");
                    if (Same(sa.anchor, sbb.anchor) && sa.nextAmbientTick == sbb.nextAmbientTick) same++;
                }
                ctx.Assert.Equal(a.Count, same, "the same seed gives the same anchors");
            }
            finally
            {
                twin.Dispose();
            }
            NetworkActor first = Contractors(sb)[0];
            TileRef before = S(first).anchor.Copy();
            ctx.Assert.True(sb.Ctx.Spatial.EnsureInitialized(first), "initialization is already done");
            ctx.Assert.True(Same(before, S(first).anchor), "it is committed once and never redone");
        }

        private static List<NetworkActor> Contractors(RuntimeTestSandbox sb)
        {
            List<NetworkActor> l = new List<NetworkActor>();
            foreach (NetworkActor a in sb.Ctx.actors.actors) if (ContractorService.IsNpcContractor(a)) l.Add(a);
            return l;
        }

        // ------------------------------------------------------------------ RT-SPAT-002 .. 004

        /// <summary>An accepted contract whose group really has to travel (the work region is not where it stands): searches fixed derived seeds.</summary>
        private static Operation Travelling(RuntimeTestContext ctx, out NetworkActor team, out Contract c)
        {
            for (int attempt = 0; attempt < 24; attempt++)
            {
                RuntimeTestSandbox sb = ctx.ReplaceSandbox(new RuntimeTestSandbox(ctx.Id, attempt));
                team = sb.AddContractor();
                c = sb.Award(sb.AddFixer(), team, RuntimeTestSandbox.Steel, 150, false);
                Operation op = sb.OpOf(c);
                if (op != null && op.spatial != null && !Same(op.spatial.workRegion, op.spatial.origin))
                {
                    ctx.Track(c);
                    ctx.Track(op);
                    ctx.Track(team);
                    ctx.Note("travelling world found at seed attempt " + attempt);
                    return op;
                }
            }
            throw new RuntimeAssertionException("No fixed seed produced a work region to travel to", "a travelling world", "none in 24 attempts");
        }

        private static void OriginWorkReturn(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor team = sb.AddContractor();
            Contract c = sb.Award(sb.AddFixer(), team, RuntimeTestSandbox.Steel, 150, false);
            Operation op = sb.OpOf(c);
            ctx.Track(c);
            ctx.Track(op);
            ctx.Track(team);
            ctx.Assert.NotNull(op.spatial, "the operation has a spatial plan");
            ctx.Assert.True(Same(op.spatial.origin, S(team).anchor) || S(team).status != SpatialStatus.Idle, "it starts from the contractor's real anchor");
            try
            {
                Force(OutcomeBand.Triumph);
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Arrive).done || c.IsTerminal, 60 * Ticks.PerDay), "the Arrive checkpoint runs");
                ctx.Assert.True(Same(op.spatial.workRegion, S(team).anchor) && S(team).status == SpatialStatus.OnAssignment, "at Arrive they are at the work region");
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Resolve).done || c.IsTerminal, 60 * Ticks.PerDay), "the Resolve checkpoint runs");
                ctx.Assert.True(S(team).purpose == SpatialPurpose.Return && Same(op.spatial.returnTo, S(team).destination), "after the work they head back");
                ctx.Assert.Equal(op.Find(Checkpoint.Return).dueTick, S(team).arrivalTick, "they arrive when the Return checkpoint is due");
                ctx.Assert.Equal(OutcomeBand.Triumph, op.outcome.band, "the resolver still decided what happened");
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Return).done || c.IsTerminal, 60 * Ticks.PerDay), "the Return checkpoint runs");
                ctx.Assert.True(Same(op.spatial.returnTo, S(team).anchor), "they are back where they return to");
                ctx.Assert.True(sb.AdvanceUntil(() => c.IsTerminal, 60 * Ticks.PerDay), "the contract closes");
            }
            finally
            {
                ctx.ClearOverrides();
            }
            ctx.Assert.Equal(ContractStatus.Fulfilled, c.status, "the drop-pod delivery path is unchanged");
            ctx.Assert.True(!S(team).operation.IsValid && S(team).status == SpatialStatus.Idle && S(team).destination == null, "idle again when the operation ends");
        }

        private static void ArrivedWhenTrue(RuntimeTestContext ctx)
        {
            NetworkActor team;
            Contract c;
            Operation op = Travelling(ctx, out team, out c);
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            try
            {
                Force(OutcomeBand.Success);
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Prep).done || c.IsTerminal, 30 * Ticks.PerDay), "preparation ends");
                ctx.Assert.Equal(0, Beats(c, FieldLogKeys.Arrived), "no 'reached the area' before they got there");
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Arrive).done || c.IsTerminal, 30 * Ticks.PerDay), "the Arrive checkpoint runs");
                sb.Ctx.Spatial.CatchUp(team);
                ctx.Assert.True(Same(S(team).anchor, op.spatial.workRegion), "spatial truth says they are at the work region");
                ctx.Assert.Equal(1, Beats(c, FieldLogKeys.Arrived), "so the Field Log says so, exactly once");
                ctx.Assert.True(sb.AdvanceUntil(() => c.IsTerminal, 60 * Ticks.PerDay), "the contract closes");
            }
            finally
            {
                ctx.ClearOverrides();
            }
        }

        private static void NoFalseArrival(RuntimeTestContext ctx)
        {
            NetworkActor team;
            Contract c;
            Operation op = Travelling(ctx, out team, out c);
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            TileRef work = op.spatial.workRegion.Copy();
            sb.AdvanceTo(op.Find(Checkpoint.Prep).dueTick + Ticks.PerHour);
            sb.Ctx.Spatial.CatchUp(team);
            sb.Ctx.Spatial.ClearRouteCache();
            // Seal the work region into a one-tile islet: still a valid passable tile, but nothing can walk to it.
            int x = sb.Graph.X(work), y = sb.Graph.Y(work);
            sb.Graph.Block(x - 2, y - 2, x + 2, y + 2);
            sb.Graph.Open(x, y, x, y);
            try
            {
                Force(OutcomeBand.Success);
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Arrive).done || c.IsTerminal, 30 * Ticks.PerDay), "the Arrive checkpoint still runs (fail-soft)");
                ctx.Assert.True(S(team).status == SpatialStatus.Blocked && !Same(S(team).anchor, work), "the group is Blocked short of the work region (" + S(team).status + ")");
                ctx.Assert.Equal(0, Beats(c, FieldLogKeys.Arrived), "so the Field Log does NOT say they reached the area");
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Resolve).done || c.IsTerminal, 30 * Ticks.PerDay), "the Resolve checkpoint runs");
                ctx.Assert.Equal(0, Beats(c, FieldLogKeys.Arrived), "not later either");
                ctx.Assert.True(sb.AdvanceUntil(() => c.IsTerminal, 60 * Ticks.PerDay), "the contract lifecycle still went on to its end (" + c.status + ")");
            }
            finally
            {
                ctx.ClearOverrides();
            }
        }

        // ------------------------------------------------------------------ RT-SPAT-005 .. 007: the charter

        private static bool OnIsland(RuntimeTestSandbox sb, TileRef t)
        {
            int x = sb.Graph.X(t), y = sb.Graph.Y(t);
            return x >= 46 && x <= 54 && y >= 6 && y <= 14;
        }

        /// <summary>
        /// The island geography: a sealed island holding the only place to work, a fast contractor on the facing shore, and a high-tech
        /// provider's settlement beside it. Searches fixed derived seeds for a world whose plan crosses by charter.
        /// </summary>
        private static Operation CharterOp(RuntimeTestContext ctx, out NetworkActor team, out Contract c)
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                RuntimeTestSandbox sb = ctx.ReplaceSandbox(new RuntimeTestSandbox(ctx.Id, attempt));
                sb.Graph.settlements.Clear();
                sb.Graph.CarveIsland();
                sb.Graph.AddSettlement(8, 20, 1, true);
                sb.Graph.AddSettlement(43, 9, 100, false, true);
                sb.Graph.AddSettlement(47, 8, 101);
                team = sb.AddContractor();
                RuntimeTestSandbox.Sim(team).mobility.speedBand = Band.VeryHigh;
                S(team).anchor = sb.Graph.Tile(42, 8);
                c = sb.Award(sb.AddFixer(), team, RuntimeTestSandbox.Steel, 150, false);
                Operation op = sb.OpOf(c);
                if (op != null && op.spatial != null && op.spatial.Charter)
                {
                    ctx.Track(c);
                    ctx.Track(op);
                    ctx.Track(team);
                    ctx.Note("charter world found at seed attempt " + attempt);
                    return op;
                }
            }
            throw new RuntimeAssertionException("No fixed seed planned a charter to the island", "a charter plan", "none in 40 attempts");
        }

        private static void CharterPlanned(RuntimeTestContext ctx)
        {
            NetworkActor team;
            Contract c;
            Operation op = CharterOp(ctx, out team, out c);
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            OperationSpatialPlan p = op.spatial;
            ctx.Assert.True(OnIsland(sb, p.workRegion) && OnIsland(sb, p.landing) && Same(p.hub, sb.Graph.Tile(43, 9)), "the work is on the island; the charter embarks at the provider's settlement and lands on the island");
            ctx.Assert.True(Walk(sb, p.origin, p.workRegion) < 0, "there is no ground route at all");
            ctx.Assert.True(Same(S(team).bridgeFrom, p.hub) && Same(S(team).bridgeTo, p.landing) && !S(team).bridged && S(team).purpose == SpatialPurpose.Outbound, "the outbound leg: walk to the hub, fly to the landing, walk on");
            ctx.Assert.Equal(0, sb.Payment.charged - c.ExternalCharged(), "no transport charge was made (only the contract's own payments)");
            ctx.Assert.Equal(0, Beats(c, FieldLogKeys.TransportArranged), "nothing is told to the player until the group sets out");
            sb.AdvanceTo(op.Find(Checkpoint.Prep).dueTick + Ticks.PerHour);
            ctx.Assert.Equal(1, Beats(c, FieldLogKeys.TransportArranged), "when it sets out the Field Log records the arrangement once");
            foreach (FieldLogEntry e in c.fieldLog)
            {
                if (e.key != FieldLogKeys.TransportArranged) continue;
                ctx.Assert.Equal(1, e.args.Count, "the beat carries only the contractor's name (no hub, landing or route)");
                ctx.Assert.Equal(op.contractorName, e.args[0], "that name");
            }
            ctx.Assert.Equal(0, sb.Payment.charged - c.ExternalCharged(), "still no transport charge");
        }

        private static void CharterReturns(RuntimeTestContext ctx)
        {
            NetworkActor team;
            Contract c;
            Operation op = CharterOp(ctx, out team, out c);
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            OperationSpatialPlan p = op.spatial;
            try
            {
                Force(OutcomeBand.Triumph);
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Arrive).done || c.IsTerminal, 60 * Ticks.PerDay), "the Arrive checkpoint runs");
                ctx.Assert.True(Same(S(team).anchor, p.workRegion) && S(team).status == SpatialStatus.OnAssignment, "they are at the island work region at Arrive");
                ctx.Assert.Equal(1, sb.Ctx.Spatial.counters.charterCrossings, "one crossing so far");
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Resolve).done || c.IsTerminal, 60 * Ticks.PerDay), "the Resolve checkpoint runs");
                ctx.Assert.Equal(OutcomeBand.Triumph, op.outcome.band, "the resolver still decided what happened");
                ctx.Assert.True(S(team).purpose == SpatialPurpose.Return && Same(S(team).bridgeFrom, p.landing) && Same(S(team).bridgeTo, p.hub) && Same(S(team).destination, p.returnTo), "two-way: the same charter picks them up at the landing and sets them down at the hub");
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Return).done || c.IsTerminal, 60 * Ticks.PerDay), "the Return checkpoint runs");
                ctx.Assert.True(Same(S(team).anchor, p.returnTo), "back home by the Return checkpoint");
                ctx.Assert.Equal(2, sb.Ctx.Spatial.counters.charterCrossings, "crossed out and back");
                ctx.Assert.True(sb.AdvanceUntil(() => c.IsTerminal, 60 * Ticks.PerDay), "the contract closes");
            }
            finally
            {
                ctx.ClearOverrides();
            }
            ctx.Assert.Equal(ContractStatus.Fulfilled, c.status, "and is delivered");
            ctx.Assert.True(p.Charter && p.charterLost == null, "the same charter plan stayed in force to the end");
        }

        private static void ProviderLost(RuntimeTestContext ctx)
        {
            NetworkActor team;
            Contract c;
            Operation op = CharterOp(ctx, out team, out c);
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            try
            {
                Force(OutcomeBand.Success);
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Arrive).done || c.IsTerminal, 60 * Ticks.PerDay), "the Arrive checkpoint runs");
                ctx.Assert.True(op.spatial.charterUsed && Same(S(team).anchor, op.spatial.workRegion), "the outbound charter was used; they are at the island work region");
                TileRef hub = op.spatial.hub.Copy(), landing = op.spatial.landing.Copy();
                // Before the return the provider is gone for good (no replacement anywhere), but a causeway exists.
                sb.Graph.settlements.RemoveAll(f => f.canProvideCharterTransport);
                sb.Graph.Open(43, 8, 46, 8);
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Resolve).done || c.IsTerminal, 60 * Ticks.PerDay), "the Resolve checkpoint runs");
                ctx.Assert.True(op.spatial.charterUsed, "history is not falsified: the outbound charter happened");
                ctx.Assert.True(Same(op.spatial.hub, hub) && Same(op.spatial.landing, landing), "its hub and landing are kept as history");
                ctx.Assert.True(!op.spatial.Charter && op.spatial.charterLost != null, "the plan says the charter is lost for the return (" + op.spatial.charterLost + ")");
                ctx.Assert.True(S(team).purpose == SpatialPurpose.Return && S(team).bridgeFrom == null && Same(S(team).destination, op.spatial.returnTo), "the return degraded to foot");
                TileRef before = S(team).anchor.Copy();
                ctx.Assert.True(sb.AdvanceUntil(() => op.Find(Checkpoint.Return).done || c.IsTerminal, 60 * Ticks.PerDay), "the Return checkpoint runs");
                sb.Ctx.Spatial.CatchUp(team);
                ctx.Assert.Equal(1, sb.Ctx.Spatial.counters.charterCrossings, "no second flight");
                ctx.Assert.True(Walk(sb, before, S(team).anchor) >= 0, "no teleport: still somewhere they could walk to");
                ctx.Assert.True(sb.AdvanceUntil(() => c.IsTerminal, 60 * Ticks.PerDay), "the contract is not stuck (" + c.status + ")");
            }
            finally
            {
                ctx.ClearOverrides();
            }
        }

        // ------------------------------------------------------------------ RT-SPAT-008

        private static void LklAgrees(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            sb.World.factions.Add(SandboxWorldFacts.Raiders());
            NetworkActor team = sb.AddContractor();
            Contract c = sb.Award(sb.AddFixer(), team, RuntimeTestSandbox.Steel, 150, false);
            Operation op = sb.OpOf(c);
            ctx.Track(c);
            ctx.Track(op);
            ctx.Track(team);
            try
            {
                ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
                ProcurementDevOverrides.forceSecured = 0;
                ProcurementDevOverrides.forceTroubled = SubStatus.Missing;
                ProcurementDevOverrides.forceFollowUp = true;
                ctx.Assert.True(sb.AdvanceUntil(() => op.outcome != null || c.IsTerminal, 60 * Ticks.PerDay), "the operation resolves");
            }
            finally
            {
                ctx.ClearOverrides();
            }
            ctx.Assert.Equal(ContractStatus.Troubled, c.status, "the group is missing");
            ctx.Assert.True(op.spatial.incident != null && Same(op.spatial.incident, op.spatial.workRegion), "the incident is where they were working");
            ctx.Assert.True(Same(op.spatial.incident, S(team).anchor), "and the contractor's last truth stays there (not reset)");
            Opportunity lkl = null;
            ctx.Assert.True(sb.AdvanceUntil(() => (lkl = FollowUp(sb, c)) != null, 3 * Ticks.PerDay), "a Last Known Location follow-up appears");
            int d = sb.Graph.ApproxDistance(op.spatial.incident, lkl.location);
            ctx.Assert.AtMost(SpatialPolicy.IncidentSiteRadius, d, "it is placed near where it happened (" + d + " tiles)");
            ctx.Assert.Equal(1, sb.Ctx.Spatial.counters.lklNear, "placed by spatial truth, not the old search");
            ctx.Assert.Equal(0, sb.Sites.nearRequests - 1, "exactly one near-incident placement request");
            ctx.Assert.AtMost(op.outcome.secured, lkl.TargetCount, "Phase 2 cargo truth is unchanged (the site never holds more than was secured)");
        }

        private static Opportunity FollowUp(RuntimeTestSandbox sb, Contract c)
        {
            foreach (Opportunity o in sb.Ctx.opportunities.opportunities) if (o.origin == OpportunityOrigin.ConsequenceRule && o.originRef.Equals(c.id.Ref)) return o;
            return null;
        }
    }
}
