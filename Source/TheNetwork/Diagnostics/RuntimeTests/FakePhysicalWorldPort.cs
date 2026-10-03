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
        }

        public bool available = true;

        /// <summary>When set, every Place fails (the "map is gone before spawn" case).</summary>
        public bool failPlace;

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

        public bool Available => available;
        public string Name => "Fake (tokens; sandbox and tests only)";

        // ------------------------------------------------------------------ scripting

        /// <summary>The next <paramref name="times"/> requests of <paramref name="action"/> (create, age, place, normalize, retain, pass, strip) throw.</summary>
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

        /// <summary>A normal edge exit, performed by "vanilla": despawned and ALREADY passed to the world, with exit evidence.</summary>
        public void ExitNormally(PawnRef p, int tileId, float health = 1f, bool downed = false)
        {
            Token t = TokenOf(p);
            if (t == null) return;
            t.spawned = false;
            t.inWorldPawns = true;
            t.scripted = new PhysicalObservation { kind = ObservedKind.WorldFree, exitEvidence = true, health = health, downed = downed, tile = new TileRef { tileId = tileId }, mapId = t.mapId };
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
            Token t = TokenOf(p);
            if (t == null) return;
            t.held = true;
            t.scripted = new PhysicalObservation { kind = kind, mapId = t.mapId };
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
                t.scripted = new PhysicalObservation { kind = ObservedKind.WorldFree, exitEvidence = true, tile = new TileRef { tileId = tileId }, mapId = mapId };
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

        public PawnRef Create(ProjectionRequest request)
        {
            Fault("create", null);
            Token t = new Token { thingId = nextThing++, def = "Fake_Human", character = request.character, slot = request.slot };
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
            catchUps++;
            lastCatchUp = elapsedTicks;
            if (t != null)
            {
                t.agedTicks += elapsedTicks;
                t.ageRequests++;
            }
            actions.Add("age " + pawn?.thingIdNumber + " +" + elapsedTicks);
        }

        public bool Place(PawnRef pawn, EpisodeId episode, TileRef tile, int mapId)
        {
            Token t = TokenOf(pawn);
            Fault("place", t);
            if (t == null || failPlace) return false;
            t.spawned = true;
            t.inWorldPawns = false; // placing a token takes it out of the (simulated) world-pawn set
            t.mapId = mapId;
            t.tag = episode;
            t.scripted = null;
            places++;
            actions.Add("place " + t.thingId);
            return true;
        }

        public PhysicalObservation Observe(PawnRef pawn, EpisodeId episode)
        {
            observes++;
            Token t = TokenOf(pawn);
            if (t == null || t.gone) return PhysicalObservation.Of(ObservedKind.Gone);
            if (t.scripted != null) return t.scripted;
            if (t.dead) return PhysicalObservation.Of(ObservedKind.Dead);
            if (t.spawned) return new PhysicalObservation { kind = ObservedKind.Spawned, mapId = t.mapId };
            return PhysicalObservation.Of(ObservedKind.Unknown);
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
