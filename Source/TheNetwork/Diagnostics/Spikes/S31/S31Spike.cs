using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace TheNetwork.Diagnostics.Spikes.S31
{
    /// <summary>A pawn the spike created in this game (runtime tracking only; rebuilt from the S31 markers after a load).</summary>
    public sealed class S31Tracked
    {
        public string role;
        public Pawn subject;
        public string scenario;
        public string lastStep;
        public string lastFailure;
    }

    /// <summary>
    /// Spike S31's driver: the actions behind the dev menu, the per-frame pump and the scenario runs. RUNTIME ONLY (nothing here is
    /// saved; the S31 state that survives a save is vanilla data carrying S31 markers). Every destructive action spends the session arm;
    /// every read-only action does not need it. Nothing here is reachable from Quick smoke or Full safe regression, and nothing here
    /// adopts a mechanism: it measures M1 and prints PASS / FAIL / INCONCLUSIVE per scenario for the owner to review.
    /// </summary>
    public static class S31Spike
    {
        private static object trackedGame;
        private static readonly List<S31Tracked> tracked = new List<S31Tracked>();
        private static S31SignalObserver observer;
        private static S31Run active;

        public static S31Verdict LastVerdict { get; private set; }

        public static S31SignalObserver Observer => observer;

        public const string ArmWarning =
            "S31 is a PHYSICAL SPIKE (dev only). Each destructive S31 action creates or removes REAL RimWorld objects in THIS save: a dedicated "
            + "world object and map (" + S31Ids.TestMapDef + ", never your colony map), temporary hidden factions named \"" + S31Ids.FactionName
            + "...\", pawns named " + S31Ids.ProbeNick + "... and " + S31Ids.DecoyNick + "..., and hidden quests that reserve them. It never touches "
            + "your colonists, your maps or any pawn without an S31 marker. Use a test save. One arm authorises exactly ONE destructive action; "
            + "the arm is never saved and is cleared on load. Run \"S31 — Cleanup\" before removing this spike build.";

        // ================================================================== pump and game identity

        /// <summary>Called once per rendered frame by the Network's world component. One static null check while no run is active.</summary>
        public static void PumpFrame()
        {
            S31Run r = active;
            if (r == null) return;
            try
            {
                if (!ReferenceEquals(Current.Game, trackedGame))
                {
                    active = null;
                    Verse.Log.Warning(S31Ids.LogPrefix + "a run was abandoned: a different game is running now");
                    return;
                }
                if (!r.Pump()) active = null;
            }
            catch (Exception ex)
            {
                active = null;
                Verse.Log.Error(S31Ids.LogPrefix + "the run stopped on an exception (state preserved for inspection; nothing was cleaned up): " + ex);
            }
        }

        private static void EnsureGame()
        {
            S31Session.Instance.Observe(Current.Game, S31World.Tick);
            if (ReferenceEquals(Current.Game, trackedGame)) return;
            trackedGame = Current.Game;
            tracked.Clear();
            active = null;
            observer = null; // the old game's SignalManager went with it
            Rebuild();
        }

        private static void EnsureObserver()
        {
            if (observer != null) return;
            observer = new S31SignalObserver();
            Find.SignalManager.RegisterReceiver(observer);
            S31World.Log("signal observer registered for this game (runtime only, observation only)");
        }

        /// <summary>After a load, the tracking is rebuilt from the S31 markers (vanilla data), never from a guess.</summary>
        private static void Rebuild()
        {
            if (Current.Game == null) return;
            foreach (Pawn p in S31World.MarkedPawns())
            {
                if (tracked.Any(t => t.subject == p)) continue;
                string role = p.questTags.Contains(S31Ids.ProbeTag) ? "probe" : p.questTags.Contains(S31Ids.DecoyTag) ? "decoy" : "faction leader";
                tracked.Add(new S31Tracked { role = role, subject = p, scenario = "(found by marker)", lastStep = "found by marker at tick " + S31World.Tick });
            }
        }

        internal static void Track(Pawn p, string role, string scenario)
        {
            tracked.Add(new S31Tracked { role = role, subject = p, scenario = scenario, lastStep = "created at tick " + S31World.Tick });
        }

        internal static void Step(Pawn p, string step, string failure)
        {
            foreach (S31Tracked t in tracked)
            {
                if (t.subject != p) continue;
                t.lastStep = step;
                if (failure != null) t.lastFailure = failure;
            }
        }

        internal static void Finished(S31Verdict v)
        {
            LastVerdict = v;
            Verse.Log.Message(v.Block());
            Messages.Message("[TheNetwork][S31] " + v.scenario + ": " + v.Outcome.ToString().ToUpperInvariant() + " (details in the log)",
                v.Outcome == S31Outcome.Pass ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent, false);
        }

        private static void Refuse(string why)
        {
            S31World.Warn("refused: " + why);
            Messages.Message("[TheNetwork][S31] " + why, MessageTypeDefOf.RejectInput, false);
        }

        // ================================================================== arm

        public static bool TryArm(string typed, out string reason)
        {
            EnsureGame();
            bool ok = S31Session.Instance.TryArm(Current.Game, S31World.Tick, typed, out reason);
            S31World.Log(ok ? "ARMED for one destructive action (runtime only; cleared on load)" : "arm refused: " + reason);
            return ok;
        }

        private static bool Spend(string action)
        {
            string reason;
            if (S31Session.Instance.Spend(Current.Game, S31World.Tick, action, out reason))
            {
                S31World.Log("arm spent by \"" + action + "\" (re-arm for the next destructive action)");
                return true;
            }
            Refuse(reason);
            return false;
        }

        // ================================================================== preconditions

        private static List<Pawn> SpawnedMarkedOn(Map map)
        {
            List<Pawn> r = new List<Pawn>();
            if (map == null) return r;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned) if (S31Ids.CarriesMarker(p.questTags)) r.Add(p);
            return r;
        }

        private static List<Pawn> StoredProbes()
        {
            List<Pawn> r = new List<Pawn>();
            foreach (Pawn p in S31World.MarkedPawns())
            {
                if (p.questTags.Contains(S31Ids.ProbeTag) && !p.Dead && !p.Destroyed && Find.WorldPawns.Contains(p) && S31World.Reserved(p)) r.Add(p);
            }
            return r;
        }

        private static bool ReadyForRun(string action, out Map map)
        {
            map = null;
            if (!Prefs.DevMode)
            {
                Refuse("Dev Mode is off");
                return false;
            }
            if (active != null)
            {
                Refuse("a scenario is already running (" + active.Title + "); wait for its result");
                return false;
            }
            map = S31World.TestMap;
            if (map == null)
            {
                Refuse("there is no S31 test map with a map (run \"S31 — Create/ensure dedicated test map\" first; scenario B removes the map)");
                return false;
            }
            List<Pawn> leftover = SpawnedMarkedOn(map);
            if (leftover.Count > 0)
            {
                Refuse(leftover.Count + " S31 pawn(s) are still spawned on the test map (a preserved earlier run?). Inspect them, then run \"S31 — Cleanup\"");
                return false;
            }
            return Spend(action);
        }

        // ================================================================== actions

        public static void EnsureMapAction()
        {
            EnsureGame();
            if (!Prefs.DevMode)
            {
                Refuse("Dev Mode is off");
                return;
            }
            if (active != null)
            {
                Refuse("a scenario is running");
                return;
            }
            if (S31World.TestMap != null)
            {
                S31World.Log("the S31 test map already exists (map " + S31World.TestMap.uniqueID + "); nothing to do, the arm was not spent");
                Messages.Message("[TheNetwork][S31] The test map already exists.", MessageTypeDefOf.NeutralEvent, false);
                return;
            }
            if (!Spend("Create/ensure dedicated test map")) return;
            string report;
            Map map = S31World.EnsureTestMap(out report);
            S31World.Log(report);
            if (map == null) Refuse(report);
            else Messages.Message("[TheNetwork][S31] " + report, MessageTypeDefOf.NeutralEvent, false);
        }

        public static void StartRun(S31Run.Kind kind)
        {
            EnsureGame();
            Map map;
            if (kind == S31Run.Kind.Rematerialize)
            {
                if (StoredProbes().Count == 0)
                {
                    Refuse("no stored, reserved S31 probe exists (run scenario A first)");
                    return;
                }
            }
            if (!ReadyForRun(S31Run.TitleOf(kind), out map)) return;
            EnsureObserver();
            S31Run run = new S31Run(kind, map, observer);
            string error;
            if (!run.Start(StoredProbes(), out error))
            {
                Refuse("the run could not start: " + error + " (anything created so far is preserved and marked; see the status report)");
                return;
            }
            active = run;
        }

        /// <summary>Scenario D, step 1: write a checkpoint of every stored probe into the S31 quest's tags (vanilla data), then the owner saves and reloads.</summary>
        public static void PrepareCheckpoint()
        {
            EnsureGame();
            if (active != null)
            {
                Refuse("a scenario is running");
                return;
            }
            List<Pawn> stored = StoredProbes();
            if (stored.Count == 0)
            {
                Refuse("no stored, reserved S31 probe exists (run scenario A first)");
                return;
            }
            if (!Spend("Prepare save/load checkpoint")) return;
            Quest q = S31World.FindManifest();
            q.tags.RemoveAll(t => t != null && t.StartsWith(S31Ids.CheckpointPrefix, StringComparison.Ordinal));
            foreach (Pawn p in stored)
            {
                S31Snap s = S31World.Snap(p, "checkpoint");
                q.tags.Add(S31Checkpoint.From(s).Encode());
                S31World.Log("checkpoint: " + s.Line());
                Step(p, "checkpointed for save/load at tick " + s.tick, null);
            }
            S31World.Log("CHECKPOINT READY for " + stored.Count + " stored probe(s). Now: (1) save the game, (2) quit to the main menu, (3) load that save, "
                + "(4) run \"S31 — Show current spike state\" (it must say \"not armed\"), (5) run \"S31 — Verify after load\".");
            Messages.Message("[TheNetwork][S31] Checkpoint written. Save, return to the menu, reload, then run \"S31 — Verify after load\".", MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>Scenario D, step 2 (read-only): compare every checkpoint with the reloaded game.</summary>
        public static void VerifyAfterLoad()
        {
            EnsureGame();
            S31Verdict v = new S31Verdict("Scenario D (save/load window)");
            S31Session session = S31Session.Instance;
            v.Note("S31 first saw this game at tick " + session.FirstSeenTick + " with the arm " + (session.ArmedWhenGameFirstSeen ? "SET" : "clear") + "; now " + (session.IsArmed(Current.Game) ? "armed" : "not armed"));
            if (session.ArmedWhenGameFirstSeen) v.Fail("the session arm was set when this game was first seen (it must be cleared by a load)");
            Quest q = S31World.FindManifest();
            if (q == null)
            {
                v.Fail("the S31 reservation quest did not survive the load (or was never created)");
                Finished(v);
                return;
            }
            int manifests = Find.QuestManager.QuestsListForReading.Count(x => x?.tags != null && x.tags.Contains(S31Ids.ManifestTag));
            if (manifests != 1) v.Fail(manifests + " S31 reservation quests exist (expected exactly one)");
            if (q.State != QuestState.Ongoing) v.Fail("the S31 reservation quest is " + q.State + " after load (it must stay Ongoing to reserve)");
            if (!q.hidden) v.Fail("the S31 reservation quest is not hidden after load");
            QuestPart_ReservePawns part = S31World.ReservePart(q);
            if (part == null) v.Fail("the reservation part did not survive the load");
            else v.Note("the reservation quest survived as built: id " + q.id + ", " + q.State + ", hidden, root " + q.root?.defName + ", reserving " + part.pawns.Count + " pawn(s)");
            List<S31Checkpoint> cps = new List<S31Checkpoint>();
            foreach (string t in q.tags)
            {
                S31Checkpoint cp;
                if (S31Checkpoint.TryDecode(t, out cp)) cps.Add(cp);
            }
            if (cps.Count == 0) v.Gap("no checkpoint found: run \"S31 — Prepare save/load checkpoint\" before saving");
            foreach (S31Checkpoint cp in cps)
            {
                Pawn p = null;
                foreach (Pawn x in PawnsFinder.All_AliveOrDead) if (x != null && x.thingIDNumber == cp.thingId) { p = x; break; }
                S31Snap now = p != null ? S31World.Snap(p, "after load") : null;
                if (now != null) S31World.Log("after load: " + now.Line());
                S31Criteria.AfterLoad(v, cp, now, S31World.CountById(cp.thingId), S31World.CountByNick(cp.nick));
                if (p != null && part != null && !part.pawns.Contains(p)) v.Fail("#" + cp.thingId + " is not in the reservation part after load");
                if (p != null) Step(p, "verified after load at tick " + S31World.Tick, v.failures.Count > 0 ? v.failures[v.failures.Count - 1] : null);
            }
            Finished(v);
        }

        public static void ShowState()
        {
            EnsureGame();
            Rebuild();
            Verse.Log.Message(StateText());
        }

        public static string StateText()
        {
            StringBuilder b = new StringBuilder();
            string P = S31Ids.LogPrefix;
            b.AppendLine(P + "===== S31 spike state at tick " + S31World.Tick + " (S31 status: NOT RUN until the owner's checklist is complete) =====");
            b.AppendLine(P + S31Session.Instance.StatusLine(Current.Game));
            MapParent parent = S31World.FindTestMapParent();
            WorldObjectDef def = S31World.TestMapDef;
            string why;
            b.AppendLine(P + "test map def: " + (def == null ? "MISSING" : def.defName + (S31World.TestMapDefIsNarrow(def, out why) ? " (MapParent, no comps, not a home, no incident tags)" : " NOT NARROW: " + why)));
            if (parent == null) b.AppendLine(P + "test map: none");
            else
            {
                Map m = parent.HasMap ? parent.Map : null;
                int s31 = m == null ? 0 : SpawnedMarkedOn(m).Count, all = m == null ? 0 : m.mapPawns.AllPawnsSpawnedCount;
                b.AppendLine(P + "test map: world object " + parent.ID + ", tile " + parent.Tile + ", " + (m == null ? "NO map (removed)" : "map " + m.uniqueID + ", " + s31 + " S31 pawn(s) spawned of " + all + " pawns (the rest are map-generated wildlife)"));
            }
            Quest q = S31World.FindManifest();
            QuestPart_ReservePawns part = S31World.ReservePart(q);
            b.AppendLine(P + "reservation quest: " + (q == null ? "none" : "id " + q.id + ", " + q.State + (q.hidden ? ", hidden" : ", NOT hidden") + ", root " + q.root?.defName + ", reserving " + (part?.pawns.Count ?? 0) + " pawn(s)"));
            Quest pool = S31World.FindPoolQuest();
            if (pool != null) b.AppendLine(P + "pool-faction reservation quest (scenario F only): id " + pool.id + ", " + pool.State);
            foreach (string t in S31World.ManifestTags(S31Ids.FactionPrefix))
            {
                int id;
                if (!S31Ids.TryParseIntTag(t, S31Ids.FactionPrefix, out id)) continue;
                Faction f = S31World.FindFaction(id);
                b.AppendLine(P + "S31 faction " + id + ": " + (f == null ? "removed by vanilla (temporary faction)" : "\"" + f.Name + "\"" + (f.temporary ? ", temporary" : ", NOT temporary") + ", leader " + (f.leader != null ? "#" + f.leader.thingIDNumber : "none")));
            }
            int cps = q?.tags?.Count(t => t != null && t.StartsWith(S31Ids.CheckpointPrefix, StringComparison.Ordinal)) ?? 0;
            b.AppendLine(P + "save/load checkpoints: " + cps);
            b.AppendLine(P + "tracked S31 pawns: " + tracked.Count + " (idx | role | thing | spawned | map | holder | inWorldPawns | situation | reserved | suspended | faction | quest | last step | last failure)");
            for (int i = 0; i < tracked.Count; i++)
            {
                S31Tracked t = tracked[i];
                S31Snap s = S31World.Snap(t.subject, "status");
                b.AppendLine(P + "  " + (i + 1) + " | " + t.role + " | #" + s.thingId + " " + s.nick + " | " + s.spawned + " | " + s.mapId + " | " + (s.holder ?? "none") + " | " + s.inWorld + " | "
                    + s.situation + " | " + s.reserved + " | " + s.suspended + " | " + (s.factionId < 0 ? "none" : s.factionId.ToString()) + " | " + (part != null && part.pawns.Contains(t.subject) ? "quest " + q.id : "-")
                    + " | " + t.lastStep + " | " + (t.lastFailure ?? "-"));
            }
            b.AppendLine(P + "active run: " + (active == null ? "none" : active.Status));
            b.AppendLine(P + "signals observed this game: " + (observer?.events.Count ?? 0));
            b.AppendLine(P + "last verdict: " + (LastVerdict == null ? "none" : LastVerdict.scenario + " " + LastVerdict.Outcome));
            b.Append(P + "===== end of S31 state =====");
            return b.ToString();
        }

        /// <summary>
        /// Removes only S31-marked state: S31 pawns (on the S31 test map or in WorldPawns), the S31 quests, the S31 test map, and asks vanilla
        /// to drop S31 temporary factions. A pawn that vanilla holds in any other way is preserved and reported, never forced. Every pawn
        /// removal goes through S31World.TryDispose, which re-checks ownership and preserves anything it cannot prove is S31's.
        /// </summary>
        public static void Cleanup()
        {
            EnsureGame();
            if (!Spend("Cleanup")) return;
            List<string> removed = new List<string>(), kept = new List<string>();
            if (active != null)
            {
                kept.Add("the active run (" + active.Title + ") was abandoned by the cleanup");
                active = null;
            }
            Map testMap = S31World.TestMap;
            List<string> factionTags = S31World.ManifestTags(S31Ids.FactionPrefix);
            foreach (Pawn p in S31World.MarkedPawns())
            {
                string who = "#" + p.thingIDNumber + " " + S31World.Nick(p);
                if (p.Spawned)
                {
                    if (p.Map == testMap)
                    {
                        string refused;
                        if (S31World.TryDispose(p, out refused)) removed.Add(who + " (spawned on the S31 test map)");
                        else kept.Add(who + ": disposal refused (" + refused + "); preserved");
                    }
                    else kept.Add(who + ": spawned on a map that is not the S31 test map (" + p.Map + "); preserved");
                    continue;
                }
                if (p.Discarded)
                {
                    removed.Add(who + " (already discarded)");
                    continue;
                }
                if (Find.WorldPawns.Contains(p))
                {
                    WorldPawnSituation s = Find.WorldPawns.GetSituation(p);
                    if (s == WorldPawnSituation.ReservedByQuest || s == WorldPawnSituation.Free || s == WorldPawnSituation.FactionLeader || s == WorldPawnSituation.Dead)
                    {
                        string refused;
                        if (S31World.TryDispose(p, out refused)) removed.Add(who + " (world pawn, " + s + ")");
                        else kept.Add(who + ": world pawn (" + s + "), disposal refused (" + refused + "); preserved");
                    }
                    else kept.Add(who + ": world pawn held by vanilla as " + s + "; preserved");
                    continue;
                }
                kept.Add(who + ": held by " + (p.ParentHolder?.ToString() ?? "nothing known") + "; preserved");
            }
            foreach (Quest q in new[] { S31World.FindManifest(), S31World.FindPoolQuest() })
            {
                if (q == null) continue;
                if (!q.Historical) q.End(QuestEndOutcome.Unknown, false, false);
                if (Find.QuestManager.QuestsListForReading.Contains(q)) Find.QuestManager.Remove(q);
                removed.Add("quest " + q.id + " (" + q.name + ")");
            }
            foreach (string t in factionTags)
            {
                int id;
                if (!S31Ids.TryParseIntTag(t, S31Ids.FactionPrefix, out id)) continue;
                Faction f = S31World.FindFaction(id);
                if (f == null)
                {
                    removed.Add("faction " + id + " (already removed by vanilla)");
                    continue;
                }
                if (!f.temporary || f.Name == null || !f.Name.StartsWith(S31Ids.FactionName, StringComparison.Ordinal))
                {
                    kept.Add("faction " + id + " is not an S31 temporary faction; untouched");
                    continue;
                }
                if (f.leader != null && (f.leader.Destroyed || f.leader.Discarded)) f.leader = null;
                Find.FactionManager.Notify_PawnLeftFaction(f);
                removed.Add("faction " + id + " \"" + f.Name + "\" handed to vanilla's temporary-faction removal (removed on a following tick if nothing holds it)");
            }
            MapParent parent = S31World.FindTestMapParent();
            if (parent != null)
            {
                Map m = parent.HasMap ? parent.Map : null;
                List<Pawn> others = m == null ? new List<Pawn>() : m.mapPawns.AllPawnsSpawned.Where(p => p.Faction == Faction.OfPlayer || p.HostFaction == Faction.OfPlayer).ToList();
                if (others.Count > 0) kept.Add("the S31 test map: " + others.Count + " player or player-hosted pawn(s) are on it; preserved");
                else
                {
                    int id = parent.ID;
                    parent.Destroy();
                    removed.Add("the S31 test map world object " + id + (m != null ? " and its map" : ""));
                }
            }
            if (observer != null)
            {
                Find.SignalManager.DeregisterReceiver(observer);
                observer = null;
            }
            tracked.Clear();
            S31Session.Instance.Disarm("cleanup finished");
            StringBuilder b = new StringBuilder();
            b.AppendLine(S31Ids.LogPrefix + "===== CLEANUP =====");
            foreach (string r in removed) b.AppendLine(S31Ids.LogPrefix + "  removed: " + r);
            foreach (string k in kept) b.AppendLine(S31Ids.LogPrefix + "  LEFTOVER: " + k);
            b.Append(S31Ids.LogPrefix + "===== end of cleanup (" + removed.Count + " removed, " + kept.Count + " leftover) =====");
            Verse.Log.Message(b.ToString());
            Messages.Message("[TheNetwork][S31] Cleanup: " + removed.Count + " removed, " + kept.Count + " leftover (details in the log).", MessageTypeDefOf.NeutralEvent, false);
        }
    }

    /// <summary>
    /// One scenario run. Phases: HOLD (spawned and reserved, observed under a vanilla DefendPoint Lord), then EXIT (a vanilla
    /// ExitMapBest Lord walks the pawns off the edge; vanilla passes them) or MAP REMOVAL (vanilla DeinitAndRemoveMap), then STORED
    /// (observed in WorldPawns), then the verdict. A failure stops nothing by force and cleans nothing: the state stays for inspection.
    /// </summary>
    public sealed class S31Run
    {
        public enum Kind
        {
            NormalExit,
            MapRemoval,
            InjuredExit,
            MultiExit,
            Redress,
            Rematerialize
        }

        private enum Phase
        {
            Hold,
            Exiting,
            Stored,
            Done
        }

        public const int HoldTicks = 900;
        public const int ExitTimeoutTicks = 20000;
        public const int StoredTicks = 900;
        public const int SampleEvery = 30;
        public const int MultiCount = 4;
        public const int DecoyCount = 6;

        private readonly Kind kind;
        private Map map;
        private Faction faction;
        private readonly S31SignalObserver observer;
        private readonly List<Pawn> probes = new List<Pawn>();
        private readonly List<Pawn> decoys = new List<Pawn>();
        private readonly Dictionary<int, List<S31Snap>> spawnedSamples = new Dictionary<int, List<S31Snap>>();
        private readonly Dictionary<int, List<S31Snap>> storedSamples = new Dictionary<int, List<S31Snap>>();
        private readonly Dictionary<int, S31Snap> lastSpawned = new Dictionary<int, S31Snap>();
        private readonly Dictionary<int, S31Snap> afterPass = new Dictionary<int, S31Snap>();
        private readonly Dictionary<int, S31Snap> firstFrame = new Dictionary<int, S31Snap>();
        private readonly Dictionary<int, int> passTick = new Dictionary<int, int>();
        private readonly Dictionary<int, int> factionAtPass = new Dictionary<int, int>();
        private readonly HashSet<int> diedOnMap = new HashSet<int>();
        private readonly List<string> order = new List<string>();
        private readonly S31Verdict verdict;
        private Phase phase;
        private int phaseStart;
        private int startTick;
        private int lastSample = -100000;
        private int lastPumpTick = -1;
        private S31World.LogMark mark;
        private int factionGoneTick = -1;

        // scenario F
        private bool pressureDone;
        private int requests, decoysRedressed, probeSelected, probeInFree, newlyGenerated, noPawnReturns;
        private readonly List<string> unexpectedReturns = new List<string>();

        // scenario G
        private S31Snap gStored, gSpawned;
        private int gIdCount, gNickCount;
        private bool gSameRef;

        public S31Run(Kind kind, Map map, S31SignalObserver observer)
        {
            this.kind = kind;
            this.map = map;
            this.observer = observer;
            verdict = new S31Verdict(TitleOf(kind));
        }

        public string Title => TitleOf(kind);

        public static string TitleOf(Kind k)
        {
            switch (k)
            {
                case Kind.NormalExit: return "Scenario A (M1, normal vanilla exit)";
                case Kind.MapRemoval: return "Scenario B (M1, map removal)";
                case Kind.InjuredExit: return "Scenario C (M1, injured exit)";
                case Kind.MultiExit: return "Scenario E (M1, " + MultiCount + " pawns leave together)";
                case Kind.Redress: return "Scenario F (M1, populated pool and redress pressure)";
                default: return "Scenario G (M1, same-pawn rematerialization and second exit)";
            }
        }

        private string Letter => Title.Substring("Scenario ".Length, 1);

        public string Status => Title + ", phase " + phase + " since tick " + phaseStart + " (now " + S31World.Tick + "), probes " + probes.Count + ", decoys " + decoys.Count;

        private List<Pawn> All
        {
            get
            {
                List<Pawn> r = new List<Pawn>(probes);
                r.AddRange(decoys);
                return r;
            }
        }

        private static string Who(Pawn p)
        {
            return "#" + p.thingIDNumber + " " + S31World.Nick(p);
        }

        private int NextNumber()
        {
            int max = 0;
            foreach (Pawn p in S31World.MarkedPawns())
            {
                string nick = S31World.Nick(p);
                if (nick == null) continue;
                string[] parts = nick.Split('-');
                int n;
                if (parts.Length > 0 && int.TryParse(parts[parts.Length - 1], out n)) max = Math.Max(max, n);
            }
            return max + 1;
        }

        public bool Start(List<Pawn> storedProbes, out string error)
        {
            error = null;
            startTick = S31World.Tick;
            mark = S31World.MarkLog();
            string report;
            faction = S31World.NewTempFaction(Letter + "-" + startTick, out report);
            if (faction == null)
            {
                error = report;
                return false;
            }
            order.Add("t=" + startTick + " temporary faction " + faction.loadID + " created");
            if (kind == Kind.Rematerialize) return StartRematerialize(storedProbes, out error);
            int count = kind == Kind.MultiExit ? MultiCount : 1;
            int number = NextNumber();
            for (int i = 0; i < count; i++)
            {
                Pawn p = S31World.NewPawn(map, faction, S31Ids.ProbeNick + Letter + "-" + (number + i), S31Ids.ProbeTag, out error);
                if (p == null) return false;
                probes.Add(p);
                S31Spike.Track(p, "probe", Title);
                if (kind == Kind.InjuredExit) S31World.Log(Who(p) + ": injured for scenario C: " + S31World.Injure(p));
                // M1: the reservation exists while the pawn is still SPAWNED, before vanilla can pass it.
                if (!S31World.Reserve(p, out error)) return false;
                S31World.Log(Who(p) + " spawned and reserved (M1): " + S31World.Snap(p, "spawned and reserved").Line());
            }
            if (kind == Kind.Redress)
            {
                if (!ReservePoolFaction(out error)) return false;
                for (int i = 0; i < DecoyCount; i++)
                {
                    Pawn d = S31World.NewPawn(map, faction, S31Ids.DecoyNick + Letter + "-" + (number + count + i), S31Ids.DecoyTag, out error);
                    if (d == null) return false;
                    decoys.Add(d);
                    S31Spike.Track(d, "decoy (unreserved)", Title);
                }
                S31World.Log(DecoyCount + " unreserved decoys spawned in the same faction (the positive control of scenario F)");
            }
            S31World.HoldLord(map, faction, All);
            phase = Phase.Hold;
            phaseStart = startTick;
            S31World.Log(Title + " started: holding " + HoldTicks + " ticks under a vanilla DefendPoint Lord while reserved (unpause the game to let it run)");
            return true;
        }

        /// <summary>Scenario F only: the pool faction is reserved (vanilla QuestPart_ReserveFaction) so redress pressure stays confined to spike pawns.</summary>
        private bool ReservePoolFaction(out string error)
        {
            error = null;
            QuestScriptDef root = QuestScriptDefOf.Util_GetDefaultRewardValueFromPoints;
            if (root == null)
            {
                error = "no vanilla root for the pool-faction quest";
                return false;
            }
            Quest q = Quest.MakeRaw();
            q.root = root;
            q.hidden = true;
            q.hiddenInUI = true;
            q.name = S31Ids.PoolQuestName;
            QuestUtility.AddQuestTag(ref q.tags, S31Ids.PoolQuestTag);
            q.AddPart(new QuestPart_ReserveFaction { faction = faction });
            q.SetInitiallyAccepted();
            Find.QuestManager.Add(q);
            verdict.Note("scenario F reserves its own faction (vanilla QuestPart_ReserveFaction, quest " + q.id + ") so that the redress pressure, which is faction-matched, can only reach S31 pawns; F's faction data are therefore NOT faction-rewrite evidence");
            return true;
        }

        private bool StartRematerialize(List<Pawn> storedProbes, out string error)
        {
            error = null;
            Pawn p = storedProbes.FirstOrDefault();
            if (p == null)
            {
                error = "no stored probe";
                return false;
            }
            gStored = S31World.Snap(p, "stored, before rematerialization");
            S31World.Log("G: stored probe before: " + gStored.Line());
            // The episode's temporary faction is set while the pawn is still a reserved world pawn, then the SAME object is spawned.
            p.SetFaction(faction);
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 6);
            GenSpawn.Spawn(p, cell, map); // vanilla Pawn.SpawnSetup removes it from WorldPawns
            Pawn onMap = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == gStored.thingId);
            gSameRef = ReferenceEquals(onMap, p);
            gSpawned = S31World.Snap(p, "spawned again (same object)");
            gIdCount = S31World.CountById(gStored.thingId);
            gNickCount = S31World.CountByNick(gStored.nick);
            S31World.Log("G: same pawn spawned again: " + gSpawned.Line() + " | objects with this id: " + gIdCount + ", with this name: " + gNickCount);
            string ignored;
            S31World.Reserve(p, out ignored); // a no-op: M1 kept it reserved throughout
            probes.Add(p);
            S31Spike.Step(p, "rematerialized at tick " + S31World.Tick, null);
            S31World.HoldLord(map, faction, All);
            phase = Phase.Hold;
            phaseStart = S31World.Tick;
            return true;
        }

        /// <summary>One frame. Returns false when the run is finished.</summary>
        public bool Pump()
        {
            int t = S31World.Tick;
            if (t == lastPumpTick) return true;
            lastPumpTick = t;
            if (factionGoneTick < 0 && faction != null && S31World.FindFaction(faction.loadID) == null)
            {
                factionGoneTick = t;
                order.Add("t=" + t + " temporary faction " + faction.loadID + " removed by vanilla");
                S31World.Log("temporary faction " + faction.loadID + " was removed by vanilla at tick " + t);
            }
            switch (phase)
            {
                case Phase.Hold:
                    SampleSpawned(t);
                    if (t - phaseStart < HoldTicks) return true;
                    if (kind == Kind.MapRemoval)
                    {
                        RemoveMap(t);
                        phase = Phase.Stored;
                        phaseStart = t;
                        return true;
                    }
                    S31World.ExitLord(map, faction, All);
                    order.Add("t=" + t + " vanilla LordJob_ExitMapBest assigned");
                    S31World.Log("exit Lord assigned (vanilla LordJob_ExitMapBest); the pawns now walk to the edge");
                    phase = Phase.Exiting;
                    phaseStart = t;
                    return true;
                case Phase.Exiting:
                    SampleSpawned(t);
                    bool all = true;
                    foreach (Pawn p in All)
                    {
                        int id = p.thingIDNumber;
                        if (passTick.ContainsKey(id)) continue;
                        if (p.Spawned)
                        {
                            all = false;
                            continue;
                        }
                        if (p.Dead)
                        {
                            // Killed on the test map (wildlife?) before leaving: no vanilla pass happened, so this is no M1 evidence.
                            diedOnMap.Add(id);
                            passTick[id] = t;
                            order.Add("t=" + t + " " + Who(p) + " DIED on the map before leaving");
                            S31World.Warn(Who(p) + " died on the test map before leaving (no M1 evidence for it; re-run the scenario)");
                            continue;
                        }
                        S31Snap sig = observer?.First("LeftMap", id, startTick);
                        S31Snap frame = S31World.Snap(p, "first frame after the exit");
                        afterPass[id] = sig ?? frame;
                        firstFrame[id] = frame;
                        passTick[id] = t;
                        factionAtPass[id] = lastSpawned.ContainsKey(id) ? lastSpawned[id].factionId : frame.factionId;
                        order.Add("t=" + t + " " + Who(p) + " left the map" + (sig != null ? " (LeftMap observed synchronously at t=" + sig.tick + ")" : " (no LeftMap observed: this scenario will be INCONCLUSIVE)"));
                        S31World.Log(Who(p) + " exited: " + (sig != null ? "at LeftMap: " + sig.Line() + " || " : "") + "first frame: " + frame.Line());
                        S31Spike.Step(p, "exited at tick " + t, null);
                    }
                    if (all)
                    {
                        phase = Phase.Stored;
                        phaseStart = t;
                        return true;
                    }
                    if (t - phaseStart > ExitTimeoutTicks)
                    {
                        verdict.Gap("the pawns did not all exit within " + ExitTimeoutTicks + " ticks (state preserved; inspect the map)");
                        return Finish();
                    }
                    return true;
                case Phase.Stored:
                    if (kind == Kind.Redress && !pressureDone) ApplyPressure();
                    SampleStored(t);
                    if (t - phaseStart < StoredTicks) return true;
                    Evaluate();
                    return Finish();
                default:
                    return false;
            }
        }

        private void SampleSpawned(int t)
        {
            bool due = t - lastSample >= SampleEvery;
            foreach (Pawn p in All)
            {
                if (!p.Spawned) continue;
                S31Snap s = S31World.Snap(p, phase == Phase.Hold ? "spawned, holding" : "spawned, exiting");
                lastSpawned[p.thingIDNumber] = s;
                if (due) Add(spawnedSamples, p.thingIDNumber, s);
            }
            if (due) lastSample = t;
        }

        private void SampleStored(int t)
        {
            if (t - lastSample < SampleEvery) return;
            lastSample = t;
            foreach (Pawn p in All)
            {
                if (p.Spawned || decoys.Contains(p) && p.Discarded) continue;
                Add(storedSamples, p.thingIDNumber, S31World.Snap(p, "stored"));
            }
        }

        private static void Add(Dictionary<int, List<S31Snap>> d, int id, S31Snap s)
        {
            List<S31Snap> l;
            if (!d.TryGetValue(id, out l)) d[id] = l = new List<S31Snap>();
            l.Add(s);
        }

        /// <summary>Scenario B: the real vanilla removal (the call MapParent.CheckRemoveMapNow makes), with the pawns still on the map.</summary>
        private void RemoveMap(int t)
        {
            foreach (Pawn p in All) lastSpawned[p.thingIDNumber] = S31World.Snap(p, "just before the map removal");
            order.Add("t=" + t + " Game.DeinitAndRemoveMap called with the pawns still spawned");
            S31World.Log("removing the test map through Game.DeinitAndRemoveMap (vanilla MapDeiniter passes every pawn to the world)");
            Current.Game.DeinitAndRemoveMap(map, true);
            map = null;
            foreach (Pawn p in All)
            {
                int id = p.thingIDNumber;
                S31Snap s = S31World.Snap(p, "immediately after DeinitAndRemoveMap");
                afterPass[id] = s;
                passTick[id] = t;
                factionAtPass[id] = lastSpawned[id].factionId;
                S31World.Log(Who(p) + " after map removal: " + s.Line());
                S31Spike.Step(p, "passed by map removal at tick " + t, null);
            }
            verdict.Note("the test map was removed; run \"S31 — Create/ensure dedicated test map\" before the next scenario");
        }

        /// <summary>
        /// Scenario F: faction-matched generation requests with a forced redress chance. They can only consider Free world pawns of the
        /// pool faction (S31 pawns only). The decoys are the positive control; the probe must never be returned. Every return is classified
        /// by proof (S31Ownership.ClassifyPressureReturn), never by faction: a known decoy is disposed, a pawn proven new for the request
        /// (its thing id issued between two fence ids taken around the request) is tagged S31 first and then disposed, and anything else is
        /// preserved untouched, FAILS the run and stops the pressure. A real GC pass is NOT forced (it would discard unrelated world
        /// pawns); the probe's GC verdict is read from the same accumulation the pass uses.
        /// </summary>
        private void ApplyPressure()
        {
            pressureDone = true;
            Pawn probe = probes[0];
            int freeDecoys = decoys.Count(d => Find.WorldPawns.Contains(d) && Find.WorldPawns.GetSituation(d) == WorldPawnSituation.Free);
            verdict.Note("before the pressure: " + freeDecoys + " of " + decoys.Count + " decoys are Free world pawns; the probe is " + Find.WorldPawns.GetSituation(probe));
            int planned = decoys.Count + 3;
            PawnGenerationRequest req = new PawnGenerationRequest(probe.kindDef, faction, PawnGenerationContext.NonPlayer, null, forceGenerateNewPawn: false,
                allowDead: false, allowDowned: true, canGeneratePawnRelations: false, allowGay: true, allowPregnant: true, allowAddictions: true,
                minChanceToRedressWorldPawn: 1f, developmentalStages: DevelopmentalStage.Adult);
            for (int i = 0; i < planned; i++)
            {
                // The fences: vanilla thing ids are one monotonic counter, so an id strictly between these two was issued during this request.
                int fenceBefore = Find.UniqueIDsManager.GetNextThingID();
                Pawn r = PawnGenerator.GeneratePawn(req);
                int fenceAfter = Find.UniqueIDsManager.GetNextThingID();
                requests++;
                string why, refused;
                S31PressureReturn kindOfReturn = S31Ownership.ClassifyPressureReturn(r != null, r != null && r == probe, r != null && decoys.Contains(r), r?.questTags,
                    r?.thingIDNumber ?? -1, fenceBefore, fenceAfter, r != null && r.Spawned, out why);
                bool stop = false;
                switch (S31Ownership.ActionFor(kindOfReturn))
                {
                    case S31PressureAction.PreserveProbe:
                        probeSelected++;
                        S31World.Warn("CRITICAL: request " + (i + 1) + " returned the RESERVED probe " + Who(probe) + " (preserved, not disposed)");
                        break;
                    case S31PressureAction.Dispose:
                        decoysRedressed++;
                        S31World.Log("request " + (i + 1) + " redressed decoy " + Who(r) + " (positive control); disposing it");
                        if (!S31World.TryDispose(r, out refused)) verdict.Fail("the disposal of redressed decoy " + Who(r) + " was refused (" + refused + "); preserved");
                        break;
                    case S31PressureAction.MarkThenDispose:
                        newlyGenerated++;
                        S31Ownership.MarkPressurePawn(ref r.questTags);
                        S31World.Log("request " + (i + 1) + " generated new pawn #" + r.thingIDNumber + " (id issued between fences " + fenceBefore + " and " + fenceAfter + "); tagged " + S31Ids.PressureTag + ", disposing it");
                        if (!S31World.TryDispose(r, out refused)) verdict.Fail("the disposal of new pressure pawn #" + r.thingIDNumber + " was refused (" + refused + "); preserved");
                        break;
                    case S31PressureAction.PreserveAndFail:
                        string identity = S31World.Identify(r) + " (" + why + ")";
                        unexpectedReturns.Add("request " + (i + 1) + ": " + identity);
                        S31World.Warn("UNEXPECTED: request " + (i + 1) + " returned a pawn whose ownership is NOT proven: " + identity + ". PRESERVED untouched; the pressure stops here");
                        stop = true;
                        break;
                    default:
                        noPawnReturns++;
                        S31World.Warn("request " + (i + 1) + " returned no pawn");
                        break;
                }
                if (Find.WorldPawns.GetPawnsBySituation(WorldPawnSituation.Free).Contains(probe)) probeInFree++;
                Add(storedSamples, probe.thingIDNumber, S31World.Snap(probe, "after redress request " + (i + 1)));
                if (stop)
                {
                    verdict.Note("the pressure stopped after request " + requests + " of " + planned + " (an unexpected pawn was returned)");
                    break;
                }
            }
            Dictionary<Pawn, string> kept = Find.WorldPawns.gc.AccumulatePawnGCDataImmediate();
            string reason;
            if (!kept.TryGetValue(probe, out reason)) verdict.Fail("the world-pawn GC would DISCARD the probe (it is not in the GC's kept set)");
            else verdict.Note("world-pawn GC verdict for the probe: kept, reason \"" + reason + "\"" + (reason == "ReservedByQuest" ? "" : " (an earlier reason than the reservation applied; the reservation itself was not what protected it here)"));
            verdict.Note("a real GC pass was NOT forced: WorldPawnGC.RunGC would discard unrelated world pawns of this save; the verdict above is computed by the accumulation the pass itself uses");
            verdict.Note("the \"faction doesn't matter\" redress path was NOT driven with real requests (it would redress unrelated Free world pawns of this save); it draws from WorldPawns' Free set, which was checked for the probe after every request");
        }

        private void Evaluate()
        {
            QuestPart_ReservePawns part = S31World.ReservePart(S31World.FindManifest());
            foreach (Pawn p in probes)
            {
                int id = p.thingIDNumber;
                string who = Who(p);
                if (diedOnMap.Contains(id))
                {
                    verdict.Gap(who + ": died on the test map before leaving (wildlife or similar), so it never reached vanilla's pass; re-run");
                    continue;
                }
                List<S31Snap> spawned;
                spawnedSamples.TryGetValue(id, out spawned);
                if (spawned != null && lastSpawned.ContainsKey(id) && spawned.Count > 0 && spawned[spawned.Count - 1] != lastSpawned[id]) spawned.Add(lastSpawned[id]);
                S31Criteria.SpawnedBehaviour(verdict, who, spawned, kind == Kind.InjuredExit);
                S31Snap before;
                lastSpawned.TryGetValue(id, out before);
                S31Snap at;
                afterPass.TryGetValue(id, out at);
                S31Criteria.AfterPass(verdict, who, before, at);
                S31Snap frameAfter;
                firstFrame.TryGetValue(id, out frameAfter);
                S31Criteria.LeftMap(verdict, who, kind != Kind.MapRemoval, observer?.First("LeftMap", id, startTick), frameAfter);
                List<S31Snap> stored;
                storedSamples.TryGetValue(id, out stored);
                int own;
                if (!factionAtPass.TryGetValue(id, out own)) own = -1;
                S31Criteria.Stored(verdict, who, own, stored);
                S31Spike.Step(p, Title + " evaluated at tick " + S31World.Tick, verdict.failures.Count > 0 ? verdict.failures[0] : null);
            }
            if (kind == Kind.MultiExit)
            {
                if (part == null) verdict.Fail("no reservation part to check for cross-talk");
                else
                {
                    foreach (Pawn p in probes)
                    {
                        int n = part.pawns.Count(x => x == p);
                        if (n != 1) verdict.Fail(Who(p) + " appears " + n + " times in the reservation part");
                    }
                    int foreign = part.pawns.Count(x => x == null || !S31Ids.CarriesMarker(x.questTags));
                    if (foreign > 0) verdict.Fail(foreign + " pawn(s) without an S31 marker are in the S31 reservation");
                    if (probes.Select(x => x.thingIDNumber).Distinct().Count() != probes.Count) verdict.Fail("two probes share a thing id");
                    verdict.Note("cross-talk check: " + probes.Count + " probes, each reserved exactly once, independent before and after the exit");
                }
            }
            if (kind == Kind.Redress) S31Criteria.Redress(verdict, requests, decoys.Count, decoysRedressed, probeSelected, probeInFree, newlyGenerated, unexpectedReturns, noPawnReturns);
            if (kind == Kind.Rematerialize) S31Criteria.Rematerialized(verdict, gStored, gSpawned, gIdCount, gNickCount, gSameRef);
            S31Criteria.NoAlreadyHere(verdict, S31World.LinesSince(mark, "already here"));
            if (kind != Kind.Redress)
            {
                if (factionGoneTick >= 0) verdict.Note("faction evidence: the temporary faction " + faction.loadID + " was removed by vanilla at tick " + factionGoneTick + "; the stored pawn's faction became null (no rewrite to another faction is a FAIL above)");
                else verdict.Note("faction evidence: the temporary faction " + faction.loadID + " still exists at the end of the run"
                    + (kind == Kind.MapRemoval ? " (expected: a map removal sends no Notify_PawnLeftMap, so vanilla never queued it for removal)" : ""));
            }
            verdict.Note("observed order: " + string.Join(" | ", order.ToArray()));
            verdict.Note("the harness never called PassToWorld: every pass above was vanilla's (" + (kind == Kind.MapRemoval ? "MapDeiniter" : "Pawn.ExitMap") + ")");
        }

        private bool Finish()
        {
            phase = Phase.Done;
            S31Spike.Finished(verdict);
            return false;
        }
    }
}
