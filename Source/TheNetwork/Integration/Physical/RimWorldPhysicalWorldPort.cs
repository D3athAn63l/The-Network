using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Diagnostics.RuntimePhysicalTests;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace TheNetwork.Integration.Physical
{
    /// <summary>Adapter counters and measurements (runtime only; never persisted). Shown by the Episode Monitor.</summary>
    public sealed class PhysicalAdapterCounters
    {
        public int projections;
        public int projectionFailures;
        public int attempts;
        public int corrections;
        public int rejected;
        public double projectionMsTotal;
        public double projectionMsMax;
        public int catchUps;
        public long agedTicks;
        public int placements;
        public int placeRefusals;
        public int normalizedInjuries;
        public int retentionProofs;
        public int retentionRefusals;
        public int passes;
        public int observations;
        public double observeMsTotal;
        public int factionsCreated;
        public int factionReleases;
        public int leftMapSeen;
        public int promotionFactReads;
        public int evidenceBattles;
        public int evidenceEntries;
        public int evidenceRelations;
        public int evidenceFailures;
        public int evidenceTruncations;
        public int episodeCacheDrops;

        public override string ToString()
        {
            return "projections " + projections + " (failed " + projectionFailures + ", attempts " + attempts + ", corrections " + corrections + ", rejected " + rejected
                + ", " + (projections > 0 ? (projectionMsTotal / projections).ToString("0.0") : "0") + " ms avg, " + projectionMsMax.ToString("0.0") + " ms max)"
                + ", catch-ups " + catchUps + " (" + agedTicks + " ticks), placements " + placements + " (refused " + placeRefusals + ")"
                + ", normalized injuries " + normalizedInjuries + ", retention proofs " + retentionProofs + " (refused " + retentionRefusals + ")"
                + ", passes " + passes + ", observations " + observations + " (" + (observations > 0 ? (observeMsTotal * 1000.0 / observations).ToString("0") : "0") + " µs avg)"
                + ", factions " + factionsCreated + " (released " + factionReleases + "), LeftMap seen " + leftMapSeen
                + ", promotion facts " + promotionFactReads + " (evidence battles " + evidenceBattles + ", entries " + evidenceEntries + ", relations " + evidenceRelations
                + ", failed reads " + evidenceFailures + ", truncated reads " + evidenceTruncations + "), released Episode cache entries " + episodeCacheDrops;
        }
    }

    /// <summary>
    /// THE production physical adapter (PHYSICAL_LIFECYCLE § 21, Phase 3.1): the real <see cref="IPhysicalWorldPort"/> over RimWorld 1.6. The
    /// lifecycle (episodes, reconciliation, the atomic commit, RELEASE / FOLLOW-UP / PUBLISH) is Phase 3.0's, unchanged; this class only
    /// creates, places, observes and releases pawns through vanilla APIs.
    ///
    /// <list type="bullet">
    /// <item>Creation is role-constrained and bounded (<see cref="PawnProjection"/>); a candidate is returned only after the authoritative
    /// verdict, so the lifecycle binds it (write-once) BEFORE it is spawned.</item>
    /// <item>M1 (ADR-053's rule, S31): the retained-pawn registry covers a bound named pawn from its binding on, while it is spawned too, so
    /// the reservation already exists when vanilla's exit or map removal passes it into WorldPawns. This class never patches anything and
    /// never calls PassToWorld for a pawn it observed as a world pawn (P3-INV-031); RELEASE only proves the reservation.</item>
    /// <item>Vanilla AI only: the visit Lord (LordJob_VisitColony with a fixed duration and no gifts); no hand-authored jobs.</item>
    /// <item>Observation is a pure read (<see cref="PawnObserver"/>); signals only wake (SignalBridge); nothing scans pawns per tick.</item>
    /// </list>
    ///
    /// The only production caller that can make it create or place anything is the session-armed physical test tier (a dev trigger).
    /// </summary>
    public sealed class RimWorldPhysicalWorldPort : IPhysicalWorldPort, IGroupPhysicalWorldPort, IPhysicalPromotionPort
    {
        /// <summary>How long a 3.1 dev visit stays at its chill spot before vanilla's own exit transition (≈ 3 in-game hours).</summary>
        public const int VisitDurationTicks = 7500;

        private readonly DomainContext ctx;
        public readonly RetainedPawnRegistry Registry;
        public readonly PhysicalAdapterCounters counters = new PhysicalAdapterCounters();

        /// <summary>The synchronous LeftMap tick of bound pawns seen this session (runtime only; the exit tick for truthful aging).</summary>
        private readonly Dictionary<Pawn, int> leftMap = new Dictionary<Pawn, int>(PawnReferenceComparer.Instance);

        // Scoped test permissions are runtime-only and belong to one exact game, Episode and map. A load creates a new port.
        private VisibilityScope visibilityScope;

        /// <summary>The last first projection (Episode Monitor, RT-PHYX-011).</summary>
        public ProjectionResult lastProjection;

        /// <summary>The last bounded optional evidence query (runtime only, for the existing diagnostics/test consumers).</summary>
        public ConcretizationEvidenceScan lastEvidenceScan;

        /// <summary>
        /// RUNTIME ONLY, never saved: the physical test tier shortens a test visit (the same vanilla Lord, a shorter stay) while one of its
        /// runs is active, and resets it to -1 when the run ends. Nothing else writes it; -1 means <see cref="VisitDurationTicks"/>.
        /// </summary>
        public int visitTicksOverride = -1;

        public int VisitTicks => visitTicksOverride > 0 ? visitTicksOverride : VisitDurationTicks;

        public RimWorldPhysicalWorldPort(DomainContext ctx)
        {
            this.ctx = ctx;
            Registry = new RetainedPawnRegistry(ctx);
            // STAGE 1 (FinalizeInit builds the runtime, BEFORE the load's cross-references resolve): the durable thing-id index only. It must
            // not read a pawn pointer (still null here). The pointer index is stage 2, OnReferencesResolved, at the world component's PostLoadInit.
            Registry.RebuildEarly();
        }

        public bool Available => Current.ProgramState == ProgramState.Playing && Find.World != null && Find.WorldPawns != null && !Registry.inert;

        public string Name => "RimWorld 1.6 (Phase 3.2B)";

        private int Now => ctx.Now;

        // ================================================================== creation and binding support

        public PawnRef Create(ProjectionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!OrganizationCompositionV1.IsRole(request.role)) throw new InvalidOperationException("a physical slot must have a valid operational role before generation");
            Faction f = request.faction?.Resolve();
            if (f == null || !f.temporary) throw new InvalidOperationException("the episode has no live temporary encounter faction");
            // M1: the registry quest exists before any pawn of ours can ever leave a map.
            Registry.EnsureQuest();
            ProjectionResult r = PawnProjection.Project(request, f, SharedIdeology(request));
            lastProjection = r;
            counters.projections++;
            counters.attempts += r.attempts;
            counters.corrections += r.corrections;
            counters.rejected += r.rejected;
            counters.projectionMsTotal += r.ms;
            if (r.ms > counters.projectionMsMax) counters.projectionMsMax = r.ms;
            NetLog.Info(LogCategory.Physical, "First projection for " + request.character + " (" + request.role + ", capability " + request.capability + ", equipment tier " + request.equipmentTier + "): " + r);
            if (r.pawn == null)
            {
                counters.projectionFailures++;
                throw new InvalidOperationException("role-constrained creation aborted, nothing bound: " + r.failure);
            }
            if (request.character.IsValid)
            {
                PawnProjection.ApplyNamePins(r.pawn, NamePins.From(request.name));
                Registry.Note(r.pawn, request.character);
            }
            return new PawnRef { pawn = r.pawn, thingIdNumber = r.pawn.thingIDNumber, defName = r.pawn.def?.defName };
        }

        /// <summary>Choose already-established group truth; never edit an existing pawn's ideology or relations.</summary>
        private Ideo SharedIdeology(ProjectionRequest request)
        {
            if (!ModsConfig.IdeologyActive) return null;
            OrganizationProfile org = ctx.actors?.Get(request.actor)?.Get<OrganizationProfile>();
            if (org == null) return null;
            KnownCharacter first = null;
            if (org.knownMembers != null)
            {
                for (int i = 0; i < org.knownMembers.Count; i++)
                {
                    KnownCharacter c = ctx.characters?.Get(org.knownMembers[i]);
                    Pawn p = c?.pawn?.pawn;
                    if (!OrganizationSeatPolicy.IsCurrentMember(c, request.actor) || p == null || p.Discarded || p.Dead || p.ideo == null || p.Ideo == null
                        || c.pawn.thingIdNumber != p.thingIDNumber) continue;
                    if (first == null || c.id.Value < first.id.Value) first = c;
                }
            }
            if (first != null) return first.pawn.pawn.Ideo;
            // With no established member, the first vanilla-generated slot supplies the construction preference for its peers.
            PhysicalEpisode e = ctx.episodes?.Get(request.episode);
            EpisodeMember earliest = null;
            if (e?.members != null)
            {
                for (int i = 0; i < e.members.Count && i < 8; i++)
                {
                    EpisodeMember m = e.members[i];
                    Pawn p = m?.pawn?.pawn;
                    if (p == null || p.Discarded || p.Dead || p.ideo == null || p.Ideo == null || m.pawn.thingIdNumber != p.thingIDNumber) continue;
                    if (earliest == null || m.slot < earliest.slot) earliest = m;
                }
            }
            return earliest?.pawn?.pawn?.Ideo;
        }

        /// <summary>The lifecycle invokes this only AFTER the member binding is durable and BEFORE any placement can expose its pawn.</summary>
        public void EpisodeBindingChanged(PhysicalEpisode episode, EpisodeMember member)
        {
            if (episode == null || member == null || !ReferenceEquals(ctx.episodes?.Get(episode.id), episode) || episode.members == null || !episode.members.Contains(member))
                throw new InvalidOperationException("the member does not belong to the current durable Episode");
            Pawn p = member.pawn?.pawn;
            if (p == null || p.Discarded || member.pawn.thingIdNumber != p.thingIDNumber)
                throw new InvalidOperationException("the durable Episode binding does not resolve to its exact pawn");
            if (member.character.IsValid)
            {
                KnownCharacter c = ctx.characters?.Get(member.character);
                if (c?.pawn == null || !ReferenceEquals(c.pawn.pawn, p) || c.pawn.thingIdNumber != p.thingIDNumber)
                    throw new InvalidOperationException("the named person and Episode do not share the same durable pawn binding");
                Registry.Note(p, member.character);
                if (c.IsAlive) PhysicalTags.Add(p, PhysicalTags.Character(c.id));
            }
            Registry.NoteEpisode(p, episode.id, member.slot);
            if (!p.Dead && !Registry.Reserves(p))
                throw new InvalidOperationException("durable binding is not covered by existing registry");
        }

        /// <summary>The release marker, not a physical observation, ends temporary ownership. Drop only derived runtime caches.</summary>
        public void EpisodeReleased(PhysicalEpisode episode)
        {
            List<Pawn> forgotten = Registry.ForgetReleasedEpisode(episode);
            counters.episodeCacheDrops += forgotten.Count;
            for (int i = 0; i < forgotten.Count; i++)
                if (!Registry.CharacterOf(forgotten[i]).IsValid) leftMap.Remove(forgotten[i]);
        }

        public bool Resolves(PawnRef pawn)
        {
            return pawn?.pawn != null && !pawn.pawn.Discarded;
        }

        public void CatchUpAge(PawnRef pawn, long elapsedTicks)
        {
            Pawn p = pawn?.pawn;
            if (p == null || p.Discarded) throw new InvalidOperationException("the binding does not resolve; nothing can be aged");
            long before = p.ageTracker?.AgeBiologicalTicks ?? -1;
            long applied = PawnAging.CatchUp(p, elapsedTicks);
            counters.catchUps++;
            counters.agedTicks += applied;
            NetLog.Info(LogCategory.Physical, "Truthful aging of " + p.LabelShort + " (#" + p.thingIDNumber + "): " + applied + " elapsed ticks applied through the mothball path; biological "
                + before + " → " + (p.ageTracker?.AgeBiologicalTicks ?? -1) + " (rate " + (p.ageTracker?.BiologicalTicksPerTick ?? 0f).ToString("0.###") + ").");
        }

        // ================================================================== faction (S10)

        public FactionRef EnsureEncounterFaction(EpisodeId episode, ActorId actor, FactionRef current, int seededGoodwill)
        {
            Faction live = current?.Resolve();
            if (live != null && live.temporary) return current;
            FactionRef made = EncounterFactions.Ensure(episode, ctx.actors?.Get(actor)?.name?.Display, current, seededGoodwill);
            counters.factionsCreated++;
            return made;
        }

        public void ReleaseEncounterFaction(FactionRef faction)
        {
            if (EncounterFactions.Release(faction)) counters.factionReleases++;
        }

        // ================================================================== placement (vanilla AI only)

        public bool Place(PawnRef pawn, EpisodeId episode, TileRef tile, int mapId, FactionRef faction)
        {
            Pawn p = pawn?.pawn;
            string refusal = PlacementRefusal(p, mapId, faction);
            if (refusal != null)
            {
                counters.placeRefusals++;
                NetLog.Warn(LogCategory.Physical, "Placement of " + pawn + " for " + episode + " refused (nothing changed): " + refusal);
                return false;
            }
            Map map = MapById(mapId);
            Faction f = faction.Resolve();
            PhysicalEpisode e = ctx.episodes?.Get(episode);
            Lord shared;
            IntVec3 sharedTarget;
            string groupRefusal = SharedVisitLord(e, map, f, out shared, out sharedTarget);
            if (groupRefusal != null || (shared != null && !shared.CanAddPawn(p)))
            {
                counters.placeRefusals++;
                NetLog.Warn(LogCategory.Physical, "Placement of " + pawn + " for " + episode + " refused: " + (groupRefusal ?? "the shared vanilla Lord cannot accept this member") + ".");
                return false;
            }
            IntVec3 entry, chill;
            bool cells = shared == null ? TryCells(map, out entry, out chill) : TrySharedCells(map, sharedTarget, out entry, out chill);
            if (!cells)
            {
                counters.placeRefusals++;
                NetLog.Warn(LogCategory.Physical, "Placement of " + pawn + " refused: no reachable edge cell on map " + mapId + ".");
                return false;
            }
            bool rematerialized = Find.WorldPawns.Contains(p);
            if (rematerialized) PawnNormalization.ComfortableNeeds(p);
            if (p.Faction != f) p.SetFaction(f);
            CharacterId who = Registry.CharacterOf(p);
            PhysicalTags.Add(p, PhysicalTags.Episode(episode));
            if (who.IsValid) PhysicalTags.Add(p, PhysicalTags.Character(who));
            // Vanilla's SpawnSetup takes a stored world pawn out of WorldPawns itself (no Network RemovePawn, no second insertion).
            // GenSpawn.Spawn returns the pawn even when SpawnSetup discarded it, and a mod may throw inside it: neither the return value nor a throw
            // says what became of the pawn. The lifecycle classifies the bound pawn from positive observation after any placement that did not
            // report success (PlacementRules); this method only reports the truth it can see and never decides the member's fate.
            GenSpawn.Spawn(p, entry, map);
            if (!p.Spawned || p.Discarded)
            {
                counters.placeRefusals++;
                NetLog.Warn(LogCategory.Physical, "Vanilla did not leave " + pawn + " spawned (SpawnSetup can discard an invalid pawn): discarded " + p.Discarded + ", dead " + p.Dead
                    + ", world pawn " + (Find.WorldPawns != null && Find.WorldPawns.Contains(p)) + ". Not reported placed; the lifecycle classifies it from observation.");
                return false;
            }
            Lord lord = shared;
            if (lord == null)
            {
                LordJob_VisitColony job = new LordJob_VisitColony(f, chill, VisitTicks) { gifts = new List<Thing>() };
                lord = LordMaker.MakeNewLord(f, job, map, new List<Pawn> { p });
            }
            else lord.AddPawn(p);
            if (lord == null || !ReferenceEquals(p.GetLord(), lord))
            {
                counters.placeRefusals++;
                NetLog.Warn(LogCategory.Physical, "Vanilla did not attach " + pawn + " to the Episode's visit Lord. Not reported placed; the lifecycle classifies the spawned pawn from observation.");
                return false;
            }
            counters.placements++;
            NetLog.Info(LogCategory.Physical, (rematerialized ? "Rematerialized" : "Placed") + " " + p.LabelShort + " (#" + p.thingIDNumber + ") on map " + mapId + " at " + entry + " for " + episode
                + ": " + (shared == null ? "created" : "joined") + " the shared visit Lord to " + chill + " for " + VisitTicks + " ticks; reserved by the registry while spawned (M1): " + Registry.Reserves(p) + ".");
            return true;
        }

        /// <summary>Derive the shared vanilla Lord from the Episode's at most eight bound members; no map/world Lord scan or new roster.</summary>
        private static string SharedVisitLord(PhysicalEpisode episode, Map map, Faction faction, out Lord shared, out IntVec3 target)
        {
            shared = null;
            target = IntVec3.Invalid;
            if (episode?.members == null || episode.members.Count > 8) return "the Episode member set is missing or exceeds eight";
            for (int i = 0; i < episode.members.Count; i++)
            {
                EpisodeMember m = episode.members[i];
                Pawn peer = m?.pawn?.pawn;
                if (peer == null || peer.Discarded || peer.Dead || !peer.Spawned || peer.Map != map) continue;
                if (peer.Faction != faction || m.pawn.thingIdNumber != peer.thingIDNumber) return "a spawned Episode peer disagrees with its faction or binding";
                Lord candidate = peer.GetLord();
                if (candidate == null) return "a spawned Episode peer has no vanilla Lord";
                if (!(candidate.LordJob is LordJob_VisitColony) || candidate.Map != map || candidate.faction != faction)
                    return "a spawned Episode peer belongs to a different vanilla Lord";
                if (shared != null && !ReferenceEquals(shared, candidate)) return "the Episode peers have conflicting visit Lords";
                shared = candidate;
            }
            if (shared == null) return null;
            // Both vanilla Travel and DefendPoint expose their current destination through FlagLoc; do not read private job state.
            target = shared.CurLordToil?.FlagLoc ?? IntVec3.Invalid;
            if (!target.IsValid || !target.InBounds(map) || !target.Standable(map)) return "the current shared Lord has no usable destination";
            if (shared.ownedPawns.Count > 8) return "the shared Lord exceeds the Episode member bound";
            for (int i = 0; i < shared.ownedPawns.Count; i++)
            {
                Pawn owned = shared.ownedPawns[i];
                bool belongs = false;
                for (int j = 0; j < episode.members.Count; j++)
                    if (ReferenceEquals(episode.members[j]?.pawn?.pawn, owned)) { belongs = true; break; }
                if (!belongs) return "the shared Lord owns a pawn outside this Episode";
            }
            return null;
        }

        /// <summary>Why a pawn may NOT be placed now (null = it may). Checked before anything is touched.</summary>
        public string PlacementRefusal(Pawn p, int mapId, FactionRef faction)
        {
            if (p == null || p.Discarded) return "the binding does not resolve";
            if (p.Dead) return "the person is dead";
            if (p.Spawned || p.SpawnedOrAnyParentSpawned) return "already spawned";
            PassToWorldCheck held = PawnObserver.CheckPass(p);
            if (held == PassToWorldCheck.Held || held == PassToWorldCheck.Unknown) return "a vanilla owner holds it (" + held + ")";
            if (MapById(mapId) == null) return "map " + mapId + " does not exist";
            Faction f = faction?.Resolve();
            if (f == null || !f.temporary) return "no live temporary encounter faction";
            if (p.Downed) return "unfit after the catch-up: downed";
            if (p.health?.capacities != null && !p.health.capacities.CapableOf(PawnCapacityDefOf.Moving)) return "unfit after the catch-up: cannot move";
            if (!Registry.Reserves(p)) return "M1 precondition: the registry does not cover this pawn before placement (binding or custody missing)";
            try
            {
                Registry.EnsureQuest();
            }
            catch (Exception ex)
            {
                return "no registry quest: " + ex.Message;
            }
            return null;
        }

        private static bool TryCells(Map map, out IntVec3 entry, out IntVec3 chill)
        {
            entry = IntVec3.Invalid;
            chill = IntVec3.Invalid;
            for (int i = 0; i < 12; i++)
            {
                IntVec3 c;
                if (!CellFinder.TryFindRandomCellNear(map.Center, map, 12, x => x.Standable(map) && !x.Fogged(map), out c)) c = map.Center;
                IntVec3 target = c;
                IntVec3 e;
                if (!CellFinder.TryFindRandomEdgeCellWith(x => x.Standable(map) && !x.Fogged(map) && map.reachability.CanReach(x, target, PathEndMode.OnCell, TraverseMode.PassDoors, Danger.Deadly),
                    map, CellFinder.EdgeRoadChance_Neutral, out e)) continue;
                entry = e;
                chill = target;
                return true;
            }
            return false;
        }

        private static bool TrySharedCells(Map map, IntVec3 target, out IntVec3 entry, out IntVec3 chill)
        {
            chill = target;
            return CellFinder.TryFindRandomEdgeCellWith(x => x.Standable(map) && !x.Fogged(map)
                && map.reachability.CanReach(x, target, PathEndMode.OnCell, TraverseMode.PassDoors, Danger.Deadly),
                map, CellFinder.EdgeRoadChance_Neutral, out entry);
        }

        public static Map MapById(int mapId)
        {
            List<Map> maps = Find.Maps;
            if (maps == null) return null;
            for (int i = 0; i < maps.Count; i++) if (maps[i] != null && maps[i].uniqueID == mapId) return maps[i];
            return null;
        }

        /// <summary>Presence evidence is a successful spawn on a home or player-occupied map, never an inference from map exit or load.</summary>
        public bool IsPlayerVisiblePlacement(PhysicalEpisode episode, EpisodeMember member)
        {
            if (episode == null || member == null || !ReferenceEquals(ctx.episodes?.Get(episode.id), episode)
                || episode.members == null || !episode.members.Contains(member)) return false;
            Pawn p = member.pawn?.pawn;
            Map map = p?.Map;
            if (p == null || p.Discarded || p.Dead || !p.Spawned || map == null || map.uniqueID != episode.whereMapId
                || member.pawn.thingIdNumber != p.thingIDNumber) return false;
            bool testMap = map.Parent?.def?.defName == PhysicalTestIds.TestMapDef;
            VisibilityScope scope = visibilityScope;
            if (scope != null && !scope.disposed && ReferenceEquals(scope.game, Current.Game) && Prefs.DevMode && PhysicalTestSession.IsRunning
                && ReferenceEquals(scope.ownedEpisode, episode) && scope.mapId == map.uniqueID)
                return scope.synthetic ? testMap : !testMap && PlayerOccupied(map);
            if (testMap || !string.IsNullOrEmpty(episode.cause?.devKey)) return false;
            return PlayerOccupied(map);
        }

        private static bool PlayerOccupied(Map map)
        {
            if (map == null) return false;
            if (map.IsPlayerHome) return true;
            // A bounded map-local read only when placement asks for evidence; never a world-pawn scan or a per-tick watcher.
            IReadOnlyList<Pawn> pawns = map.mapPawns?.AllPawnsSpawned;
            if (pawns == null) return false;
            for (int i = 0; i < pawns.Count; i++)
                if (pawns[i]?.Faction?.IsPlayer == true || pawns[i]?.HostFaction?.IsPlayer == true) return true;
            return false;
        }

        /// <summary>TEST ONLY: one active owned Episode on the dedicated TestSite may exercise SYNTHETIC P0 inside a using/finally scope.</summary>
        public IDisposable OverrideTestVisibility(EpisodeId episode, int mapId)
        {
            PhysicalEpisode e = VisibilityEpisode(episode, mapId);
            if (MapById(mapId)?.Parent?.def?.defName != PhysicalTestIds.TestMapDef)
                throw new InvalidOperationException("synthetic P0 is restricted to the dedicated physical TestSite");
            return PushVisibility(e, mapId, true);
        }

        /// <summary>A separate typed gate for an owner-driven genuine P0 run. The ordinary physical-test arm never grants this scope.</summary>
        public static string ConfirmedHomeVisibilityPhrase(int mapId)
        {
            return "CONFIRM PHYSICAL P0 ON MAP " + mapId + " THIS SAVE WILL BE MODIFIED";
        }

        public IDisposable OverrideConfirmedHomeVisibility(EpisodeId episode, int mapId, string typedPhrase)
        {
            PhysicalEpisode e = VisibilityEpisode(episode, mapId);
            Map map = MapById(mapId);
            if (!string.Equals(typedPhrase, ConfirmedHomeVisibilityPhrase(mapId), StringComparison.Ordinal))
                throw new InvalidOperationException("genuine dev P0 requires the separate exact typed map/save-modification confirmation");
            if (map?.Parent?.def?.defName == PhysicalTestIds.TestMapDef || !PlayerOccupied(map))
                throw new InvalidOperationException("genuine P0 requires an actual home or player-occupied map");
            return PushVisibility(e, mapId, false);
        }

        private PhysicalEpisode VisibilityEpisode(EpisodeId id, int mapId)
        {
            PhysicalEpisode e = ctx.episodes?.Get(id);
            if (!Prefs.DevMode || !PhysicalTestSession.IsRunning || e == null || !e.IsActive || e.whereMapId != mapId
                || MapById(mapId) == null || !PhysicalTestIds.IsTestDevKey(e.cause?.devKey))
                throw new InvalidOperationException("visibility overrides require a running dev test and its exact active owned Episode/map");
            return e;
        }

        private IDisposable PushVisibility(PhysicalEpisode e, int mapId, bool synthetic)
        {
            VisibilityScope scope = new VisibilityScope(this, visibilityScope, e, mapId, synthetic);
            visibilityScope = scope;
            return scope;
        }

        private sealed class VisibilityScope : IDisposable
        {
            private readonly RimWorldPhysicalWorldPort owner;
            public readonly VisibilityScope previous;
            public readonly Game game;
            public readonly PhysicalEpisode ownedEpisode;
            public readonly int mapId;
            public readonly bool synthetic;
            public bool disposed;

            public VisibilityScope(RimWorldPhysicalWorldPort owner, VisibilityScope previous, PhysicalEpisode episode, int mapId, bool synthetic)
            {
                this.owner = owner;
                this.previous = previous;
                game = Current.Game;
                ownedEpisode = episode;
                this.mapId = mapId;
                this.synthetic = synthetic;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (!ReferenceEquals(owner.visibilityScope, this)) return;
                VisibilityScope restore = previous;
                while (restore != null && restore.disposed) restore = restore.previous;
                owner.visibilityScope = restore;
            }
        }

        // ================================================================== observation (pure read)

        public PhysicalObservation Observe(PawnRef pawn, EpisodeId episode)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Pawn p = pawn?.pawn;
            PhysicalObservation o = PawnObserver.Classify(p, Registry, Registry.FindQuest(), ExitTick(p));
            sw.Stop();
            counters.observations++;
            counters.observeMsTotal += sw.Elapsed.TotalMilliseconds;
            return o;
        }

        /// <summary>Terminal reconciliation only: capture this exact bound Pawn's existing name before any optional bounded history read.</summary>
        public PhysicalPromotionFacts ReadPromotionFacts(PhysicalEpisode episode, EpisodeMember member)
        {
            if (episode == null || member == null || !ReferenceEquals(ctx.episodes?.Get(episode.id), episode)
                || episode.members == null || !episode.members.Contains(member)) return null;
            Pawn p = member.pawn?.pawn;
            if (p == null || member.pawn.thingIdNumber <= 0 || member.pawn.thingIdNumber != p.thingIDNumber) return null;
            PhysicalPromotionFacts facts = new PhysicalPromotionFacts { name = ExistingPawnName(p) };
            counters.promotionFactReads++;
            // Missing/pruned/unreadable optional logs cannot erase the name or suppress mandatory S1 / latched P0.
            try
            {
                ConcretizationEvidenceScan scan = ConcretizationEvidenceCollector.Scan(p, episode.createdTick, ctx.Now);
                lastEvidenceScan = scan;
                if (scan != null)
                {
                    facts.evidence = scan.evidence;
                    counters.evidenceBattles += scan.battlesExamined;
                    counters.evidenceEntries += scan.entriesExamined;
                    counters.evidenceRelations += scan.relationRecordsExamined;
                    if (scan.collectionFailed) counters.evidenceFailures++;
                    if (scan.truncatedBattles || scan.truncatedEntries || scan.truncatedRelations) counters.evidenceTruncations++;
                }
            }
            catch (Exception)
            {
                lastEvidenceScan = new ConcretizationEvidenceScan { collectionFailed = true };
                counters.evidenceFailures++;
            }
            return facts;
        }

        private static NameSnapshot ExistingPawnName(Pawn p)
        {
            Name actual = p?.Name;
            string full = actual?.ToStringFull;
            if (string.IsNullOrWhiteSpace(full)) return null;
            NameTriple triple = actual as NameTriple;
            if (triple != null) return new NameSnapshot { first = triple.First, nick = triple.Nick, last = triple.Last, display = full };
            // A single name is a display fact, not a string to split or translate into a made-up first/nick/last identity.
            if (actual is NameSingle) return new NameSnapshot { display = full };
            return null;
        }

        /// <summary>SignalBridge: a bound pawn's synchronous LeftMap (runtime only; the tick it stopped ticking, for truthful aging).</summary>
        public void NoteLeftMap(Pawn p)
        {
            if (p == null) return;
            leftMap[p] = Now;
            counters.leftMapSeen++;
        }

        public int ExitTick(Pawn p)
        {
            int t;
            return p != null && leftMap.TryGetValue(p, out t) ? t : -1;
        }

        // ================================================================== RELEASE actions (idempotent by observed state)

        public void Normalize(PawnRef pawn)
        {
            Pawn p = pawn?.pawn;
            if (p == null) return;
            int healed = PawnNormalization.Normalize(p);
            counters.normalizedInjuries += healed;
            List<string> left = PawnNormalization.Remaining(p);
            if (healed > 0 || left.Count > 0)
            {
                NetLog.Info(LogCategory.Physical, "Store-time normalization of " + p.LabelShort + " (#" + p.thingIDNumber + "): " + healed + " temporary injuries healed (the abstract recovery owns them)"
                    + (left.Count > 0 ? "; left alone (not a vanilla injury, measured): " + string.Join(", ", left.ToArray()) : "") + ".");
            }
        }

        /// <summary>
        /// PROVE the M1 reservation (ADR-053: RELEASE proves it, it does not create it): the registry quest exists, the registry covers the pawn
        /// and, as a world pawn, vanilla sees it as ReservedByQuest. Nothing is established or repaired here (the binding, the custody and the
        /// quest made at the first binding already did it): a registry quest that is missing is NOT recreated by this stage, and a failed proof
        /// throws so RELEASE stays pending, never quietly healed.
        /// </summary>
        public void EnsureRetained(PawnRef pawn)
        {
            Pawn p = pawn?.pawn;
            if (p == null || p.Discarded) throw Refused("the binding does not resolve");
            if (Registry.FindQuest() == null) throw Refused("the registry quest does not exist (RELEASE proves the reservation, it never creates it)");
            if (!Registry.Reserves(p)) throw Refused("the registry does not cover " + p.LabelShort + " (custody or binding)");
            if (Find.WorldPawns.Contains(p))
            {
                WorldPawnSituation s = Find.WorldPawns.GetSituation(p);
                if (s != WorldPawnSituation.ReservedByQuest) throw Refused(p.LabelShort + " is a world pawn in situation " + s + ", not ReservedByQuest (M1 / P3-INV-032)");
            }
            counters.retentionProofs++;
        }

        private Exception Refused(string why)
        {
            counters.retentionRefusals++;
            return new InvalidOperationException("RELEASE: the retained reservation is not proven: " + why);
        }

        public PassToWorldCheck CheckPassToWorld(PawnRef pawn)
        {
            return PawnObserver.CheckPass(pawn?.pawn);
        }

        /// <summary>Only for a bound pawn positively NOT yet a world pawn and held by nobody (§ 7.5): re-checked here, never forced.</summary>
        public void PassToWorld(PawnRef pawn)
        {
            Pawn p = pawn?.pawn;
            PassToWorldCheck check = PawnObserver.CheckPass(p);
            if (check != PassToWorldCheck.Allowed) throw new PhysicalPreconditionException("PassToWorld " + pawn, check);
            Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.Decide);
            counters.passes++;
        }

        public void StripEpisodeTag(PawnRef pawn, EpisodeId episode)
        {
            PhysicalTags.Remove(pawn?.pawn, PhysicalTags.Episode(episode));
        }

        // ================================================================== load, removal, diagnostics

        /// <summary>
        /// LOAD STAGE 2 (the Phase 3.1 runtime-QA correction), called from the world component's PostLoadInit: every cross-reference is
        /// resolved, so the validated pointer index is built (it becomes the authority), the registry quest is ensured when the DURABLE state
        /// says anyone is retained, and every living binding that is unresolved, discarded or disagrees with its persisted thing id is reported
        /// loudly. All of it happens before the first tick, so a Stored or Deployed person is never exposed as an ordinary Free world pawn.
        /// Nothing is generated, spawned, destroyed, cleared or decided; the stage is idempotent.
        /// </summary>
        public RegistryLoadReport OnReferencesResolved()
        {
            RegistryLoadReport report = Registry.ResolvePointers();
            string quest = "not needed (no retained named or temporary Episode pawn)";
            if (report.durableRetained > 0)
            {
                try
                {
                    Quest q = Registry.EnsureQuest();
                    quest = "quest " + q.id + " " + q.State;
                }
                catch (Exception ex)
                {
                    quest = "FAILED: " + ex.Message;
                    NetLog.Error(LogCategory.Physical, "The retained-pawn registry quest could not be ensured after the load's references resolved: " + ex.Message);
                }
            }
            for (int i = 0; i < report.findings.Count; i++) NetLog.ErrorOnce(LogCategory.Physical, "binding." + report.findings[i].id.Value + "." + report.findings[i].kind, report.findings[i].ToString());
            for (int i = 0; i < report.episodeFindings.Count; i++)
            {
                EpisodeBindingFinding f = report.episodeFindings[i];
                NetLog.ErrorOnce(LogCategory.Physical, "episode.binding." + f.episode.Value + "." + f.slot + "." + f.kind, f.ToString());
            }
            if (report.bound > 0 || report.episodeBound > 0 || report.durableRetained > 0)
            {
                NetLog.Info(LogCategory.Physical, "Physical load, stage 2 (post-load-init, references resolved, before the first tick): " + report + "; registry " + quest + ".");
            }
            if (report.covered < report.durableRetained)
            {
                NetLog.Error(LogCategory.Physical, "RESERVATION GAP: " + report.durableRetained + " named or temporary Episode pawn(s) must be reserved but only " + report.covered
                    + " are covered; the rest have an unresolved, discarded or mismatching binding (see the integrity findings above).");
            }
            return report;
        }

        /// <summary>
        /// The load pass (§ 16.1–16.3), at start-up (the first tick's gate, AFTER <see cref="OnReferencesResolved"/>): tags corrected FROM the
        /// bindings (bounded by the bound people), and the registry quest re-ensured when anyone is retained (idempotent). Nothing is
        /// generated, spawned, destroyed or decided. The reservation itself never waits for this pass: vanilla's first tick runs
        /// WorldPawns BEFORE any world component, so the reservation is complete before it.
        /// </summary>
        public string OnLoaded()
        {
            int tagged = 0, stripped = 0;
            Dictionary<Pawn, HashSet<string>> wanted = new Dictionary<Pawn, HashSet<string>>(PawnReferenceComparer.Instance);
            List<KnownCharacter> all = ctx.characters?.characters;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    KnownCharacter c = all[i];
                    HashSet<string> keep = TagsForBinding(wanted, c?.pawn);
                    if (keep == null) continue;
                    if (c.IsAlive) keep.Add(PhysicalTags.Character(c.id));
                    PhysicalEpisode e = c.episode.IsValid ? ctx.episodes?.Get(c.episode) : null;
                    if (e != null && !e.releaseApplied) keep.Add(PhysicalTags.Episode(e.id));
                }
            }
            List<PhysicalEpisode> episodes = ctx.episodes?.episodes;
            if (episodes != null) for (int i = 0; i < episodes.Count; i++)
            {
                PhysicalEpisode e = episodes[i];
                if (e?.members == null) continue;
                for (int k = 0; k < e.members.Count; k++)
                {
                    HashSet<string> keep = TagsForBinding(wanted, e.members[k]?.pawn);
                    if (keep != null && !e.releaseApplied) keep.Add(PhysicalTags.Episode(e.id));
                }
            }
            foreach (KeyValuePair<Pawn, HashSet<string>> pair in wanted)
            {
                stripped += PhysicalTags.RemoveAllExcept(pair.Key, pair.Value);
                foreach (string t in pair.Value)
                {
                    if (!PhysicalTags.Has(pair.Key, t))
                    {
                        PhysicalTags.Add(pair.Key, t);
                        tagged++;
                    }
                }
            }
            int retained = Registry.RetainedCount();
            int durable = Registry.DurableRetainedCount();
            string quest = "not needed";
            if (durable > 0)
            {
                try
                {
                    quest = "quest " + Registry.EnsureQuest().id;
                }
                catch (Exception ex)
                {
                    quest = "FAILED: " + ex.Message;
                    NetLog.Error(LogCategory.Physical, "The retained-pawn registry quest could not be ensured at load: " + ex.Message);
                }
            }
            string summary = "Physical load pass: " + wanted.Count + " bound pawn(s), " + retained + " of " + durable + " durable retained covered (named " + Registry.NamedRetainedCount()
                + ", temporary Episode " + Registry.TemporaryRetainedCount() + "; " + (Registry.pointersResolved ? "pointer index resolved at post-load-init" : "thing-id bridge")
                + "), " + tagged + " tag(s) restored, " + stripped + " stale tag(s) stripped, registry " + quest + ".";
            if (wanted.Count > 0) NetLog.Info(LogCategory.Physical, summary);
            return summary;
        }

        private static HashSet<string> TagsForBinding(Dictionary<Pawn, HashSet<string>> wanted, PawnRef binding)
        {
            Pawn p = binding?.pawn;
            if (p == null || p.Discarded || binding.thingIdNumber <= 0 || binding.thingIdNumber != p.thingIDNumber) return null;
            HashSet<string> tags;
            if (!wanted.TryGetValue(p, out tags)) wanted[p] = tags = new HashSet<string>();
            return tags;
        }

        /// <summary>Prepare-for-removal (§ 20 steps 2–3): the registry reserves nobody and its quest is ended; every Network tag leaves our pawns.</summary>
        public string PrepareForRemoval()
        {
            int quests = Registry.Release();
            int tags = 0;
            HashSet<Pawn> pawns = new HashSet<Pawn>(PawnReferenceComparer.Instance);
            List<KnownCharacter> all = ctx.characters?.characters;
            if (all != null) for (int i = 0; i < all.Count; i++) if (all[i]?.pawn?.pawn != null) pawns.Add(all[i].pawn.pawn);
            List<PhysicalEpisode> episodes = ctx.episodes?.episodes;
            if (episodes != null) for (int i = 0; i < episodes.Count; i++)
            {
                List<EpisodeMember> members = episodes[i]?.members;
                if (members == null) continue;
                for (int k = 0; k < members.Count; k++) if (members[k]?.pawn?.pawn != null) pawns.Add(members[k].pawn.pawn);
            }
            foreach (Pawn p in pawns) tags += PhysicalTags.RemoveAllExcept(p, null);
            return "registry released (" + quests + " quest ended), " + tags + " pawn tag(s) stripped";
        }

        public string Resume()
        {
            Registry.Resume();
            return OnLoaded();
        }

        public string Describe()
        {
            return Name + (Available ? "" : " (unavailable)") + "; " + Registry.Describe() + "; " + counters;
        }

        /// <summary>
        /// A READ-ONLY description of a binding's pawn for the Episode Monitor (§ 18 of the 3.1 brief): where it is, what vanilla thinks it
        /// is and whether the registry reserves it. Plain text, so no diagnostics window ever touches a Pawn. Changes nothing.
        /// </summary>
        public PawnFacts DescribeBinding(PawnRef r)
        {
            PawnFacts f = new PawnFacts();
            Pawn p = r?.pawn;
            if (r == null || !r.IsBound)
            {
                f.where = "unbound";
                return f;
            }
            f.thingId = p != null ? p.thingIDNumber : r.thingIdNumber;
            if (p == null)
            {
                f.where = "binding #" + r.thingIdNumber + " does not resolve";
                return f;
            }
            f.label = p.LabelShort;
            f.dead = p.Dead;
            f.discarded = p.Discarded;
            f.downed = !p.Dead && p.Downed;
            f.spawned = p.Spawned;
            f.mapId = p.MapHeld?.uniqueID ?? -1;
            f.faction = p.Faction == null ? "none" : p.Faction.loadID + " \"" + p.Faction.Name + "\"" + (p.Faction.temporary ? " (temporary)" : "");
            WorldPawns wp = Find.WorldPawns;
            f.worldPawn = wp != null && wp.Contains(p);
            f.situation = f.worldPawn ? wp.GetSituation(p).ToString() : "-";
            f.reserved = Registry.Reserves(p);
            f.reservation = f.reserved ? (Registry.IsTemporaryReserved(p) ? "temporary Episode " + Registry.EpisodeOf(p) : "named") : "none";
            f.suspended = f.worldPawn && p.Suspended;
            f.tags = p.questTags == null ? "" : string.Join(" ", p.questTags.FindAll(PhysicalTags.IsNetworkPawnTag).ToArray());
            f.where = p.Discarded ? "discarded" : p.Dead ? "dead" + (f.mapId >= 0 ? " on map " + f.mapId : "") : f.spawned ? "spawned on map " + f.mapId : f.worldPawn ? "world pawn" : "held by " + (p.ParentHolder?.ToString() ?? "nobody");
            return f;
        }
    }

    /// <summary>Plain, read-only facts about one bound pawn (Episode Monitor). Never persisted.</summary>
    public sealed class PawnFacts
    {
        public int thingId = -1;
        public string label;
        public string where;
        public bool spawned;
        public bool dead;
        public bool discarded;
        public bool downed;
        public int mapId = -1;
        public string faction = "-";
        public bool worldPawn;
        public string situation = "-";
        public bool reserved;
        public string reservation = "none";
        public bool suspended;
        public string tags = "";

        public override string ToString()
        {
            return "#" + thingId + (label != null ? " " + label : "") + ": " + where + (downed ? ", DOWNED" : "") + "; world pawn " + (worldPawn ? situation : "no")
                + (suspended ? " (suspended)" : "") + "; registry " + (reserved ? "RESERVES (" + reservation + ")" : "does not reserve") + "; faction " + faction + (tags.Length > 0 ? "; tags " + tags : "");
        }
    }
}
