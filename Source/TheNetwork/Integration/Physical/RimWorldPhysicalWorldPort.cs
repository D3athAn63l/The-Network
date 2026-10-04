using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
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

        public override string ToString()
        {
            return "projections " + projections + " (failed " + projectionFailures + ", attempts " + attempts + ", corrections " + corrections + ", rejected " + rejected
                + ", " + (projections > 0 ? (projectionMsTotal / projections).ToString("0.0") : "0") + " ms avg, " + projectionMsMax.ToString("0.0") + " ms max)"
                + ", catch-ups " + catchUps + " (" + agedTicks + " ticks), placements " + placements + " (refused " + placeRefusals + ")"
                + ", normalized injuries " + normalizedInjuries + ", retention proofs " + retentionProofs + " (refused " + retentionRefusals + ")"
                + ", passes " + passes + ", observations " + observations + " (" + (observations > 0 ? (observeMsTotal * 1000.0 / observations).ToString("0") : "0") + " µs avg)"
                + ", factions " + factionsCreated + " (released " + factionReleases + "), LeftMap seen " + leftMapSeen;
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
    public sealed class RimWorldPhysicalWorldPort : IPhysicalWorldPort
    {
        /// <summary>How long a 3.1 dev visit stays at its chill spot before vanilla's own exit transition (≈ 3 in-game hours).</summary>
        public const int VisitDurationTicks = 7500;

        private readonly DomainContext ctx;
        public readonly RetainedPawnRegistry Registry;
        public readonly PhysicalAdapterCounters counters = new PhysicalAdapterCounters();

        /// <summary>The synchronous LeftMap tick of bound pawns seen this session (runtime only; the exit tick for truthful aging).</summary>
        private readonly Dictionary<Pawn, int> leftMap = new Dictionary<Pawn, int>(PawnReferenceComparer.Instance);

        /// <summary>The last first projection (Episode Monitor, RT-PHYX-011).</summary>
        public ProjectionResult lastProjection;

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

        public string Name => "RimWorld 1.6 (Phase 3.1)";

        private int Now => ctx.Now;

        // ================================================================== creation and binding support

        public PawnRef Create(ProjectionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!request.character.IsValid) throw new NotSupportedException("anonymous episode slots are Phase 3.2; Phase 3.1 materializes one named Solo");
            Faction f = request.faction?.Resolve();
            if (f == null || !f.temporary) throw new InvalidOperationException("the episode has no live temporary encounter faction");
            // M1: the registry quest exists before any pawn of ours can ever leave a map.
            Registry.EnsureQuest();
            ProjectionResult r = PawnProjection.Project(request, f);
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
            PawnProjection.ApplyNamePins(r.pawn, NamePins.From(request.name));
            Registry.Note(r.pawn, request.character);
            return new PawnRef { pawn = r.pawn, thingIdNumber = r.pawn.thingIDNumber, defName = r.pawn.def?.defName };
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
            IntVec3 entry, chill;
            if (!TryCells(map, out entry, out chill))
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
            LordJob_VisitColony job = new LordJob_VisitColony(f, chill, VisitTicks) { gifts = new List<Thing>() };
            LordMaker.MakeNewLord(f, job, map, new List<Pawn> { p });
            counters.placements++;
            NetLog.Info(LogCategory.Physical, (rematerialized ? "Rematerialized" : "Placed") + " " + p.LabelShort + " (#" + p.thingIDNumber + ") on map " + mapId + " at " + entry + " for " + episode
                + ": visit Lord to " + chill + " for " + VisitTicks + " ticks; reserved by the registry while spawned (M1): " + Registry.Reserves(p) + ".");
            return true;
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

        public static Map MapById(int mapId)
        {
            List<Map> maps = Find.Maps;
            if (maps == null) return null;
            for (int i = 0; i < maps.Count; i++) if (maps[i] != null && maps[i].uniqueID == mapId) return maps[i];
            return null;
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
            string quest = "not needed (no living Deployed, Stored or held bound person)";
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
            if (report.bound > 0 || report.durableRetained > 0)
            {
                NetLog.Info(LogCategory.Physical, "Physical load, stage 2 (post-load-init, references resolved, before the first tick): " + report + "; registry " + quest + ".");
            }
            if (report.covered < report.durableRetained)
            {
                NetLog.Error(LogCategory.Physical, "RESERVATION GAP: " + report.durableRetained + " living Deployed, Stored or held bound person(s) must be reserved but only " + report.covered
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
            int tagged = 0, stripped = 0, bound = 0;
            List<KnownCharacter> all = ctx.characters?.characters;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    KnownCharacter c = all[i];
                    Pawn p = c?.pawn?.pawn;
                    if (p == null || p.Discarded) continue;
                    bound++;
                    HashSet<string> keep = new HashSet<string>();
                    if (c.IsAlive) keep.Add(PhysicalTags.Character(c.id));
                    PhysicalEpisode e = c.episode.IsValid ? ctx.episodes?.Get(c.episode) : null;
                    if (e != null && e.IsActive) keep.Add(PhysicalTags.Episode(e.id));
                    stripped += PhysicalTags.RemoveAllExcept(p, keep);
                    foreach (string t in keep)
                    {
                        if (PhysicalTags.Has(p, t)) continue;
                        PhysicalTags.Add(p, t);
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
            string summary = "Physical load pass: " + bound + " bound pawn(s), " + retained + " of " + durable + " durable retained covered (" + (Registry.pointersResolved ? "pointer index resolved at post-load-init" : "thing-id bridge")
                + "), " + tagged + " tag(s) restored, " + stripped + " stale tag(s) stripped, registry " + quest + ".";
            if (bound > 0) NetLog.Info(LogCategory.Physical, summary);
            return summary;
        }

        /// <summary>Prepare-for-removal (§ 20 steps 2–3): the registry reserves nobody and its quest is ended; every Network tag leaves our pawns.</summary>
        public string PrepareForRemoval()
        {
            int quests = Registry.Release();
            int tags = 0;
            List<KnownCharacter> all = ctx.characters?.characters;
            if (all != null) for (int i = 0; i < all.Count; i++) tags += PhysicalTags.RemoveAllExcept(all[i]?.pawn?.pawn, null);
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
        public bool suspended;
        public string tags = "";

        public override string ToString()
        {
            return "#" + thingId + (label != null ? " " + label : "") + ": " + where + (downed ? ", DOWNED" : "") + "; world pawn " + (worldPawn ? situation : "no")
                + (suspended ? " (suspended)" : "") + "; registry " + (reserved ? "RESERVES" : "does not reserve") + "; faction " + faction + (tags.Length > 0 ? "; tags " + tags : "");
        }
    }
}
