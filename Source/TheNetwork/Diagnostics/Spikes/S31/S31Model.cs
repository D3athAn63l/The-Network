using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TheNetwork.Diagnostics.Spikes.S31
{
    /// <summary>
    /// Spike S31 (PHYSICAL_LIFECYCLE § 7.6): the retained-pawn exit window. THIS IS A RUNTIME SPIKE, NOT PHASE 3.1. It builds and
    /// instruments candidate M1 ("reserve while still spawned") and adopts nothing. Every marker below is plain vanilla data (a quest
    /// tag, a pawn quest tag, a name prefix), so a save holds no S31 type, and the status report and the cleanup find S31 state by
    /// marker, never by guess.
    /// </summary>
    public static class S31Ids
    {
        public const string ArmPhrase = "ARM S31";
        public const string LogPrefix = "[TheNetwork][S31] ";

        /// <summary>Every S31 marker starts with this; nothing outside the spike uses it.</summary>
        public const string MarkerRoot = "TheNetwork_S31_";

        /// <summary>Pawn quest tags (vanilla <c>Thing.questTags</c>): vanilla sends "tag.LeftMap" and other target signals for them.</summary>
        public const string ProbeTag = MarkerRoot + "Probe";
        public const string DecoyTag = MarkerRoot + "Decoy";
        public const string LeaderTag = MarkerRoot + "FactionLeader";

        /// <summary>Quest tags (vanilla <c>Quest.tags</c>): the reservation quest doubles as the spike's saved manifest.</summary>
        public const string ManifestTag = MarkerRoot + "Manifest";
        public const string PoolQuestTag = MarkerRoot + "PoolFactionReservation";
        public const string MapPrefix = MarkerRoot + "Map:";
        public const string FactionPrefix = MarkerRoot + "Faction:";
        public const string CheckpointPrefix = MarkerRoot + "Checkpoint:";

        public const string ProbeNick = "S31-Probe-";
        public const string DecoyNick = "S31-Decoy-";
        public const string FactionName = "S31 spike faction ";

        /// <summary>The spike-only WorldObjectDef (1.6/Defs/Spikes/TheNetwork_S31_SpikeDefs.xml): plain MapParent, no comps.</summary>
        public const string TestMapDef = "TheNetwork_S31_TestMap";

        public const string QuestName = "The Network S31 spike reservation (dev only)";
        public const string PoolQuestName = "The Network S31 pool-faction reservation (dev only)";

        public static bool IsMarker(string tag)
        {
            return tag != null && tag.StartsWith(MarkerRoot, StringComparison.Ordinal);
        }

        public static bool CarriesMarker(IList<string> tags)
        {
            if (tags == null) return false;
            for (int i = 0; i < tags.Count; i++) if (IsMarker(tags[i])) return true;
            return false;
        }

        /// <summary>A vanilla quest-target signal sent for an S31 pawn ("TheNetwork_S31_Probe.LeftMap").</summary>
        public static bool IsSignal(string signalTag)
        {
            return signalTag != null && (signalTag.StartsWith(ProbeTag + ".", StringComparison.Ordinal) || signalTag.StartsWith(DecoyTag + ".", StringComparison.Ordinal)
                || signalTag.StartsWith(LeaderTag + ".", StringComparison.Ordinal));
        }

        public static string SignalPart(string signalTag)
        {
            if (signalTag == null) return null;
            int dot = signalTag.IndexOf('.');
            return dot < 0 ? signalTag : signalTag.Substring(dot + 1);
        }

        public static string IntTag(string prefix, int value)
        {
            return prefix + value.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryParseIntTag(string tag, string prefix, out int value)
        {
            value = 0;
            if (tag == null || !tag.StartsWith(prefix, StringComparison.Ordinal)) return false;
            return int.TryParse(tag.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>
    /// The S31 session: RUNTIME ONLY. It is not saved by anything (no Scribe, no IExposable). The arm is bound to one game object, and a
    /// load always creates a new game object, so the arm is false after every load without any load hook. One arm authorises exactly one
    /// destructive action: it is spent when that action starts (PHYSICAL_LIFECYCLE § 21.2: "cleared on every load, on quit, and after each
    /// run"). Nothing here ever infers that a save is disposable.
    /// </summary>
    public sealed class S31Session
    {
        public static readonly S31Session Instance = new S31Session();

        private object game;
        private bool armed;

        public int FirstSeenTick { get; private set; } = -1;

        /// <summary>Evidence for "the arm is false right after a load": the arm state at the moment this game was first observed.</summary>
        public bool ArmedWhenGameFirstSeen { get; private set; }

        public int GamesSeen { get; private set; }
        public int ArmsGranted { get; private set; }
        public int ArmsSpent { get; private set; }
        public int ArmsRefused { get; private set; }
        public string LastArmEvent { get; private set; } = "never armed";

        public bool IsArmed(object currentGame)
        {
            return armed && currentGame != null && ReferenceEquals(currentGame, game);
        }

        /// <summary>
        /// Called by the pump and by every action. A different game object means a load, a new game or a return to the menu: the arm and
        /// all runtime tracking reset. Returns true when the game changed.
        /// </summary>
        public bool Observe(object currentGame, int tick)
        {
            if (ReferenceEquals(currentGame, game)) return false;
            armed = false;
            LastArmEvent = "cleared: a different game is running (load, new game or menu)";
            game = currentGame;
            GamesSeen++;
            FirstSeenTick = tick;
            ArmedWhenGameFirstSeen = armed;
            return true;
        }

        public bool TryArm(object currentGame, int tick, string typed, out string reason)
        {
            Observe(currentGame, tick);
            if (currentGame == null)
            {
                ArmsRefused++;
                reason = "no game is running";
                return false;
            }
            if (typed == null || !string.Equals(typed.Trim(), S31Ids.ArmPhrase, StringComparison.Ordinal))
            {
                ArmsRefused++;
                reason = "the typed phrase must be exactly \"" + S31Ids.ArmPhrase + "\"";
                return false;
            }
            armed = true;
            ArmsGranted++;
            LastArmEvent = "armed at tick " + tick;
            reason = null;
            return true;
        }

        /// <summary>One arm, one destructive action: true, and the arm is spent, only when armed for THIS game.</summary>
        public bool Spend(object currentGame, int tick, string action, out string reason)
        {
            Observe(currentGame, tick);
            if (!IsArmed(currentGame))
            {
                reason = "S31 is not armed for this game. Use \"S31 — Arm physical spike...\" and type " + S31Ids.ArmPhrase + " (one arm = one destructive action; the arm is never saved and is cleared on load).";
                return false;
            }
            armed = false;
            ArmsSpent++;
            LastArmEvent = "spent by \"" + action + "\" at tick " + tick;
            reason = null;
            return true;
        }

        public void Disarm(string why)
        {
            armed = false;
            LastArmEvent = "disarmed: " + why;
        }

        public string StatusLine(object currentGame)
        {
            return "arm: " + (IsArmed(currentGame) ? "ARMED (one destructive action)" : "not armed") + "; last arm event: " + LastArmEvent
                + "; this game first seen at tick " + FirstSeenTick + " with the arm " + (ArmedWhenGameFirstSeen ? "SET (unexpected)" : "clear")
                + "; arms granted " + ArmsGranted + ", spent " + ArmsSpent + ", refused " + ArmsRefused;
        }
    }

    /// <summary>One read-only observation of a pawn, as plain data (so the criteria are testable without a game).</summary>
    public sealed class S31Snap
    {
        public int tick;
        public string step;
        public int thingId = -1;
        public string nick;
        public string kind;
        public bool dead;
        public bool destroyed;
        public bool discarded;
        public bool spawned;
        public int mapId = -1;
        public string holder;
        public bool inWorld;
        public string situation;
        public bool reserved;
        public bool suspended;
        public int factionId = -1;
        public string factionName;
        public bool factionTemporary;
        public string job;
        public string lord;
        public long bioTicks = -1;
        public float food = -1f;
        public float rest = -1f;
        public int x = -1;
        public int z = -1;
        public int hediffs;
        public string hediffSig;
        public int maxHediffAge = -1;
        public int apparel;
        public string apparelSig;

        public bool Observed => thingId >= 0;

        public string Line()
        {
            StringBuilder b = new StringBuilder();
            b.Append("t=").Append(tick).Append(" [").Append(step).Append("] #").Append(thingId).Append(' ').Append(nick ?? "?").Append(" (").Append(kind ?? "?").Append(')');
            b.Append(" spawned=").Append(spawned).Append(" map=").Append(mapId).Append(" holder=").Append(holder ?? "none");
            b.Append(" inWorldPawns=").Append(inWorld).Append(" situation=").Append(situation ?? "?").Append(" reserved=").Append(reserved).Append(" suspended=").Append(suspended);
            b.Append(" faction=").Append(factionId < 0 ? "none" : factionId + (factionTemporary ? "(temporary)" : "")).Append(" job=").Append(job ?? "-").Append(" lord=").Append(lord ?? "-");
            b.Append(" bioTicks=").Append(bioTicks).Append(" food=").Append(food.ToString("0.000", CultureInfo.InvariantCulture)).Append(" rest=").Append(rest.ToString("0.000", CultureInfo.InvariantCulture));
            b.Append(" pos=").Append(x).Append(',').Append(z).Append(" hediffs=").Append(hediffs).Append(" apparel=").Append(apparel);
            if (dead) b.Append(" DEAD");
            if (destroyed) b.Append(" DESTROYED");
            if (discarded) b.Append(" DISCARDED");
            return b.ToString();
        }
    }

    /// <summary>The save/load checkpoint (scenario D), stored as one vanilla quest tag on the S31 manifest quest.</summary>
    public sealed class S31Checkpoint
    {
        public int thingId;
        public string nick;
        public string kind;
        public int factionId;
        public string apparelSig;
        public int hediffs;
        public long bioTicks;
        public int tick;

        public static S31Checkpoint From(S31Snap s)
        {
            return new S31Checkpoint
            {
                thingId = s.thingId, nick = s.nick, kind = s.kind, factionId = s.factionId, apparelSig = s.apparelSig, hediffs = s.hediffs,
                bioTicks = s.bioTicks, tick = s.tick
            };
        }

        private static string Clean(string v)
        {
            return (v ?? "").Replace('|', '/');
        }

        public string Encode()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return S31Ids.CheckpointPrefix + string.Join("|", new[]
            {
                thingId.ToString(c), Clean(nick), Clean(kind), factionId.ToString(c), Clean(apparelSig), hediffs.ToString(c), bioTicks.ToString(c), tick.ToString(c)
            });
        }

        public static bool TryDecode(string tag, out S31Checkpoint cp)
        {
            cp = null;
            if (tag == null || !tag.StartsWith(S31Ids.CheckpointPrefix, StringComparison.Ordinal)) return false;
            string[] f = tag.Substring(S31Ids.CheckpointPrefix.Length).Split('|');
            if (f.Length != 8) return false;
            CultureInfo c = CultureInfo.InvariantCulture;
            S31Checkpoint r = new S31Checkpoint { nick = f[1], kind = f[2], apparelSig = f[4] };
            if (!int.TryParse(f[0], NumberStyles.Integer, c, out r.thingId)) return false;
            if (!int.TryParse(f[3], NumberStyles.Integer, c, out r.factionId)) return false;
            if (!int.TryParse(f[5], NumberStyles.Integer, c, out r.hediffs)) return false;
            if (!long.TryParse(f[6], NumberStyles.Integer, c, out r.bioTicks)) return false;
            if (!int.TryParse(f[7], NumberStyles.Integer, c, out r.tick)) return false;
            cp = r;
            return true;
        }
    }

    public enum S31Outcome
    {
        Pass,
        Inconclusive,
        Fail
    }

    /// <summary>
    /// A scenario's verdict. FAIL = a required criterion is violated (M1 is then NOT acceptable as tested). INCONCLUSIVE = the run could
    /// not produce the evidence (too short, positive control did not fire): never read as PASS. Observations are evidence, not verdicts.
    /// </summary>
    public sealed class S31Verdict
    {
        public readonly string scenario;
        public readonly List<string> failures = new List<string>();
        public readonly List<string> gaps = new List<string>();
        public readonly List<string> notes = new List<string>();

        public S31Verdict(string scenario)
        {
            this.scenario = scenario;
        }

        public S31Outcome Outcome => failures.Count > 0 ? S31Outcome.Fail : gaps.Count > 0 ? S31Outcome.Inconclusive : S31Outcome.Pass;

        public void Fail(string what)
        {
            failures.Add(what);
        }

        public void Gap(string what)
        {
            gaps.Add(what);
        }

        public void Note(string what)
        {
            notes.Add(what);
        }

        public string Block()
        {
            StringBuilder b = new StringBuilder();
            string word = Outcome == S31Outcome.Pass ? "PASS" : Outcome == S31Outcome.Fail ? "FAIL" : "INCONCLUSIVE";
            b.AppendLine(S31Ids.LogPrefix + "===== " + scenario + " RESULT: " + word + " =====");
            foreach (string f in failures) b.AppendLine(S31Ids.LogPrefix + "  FAIL: " + f);
            foreach (string g in gaps) b.AppendLine(S31Ids.LogPrefix + "  INCONCLUSIVE: " + g);
            foreach (string n in notes) b.AppendLine(S31Ids.LogPrefix + "  note: " + n);
            b.Append(S31Ids.LogPrefix + "===== end of " + scenario + " =====");
            return b.ToString();
        }
    }

    /// <summary>
    /// The S31 pass/fail rules, as pure functions over observations. They encode PHYSICAL_LIFECYCLE § 7.6 and the S31 brief; a headless
    /// test proves the RULES, never a runtime result (the runtime result exists only when the owner runs the spike in RimWorld).
    /// </summary>
    public static class S31Criteria
    {
        /// <summary>The shortest spawned observation that can show a pawn is not frozen.</summary>
        public const int MinSpawnedWindowTicks = 600;

        /// <summary>The shortest stored observation after the pass (long enough for a temporary faction's vanilla removal).</summary>
        public const int MinStoredWindowTicks = 600;

        /// <summary>
        /// M1 while spawned: a reserved pawn must behave like any spawned pawn. Ticking (biological age), needs, a job, movement, its Lord
        /// and its faction are observed; <c>Suspended</c> is recorded but is never the only proof.
        /// </summary>
        public static void SpawnedBehaviour(S31Verdict v, string who, IList<S31Snap> samples, bool expectHediffTicking)
        {
            List<S31Snap> s = new List<S31Snap>();
            if (samples != null) foreach (S31Snap x in samples) if (x != null && x.spawned) s.Add(x);
            if (s.Count < 2)
            {
                v.Gap(who + ": fewer than two observations while spawned");
                return;
            }
            S31Snap first = s[0], last = s[s.Count - 1];
            bool moved = false, needsChanged = false, hadJob = false;
            HashSet<string> jobs = new HashSet<string>();
            foreach (S31Snap x in s)
            {
                if (!x.reserved) v.Fail(who + ": NOT reserved while spawned at tick " + x.tick + " (M1 requires the reservation before the exit)");
                if (x.suspended) v.Fail(who + ": Suspended while spawned at tick " + x.tick + " (M1 FAILS: the reservation froze a spawned pawn)");
                if (x.situation != "None") v.Fail(who + ": world-pawn situation " + x.situation + " while spawned at tick " + x.tick);
                if (x.lord == null) v.Fail(who + ": lost its Lord while spawned at tick " + x.tick);
                if (x.factionId != first.factionId) v.Fail(who + ": faction changed while spawned (" + first.factionId + " -> " + x.factionId + ") at tick " + x.tick);
                if (x.x != first.x || x.z != first.z) moved = true;
                if (Math.Abs(x.food - first.food) > 1e-6f || Math.Abs(x.rest - first.rest) > 1e-6f) needsChanged = true;
                if (x.job != null)
                {
                    hadJob = true;
                    jobs.Add(x.job);
                }
            }
            int window = last.tick - first.tick;
            if (window < MinSpawnedWindowTicks) v.Gap(who + ": observed only " + window + " ticks while spawned (need at least " + MinSpawnedWindowTicks + ")");
            long bio = last.bioTicks - first.bioTicks;
            if (bio <= 0) v.Fail(who + ": biological age did not advance over " + window + " spawned ticks: the reserved pawn did not tick (M1 FAILS)");
            else v.Note(who + ": ticked while reserved and spawned: biological age +" + bio + " over " + window + " game ticks");
            if (!needsChanged) v.Fail(who + ": needs (food, rest) never changed over " + window + " spawned ticks");
            if (!moved) v.Fail(who + ": never moved over " + window + " spawned ticks (pathing frozen?)");
            if (!hadJob) v.Fail(who + ": never had a job while spawned");
            else v.Note(who + ": jobs seen while reserved: " + string.Join(", ", new List<string>(jobs).ToArray()));
            if (expectHediffTicking)
            {
                if (last.maxHediffAge <= first.maxHediffAge) v.Fail(who + ": health did not tick (hediff age " + first.maxHediffAge + " -> " + last.maxHediffAge + ")");
                else v.Note(who + ": health ticked while reserved (hediff age " + first.maxHediffAge + " -> " + last.maxHediffAge + ")");
            }
            else if (last.hediffs == 0)
            {
                v.Note(who + ": no hediff to show health ticking here (scenario C shows it with an injury)");
            }
        }

        /// <summary>
        /// Immediately after vanilla put the pawn into WorldPawns (the synchronous LeftMap observation for a normal exit, the line after
        /// DeinitAndRemoveMap for a map removal). The M1 target: the reservation existed before and still holds, so the pawn was never
        /// an unreserved Free world pawn.
        /// </summary>
        public static void AfterPass(S31Verdict v, string who, S31Snap before, S31Snap at)
        {
            if (at == null || !at.Observed)
            {
                v.Gap(who + ": no observation immediately after the pass");
                return;
            }
            if (before != null && before.Observed)
            {
                if (!before.reserved) v.Fail(who + ": NOT reserved just before the exit (tick " + before.tick + ")");
                if (at.thingId != before.thingId) v.Fail(who + ": a different thing id after the exit (" + before.thingId + " -> " + at.thingId + ")");
                if (at.nick != before.nick || at.kind != before.kind) v.Fail(who + ": name or kind changed at the pass (" + before.nick + "/" + before.kind + " -> " + at.nick + "/" + at.kind + ")");
                if (at.factionId != before.factionId) v.Fail(who + ": faction rewritten AT the pass (" + before.factionId + " -> " + at.factionId + ")");
                if (at.apparelSig != before.apparelSig) v.Fail(who + ": apparel changed at the pass (redressed?)");
            }
            if (at.spawned) v.Fail(who + ": still spawned after the pass");
            if (!at.inWorld) v.Fail(who + ": not in WorldPawns after the pass");
            if (!at.reserved) v.Fail(who + ": reservation GONE immediately after the pass (an M1 window exists)");
            if (at.situation != "ReservedByQuest") v.Fail(who + ": world-pawn situation " + at.situation + " immediately after the pass (anything but ReservedByQuest exposes the pawn; Free = redress, GC and faction-rewrite eligible)");
            if (at.dead || at.destroyed || at.discarded) v.Fail(who + ": dead, destroyed or discarded at the pass");
        }

        /// <summary>The stored window: never Free, never discarded, and no faction but its own temporary one or none (§ 13.2).</summary>
        public static void Stored(S31Verdict v, string who, int ownFactionId, IList<S31Snap> stored)
        {
            List<S31Snap> s = new List<S31Snap>();
            if (stored != null) foreach (S31Snap x in stored) if (x != null && x.Observed) s.Add(x);
            if (s.Count == 0)
            {
                v.Gap(who + ": no stored observation");
                return;
            }
            int nulledAt = -1;
            foreach (S31Snap x in s)
            {
                if (x.spawned) continue;
                if (!x.inWorld) v.Fail(who + ": left WorldPawns while stored (tick " + x.tick + ", holder " + (x.holder ?? "none") + ")");
                if (!x.reserved) v.Fail(who + ": reservation lost while stored (tick " + x.tick + ")");
                if (x.situation != "ReservedByQuest") v.Fail(who + ": situation " + x.situation + " while stored (tick " + x.tick + ")");
                if (x.dead || x.destroyed || x.discarded) v.Fail(who + ": dead, destroyed or discarded while stored (tick " + x.tick + ")");
                if (x.factionId != ownFactionId && x.factionId != -1) v.Fail(who + ": faction REWRITTEN to " + x.factionId + " (" + x.factionName + ") while stored (only its own temporary faction or none is acceptable)");
                if (x.factionId == -1 && ownFactionId != -1 && nulledAt < 0) nulledAt = x.tick;
                if (!x.suspended) v.Note(who + ": not Suspended while stored at tick " + x.tick + " (unexpected for a reserved world pawn; recorded)");
            }
            if (nulledAt >= 0) v.Note(who + ": its temporary faction was removed by vanilla; faction null from tick " + nulledAt + " while still reserved (PHYSICAL_LIFECYCLE § 13.2 expects this)");
            int window = s[s.Count - 1].tick - s[0].tick;
            if (window < MinStoredWindowTicks) v.Gap(who + ": observed only " + window + " ticks while stored (need at least " + MinStoredWindowTicks + ")");
        }

        /// <summary>The synchronous LeftMap observation (M2 observability). For a map removal the audit expects NO LeftMap for these pawns.</summary>
        public static void LeftMap(S31Verdict v, string who, bool expected, S31Snap atSignal)
        {
            if (expected)
            {
                if (atSignal == null) v.Note(who + ": no LeftMap signal observed (M2 evidence missing; M1 does not depend on it)");
                else v.Note(who + ": LeftMap arrived after vanilla's pass: inWorldPawns=" + atSignal.inWorld + ", reserved=" + atSignal.reserved + ", situation=" + atSignal.situation);
            }
            else if (atSignal != null)
            {
                v.Note(who + ": AUDIT DEVIATION: a LeftMap signal WAS sent on map removal (the audit expected none)");
            }
            else
            {
                v.Note(who + ": confirmed: no LeftMap signal on map removal (as the audit predicted)");
            }
        }

        public static void NoAlreadyHere(S31Verdict v, IList<string> lines)
        {
            if (lines != null && lines.Count > 0) v.Fail("an \"already here\" PassToWorld error was logged: " + lines[0]);
            else v.Note("no \"already here\" error in the log; the harness itself never calls PassToWorld, as a static scan checks");
        }

        /// <summary>Scenario F: the pressure must reach the spike-owned pool (positive control) and must never reach the probe.</summary>
        public static void Redress(S31Verdict v, int requests, int decoys, int decoysRedressed, int probeSelected, int probeInFreeSet, int newlyGenerated)
        {
            v.Note("redress pressure: " + requests + " generation requests with a forced redress chance; " + decoysRedressed + " of " + decoys + " unreserved decoys redressed; " + newlyGenerated + " new pawns generated");
            if (probeSelected > 0) v.Fail("CRITICAL: the reserved probe was returned by PawnGenerator " + probeSelected + " time(s): it was redressed");
            if (probeInFreeSet > 0) v.Fail("the reserved probe appeared in WorldPawns' Free set " + probeInFreeSet + " time(s) (the set every redress and the faction-doesn't-matter path draw from)");
            if (decoysRedressed == 0) v.Gap("positive control did not fire: no decoy was redressed, so this run cannot show the pressure was real");
        }

        /// <summary>Scenario G: the SAME pawn comes back; no twin, no generation, coherent reservation while spawned again.</summary>
        public static void Rematerialized(S31Verdict v, S31Snap stored, S31Snap spawned, int sameIdCount, int sameNickCount, bool sameReference)
        {
            if (!sameReference) v.Fail("the spawned pawn is not the same object as the stored one");
            if (spawned == null || stored == null || !spawned.Observed || !stored.Observed)
            {
                v.Gap("missing before/after observation for the rematerialization");
                return;
            }
            if (spawned.thingId != stored.thingId) v.Fail("thing id changed on rematerialization (" + stored.thingId + " -> " + spawned.thingId + ")");
            if (spawned.nick != stored.nick || spawned.kind != stored.kind) v.Fail("name or kind changed on rematerialization");
            if (spawned.hediffSig != stored.hediffSig) v.Note("hediffs changed while stored: " + stored.hediffSig + " -> " + spawned.hediffSig);
            if (spawned.apparelSig != stored.apparelSig) v.Fail("apparel changed while stored (redressed?)");
            if (sameIdCount != 1) v.Fail(sameIdCount + " pawns share thing id " + stored.thingId + " (a twin, or the pawn is missing)");
            if (sameNickCount != 1) v.Fail(sameNickCount + " pawns are named " + stored.nick + " (a twin was generated)");
            if (!spawned.spawned) v.Fail("not spawned after rematerialization");
            if (spawned.inWorld) v.Fail("still in WorldPawns while spawned (vanilla SpawnSetup should have removed it)");
            if (!spawned.reserved) v.Fail("not reserved while spawned again (M1 keeps the reservation for the whole retained life)");
            if (spawned.suspended) v.Fail("Suspended while spawned again");
            v.Note("age: biological ticks " + stored.bioTicks + " (stored) -> " + spawned.bioTicks + " (spawned); truthful aging is S12's question, recorded only");
        }

        /// <summary>Scenario D, after the owner's save and reload.</summary>
        public static void AfterLoad(S31Verdict v, S31Checkpoint cp, S31Snap now, int sameIdCount, int sameNickCount)
        {
            string who = "#" + cp.thingId + " " + cp.nick;
            if (now == null || !now.Observed)
            {
                v.Fail(who + ": not found after load");
                return;
            }
            if (sameIdCount != 1) v.Fail(who + ": " + sameIdCount + " pawns share its thing id after load");
            if (sameNickCount != 1) v.Fail(who + ": " + sameNickCount + " pawns share its name after load (duplicate?)");
            if (now.nick != cp.nick || now.kind != cp.kind) v.Fail(who + ": name or kind differs after load");
            if (!now.inWorld) v.Fail(who + ": not in WorldPawns after load");
            if (!now.reserved) v.Fail(who + ": reservation NOT restored after load");
            if (now.situation != "ReservedByQuest") v.Fail(who + ": situation " + now.situation + " after load");
            if (now.factionId != cp.factionId) v.Fail(who + ": faction changed across save/load (" + cp.factionId + " -> " + now.factionId + ")");
            if (now.apparelSig != cp.apparelSig) v.Fail(who + ": apparel changed across save/load (redressed?)");
            if (now.hediffs != cp.hediffs) v.Note(who + ": hediff count " + cp.hediffs + " -> " + now.hediffs);
            if (now.dead || now.destroyed || now.discarded) v.Fail(who + ": dead, destroyed or discarded after load");
            v.Note(who + ": biological ticks " + cp.bioTicks + " at the checkpoint, " + now.bioTicks + " now (a suspended stored pawn does not age; recorded only)");
        }
    }
}
