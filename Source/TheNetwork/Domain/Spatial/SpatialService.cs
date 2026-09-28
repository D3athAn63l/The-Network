using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Domain.Spatial
{
    /// <summary>Tuning of the spatial layer (SPATIAL § 5). Values, never formulas scattered elsewhere.</summary>
    public static class SpatialPolicy
    {
        /// <summary>Longest route the layer will represent, in world tiles.</summary>
        public const int MaxRouteSteps = 300;

        /// <summary>"Approximately around here": the derived local presence radius (never persisted).</summary>
        public const int PresenceRadius = 2;

        /// <summary>A Last Known Location is placed within this many tiles of where the trouble was.</summary>
        public const int IncidentSiteRadius = 4;

        public const float AmbientRelocateChance = 0.55f;

        /// <summary>World tiles an abstract group covers in a day, by speed band.</summary>
        public static int TilesPerDay(Band speed)
        {
            switch (speed)
            {
                case Band.VeryLow: return 5;
                case Band.Low: return 7;
                case Band.High: return 12;
                case Band.VeryHigh: return 16;
                default: return 9;
            }
        }

        public static int TicksPerTile(Band speed)
        {
            return Ticks.PerDay / TilesPerDay(speed);
        }

        /// <summary>How far, in world tiles, a contractor ranges for work, by range band.</summary>
        public static int RangeTiles(Band range)
        {
            switch (range)
            {
                case Band.VeryLow: return 6;
                case Band.Low: return 10;
                case Band.High: return 26;
                case Band.VeryHigh: return 40;
                default: return 16;
            }
        }
    }

    /// <summary>Runtime counters (not saved): the soak harness and the dev performance readout print them.</summary>
    public sealed class SpatialCounters
    {
        public int initialized;
        public int initFailed;
        public long catchUps;
        public long stepsAdvanced;
        public int routesBuilt;
        public int routesRebuilt;
        public int routeFailures;
        public int blocked;
        public int invalidDestinations;
        public int invalidAnchors;
        public int ambientJourneys;
        public int operationPlans;
        public int workRegionFallbacks;
        public int arrivals;
        public int lklNear;
        public int lklFallback;
        public int faults;

        /// <summary>A unit of movement work: a catch-up, plus the steps it advanced and the routes it built.</summary>
        public long Work => catchUps + stepsAdvanced + routesBuilt * 20L;

        public override string ToString()
        {
            return "initialized " + initialized + " (failed " + initFailed + "), catch-ups " + catchUps + ", steps " + stepsAdvanced + ", routes built " + routesBuilt
                + " (rebuilt " + routesRebuilt + ", failed " + routeFailures + "), blocked " + blocked + ", invalid destinations " + invalidDestinations + ", invalid anchors " + invalidAnchors
                + ", ambient journeys " + ambientJourneys + ", operation plans " + operationPlans + " (work-region fallbacks " + workRegionFallbacks + "), arrivals " + arrivals
                + ", LKL near incident " + lklNear + " (fallback " + lklFallback + "), faults " + faults;
        }
    }

    /// <summary>
    /// Abstract spatial continuity (SPATIAL, ADR-041, ADR-042): every NPC contractor has one hidden
    /// anchor tile, and travels coarsely and lazily between anchors. Nothing runs per tick: movement is
    /// caught up from committed timing when the contractor's daily upkeep runs and when an operation
    /// checkpoint needs its position. Only the anchor, destination and timing are persisted; the route
    /// is a runtime cache rebuilt from them. Spatial answers WHERE; operations still answer WHAT, and
    /// the operation timeline stays authoritative (spatial conforms to it, never the reverse). No pawn,
    /// no world object, no caravan, nothing player-visible.
    /// </summary>
    public sealed class SpatialService
    {
        private sealed class RouteCache
        {
            public int fromTile;
            public int layer;
            public int toTile;
            public List<int> steps;
            public int index;

            public int CurrentTile => index == 0 ? fromTile : steps[index - 1];
        }

        private readonly DomainContext ctx;
        private readonly Dictionary<int, RouteCache> routes = new Dictionary<int, RouteCache>();
        public readonly SpatialCounters counters = new SpatialCounters();
        private string lastRouteFailure;

        public SpatialService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        private ISpatialWorld Graph => ctx.graph;

        private bool GraphReady => ctx.graph != null && ctx.graph.Ready;

        /// <summary>Runtime route caches currently held (disposable; rebuilt on demand).</summary>
        public int CachedRoutes => routes.Count;

        /// <summary>Drops every runtime route (as a load does). Positions and destinations are untouched.</summary>
        public void ClearRouteCache()
        {
            routes.Clear();
        }

        // ================================================================== fail-soft entry points

        // Everything Phase 2 code, start-up and the load validator call goes through these. Spatial answers
        // only WHERE, so a fault in it is logged once, counted and swallowed: it never stops the upkeep,
        // checkpoint, consequence or load that called it, and an operation keeps its Phase 2 timeline
        // (the load validator repairs whatever spatial state was left half-updated).

        /// <summary>
        /// Gives an NPC contractor its first anchor (see <see cref="EnsureInitializedCore"/>). False when
        /// world data is not available yet, or on a fault.
        /// </summary>
        public bool EnsureInitialized(NetworkActor a)
        {
            try { return EnsureInitializedCore(a); }
            catch (Exception ex) { return Fault("init", a?.id.Value ?? 0, ex, false); }
        }

        /// <summary>Initializes every active NPC contractor still Uninitialized (start-up, after load). Returns how many.</summary>
        public int InitializeAll()
        {
            try { return InitializeAllCore(); }
            catch (Exception ex) { return Fault("initAll", 0, ex, 0); }
        }

        /// <summary>From the contractor's own staggered daily upkeep: catch up, then maybe relocate.</summary>
        public void Upkeep(NetworkActor a)
        {
            try { UpkeepCore(a); }
            catch (Exception ex) { Fault("upkeep", a?.id.Value ?? 0, ex, 0); }
        }

        /// <summary>A new operation: its origin and hidden work region (see <see cref="BeginOperationCore"/>).</summary>
        public void BeginOperation(Operation op, NetworkActor a, Contract c, ItemFacts f)
        {
            try { BeginOperationCore(op, a, c, f); }
            catch (Exception ex) { Fault("begin", OpKey(op), ex, 0); }
        }

        public void OnCheckpoint(Operation op)
        {
            try { OnCheckpointCore(op); }
            catch (Exception ex) { Fault("checkpoint", OpKey(op), ex, 0); }
        }

        public void ArriveAtWork(Operation op)
        {
            try { ArriveAtWorkCore(op); }
            catch (Exception ex) { Fault("arrive", OpKey(op), ex, 0); }
        }

        public void RecordIncident(Operation op)
        {
            try { RecordIncidentCore(op); }
            catch (Exception ex) { Fault("incident", OpKey(op), ex, 0); }
        }

        public void StartReturn(Operation op)
        {
            try { StartReturnCore(op); }
            catch (Exception ex) { Fault("return", OpKey(op), ex, 0); }
        }

        public void ReturnFromWork(Operation op)
        {
            try { ReturnFromWorkCore(op); }
            catch (Exception ex) { Fault("back", OpKey(op), ex, 0); }
        }

        public void EndOperation(Operation op)
        {
            try { EndOperationCore(op); }
            catch (Exception ex) { Fault("end", OpKey(op), ex, 0); }
        }

        public void OnActorEnded(NetworkActor a)
        {
            try { OnActorEndedCore(a); }
            catch (Exception ex) { Fault("actorEnded", a?.id.Value ?? 0, ex, 0); }
        }

        /// <summary>Where a Last Known Location should be; null (the old placement is used) on a fault.</summary>
        public TileRef IncidentTile(Operation op)
        {
            try { return IncidentTileCore(op); }
            catch (Exception ex) { return Fault<TileRef>("incidentTile", OpKey(op), ex, null); }
        }

        /// <summary>Load reconciliation (see <see cref="ValidateCore"/>). Returns the number of repairs.</summary>
        public int Validate(List<string> findings)
        {
            try { return ValidateCore(findings); }
            catch (Exception ex)
            {
                findings?.Add("Spatial reconciliation failed and was skipped: " + ex.Message);
                return Fault("validate", 0, ex, 0);
            }
        }

        private static int OpKey(Operation op) => op == null ? 0 : op.id.Value;

        private T Fault<T>(string what, int key, Exception ex, T fallback)
        {
            counters.faults++;
            NetLog.ErrorOnce(LogCategory.Spatial, "spatial.fault." + what + "." + key,
                "Spatial '" + what + "' failed for " + key + "; the Phase 2 flow continues without it: " + ex);
            return fallback;
        }

        // ================================================================== initialization

        /// <summary>
        /// Gives an NPC contractor its first anchor, deterministically from its seed and the world
        /// (SPATIAL § 3): near a settlement of its live origin faction, else near another non-player
        /// settlement, else any passable tile. Never at a player colony, never a claim on the settlement.
        /// False when world data is not available yet (it stays Uninitialized and is tried again later).
        /// </summary>
        private bool EnsureInitializedCore(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null) return false;
            SpatialState s = sim.spatial;
            if (s.IsInitialized) return true;
            if (!GraphReady) return false;
            TileRef anchor = ChooseInitialAnchor(a, sim);
            if (anchor == null)
            {
                counters.initFailed++;
                NetLog.WarnOnce(LogCategory.Spatial, "spatial.init." + a.id.Value, a.name.Display + ": no valid world tile for a spatial anchor yet.");
                return false;
            }
            int now = ctx.Now;
            s.status = SpatialStatus.Idle;
            s.anchor = anchor;
            s.destination = null;
            s.journeyOrigin = null;
            s.purpose = SpatialPurpose.None;
            s.operation = OperationId.None;
            s.journeyStartTick = -1;
            s.arrivalTick = -1;
            s.lastUpdateTick = now;
            s.initializedTick = now;
            s.blockedReason = null;
            s.nextAmbientTick = now + new NetRng(a.seed, "spatial.firstAmbient").RangeInclusive(5, 25) * Ticks.PerDay;
            counters.initialized++;
            return true;
        }

        /// <summary>Initializes every active NPC contractor still Uninitialized (start-up, after load). Returns how many.</summary>
        private int InitializeAllCore()
        {
            if (!GraphReady) return 0;
            int n = 0;
            List<NetworkActor> all = ctx.actors.actors;
            for (int i = 0; i < all.Count; i++)
            {
                NetworkActor a = all[i];
                if (!ContractorService.IsNpcContractor(a) || a.status != ActorStatus.Active || a.quarantinedReason != null) continue;
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                if (sim == null || sim.spatial.IsInitialized) continue;
                if (EnsureInitialized(a)) n++;
            }
            return n;
        }

        private TileRef ChooseInitialAnchor(NetworkActor a, ContractorSimulation sim)
        {
            NetRng rng = new NetRng(a.seed, "spatial.init");
            List<SettlementFacts> all = Graph.Settlements();
            List<SettlementFacts> candidates = new List<SettlementFacts>();
            int origin = sim.origin != null && !sim.originLost ? sim.origin.loadId : -1;
            if (origin >= 0)
            {
                for (int i = 0; i < all.Count; i++) if (!all[i].player && all[i].factionLoadId == origin && Graph.IsValid(all[i].tile)) candidates.Add(all[i]);
            }
            if (candidates.Count == 0)
            {
                for (int i = 0; i < all.Count; i++) if (!all[i].player && Graph.IsValid(all[i].tile)) candidates.Add(all[i]);
            }
            if (candidates.Count > 0)
            {
                SettlementFacts pick = candidates[rng.Range(0, candidates.Count)];
                TileRef near;
                // Around the settlement, not on it: the contractor operates in the area, it owns nothing.
                if (Graph.TryFindPassableNear(pick.tile, 1, 3, NetHash.Combine(a.seed, "spatial.init.near"), out near)) return near;
                if (Graph.IsPassable(pick.tile)) return pick.tile.Copy();
            }
            TileRef any;
            return Graph.TryFindAnyPassable(NetHash.Combine(a.seed, "spatial.init.any"), out any) ? any : null;
        }

        // ================================================================== catch-up

        /// <summary>
        /// Brings the contractor's position up to now from its committed journey (SPATIAL § 5). Cheap when
        /// it is not travelling; builds a route only when a journey needs one and the runtime cache has
        /// none. Progress is proportional to time between the last update and the committed arrival, and
        /// fractional progress is kept, so a slow journey never jumps at the end. At or after the arrival
        /// tick the contractor is at the destination (the destination is never rerolled).
        /// </summary>
        public void CatchUp(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null) return;
            SpatialState s = sim.spatial;
            if (!s.IsInitialized && !EnsureInitializedCore(a)) return;
            if (!GraphReady) return;
            counters.catchUps++;
            int now = ctx.Now;
            if (!Graph.IsValid(s.anchor))
            {
                RecoverAnchor(a, sim);
                return;
            }
            if (s.destination == null)
            {
                if (s.status == SpatialStatus.Travelling) s.status = SpatialStatus.Idle;
                return;
            }
            if (!Graph.IsValid(s.destination) || s.destination.layerId != s.anchor.layerId)
            {
                counters.invalidDestinations++;
                Block(a, sim, Graph.IsValid(s.destination) ? "CrossLayer" : "DestinationInvalid");
                return;
            }
            int from = Math.Max(s.lastUpdateTick, s.journeyStartTick);
            if (now <= from) return;
            if (now >= s.arrivalTick)
            {
                Arrive(a, sim);
                return;
            }
            RouteCache r = Route(a, s);
            if (r == null)
            {
                Block(a, sim, lastRouteFailure ?? "NoRoute");
                return;
            }
            int remaining = r.steps.Count - r.index;
            if (remaining <= 0)
            {
                Arrive(a, sim);
                return;
            }
            long span = Math.Max(1, s.arrivalTick - from);
            int advance = (int)(remaining * (long)(now - from) / span);
            if (advance <= 0) return;
            if (advance >= remaining)
            {
                Arrive(a, sim);
                return;
            }
            r.index += advance;
            s.anchor = Graph.OnLayerOf(s.anchor, r.steps[r.index - 1]);
            // The time at which the last step was reached, so the remainder is never lost to rounding.
            s.lastUpdateTick = (int)(from + advance * span / remaining);
            counters.stepsAdvanced += advance;
        }

        private RouteCache Route(NetworkActor a, SpatialState s)
        {
            RouteCache r;
            bool had = routes.TryGetValue(a.id.Value, out r);
            if (had && r.toTile == s.destination.tileId && r.layer == s.destination.layerId && r.CurrentTile == s.anchor.tileId) return r;
            List<int> steps = new List<int>();
            string failure;
            if (!Graph.TryRoute(s.anchor, s.destination, SpatialPolicy.MaxRouteSteps, steps, out failure))
            {
                routes.Remove(a.id.Value);
                counters.routeFailures++;
                lastRouteFailure = failure;
                return null;
            }
            counters.routesBuilt++;
            // A journey already under way (cache lost at load, or stale): rebuilt from the persisted anchor.
            if (had || s.lastUpdateTick > s.journeyStartTick) counters.routesRebuilt++;
            r = new RouteCache { fromTile = s.anchor.tileId, layer = s.anchor.layerId, toTile = s.destination.tileId, steps = steps, index = 0 };
            routes[a.id.Value] = r;
            return r;
        }

        private void Arrive(NetworkActor a, ContractorSimulation sim)
        {
            SpatialState s = sim.spatial;
            int now = ctx.Now;
            s.anchor = s.destination.Copy();
            s.destination = null;
            s.journeyStartTick = -1;
            s.arrivalTick = -1;
            s.lastUpdateTick = now;
            s.blockedReason = null;
            routes.Remove(a.id.Value);
            counters.arrivals++;
            switch (s.purpose)
            {
                case SpatialPurpose.Outbound:
                    s.status = SpatialStatus.OnAssignment;
                    break;
                case SpatialPurpose.Return:
                    s.status = SpatialStatus.Idle;
                    break;
                default:
                    s.status = SpatialStatus.Idle;
                    s.purpose = SpatialPurpose.None;
                    s.nextAmbientTick = now + new NetRng(a.seed, "spatial.rest", s.journeys).RangeInclusive(10, 40) * Ticks.PerDay;
                    break;
            }
        }

        /// <summary>
        /// A journey that cannot be represented: the contractor stays at its last valid anchor (no
        /// teleport). An ambient journey is dropped; an operation leg keeps the operation timeline and
        /// records the degradation on the plan. Diagnostics only; no contract state changes.
        /// </summary>
        private void Block(NetworkActor a, ContractorSimulation sim, string reason)
        {
            SpatialState s = sim.spatial;
            counters.blocked++;
            routes.Remove(a.id.Value);
            NetLog.Info(LogCategory.Spatial, a.name.Display + ": journey to " + s.destination + " cannot be represented (" + reason + "); staying at " + s.anchor + ".");
            s.destination = null;
            s.journeyStartTick = -1;
            s.arrivalTick = -1;
            s.lastUpdateTick = ctx.Now;
            s.status = SpatialStatus.Blocked;
            s.blockedReason = reason;
            if (s.purpose == SpatialPurpose.Ambient)
            {
                s.purpose = SpatialPurpose.None;
                s.nextAmbientTick = ctx.Now + 5 * Ticks.PerDay;
            }
            Operation op = s.operation.IsValid ? ctx.operations.Get(s.operation) : null;
            if (op?.spatial != null && op.spatial.fallbackKey == null) op.spatial.fallbackKey = reason;
        }

        /// <summary>The anchor no longer resolves (a layer or world change): anchored again, deterministically.</summary>
        private void RecoverAnchor(NetworkActor a, ContractorSimulation sim)
        {
            counters.invalidAnchors++;
            SpatialState s = sim.spatial;
            NetLog.Info(LogCategory.Spatial, a.name.Display + ": spatial anchor " + s.anchor + " is no longer valid; anchoring again.");
            routes.Remove(a.id.Value);
            OperationId bound = s.operation;
            SpatialPurpose purpose = s.purpose;
            s.status = SpatialStatus.Uninitialized;
            s.anchor = null;
            if (!EnsureInitializedCore(a)) return;
            // Still with its operation, where it now is.
            s.operation = bound;
            s.purpose = bound.IsValid ? purpose : SpatialPurpose.None;
            if (bound.IsValid) s.status = SpatialStatus.OnAssignment;
        }

        // ================================================================== daily upkeep and ambient relocation

        /// <summary>From the contractor's own staggered daily upkeep: catch up, then maybe relocate.</summary>
        private void UpkeepCore(NetworkActor a)
        {
            CatchUp(a);
            MaybeRelocate(a);
        }

        /// <summary>
        /// Ambient relocation (SPATIAL § 5.3): an idle contractor with no work occasionally moves its
        /// operating area to another plausible place in its range. Committed once chosen; never a hidden
        /// contract, payment, casualty, history record, letter or log entry.
        /// </summary>
        public bool MaybeRelocate(NetworkActor a)
        {
            return MaybeRelocate(a, false);
        }

        private bool MaybeRelocate(NetworkActor a, bool force)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || !GraphReady) return false;
            SpatialState s = sim.spatial;
            if (!s.IsInitialized || s.destination != null || s.operation.IsValid || sim.commitments.Count > 0) return false;
            if (s.status != SpatialStatus.Idle && s.status != SpatialStatus.Blocked) return false;
            int now = ctx.Now;
            if (s.nextAmbientTick > now) return false;
            Availability av = ctx.Contractors.AvailabilityOf(a);
            if (av == Availability.Recovering || av == Availability.Ended || av == Availability.Unavailable)
            {
                s.nextAmbientTick = now + 3 * Ticks.PerDay;
                return false;
            }
            NetRng rng = new NetRng(a.seed, "spatial.ambient." + s.journeys, now / Ticks.PerDay);
            if (!rng.Chance(SpatialPolicy.AmbientRelocateChance) && !force)
            {
                s.nextAmbientTick = now + rng.RangeInclusive(10, 30) * Ticks.PerDay;
                return false;
            }
            TileRef dest = ChooseAmbientDestination(a, sim, rng);
            return dest != null && StartJourney(a, sim, dest, SpatialPurpose.Ambient, now, -1, rng);
        }

        /// <summary>Starts a journey now. The arrival follows the route length and the speed band unless given.</summary>
        private bool StartJourney(NetworkActor a, ContractorSimulation sim, TileRef dest, SpatialPurpose purpose, int start, int arrival, NetRng rng)
        {
            SpatialState s = sim.spatial;
            int now = ctx.Now;
            if (dest.tileId == s.anchor.tileId && dest.layerId == s.anchor.layerId)
            {
                if (purpose == SpatialPurpose.Ambient) s.nextAmbientTick = now + (rng != null ? rng.RangeInclusive(10, 30) : 15) * Ticks.PerDay;
                return false;
            }
            List<int> steps = new List<int>();
            string failure;
            if (!Graph.TryRoute(s.anchor, dest, SpatialPolicy.MaxRouteSteps, steps, out failure))
            {
                counters.routeFailures++;
                if (purpose == SpatialPurpose.Ambient) s.nextAmbientTick = now + 5 * Ticks.PerDay;
                return false;
            }
            counters.routesBuilt++;
            routes[a.id.Value] = new RouteCache { fromTile = s.anchor.tileId, layer = s.anchor.layerId, toTile = dest.tileId, steps = steps, index = 0 };
            s.journeyOrigin = s.anchor.Copy();
            s.destination = dest.Copy();
            s.purpose = purpose;
            s.status = SpatialStatus.Travelling;
            s.journeyStartTick = start;
            s.lastUpdateTick = start;
            s.arrivalTick = arrival > start ? arrival : start + Math.Max(1, steps.Count) * SpatialPolicy.TicksPerTile(sim.mobility.speedBand);
            s.blockedReason = null;
            s.journeys++;
            if (purpose == SpatialPurpose.Ambient) counters.ambientJourneys++;
            return true;
        }

        private TileRef ChooseAmbientDestination(NetworkActor a, ContractorSimulation sim, NetRng rng)
        {
            SpatialState s = sim.spatial;
            int range = Math.Max(3, (int)(SpatialPolicy.RangeTiles(sim.mobility.rangeBand) * 0.6f));
            int origin = sim.origin != null && !sim.originLost ? sim.origin.loadId : -1;
            List<SettlementFacts> all = Graph.Settlements();
            List<SettlementFacts> near = new List<SettlementFacts>();
            List<float> weights = new List<float>();
            for (int i = 0; i < all.Count; i++)
            {
                SettlementFacts f = all[i];
                if (f.player) continue;
                int d = Graph.ApproxDistance(s.anchor, f.tile);
                if (d < 3 || d > range) continue;
                near.Add(f);
                weights.Add(f.factionLoadId == origin ? 3f : 1f);
            }
            int seed = NetHash.Combine(NetHash.Combine(a.seed, "spatial.ambient.dest"), s.journeys);
            TileRef dest;
            if (near.Count > 0)
            {
                SettlementFacts pick = near[rng.WeightedIndex(weights.ToArray())];
                // Around the settlement, and still within the contractor's range.
                if (Graph.TryFindPassableNear(pick.tile, 1, SpatialPolicy.PresenceRadius, seed, out dest) && Graph.ApproxDistance(s.anchor, dest) <= range) return dest;
                if (Graph.IsPassable(pick.tile)) return pick.tile.Copy();
            }
            return Graph.TryFindPassableNear(s.anchor, 3, range, seed, out dest) ? dest : null;
        }

        // ================================================================== operations

        /// <summary>
        /// A new operation starts (SPATIAL § 6): the contractor is caught up, its real anchor becomes the
        /// origin, and a hidden work region is committed from the operation's own seed, scaled by the time
        /// the committed timeline leaves for travel (the ETA is never changed). The main body sets out at
        /// the Prep checkpoint and is at the work region by Arrive. A second concurrent job of an
        /// organization is a detachment: it gets a plan, the main body's anchor does not move. Without
        /// world data the operation simply runs on its Phase 2 timeline (no plan).
        /// </summary>
        private void BeginOperationCore(Operation op, NetworkActor a, Contract c, ItemFacts f)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || op == null) return;
            CatchUp(a);
            SpatialState s = sim.spatial;
            if (!s.IsInitialized || !GraphReady) return;
            OperationSpatialPlan plan = new OperationSpatialPlan { origin = s.anchor.Copy() };
            Operation bound = s.operation.IsValid ? ctx.operations.Get(s.operation) : null;
            plan.detached = bound != null && bound != op && !bound.IsFinished;
            Checkpoint prep = op.Find(Checkpoint.Prep), arrive = op.Find(Checkpoint.Arrive);
            int depart = prep?.dueTick ?? op.startedTick;
            int arriveAt = Math.Max(depart + 1, arrive?.dueTick ?? depart + 1);
            plan.workRegion = ChooseWorkRegion(a, sim, op, c, f, plan.origin, arriveAt - depart);
            if (plan.workRegion == null)
            {
                plan.workRegion = plan.origin.Copy();
                plan.fallbackKey = "NoWorkRegion";
                counters.workRegionFallbacks++;
            }
            plan.returnTo = plan.origin.Copy();
            op.spatial = plan;
            counters.operationPlans++;
            if (plan.detached) return;

            // The main body goes. An ambient journey in progress stops where it is (already caught up).
            routes.Remove(a.id.Value);
            s.operation = op.id;
            s.destination = null;
            s.purpose = SpatialPurpose.Outbound;
            s.blockedReason = null;
            if (SameTile(plan.workRegion, s.anchor))
            {
                s.status = SpatialStatus.OnAssignment;
                s.journeyStartTick = -1;
                s.arrivalTick = -1;
                s.lastUpdateTick = ctx.Now;
                return;
            }
            if (!StartJourney(a, sim, plan.workRegion, SpatialPurpose.Outbound, depart, arriveAt, null))
            {
                // No route after all: the work happens where they are; the timeline is untouched.
                plan.workRegion = s.anchor.Copy();
                plan.fallbackKey = "NoRoute";
                counters.workRegionFallbacks++;
                s.status = SpatialStatus.OnAssignment;
                s.purpose = SpatialPurpose.Outbound;
            }
        }

        private TileRef ChooseWorkRegion(NetworkActor a, ContractorSimulation sim, Operation op, Contract c, ItemFacts f, TileRef origin, int travelTicks)
        {
            int budget = travelTicks / SpatialPolicy.TicksPerTile(sim.mobility.speedBand);
            float difficulty = f == null ? 0.5f : Math.Max(0f, Math.Min(1f, Valuation.Difficulty(f, c.Quantity)));
            int maxDist = Math.Min(SpatialPolicy.RangeTiles(sim.mobility.rangeBand), (int)Math.Round(budget * (0.5f + 0.5f * difficulty)));
            if (maxDist <= 0) return origin.Copy();
            int minDist = Math.Max(1, maxDist / 3);
            int seed = NetHash.Combine(op.seed, "spatial.work");
            NetRng rng = new NetRng(op.seed, "spatial.work");
            // A settlement in reach is a plausible place to find ordinary goods (never a claim that it has them).
            List<SettlementFacts> all = Graph.Settlements();
            List<SettlementFacts> inReach = new List<SettlementFacts>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].player) continue;
                int d = Graph.ApproxDistance(origin, all[i].tile);
                if (d >= minDist && d <= maxDist) inReach.Add(all[i]);
            }
            TileRef t;
            if (inReach.Count > 0 && rng.Chance(0.7f))
            {
                SettlementFacts pick = inReach[rng.Range(0, inReach.Count)];
                if (Graph.TryFindPassableNear(pick.tile, 0, 1, seed, out t) && Graph.ApproxDistance(origin, t) <= maxDist && Reachable(origin, t)) return t;
            }
            return Graph.TryFindPassableNear(origin, minDist, maxDist, seed, out t) ? t : null;
        }

        private bool Reachable(TileRef from, TileRef to)
        {
            List<int> steps = new List<int>();
            string failure;
            return Graph.TryRoute(from, to, SpatialPolicy.MaxRouteSteps, steps, out failure);
        }

        /// <summary>A checkpoint that reads position: the bound contractor is caught up first.</summary>
        private void OnCheckpointCore(Operation op)
        {
            NetworkActor a = BoundActor(op);
            if (a != null) CatchUp(a);
        }

        /// <summary>
        /// The Arrive checkpoint: the operation says they are there, so they are. A journey the timeline
        /// has completed (or a checkpoint run early from the dev menu) ends at the committed work region.
        /// </summary>
        private void ArriveAtWorkCore(Operation op)
        {
            NetworkActor a = BoundActor(op);
            if (a == null) return;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            CatchUp(a);
            SpatialState s = sim.spatial;
            if (s.purpose == SpatialPurpose.Outbound && s.destination != null && SameTile(s.destination, op.spatial.workRegion)) Arrive(a, sim);
            if (s.status != SpatialStatus.Blocked) s.status = SpatialStatus.OnAssignment;
        }

        /// <summary>
        /// The resolver has just committed the outcome (still WHAT; this is only WHERE). Trouble or disaster
        /// happened at the work area: that tile becomes the incident, before any consequence reads it,
        /// and the group stays there.
        /// </summary>
        private void RecordIncidentCore(Operation op)
        {
            if (op?.spatial == null || op.outcome == null) return;
            if (op.outcome.troubledKey == null && op.outcome.band != OutcomeBand.Disaster) return;
            NetworkActor a = BoundActor(op);
            if (a == null)
            {
                if (op.spatial.incident == null) op.spatial.incident = op.spatial.workRegion?.Copy();
                return;
            }
            CatchUp(a);
            op.spatial.incident = a.Get<ContractorSimulation>().spatial.anchor?.Copy();
        }

        /// <summary>
        /// After the Resolve checkpoint (and any delay it committed): a group that is not in trouble heads
        /// back, arriving when the Return checkpoint is due. A later change of that checkpoint is followed
        /// at the checkpoint itself; there is no separate spatial delay.
        /// </summary>
        private void StartReturnCore(Operation op)
        {
            if (op?.spatial == null || op.outcome == null || op.IsFinished || op.spatial.incident != null) return;
            if (op.outcome.troubledKey != null || op.outcome.band == OutcomeBand.Disaster) return;
            NetworkActor a = BoundActor(op);
            if (a == null) return;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            CatchUp(a);
            SpatialState s = sim.spatial;
            Checkpoint ret = op.Find(Checkpoint.Return);
            int arrival = ret?.dueTick ?? ctx.Now + 1;
            TileRef home = op.spatial.returnTo;
            if (home == null || !Graph.IsValid(home) || SameTile(home, s.anchor)) return;
            if (!StartJourney(a, sim, home, SpatialPurpose.Return, ctx.Now, Math.Max(ctx.Now + 1, arrival), null))
            {
                // They stay near the work area; the operation's own timeline is unaffected.
                if (op.spatial.fallbackKey == null) op.spatial.fallbackKey = "NoReturnRoute";
            }
        }

        /// <summary>The Return checkpoint: back where they were heading (a late return follows the delayed checkpoint).</summary>
        private void ReturnFromWorkCore(Operation op)
        {
            NetworkActor a = BoundActor(op);
            if (a == null) return;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            CatchUp(a);
            SpatialState s = sim.spatial;
            if (s.purpose == SpatialPurpose.Return && s.destination != null) Arrive(a, sim);
        }

        /// <summary>
        /// The operation ended (finished, aborted, written off). The contractor stays where it is now
        /// (an aborted journey stops mid-way; a group in trouble stays at the incident) and is idle again,
        /// resting a while before any ambient move.
        /// </summary>
        private void EndOperationCore(Operation op)
        {
            if (op == null) return;
            NetworkActor a = ctx.actors.Get(op.contractor);
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || sim.spatial.operation != op.id) return;
            CatchUp(a);
            SpatialState s = sim.spatial;
            routes.Remove(a.id.Value);
            s.operation = OperationId.None;
            s.destination = null;
            s.purpose = SpatialPurpose.None;
            s.journeyStartTick = -1;
            s.arrivalTick = -1;
            s.lastUpdateTick = ctx.Now;
            if (s.IsInitialized) s.status = SpatialStatus.Idle;
            s.nextAmbientTick = Math.Max(s.nextAmbientTick, ctx.Now + new NetRng(a.seed, "spatial.afterOp", s.journeys).RangeInclusive(3, 10) * Ticks.PerDay);
        }

        /// <summary>The actor ended (death, dissolution): its last position stays as truth for later phases.</summary>
        private void OnActorEndedCore(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || !sim.spatial.IsInitialized) return;
            CatchUp(a);
            SpatialState s = sim.spatial;
            routes.Remove(a.id.Value);
            s.destination = null;
            s.journeyStartTick = -1;
            s.arrivalTick = -1;
            if (s.status == SpatialStatus.Travelling) s.status = SpatialStatus.Idle;
        }

        /// <summary>
        /// Where a Last Known Location should be (SPATIAL § 7): the recorded incident, else where the main
        /// body is now, else the work region. Null for an operation without a spatial plan (the old
        /// placement is used).
        /// </summary>
        private TileRef IncidentTileCore(Operation op)
        {
            if (op?.spatial == null) return null;
            if (op.spatial.incident != null && Graph != null && Graph.IsValid(op.spatial.incident)) return op.spatial.incident;
            NetworkActor a = BoundActor(op);
            if (a != null)
            {
                CatchUp(a);
                TileRef here = a.Get<ContractorSimulation>().spatial.anchor;
                if (here != null) return here;
            }
            return op.spatial.workRegion != null && Graph != null && Graph.IsValid(op.spatial.workRegion) ? op.spatial.workRegion : null;
        }

        /// <summary>The contractor whose main body is on this operation (null for a detachment or a legacy operation).</summary>
        private NetworkActor BoundActor(Operation op)
        {
            if (op?.spatial == null || op.spatial.detached) return null;
            NetworkActor a = ctx.actors.Get(op.contractor);
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            return sim != null && sim.spatial.operation == op.id ? a : null;
        }

        private static bool SameTile(TileRef a, TileRef b)
        {
            return a != null && b != null && a.tileId == b.tileId && a.layerId == b.layerId;
        }

        // ================================================================== dev (diagnostics only; exact tiles allowed here)

        /// <summary>Dev: an idle contractor sets out for the given tile now (an ordinary ambient journey).</summary>
        public bool DevSendTo(NetworkActor a, TileRef dest)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || dest == null) return false;
            CatchUp(a);
            if (!sim.spatial.IsInitialized || sim.spatial.operation.IsValid) return false;
            routes.Remove(a.id.Value);
            sim.spatial.destination = null;
            return StartJourney(a, sim, dest, SpatialPurpose.Ambient, ctx.Now, -1, null);
        }

        /// <summary>Dev: the ambient decision is taken now, and it moves.</summary>
        public bool DevRelocateNow(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null) return false;
            CatchUp(a);
            sim.spatial.nextAmbientTick = ctx.Now;
            return MaybeRelocate(a, true);
        }

        /// <summary>Dev: the destination stops resolving (as a world change would); the next catch-up recovers.</summary>
        public bool DevInvalidateDestination(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim?.spatial.destination == null) return false;
            sim.spatial.destination = new TileRef { tileId = -1, layerId = sim.spatial.destination.layerId, layerDef = sim.spatial.destination.layerDef };
            return true;
        }

        /// <summary>Dev: a running operation's work region moves to the given tile (the timeline is untouched).</summary>
        public bool DevRetarget(Operation op, TileRef work)
        {
            if (op?.spatial == null || work == null || op.IsFinished) return false;
            op.spatial.workRegion = work.Copy();
            NetworkActor a = BoundActor(op);
            if (a == null) return true;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            CatchUp(a);
            SpatialState s = sim.spatial;
            if (s.purpose == SpatialPurpose.Outbound && s.destination != null)
            {
                routes.Remove(a.id.Value);
                s.destination = work.Copy();
            }
            return true;
        }

        public string DevDescribe(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null) return "(not an NPC contractor)";
            SpatialState s = sim.spatial;
            RouteCache r;
            string route = routes.TryGetValue(a.id.Value, out r) ? "cached route " + (r.steps.Count - r.index) + " of " + r.steps.Count + " steps left" : "no cached route";
            string dist = s.destination != null && Graph != null ? ", " + Graph.ApproxDistance(s.anchor, s.destination) + " tiles to go" : "";
            return a.name.Display + " [" + a.id + "]: " + s + ", purpose " + s.purpose + ", operation " + s.operation + ", origin " + (s.journeyOrigin?.ToString() ?? "-")
                + ", departs " + s.journeyStartTick + ", updated " + s.lastUpdateTick + ", next ambient " + s.nextAmbientTick + ", journeys " + s.journeys
                + (s.blockedReason != null ? ", blocked: " + s.blockedReason : "") + "; " + route + dist + "; speed " + sim.mobility.speedBand + " (" + SpatialPolicy.TilesPerDay(sim.mobility.speedBand) + " tiles/day), range " + sim.mobility.rangeBand;
        }

        // ================================================================== validation (load)

        /// <summary>
        /// Load reconciliation: Uninitialized contractors are anchored; invalid anchors are anchored again;
        /// invalid destinations are dropped where they stand (an operation keeps its timeline); a binding
        /// to an operation that has ended is released. Returns the number of repairs.
        /// </summary>
        private int ValidateCore(List<string> findings)
        {
            int repairs = 0;
            List<NetworkActor> all = ctx.actors.actors;
            for (int i = 0; i < all.Count; i++)
            {
                NetworkActor a = all[i];
                if (!ContractorService.IsNpcContractor(a) || a.status != ActorStatus.Active || a.quarantinedReason != null) continue;
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                if (sim == null) continue;
                SpatialState s = sim.spatial;
                if (!s.IsInitialized)
                {
                    if (EnsureInitializedCore(a))
                    {
                        repairs++;
                        findings?.Add("Contractor " + a.id + ": spatial state initialized at " + s.anchor + ".");
                    }
                    continue;
                }
                if (!GraphReady) continue;
                if (!Graph.IsValid(s.anchor))
                {
                    RecoverAnchor(a, sim);
                    repairs++;
                    findings?.Add("Contractor " + a.id + ": invalid spatial anchor; anchored again.");
                    continue;
                }
                if (s.destination != null && (!Graph.IsValid(s.destination) || s.destination.layerId != s.anchor.layerId))
                {
                    counters.invalidDestinations++;
                    Block(a, sim, "DestinationInvalid");
                    repairs++;
                    findings?.Add("Contractor " + a.id + ": invalid spatial destination dropped; stays at " + s.anchor + ".");
                }
                if (s.operation.IsValid)
                {
                    Operation op = ctx.operations.Get(s.operation);
                    if (op == null || op.IsFinished)
                    {
                        s.operation = OperationId.None;
                        s.purpose = SpatialPurpose.None;
                        s.destination = null;
                        s.status = SpatialStatus.Idle;
                        repairs++;
                        findings?.Add("Contractor " + a.id + ": released from ended operation.");
                    }
                }
            }
            return repairs;
        }
    }
}
