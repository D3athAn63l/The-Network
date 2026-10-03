using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TheNetwork.Core;
using TheNetwork.Diagnostics.Spikes.S31;

namespace TheNetwork.Tests
{
    /// <summary>
    /// Spike S31 (PHYSICAL_LIFECYCLE § 7.6), the part provable WITHOUT RimWorld: the session arm, the S31 markers, the checkpoint codec,
    /// the pass/fail rules (over synthetic observations), and the source isolation of the harness. These tests prove the RULES and the
    /// SCOPE; they never stand in for the runtime result. S31 stays NOT RUN until the owner runs the checklist in a real game.
    /// </summary>
    public static class S31SpikeTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("S31.ArmDefaultsFalseAndIsNeverPersisted", ArmDefaultsFalse));
            t.Add(new KeyValuePair<string, Action>("S31.ArmNeedsTheExactPhrase", ArmPhrase));
            t.Add(new KeyValuePair<string, Action>("S31.OneArmAuthorisesOneDestructiveAction", ArmSpentOnce));
            t.Add(new KeyValuePair<string, Action>("S31.ALoadClearsTheArm", LoadClearsArm));
            t.Add(new KeyValuePair<string, Action>("S31.MarkersCannotMatchProductionEntities", Markers));
            t.Add(new KeyValuePair<string, Action>("S31.CheckpointRoundTrip", Checkpoint));
            t.Add(new KeyValuePair<string, Action>("S31.CriteriaSpawnedBehaviour", CriteriaSpawned));
            t.Add(new KeyValuePair<string, Action>("S31.CriteriaAfterThePass", CriteriaAfterPass));
            t.Add(new KeyValuePair<string, Action>("S31.CriteriaStoredAndFaction", CriteriaStored));
            t.Add(new KeyValuePair<string, Action>("S31.CriteriaRedressNeedsAPositiveControl", CriteriaRedress));
            t.Add(new KeyValuePair<string, Action>("S31.DisposalIsFailClosedOnOwnership", DisposalFailClosed));
            t.Add(new KeyValuePair<string, Action>("S31.PressureReturnsAreClassifiedByProof", PressureReturns));
            t.Add(new KeyValuePair<string, Action>("S31.MissingLeftMapIsInconclusive", MissingLeftMap));
            t.Add(new KeyValuePair<string, Action>("S31.CriteriaRematerializationAndLoad", CriteriaRematerializeAndLoad));
            t.Add(new KeyValuePair<string, Action>("S31.HarnessLivesOnlyInTheSpikeScope", SourceScope));
            t.Add(new KeyValuePair<string, Action>("S31.SafeSuitesCannotInvokeS31", SafeSuitesCannotInvoke));
            t.Add(new KeyValuePair<string, Action>("S31.ProductionStaysFailClosed", ProductionFailClosed));
            t.Add(new KeyValuePair<string, Action>("S31.TestMapDefIsNarrow", TestMapDef));
            t.Add(new KeyValuePair<string, Action>("S31.IdlePumpIsInert", IdlePump));
            t.Add(new KeyValuePair<string, Action>("S31.RecordStaysNotRun", RecordNotRun));
        }

        // ================================================================== helpers

        private static string Repo
        {
            get
            {
                string repo = Environment.GetEnvironmentVariable("THENETWORK_REPO");
                if (!string.IsNullOrEmpty(repo)) return repo;
                for (DirectoryInfo d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
                {
                    if (Directory.Exists(Path.Combine(d.FullName, "Source/TheNetwork"))) return d.FullName;
                }
                throw new InvalidOperationException("repository root not found (set THENETWORK_REPO)");
            }
        }

        private static string SourceRoot => Path.Combine(Repo, "Source/TheNetwork");

        private static string Rel(string f)
        {
            return f.Replace('\\', '/').Substring(SourceRoot.Replace('\\', '/').Length + 1);
        }

        private static bool InSpike(string f)
        {
            return Rel(f).StartsWith("Diagnostics/Spikes/S31/", StringComparison.Ordinal);
        }

        private static string[] Sources()
        {
            return Directory.GetFiles(SourceRoot, "*.cs", SearchOption.AllDirectories).Where(f => !Rel(f).StartsWith("obj/", StringComparison.Ordinal)).ToArray();
        }

        private static string Code(string src)
        {
            string noBlock = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(noBlock, @"//[^\n]*", "");
        }

        private static S31Snap Spawned(int tick, int x, float food, long bio)
        {
            return new S31Snap
            {
                tick = tick, step = "spawned", thingId = 7, nick = "S31-Probe-A-1", kind = "Villager", spawned = true, mapId = 3, inWorld = false, situation = "None",
                reserved = true, suspended = false, factionId = 40, factionTemporary = true, job = tick % 60 == 0 ? "Goto" : "Wait_Wander", lord = "LordJob_ExitMapBest",
                bioTicks = bio, food = food, rest = 0.9f, x = x, z = 50, apparelSig = "Apparel_Pants,Apparel_Shirt"
            };
        }

        private static S31Snap Stored(int tick, int factionId)
        {
            return new S31Snap
            {
                tick = tick, step = "stored", thingId = 7, nick = "S31-Probe-A-1", kind = "Villager", spawned = false, inWorld = true, situation = "ReservedByQuest",
                reserved = true, suspended = true, factionId = factionId, apparelSig = "Apparel_Pants,Apparel_Shirt"
            };
        }

        private static List<S31Snap> GoodSpawnedWindow()
        {
            List<S31Snap> l = new List<S31Snap>();
            for (int i = 0; i <= 25; i++) l.Add(Spawned(1000 + i * 30, 50 + i, 0.8f - i * 0.001f, 100000 + i * 30));
            return l;
        }

        // ================================================================== the arm

        private static void ArmDefaultsFalse()
        {
            S31Session s = new S31Session();
            object game = new object();
            T.Check(!s.IsArmed(game), "a fresh session is not armed");
            T.Check(!s.IsArmed(null), "no game, no arm");
            foreach (Type ty in typeof(S31Session).Assembly.GetTypes().Where(x => x.Namespace == "TheNetwork.Diagnostics.Spikes.S31"))
            {
                T.Check(!typeof(Verse.IExposable).IsAssignableFrom(ty), "no S31 type is IExposable (" + ty.Name + ")");
            }
            foreach (string f in Sources().Where(InSpike))
            {
                string code = Code(File.ReadAllText(f));
                T.Check(!Regex.IsMatch(code, @"\bScribe(_\w+)?\b|\bExposeData\b|\bIExposable\b|\bGameComponent\b|\bWorldComponent\b|\bMapComponent\b"), "S31 code saves nothing of its own (" + Rel(f) + ")");
            }
        }

        private static void ArmPhrase()
        {
            S31Session s = new S31Session();
            object game = new object();
            string reason;
            T.Check(!s.TryArm(game, 10, "", out reason) && reason != null, "an empty phrase is refused");
            T.Check(!s.TryArm(game, 10, "arm s31", out reason), "the phrase is case-sensitive");
            T.Check(!s.TryArm(game, 10, "ARM", out reason), "a partial phrase is refused");
            T.Check(!s.TryArm(null, 10, S31Ids.ArmPhrase, out reason), "no game, no arm");
            T.Check(!s.IsArmed(game), "nothing armed by a refusal");
            T.Eq(4, s.ArmsRefused, "every refusal counted");
            T.Check(s.TryArm(game, 11, "  " + S31Ids.ArmPhrase + " ", out reason), "the exact phrase arms (surrounding spaces ignored)");
            T.Check(s.IsArmed(game), "armed for this game");
            T.Eq("ARM S31", S31Ids.ArmPhrase, "the documented phrase");
        }

        private static void ArmSpentOnce()
        {
            S31Session s = new S31Session();
            object game = new object();
            string reason;
            T.Check(!s.Spend(game, 5, "Run A", out reason) && reason.Contains(S31Ids.ArmPhrase), "a destructive action is refused without the arm, and the refusal says how to arm");
            s.TryArm(game, 6, S31Ids.ArmPhrase, out reason);
            T.Check(s.Spend(game, 7, "Run A", out reason), "the arm authorises one destructive action");
            T.Check(!s.IsArmed(game), "and is spent by it");
            T.Check(!s.Spend(game, 8, "Run B", out reason), "a second destructive action needs a new arm");
            s.TryArm(game, 9, S31Ids.ArmPhrase, out reason);
            s.Disarm("test");
            T.Check(!s.IsArmed(game), "disarm clears it");
            T.Eq(1, s.ArmsSpent, "one arm spent");
        }

        private static void LoadClearsArm()
        {
            S31Session s = new S31Session();
            object before = new object(), afterLoad = new object();
            string reason;
            s.TryArm(before, 100, S31Ids.ArmPhrase, out reason);
            T.Check(s.IsArmed(before), "armed in the first game");
            T.Check(s.Observe(afterLoad, 5), "a load is a different game object");
            T.Check(!s.IsArmed(afterLoad), "the arm is false in the loaded game");
            T.Check(!s.IsArmed(before), "and does not come back for the old game object");
            T.Check(!s.ArmedWhenGameFirstSeen, "the arm was clear when the loaded game was first observed");
            T.Eq(5, s.FirstSeenTick, "first-seen tick recorded");
            T.Check(!s.Spend(afterLoad, 6, "Run A", out reason), "nothing destructive runs after a load without re-arming");
            T.Check(!s.Observe(afterLoad, 7), "observing the same game again changes nothing");
            T.Check(s.TryArm(afterLoad, 8, S31Ids.ArmPhrase, out reason) && s.IsArmed(afterLoad), "re-arming explicitly works");
            T.Check(s.Observe(null, 0) && !s.IsArmed(afterLoad), "returning to the menu clears it too");
        }

        // ================================================================== markers and the checkpoint

        private static void Markers()
        {
            string[] all = { S31Ids.ProbeTag, S31Ids.DecoyTag, S31Ids.LeaderTag, S31Ids.PressureTag, S31Ids.ManifestTag, S31Ids.PoolQuestTag, S31Ids.MapPrefix, S31Ids.FactionPrefix, S31Ids.CheckpointPrefix };
            foreach (string m in all) T.Check(S31Ids.IsMarker(m), "every S31 marker carries the S31 root (" + m + ")");
            T.Eq(all.Length, all.Distinct().Count(), "markers are distinct");
            T.Check(!S31Ids.IsMarker(null) && !S31Ids.IsMarker("Quest12.pawn") && !S31Ids.IsMarker("TheNetwork") && !S31Ids.IsMarker("TheNetwork.S31"), "vanilla-style and Network tags are not S31 markers");
            T.Check(!S31Ids.CarriesMarker(null) && !S31Ids.CarriesMarker(new List<string> { "Quest3.lodgers", "Quest9.raid" }), "a pawn without an S31 tag is never S31's");
            T.Check(S31Ids.CarriesMarker(new List<string> { "Quest3.lodgers", S31Ids.ProbeTag }), "an S31 tag among others is found");
            T.Check(S31Ids.IsSignal(S31Ids.ProbeTag + ".LeftMap") && S31Ids.IsSignal(S31Ids.DecoyTag + ".Destroyed"), "S31 pawn signals are recognised");
            T.Check(!S31Ids.IsSignal("Quest4.lodgers.LeftMap") && !S31Ids.IsSignal(S31Ids.ManifestTag + ".Added") && !S31Ids.IsSignal(null), "no other signal is");
            T.Eq("LeftMap", S31Ids.SignalPart(S31Ids.ProbeTag + ".LeftMap"), "signal part");
            int v;
            T.Check(S31Ids.TryParseIntTag(S31Ids.IntTag(S31Ids.MapPrefix, 4711), S31Ids.MapPrefix, out v) && v == 4711, "map tag round trip");
            T.Check(!S31Ids.TryParseIntTag("TheNetwork_S31_Map:x", S31Ids.MapPrefix, out v) && !S31Ids.TryParseIntTag(S31Ids.IntTag(S31Ids.FactionPrefix, 3), S31Ids.MapPrefix, out v), "malformed or foreign tags refused");
            // No production file outside the spike uses an S31 marker: production entities can never carry one by accident.
            foreach (string f in Sources().Where(x => !InSpike(x)))
            {
                T.Check(!File.ReadAllText(f).Contains(S31Ids.MarkerRoot), "no production code writes an S31 marker (" + Rel(f) + ")");
            }
            T.Check(S31Ids.ProbeNick.StartsWith("S31-") && S31Ids.DecoyNick.StartsWith("S31-") && S31Ids.FactionName.StartsWith("S31 "), "names are unmistakably S31's");
        }

        private static void Checkpoint()
        {
            S31Snap s = Stored(5000, -1);
            s.bioTicks = 123456789012L;
            s.hediffs = 2;
            S31Checkpoint cp = S31Checkpoint.From(s);
            string tag = cp.Encode();
            T.Check(tag.StartsWith(S31Ids.CheckpointPrefix), "the checkpoint is an S31 quest tag");
            S31Checkpoint back;
            T.Check(S31Checkpoint.TryDecode(tag, out back), "decodes");
            T.Check(back.thingId == 7 && back.nick == s.nick && back.kind == s.kind && back.factionId == -1 && back.apparelSig == s.apparelSig && back.hediffs == 2 && back.bioTicks == 123456789012L && back.tick == 5000, "round trip is exact");
            S31Snap pipe = Stored(1, 3);
            pipe.nick = "bad|name";
            S31Checkpoint p2;
            T.Check(S31Checkpoint.TryDecode(S31Checkpoint.From(pipe).Encode(), out p2) && p2.nick == "bad/name", "a separator inside a name cannot break the record");
            T.Check(!S31Checkpoint.TryDecode("TheNetwork_S31_Checkpoint:1|2", out back) && !S31Checkpoint.TryDecode(null, out back) && !S31Checkpoint.TryDecode("Quest1.x", out back), "malformed records refused");
        }

        // ================================================================== the rules

        private static void CriteriaSpawned()
        {
            S31Verdict ok = new S31Verdict("t");
            S31Criteria.SpawnedBehaviour(ok, "p", GoodSpawnedWindow(), false);
            T.Eq(S31Outcome.Pass, ok.Outcome, "a reserved pawn that ticks, moves, has jobs and needs, and keeps its Lord passes (" + string.Join("; ", ok.failures.Concat(ok.gaps).ToArray()) + ")");

            List<S31Snap> frozen = GoodSpawnedWindow();
            foreach (S31Snap s in frozen)
            {
                s.bioTicks = 100000;
                s.x = 50;
                s.food = 0.8f;
                s.rest = 0.9f;
            }
            S31Verdict f = new S31Verdict("t");
            S31Criteria.SpawnedBehaviour(f, "p", frozen, false);
            T.Check(f.Outcome == S31Outcome.Fail && f.failures.Any(x => x.Contains("did not tick")) && f.failures.Any(x => x.Contains("never moved")) && f.failures.Any(x => x.Contains("needs")), "a frozen pawn FAILS M1");

            List<S31Snap> suspended = GoodSpawnedWindow();
            suspended[3].suspended = true;
            S31Verdict sv = new S31Verdict("t");
            S31Criteria.SpawnedBehaviour(sv, "p", suspended, false);
            T.Check(sv.failures.Any(x => x.Contains("Suspended while spawned")), "Suspended while spawned FAILS");

            List<S31Snap> unreserved = GoodSpawnedWindow();
            unreserved[0].reserved = false;
            S31Verdict uv = new S31Verdict("t");
            S31Criteria.SpawnedBehaviour(uv, "p", unreserved, false);
            T.Check(uv.failures.Any(x => x.Contains("NOT reserved while spawned")), "M1 requires the reservation before the exit");

            List<S31Snap> lordless = GoodSpawnedWindow();
            lordless[4].lord = null;
            S31Verdict lv = new S31Verdict("t");
            S31Criteria.SpawnedBehaviour(lv, "p", lordless, false);
            T.Check(lv.failures.Any(x => x.Contains("lost its Lord")), "losing the Lord FAILS");

            List<S31Snap> shortWindow = GoodSpawnedWindow().Take(5).ToList();
            S31Verdict w = new S31Verdict("t");
            S31Criteria.SpawnedBehaviour(w, "p", shortWindow, false);
            T.Check(w.Outcome == S31Outcome.Inconclusive && w.gaps.Any(x => x.Contains("observed only")), "a short window is INCONCLUSIVE, never PASS");

            S31Verdict none = new S31Verdict("t");
            S31Criteria.SpawnedBehaviour(none, "p", null, false);
            T.Eq(S31Outcome.Inconclusive, none.Outcome, "no observation is INCONCLUSIVE");

            List<S31Snap> health = GoodSpawnedWindow();
            S31Verdict h = new S31Verdict("t");
            S31Criteria.SpawnedBehaviour(h, "p", health, true);
            T.Check(h.failures.Any(x => x.Contains("health did not tick")), "scenario C requires the injury to age");
            for (int i = 0; i < health.Count; i++) health[i].maxHediffAge = 10 + i * 30;
            S31Verdict h2 = new S31Verdict("t");
            S31Criteria.SpawnedBehaviour(h2, "p", health, true);
            T.Eq(S31Outcome.Pass, h2.Outcome, "an ageing injury passes");
        }

        private static void CriteriaAfterPass()
        {
            S31Snap before = Spawned(2000, 99, 0.7f, 200000);
            S31Verdict ok = new S31Verdict("t");
            S31Snap at = Stored(2001, 40);
            at.suspended = true;
            S31Criteria.AfterPass(ok, "p", before, at);
            T.Eq(S31Outcome.Pass, ok.Outcome, "reserved before and ReservedByQuest immediately after: no window (" + string.Join("; ", ok.failures.ToArray()) + ")");

            S31Snap free = Stored(2001, 40);
            free.situation = "Free";
            free.reserved = false;
            S31Verdict fv = new S31Verdict("t");
            S31Criteria.AfterPass(fv, "p", before, free);
            T.Check(fv.failures.Any(x => x.Contains("reservation GONE")) && fv.failures.Any(x => x.Contains("situation Free")), "a Free pawn after the pass FAILS M1");

            S31Snap rewritten = Stored(2001, 77);
            S31Verdict rv = new S31Verdict("t");
            S31Criteria.AfterPass(rv, "p", before, rewritten);
            T.Check(rv.failures.Any(x => x.Contains("faction rewritten AT the pass")), "a faction rewrite at the pass FAILS");

            S31Snap missing = Stored(2001, 40);
            missing.inWorld = false;
            missing.situation = "None";
            S31Verdict mv = new S31Verdict("t");
            S31Criteria.AfterPass(mv, "p", before, missing);
            T.Check(mv.failures.Any(x => x.Contains("not in WorldPawns")), "a pawn missing from WorldPawns FAILS");

            S31Snap redressed = Stored(2001, 40);
            redressed.apparelSig = "Apparel_Duster";
            S31Verdict dv = new S31Verdict("t");
            S31Criteria.AfterPass(dv, "p", before, redressed);
            T.Check(dv.failures.Any(x => x.Contains("apparel changed")), "a redress at the pass FAILS");

            S31Verdict gv = new S31Verdict("t");
            S31Criteria.AfterPass(gv, "p", before, null);
            T.Eq(S31Outcome.Inconclusive, gv.Outcome, "no observation after the pass is INCONCLUSIVE");

            S31Verdict lm = new S31Verdict("t");
            S31Criteria.LeftMap(lm, "p", false, at, null);
            T.Check(lm.notes.Any(x => x.Contains("AUDIT DEVIATION")), "a LeftMap on map removal is reported as an audit deviation");
            S31Verdict lm2 = new S31Verdict("t");
            S31Criteria.LeftMap(lm2, "p", false, null, null);
            T.Check(lm2.notes.Any(x => x.Contains("confirmed: no LeftMap")) && lm2.Outcome == S31Outcome.Pass, "no LeftMap on map removal is confirmed, not failed");

            S31Verdict ah = new S31Verdict("t");
            S31Criteria.NoAlreadyHere(ah, new List<string> { "Tried to pass pawn S31 to world, but it's already here." });
            T.Eq(S31Outcome.Fail, ah.Outcome, "an \"already here\" error FAILS");
        }

        private static void CriteriaStored()
        {
            List<S31Snap> l = new List<S31Snap>();
            for (int i = 0; i < 30; i++) l.Add(Stored(3000 + i * 30, i < 5 ? 40 : -1));
            S31Verdict ok = new S31Verdict("t");
            S31Criteria.Stored(ok, "p", 40, l);
            T.Eq(S31Outcome.Pass, ok.Outcome, "its own temporary faction, then none (vanilla removed it): accepted (§ 13.2)");
            T.Check(ok.notes.Any(x => x.Contains("faction null from tick 3150")), "the removal tick is recorded");

            List<S31Snap> rw = new List<S31Snap>(l);
            rw.Add(Stored(4000, 12));
            S31Verdict rv = new S31Verdict("t");
            S31Criteria.Stored(rv, "p", 40, rw);
            T.Check(rv.failures.Any(x => x.Contains("faction REWRITTEN to 12")), "a rewrite to another faction FAILS");

            List<S31Snap> fr = new List<S31Snap>(l);
            S31Snap free = Stored(4100, -1);
            free.situation = "Free";
            free.reserved = false;
            fr.Add(free);
            S31Verdict fv = new S31Verdict("t");
            S31Criteria.Stored(fv, "p", 40, fr);
            T.Check(fv.failures.Any(x => x.Contains("situation Free")) && fv.failures.Any(x => x.Contains("reservation lost")), "a Free stored pawn FAILS");

            List<S31Snap> gone = new List<S31Snap>(l);
            S31Snap discarded = Stored(4200, -1);
            discarded.discarded = true;
            discarded.inWorld = false;
            gone.Add(discarded);
            S31Verdict gv = new S31Verdict("t");
            S31Criteria.Stored(gv, "p", 40, gone);
            T.Check(gv.failures.Any(x => x.Contains("discarded")), "a discarded stored pawn FAILS");

            S31Verdict sh = new S31Verdict("t");
            S31Criteria.Stored(sh, "p", 40, l.Take(3).ToList());
            T.Eq(S31Outcome.Inconclusive, sh.Outcome, "a short stored window is INCONCLUSIVE");
        }

        private static void CriteriaRedress()
        {
            S31Verdict ok = new S31Verdict("t");
            S31Criteria.Redress(ok, 9, 6, 6, 0, 0, 3, null, 0);
            T.Eq(S31Outcome.Pass, ok.Outcome, "decoys redressed, probe never: PASS");
            S31Verdict sel = new S31Verdict("t");
            S31Criteria.Redress(sel, 9, 6, 5, 1, 0, 3, null, 0);
            T.Check(sel.Outcome == S31Outcome.Fail && sel.failures.Any(x => x.Contains("CRITICAL")), "the probe returned by PawnGenerator FAILS");
            S31Verdict inFree = new S31Verdict("t");
            S31Criteria.Redress(inFree, 9, 6, 6, 0, 2, 3, null, 0);
            T.Eq(S31Outcome.Fail, inFree.Outcome, "the probe in the Free set FAILS");
            S31Verdict noControl = new S31Verdict("t");
            S31Criteria.Redress(noControl, 9, 6, 0, 0, 0, 9, null, 0);
            T.Check(noControl.Outcome == S31Outcome.Inconclusive && noControl.gaps.Any(x => x.Contains("positive control")), "no decoy redressed: INCONCLUSIVE (the pressure was not shown to be real)");
        }

        // ================================================================== fail-closed disposal (review finding 1)

        private static bool May(IList<string> tags, bool spawned = false, bool onTestMap = false, bool playerHeld = false)
        {
            string reason;
            bool ok = S31Ownership.MayDispose(tags, spawned, onTestMap, playerHeld, out reason);
            T.Check(ok == (reason == null), "a refusal always carries its reason");
            return ok;
        }

        private static void DisposalFailClosed()
        {
            // 1. An unmarked pawn is refused, whatever else it carries: no tag, vanilla tags, an S31-looking NAME used as a tag, or an S31
            //    marker that is not a pawn tag (quest / map / faction / checkpoint markers never make a pawn S31's).
            T.Check(!May(null) && !May(new List<string>()), "a pawn without quest tags is refused");
            T.Check(!May(new List<string> { "Quest3.lodgers", "Quest9.raid" }), "a pawn with only vanilla quest tags is refused");
            T.Check(!May(new List<string> { S31Ids.ProbeNick + "A-1", S31Ids.DecoyNick + "F-2", S31Ids.FactionName + "A" }), "S31 names are not ownership");
            T.Check(!May(new List<string> { S31Ids.ManifestTag, S31Ids.PoolQuestTag, S31Ids.IntTag(S31Ids.MapPrefix, 3), S31Ids.IntTag(S31Ids.FactionPrefix, 4), S31Ids.CheckpointPrefix + "x" }),
                "an S31 marker that is not a pawn tag is refused");
            T.Check(!May(new List<string> { S31Ids.ProbeTag + "x", "x" + S31Ids.DecoyTag, S31Ids.MarkerRoot }), "near-miss tags are refused (exact pawn tags only)");
            string reason;
            S31Ownership.MayDispose(new List<string> { "Quest3.lodgers" }, false, false, false, out reason);
            T.Check(reason != null && reason.Contains("ownership not proven"), "the refusal says ownership is not proven");

            // 2. A marked S31 pawn is eligible; 6. decoy disposal stays allowed.
            foreach (string tag in S31Ownership.PawnTags) T.Check(May(new List<string> { "Quest3.lodgers", tag }), "an S31 pawn tag makes the pawn disposable (" + tag + ")");
            T.Check(May(new List<string> { S31Ids.DecoyTag }), "a known decoy stays disposable");
            T.Check(May(new List<string> { S31Ids.ProbeTag }, true, true), "an S31 pawn spawned on the S31 test map is disposable");
            T.Check(!May(new List<string> { S31Ids.ProbeTag }, true, false), "an S31 pawn spawned on any other map is refused");
            T.Check(!May(new List<string> { S31Ids.ProbeTag }, false, false, true), "an S31 pawn the player holds is refused");
            T.Eq(4, S31Ownership.PawnTags.Length, "four pawn tags: probe, decoy, faction leader, pressure pawn");
            foreach (string tag in S31Ownership.PawnTags) T.Check(S31Ids.IsMarker(tag), "each pawn tag is an S31 marker, so the marker-scoped cleanup finds it (" + tag + ")");

            // The only destructive pawn helper checks ownership itself, BEFORE anything destructive, and the unguarded helper is gone.
            string world = Code(File.ReadAllText(Path.Combine(SourceRoot, "Diagnostics/Spikes/S31/S31World.cs")));
            Match body = Regex.Match(world, @"public static bool TryDispose\(Pawn p, out string reason\)\s*\{(?<b>.*?)\n        \}", RegexOptions.Singleline);
            T.Check(body.Success, "S31World.TryDispose exists");
            string b = body.Groups["b"].Value;
            int check = b.IndexOf("S31Ownership.MayDispose(", StringComparison.Ordinal);
            int[] destructive = { b.IndexOf("f.leader = null", StringComparison.Ordinal), b.IndexOf("RemovePawn(", StringComparison.Ordinal), b.IndexOf(".Destroy(", StringComparison.Ordinal), b.IndexOf(".Discard(", StringComparison.Ordinal) };
            T.Check(check >= 0 && destructive.All(i => i > check), "TryDispose decides ownership before it changes anything");
            T.Check(Regex.IsMatch(b, @"if \(!S31Ownership\.MayDispose\([^\n]*\)\)\s*\{[^}]*return false;", RegexOptions.Singleline), "a refusal returns before any change");
            // 8. The cleanup stays marker-scoped: every destructive pawn call in the spike lives inside TryDispose, and the cleanup acts only
            //    on MarkedPawns().
            string spike = string.Join("\n", Sources().Where(InSpike).Select(f => Code(File.ReadAllText(f))).ToArray());
            string outside = spike.Replace(b, "");
            T.Check(!Regex.IsMatch(outside, @"\.Discard\s*\(|RemovePawn\s*\(|\.Destroy\s*\(\s*DestroyMode"), "no pawn is removed, destroyed or discarded outside TryDispose");
            T.Check(!Regex.IsMatch(spike, @"S31World\.Dispose\s*\(|\bvoid Dispose\s*\(Pawn"), "the unguarded Dispose helper is gone");
            string spikeRun = Code(File.ReadAllText(Path.Combine(SourceRoot, "Diagnostics/Spikes/S31/S31Spike.cs")));
            Match cleanup = Regex.Match(spikeRun, @"public static void Cleanup\(\)\s*\{(?<b>.*?)\n        \}", RegexOptions.Singleline);
            T.Check(cleanup.Success && cleanup.Groups["b"].Value.Contains("foreach (Pawn p in S31World.MarkedPawns())"), "the cleanup acts only on S31-marked pawns");
            T.Check(cleanup.Success && Regex.Matches(cleanup.Groups["b"].Value, @"S31World\.TryDispose\(p, out refused\)").Count == 2
                && Regex.Matches(cleanup.Groups["b"].Value, @"disposal refused").Count == 2, "both cleanup disposals go through TryDispose and report a refusal as preserved");
            T.Check(world.Contains("S31Ids.CarriesMarker(p.questTags)"), "MarkedPawns() selects by S31 marker only");
        }

        private static S31PressureReturn Classify(bool returned, bool isProbe, bool inDecoys, IList<string> tags, int id, bool spawned, out string why)
        {
            return S31Ownership.ClassifyPressureReturn(returned, isProbe, inDecoys, tags, id, 1000, 1040, spawned, out why);
        }

        private static void PressureReturns()
        {
            string why;
            // 7. The probe is preserved, whatever else is true of it.
            T.Eq(S31PressureReturn.Probe, Classify(true, true, false, new List<string> { S31Ids.ProbeTag }, 7, false, out why), "the probe is the probe");
            T.Eq(S31PressureReturn.Probe, Classify(true, true, false, null, 1001, false, out why), "even with a fresh-looking id");
            T.Eq(S31PressureAction.PreserveProbe, S31Ownership.ActionFor(S31PressureReturn.Probe), "the probe is preserved, never disposed");
            S31Verdict sel = new S31Verdict("t");
            S31Criteria.Redress(sel, 9, 6, 5, 1, 0, 3, null, 0);
            T.Check(sel.Outcome == S31Outcome.Fail && sel.failures.Any(x => x.Contains("CRITICAL")), "and the run FAILS");

            // 6. A known decoy is disposed; a listed decoy that lost its tag is not proven and is preserved.
            T.Eq(S31PressureReturn.KnownDecoy, Classify(true, false, true, new List<string> { S31Ids.DecoyTag }, 12, false, out why), "a listed, tagged decoy");
            T.Eq(S31PressureAction.Dispose, S31Ownership.ActionFor(S31PressureReturn.KnownDecoy), "is disposed");
            T.Eq(S31PressureReturn.Unexpected, Classify(true, false, true, null, 12, false, out why), "a listed decoy without its tag is not proven");

            // 3. An unknown, unmarked returned pawn is NOT treated as newly generated: an existing id is an existing pawn.
            S31PressureReturn old = Classify(true, false, false, null, 12, false, out why);
            T.Eq(S31PressureReturn.Unexpected, old, "an unmarked pawn with an id from before the request is unexpected");
            T.Check(why != null && why.Contains("not issued during this request"), "and the reason names the id fence (" + why + ")");
            T.Eq(S31PressureAction.PreserveAndFail, S31Ownership.ActionFor(old), "it is preserved, and the run fails");
            T.Check(S31Ownership.ActionFor(old) != S31PressureAction.MarkThenDispose && S31Ownership.ActionFor(old) != S31PressureAction.Dispose, "it is never marked or disposed");
            T.Eq(S31PressureReturn.Unexpected, Classify(true, false, false, null, 1000, false, out why), "the 'before' fence id itself is not inside the request");
            T.Eq(S31PressureReturn.Unexpected, Classify(true, false, false, null, 1040, false, out why), "nor the 'after' fence id");
            T.Eq(S31PressureReturn.Unexpected, Classify(true, false, false, null, 5000, false, out why), "an id after the request is not proven either");
            T.Eq(S31PressureReturn.Unexpected, Classify(true, false, false, new List<string> { "Quest3.lodgers" }, 1001, false, out why), "a fresh id that already carries a quest tag is not proven");
            T.Eq(S31PressureReturn.Unexpected, Classify(true, false, false, new List<string> { S31Ids.LeaderTag }, 30, false, out why), "an S31 pawn that is neither the probe nor a decoy is unexpected too");
            T.Eq(S31PressureReturn.Unexpected, Classify(true, false, false, null, 1001, true, out why), "a spawned return is not proven new");
            T.Eq(S31PressureReturn.NoPawn, Classify(false, false, false, null, -1, false, out why), "no pawn returned");
            T.Eq(S31PressureAction.Nothing, S31Ownership.ActionFor(S31PressureReturn.NoPawn), "nothing to act on");

            // 4. An unexpected return FAILS with an explicit reason that carries what vanilla returned.
            S31Verdict uv = new S31Verdict("t");
            S31Criteria.Redress(uv, 4, 6, 3, 0, 0, 0, new List<string> { "request 4: t=9 [identity] #12 Smith (Villager) inWorldPawns=False (not issued during this request)" }, 0);
            T.Eq(S31Outcome.Fail, uv.Outcome, "an unexpected returned pawn FAILS scenario F");
            T.Check(uv.failures.Any(x => x.Contains("UNEXPECTED") && x.Contains("#12 Smith") && x.Contains("PRESERVED untouched")), "the failure names the pawn and says it was preserved");
            S31Verdict nv = new S31Verdict("t");
            S31Criteria.Redress(nv, 9, 6, 6, 0, 0, 2, null, 1);
            T.Eq(S31Outcome.Inconclusive, nv.Outcome, "a request that returned no pawn is INCONCLUSIVE, not PASS");

            // 5. A genuinely new pawn is proven by the fences, then tagged, and only then disposable.
            T.Eq(S31PressureReturn.NewForRequest, Classify(true, false, false, null, 1001, false, out why), "an id issued inside the request, no tags, not spawned: new");
            T.Eq(S31PressureReturn.NewForRequest, Classify(true, false, false, new List<string>(), 1039, false, out why), "an empty tag list is no tag");
            T.Eq(S31PressureAction.MarkThenDispose, S31Ownership.ActionFor(S31PressureReturn.NewForRequest), "a new pawn is marked, then disposed");
            List<string> tags = null;
            T.Check(!May(tags), "unmarked, the new pawn is not yet disposable");
            S31Ownership.MarkPressurePawn(ref tags);
            T.Check(tags != null && tags.Contains(S31Ids.PressureTag) && May(tags), "marked, it is");
            S31Ownership.MarkPressurePawn(ref tags);
            T.Eq(1, tags.Count, "marking is idempotent");
            // Nothing in the classifier reads the faction: faction matching never makes a pawn disposable.
            T.Check(!typeof(S31Ownership).GetMethod("ClassifyPressureReturn").GetParameters().Any(x => x.Name.IndexOf("faction", StringComparison.OrdinalIgnoreCase) >= 0), "the classification never looks at the faction");

            // The runtime path does exactly this, in this order, and stops on an unexpected return.
            string run = Code(File.ReadAllText(Path.Combine(SourceRoot, "Diagnostics/Spikes/S31/S31Spike.cs")));
            Match ap = Regex.Match(run, @"private void ApplyPressure\(\)\s*\{(?<b>.*?)\n        \}", RegexOptions.Singleline);
            T.Check(ap.Success, "ApplyPressure exists");
            string a = ap.Groups["b"].Value;
            int fenceA = a.IndexOf("Find.UniqueIDsManager.GetNextThingID()", StringComparison.Ordinal);
            int gen = a.IndexOf("PawnGenerator.GeneratePawn(req)", StringComparison.Ordinal);
            int fenceB = a.IndexOf("Find.UniqueIDsManager.GetNextThingID()", gen + 1, StringComparison.Ordinal);
            T.Check(fenceA >= 0 && gen > fenceA && fenceB > gen, "the request is fenced by two thing ids");
            T.Check(a.Contains("S31Ownership.ClassifyPressureReturn(") && a.Contains("S31Ownership.ActionFor("), "every return goes through the classifier");
            Match mark = Regex.Match(a, @"case S31PressureAction\.MarkThenDispose:(?<c>.*?)break;", RegexOptions.Singleline);
            T.Check(mark.Success && mark.Groups["c"].Value.IndexOf("S31Ownership.MarkPressurePawn(ref r.questTags)", StringComparison.Ordinal) >= 0
                && mark.Groups["c"].Value.IndexOf("S31Ownership.MarkPressurePawn(ref r.questTags)", StringComparison.Ordinal) < mark.Groups["c"].Value.IndexOf("S31World.TryDispose(r", StringComparison.Ordinal),
                "a new pawn is marked BEFORE its disposal");
            Match keep = Regex.Match(a, @"case S31PressureAction\.PreserveAndFail:(?<c>.*?)break;", RegexOptions.Singleline);
            T.Check(keep.Success && !Regex.IsMatch(keep.Groups["c"].Value, @"TryDispose|questTags|SetFaction|RemovePawn|Destroy|Discard") && keep.Groups["c"].Value.Contains("stop = true"),
                "an unexpected pawn is left untouched and the pressure stops");
            Match probe = Regex.Match(a, @"case S31PressureAction\.PreserveProbe:(?<c>.*?)break;", RegexOptions.Singleline);
            T.Check(probe.Success && !probe.Groups["c"].Value.Contains("TryDispose"), "the probe is never disposed");
            T.Check(!Regex.IsMatch(a, @"\.Faction\b|faction\s*==|==\s*faction"), "no return is judged by its faction");
        }

        // ================================================================== LeftMap evidence (review finding 2)

        private static void MissingLeftMap()
        {
            S31Snap before = Spawned(2000, 99, 0.7f, 200000);
            S31Snap atSignal = Stored(2001, 40);
            S31Snap frame = Stored(2002, 40);
            frame.step = "first frame after the exit";

            // 1. Expected and observed: no gap from this criterion.
            S31Verdict seen = new S31Verdict("t");
            S31Criteria.LeftMap(seen, "p", true, atSignal, frame);
            T.Check(seen.gaps.Count == 0 && seen.failures.Count == 0 && seen.Outcome == S31Outcome.Pass, "an observed LeftMap adds no gap");
            T.Check(seen.notes.Any(x => x.Contains("LeftMap arrived after vanilla's pass")), "and is recorded");

            // 2. Expected and missing: INCONCLUSIVE, never PASS, never FAIL.
            S31Verdict missing = new S31Verdict("t");
            S31Criteria.LeftMap(missing, "p", true, null, frame);
            T.Eq(S31Outcome.Inconclusive, missing.Outcome, "a missing expected LeftMap is INCONCLUSIVE");
            T.Eq(0, missing.failures.Count, "it does not FAIL M1 by itself");
            string g = missing.gaps.FirstOrDefault() ?? "";
            T.Check(g.Contains("NOT obtained") && g.Contains("first-frame snapshot was still captured") && g.Contains("M1 may look healthy") && g.Contains("re-run"),
                "the gap says the first frame was captured, M1 may look healthy, the exit window is unproven, and to re-run (" + g + ")");
            S31Verdict none = new S31Verdict("t");
            S31Criteria.LeftMap(none, "p", true, null, null);
            T.Eq(S31Outcome.Inconclusive, none.Outcome, "missing with no first frame either: INCONCLUSIVE");

            // 3. Map removal with a LeftMap: still an audit deviation (diagnostic), not a verdict change.
            S31Verdict dev = new S31Verdict("t");
            S31Criteria.LeftMap(dev, "p", false, atSignal, null);
            T.Check(dev.notes.Any(x => x.Contains("AUDIT DEVIATION")) && dev.Outcome == S31Outcome.Pass, "an unexpected LeftMap on map removal stays an audit deviation");
            // 4. Map removal without a LeftMap: acceptable.
            S31Verdict rm = new S31Verdict("t");
            S31Criteria.LeftMap(rm, "p", false, null, frame);
            T.Check(rm.gaps.Count == 0 && rm.Outcome == S31Outcome.Pass && rm.notes.Any(x => x.Contains("confirmed: no LeftMap")), "no LeftMap on map removal is not a gap");

            // 5. The first-frame fallback still feeds the after-pass check and the diagnostics, but cannot turn missing evidence into PASS.
            S31Verdict fallback = new S31Verdict("t");
            S31Criteria.AfterPass(fallback, "p", before, frame);
            T.Eq(S31Outcome.Pass, fallback.Outcome, "a healthy first frame passes the after-pass check on its own");
            S31Criteria.LeftMap(fallback, "p", true, null, frame);
            T.Eq(S31Outcome.Inconclusive, fallback.Outcome, "but with the synchronous LeftMap missing the scenario is INCONCLUSIVE, not PASS");
            T.Check(fallback.gaps.Any(x => x.Contains(frame.Line())), "the first-frame snapshot is quoted for diagnosis");
            S31Verdict proven = new S31Verdict("t");
            S31Criteria.AfterPass(proven, "p", before, atSignal);
            S31Criteria.LeftMap(proven, "p", true, atSignal, frame);
            T.Eq(S31Outcome.Pass, proven.Outcome, "with the synchronous observation the same pawn PASSES");

            // The runtime keeps the first frame separately and passes it; only a map removal does not expect LeftMap.
            string run = Code(File.ReadAllText(Path.Combine(SourceRoot, "Diagnostics/Spikes/S31/S31Spike.cs")));
            T.Check(run.Contains("firstFrame[id] = frame;"), "the first frame after a normal exit is kept for diagnosis");
            T.Check(run.Contains("S31Criteria.LeftMap(verdict, who, kind != Kind.MapRemoval, observer?.First(\"LeftMap\", id, startTick), frameAfter);"),
                "LeftMap is expected for every normal exit and not for the map removal");
        }

        private static void CriteriaRematerializeAndLoad()
        {
            S31Snap stored = Stored(5000, -1);
            stored.hediffSig = "Bruise@Leg";
            S31Snap spawned = Spawned(6000, 50, 0.8f, 300000);
            spawned.hediffSig = "Bruise@Leg";
            S31Verdict ok = new S31Verdict("t");
            S31Criteria.Rematerialized(ok, stored, spawned, 1, 1, true);
            T.Eq(S31Outcome.Pass, ok.Outcome, "the same pawn, reserved and unsuspended while spawned again (" + string.Join("; ", ok.failures.ToArray()) + ")");
            S31Verdict twin = new S31Verdict("t");
            S31Criteria.Rematerialized(twin, stored, spawned, 2, 2, true);
            T.Check(twin.failures.Count >= 2, "a twin FAILS");
            S31Verdict other = new S31Verdict("t");
            S31Snap different = Spawned(6000, 50, 0.8f, 300000);
            different.thingId = 8;
            S31Criteria.Rematerialized(other, stored, different, 1, 1, false);
            T.Check(other.failures.Any(x => x.Contains("not the same object")) && other.failures.Any(x => x.Contains("thing id changed")), "a different pawn FAILS");

            S31Checkpoint cp = S31Checkpoint.From(Stored(7000, -1));
            S31Verdict load = new S31Verdict("t");
            S31Criteria.AfterLoad(load, cp, Stored(9000, -1), 1, 1);
            T.Eq(S31Outcome.Pass, load.Outcome, "identity, reservation, situation and faction survive a load");
            S31Snap lost = Stored(9000, -1);
            lost.reserved = false;
            lost.situation = "Free";
            S31Verdict lv = new S31Verdict("t");
            S31Criteria.AfterLoad(lv, cp, lost, 1, 1);
            T.Check(lv.failures.Any(x => x.Contains("NOT restored")), "a reservation lost across save/load FAILS");
            S31Verdict dup = new S31Verdict("t");
            S31Criteria.AfterLoad(dup, cp, Stored(9000, -1), 2, 1);
            T.Eq(S31Outcome.Fail, dup.Outcome, "a duplicate after load FAILS");
            S31Verdict missing = new S31Verdict("t");
            S31Criteria.AfterLoad(missing, cp, null, 0, 0);
            T.Eq(S31Outcome.Fail, missing.Outcome, "a missing pawn after load FAILS");
        }

        // ================================================================== scope and isolation (static)

        private static readonly string[] CreationApis =
        {
            "PawnGenerator", "GeneratePawn", "GenSpawn", "LordMaker", "MakeNewLord", "FactionGenerator", "NewGeneratedFaction", "WorldObjectMaker",
            "GetOrGenerateMap", "DeinitAndRemoveMap", "QuestManager.Add", "HediffMaker"
        };

        private static void SourceScope()
        {
            string[] spike = Sources().Where(InSpike).ToArray();
            T.Check(spike.Length >= 4, "the S31 harness exists (" + spike.Length + " files)");
            foreach (string f in Sources())
            {
                string code = Code(File.ReadAllText(f));
                foreach (string api in CreationApis)
                {
                    if (!Regex.IsMatch(code, @"\b" + Regex.Escape(api) + @"\b")) continue;
                    T.Check(InSpike(f), "real creation API " + api + " only in Diagnostics/Spikes/S31 (found in " + Rel(f) + ")");
                }
            }
            foreach (string f in spike)
            {
                string code = Code(File.ReadAllText(f));
                T.Check(!Regex.IsMatch(code, @"\bPassToWorld\s*\("), "the harness never calls PassToWorld; vanilla performs every pass (" + Rel(f) + ")");
                T.Check(!Regex.IsMatch(code, @"\b(IPhysicalWorldPort|PhysicalLifecycleService|EpisodeRequest|PhysicalEpisode|UnavailablePhysicalWorldPort|physicalPort|Lifecycle)\b"), "the harness implements no physical port and starts no Physical Episode (" + Rel(f) + ")");
                T.Check(!Regex.IsMatch(code, @"\b(NetworkRuntime|ctx\.|DomainContext|KnownCharacter|NetworkActor|CustodyState)\b"), "the harness touches no Network state (" + Rel(f) + ")");
                T.Check(!code.Contains("Harmony"), "no patching library (" + Rel(f) + ")");
                T.Check(!Regex.IsMatch(code, @"\b(RunGC|PawnGCPass|WorldPawnGCTick)\s*\("), "no forced world-pawn GC pass (" + Rel(f) + ")");
                T.Check(!Regex.IsMatch(code, @"WorldPawnFactionDoesntMatter|worldPawnFactionDoesntMatter\s*:\s*true"), "no faction-doesn't-matter redress against unrelated world pawns (" + Rel(f) + ")");
            }
            string spikeCode = string.Join("\n", spike.Select(f => Code(File.ReadAllText(f))).ToArray());
            T.Check(spikeCode.Contains("QuestPart_ReservePawns") && spikeCode.Contains("QuestUtility.IsReservedByQuestOrQuestBeingGenerated"), "M1's fixture is the vanilla reservation part, read through the vanilla reservation query");
            T.Check(spikeCode.Contains("forceGenerateNewPawn: true"), "probes are freshly generated, never a redressed world pawn");
            T.Check(spikeCode.Contains("LordJob_ExitMapBest"), "the exit is vanilla's");
            T.Check(spikeCode.Contains("Spend(") && spikeCode.Contains("S31Session"), "destructive actions spend the session arm");
        }

        private static void SafeSuitesCannotInvoke()
        {
            foreach (string f in Sources().Where(x => Rel(x).StartsWith("Diagnostics/RuntimeTests/", StringComparison.Ordinal)))
            {
                string code = Code(File.ReadAllText(f));
                T.Check(!code.Contains("Spikes") && !code.Contains("S31"), "the Quick / Full safe runtime suites cannot reach S31 (" + Rel(f) + ")");
            }
            // Outside the spike, exactly one line names it: the per-frame pump in the world component.
            List<string> refs = new List<string>();
            foreach (string f in Sources().Where(x => !InSpike(x)))
            {
                string[] lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length; i++)
                {
                    string c = Code(lines[i]);
                    if (c.Contains("Spikes.S31") || c.Contains("S31Spike")) refs.Add(Rel(f) + ":" + (i + 1) + ": " + c.Trim());
                }
            }
            T.Eq(1, refs.Count, "one reference to S31 outside its folder (" + string.Join(" || ", refs.ToArray()) + ")");
            T.Check(refs.Count == 1 && refs[0].StartsWith("Core/NetworkWorldComponent.cs") && refs[0].EndsWith("Diagnostics.Spikes.S31.S31Spike.PumpFrame();"), "and it is the pump call");
            string devActions = File.ReadAllText(Path.Combine(SourceRoot, "Diagnostics/Spikes/S31/S31DevActions.cs"));
            T.Check(devActions.Contains("\"The Network (PHYSICAL SPIKES — S31)\""), "S31 has its own Dev Mode category");
            foreach (Match m in Regex.Matches(devActions, @"DebugAction\(Cat, ""([^""]+)"""))
            {
                string label = m.Groups[1].Value;
                bool readOnly = label.Contains("read-only") || label.Contains("Arm physical spike");
                T.Check(readOnly || label.Contains("[armed]"), "every destructive S31 action is labelled [armed] (" + label + ")");
            }
        }

        private static void ProductionFailClosed()
        {
            string runtime = File.ReadAllText(Path.Combine(SourceRoot, "Core/NetworkRuntime.cs"));
            T.Check(runtime.Contains("physicalPort = new Domain.Physical.UnavailablePhysicalWorldPort()"), "the live runtime still holds the fail-closed physical port");
            T.Eq(5, SaveMigrations.Current, "the save format stays 5 (the spike adds no Network state)");
            foreach (string f in Sources().Where(x => !Rel(x).StartsWith("Diagnostics/", StringComparison.Ordinal)))
            {
                string code = Code(File.ReadAllText(f));
                T.Check(!Regex.IsMatch(code, @"\.Lifecycle\.(Plan|Materialize)\("), "no production caller plans or materializes a Physical Episode (" + Rel(f) + ")");
            }
            string csproj = File.ReadAllText(Path.Combine(SourceRoot, "TheNetwork.csproj"));
            T.Check(!Regex.IsMatch(csproj, @"<Reference Include=""0?Harmony"), "the mod project references no patching library");
        }

        private static void TestMapDef()
        {
            string path = Path.Combine(Repo, "1.6/Defs/Spikes/TheNetwork_S31_SpikeDefs.xml");
            T.Check(File.Exists(path), "the spike-only def file exists");
            string xml = File.ReadAllText(path);
            T.Check(xml.Contains("<defName>" + S31Ids.TestMapDef + "</defName>"), "it defines " + S31Ids.TestMapDef);
            T.Check(xml.Contains("<worldObjectClass>MapParent</worldObjectClass>"), "a plain MapParent (vanilla never removes its map on its own)");
            T.Check(!xml.Contains("<comps>") && !xml.Contains("canBePlayerHome") && !xml.Contains("IncidentTargetTags") && !xml.Contains("<mapGenerator>"), "no comps, not a home, no incident tags, the default generator");
            T.Eq(1, Regex.Matches(xml, "<WorldObjectDef").Count, "the file defines nothing else");
        }

        private static void IdlePump()
        {
            // Headless there is no game: the idle pump is one static check and must not touch anything.
            S31Spike.PumpFrame();
            S31Spike.PumpFrame();
            T.Check(true, "the idle S31 pump returns without a game");
        }

        private static void RecordNotRun()
        {
            string path = Path.Combine(Repo, "docs/spikes/S31-retained-pawn-exit-reservation.md");
            T.Check(File.Exists(path), "the S31 spike record exists");
            string doc = File.ReadAllText(path);
            T.Check(doc.Contains("NOT RUN — OWNER RUNTIME VALIDATION REQUIRED"), "its verdict is NOT RUN until the owner runs it");
            T.Check(Regex.IsMatch(doc, @"Final mechanism:\s*\**\s*`?UNDECIDED"), "the final mechanism is UNDECIDED");
            string readme = File.ReadAllText(Path.Combine(Repo, "docs/spikes/README.md"));
            T.Check(readme.Contains("S31-retained-pawn-exit-reservation.md") && readme.Contains("NOT RUN"), "the spike index links it as NOT RUN");
        }
    }
}
