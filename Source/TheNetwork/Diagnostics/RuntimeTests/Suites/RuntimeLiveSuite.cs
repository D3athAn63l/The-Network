using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Domain.Catalog;
using TheNetwork.Domain.Ports;
using TheNetwork.Integration;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Diagnostics.RuntimeTests.Suites
{
    /// <summary>
    /// RT-LIVE: READ-ONLY checks of the real integration against the currently loaded game: the catalog, the comms gate, the payment
    /// environment, the world graph, the drop plan and the live Network's invariants. It never spends, spawns, destroys or repairs
    /// anything. A colony that has no comms console or no home map is a gameplay state (SKIP or a described PASS), never a FAIL: a
    /// check fails only when an adapter contradicts the world it reads or throws.
    /// </summary>
    public static class RuntimeLiveSuite
    {
        public const string Suite = "LIVE";

        public static IEnumerable<RuntimeTestCase> Cases(IRuntimeTestHost host)
        {
            yield return RuntimeTestCase.Immediate(Suite, "RT-LIVE-001", "The real catalog holds base-game goods", CatalogHasBaseGoods);
            yield return RuntimeTestCase.Immediate(Suite, "RT-LIVE-002", "The comms gate reads the loaded colony truthfully", CommsGate);
            yield return RuntimeTestCase.Immediate(Suite, "RT-LIVE-003", "The payment environment can be inspected without spending", PaymentEnvironment);
            yield return RuntimeTestCase.Immediate(Suite, "RT-LIVE-004", "The world graph adapter reads the actual world", WorldGraph);
            yield return RuntimeTestCase.Immediate(Suite, "RT-LIVE-005", "The drop-pod plan is deterministic and spawns nothing", DropPlan);
            yield return RuntimeTestCase.Immediate(Suite, "RT-LIVE-006", "The live Network passes the read-only invariant scan", LiveInvariantScan);
        }

        // ------------------------------------------------------------------ RT-LIVE-001

        private static readonly string[] BaseGoods = { "Steel", "Plasteel", "ComponentIndustrial", "MedicineIndustrial", "WoodLog" };

        private static void CatalogHasBaseGoods(RuntimeTestContext ctx)
        {
            ItemCatalog c = CatalogCache.Get();
            int found = 0, requestable = 0;
            List<string> blocked = new List<string>();
            foreach (string def in BaseGoods)
            {
                ItemFacts f = c.Facts(def);
                if (f == null)
                {
                    ctx.Warn(def + " is not in the catalog (a mod removed it?)");
                    continue;
                }
                found++;
                ctx.Assert.True(f.marketValue > 0f && f.stackLimit >= 1, def + " has real facts (market value " + f.marketValue.ToString("0.##") + ", stack " + f.stackLimit + ")");
                string reason;
                if (c.IsRequestable(def, out reason)) requestable++;
                else blocked.Add(def + " (" + reason + ")");
            }
            ctx.Assert.AtLeast(1, found, "at least one guaranteed base-game good is in the catalog");
            ctx.Note(found + " of " + BaseGoods.Length + " base goods in the catalog, " + requestable + " requestable now" + (blocked.Count > 0 ? "; not requestable: " + string.Join(", ", blocked.ToArray()) : ""));
            if (blocked.Count > 0) ctx.Warn("some base goods are not requestable in this game: " + string.Join(", ", blocked.ToArray()) + " (an override or a runtime failure; a state, not a defect)");
        }

        // ------------------------------------------------------------------ RT-LIVE-002 / 003

        private static void CommsGate(RuntimeTestContext ctx)
        {
            CommsAccessAdapter comms = new CommsAccessAdapter();
            string reason;
            bool usable = comms.CanContact(out reason);
            if (usable) ctx.Assert.Null(reason, "a usable gate gives no reason");
            else ctx.Assert.True(reason == CommsAccessAdapter.NoConsole || reason == CommsAccessAdapter.Unpowered || reason == CommsAccessAdapter.ElectricityDisabled, "a blocked gate names one of its known reasons (" + reason + ")");
            // Cross-check against the world itself, read-only: usable exactly when a spawned console of a player home map says it can be used now.
            bool console = false, anyUsable = false;
            foreach (Map m in Find.Maps)
            {
                if (!m.IsPlayerHome) continue;
                foreach (Building_CommsConsole b in m.listerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>())
                {
                    if (!b.Spawned) continue;
                    console = true;
                    if (b.CanUseCommsNow) anyUsable = true;
                }
            }
            ctx.Assert.Equal(anyUsable, usable, "the adapter agrees with the consoles on the home maps");
            ctx.Note("comms: " + (usable ? "usable" : "blocked (" + reason + ")") + "; " + (console ? "a comms console exists" : "no comms console on a home map") + (Domain.IntelDevOverrides.commsGateOverride ? "; the dev comms override is ON (not part of this reading)" : ""));
            if (!usable && !console) ctx.Skip("this colony has no comms console: a gameplay state, reported (" + reason + ")");
        }

        private static void PaymentEnvironment(RuntimeTestContext ctx)
        {
            PaymentAdapter pay = new PaymentAdapter();
            string reason;
            ctx.Assert.True(pay.CanCharge(0, out reason), "charging nothing is always possible");
            // The beacon-reachable silver of each player home map, read with the same vanilla query the adapter uses (no silver moves).
            long best = -1;
            int homes = 0;
            foreach (Map map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                homes++;
                long silver = 0;
                foreach (Thing t in TradeUtility.AllLaunchableThingsForTrade(map)) if (t.def == ThingDefOf.Silver) silver += t.stackCount;
                if (silver > best) best = silver;
            }
            if (homes == 0)
            {
                bool any = pay.CanCharge(1, out reason);
                ctx.Assert.False(any, "with no home map nothing can be charged");
                ctx.Assert.Equal(PaymentAdapter.NoHomeMap, reason, "and the reason says so");
                ctx.Skip("no player home map to pay from: a gameplay state");
            }
            int reachable = (int)Math.Min(best, 1000000000L);
            if (reachable > 0)
            {
                ctx.Assert.True(pay.CanCharge(reachable, out reason), "the adapter says the colony can pay what it reaches (" + reachable + ")");
            }
            bool one = pay.CanCharge(reachable + 1, out reason);
            ctx.Assert.False(one, "and cannot pay one silver more than it reaches");
            ctx.Assert.Equal(PaymentAdapter.NotEnoughSilver, reason, "the refusal names the beacon silver (" + reason + ")");
            ctx.Note("payment environment: " + best + " silver reachable by beacon on " + homes + " home map(s); the adapter agrees at that figure and one above it (read without spending)");
        }

        // ------------------------------------------------------------------ RT-LIVE-004

        private static void WorldGraph(RuntimeTestContext ctx)
        {
            SpatialWorldAdapter w = new SpatialWorldAdapter();
            if (!w.Ready) ctx.Skip("no world surface yet (the world graph is not ready)");
            List<SettlementFacts> settlements = w.Settlements();
            ctx.Assert.NotNull(settlements, "the settlement list is returned");
            int player = 0, charter = 0;
            for (int i = 0; i < settlements.Count; i++)
            {
                ctx.Assert.True(w.IsValid(settlements[i].tile), "settlement " + i + " sits on a valid tile");
                if (settlements[i].player) player++;
                if (settlements[i].canProvideCharterTransport) charter++;
            }
            TileRef any;
            ctx.Assert.True(w.TryFindAnyPassable(RuntimeTestSeeds.ForTest(ctx.Id), out any), "a passable surface tile can be found");
            ctx.Assert.True(w.IsValid(any) && w.IsPassable(any), "and it is valid and passable");
            ctx.Assert.AtMost(1, w.ApproxDistance(any, any), "a tile is about zero tiles from itself");
            TileRef again;
            w.TryFindAnyPassable(RuntimeTestSeeds.ForTest(ctx.Id), out again);
            ctx.Assert.True(any.tileId == again.tileId && any.layerId == again.layerId, "the search is deterministic for a seed");
            ctx.Note(settlements.Count + " settlements (" + player + " player, " + charter + " able to provide charter transport)");
        }

        // ------------------------------------------------------------------ RT-LIVE-005

        private static readonly string[] KnownPlanFailures = { "NoHomeMap", "NoDropSpot" };

        private static void DropPlan(RuntimeTestContext ctx)
        {
            DropPodDelivery d = new DropPodDelivery();
            string label;
            int home = d.DefaultHomeMapId(out label);
            if (home < 0) ctx.Skip("no player home map: a gameplay state (the plan would say NoHomeMap)");
            Map map = null;
            foreach (Map m in Find.Maps) if (m.uniqueID == home) map = m;
            ctx.Assert.NotNull(map, "the default home map exists in the game");
            int things = map.listerThings.AllThings.Count;
            int seed = RuntimeTestSeeds.ForTest(ctx.Id);
            DeliveryPlan a = d.Plan(home, seed), b = d.Plan(home, seed);
            ctx.Assert.Equal(a.ok, b.ok, "the same inputs give the same verdict");
            ctx.Assert.Equal(things, map.listerThings.AllThings.Count, "planning spawned nothing on the map");
            if (a.ok)
            {
                ctx.Assert.True(a.cellX >= 0 && a.cellZ >= 0, "a valid plan has a drop cell (" + a.cellX + ", " + a.cellZ + ")");
                ctx.Assert.True(a.cellX == b.cellX && a.cellZ == b.cellZ && a.mapId == b.mapId, "and it is deterministic");
                ctx.Note("drop plan: map '" + a.mapLabel + "', cell (" + a.cellX + ", " + a.cellZ + ")" + (a.rerouted ? ", rerouted" : ""));
            }
            else
            {
                ctx.Assert.True(Array.IndexOf(KnownPlanFailures, a.failureKey) >= 0, "a refused plan gives a truthful, known reason (" + a.failureKey + ")");
                ctx.Assert.Equal(a.failureKey, b.failureKey, "and the same one every time");
                ctx.Note("drop plan refused: " + a.failureKey);
            }
        }

        // ------------------------------------------------------------------ RT-LIVE-006

        private static void LiveInvariantScan(RuntimeTestContext ctx)
        {
            NetworkRuntime rt = NetworkRuntime.Current;
            if (rt == null) ctx.Skip("no Network runtime in this game");
            if (!rt.Session.IsRunning) ctx.Skip("the Network has not started in this session yet (its first tick starts it); a read-only scan does not start it");
            LiveInvariants.Result r = LiveInvariants.Scan(rt.Ctx, rt.Root.ids, rt.Scheduler);
            ctx.Note("scanned " + r.Counted + " (read-only: nothing repaired)");
            foreach (string n in r.Notes) ctx.Note("note: " + n);
            if (r.Violations.Count > 0)
            {
                foreach (string v in r.Violations) ctx.Note("violation: " + v);
                ctx.Assert.Fail(r.Violations.Count + " impossible state(s) in the live Network: " + r.Violations[0]);
            }
            if (r.Notes.Count > 0) ctx.Warn(r.Notes.Count + " state(s) a validation would normally resolve (first: " + r.Notes[0] + ")");
        }
    }
}
