using System.Collections.Generic;
using System.Text;
using LudeonTK;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Spatial;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Diagnostics
{
    /// <summary>
    /// Phase 2.5 dev actions (DEBUGGING § 3): hidden spatial truth and the Field Log. Exact tile ids are
    /// allowed here and nowhere in normal UI. Readouts go to the log; nothing is drawn on the world map
    /// and nothing is created in the world except by the explicit Last Known Location action.
    /// </summary>
    public static partial class NetworkDevActions
    {
        private const string CatSpatial = "The Network (Phase 2.5)";

        [DebugAction(CatSpatial, "Inspect contractor spatial state…", allowedGameStates = AllowedGameStates.Playing)]
        public static void InspectSpatial()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            PickContractor(rt, a => Out(rt.Ctx.Spatial.DevDescribe(a)), false);
        }

        [DebugAction(CatSpatial, "Dump all spatial states", allowedGameStates = AllowedGameStates.Playing)]
        public static void DumpSpatial()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            StringBuilder sb = new StringBuilder("[TheNetwork] Spatial states (hidden world truth)\n");
            Dictionary<SpatialStatus, int> counts = new Dictionary<SpatialStatus, int>();
            foreach (NetworkActor a in rt.State.actors.actors)
            {
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                if (sim == null) continue;
                int n;
                counts.TryGetValue(sim.spatial.status, out n);
                counts[sim.spatial.status] = n + 1;
                sb.AppendLine("  " + (a.IsActive ? "" : "(" + a.status + ") ") + rt.Ctx.Spatial.DevDescribe(a));
            }
            sb.Append("  by status:");
            foreach (KeyValuePair<SpatialStatus, int> kv in counts) sb.Append(" " + kv.Key + "=" + kv.Value);
            sb.AppendLine();
            sb.AppendLine("  counters: " + rt.Ctx.Spatial.counters + "; cached routes " + rt.Ctx.Spatial.CachedRoutes);
            Out(sb.ToString());
        }

        [DebugAction(CatSpatial, "Initialize spatial state (all Uninitialized)", allowedGameStates = AllowedGameStates.Playing)]
        public static void InitializeSpatial()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            int n = rt.Ctx.Spatial.InitializeAll();
            StateVersion.Bump();
            Out("Anchored " + n + " contractors.");
        }

        [DebugAction(CatSpatial, "Send contractor somewhere nearby now…", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceDestination()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContractor(rt, a =>
            {
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                rt.Ctx.Spatial.CatchUp(a);
                TileRef dest;
                if (sim?.spatial.anchor == null || !rt.Ctx.graph.TryFindPassableNear(sim.spatial.anchor, 5, 15, Rand.Int, out dest))
                {
                    Out("No destination found (no anchor, or nothing passable within 5–15 tiles).");
                    return;
                }
                bool ok = rt.Ctx.Spatial.DevSendTo(a, dest);
                StateVersion.Bump();
                Out(ok ? rt.Ctx.Spatial.DevDescribe(a) : a.name.Display + " cannot set out now (on an operation, or no route).");
            });
        }

        [DebugAction(CatSpatial, "Force ambient relocation now…", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceRelocation()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContractor(rt, a =>
            {
                bool ok = rt.Ctx.Spatial.DevRelocateNow(a);
                StateVersion.Bump();
                Out((ok ? "Relocating: " : "No relocation (working, recovering or no destination): ") + rt.Ctx.Spatial.DevDescribe(a));
            });
        }

        [DebugAction(CatSpatial, "Catch up one contractor now…", allowedGameStates = AllowedGameStates.Playing)]
        public static void CatchUpOne()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContractor(rt, a =>
            {
                rt.Ctx.Spatial.CatchUp(a);
                StateVersion.Bump();
                Out(rt.Ctx.Spatial.DevDescribe(a));
            });
        }

        [DebugAction(CatSpatial, "Invalidate a contractor's destination…", allowedGameStates = AllowedGameStates.Playing)]
        public static void InvalidateDestination()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContractor(rt, a =>
            {
                bool had = rt.Ctx.Spatial.DevInvalidateDestination(a);
                rt.Ctx.Spatial.CatchUp(a);
                StateVersion.Bump();
                Out((had ? "Destination invalidated and recovered: " : "No destination to invalidate: ") + rt.Ctx.Spatial.DevDescribe(a));
            });
        }

        [DebugAction(CatSpatial, "Rebuild route caches (as a load does)", allowedGameStates = AllowedGameStates.Playing)]
        public static void RebuildRoutes()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            int before = rt.Ctx.Spatial.CachedRoutes;
            rt.Ctx.Spatial.ClearRouteCache();
            Out("Dropped " + before + " cached routes; each is rebuilt from the saved anchor and destination when next needed.");
        }

        [DebugAction(CatSpatial, "Move a running operation's work region…", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceWorkRegion()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContract(rt, c => !c.IsTerminal && rt.Ctx.Procurement.CurrentOperation(c)?.spatial != null, c =>
            {
                Operation op = rt.Ctx.Procurement.CurrentOperation(c);
                TileRef work;
                if (!rt.Ctx.graph.TryFindPassableNear(op.spatial.origin, 3, 12, Rand.Int, out work))
                {
                    Out("No other work region found within 3–12 tiles of the origin.");
                    return;
                }
                rt.Ctx.Spatial.DevRetarget(op, work);
                StateVersion.Bump();
                Out(op + ": work region now " + work + " (timeline unchanged).");
            });
        }

        [DebugAction(CatSpatial, "Force Missing at the contractor's position on next resolution", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceMissingHere()
        {
            ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
            ProcurementDevOverrides.forceTroubled = SubStatus.Missing;
            ProcurementDevOverrides.forceFollowUp = true;
            Out("The next operation to resolve goes Missing where its contractor is, and a Last Known Location follows near there.");
        }

        [DebugAction(CatSpatial, "Create a Last Known Location near an operation's contractor…", allowedGameStates = AllowedGameStates.Playing)]
        public static void LklHere()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContract(rt, c => rt.Ctx.Procurement.CurrentOperation(c)?.spatial != null, c =>
            {
                DomainContext ctx = rt.Ctx;
                Operation op = ctx.Procurement.CurrentOperation(c);
                TileRef near = ctx.Spatial.IncidentTile(op);
                Domain.Ports.ItemFacts f = ctx.catalog.Facts(c.Acquire?.DefName);
                if (near == null || f == null)
                {
                    Out("No spatial truth or no item facts for " + c + ".");
                    return;
                }
                ItemPayload cargo = op.outcome != null && op.outcome.securedPayload.Count > 0 ? op.outcome.securedPayload[0] : null;
                string failure;
                Domain.Opportunities.Opportunity opp = ctx.Opportunities.GenerateFollowUp(f, cargo?.count ?? 0, cargo, NetHash.Combine(op.seed, "dev.lkl." + ctx.Now), c.id.Ref, c.lineage.depth + 1, null, near, out failure);
                StateVersion.Bump();
                Out(opp != null ? "Created " + opp.id + " at " + opp.location + " (contractor near " + near + ", " + ctx.graph.ApproxDistance(near, opp.location) + " tiles away)." : "Not created: " + failure);
            });
        }

        [DebugAction(CatSpatial, "Inspect a contract's Field Log…", allowedGameStates = AllowedGameStates.Playing)]
        public static void InspectFieldLog()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            PickContract(rt, c => c.fieldLog.Count > 0 || !c.IsTerminal, c =>
            {
                StringBuilder sb = new StringBuilder("[TheNetwork] Field Log of " + c + " (visible to the player: " + (rt.Ctx.FieldLog.Visible(c).Count > 0) + ")\n");
                foreach (FieldLogEntry e in c.fieldLog) sb.AppendLine("  " + e.tick + " " + e.key + " [" + string.Join(", ", e.args.ToArray()) + "]");
                Out(sb.ToString());
            });
        }

        [DebugAction(CatSpatial, "Spatial performance counters", allowedGameStates = AllowedGameStates.Playing)]
        public static void SpatialCounters()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            Out("[TheNetwork] Spatial counters this session: " + rt.Ctx.Spatial.counters + "; cached routes " + rt.Ctx.Spatial.CachedRoutes + ".");
        }
    }
}
