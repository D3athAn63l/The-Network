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

        /// <summary>
        /// Candidates found cheaply by approximate distance are proven with real route queries only for a
        /// short list of this many (never an all-settlement search).
        /// </summary>
        public const int SearchShortlist = 3;

        /// <summary>
        /// Arranging and flying an abstract charter (ADR-045): the time a chartered crossing takes out of
        /// the travel window. The flight itself is abstract and never walked.
        /// </summary>
        public const int CharterTicks = Ticks.PerDay / 2;

        /// <summary>A charter sets down (and later picks up) within this many tiles of the work region.</summary>
        public const int LandingRadius = 2;

        /// <summary>Candidate work regions a plan may try to reach by charter (each costs a few route queries).</summary>
        public const int CharterAttempts = 2;

        /// <summary>A chartered crossing, in steps of the contractor's own pace (progress spacing only).</summary>
        public static int BridgeUnits(Band speed)
        {
            return Math.Max(1, CharterTicks / TicksPerTile(speed));
        }

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

    /// <summary>
    /// The guaranteed fallback search over one layer's tiles (SPATIAL § 3): a few seeded probes first
    /// (cheap on an ordinary planet), then a scan of every tile from a seeded offset, so a passable tile
    /// is found whenever one exists, however sparse the land. Deterministic (no camera, no global RNG);
    /// the same seed never retries the same failed sample set forever. A one-time initialization cost,
    /// never per tick.
    /// </summary>
    public static class SpatialSearch
    {
        public const int Probes = 400;

        /// <summary>The first passable tile id found, or -1 when the layer has none.</summary>
        public static int FirstPassable(int count, int seed, Func<int, bool> passable)
        {
            if (count <= 0 || passable == null) return -1;
            NetRng rng = new NetRng(seed, "spatial.any");
            for (int i = 0; i < Probes; i++)
            {
                int id = rng.Range(0, count);
                if (passable(id)) return id;
            }
            int start = (int)((uint)seed % (uint)count);
            for (int i = 0; i < count; i++)
            {
                int id = start + i;
                if (id >= count) id -= count;
                if (passable(id)) return id;
            }
            return -1;
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

        /// <summary>Journeys made later than committed because the contractor could not cover the route sooner (never faster).</summary>
        public int lateArrivals;

        /// <summary>Troubled operations the Phase 2 lifecycle declared recovered: the group is put back where it returns to.</summary>
        public int reconciled;

        /// <summary>Journeys started on foot (ambient and operation legs) and with a chartered crossing.</summary>
        public int groundJourneys;
        public int charterJourneys;

        /// <summary>Operation plans committed with a charter; searches that found no provider or landing; legs replanned from current truth.</summary>
        public int charterPlans;
        public int charterFailures;
        public int charterReplans;
        public int charterCrossings;

        /// <summary>Operation legs that could no longer be proven and were planned again from current truth (on foot or by charter).</summary>
        public int replans;

        /// <summary>A unit of movement work: a catch-up, plus the steps it advanced and the routes it built.</summary>
        public long Work => catchUps + stepsAdvanced + routesBuilt * 20L;

        public override string ToString()
        {
            return "initialized " + initialized + " (failed " + initFailed + "), catch-ups " + catchUps + ", steps " + stepsAdvanced + ", routes built " + routesBuilt
                + " (rebuilt " + routesRebuilt + ", failed " + routeFailures + "), blocked " + blocked + ", invalid destinations " + invalidDestinations + ", invalid anchors " + invalidAnchors
                + ", ambient journeys " + ambientJourneys + ", operation plans " + operationPlans + " (work-region fallbacks " + workRegionFallbacks + "), arrivals " + arrivals
                + " (late " + lateArrivals + "), recovered and reconciled " + reconciled + ", journeys on foot " + groundJourneys + ", chartered " + charterJourneys
                + " (plans " + charterPlans + ", crossings " + charterCrossings + ", charter replans " + charterReplans + ", no provider or landing " + charterFailures + ")"
                + ", legs replanned " + replans + ", LKL near incident " + lklNear + " (fallback " + lklFallback + "), faults " + faults;
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
        /// <summary>
        /// A runtime route (never saved). A chartered leg is one sequence: the walk to the hub, the crossing
        /// (<see cref="bridgeUnits"/> entries: waiting at the hub, then set down at the landing), and the
        /// walk on to the destination.
        /// </summary>
        private sealed class RouteCache
        {
            public int fromTile;
            public int layer;
            public int toTile;
            public List<int> steps;
            public int index;

            /// <summary>Index of the first crossing entry, and how many there are (0 for a ground leg).</summary>
            public int bridgeStart;
            public int bridgeUnits;

            public int CurrentTile => index == 0 ? fromTile : steps[index - 1];

            /// <summary>Reached once <see cref="index"/> gets here: the group has been set down on the far side.</summary>
            public int LandedIndex => bridgeUnits == 0 ? -1 : bridgeStart + bridgeUnits;

            /// <summary>Steps still to be walked (the crossing is flown, not walked).</summary>
            public int GroundLeft
            {
                get
                {
                    int left = steps.Count - index;
                    if (bridgeUnits == 0) return left;
                    int flown = Math.Max(0, Math.Min(bridgeUnits, bridgeStart + bridgeUnits - Math.Max(index, bridgeStart)));
                    return left - flown;
                }
            }
        }

        private readonly DomainContext ctx;
        private readonly Dictionary<int, RouteCache> routes = new Dictionary<int, RouteCache>();
        public readonly SpatialCounters counters = new SpatialCounters();
        private string lastRouteFailure;

        // Runtime diagnostics (not saved): when each contractor last changed place for a stated reason
        // other than walking (re-anchoring, a lifecycle reconciliation). The soak uses it to tell an
        // explained discontinuity from an impossible jump.
        private readonly Dictionary<int, int> explainedJumps = new Dictionary<int, int>();

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

        /// <summary>Diagnostics: the last tick this contractor changed place for a stated non-walking reason (-1 if never).</summary>
        public int LastExplainedJump(int actorId)
        {
            int t;
            return explainedJumps.TryGetValue(actorId, out t) ? t : -1;
        }

        private void MarkExplainedJump(NetworkActor a)
        {
            explainedJumps[a.id.Value] = ctx.Now;
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

        /// <summary>Phase 2 declared a Troubled operation's group found and back (see <see cref="OnTroubledRecoveredCore"/>).</summary>
        public void OnTroubledRecovered(Operation op)
        {
            try { OnTroubledRecoveredCore(op); }
            catch (Exception ex) { Fault("recovered", OpKey(op), ex, 0); }
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
            ClearLeg(s);
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
                int first = rng.Range(0, candidates.Count);
                TileRef near;
                // Around a settlement, never on it: the contractor operates in the area, it owns nothing. A
                // settlement with no free tile around it is passed over for the next one.
                for (int k = 0; k < Math.Min(SpatialPolicy.SearchShortlist, candidates.Count); k++)
                {
                    SettlementFacts pick = candidates[(first + k) % candidates.Count];
                    if (Graph.TryFindPassableNear(pick.tile, 1, 3, NetHash.Combine(NetHash.Combine(a.seed, "spatial.init.near"), k), out near)) return near;
                }
            }
            TileRef any;
            return Graph.TryFindAnyPassable(NetHash.Combine(a.seed, "spatial.init.any"), out any) ? any : null;
        }

        // ================================================================== catch-up

        /// <summary>
        /// Brings the contractor's position up to now from its committed journey (SPATIAL § 5). Cheap when
        /// it is not travelling; builds a route only when a journey needs one and the runtime cache has
        /// none. Progress is proportional to time between the last update and the committed arrival, and
        /// fractional progress is kept, so a slow journey never jumps at the end. The remaining journey is
        /// proven before any progress, including the final arrival: a destination that is still a valid
        /// tile is no evidence it can still be reached (a cache lost at load, a world that changed), so
        /// without a route there is no arrival. An ended contractor never moves.
        /// </summary>
        public void CatchUp(NetworkActor a)
        {
            if (a == null || a.status != ActorStatus.Active) return;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            if (sim == null) return;
            if (!sim.spatial.IsInitialized && !EnsureInitializedCore(a)) return;
            Advance(a, sim);
        }

        private void Advance(NetworkActor a, ContractorSimulation sim)
        {
            SpatialState s = sim.spatial;
            if (!s.IsInitialized || !GraphReady) return;
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
            RouteCache r = Route(a, sim);
            if (r == null && ReplanLeg(a, sim)) r = Route(a, sim);
            if (r == null)
            {
                Block(a, sim, lastRouteFailure ?? "NoRoute");
                return;
            }
            from = Math.Max(s.lastUpdateTick, s.journeyStartTick);
            int remaining = r.steps.Count - r.index;
            // Never faster than the contractor moves: a route that is longer than the committed timing
            // allows (a world change, a rebuild after load) makes the journey later, never quicker. Only
            // walked steps count; a chartered crossing is flown.
            int earliest = from + r.GroundLeft * SpatialPolicy.TicksPerTile(sim.mobility.speedBand);
            if (s.arrivalTick < earliest)
            {
                s.arrivalTick = earliest;
                counters.lateArrivals++;
            }
            if (remaining <= 0 || now >= s.arrivalTick)
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
            if (!s.bridged && r.LandedIndex > 0 && r.index >= r.LandedIndex) Crossed(a, s);
            // The time at which the last step was reached, so the remainder is never lost to rounding.
            s.lastUpdateTick = (int)(from + advance * span / remaining);
            counters.stepsAdvanced += advance;
        }

        /// <summary>
        /// The runtime route of the current leg: the cached one while it still matches the saved truth, else
        /// rebuilt from the persisted anchor, destination and (for a chartered leg not yet crossed) the
        /// committed charter ends. Null when the leg can no longer be proven (no route, a charter end that
        /// no longer resolves, the provider gone).
        /// </summary>
        private RouteCache Route(NetworkActor a, ContractorSimulation sim)
        {
            SpatialState s = sim.spatial;
            RouteCache r;
            bool had = routes.TryGetValue(a.id.Value, out r);
            bool charter = s.bridgeFrom != null && !s.bridged;
            if (had && r.toTile == s.destination.tileId && r.layer == s.destination.layerId && r.CurrentTile == s.anchor.tileId)
            {
                // A cached ground leg (or one already past its crossing) stands; a crossing still ahead
                // needs its charter ends and provider to still be there.
                bool crossingAhead = r.bridgeUnits > 0 && r.index < r.LandedIndex;
                if (!charter && !crossingAhead) return r;
                if (charter && crossingAhead && CharterEndsFailure(s.anchor, s.destination, s.bridgeFrom, s.bridgeTo) == null) return r;
            }
            string failure;
            r = BuildRoute(s.anchor, s.destination, charter ? s.bridgeFrom : null, charter ? s.bridgeTo : null, SpatialPolicy.MaxRouteSteps, sim.mobility.speedBand, out failure);
            if (r == null)
            {
                routes.Remove(a.id.Value);
                counters.routeFailures++;
                lastRouteFailure = failure;
                return null;
            }
            counters.routesBuilt++;
            // A journey already under way (cache lost at load, or stale): rebuilt from the persisted anchor.
            if (had || s.lastUpdateTick > s.journeyStartTick) counters.routesRebuilt++;
            routes[a.id.Value] = r;
            return r;
        }

        /// <summary>
        /// A leg from <paramref name="from"/> to <paramref name="to"/>, on foot, or (with both charter ends)
        /// on foot to <paramref name="bridgeFrom"/>, a chartered crossing to <paramref name="bridgeTo"/>, and on
        /// foot again. A charter needs valid, passable ends on the same layer and a provider at one of them
        /// (it never explains an invalid or cross-layer destination). Null with a reason when it cannot be proven.
        /// </summary>
        private RouteCache BuildRoute(TileRef from, TileRef to, TileRef bridgeFrom, TileRef bridgeTo, int maxSteps, Band speed, out string failure)
        {
            List<int> steps = new List<int>();
            if (bridgeFrom == null || bridgeTo == null)
            {
                if (!Graph.TryRoute(from, to, Math.Min(maxSteps, SpatialPolicy.MaxRouteSteps), steps, out failure)) return null;
                return new RouteCache { fromTile = from.tileId, layer = from.layerId, toTile = to.tileId, steps = steps };
            }
            failure = CharterEndsFailure(from, to, bridgeFrom, bridgeTo);
            if (failure != null) return null;
            List<int> after = new List<int>();
            if (!Graph.TryRoute(from, bridgeFrom, SpatialPolicy.MaxRouteSteps, steps, out failure)) return null;
            if (!Graph.TryRoute(bridgeTo, to, SpatialPolicy.MaxRouteSteps, after, out failure)) return null;
            int bridgeStart = steps.Count, units = SpatialPolicy.BridgeUnits(speed);
            for (int i = 0; i < units - 1; i++) steps.Add(bridgeFrom.tileId);
            steps.Add(bridgeTo.tileId);
            steps.AddRange(after);
            return new RouteCache { fromTile = from.tileId, layer = from.layerId, toTile = to.tileId, steps = steps, bridgeStart = bridgeStart, bridgeUnits = units };
        }

        /// <summary>Why a committed charter can no longer carry this leg, or null when it still can.</summary>
        private string CharterEndsFailure(TileRef from, TileRef to, TileRef bridgeFrom, TileRef bridgeTo)
        {
            if (!Graph.IsPassable(bridgeFrom) || !Graph.IsPassable(bridgeTo) || !Graph.IsPassable(to)) return "CharterEndInvalid";
            if (bridgeFrom.layerId != from.layerId || bridgeTo.layerId != from.layerId || to.layerId != from.layerId) return "CrossLayer";
            if (!IsCharterHub(bridgeFrom) && !IsCharterHub(bridgeTo)) return "CharterProviderGone";
            return null;
        }

        /// <summary>A live settlement of a charter-capable (high-tech) faction stands on this tile.</summary>
        private bool IsCharterHub(TileRef t)
        {
            List<SettlementFacts> all = Graph.Settlements();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].canProvideCharterTransport && !all[i].player && SameTile(all[i].tile, t)) return true;
            }
            return false;
        }

        /// <summary>The chartered crossing of the current leg is done: set down on the far side (an explained jump).</summary>
        private void Crossed(NetworkActor a, SpatialState s)
        {
            s.bridged = true;
            counters.charterCrossings++;
            MarkExplainedJump(a);
        }

        /// <summary>Ends the committed journey truth of the current leg (never the position).</summary>
        private static void ClearLeg(SpatialState s)
        {
            s.destination = null;
            s.bridgeFrom = null;
            s.bridgeTo = null;
            s.bridged = false;
            s.journeyStartTick = -1;
            s.arrivalTick = -1;
        }

        private void Arrive(NetworkActor a, ContractorSimulation sim)
        {
            SpatialState s = sim.spatial;
            int now = ctx.Now;
            if (s.bridgeFrom != null && !s.bridged) Crossed(a, s);
            s.anchor = s.destination.Copy();
            ClearLeg(s);
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
            ClearLeg(s);
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
            MarkExplainedJump(a);
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
            // Ambient movement is ground-only and never longer, in real route steps, than the ambient range.
            int range = AmbientRange(sim);
            TileRef dest = ChooseAmbientDestination(a, sim, rng, range);
            if (dest != null && StartJourney(a, sim, dest, SpatialPurpose.Ambient, now, -1, rng, range)) return true;
            TileRef ring;
            int seed = NetHash.Combine(NetHash.Combine(a.seed, "spatial.ambient.ring"), s.journeys);
            return s.destination == null && Graph.TryFindPassableNear(s.anchor, 3, range, seed, out ring)
                && StartJourney(a, sim, ring, SpatialPurpose.Ambient, now, -1, rng, range);
        }

        private static int AmbientRange(ContractorSimulation sim)
        {
            return Math.Max(3, (int)(SpatialPolicy.RangeTiles(sim.mobility.rangeBand) * 0.6f));
        }

        /// <summary>
        /// Starts a journey. The arrival follows the route length and the speed band unless given, and is
        /// never sooner than the contractor can walk the route (a given arrival that is too soon becomes
        /// later: never faster). A route longer than <paramref name="maxSteps"/> real steps is refused. With
        /// both charter ends the leg crosses by abstract charter (operation legs only, never ambient).
        /// </summary>
        private bool StartJourney(NetworkActor a, ContractorSimulation sim, TileRef dest, SpatialPurpose purpose, int start, int arrival, NetRng rng, int maxSteps = SpatialPolicy.MaxRouteSteps, TileRef bridgeFrom = null, TileRef bridgeTo = null)
        {
            if (purpose == SpatialPurpose.Ambient) bridgeFrom = bridgeTo = null;
            SpatialState s = sim.spatial;
            int now = ctx.Now;
            if (dest.tileId == s.anchor.tileId && dest.layerId == s.anchor.layerId)
            {
                if (purpose == SpatialPurpose.Ambient) s.nextAmbientTick = now + (rng != null ? rng.RangeInclusive(10, 30) : 15) * Ticks.PerDay;
                return false;
            }
            string failure;
            RouteCache r = BuildRoute(s.anchor, dest, bridgeFrom, bridgeTo, maxSteps, sim.mobility.speedBand, out failure);
            if (r == null)
            {
                counters.routeFailures++;
                if (purpose == SpatialPurpose.Ambient) s.nextAmbientTick = now + 5 * Ticks.PerDay;
                return false;
            }
            counters.routesBuilt++;
            routes[a.id.Value] = r;
            ClearLeg(s);
            s.journeyOrigin = s.anchor.Copy();
            s.destination = dest.Copy();
            s.bridgeFrom = r.bridgeUnits > 0 ? bridgeFrom.Copy() : null;
            s.bridgeTo = r.bridgeUnits > 0 ? bridgeTo.Copy() : null;
            s.purpose = purpose;
            s.status = SpatialStatus.Travelling;
            s.journeyStartTick = start;
            s.lastUpdateTick = start;
            if (r.bridgeUnits > 0) counters.charterJourneys++;
            else counters.groundJourneys++;
            // The crossing is spaced like steps of the group's own pace; only the walked steps bound the pace.
            int walk = start + Math.Max(1, r.steps.Count) * SpatialPolicy.TicksPerTile(sim.mobility.speedBand);
            if (arrival > start && arrival < walk) counters.lateArrivals++;
            s.arrivalTick = Math.Max(arrival, walk);
            s.blockedReason = null;
            s.journeys++;
            if (purpose == SpatialPurpose.Ambient) counters.ambientJourneys++;
            return true;
        }

        /// <summary>
        /// A settlement area in range, chosen cheaply by approximate distance (origin faction preferred).
        /// The caller proves it with the real route length; null when there is none.
        /// </summary>
        private TileRef ChooseAmbientDestination(NetworkActor a, ContractorSimulation sim, NetRng rng, int range)
        {
            SpatialState s = sim.spatial;
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
                // Around the settlement (never on it), and still within the contractor's range.
                if (Graph.TryFindPassableNear(pick.tile, 1, SpatialPolicy.PresenceRadius, seed, out dest) && Graph.ApproxDistance(s.anchor, dest) <= range) return dest;
            }
            return null;
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
            Checkpoint resolve = op.Find(Checkpoint.Resolve), ret = op.Find(Checkpoint.Return);
            int depart = prep?.dueTick ?? op.startedTick;
            int arriveAt = Math.Max(depart + 1, arrive?.dueTick ?? depart + 1);
            // The way back must fit the planned return window as well as the way out.
            int travel = arriveAt - depart;
            if (resolve != null && ret != null && ret.dueTick > resolve.dueTick) travel = Math.Min(travel, ret.dueTick - resolve.dueTick);
            TileRef hub, landing;
            plan.workRegion = ChooseWorkRegion(a, sim, op, c, f, plan.origin, travel, out hub, out landing);
            if (plan.workRegion == null)
            {
                plan.workRegion = plan.origin.Copy();
                plan.fallbackKey = "NoWorkRegion";
                counters.workRegionFallbacks++;
            }
            else if (hub != null)
            {
                // Committed truth: the same reusable charter carries them out and picks them up again.
                plan.hub = hub;
                plan.landing = landing;
                counters.charterPlans++;
            }
            plan.returnTo = plan.origin.Copy();
            op.spatial = plan;
            counters.operationPlans++;
            if (plan.detached) return;

            // The main body goes. An ambient journey in progress stops where it is (already caught up).
            routes.Remove(a.id.Value);
            s.operation = op.id;
            ClearLeg(s);
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
            if (!StartJourney(a, sim, plan.workRegion, SpatialPurpose.Outbound, depart, arriveAt, null, SpatialPolicy.MaxRouteSteps, plan.hub, plan.landing))
            {
                // No route after all: the work happens where they are; the timeline is untouched.
                plan.hub = null;
                plan.landing = null;
                plan.workRegion = s.anchor.Copy();
                plan.fallbackKey = "NoRoute";
                counters.workRegionFallbacks++;
                s.status = SpatialStatus.OnAssignment;
                s.purpose = SpatialPurpose.Outbound;
            }
        }

        /// <summary>
        /// The hidden work region. Approximate distance only DISCOVERS candidates cheaply; a candidate is
        /// committed only when its real route, in steps, fits what the contractor can walk in the travel
        /// window and within its range (a short hop across a bay can be a long detour on land). Null when
        /// nothing fits (the work then happens where they are).
        /// </summary>
        private TileRef ChooseWorkRegion(NetworkActor a, ContractorSimulation sim, Operation op, Contract c, ItemFacts f, TileRef origin, int travelTicks, out TileRef hub, out TileRef landing)
        {
            hub = landing = null;
            int budget = travelTicks / SpatialPolicy.TicksPerTile(sim.mobility.speedBand);
            int range = SpatialPolicy.RangeTiles(sim.mobility.rangeBand);
            int walkable = Math.Min(range, budget);
            float difficulty = f == null ? 0.5f : Math.Max(0f, Math.Min(1f, Valuation.Difficulty(f, c.Quantity)));
            int maxDist = Math.Min(range, (int)Math.Round(budget * (0.5f + 0.5f * difficulty)));
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
                int first = rng.Range(0, inReach.Count);
                List<TileRef> outOfReach = new List<TileRef>();
                for (int k = 0; k < Math.Min(SpatialPolicy.SearchShortlist, inReach.Count); k++)
                {
                    SettlementFacts pick = inReach[(first + k) % inReach.Count];
                    if (!Graph.TryFindPassableNear(pick.tile, 1, 2, NetHash.Combine(seed, k), out t)) continue;
                    if (GroundSteps(origin, t, walkable) >= 0) return t;
                    outOfReach.Add(t);
                }
                // Out of reach on foot in the time (no ground route at all, or a detour too long): an
                // abstract charter from a high-tech provider may bridge it (operation travel only).
                for (int k = 0; k < Math.Min(SpatialPolicy.CharterAttempts, outOfReach.Count); k++)
                {
                    if (PlanCharter(origin, outOfReach[k], true, null, null, travelTicks, sim.mobility.speedBand, range, NetHash.Combine(seed, "charter." + k), out hub, out landing)) return outOfReach[k];
                }
            }
            return Graph.TryFindPassableNear(origin, minDist, maxDist, seed, out t) && GroundSteps(origin, t, walkable) >= 0 ? t : null;
        }

        /// <summary>
        /// Plans an abstract charter (ADR-045) for an operation leg from <paramref name="from"/> to
        /// <paramref name="to"/> that cannot be walked in the time it has: a hub (a settlement of a
        /// charter-capable, high-tech faction) reached on foot on its side, and a landing area (set down on
        /// the way out, picked up again on the way back) near the other end, all on the same layer. On the
        /// way out (<paramref name="hubNearFrom"/>) they walk to the hub, are flown to the landing and walk
        /// to the work region; on the way back they walk to the pickup, are flown to the hub and walk home.
        /// The committed hub and landing are preferred (one reusable two-way charter); otherwise only a
        /// short list of the nearest providers is proven with real routes. Never for an invalid, impassable
        /// or cross-layer destination. The walked steps plus the crossing must fit <paramref name="windowTicks"/>
        /// (int.MaxValue: no window, the leg may be late). No money moves: the charter is part of the
        /// contractor's own quoted costs.
        /// </summary>
        private bool PlanCharter(TileRef from, TileRef to, bool hubNearFrom, TileRef preferHub, TileRef preferLanding, int windowTicks, Band speed, int range, int seed, out TileRef hub, out TileRef landing)
        {
            hub = landing = null;
            if (from == null || to == null || !Graph.IsValid(from) || !Graph.IsPassable(to) || from.layerId != to.layerId) return false;
            int tpt = SpatialPolicy.TicksPerTile(speed);
            int maxWalk = windowTicks == int.MaxValue ? SpatialPolicy.MaxRouteSteps : (windowTicks - SpatialPolicy.CharterTicks) / tpt;
            if (maxWalk < 0)
            {
                counters.charterFailures++;
                return false;
            }
            // The field end: set down near the work region on the way out; picked up near where they are on the way back.
            TileRef field = hubNearFrom ? to : from;
            TileRef land = null;
            int landWalk = -1;
            if (preferLanding != null && Graph.IsPassable(preferLanding) && preferLanding.layerId == field.layerId)
            {
                landWalk = hubNearFrom ? GroundSteps(preferLanding, to, range) : GroundSteps(from, preferLanding, range);
                if (landWalk >= 0) land = preferLanding;
            }
            if (land == null)
            {
                TileRef t;
                if (Graph.TryFindPassableNear(field, 1, SpatialPolicy.LandingRadius, seed, out t))
                {
                    landWalk = hubNearFrom ? GroundSteps(t, to, range) : GroundSteps(from, t, range);
                    if (landWalk >= 0) land = t;
                }
                if (land == null)
                {
                    land = field.Copy();
                    landWalk = 0;
                }
            }
            if (landWalk > maxWalk)
            {
                counters.charterFailures++;
                return false;
            }
            // The hub end: the committed provider first, then the few nearest providers by approximate distance.
            TileRef home = hubNearFrom ? from : to;
            List<TileRef> hubs = new List<TileRef>();
            if (preferHub != null && preferHub.layerId == home.layerId && IsCharterHub(preferHub)) hubs.Add(preferHub);
            List<SettlementFacts> all = Graph.Settlements();
            List<SettlementFacts> providers = new List<SettlementFacts>();
            for (int i = 0; i < all.Count; i++)
            {
                SettlementFacts f = all[i];
                if (!f.canProvideCharterTransport || f.player || f.tile == null || f.tile.layerId != home.layerId || !Graph.IsPassable(f.tile)) continue;
                if (SameTile(f.tile, preferHub) || SameTile(f.tile, land)) continue;
                providers.Add(f);
            }
            providers.Sort((x, y) =>
            {
                int c = Graph.ApproxDistance(home, x.tile).CompareTo(Graph.ApproxDistance(home, y.tile));
                return c != 0 ? c : x.tile.tileId.CompareTo(y.tile.tileId);
            });
            for (int i = 0; i < providers.Count && i < SpatialPolicy.SearchShortlist; i++) hubs.Add(providers[i].tile);
            int hubBudget = Math.Min(range, maxWalk - landWalk);
            for (int i = 0; i < hubs.Count; i++)
            {
                TileRef h = hubs[i];
                if (SameTile(h, land)) continue;
                int hubWalk = hubNearFrom ? GroundSteps(from, h, hubBudget) : GroundSteps(h, to, hubBudget);
                if (hubWalk < 0) continue;
                hub = h.Copy();
                landing = land.Copy();
                return true;
            }
            counters.charterFailures++;
            return false;
        }

        /// <summary>
        /// An operation leg that can no longer be proven (its route gone after a world change, a charter end
        /// or its provider gone) is planned again from the contractor's CURRENT truth: on foot if a route
        /// exists, else by charter (the committed hub and landing preferred). The committed arrival stands
        /// unless the contractor cannot make it (then later, never sooner). Ambient journeys are never
        /// replanned into a charter. False when neither works: the caller blocks the leg, and the operation
        /// timeline goes on.
        /// </summary>
        private bool ReplanLeg(NetworkActor a, ContractorSimulation sim)
        {
            SpatialState s = sim.spatial;
            if (s.purpose != SpatialPurpose.Outbound && s.purpose != SpatialPurpose.Return) return false;
            Operation op = s.operation.IsValid ? ctx.operations.Get(s.operation) : null;
            if (op?.spatial == null || op.IsFinished || s.destination == null) return false;
            TileRef dest = s.destination.Copy();
            if (!Graph.IsPassable(dest) || dest.layerId != s.anchor.layerId) return false;
            int now = ctx.Now, arrival = s.arrivalTick;
            SpatialPurpose purpose = s.purpose;
            bool outbound = purpose == SpatialPurpose.Outbound;
            if (GroundSteps(s.anchor, dest, SpatialPolicy.MaxRouteSteps) >= 0 && StartJourney(a, sim, dest, purpose, now, arrival, null))
            {
                counters.replans++;
                return true;
            }
            TileRef hub, landing;
            int seed = NetHash.Combine(op.seed, "spatial.replan." + s.journeys);
            if (!PlanCharter(s.anchor, dest, outbound, op.spatial.hub, op.spatial.landing, int.MaxValue, sim.mobility.speedBand, SpatialPolicy.RangeTiles(sim.mobility.rangeBand), seed, out hub, out landing)) return false;
            if (!StartJourney(a, sim, dest, purpose, now, arrival, null, SpatialPolicy.MaxRouteSteps, outbound ? hub : landing, outbound ? landing : hub)) return false;
            bool wasCharter = op.spatial.Charter;
            op.spatial.hub = hub;
            op.spatial.landing = landing;
            counters.replans++;
            counters.charterReplans++;
            if (outbound && !wasCharter) NoteTransport(op);
            return true;
        }

        /// <summary>The one Field Log beat a charter earns, on the player's own contract (no hub, no landing, no route).</summary>
        private void NoteTransport(Operation op)
        {
            Contract c = ctx.contracts.Get(op.contract);
            if (c != null) ctx.FieldLog?.NoteOnce(c, FieldLogKeys.TransportArranged, op.contractorName);
        }

        /// <summary>Real ground route steps from one tile to another, or -1 when there is none within <paramref name="maxSteps"/>.</summary>
        private int GroundSteps(TileRef from, TileRef to, int maxSteps)
        {
            List<int> steps = new List<int>();
            string failure;
            return Graph.TryRoute(from, to, Math.Min(maxSteps, SpatialPolicy.MaxRouteSteps), steps, out failure) ? steps.Count : -1;
        }

        /// <summary>A checkpoint that reads position: the bound contractor is caught up first.</summary>
        private void OnCheckpointCore(Operation op)
        {
            NetworkActor a = BoundActor(op);
            if (a != null) CatchUp(a);
        }

        /// <summary>
        /// The Arrive checkpoint. The journey was committed to arrive when this checkpoint is due, so a
        /// caught-up contractor is normally there now. It is never snapped there: a journey the world made
        /// longer, or a checkpoint run early from the dev menu, leaves it still travelling (spatial lags,
        /// the operation's timeline goes on).
        /// </summary>
        private void ArriveAtWorkCore(Operation op)
        {
            NetworkActor a = BoundActor(op);
            if (a != null) CatchUp(a);
        }

        /// <summary>
        /// The resolver has just committed the outcome (still WHAT; this is only WHERE). Trouble or disaster
        /// happened where the group is: that tile becomes the incident, before any consequence reads it.
        /// </summary>
        private void RecordIncidentCore(Operation op)
        {
            if (op?.spatial == null || op.outcome == null) return;
            if (op.outcome.troubledKey == null && op.outcome.band != OutcomeBand.Disaster) return;
            NetworkActor a = TrackedActor(op);
            if (a == null)
            {
                if (op.spatial.incident == null) op.spatial.incident = op.spatial.workRegion?.Copy();
                return;
            }
            CatchUp(a);
            op.spatial.incident = a.Get<ContractorSimulation>().spatial.anchor?.Copy();
        }

        /// <summary>
        /// After the Resolve checkpoint (and any delay it committed): a group that is not Troubled heads
        /// back, arriving when the Return checkpoint is due, or later if it cannot walk that fast (never
        /// sooner). Only a Troubled outcome keeps the group out: a Disaster with survivors that is not
        /// Troubled comes back like any other, and its incident stays recorded for the consequences.
        /// There is no separate spatial delay.
        /// </summary>
        private void StartReturnCore(Operation op)
        {
            if (op?.spatial == null || op.outcome == null || op.IsFinished) return;
            if (op.outcome.troubledKey != null) return;
            NetworkActor a = BoundActor(op);
            if (a == null) return;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            CatchUp(a);
            SpatialState s = sim.spatial;
            Checkpoint ret = op.Find(Checkpoint.Return);
            int arrival = ret?.dueTick ?? ctx.Now + 1;
            TileRef home = op.spatial.returnTo;
            if (home == null || !Graph.IsValid(home)) return;
            // Whatever leg was still under way (a late outbound leg) ends here: they head back from where they are.
            routes.Remove(a.id.Value);
            ClearLeg(s);
            s.purpose = SpatialPurpose.Return;
            if (SameTile(home, s.anchor))
            {
                s.status = SpatialStatus.Idle;
                return;
            }
            int now = ctx.Now, due = Math.Max(now + 1, arrival);
            bool started = false;
            if (op.spatial.Charter && GroundSteps(s.anchor, home, (due - now) / SpatialPolicy.TicksPerTile(sim.mobility.speedBand)) < 0)
            {
                // The same reusable charter picks them up again at the landing and sets them down at the
                // hub; if either end is gone, the pickup is planned again from where they are.
                TileRef hub, landing;
                int seed = NetHash.Combine(op.seed, "spatial.pickup");
                if (PlanCharter(s.anchor, home, false, op.spatial.hub, op.spatial.landing, int.MaxValue, sim.mobility.speedBand, SpatialPolicy.RangeTiles(sim.mobility.rangeBand), seed, out hub, out landing))
                {
                    started = StartJourney(a, sim, home, SpatialPurpose.Return, now, due, null, SpatialPolicy.MaxRouteSteps, landing, hub);
                    if (started && (!SameTile(hub, op.spatial.hub) || !SameTile(landing, op.spatial.landing)))
                    {
                        op.spatial.hub = hub;
                        op.spatial.landing = landing;
                        counters.charterReplans++;
                    }
                }
            }
            if (!started && !StartJourney(a, sim, home, SpatialPurpose.Return, now, due, null))
            {
                // They stay near the work area; the operation's own timeline is unaffected.
                if (op.spatial.fallbackKey == null) op.spatial.fallbackKey = "NoReturnRoute";
            }
        }

        /// <summary>The Return checkpoint: caught up on the way back (a late return follows the delayed checkpoint; never snapped home).</summary>
        private void ReturnFromWorkCore(Operation op)
        {
            NetworkActor a = BoundActor(op);
            if (a != null) CatchUp(a);
        }

        /// <summary>
        /// Phase 2's Troubled deadline decided the group turned up again and has returned (its Return
        /// checkpoint is complete). Spatial agrees with that authoritative lifecycle: the group is where it
        /// returns to, not left at the incident. This is a reconciliation, not a journey (Phase 2 already
        /// declared the return done); the incident stays recorded on the plan.
        /// </summary>
        private void OnTroubledRecoveredCore(Operation op)
        {
            NetworkActor a = BoundActor(op);
            if (a == null) return;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            SpatialState s = sim.spatial;
            TileRef home = op.spatial.returnTo;
            if (home == null || !Graph.IsPassable(home)) return;
            routes.Remove(a.id.Value);
            s.anchor = home.Copy();
            ClearLeg(s);
            s.purpose = SpatialPurpose.None;
            s.lastUpdateTick = ctx.Now;
            s.blockedReason = null;
            s.status = SpatialStatus.Idle;
            counters.reconciled++;
            MarkExplainedJump(a);
        }

        /// <summary>
        /// The operation ended (finished, aborted, written off). The contractor stays where it is now (an
        /// aborted journey stops mid-way; a group in trouble stays at the incident) and is idle again,
        /// resting a while before any ambient move. A group still on its way home keeps going. An ended
        /// contractor is only released: it never moves again.
        /// </summary>
        private void EndOperationCore(Operation op)
        {
            if (op == null) return;
            NetworkActor a = ctx.actors.Get(op.contractor);
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || sim.spatial.operation != op.id) return;
            SpatialState s = sim.spatial;
            if (a.status != ActorStatus.Active)
            {
                s.operation = OperationId.None;
                return;
            }
            CatchUp(a);
            s.operation = OperationId.None;
            int rest = new NetRng(a.seed, "spatial.afterOp", s.journeys).RangeInclusive(3, 10) * Ticks.PerDay;
            if (s.purpose == SpatialPurpose.Return && s.destination != null)
            {
                s.nextAmbientTick = Math.Max(s.nextAmbientTick, s.arrivalTick + rest);
                return;
            }
            routes.Remove(a.id.Value);
            ClearLeg(s);
            s.purpose = SpatialPurpose.None;
            s.lastUpdateTick = ctx.Now;
            if (s.IsInitialized) s.status = SpatialStatus.Idle;
            s.nextAmbientTick = Math.Max(s.nextAmbientTick, ctx.Now + rest);
        }

        /// <summary>
        /// The actor ended (death, dissolution). Its position is brought up to the moment it ended, then
        /// frozen: that last valid position stays as truth for later phases, and nothing (no checkpoint, no
        /// return, no upkeep) moves it again.
        /// </summary>
        private void OnActorEndedCore(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || !sim.spatial.IsInitialized) return;
            Advance(a, sim);
            SpatialState s = sim.spatial;
            routes.Remove(a.id.Value);
            ClearLeg(s);
            s.purpose = SpatialPurpose.None;
            s.lastUpdateTick = ctx.Now;
            if (s.status == SpatialStatus.Travelling || s.status == SpatialStatus.OnAssignment) s.status = SpatialStatus.Idle;
        }

        /// <summary>
        /// Where a Last Known Location should be (SPATIAL § 7): the recorded incident, else where the main
        /// body is (or was, if it ended), else the work region. Null for an operation without a spatial
        /// plan (the old placement is used).
        /// </summary>
        private TileRef IncidentTileCore(Operation op)
        {
            if (op?.spatial == null) return null;
            if (op.spatial.incident != null && Graph != null && Graph.IsValid(op.spatial.incident)) return op.spatial.incident;
            NetworkActor a = TrackedActor(op);
            if (a != null)
            {
                CatchUp(a);
                TileRef here = a.Get<ContractorSimulation>().spatial.anchor;
                if (here != null) return here;
            }
            return op.spatial.workRegion != null && Graph != null && Graph.IsValid(op.spatial.workRegion) ? op.spatial.workRegion : null;
        }

        /// <summary>
        /// The contractor whose main body is on this operation and may move for it: active only (null for
        /// an ended contractor, a detachment or a legacy operation).
        /// </summary>
        private NetworkActor BoundActor(Operation op)
        {
            NetworkActor a = TrackedActor(op);
            return a != null && a.status == ActorStatus.Active && a.quarantinedReason == null ? a : null;
        }

        /// <summary>The contractor whose main body is (or was, if it ended) on this operation: for reading truth, never for moving.</summary>
        private NetworkActor TrackedActor(Operation op)
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
            ClearLeg(sim.spatial);
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

        /// <summary>
        /// Dev: a running operation's work region moves to the given tile (the timeline is untouched). An
        /// outbound leg under way is re-proven from where they are at the next catch-up: on foot if it can,
        /// by charter if it cannot (the committed charter of the old region no longer applies).
        /// </summary>
        public bool DevRetarget(Operation op, TileRef work)
        {
            if (op?.spatial == null || work == null || op.IsFinished) return false;
            op.spatial.workRegion = work.Copy();
            op.spatial.hub = null;
            op.spatial.landing = null;
            NetworkActor a = BoundActor(op);
            if (a == null) return true;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            CatchUp(a);
            SpatialState s = sim.spatial;
            if (s.purpose == SpatialPurpose.Outbound && s.destination != null)
            {
                routes.Remove(a.id.Value);
                int start = s.journeyStartTick, arrival = s.arrivalTick;
                ClearLeg(s);
                s.destination = work.Copy();
                s.journeyStartTick = start;
                s.arrivalTick = arrival;
            }
            return true;
        }

        /// <summary>
        /// Dev: moves a running operation's work region to a passable tile on the same layer that has NO
        /// ground route from the contractor (an island, a sealed-off region), so the charter path is
        /// exercised. Null when this world has no such tile near a settlement.
        /// </summary>
        public TileRef DevRetargetAcrossWater(Operation op)
        {
            NetworkActor a = BoundActor(op);
            if (a == null || !GraphReady) return null;
            CatchUp(a);
            TileRef here = a.Get<ContractorSimulation>().spatial.anchor;
            List<SettlementFacts> all = new List<SettlementFacts>(Graph.Settlements());
            all.Sort((x, y) => Graph.ApproxDistance(here, x.tile).CompareTo(Graph.ApproxDistance(here, y.tile)));
            for (int i = 0; i < all.Count; i++)
            {
                TileRef t;
                if (all[i].player || !Graph.TryFindPassableNear(all[i].tile, 1, 2, NetHash.Combine(op.seed, "dev.water." + i), out t)) continue;
                if (t.layerId != here.layerId || GroundSteps(here, t, SpatialPolicy.MaxRouteSteps) >= 0) continue;
                DevRetarget(op, t);
                CatchUp(a);
                return t;
            }
            return null;
        }

        /// <summary>Dev: the committed charter hub stops resolving (as a destroyed provider would); the next catch-up reconciles from current truth.</summary>
        public bool DevInvalidateCharter(Operation op)
        {
            if (op?.spatial?.hub == null) return false;
            TileRef dead = new TileRef { tileId = -1, layerId = op.spatial.hub.layerId, layerDef = op.spatial.hub.layerDef };
            NetworkActor a = TrackedActor(op);
            SpatialState s = a?.Get<ContractorSimulation>()?.spatial;
            if (s != null)
            {
                if (SameTile(s.bridgeFrom, op.spatial.hub)) s.bridgeFrom = dead.Copy();
                if (SameTile(s.bridgeTo, op.spatial.hub)) s.bridgeTo = dead.Copy();
            }
            op.spatial.hub = dead;
            return true;
        }

        /// <summary>Dev: an operation's hidden plan, ground or charter (exact tiles; diagnostics only).</summary>
        public string DevDescribePlan(Operation op)
        {
            OperationSpatialPlan p = op?.spatial;
            if (p == null) return "(no spatial plan: a legacy operation, or no world data when it started)";
            return (p.Charter ? "CHARTER" : "ground") + (p.detached ? " (detachment)" : "") + ": origin " + p.origin + ", work region " + p.workRegion + ", return to " + p.returnTo
                + (p.Charter ? ", hub " + p.hub + ", landing and pickup " + p.landing : "") + (p.incident != null ? ", incident " + p.incident : "") + (p.fallbackKey != null ? ", fallback " + p.fallbackKey : "");
        }

        public string DevDescribe(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null) return "(not an NPC contractor)";
            SpatialState s = sim.spatial;
            RouteCache r;
            string route = routes.TryGetValue(a.id.Value, out r) ? "cached route " + (r.steps.Count - r.index) + " of " + r.steps.Count + " steps left (" + r.GroundLeft + " on foot)" : "no cached route";
            if (s.bridgeFrom != null)
            {
                string segment = s.bridged ? "on foot from the landing"
                    : r != null && r.bridgeUnits > 0 && r.index < r.bridgeStart ? "on foot to the charter hub"
                    : "at the hub or crossing by charter";
                route += "; segment: " + segment;
            }
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
                        routes.Remove(a.id.Value);
                        ClearLeg(s);
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
