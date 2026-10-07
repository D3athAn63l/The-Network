using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    /// <summary>The tier's log lines: one recognisable prefix, so the owner can cut the blocks out of Player.log.</summary>
    public static class PhysLog
    {
        public static void Info(string line)
        {
            Verse.Log.Message(PhysicalTestIds.LogPrefix + line);
        }

        public static void Warn(string line)
        {
            Verse.Log.Warning(PhysicalTestIds.LogPrefix + line);
        }

        public static int Tick => Find.TickManager?.TicksGame ?? -1;
    }

    /// <summary>A mark in vanilla's log, to find the errors (and vanilla's "already here" warning) written during a run. Read-only.</summary>
    public sealed class LogMark
    {
        private readonly Dictionary<LogMessage, int> seen = new Dictionary<LogMessage, int>();

        public static LogMark Take()
        {
            LogMark m = new LogMark();
            try
            {
                foreach (LogMessage msg in Verse.Log.Messages) if (msg != null) m.seen[msg] = msg.repeats;
            }
            catch (Exception)
            {
                // The log is read for evidence only; a mark taken during a concurrent write is merely partial.
            }
            return m;
        }

        /// <summary>Lines written (or repeated) since the mark, of the given type (null = any) and containing the text (null = any).</summary>
        public List<string> Since(LogMessageType? type, string contains)
        {
            List<string> r = new List<string>();
            try
            {
                foreach (LogMessage msg in Verse.Log.Messages)
                {
                    if (msg?.text == null) continue;
                    if (type.HasValue && msg.type != type.Value) continue;
                    if (contains != null && msg.text.IndexOf(contains, StringComparison.Ordinal) < 0) continue;
                    int before;
                    if (seen.TryGetValue(msg, out before) && before == msg.repeats) continue;
                    r.Add(msg.text.Length > 300 ? msg.text.Substring(0, 300) : msg.text);
                }
            }
            catch (Exception)
            {
            }
            return r;
        }
    }

    /// <summary>One bound or disposable pawn at one instant (evidence; never stored).</summary>
    public sealed class PawnSnap
    {
        public string when;
        public int tick;
        public int thingId = -1;
        public bool exists;
        public bool spawned;
        public int mapId = -1;
        public bool worldPawn;
        public WorldPawnSituation situation = WorldPawnSituation.None;
        public bool reserved;
        public bool suspended;
        public int factionId = -1;
        public bool dead;
        public bool downed;
        public float health = 1f;
        public long bioTicks = -1;
        public long chronoTicks = -1;
        public long birthAbs = -1;

        public static PawnSnap Of(Pawn p, string when)
        {
            PawnSnap s = new PawnSnap { when = when, tick = PhysLog.Tick };
            if (p == null) return s;
            s.exists = !p.Discarded;
            s.thingId = p.thingIDNumber;
            s.spawned = p.Spawned;
            s.mapId = p.MapHeld?.uniqueID ?? -1;
            s.worldPawn = Find.WorldPawns.Contains(p);
            s.situation = s.worldPawn ? Find.WorldPawns.GetSituation(p) : WorldPawnSituation.None;
            s.reserved = RetainedPawnRegistry.Active != null && RetainedPawnRegistry.Active.Reserves(p);
            s.suspended = s.worldPawn && p.Suspended;
            s.factionId = p.Faction?.loadID ?? -1;
            s.dead = p.Dead;
            s.downed = !p.Dead && p.Downed;
            s.health = p.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
            s.bioTicks = p.ageTracker?.AgeBiologicalTicks ?? -1;
            s.chronoTicks = p.ageTracker?.AgeChronologicalTicks ?? -1;
            s.birthAbs = p.ageTracker?.BirthAbsTicks ?? -1;
            return s;
        }

        public string Line()
        {
            return when + " @" + tick + ": #" + thingId + (exists ? "" : " DISCARDED") + (spawned ? " spawned on map " + mapId : "") + (worldPawn ? " world pawn " + situation : " not a world pawn")
                + (reserved ? ", registry RESERVES" : ", registry does not reserve") + (suspended ? ", suspended" : "") + ", faction " + factionId + (dead ? ", DEAD" : "") + (downed ? ", DOWNED" : "")
                + ", health " + health.ToString("0.00") + ", bio " + bioTicks + ", birthAbs " + birthAbs;
        }
    }

    /// <summary>
    /// A RUNTIME-ONLY signal receiver for the Network's pawn tags (registered for one run, never saved). It records what it sees at the
    /// synchronous instant vanilla sends "&lt;tag&gt;.LeftMap" (inside Pawn.ExitMap, right after vanilla's own PassToWorld) and changes nothing.
    /// Production routing is SignalBridge's; this is the test's eyes only.
    /// </summary>
    public sealed class PhysicalSignalObserver : ISignalReceiver
    {
        public sealed class Seen
        {
            public int tick;
            public string tag;
            public string part;
            public PawnSnap snap;
            public int characterCustodyAtSignal = -1;
        }

        public readonly List<Seen> events = new List<Seen>();

        public void Notify_SignalReceived(Signal signal)
        {
            try
            {
                string tag = signal.tag;
                if (tag == null || !tag.StartsWith(PhysicalTags.CharacterPrefix, StringComparison.Ordinal)) return;
                int id;
                string part;
                if (!PhysicalTags.TryParse(tag, PhysicalTags.CharacterPrefix, out id, out part) || part == null) return;
                Pawn subject;
                signal.args.TryGetArg("SUBJECT", out subject);
                Seen s = new Seen { tick = PhysLog.Tick, tag = tag, part = part, snap = subject == null ? null : PawnSnap.Of(subject, "at signal " + part) };
                KnownCharacter c = NetworkRuntime.Current?.Ctx?.characters?.Get(new CharacterId(id));
                if (c != null) s.characterCustodyAtSignal = (int)c.custody;
                if (events.Count < 500) events.Add(s);
                PhysLog.Info("vanilla signal " + tag + (s.snap != null ? ": " + s.snap.Line() : "") + (c != null ? "; custody at the signal " + c.custody : ""));
            }
            catch (Exception ex)
            {
                PhysLog.Warn("signal observer error (observation only, nothing changed): " + ex.Message);
            }
        }

        public Seen First(string part, int thingId, int sinceTick)
        {
            for (int i = 0; i < events.Count; i++)
            {
                Seen e = events[i];
                if (e.part == part && e.snap != null && e.snap.thingId == thingId && e.tick >= sinceTick) return e;
            }
            return null;
        }

        public int Count(string part, int thingId, int sinceTick)
        {
            int n = 0;
            for (int i = 0; i < events.Count; i++)
            {
                Seen e = events[i];
                if (e.part == part && e.snap != null && e.snap.thingId == thingId && e.tick >= sinceTick) n++;
            }
            return n;
        }
    }

    /// <summary>
    /// The suite's own dedicated test map (§ 21.2): a plain MapParent (no comps, not a home, no incident target) on an empty temperate tile.
    /// The world object's def IS its tag: nothing else ever creates <see cref="PhysicalTestIds.TestMapDef"/>. Never the colony map.
    /// </summary>
    public static class TestSite
    {
        public const int MapSize = 60;

        public static WorldObjectDef Def => DefDatabase<WorldObjectDef>.GetNamedSilentFail(PhysicalTestIds.TestMapDef);

        public static int Count
        {
            get
            {
                int count = 0;
                if (Def != null && Find.WorldObjects != null)
                    foreach (WorldObject site in Find.WorldObjects.AllWorldObjects)
                        if (site.def == Def && !site.Destroyed) count++;
                return count;
            }
        }

        /// <summary>The narrowness check on the def actually loaded (a patch could have widened it).</summary>
        public static string DefRefusal(WorldObjectDef def)
        {
            if (def == null) return "the WorldObjectDef " + PhysicalTestIds.TestMapDef + " is not loaded";
            if (def.worldObjectClass != typeof(MapParent)) return def.defName + " is a " + def.worldObjectClass + ", not exactly MapParent";
            if (def.comps != null && def.comps.Count > 0) return def.defName + " has " + def.comps.Count + " comps (a timeout, raid or abandon comp would disturb the tests)";
            if (def.canBePlayerHome) return def.defName + " can be a player home";
            if (def.IncidentTargetTags != null && def.IncidentTargetTags.Count > 0) return def.defName + " is an incident target";
            return null;
        }

        public static MapParent FindParent()
        {
            WorldObjectDef def = Def;
            if (def == null || Find.WorldObjects == null) return null;
            foreach (WorldObject w in Find.WorldObjects.AllWorldObjects) if (w is MapParent mp && w.def == def && !w.Destroyed) return mp;
            return null;
        }

        public static Map Map
        {
            get
            {
                MapParent p = FindParent();
                return p != null && p.HasMap ? p.Map : null;
            }
        }

        public static bool IsTestMap(Map m)
        {
            return m != null && m.Parent != null && m.Parent.def == Def;
        }

        /// <summary>Creates the world object (once) and its map (again after a removal). Null with a reason on refusal.</summary>
        public static Map Create(out string report)
        {
            if (Count > 1) { report = "refused: multiple dedicated TestSites exist; resolve ownership before provisioning"; return null; }
            WorldObjectDef def = Def;
            string why = DefRefusal(def);
            if (why != null)
            {
                report = "refused: " + why;
                return null;
            }
            MapParent parent = FindParent();
            if (Count == 1 && parent == null) { report = "refused: the existing TestSite object is not an approved MapParent"; return null; }
            if (parent != null && (parent.GetType() != typeof(MapParent) || parent.Faction != null || (parent.HasMap && parent.Map.IsPlayerHome)))
            {
                report = "refused: the dedicated test site has acquired faction ownership or home-map status";
                return null;
            }
            if (parent == null)
            {
                PlanetTile tile = TileFinder.RandomSettlementTileFor(Faction.OfPlayer, false, t => !Find.WorldObjects.AnyWorldObjectAt(t)
                    && Find.World.tileTemperatures.SeasonAndOutdoorTemperatureAcceptableFor(t, ThingDefOf.Human));
                if (!tile.Valid)
                {
                    report = "refused: no empty, temperate tile was found for the test map";
                    return null;
                }
                parent = (MapParent)WorldObjectMaker.MakeWorldObject(def);
                parent.Tile = tile;
                Find.WorldObjects.Add(parent);
                PhysLog.Info("test site created: world object " + parent.ID + " (" + def.defName + ", plain MapParent, no faction) on tile " + tile);
            }
            if (!parent.HasMap)
            {
                Map m = GetOrGenerateMapUtility.GetOrGenerateMap(parent.Tile, new IntVec3(MapSize, 1, MapSize), def);
                if (m == null || !ReferenceEquals(m.Parent, parent) || m.IsPlayerHome)
                { report = "refused: map generation did not return the exact non-home TestSite"; return null; }
                m.fogGrid.ClearAllFog();
                PhysLog.Info("test map generated: map " + m.uniqueID + " (" + MapSize + "x" + MapSize + ") on world object " + parent.ID);
            }
            string labReport;
            bool valid = QaLab.Validate(parent.Map, out labReport);
            report = "TestSite world object " + parent.ID + ", map " + parent.Map.uniqueID + ", " + parent.Map.Size.x + "x" + parent.Map.Size.z
                + "; QA Lab valid=" + valid + ": " + labReport + ". Create does not initialize or reset the lab.";
            return parent.Map;
        }

        public static Map GetExisting(out string report)
        {
            if (Count > 1) { report = "Multiple dedicated TestSites exist; lab ownership is ambiguous."; return null; }
            Map map = Map;
            report = map == null ? "No physical TestSite map exists. Use Dev Mode -> PHYX — Create 60×60 Test Map first." : "existing TestSite map " + map.uniqueID;
            return map;
        }

        public static Map GetPrepared(out string report)
        {
            Map map = GetExisting(out report);
            if (map == null) return null;
            if (!QaLab.Validate(map, out report)) { report = QaLab.SetupInstruction + " " + report; return null; }
            return map;
        }

        /// <summary>
        /// Removes the test map through vanilla's own removal (Game.DeinitAndRemoveMap, as MapParent.CheckRemoveMapNow does): vanilla passes
        /// every pawn on it to the world, with NO LeftMap for a non-player pawn. Bound pawns are passed, never destroyed. Optionally also the
        /// world object. Returns what happened.
        /// </summary>
        public static string RemoveMap(bool alsoWorldObject)
        {
            MapParent parent = FindParent();
            if (parent == null) return "no test site exists";
            string r = "";
            if (parent.HasMap)
            {
                int id = parent.Map.uniqueID;
                Current.Game.DeinitAndRemoveMap(parent.Map, false);
                r = "test map " + id + " removed through Game.DeinitAndRemoveMap";
            }
            if (alsoWorldObject && !parent.Destroyed)
            {
                int wid = parent.ID;
                parent.Destroy();
                r += (r.Length > 0 ? "; " : "") + "test site world object " + wid + " destroyed";
            }
            return r.Length > 0 ? r : "nothing to remove";
        }

        /// <summary>Incomplete episodes with a member spawned on the test map (cleanup refuses to remove the map under them).</summary>
        public static int IncompleteMembersOnMap(DomainContext ctx)
        {
            Map m = Map;
            if (m == null || ctx?.episodes == null) return 0;
            int n = 0;
            foreach (PhysicalEpisode e in ctx.episodes.Incomplete())
            {
                for (int i = 0; i < e.members.Count; i++)
                {
                    Pawn p = e.members[i].pawn?.pawn;
                    if (p != null && p.Spawned && p.Map == m) n++;
                }
            }
            return n;
        }
    }

    /// <summary>
    /// The pawn-level sentinel (§ 21.2), the counterpart of ColonySentinel. Bounded tripwires on what a physical test could break, with
    /// every delta EXPLAINED or reported. The game may be unpaused for minutes during a run, so vanilla's own world activity (quest sites,
    /// world-pawn GC, generated travellers) is reported as notes, never mistaken for the test's effect. A FAIL is reserved for what the
    /// suite promises never to touch: the player's colonists, prisoners and slaves; other Network people's custody and bindings; a
    /// retained pawn leaving WorldPawns other than by being spawned.
    /// </summary>
    public sealed class PhysicalSentinel
    {
        private readonly HashSet<Pawn> colonyPawns = new HashSet<Pawn>(PawnReferenceComparer.Instance);
        private readonly HashSet<Pawn> worldPawns = new HashSet<Pawn>(PawnReferenceComparer.Instance);
        private readonly HashSet<int> permanentFactions = new HashSet<int>();
        private readonly HashSet<int> temporaryFactions = new HashSet<int>();
        private readonly HashSet<int> worldObjects = new HashSet<int>();
        private readonly Dictionary<int, string> people = new Dictionary<int, string>();
        public int freeWorldPawns;
        public int tick;

        public static PhysicalSentinel Capture(DomainContext ctx)
        {
            PhysicalSentinel s = new PhysicalSentinel { tick = PhysLog.Tick };
            foreach (Map m in Find.Maps)
            {
                if (!m.IsPlayerHome) continue;
                foreach (Pawn p in m.mapPawns.FreeColonists) s.colonyPawns.Add(p);
                foreach (Pawn p in m.mapPawns.PrisonersOfColony) s.colonyPawns.Add(p);
                foreach (Pawn p in m.mapPawns.SlavesOfColonySpawned) s.colonyPawns.Add(p);
            }
            foreach (Pawn p in Find.WorldPawns.AllPawnsAliveOrDead) s.worldPawns.Add(p);
            s.freeWorldPawns = Find.WorldPawns.GetPawnsBySituationCount(WorldPawnSituation.Free);
            foreach (Faction f in Find.FactionManager.AllFactionsListForReading) (f.temporary ? s.temporaryFactions : s.permanentFactions).Add(f.loadID);
            WorldObjectDef site = TestSite.Def;
            foreach (WorldObject w in Find.WorldObjects.AllWorldObjects) if (w.def != site) s.worldObjects.Add(w.ID);
            if (ctx?.characters != null) foreach (KnownCharacter c in ctx.characters.characters) s.people[c.id.Value] = PersonKey(c);
            return s;
        }

        private static string PersonKey(KnownCharacter c)
        {
            // Physical fields only: the abstract simulation keeps running during a run and may change other people's status legitimately.
            return c.custody + "|" + (c.pawn != null && c.pawn.IsBound ? c.pawn.thingIdNumber : 0) + "|" + c.episode.Value;
        }

        /// <summary>
        /// Compares with a later capture. <paramref name="testPeople"/> are the characters the run drove; <paramref name="runFactions"/> the
        /// faction load ids the run is expected to have created (episode encounter factions, fixtures).
        /// </summary>
        public void CompareTo(PhysicalSentinel after, DomainContext ctx, ICollection<int> testPeople, ICollection<int> runFactions, PhysicalVerdict v)
        {
            int lostColony = 0, newColony = 0;
            foreach (Pawn p in colonyPawns) if (!after.colonyPawns.Contains(p)) lostColony++;
            foreach (Pawn p in after.colonyPawns) if (!colonyPawns.Contains(p)) newColony++;
            v.Check(lostColony == 0 && newColony == 0, "sentinel: the player's colonists, prisoners and slaves are the same pawns (" + colonyPawns.Count + "; " + lostColony + " gone, " + newColony + " new)");

            int otherPeopleChanged = 0;
            List<string> changed = new List<string>();
            if (ctx?.characters != null)
            {
                foreach (KnownCharacter c in ctx.characters.characters)
                {
                    if (testPeople != null && testPeople.Contains(c.id.Value)) continue;
                    string before;
                    if (!people.TryGetValue(c.id.Value, out before)) continue; // a person created during the run (abstract life): not ours
                    if (before != PersonKey(c))
                    {
                        otherPeopleChanged++;
                        if (changed.Count < 5) changed.Add(c.id + " " + before + " → " + PersonKey(c));
                    }
                }
            }
            v.Check(otherPeopleChanged == 0, "sentinel: no other Network person's custody, binding or episode link changed" + (changed.Count > 0 ? " (" + string.Join("; ", changed.ToArray()) + ")" : ""));

            int permanentGone = 0;
            foreach (int f in permanentFactions) if (!after.permanentFactions.Contains(f)) permanentGone++;
            v.Check(permanentGone == 0, "sentinel: no permanent faction was removed (" + permanentFactions.Count + " before)");
            int newTemporary = 0, newExpected = 0;
            foreach (int f in after.temporaryFactions)
            {
                if (temporaryFactions.Contains(f)) continue;
                if (runFactions != null && runFactions.Contains(f)) newExpected++;
                else newTemporary++;
            }
            v.Note("sentinel: temporary factions " + temporaryFactions.Count + " → " + after.temporaryFactions.Count + " (" + newExpected + " created by this run and still present, " + newTemporary + " from elsewhere)");

            RetainedPawnRegistry reg = RetainedPawnRegistry.Active;
            int newBound = 0, newLeaders = 0, newOther = 0, goneRetained = 0, goneOther = 0;
            HashSet<Pawn> leaders = new HashSet<Pawn>(PawnReferenceComparer.Instance);
            if (runFactions != null)
            {
                foreach (int id in runFactions)
                {
                    Faction f = Find.FactionManager.AllFactionsListForReading.Find(x => x.loadID == id);
                    if (f?.leader != null) leaders.Add(f.leader);
                }
            }
            foreach (Pawn p in after.worldPawns)
            {
                if (worldPawns.Contains(p)) continue;
                if (reg != null && reg.CharacterOf(p).IsValid) newBound++;
                else if (leaders.Contains(p)) newLeaders++;
                else newOther++;
            }
            foreach (Pawn p in worldPawns)
            {
                if (after.worldPawns.Contains(p)) continue;
                if (reg != null && reg.CharacterOf(p).IsValid && !p.Spawned) goneRetained++;
                else goneOther++;
            }
            v.Check(goneRetained == 0, "sentinel: no bound Network pawn left WorldPawns other than by being spawned");
            v.Note("sentinel: world pawns " + worldPawns.Count + " → " + after.worldPawns.Count + ": new = " + newBound + " bound Network pawn(s) (explained), " + newLeaders
                + " encounter-faction leader(s) (explained: vanilla generates one per faction), " + newOther + " other (vanilla activity during the run); gone = " + goneOther
                + " (vanilla GC, spawning, or a bound pawn rematerialized)");
            v.Note("sentinel: Free world pawns " + freeWorldPawns + " → " + after.freeWorldPawns + "; world objects (test site excluded) " + worldObjects.Count + " → " + after.worldObjects.Count);
        }
    }

    /// <summary>
    /// The deterministic Solo choice: the eligible Solo contractor with the LOWEST actor id that the scenario's need allows. Eligible means
    /// exactly what the production Plan will accept (alive, Active, abstractly simulatable, on no operation, binding consistent with
    /// custody), so the scenario never fights the lifecycle. Read-only.
    /// </summary>
    public static class SoloPicker
    {
        public static KnownCharacter Pick(DomainContext ctx, SoloNeed need, ICollection<int> exclude, out NetworkActor actor, out string why)
        {
            actor = null;
            why = null;
            if (need == SoloNeed.None) return null;
            if (need == SoloNeed.Any)
            {
                KnownCharacter fresh = Pick(ctx, SoloNeed.Fresh, exclude, out actor, out why);
                if (fresh != null) return fresh;
                return Pick(ctx, SoloNeed.Stored, exclude, out actor, out why);
            }
            List<NetworkActor> all = new List<NetworkActor>(ctx.actors.actors);
            all.Sort((a, b) => a.id.Value.CompareTo(b.id.Value));
            int solos = 0;
            for (int i = 0; i < all.Count; i++)
            {
                NetworkActor a = all[i];
                // Phase 3.1's target is ONE existing NPC SOLO CONTRACTOR. IsSolo alone also counts a Fixer (an individual with an embodied person and no
                // ContractorProfile), which would let the tier PASS without ever exercising a contractor: only IsNpcSoloContractor qualifies.
                if (a == null || !a.IsActive || !ContractorService.IsNpcSoloContractor(a) || !a.bindings.embodies.IsValid) continue;
                solos++;
                KnownCharacter c = ctx.characters.Get(a.bindings.embodies);
                if (c == null || (exclude != null && exclude.Contains(c.id.Value))) continue;
                if (!c.IsAlive || c.status != CharacterStatus.Active || !AuthorityGate.CanSimulateAbstractly(c)) continue;
                if (ctx.Contractors.Occupied(a, OperationId.None).Contains(c.id)) continue;
                bool bound = c.pawn != null && c.pawn.IsBound;
                if (bound != (c.custody == CustodyState.Stored)) continue;
                if (need == SoloNeed.Fresh && (bound || c.custody != CustodyState.Unmaterialized)) continue;
                if (need == SoloNeed.Stored)
                {
                    if (!bound || c.pawn.pawn == null || c.pawn.pawn.Discarded || c.pawn.pawn.Dead) continue;
                }
                actor = a;
                return c;
            }
            why = solos == 0 ? "this save has no NPC Solo contractor actor (Fixers are not Phase 3.1 candidates)" : need == SoloNeed.Stored
                ? "no STORED Solo is available: run RT-PHYX-001, 002, 003 or 005 first (they leave the person stored)"
                : "no never-materialized, available Solo is left (" + solos + " Solo actors; busy on operations, wounded, or already materialized)";
            return null;
        }
    }

    /// <summary>
    /// Vanilla's redress rule, READ through reflection (PawnGenerator.IsValidCandidateToRedress is private; this only calls the predicate,
    /// it patches nothing). Used by RT-PHYX-007 to prove the pressure request is NOT vacuous: the stored pawn satisfies every condition of
    /// the request, so the reservation is the only thing that keeps it out of vanilla's candidate pool.
    /// </summary>
    public static class RedressProbe
    {
        private static readonly MethodInfo IsValid = typeof(PawnGenerator).GetMethod("IsValidCandidateToRedress", BindingFlags.NonPublic | BindingFlags.Static);

        public static bool Available => IsValid != null;

        /// <summary>Null when the predicate cannot be read (a different game version): the dynamic part is then inconclusive.</summary>
        public static bool? WouldBeCandidate(Pawn p, PawnGenerationRequest r)
        {
            if (IsValid == null || p == null) return null;
            try
            {
                return (bool)IsValid.Invoke(null, new object[] { p, r });
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The request's real candidate pool: vanilla's source (Free, plus FactionLeader for a leader kind) through vanilla's own predicate.</summary>
        public static List<Pawn> Pool(PawnGenerationRequest r)
        {
            List<Pawn> pool = new List<Pawn>();
            List<Pawn> source = new List<Pawn>(Find.WorldPawns.GetPawnsBySituation(WorldPawnSituation.Free));
            if (r.KindDef.factionLeader) source.AddRange(Find.WorldPawns.GetPawnsBySituation(WorldPawnSituation.FactionLeader));
            for (int i = 0; i < source.Count; i++) if (WouldBeCandidate(source[i], r) == true) pool.Add(source[i]);
            return pool;
        }
    }

    /// <summary>
    /// The suite's OWN disposable entities (RT-PHYX-007/011/012): fixture factions and disposable pawns, every one tagged with the run.
    /// The ONE place the tier discards a pawn, and it refuses anything it cannot prove is its own: tagged by THIS run, bound to no Network
    /// person, never spawned, not a world pawn. A bound pawn is never destroyed or discarded by the tier.
    /// </summary>
    public static class TestFixtures
    {
        /// <summary>A hidden temporary fixture faction (the vanilla refugee pattern), named so cleanup can recognise it.</summary>
        public static Faction MakeFaction(string runId)
        {
            FactionDef def = EncounterFactions.ChooseDef();
            if (def == null) return null;
            List<FactionRelation> relations = new List<FactionRelation>();
            foreach (Faction other in Find.FactionManager.AllFactionsListForReading)
            {
                if (other.def.PermanentlyHostileTo(def)) continue;
                relations.Add(new FactionRelation { other = other, kind = FactionRelationKind.Neutral });
            }
            Faction f = FactionGenerator.NewGeneratedFactionWithRelations(def, relations, true);
            f.temporary = true;
            f.Name = PhysicalTestIds.FixtureFactionName + " " + runId;
            Find.FactionManager.Add(f);
            PhysLog.Info("fixture faction " + f.loadID + " \"" + f.Name + "\" created (hidden, temporary; def " + def.defName + ")");
            return f;
        }

        public static bool IsFixture(Faction f)
        {
            return f != null && f.temporary && f.Name != null && f.Name.StartsWith(PhysicalTestIds.FixtureFactionName, StringComparison.Ordinal);
        }

        /// <summary>Hands a fixture faction to vanilla's temporary-faction removal (removed on a following tick when nothing holds it).</summary>
        public static bool ReleaseFaction(Faction f)
        {
            if (!IsFixture(f) || !Find.FactionManager.AllFactionsListForReading.Contains(f)) return false;
            if (f.leader != null && (f.leader.Destroyed || f.leader.Discarded)) f.leader = null;
            Find.FactionManager.Notify_PawnLeftFaction(f);
            return true;
        }

        public static void Tag(Pawn p, string runId)
        {
            if (p != null) QuestUtility.AddQuestTag(ref p.questTags, PhysicalTestIds.RunTag(runId));
        }

        /// <summary>A fresh adult disposable pawn (never spawned, never bound), tagged with the run.</summary>
        public static Pawn Disposable(PawnKindDef kind, Faction f, float age, string runId)
        {
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, f, PawnGenerationContext.NonPlayer, null, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                colonistRelationChanceFactor: 0f, allowAddictions: false, fixedBiologicalAge: age, fixedChronologicalAge: age, developmentalStages: DevelopmentalStage.Adult));
            Tag(p, runId);
            return p;
        }

        /// <summary>Why the tier may NOT discard this pawn (null = it may).</summary>
        public static string DisposeRefusal(Pawn p, string runId, DomainContext ctx)
        {
            if (p == null) return "no pawn";
            if (p.Discarded) return "already discarded";
            if (p.questTags == null || !p.questTags.Contains(PhysicalTestIds.RunTag(runId))) return "not tagged by this run";
            if (p.Spawned || p.SpawnedOrAnyParentSpawned) return "spawned";
            if (Find.WorldPawns.Contains(p)) return "a world pawn";
            if (RetainedPawnRegistry.Active != null && RetainedPawnRegistry.Active.Reserves(p)) return "reserved by a named person or an active Episode";
            if (GroupQaRules.HasUnreleasedBinding(ctx?.episodes?.episodes, p.thingIDNumber)) return "bound to an unreleased Episode member";
            if (RetainedPawnRegistry.Active != null && RetainedPawnRegistry.Active.CharacterOf(p).IsValid) return "bound to a Network person (registry)";
            if (ctx?.characters != null) foreach (KnownCharacter c in ctx.characters.characters) if (c.pawn != null && ReferenceEquals(c.pawn.pawn, p)) return "bound to Network person " + c.id;
            return null;
        }

        public static bool TryDispose(Pawn p, string runId, DomainContext ctx, out string refusal)
        {
            refusal = DisposeRefusal(p, runId, ctx);
            if (refusal != null) return false;
            p.Discard(true);
            return true;
        }
    }
}
