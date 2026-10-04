using System.Collections.Generic;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    /// <summary>
    /// The SCRIPTABLE FAKE physical world (PHYSICAL_LIFECYCLE § 21.1, § 21.3): the same <see cref="IPhysicalWorldPort"/> the future
    /// real adapter implements, so the lifecycle under test is the production lifecycle. It hands out pawn TOKENS (plain numbers
    /// in a dictionary), lets a test script what <see cref="Observe"/> returns, records every action it was asked to perform, and
    /// can be told to throw on a chosen action (fault injection). It enforces the § 7.5 three-part precondition on its own
    /// <see cref="PassToWorld"/> and records a rejection, so a lifecycle bug that would pass an already-passed pawn is caught.
    ///
    /// It is the "world" of a sandbox or a headless test: the Network's durable state (episodes, bindings) can be saved and loaded
    /// through the real Scribe while the same fake instance keeps its tokens, which is exactly the save/reload reconstruction the
    /// tests need. It references no RimWorld API: no pawn, thing, map, faction, Lord or WorldPawns is ever touched; nothing in it
    /// is persisted.
    /// </summary>
    public sealed class FakePhysicalWorldPort : IPhysicalWorldPort
    {
        public sealed class Token
        {
            public int thingId;
            public string def;
            public CharacterId character;
            public int slot;
            public bool spawned;
            public int mapId = -1;
            public bool inWorldPawns;
            public bool dead;
            public bool gone;
            public bool held;

            /// <summary>
            /// M1 (ADR-053): a NAMED token is covered by the registry from its binding on, so vanilla sees it as ReservedByQuest the instant it
            /// is passed. False scripts the reservation failing (a missing registry quest): the pawn is then an ordinary Free world pawn. An
            /// anonymous token is never covered.
            /// </summary>
            public bool registryReserves = true;

            /// <summary>Scripts "a quest other than the Network's also reserves this pawn".</summary>
            public bool otherQuestReserves;

            /// <summary>Phase 3.2A: scripts "a permanent faction other than the player's made this pawn a member" (a captor recruited it).</summary>
            public bool otherAllegiance;

            /// <summary>How and where vanilla made the pawn a world pawn (a normal exit or a map removal); null = it never left a map.</summary>
            public ExitRecord exit;

            /// <summary>What Observe returns; null = derived from the token's state.</summary>
            public PhysicalObservation scripted;

            public long agedTicks;
            public int ageRequests;
            public int normalized;
            public int retainCalls;
            public bool retained;
            public EpisodeId tag;
            public int tagStrips;
            public int passedToWorld;

            /// <summary>The encounter faction the token was last placed in (a fake load id).</summary>
            public int factionId = -1;
        }

        /// <summary>The facts of a pawn's exit, kept so the observation is derived at the moment of observing (it can change afterwards).</summary>
        public sealed class ExitRecord
        {
            public int tileId;
            public float health = 1f;
            public bool downed;
            public int mapId = -1;
        }

        public bool available = true;

        /// <summary>When set, every Place fails (the "map is gone before spawn" case).</summary>
        public bool failPlace;

        /// <summary>With <see cref="failPlace"/>: the failed placement THROWS (after <see cref="onPlaceFailed"/> ran) instead of returning false, as a mod's exception inside SpawnSetup would.</summary>
        public bool failPlaceThrows;

        /// <summary>
        /// One-shot: the next truthful-aging catch-up completes this many ticks, then a step THROWS after already advancing the pawn by the whole
        /// step (vanilla's non-atomic mothball step). −1 = off. Models <see cref="AgingUncertainException"/>.
        /// </summary>
        public long ageUncertainAfter = -1;

        /// <summary>
        /// Scripts what a failed placement left behind (for example a pawn that ended up spawned, held, dead or gone anyway), so a
        /// test can drive RELEASE's precondition for a never-placed member through the production lifecycle.
        /// </summary>
        public System.Action<Token> onPlaceFailed;

        public readonly Dictionary<int, Token> tokens = new Dictionary<int, Token>();

        /// <summary>Every action requested, in order ("create 900001", "place 900001", "pass-rejected 900003 AlreadyInWorldPawns", ...).</summary>
        public readonly List<string> actions = new List<string>();

        private readonly Dictionary<string, int> faults = new Dictionary<string, int>();
        private int nextThing = 900001;

        public int creates;
        public int places;
        public int observes;
        public int catchUps;
        public long lastCatchUp = -1;
        public int passCalls;
        public int passRejected;
        public int normalizes;
        public int retains;
        public int strips;
        public int factionsCreated;
        public int factionReleases;

        /// <summary>Live fake encounter factions (by fake load id); a released one stays released (idempotent).</summary>
        public readonly Dictionary<int, bool> factions = new Dictionary<int, bool>();

        private int nextFaction = 70001;

        public bool Available => available;
        public string Name => "Fake (tokens; sandbox and tests only)";

        // ------------------------------------------------------------------ scripting

        /// <summary>The next <paramref name="times"/> requests of <paramref name="action"/> (create, age, place, observe, normalize, retain, pass, strip, faction, faction-release) throw.</summary>
        public void ThrowOn(string action, int times = 1)
        {
            faults[action] = times;
        }

        public void ClearFaults()
        {
            faults.Clear();
        }

        public Token TokenOf(PawnRef p)
        {
            Token t;
            return p != null && tokens.TryGetValue(p.thingIdNumber, out t) ? t : null;
        }

        public void Script(PawnRef p, PhysicalObservation o)
        {
            Token t = TokenOf(p);
            if (t != null) t.scripted = o;
        }

        /// <summary>
        /// A normal edge exit, performed by "vanilla": despawned and ALREADY passed to the world. What the Network then observes is derived from
        /// the token's reservation by the same pure rule the real adapter uses (<see cref="WorldPawnRules"/>): a named token is reserved by the
        /// registry (M1), so it is WorldFree with exit evidence; an anonymous token is an ordinary Free world pawn.
        /// </summary>
        public void ExitNormally(PawnRef p, int tileId, float health = 1f, bool downed = false)
        {
            Token t = TokenOf(p);
            if (t == null) return;
            t.spawned = false;
            t.inWorldPawns = true;
            t.exit = new ExitRecord { tileId = tileId, health = health, downed = downed, mapId = t.mapId };
            t.scripted = null;
        }

        /// <summary>The M1 reservation fails: vanilla passes the pawn while the registry does not cover it, so it is an actual Free world pawn.</summary>
        public void ExitFree(PawnRef p, int tileId, float health = 1f, bool downed = false)
        {
            BreakReservation(p);
            ExitNormally(p, tileId, health, downed);
        }

        public void BreakReservation(PawnRef p)
        {
            Token t = TokenOf(p);
            if (t != null) t.registryReserves = false;
        }

        /// <summary>The owner repairs the registry by hand (a test of what a LATER positive observation does; the Network never does this itself).</summary>
        public void RepairReservation(PawnRef p)
        {
            Token t = TokenOf(p);
            if (t != null) t.registryReserves = true;
        }

        /// <summary>The pawn died where it was (a corpse; not a world pawn).</summary>
        public void Die(PawnRef p)
        {
            Token t = TokenOf(p);
            if (t == null) return;
            t.dead = true;
            t.scripted = new PhysicalObservation { kind = ObservedKind.Dead, mapId = t.mapId };
        }

        /// <summary>Discarded with no evidence (a mod removed it): Gone.</summary>
        public void Vanish(PawnRef p)
        {
            Token t = TokenOf(p);
            if (t == null) return;
            t.gone = true;
            t.spawned = false;
            t.scripted = null;
        }

        public void Hold(PawnRef p, ObservedKind kind)
        {
            Hold(p, kind, HeldKind.None);
        }

        /// <summary>A vanilla holder takes the pawn (Phase 3.2A): the observation names the holder as the real adapter would.</summary>
        public void Hold(PawnRef p, ObservedKind kind, HeldKind heldBy)
        {
            Token t = TokenOf(p);
            if (t == null) return;
            t.held = true;
            t.scripted = new PhysicalObservation { kind = kind, holder = heldBy, mapId = t.mapId };
        }

        /// <summary>
        /// Vanilla lets a held pawn go (released, escaped, its map removed, a captor's caravan dissolved): it is a WORLD pawn again, passed by
        /// "vanilla", never by the Network. A named token stays covered by the registry (M1 includes OutOfCustody, ADR-056), so it is observed
        /// WorldFree with exit evidence; the tile is unknown (a world pawn has none) unless one is given.
        /// </summary>
        public void Free(PawnRef p, int tileId = -1)
        {
            Token t = TokenOf(p);
            if (t == null) return;
            t.held = false;
            t.spawned = false;
            t.inWorldPawns = true;
            t.exit = new ExitRecord { tileId = tileId, mapId = t.mapId };
            t.scripted = null;
        }

        /// <summary>A captor recruits the held pawn (vanilla's ≈ 30-day MTB): a free world pawn, but a member of another permanent faction.</summary>
        public void JoinOtherFaction(PawnRef p)
        {
            Free(p);
            Token t = TokenOf(p);
            if (t != null) t.otherAllegiance = true;
        }

        public void Wound(PawnRef p, float health, bool downed)
        {
            Token t = TokenOf(p);
            if (t == null) return;
            t.scripted = new PhysicalObservation { kind = ObservedKind.Spawned, health = health, downed = downed, mapId = t.mapId };
        }

        /// <summary>
        /// The map is removed with members still on it: like vanilla's MapDeiniter, every spawned token is passed to the world WITHOUT
        /// a LeftMap signal; the episode map no longer holding the pawn is the exit evidence (§ 12.3).
        /// </summary>
        public int RemoveMap(int mapId, int tileId)
        {
            int n = 0;
            foreach (Token t in tokens.Values)
            {
                if (!t.spawned || t.mapId != mapId || t.dead || t.gone) continue;
                t.spawned = false;
                t.inWorldPawns = true;
                t.exit = new ExitRecord { tileId = tileId, mapId = mapId };
                t.scripted = null;
                n++;
            }
            actions.Add("map-removed " + mapId + " (" + n + " passed by vanilla)");
            return n;
        }

        public int Count(string prefix)
        {
            int n = 0;
            for (int i = 0; i < actions.Count; i++) if (actions[i].StartsWith(prefix, System.StringComparison.Ordinal)) n++;
            return n;
        }

        private void Fault(string action, Token t)
        {
            int left;
            if (!faults.TryGetValue(action, out left) || left <= 0) return;
            faults[action] = left - 1;
            actions.Add("fault " + action + (t != null ? " " + t.thingId : ""));
            throw new InjectedFaultException("fake port: " + action);
        }

        // ------------------------------------------------------------------ IPhysicalWorldPort

        /// <summary>Every creation request, as given (RT-PHYS-021/022: what the lifecycle asked for, never what a pawn became).</summary>
        public readonly List<ProjectionRequest> requests = new List<ProjectionRequest>();

        public PawnRef Create(ProjectionRequest request)
        {
            requests.Add(request);
            Fault("create", null);
            Token t = new Token { thingId = nextThing++, def = "Fake_Human", character = request.character, slot = request.slot, registryReserves = request.character.IsValid };
            tokens[t.thingId] = t;
            creates++;
            actions.Add("create " + t.thingId);
            return new PawnRef { thingIdNumber = t.thingId, defName = t.def };
        }

        public bool Resolves(PawnRef pawn)
        {
            Token t = TokenOf(pawn);
            return t != null && !t.gone;
        }

        public void CatchUpAge(PawnRef pawn, long elapsedTicks)
        {
            Token t = TokenOf(pawn);
            Fault("age", t);
            if (ageUncertainAfter >= 0)
            {
                long completed = System.Math.Min(ageUncertainAfter, elapsedTicks);
                long step = System.Math.Min(3600000L, elapsedTicks - completed);
                ageUncertainAfter = -1;
                catchUps++;
                lastCatchUp = elapsedTicks;
                // Vanilla advances the WHOLE failing step before its birthday effects can throw, so the pawn really is older by it.
                if (t != null)
                {
                    t.agedTicks += completed + step;
                    t.ageRequests++;
                }
                actions.Add("age-uncertain " + pawn?.thingIdNumber + " completed " + completed + " step " + step);
                throw new AgingUncertainException(completed, step, 0, step, new InjectedFaultException("fake port: a birthday effect threw"));
            }
            catchUps++;
            lastCatchUp = elapsedTicks;
            if (t != null)
            {
                t.agedTicks += elapsedTicks;
                t.ageRequests++;
            }
            actions.Add("age " + pawn?.thingIdNumber + " +" + elapsedTicks);
        }

        public FactionRef EnsureEncounterFaction(EpisodeId episode, ActorId actor, FactionRef current, int seededGoodwill)
        {
            Fault("faction", null);
            bool live;
            if (current != null && current.IsValid && factions.TryGetValue(current.loadId, out live) && live) return current;
            int id = nextFaction++;
            factions[id] = true;
            factionsCreated++;
            actions.Add("faction " + id + " for " + episode + " goodwill " + seededGoodwill);
            return new FactionRef { loadId = id, name = "Fake encounter faction " + id, defName = "Fake_Encounter" };
        }

        public void ReleaseEncounterFaction(FactionRef faction)
        {
            Fault("faction-release", null);
            if (faction == null || !faction.IsValid) return;
            factionReleases++;
            factions[faction.loadId] = false;
            actions.Add("faction-release " + faction.loadId);
        }

        public bool Place(PawnRef pawn, EpisodeId episode, TileRef tile, int mapId, FactionRef faction)
        {
            Token t = TokenOf(pawn);
            Fault("place", t);
            if (t == null) return false;
            if (failPlace)
            {
                onPlaceFailed?.Invoke(t);
                if (failPlaceThrows) throw new InjectedFaultException("fake port: place threw after the attempt");
                return false;
            }
            t.spawned = true;
            t.inWorldPawns = false; // placing a token takes it out of the (simulated) world-pawn set
            t.mapId = mapId;
            t.tag = episode;
            t.factionId = faction?.loadId ?? -1;
            t.scripted = null;
            places++;
            actions.Add("place " + t.thingId);
            return true;
        }

        public PhysicalObservation Observe(PawnRef pawn, EpisodeId episode)
        {
            observes++;
            Token t = TokenOf(pawn);
            Fault("observe", t);
            if (t == null || t.gone) return PhysicalObservation.Of(ObservedKind.Gone);
            if (t.scripted != null) return t.scripted;
            if (t.dead) return PhysicalObservation.Of(ObservedKind.Dead);
            if (t.spawned) return new PhysicalObservation { kind = ObservedKind.Spawned, mapId = t.mapId };
            if (t.exit != null && t.inWorldPawns) return WorldObservation(t);
            return PhysicalObservation.Of(ObservedKind.Unknown);
        }

        /// <summary>A world pawn that left a map, observed NOW through the same pure rule as the real adapter (M1: a named token is reserved by the registry).</summary>
        private static PhysicalObservation WorldObservation(Token t)
        {
            bool named = t.character.IsValid;
            WorldPawnFacts facts = new WorldPawnFacts
            {
                retained = named,
                situation = named && t.registryReserves ? WorldSituation.ReservedByQuest : named && t.otherQuestReserves ? WorldSituation.ReservedByQuest : WorldSituation.Free,
                otherQuestReserves = t.otherQuestReserves
            };
            ObservedKind kind = WorldPawnRules.KindOf(facts);
            PhysicalObservation o = new PhysicalObservation { kind = kind, mapId = t.exit.mapId, otherAllegiance = t.otherAllegiance };
            if (kind == ObservedKind.WorldFree)
            {
                o.exitEvidence = true;
                o.health = t.exit.health;
                o.downed = t.exit.downed;
                o.tile = new TileRef { tileId = t.exit.tileId };
            }
            else if (kind == ObservedKind.ReservationBroken)
            {
                o.note = WorldPawnRules.BrokenReservation;
            }
            return o;
        }

        public void Normalize(PawnRef pawn)
        {
            Token t = TokenOf(pawn);
            Fault("normalize", t);
            normalizes++;
            if (t != null) t.normalized++;
            actions.Add("normalize " + pawn?.thingIdNumber);
        }

        public void EnsureRetained(PawnRef pawn)
        {
            Token t = TokenOf(pawn);
            Fault("retain", t);
            retains++;
            if (t != null)
            {
                t.retainCalls++;
                // A PROOF, like the real adapter's (ADR-053): it establishes and creates nothing, and a reservation that is not in force is refused.
                if (t.character.IsValid && !t.registryReserves)
                {
                    actions.Add("retain-refused " + pawn?.thingIdNumber);
                    throw new System.InvalidOperationException("RELEASE: the retained reservation is not proven (the fake registry does not cover " + t.thingId + ")");
                }
                t.retained = true;
            }
            actions.Add("retain " + pawn?.thingIdNumber);
        }

        public PassToWorldCheck CheckPassToWorld(PawnRef pawn)
        {
            Token t = TokenOf(pawn);
            if (t == null || t.gone) return PassToWorldCheck.Unknown;
            if (t.dead) return PassToWorldCheck.Dead;
            if (t.held) return PassToWorldCheck.Held;
            if (t.spawned) return PassToWorldCheck.Spawned;
            if (t.inWorldPawns) return PassToWorldCheck.AlreadyInWorldPawns;
            return PassToWorldCheck.Allowed;
        }

        /// <summary>Validates the § 7.5 precondition itself: a violating request is REJECTED, recorded and thrown, never performed.</summary>
        public void PassToWorld(PawnRef pawn)
        {
            Token t = TokenOf(pawn);
            Fault("pass", t);
            passCalls++;
            PassToWorldCheck check = CheckPassToWorld(pawn);
            if (check != PassToWorldCheck.Allowed)
            {
                passRejected++;
                actions.Add("pass-rejected " + pawn?.thingIdNumber + " " + check);
                throw new PhysicalPreconditionException("PassToWorld", check);
            }
            t.inWorldPawns = true;
            t.passedToWorld++;
            actions.Add("pass " + t.thingId);
        }

        public void StripEpisodeTag(PawnRef pawn, EpisodeId episode)
        {
            Token t = TokenOf(pawn);
            Fault("strip", t);
            strips++;
            if (t != null)
            {
                if (t.tag == episode) t.tag = EpisodeId.None;
                t.tagStrips++;
            }
            actions.Add("strip " + pawn?.thingIdNumber);
        }
    }
}
