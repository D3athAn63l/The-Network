using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TheNetwork.Diagnostics.RuntimePhysicalTests;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Settings;

namespace TheNetwork.Tests
{
    /// <summary>
    /// The Phase 3.1 FINAL cleanup / runtime sign-off pass (PR #10). The owner's corrected reduced matrix passed in real RimWorld, and the 010B reload
    /// showed the one thing the harness had wrong: RimWorld may resume time immediately after a load even when the checkpoint was saved while paused,
    /// so the saved episode can complete on the first gameplay tick before the owner can click "010V VERIFY — loaded save". This file pins the harness
    /// correction (the pure selection and terminal-verdict rules of 010V), that 010V stays read-only and persists nothing, that no paused-load claim
    /// remains, and that the status and evidence documents say what the owner proved, keep the history, and claim no more.
    ///
    /// Production lifecycle code is frozen in this pass: nothing here tests a production change.
    /// </summary>
    public static class Phase31FinalTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Phys31Fin.ScenarioOfReadsTheExistingPersistedCauseOnly", ScenarioOf));
            t.Add(new KeyValuePair<string, Action>("Phys31Fin.Select_AnIncompleteMatchingEpisodeUsesTheExistingBranch", SelectIncomplete));
            t.Add(new KeyValuePair<string, Action>("Phys31Fin.Select_ACompletedMatchingEpisodeUsesTheTerminalBranch", SelectTerminal));
            t.Add(new KeyValuePair<string, Action>("Phys31Fin.Select_HistoricalEpisodesAreResolvedDeterministicallyOrReportedInconclusive", SelectHistorical));
            t.Add(new KeyValuePair<string, Action>("Phys31Fin.Terminal_TheExpectedPersistedResultPasses", TerminalPasses));
            t.Add(new KeyValuePair<string, Action>("Phys31Fin.Terminal_LostKilledAndCapturedAreRejected", TerminalRejectsOutcomes));
            t.Add(new KeyValuePair<string, Action>("Phys31Fin.Terminal_WrongBindingActiveLinkIncompleteMarkersAndReservationFailureAreRejected", TerminalRejectsFacts));
            t.Add(new KeyValuePair<string, Action>("Phys31Fin.Scan_010VIsReadOnlyPersistsNothingAndNeedsNoProductionChange", ScanReadOnly));
            t.Add(new KeyValuePair<string, Action>("Phys31Fin.Scan_NoFalsePausedLoadClaimRemainsAnywhere", ScanNoPausedLoadClaim));
            t.Add(new KeyValuePair<string, Action>("Phys31Fin.Docs_Phase31IsRecordedValidatedTheHistoryIsKeptAndNothingMoreIsClaimed", DocsFinal));
        }

        // ================================================================== helpers

        private const string Family = "RT-PHYX-010";

        private static NetworkActor Solo(TestNet n, string id) { return PhysicalLifecycleTests.Make(n, ContractorForm.Solo, id); }

        private static string Code(string rel) { return PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src(rel)); }

        private static string Body(string code, string from, string to)
        {
            int a = code.IndexOf(from, StringComparison.Ordinal);
            T.Check(a >= 0, "found " + from);
            if (a < 0) return "";
            int b = code.IndexOf(to, a + from.Length, StringComparison.Ordinal);
            return b < 0 ? code.Substring(a) : code.Substring(a, b - a);
        }

        private static string Doc(string rel)
        {
            string root = Environment.GetEnvironmentVariable("THENETWORK_REPO") ?? ".";
            return File.ReadAllText(Path.Combine(root, rel));
        }

        /// <summary>A plain episode (no lifecycle) with just what the selection rule reads.</summary>
        private static PhysicalEpisode Plain(int id, int createdTick, string devKey, bool complete)
        {
            PhysicalEpisode e = new PhysicalEpisode { id = new EpisodeId(id), createdTick = createdTick, cause = new EpisodeCause { devKey = devKey } };
            if (complete)
            {
                e.state = EpisodeState.Closed;
                e.consequencesApplied = true;
                e.releaseApplied = true;
                e.followUpApplied = true;
                e.publishedTick = createdTick + 100;
            }
            else
            {
                e.state = EpisodeState.Open;
            }
            return e;
        }

        private sealed class Fixture
        {
            public TestNet n;
            public NetworkActor a;
            public KnownCharacter c;
            public PhysicalEpisode e;
        }

        /// <summary>A REAL episode through the production lifecycle over the fake port, stamped as the suite stamps its own (the existing cause).</summary>
        private static Fixture Real(int seed, string devKey)
        {
            Fixture f = new Fixture { n = new TestNet(seed) };
            f.a = Solo(f.n, "fin" + seed);
            f.c = PhysicalLifecycleTests.Self(f.n, f.a);
            f.e = PhysicalLifecycleTests.Begin(f.n, f.a, new[] { f.c });
            f.e.cause.devKey = devKey;
            return f;
        }

        private static string Key(string run, string scenario) { return PhysicalTestIds.DevKey(run, scenario); }

        /// <summary>The facts a healthy world gives after a normal return: a reserved world pawn, once, no integrity finding.</summary>
        private static Phyx010WorldFacts Healthy(KnownCharacter c)
        {
            return new Phyx010WorldFacts { pawnResolved = true, pawnThingId = c.pawn.thingIdNumber, pawnsWithThatThingId = 1, worldPawn = true, reservedByQuest = true, registryReserves = true, integrityFindings = 0 };
        }

        private static List<string> Failed(PhysicalEpisode e, KnownCharacter c, Phyx010WorldFacts w)
        {
            List<string> r = new List<string>();
            foreach (Phyx010Clause cl in Phyx010Terminal.Failures(Phyx010Terminal.Judge(e, c, w))) r.Add(cl.text);
            return r;
        }

        private static bool Mentions(List<string> failures, string phrase)
        {
            foreach (string f in failures) if (f.Contains(phrase)) return true;
            return false;
        }

        // ================================================================== provenance

        private static void ScenarioOf()
        {
            T.Eq(Family, PhysicalTestIds.ScenarioOf(Key("r7", Family)), "the scenario id is read from the existing dev key");
            T.Eq("RT-PHYX-001", PhysicalTestIds.ScenarioOf(Key("r12", "RT-PHYX-001")), "any suite scenario");
            T.Check(PhysicalTestIds.ScenarioOf(null) == null && PhysicalTestIds.ScenarioOf("RT-PHYS") == null && PhysicalTestIds.ScenarioOf("") == null, "a key that is not the suite's has no scenario");
            T.Check(PhysicalTestIds.ScenarioOf(PhysicalTestIds.DevKeyPrefix) == null, "a bare prefix has none");
            T.Check(!Phyx010Selection.IsFamily(Plain(1, 1, "RT-PHYS", true)) && !Phyx010Selection.IsFamily(null) && Phyx010Selection.IsFamily(Plain(2, 1, Key("r1", Family), true)), "family membership is exactly the persisted cause");
        }

        // ================================================================== selection (the branch decision)

        private static void SelectIncomplete()
        {
            PhysicalEpisode open = Plain(10, 500, Key("r3", Family), false);
            Phyx010Selection s = Phyx010Selection.Select(new List<PhysicalEpisode> { Plain(5, 100, Key("r1", Family), true), open });
            T.Eq(Phyx010Branch.Incomplete, s.branch, "a matching episode still incomplete selects Branch A (the existing verify-and-follow)");
            T.Check(s.incomplete.Count == 1 && ReferenceEquals(s.incomplete[0], open), "and names it");
            T.Check(s.relevant == null, "no terminal episode is chosen while a matching one is incomplete (an old completed 010 is never accepted instead)");
            // A quarantined matching episode is incomplete too.
            PhysicalEpisode q = Plain(11, 600, Key("r4", Family), false);
            q.state = EpisodeState.Quarantined;
            T.Eq(Phyx010Branch.Incomplete, Phyx010Selection.Select(new List<PhysicalEpisode> { q }).branch, "a quarantined matching episode is not terminal");
            // An incomplete episode of ANOTHER scenario does not make this Branch A.
            Phyx010Selection other = Phyx010Selection.Select(new List<PhysicalEpisode> { Plain(12, 700, Key("r5", "RT-PHYX-001"), false), Plain(13, 800, Key("r5", Family), true) });
            T.Eq(Phyx010Branch.Terminal, other.branch, "an unrelated incomplete test episode is not a matching one");
        }

        private static void SelectTerminal()
        {
            PhysicalEpisode done = Plain(20, 900, Key("r6", Family), true);
            Phyx010Selection s = Phyx010Selection.Select(new List<PhysicalEpisode> { done });
            T.Eq(Phyx010Branch.Terminal, s.branch, "a completed matching episode, none incomplete, selects the terminal branch");
            T.Check(ReferenceEquals(s.relevant, done) && s.incomplete.Count == 0, "and it is that episode");
            T.Check(s.diagnostic.Contains("relevant"), "with a plain explanation: " + s.diagnostic);
            T.Eq(Phyx010Branch.None, Phyx010Selection.Select(new List<PhysicalEpisode>()).branch, "no episode at all: nothing relevant (inconclusive, never a pass)");
            T.Eq(Phyx010Branch.None, Phyx010Selection.Select(null).branch, "null: nothing relevant");
            T.Eq(Phyx010Branch.None, Phyx010Selection.Select(new List<PhysicalEpisode> { Plain(21, 1, Key("r6", "RT-PHYX-002"), true), Plain(22, 2, "RT-PHYS", true) }).branch,
                "completed episodes of OTHER scenarios, or without the suite's cause, are never candidates");
        }

        private static void SelectHistorical()
        {
            PhysicalEpisode a = Plain(30, 1000, Key("r1", Family), true), b = Plain(31, 5000, Key("r2", Family), true), c = Plain(32, 3000, Key("r3", Family), true);
            Phyx010Selection s = Phyx010Selection.Select(new List<PhysicalEpisode> { a, b, c });
            T.Check(s.branch == Phyx010Branch.Terminal && ReferenceEquals(s.relevant, b), "of several completed RT-PHYX-010 episodes the LATEST (by creation tick) is the relevant one");
            Phyx010Selection reversed = Phyx010Selection.Select(new List<PhysicalEpisode> { c, b, a });
            T.Check(reversed.branch == Phyx010Branch.Terminal && ReferenceEquals(reversed.relevant, b), "independent of the order the store lists them: deterministic");
            // The newest one is incomplete: the old completed ones are history, not a substitute.
            PhysicalEpisode open = Plain(33, 9000, Key("r4", Family), false);
            T.Eq(Phyx010Branch.Incomplete, Phyx010Selection.Select(new List<PhysicalEpisode> { a, b, c, open }).branch, "a newer incomplete matching episode wins: older completed ones are never accepted in its place");
            // Two completed candidates created at the same tick cannot be told apart by the existing provenance: inconclusive, not a guess.
            PhysicalEpisode t1 = Plain(34, 7000, Key("r5", Family), true), t2 = Plain(35, 7000, Key("r6", Family), true);
            Phyx010Selection amb = Phyx010Selection.Select(new List<PhysicalEpisode> { a, t1, t2 });
            T.Check(amb.branch == Phyx010Branch.Ambiguous && amb.relevant == null, "two candidates with the same creation tick are Ambiguous and none is chosen");
            T.Check(amb.diagnostic.Contains("nothing is guessed") && amb.diagnostic.Contains("7000"), "with a useful diagnostic: " + amb.diagnostic);
            // An older tie is irrelevant when the latest is unique.
            PhysicalEpisode o1 = Plain(36, 100, Key("r7", Family), true), o2 = Plain(37, 100, Key("r8", Family), true), newest = Plain(38, 200, Key("r9", Family), true);
            T.Check(Phyx010Selection.Select(new List<PhysicalEpisode> { o1, o2, newest }).branch == Phyx010Branch.Terminal, "a tie among OLD episodes does not make the latest ambiguous");
        }

        // ================================================================== the terminal verdict

        private static void TerminalPasses()
        {
            Fixture f = Real(9801, Key("r1", Family));
            f.n.physical.ExitNormally(f.c.pawn, 80);
            PhysicalLifecycleTests.L(f.n).Reconcile(f.e, "exit");
            T.Check(f.e.IsComplete && f.e.members[0].outcome == MemberOutcome.Returned && f.e.members[0].observed == ObservedKind.WorldFree, "the fixture is a real Returned episode from the production lifecycle");
            List<Phyx010Clause> clauses = Phyx010Terminal.Judge(f.e, f.c, Healthy(f.c));
            T.Check(clauses.Count >= 20, "the verdict judges every clause one by one (" + clauses.Count + ")");
            T.Eq(0, Phyx010Terminal.Failures(clauses).Count, "the expected persisted result passes: Returned, WorldFree, the same binding, Stored, no episode link, abstract authority, a reserved world pawn, complete markers");
            foreach (Phyx010Clause cl in clauses) T.Check(cl.ok, "terminal clause: " + cl.text);
            T.Eq(CustodyState.Stored, f.c.custody, "(Stored)");
            T.Check(!f.c.episode.IsValid && AuthorityGate.CanSimulateAbstractly(f.c), "(no active link, abstract authority)");
        }

        private static void TerminalRejectsOutcomes()
        {
            // Lost and Killed through the REAL lifecycle.
            Fixture lost = Real(9802, Key("r1", Family));
            lost.n.physical.Vanish(lost.c.pawn);
            PhysicalLifecycleTests.L(lost.n).Reconcile(lost.e, "gone");
            List<string> fl = Failed(lost.e, lost.c, Healthy(lost.c));
            T.Check(lost.e.members[0].outcome == MemberOutcome.Lost, "the fixture is a real Lost outcome");
            T.Check(fl.Count > 0 && Mentions(fl, "outcome is Returned") && Mentions(fl, "Lost"), "Lost is rejected: " + string.Join(" | ", fl.ToArray()));
            Fixture killed = Real(9803, Key("r1", Family));
            killed.n.physical.Die(killed.c.pawn);
            PhysicalLifecycleTests.L(killed.n).Reconcile(killed.e, "dead");
            List<string> fk = Failed(killed.e, killed.c, Healthy(killed.c));
            T.Check(killed.e.members[0].outcome == MemberOutcome.Killed, "the fixture is a real Killed outcome");
            T.Check(fk.Count > 0 && Mentions(fk, "outcome is Returned") && Mentions(fk, "alive"), "Killed is rejected (and the person is not alive): " + string.Join(" | ", fk.ToArray()));
            // Every other non-Returned outcome, including Captured.
            foreach (MemberOutcome o in Enum.GetValues(typeof(MemberOutcome)))
            {
                if (o == MemberOutcome.Returned) continue;
                Fixture g = Real(9810 + (int)o, Key("r1", Family));
                g.n.physical.ExitNormally(g.c.pawn, 80);
                PhysicalLifecycleTests.L(g.n).Reconcile(g.e, "exit");
                g.e.members[0].outcome = o;
                T.Check(Mentions(Failed(g.e, g.c, Healthy(g.c)), "outcome is Returned"), "the outcome " + o + " is rejected");
            }
            // "Captured" in this model: held by the player or another owner, kidnapped, or joined to the player. All rejected.
            foreach (MemberOutcome captured in new[] { MemberOutcome.HeldByPlayer, MemberOutcome.JoinedPlayer, MemberOutcome.Kidnapped, MemberOutcome.HeldByOther })
            {
                Fixture cap = Real(9840 + (int)captured, Key("r1", Family));
                cap.n.physical.ExitNormally(cap.c.pawn, 80);
                PhysicalLifecycleTests.L(cap.n).Reconcile(cap.e, "exit");
                cap.e.members[0].outcome = captured;
                cap.e.members[0].observed = ObservedKind.HeldByPlayer;
                List<string> fc = Failed(cap.e, cap.c, Healthy(cap.c));
                T.Check(Mentions(fc, "outcome is Returned") && Mentions(fc, "WorldFree"), "a captured member (" + captured + ") is rejected: its outcome is not Returned and it was not observed WorldFree");
            }
        }

        private static void TerminalRejectsFacts()
        {
            Func<int, Fixture> good = seed =>
            {
                Fixture f = Real(seed, Key("r1", Family));
                f.n.physical.ExitNormally(f.c.pawn, 80);
                PhysicalLifecycleTests.L(f.n).Reconcile(f.e, "exit");
                T.Eq(0, Failed(f.e, f.c, Healthy(f.c)).Count, "(control " + seed + ": the unmodified fixture passes)");
                return f;
            };
            Fixture w = good(9820);
            w.e.members[0].pawn = new PawnRef { thingIdNumber = w.c.pawn.thingIdNumber + 1, defName = "Human" };
            T.Check(Mentions(Failed(w.e, w.c, Healthy(w.c)), "SAME Pawn"), "a member binding that is not the person's own binding (a wrong Pawn) is rejected");
            Fixture w2 = good(9821);
            w2.c.pawn = new PawnRef { thingIdNumber = w2.c.pawn.thingIdNumber + 7, defName = "Human" };
            T.Check(Failed(w2.e, w2.c, Healthy(w2.c)).Count > 0, "a person re-bound to a different Pawn (a replacement) is rejected");
            Fixture w3 = good(9822);
            Phyx010WorldFacts wrongPawn = Healthy(w3.c);
            wrongPawn.pawnThingId = w3.c.pawn.thingIdNumber + 3;
            T.Check(Mentions(Failed(w3.e, w3.c, wrongPawn), "thing id is the persisted one"), "a binding that resolves to a Pawn with another thing id is rejected");
            Phyx010WorldFacts unresolved = Healthy(w3.c);
            unresolved.pawnResolved = false;
            T.Check(Failed(w3.e, w3.c, unresolved).Count > 0, "an unresolved binding is rejected");
            Phyx010WorldFacts twin = Healthy(w3.c);
            twin.pawnsWithThatThingId = 2;
            T.Check(Mentions(Failed(w3.e, w3.c, twin), "no duplicated Thing ID"), "a duplicated Thing ID is rejected");

            Fixture link = good(9823);
            link.c.episode = link.e.id;
            T.Check(Mentions(Failed(link.e, link.c, Healthy(link.c)), "no active episode link"), "an active episode link is rejected");
            Fixture dep = good(9824);
            dep.c.custody = CustodyState.Deployed;
            T.Check(Mentions(Failed(dep.e, dep.c, Healthy(dep.c)), "custody is Stored"), "a person who is not Stored is rejected");

            Fixture rel = good(9825);
            rel.e.releaseApplied = false;
            T.Check(Mentions(Failed(rel.e, rel.c, Healthy(rel.c)), "RELEASE is complete") && Mentions(Failed(rel.e, rel.c, Healthy(rel.c)), "COMPLETE"), "incomplete RELEASE markers are rejected");
            Fixture fol = good(9826);
            fol.e.followUpApplied = false;
            T.Check(Mentions(Failed(fol.e, fol.c, Healthy(fol.c)), "FOLLOW-UP is complete"), "an incomplete FOLLOW-UP is rejected");
            Fixture pub = good(9827);
            pub.e.publishedTick = -1;
            T.Check(Mentions(Failed(pub.e, pub.c, Healthy(pub.c)), "PUBLISH is complete"), "an incomplete PUBLISH is rejected");
            Fixture st = good(9828);
            st.e.state = EpisodeState.Quarantined;
            st.e.quarantineKey = "UnsupportedCustody:HeldByPlayer";
            T.Check(Mentions(Failed(st.e, st.c, Healthy(st.c)), "not quarantined"), "a quarantined episode is rejected");

            Fixture rb = good(9829);
            rb.e.members[0].observed = ObservedKind.ReservationBroken;
            T.Check(Mentions(Failed(rb.e, rb.c, Healthy(rb.c)), "ReservationBroken") && Mentions(Failed(rb.e, rb.c, Healthy(rb.c)), "WorldFree"), "a ReservationBroken observation is rejected");
            Fixture reg = good(9830);
            Phyx010WorldFacts gap = Healthy(reg.c);
            gap.registryReserves = false;
            gap.reservedByQuest = false;
            List<string> fg = Failed(reg.e, reg.c, gap);
            T.Check(Mentions(fg, "registry covers") && Mentions(fg, "ReservedByQuest"), "a reservation failure is rejected (both the registry and vanilla's view)");
            Phyx010WorldFacts integ = Healthy(reg.c);
            integ.integrityFindings = 1;
            T.Check(Mentions(Failed(reg.e, reg.c, integ), "integrity finding"), "an integrity finding is rejected");
            Phyx010WorldFacts notWorld = Healthy(reg.c);
            notWorld.worldPawn = false;
            T.Check(Mentions(Failed(reg.e, reg.c, notWorld), "world pawn"), "a Pawn that is not a world pawn is rejected");

            Fixture notFamily = good(9831);
            notFamily.e.cause.devKey = Key("r1", "RT-PHYX-001");
            T.Check(Mentions(Failed(notFamily.e, notFamily.c, Healthy(notFamily.c)), "RT-PHYX-010 episode"), "an episode of another scenario is rejected as the wrong evidence");
            T.Check(Failed(null, null, null).Count > 0 && Failed(good(9832).e, null, Healthy(good(9833).c)).Count > 0, "no episode, or no person, is never a pass");
        }

        // ================================================================== scans

        private static void ScanReadOnly()
        {
            string scenarios = Code("Diagnostics/RuntimePhysicalTests/PhysicalScenarios.cs");
            string verifier = Body(scenarios, "public sealed class Phyx010VerifyAfterLoad", "public sealed class Phyx011RoleGeneration");
            T.Check(verifier.Length > 1500, "found the verifier");
            string rules = Code("Diagnostics/RuntimePhysicalTests/Phyx010Rules.cs");
            T.Check(rules.Length > 1500, "found the pure rules");
            string[] forbidden = { "lc.Plan(", "Materialize", "lc.Reconcile(", "lc.WakeAll", ".Wake(", "FinishPending", "SettleForRemoval", "EnsureQuest", "Registry.Rebuild", "RebuildEarly", "ResolvePointers", "Registry.Release", "Resume(", "PassToWorld", ".Destroy(", ".Discard(", "GenSpawn", "Spawn(", "Create(", "CatchUp", "GeneratePawn", "PawnGenerator", "CapturedBy", "RemoveMap", "Commit(", "SetFaction", "AddHediff", "RemoveHediff", "PhysicalTags.Add", "PhysicalTags.Remove", "Registry.Note(" };
            foreach (string f in forbidden)
            {
                T.Check(!verifier.Contains(f), "010V never calls " + f + " (read-only: it generates, spawns, destroys, discards and reconciles nothing)");
                T.Check(!rules.Contains(f), "the pure rules never call " + f);
            }
            T.Check(!Regex.IsMatch(rules, @"\b(ctx|port|lc|rt)\b\s*\.") && !rules.Contains("Find.") && !rules.Contains("Verse"), "the pure rules reach no context, port, lifecycle or RimWorld object: plain data in, a verdict out");
            // The branch uses exactly the pure rules, the existing provenance and the required wording.
            T.Check(verifier.Contains("Phyx010Selection.Select(ctx.episodes.episodes)") && verifier.Contains("Phyx010Terminal.Judge(ep, c, w)"), "010V decides with the pure selection and terminal rules");
            T.Check(PhysicalLifecycleTests.Src("Diagnostics/RuntimePhysicalTests/PhysicalScenarios.cs").Contains("The RT-PHYX-010 episode completed before the owner could run 010V; validating the persisted terminal result instead."), "and logs the required sentence");
            T.Check(verifier.Contains("case Phyx010Branch.Incomplete:") && verifier.Contains("case Phyx010Branch.Terminal:") && verifier.Contains("v.Gap(sel.diagnostic"), "Branch A keeps the existing behaviour, Branch B validates, and an unidentifiable episode is an inconclusive gap, never a guess");
            T.Check(verifier.Contains("for (int i = 0; i < open.Count; i++) if (PhysicalTestIds.IsTestDevKey(open[i].cause?.devKey) && open[i].state != EpisodeState.Quarantined) return StepResult.Wait;"), "the follow-to-completion step of Branch A is unchanged");
            // Nothing new is persisted: no Scribe, no IExposable, no persisted marker, in the new file or the tier's id helper.
            foreach (string rel in new[] { "Diagnostics/RuntimePhysicalTests/Phyx010Rules.cs", "Diagnostics/RuntimePhysicalTests/PhysicalTestModel.cs" })
            {
                string code = Code(rel);
                T.Check(!Regex.IsMatch(code, @"Scribe_\w+\.Look") && !code.Contains("IExposable") && !code.Contains("ExposeData"), rel + " persists nothing");
            }
            string ids = Body(Code("Diagnostics/RuntimePhysicalTests/PhysicalTestModel.cs"), "public static class PhysicalTestIds", "public enum SoloNeed");
            T.Check(!Regex.IsMatch(ids, @"public static (readonly )?[\w<>\[\]]+ \w+\s*(=[^=>]|;)") && !ids.Contains("{ get"), "PhysicalTestIds gained no static field or property (the new member is the method ScenarioOf)");
            T.Check(ids.Contains("public static string ScenarioOf(string devKey)"), "ScenarioOf is a plain method over the existing cause");
            T.Check(!Regex.IsMatch(Code("Diagnostics/RuntimePhysicalTests/PhysicalTestRunner.cs"), @"bool\s+\w*[dD]isposable\w*\s*[;=]"), "no 'disposable save' flag exists");
            T.Eq(5, SaveMigrations.Current, "save format 5");
            // The production lifecycle is not touched by the harness branch: 010V is the only scenario that reads episodes this way, and the lifecycle never names it.
            foreach (string rel in new[] { "Domain/Physical/PhysicalLifecycleService.cs", "Domain/Physical/ReconciliationApplier.cs", "Domain/Physical/AuthorityGate.cs", "Integration/Physical/RetainedPawnRegistry.cs", "Integration/Physical/PawnObserver.cs", "Domain/Physical/BindingRules.cs" })
                T.Check(!Code(rel).Contains("Phyx010") && !Code(rel).Contains("ScenarioOf"), rel + " knows nothing of the 010V harness");
        }

        private static void ScanNoPausedLoadClaim()
        {
            string[] forbidden =
            {
                @"(?i)\bloads?\s+paused\b", @"(?i)\bloaded\s+paused\b", @"(?i)before\s+unpausing", @"(?i)\bno\s+tick\s+runs\b", @"(?i)paused\s+right\s+after\s+the\s+load",
                @"(?i)a\s+save\s+made\s+while\s+paused", @"(?i)immediately\s+run\s+(`)?010V", @"(?i)run\s+it\s+again\s+once\s+the\s+episode\s+completed"
            };
            string root = Environment.GetEnvironmentVariable("THENETWORK_REPO") ?? ".";
            List<string> files = new List<string>(PhysicalLifecycleTests.AllSources());
            foreach (string f in Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)) files.Add(f);
            files.Add(Path.Combine(root, "README.md"));
            foreach (string f in files)
            {
                string text = File.ReadAllText(f);
                foreach (string rx in forbidden) T.Check(!Regex.IsMatch(text, rx), Path.GetFileName(f) + " makes no paused-load claim (" + rx + ")");
            }
            string testing = Doc("docs/RUNTIME_TESTING.md");
            T.Check(testing.Contains("RimWorld may resume time immediately after a load, even when the checkpoint was saved while paused"), "RUNTIME_TESTING states the truth: RimWorld may resume time immediately after a load");
            T.Check(testing.Contains("registry and load safety must already be correct before the first tick") || testing.Contains("registry/load safety must already be correct before the first tick"), "and the production requirement");
            T.Check(!testing.Contains("preserving the pause is required") && !Regex.IsMatch(testing, @"(?i)pause\s+state\s+is\s+required"), "it never tells the owner that preserving the pause is required for correctness");
            string scenarios = PhysicalLifecycleTests.Src("Diagnostics/RuntimePhysicalTests/PhysicalScenarios.cs");
            T.Check(scenarios.Contains("RimWorld may resume time right after the load even though this checkpoint is saved while paused"), "the in-run instruction tells the same truth");
        }

        // ================================================================== documentation

        private static void DocsFinal()
        {
            string readme = Doc("README.md"), phases = Doc("docs/IMPLEMENTATION_PHASES.md"), lifecycle = Doc("docs/PHYSICAL_LIFECYCLE.md"), testing = Doc("docs/RUNTIME_TESTING.md"), risks = Doc("docs/RISKS.md");
            string decisions = Doc("docs/DECISIONS.md"), spikes = Doc("docs/spikes/README.md"), s31 = Doc("docs/spikes/S31-retained-pawn-exit-reservation.md");
            // The final status, everywhere that states it.
            foreach (KeyValuePair<string, string> kv in new[] { new KeyValuePair<string, string>("README", readme), new KeyValuePair<string, string>("IMPLEMENTATION_PHASES", phases), new KeyValuePair<string, string>("PHYSICAL_LIFECYCLE", lifecycle),
                new KeyValuePair<string, string>("RUNTIME_TESTING", testing), new KeyValuePair<string, string>("RISKS", risks), new KeyValuePair<string, string>("DECISIONS", decisions), new KeyValuePair<string, string>("spikes README", spikes) })
            {
                T.Check(Regex.IsMatch(kv.Value, @"(?i)owner runtime validated"), kv.Key + " states the Phase 3.1 status: owner runtime validated");
                T.Check(!Regex.IsMatch(kv.Value, @"(?i)(owner )?physical validation in progress") && !Regex.IsMatch(kv.Value, @"(?i)awaits? (its|the|a) reduced retest") && !Regex.IsMatch(kv.Value, @"(?i)awaiting its reduced retest")
                    && !Regex.IsMatch(kv.Value, @"(?i)not a phase 3\.1 pass|\(not a pass[:)]") && !Regex.IsMatch(kv.Value, @"(?i)not runtime-validated") && !Regex.IsMatch(kv.Value, @"(?i)runtime validation (of the 3\.1 suite )?(is )?still required"),
                    kv.Key + " has no stale 'in progress', 'awaits retest', 'not a PASS' or 'not runtime-validated' status");
            }
            T.Check(lifecycle.Contains("**Phase 3.1 Controlled Physical Episode — IMPLEMENTED AND OWNER RUNTIME VALIDATED (PASS).**"), "PHYSICAL_LIFECYCLE's banner states the final status in the agreed words");
            T.Check(Regex.IsMatch(lifecycle + testing + phases, @"Phase 3\.1 scope only|this status applies to the Phase 3\.1 scope only|applies to the Phase 3\.1 scope"), "the status is limited to the Phase 3.1 scope");
            // The history is kept, in order, and the first run is not rewritten as a pass.
            string appendix = lifecycle.Substring(lifecycle.IndexOf("## Appendix K:", StringComparison.Ordinal));
            int s1 = appendix.IndexOf("first owner run found real defects", StringComparison.Ordinal);
            int s2 = s1 < 0 ? -1 : appendix.IndexOf("defects corrected", s1 + 1, StringComparison.Ordinal);
            int s3 = s2 < 0 ? -1 : appendix.IndexOf("reduced owner rerun passed", s2 + 1, StringComparison.Ordinal);
            int s4 = s3 < 0 ? -1 : appendix.IndexOf("010B exposed the false paused-load harness assumption", s3 + 1, StringComparison.Ordinal);
            int s5 = s4 < 0 ? -1 : appendix.IndexOf("isolated evidence confirmed production load behavior", s4 + 1, StringComparison.Ordinal);
            int s6 = s5 < 0 ? -1 : appendix.IndexOf("harness and docs cleaned up", s5 + 1, StringComparison.Ordinal);
            T.Check(s1 >= 0 && s2 > s1 && s3 > s2 && s4 > s3 && s5 > s4 && s6 > s5, "Appendix K preserves the six-step sequence in order (first run found defects, corrected, reduced rerun passed, 010B exposed the paused-load assumption, isolated evidence confirmed production load, cleanup)");
            T.Check(appendix.Contains("six issues") && appendix.Contains("`29f31dd`"), "the first run's findings and build are still recorded");
            // The evidence block.
            T.Check(testing.Contains("### 17.5 Final Phase 3.1 owner runtime evidence"), "the final evidence block exists");
            string ev = testing.Substring(testing.IndexOf("### 17.5 Final Phase 3.1 owner runtime evidence", StringComparison.Ordinal));
            foreach (string id in new[] { "001", "002", "003", "004", "005", "006", "007", "008", "009", "010A", "010B", "011", "012", "015", "016" }) T.Check(Regex.IsMatch(ev, @"\| `?" + id + @"`? \|"), "the evidence block lists " + id);
            foreach (string src in new[] { "S31 owner spike", "initial Phase 3.1 run", "corrected reduced rerun", "isolated 010B follow-up" }) T.Check(ev.Contains(src), "it names the evidence source: " + src);
            T.Check(ev.Contains("not one run") || ev.Contains("not from one run") || ev.Contains("did not all come from one run"), "and says the evidence did not all come from one run");
            foreach (string runtime in new[] { "ContractorProfile", "ContractorSimulation", "not Fixers", "ReservedByQuest", "no replacement Pawn", "same dead Pawn", "Quarantined(UnsupportedCustody", "WorldFree", "first gameplay tick" }) T.Check(ev.Contains(runtime), "the evidence block records: " + runtime);
            // What is NOT claimed.
            foreach (string notClaimed in new[] { "Phase 3.2", "group", "custody", "modded races", "every RimWorld/mod combination" }) T.Check(ev.Contains(notClaimed), "the block states what is not claimed: " + notClaimed);
            // The S31 record: the ORIGINAL spike record from PR #9 (branch claude/spike-s31-retained-pawn-exit, head e2e126e), carried unchanged except for the status, links, results and the two added sections.
            T.Check(s31.Contains("**Verdict: PASS — OWNER RUNTIME VALIDATED**") && s31.Contains("**Final mechanism:** `M1 ACCEPTED`"), "the S31 record carries the accepted verdict and mechanism");
            T.Check(!s31.Contains("NOT RUN — OWNER RUNTIME VALIDATION REQUIRED") && !s31.Contains("`UNDECIDED` *(allowed") && !s31.Contains("an-open-mandatory-spike-s31") && !s31.Contains("**Phase 3.1 stays blocked**"), "and none of the original NOT RUN / UNDECIDED / blocked wording or the stale link survives");
            foreach (string part in new[] { "# S31 — Retained pawn exit reservation", "## 1. The question", "P3-INV-032", "## 2. Build and environment", "## 3. What the 1.6 source says", "## 4. The harness", "## 5. Scenarios and pass criteria", "## 6. Owner run checklist", "## 7. Results", "**M1 decision rule.**", "## 8. Uncertainties only the owner's run can settle", "## 9. The invariant it establishes", "## 10. Later confirmation on the production implementation" })
                T.Check(s31.Contains(part), "the S31 record keeps its original structure: " + part);
            foreach (string scenario in new[] { "| **A** | **Normal vanilla exit**", "| **B** | **Map removal**", "| **C** | **Injured exit**", "| **D** | **Save/load window**", "| **E** | **Four probes leave together**", "| **F** | **Populated pool / redress pressure**", "| **G** | **Rematerialize the same pawn**" })
                T.Check(s31.Contains(scenario), "the original scenario table is intact: " + scenario);
            foreach (string cand in new[] { "**M1** — reserve while still spawned", "**M2** — reserve synchronously at a vanilla-supported callback", "**M3** — the narrow Harmony contingency C-4" }) T.Check(s31.Contains(cand), "the candidate mechanisms are recorded: " + cand);
            T.Check(s31.Contains("PR #9") && s31.Contains("e2e126e0fe7b5783da6d00e150cecfa5ca867dba") && s31.Contains("deliberately **never merged**"), "it states where the original lives and that it was deliberately never merged");
            T.Check(s31.Contains("none of it is in this repository") && !Regex.IsMatch(s31, @"(?m)^\s*(using |namespace |public (sealed )?(static )?class )"), "the spike's harness code is not carried");
            T.Check(s31.Contains("is not the Phase 3.1 suite") && s31.Contains("17.5"), "S31 is kept distinct from the Phase 3.1 suite and points at its evidence block");
            T.Check(!s31.Contains("No dedicated record file for S31 existed"), "and the record no longer calls itself a reconstruction");
            int res = s31.IndexOf("## 7. Results", StringComparison.Ordinal), rule = s31.IndexOf("**M1 decision rule.**", StringComparison.Ordinal);
            string results = res >= 0 && rule > res ? s31.Substring(res, rule - res) : "";
            T.Check(results.Contains("ADR-053") && !results.Contains("NOT RUN") && Regex.Matches(results, @"\*\*PASS\*\*").Count >= 8, "§ 7 holds the owner's accepted result, all seven scenarios PASS, nothing marked NOT RUN");
            T.Check(results.Contains("not recorded in this repository"), "and says plainly what the repository does not hold (no invented date, build or log)");
            foreach (string accepted in new[] { "no Free-world-pawn window", "no faction rewrite", "no Network double `PassToWorld`", "covered by the reservation while spawned", "same-pawn rematerialization", "no `LeftMap`" })
                T.Check(results.Contains(accepted), "§ 7 records the accepted observation: " + accepted);
            string later = s31.Substring(s31.IndexOf("## 10. Later confirmation", StringComparison.Ordinal));
            foreach (string l in new[] { "RT-PHYX-015", "RT-PHYX-016", "RT-PHYX-007", "RT-PHYX-010A", "two-stage load", "does not reopen S31" }) T.Check(later.Contains(l), "the later production evidence is kept apart and records: " + l);
            T.Check(spikes.Contains("(S31-retained-pawn-exit-reservation.md)"), "the spikes README links the S31 record");
            T.Check(!spikes.Contains("it has no file in this folder"), "and no longer says S31 has no file");
            // The owner workflow.
            T.Check(testing.Contains("### 17.3 RT-PHYX-010: the save and load workflow (owner steps)") && testing.Contains("— **NO ARM**"), "the 010 workflow is documented, no arm");
            T.Check(testing.Contains("validates the persisted terminal result instead") || testing.Contains("validating the persisted terminal result instead"), "and explains that a completed episode is validated by its persisted terminal result");
            T.Check(testing.Contains("Stopping `010V` stops only the read-only QA runner. It does NOT cancel a real production `PhysicalEpisode`."), "and that stopping 010V does not cancel a production episode");
            foreach (string label in new[] { PhysicalTestIds.Label010A.Replace(" [armed]", ""), PhysicalTestIds.Label010B.Replace(" [armed]", ""), PhysicalTestIds.Label010V }) T.Check(testing.Contains(label), "the short label is kept: " + label);
            // No phase creep, no overclaim.
            T.Check(!Regex.IsMatch(readme + phases + lifecycle + testing, @"(?i)Phase 3\.2 (is )?(implemented|started|begun|in progress)"), "no document says Phase 3.2 has started");
            T.Eq(5, SaveMigrations.Current, "save format 5");
        }
    }
}
