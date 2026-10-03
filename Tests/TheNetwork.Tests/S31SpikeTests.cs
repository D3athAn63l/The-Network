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
            string[] all = { S31Ids.ProbeTag, S31Ids.DecoyTag, S31Ids.LeaderTag, S31Ids.ManifestTag, S31Ids.PoolQuestTag, S31Ids.MapPrefix, S31Ids.FactionPrefix, S31Ids.CheckpointPrefix };
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
            S31Criteria.LeftMap(lm, "p", false, at);
            T.Check(lm.notes.Any(x => x.Contains("AUDIT DEVIATION")), "a LeftMap on map removal is reported as an audit deviation");
            S31Verdict lm2 = new S31Verdict("t");
            S31Criteria.LeftMap(lm2, "p", false, null);
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
            S31Criteria.Redress(ok, 9, 6, 6, 0, 0, 3);
            T.Eq(S31Outcome.Pass, ok.Outcome, "decoys redressed, probe never: PASS");
            S31Verdict sel = new S31Verdict("t");
            S31Criteria.Redress(sel, 9, 6, 5, 1, 0, 3);
            T.Check(sel.Outcome == S31Outcome.Fail && sel.failures.Any(x => x.Contains("CRITICAL")), "the probe returned by PawnGenerator FAILS");
            S31Verdict inFree = new S31Verdict("t");
            S31Criteria.Redress(inFree, 9, 6, 6, 0, 2, 3);
            T.Eq(S31Outcome.Fail, inFree.Outcome, "the probe in the Free set FAILS");
            S31Verdict noControl = new S31Verdict("t");
            S31Criteria.Redress(noControl, 9, 6, 0, 0, 0, 9);
            T.Check(noControl.Outcome == S31Outcome.Inconclusive && noControl.gaps.Any(x => x.Contains("positive control")), "no decoy redressed: INCONCLUSIVE (the pressure was not shown to be real)");
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
