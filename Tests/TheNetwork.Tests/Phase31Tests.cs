using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TheNetwork.Core;
using TheNetwork.Diagnostics.RuntimePhysicalTests;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using TheNetwork.Settings;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>
    /// Phase 3.1 (PHYSICAL_LIFECYCLE § 21, the controlled physical episode): the guard of the physical test tier (§ 21.2: arm, guard,
    /// verdict, labels, no persisted state), the M1 rule as the production registry represents it, the real adapter's isolation from the
    /// safe suites, and the source scans the 3.1 brief requires. RT-PHYS-020…022 and 030 are cases of the safe physical suite (run
    /// headlessly by Runner.SandboxPhysicalSuite and in game over the fake port); the source-scan half of RT-PHYS-030 is here.
    /// </summary>
    public static class Phase31Tests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Phys31.TierArmIsRuntimeOnlyAndSpent", ArmRuntimeOnly));
            t.Add(new KeyValuePair<string, Action>("Phys31.TierGuardNamesEveryRefusal", GuardRefusals));
            t.Add(new KeyValuePair<string, Action>("Phys31.TierVerdictRules", VerdictRules));
            t.Add(new KeyValuePair<string, Action>("Phys31.TierScenarioTableAndMenuLabels", ScenarioLabels));
            t.Add(new KeyValuePair<string, Action>("Phys31.TierHasNoPersistedStateAndAnIdlePump", NoPersistedState));
            t.Add(new KeyValuePair<string, Action>("Phys31.SafeSuitesCannotReachTheRealAdapter", SafeSuitesIsolated));
            t.Add(new KeyValuePair<string, Action>("Phys31.M1RuleIsRepresentedInTheRealAdapter", M1Represented));
            t.Add(new KeyValuePair<string, Action>("Phys31.ReleaseHandsBackTheEncounterFactionBeforeAuthorityReopens", FactionRelease));
            t.Add(new KeyValuePair<string, Action>("Phys31.Scan_PawnApisOnlyInTheAdapterAndTheTier", PawnApisScoped));
            t.Add(new KeyValuePair<string, Action>("Phys31.Scan_NoPassToWorldOutsideTheAdapter", PassToWorldScoped));
            t.Add(new KeyValuePair<string, Action>("Phys31.Scan_NoDestroyOrDiscardOfBoundPawns", NoDestroyBound));
            t.Add(new KeyValuePair<string, Action>("Phys31.Scan_NoFameInProjection", NoFameInProjection));
            t.Add(new KeyValuePair<string, Action>("Phys31.Scan_NoIdentityRewrite", NoIdentityRewrite));
            t.Add(new KeyValuePair<string, Action>("Phys31.Scan_NoSpikeCodeAndNoHarmony", NoSpikeNoHarmony));
            t.Add(new KeyValuePair<string, Action>("Phys31.RT030_OriginFactsWrittenOnlyAtCreation", OriginFactsWrittenOnce));
            t.Add(new KeyValuePair<string, Action>("Phys31.EpisodeMonitorIsReadOnly", MonitorReadOnly));
            t.Add(new KeyValuePair<string, Action>("Phys31.SaveFormatStaysFive", SaveFormatFive));
        }

        private static string Src(string rel) { return PhysicalLifecycleTests.Src(rel); }

        private static string Code(string src) { return PhysicalLifecycleTests.Code(src); }

        private static string Rel(string f) { return PhysicalLifecycleTests.Rel(f); }

        private static bool InTier(string rel) { return rel.Contains("/Diagnostics/RuntimePhysicalTests/"); }

        private static bool InAdapter(string rel) { return rel.Contains("/Integration/Physical/"); }

        // ================================================================== the tier's guard (§ 21.2)

        private static void ArmRuntimeOnly()
        {
            PhysicalTestArm arm = new PhysicalTestArm();
            object gameA = new object(), gameB = new object();
            string why;
            T.Check(!arm.IsArmedFor(gameA), "not armed at the start");
            T.Check(!arm.Arm(gameA, "arm physical tests", out why) && why != null, "the wrong phrase is refused (" + why + ")");
            T.Check(!arm.Arm(gameA, PhysicalTestIds.ArmPhrase + " ", out why), "a phrase with an extra space is refused");
            T.Check(!arm.Arm(null, PhysicalTestIds.ArmPhrase, out why), "no game, no arm");
            T.Check(!arm.IsArmedFor(gameA), "a refusal arms nothing");
            T.Check(arm.Arm(gameA, PhysicalTestIds.ArmPhrase, out why), "the exact phrase arms");
            T.Check(arm.IsArmedFor(gameA), "armed for that game");
            T.Check(!arm.IsArmedFor(gameB), "never for another game object (a load or a new game is another object: cleared on load and quit)");
            T.Check(arm.Spend(gameA, "RT-PHYX-001"), "one action spends it");
            T.Check(!arm.IsArmedFor(gameA), "spent: one arm = one action");
            T.Check(!arm.Spend(gameA, "again"), "a second action finds it spent");
            arm.Arm(gameA, PhysicalTestIds.ArmPhrase, out why);
            arm.Clear("load");
            T.Check(!arm.IsArmedFor(gameA), "Clear disarms (FinalizeInit calls it on every load and new game)");
            T.Eq(1, arm.SpentCount, "one action was authorised in all");
            string world = Src("Core/NetworkWorldComponent.cs");
            T.Check(world.Contains("PhysicalTestSession.ResetForNewGame();"), "FinalizeInit resets the arm on every load and new game");
        }

        private static void GuardRefusals()
        {
            T.Check(PhysicalTestGuard.Refusal(true, true, true, true, false, 0) == null, "all facts true: an armed action may proceed");
            T.Check(PhysicalTestGuard.Refusal(false, true, true, true, false, 0).Contains("Dev Mode"), "Dev Mode off is named");
            T.Check(PhysicalTestGuard.Refusal(true, false, true, true, false, 0).Contains("not armed"), "an unarmed menu item is inert and says so");
            T.Check(PhysicalTestGuard.Refusal(true, true, false, true, false, 0).Contains("not running"), "a Network that is not running is named");
            T.Check(PhysicalTestGuard.Refusal(true, true, true, false, false, 0).Contains("unavailable"), "an unavailable adapter is named");
            T.Check(PhysicalTestGuard.Refusal(true, true, true, true, true, 0).Contains("in progress"), "a run in progress is named");
            T.Check(PhysicalTestGuard.Refusal(true, true, true, true, false, 2).Contains("not complete"), "incomplete episodes are named");
            // No heuristic about the save: the guard reads facts only (no colonist count, no wealth).
            string model = Code(Src("Diagnostics/RuntimePhysicalTests/PhysicalTestModel.cs"));
            foreach (string guess in new[] { "wealth", "Wealth", "ColonistCount", "colonists", "FreeColonistsCount" }) T.Check(!model.Contains(guess), "the guard never guesses whether the save matters (" + guess + ")");
        }

        private static void VerdictRules()
        {
            PhysicalVerdict v = new PhysicalVerdict("RT-PHYX-000", "r1");
            T.Eq(PhysicalOutcome.Running, v.Outcome, "unfinished: Running");
            v.Check(true, "a");
            v.finished = true;
            T.Eq(PhysicalOutcome.Pass, v.Outcome, "all checks passed: PASS");
            v.Gap("no LeftMap");
            T.Eq(PhysicalOutcome.Inconclusive, v.Outcome, "a gap is INCONCLUSIVE, never PASS");
            v.Check(false, "b");
            T.Eq(PhysicalOutcome.Fail, v.Outcome, "a failure beats everything");
            PhysicalVerdict empty = new PhysicalVerdict("RT-PHYX-000", "r2") { finished = true };
            T.Eq(PhysicalOutcome.Inconclusive, empty.Outcome, "no check at all is never a PASS");
            PhysicalVerdict stop = new PhysicalVerdict("RT-PHYX-000", "r3") { stopped = true };
            T.Eq(PhysicalOutcome.Stopped, stop.Outcome, "a stopped run is reported as stopped");
            T.Check(v.Render("RT-PHYX-000 — x").StartsWith(PhysicalTestIds.LogPrefix + "===== RT-PHYX-000 — x: FAIL", StringComparison.Ordinal), "the log block names the scenario and the outcome");
        }

        private static void ScenarioLabels()
        {
            // 3.2A appended custody 020–025; 3.2B appends groups 026–032. All earlier implemented IDs retain their order and meaning.
            string[] expected = { "RT-PHYX-001", "RT-PHYX-002", "RT-PHYX-003", "RT-PHYX-004", "RT-PHYX-005", "RT-PHYX-006", "RT-PHYX-007", "RT-PHYX-008", "RT-PHYX-009", "RT-PHYX-010", "RT-PHYX-011", "RT-PHYX-012", "RT-PHYX-015", "RT-PHYX-016",
                "RT-PHYX-020", "RT-PHYX-021", "RT-PHYX-022", "RT-PHYX-023", "RT-PHYX-024", "RT-PHYX-025",
                "RT-PHYX-026", "RT-PHYX-027", "RT-PHYX-028", "RT-PHYX-029", "RT-PHYX-030", "RT-PHYX-031", "RT-PHYX-032" };
            T.Eq(expected.Length, PhysicalScenarioTable.All.Count, "the scenario table has exactly the existing and appended group ids");
            for (int i = 0; i < expected.Length; i++) T.Eq(expected[i], PhysicalScenarioTable.All[i].id, "scenario " + (i + 1));
            T.Check(PhysicalScenarioTable.Get("RT-PHYX-013") == null && PhysicalScenarioTable.Get("RT-PHYX-014") == null, "the old suggested gaps are not reused; new implementations append after 025");
            string actions = Src("Diagnostics/RuntimePhysicalTests/PhysicalTestDevActions.cs");
            foreach (PhysicalScenarioInfo s in PhysicalScenarioTable.All)
            {
                if (s.id == "RT-PHYX-010" || s.id == "RT-PHYX-030" || s.id == "RT-PHYX-032")
                {
                    // The runtime-QA correction: RimWorld truncates long debug labels and the three save-matrix items were nearly identical, so they
                    // start with their UNIQUE part (010A / 010B / 010V). The scenario family id is unchanged everywhere else.
                    string[] familyLabels = s.id == "RT-PHYX-010" ? new[] { PhysicalTestIds.Label010A, PhysicalTestIds.Label010B, PhysicalTestIds.Label010V }
                        : s.id == "RT-PHYX-030" ? new[] { PhysicalTestIds.Label030A, PhysicalTestIds.Label030B, PhysicalTestIds.Label030V }
                        : new[] { PhysicalTestIds.Label032A, PhysicalTestIds.Label032B, PhysicalTestIds.Label032V };
                    foreach (string l in familyLabels)
                        T.Check(actions.Contains("\"" + l + "\""), "the menu shows \"" + l + "\" (" + s.id + ", front-loaded)");
                }
                else T.Check(actions.Contains("\"" + s.Label), "the menu shows \"" + s.Label + "\" first");
                T.Check(actions.Contains("PhysicalScenarioTable.Get(\"" + s.id + "\")"), s.id + " is reachable from the menu");
            }
            MatchCollection labels = Regex.Matches(actions, @"\[DebugAction\(Cat, ""([^""]+)""");
            T.Check(labels.Count >= expected.Length + 4, "every scenario and the arm/status/stop/cleanup items are menu items (" + labels.Count + ")");
            foreach (Match m in labels)
            {
                string label = m.Groups[1].Value;
                bool is010 = label == PhysicalTestIds.Label010A || label == PhysicalTestIds.Label010B || label == PhysicalTestIds.Label010V;
                bool isGroupFamily = label == PhysicalTestIds.Label030A || label == PhysicalTestIds.Label030B || label == PhysicalTestIds.Label030V
                    || label == PhysicalTestIds.Label032A || label == PhysicalTestIds.Label032B || label == PhysicalTestIds.Label032V;
                T.Check(label.StartsWith("RT-PHYX-", StringComparison.Ordinal) || label.StartsWith("PHYX — ", StringComparison.Ordinal) || is010 || isGroupFamily, "every label starts with its stable id or unique family part (" + label + ")");
                bool readOnly = label.Contains("(read-only)") || label.Contains("Arm physical tests") || label.Contains("Stop current run") || label == PhysicalTestIds.Label010V;
                T.Check(readOnly || label.EndsWith("[armed]", StringComparison.Ordinal), "a destructive item says it needs the arm (" + label + ")");
            }
            T.Check(Regex.Matches(actions, @"StartReadOnly\(").Count == 4 && actions.Contains("\"" + PhysicalTestIds.Label010V + "\"")
                && actions.Contains("\"RT-PHYX-025 — Held people after save/load (read-only)\"")
                && actions.Contains("\"" + PhysicalTestIds.Label030V + "\"") && actions.Contains("\"" + PhysicalTestIds.Label032V + "\""),
                "only the four after-load verifications/observations (010V, 025, 030V, 032V) run without the arm");
            T.Check(PhysicalTestIds.Category != "The Network" && Src("Diagnostics/RuntimePhysicalTests/PhysicalTestDevActions.cs").Contains("private const string Cat = PhysicalTestIds.Category;"), "a separate category");
            T.Check(PhysicalTestIds.Category.Contains("PHYSICAL TESTS") && PhysicalTestIds.Category.Contains("disposable"), "the category warns in plain words");
        }

        private static void NoPersistedState()
        {
            foreach (string f in PhysicalLifecycleTests.SourceFiles("Diagnostics/RuntimePhysicalTests"))
            {
                string code = Code(File.ReadAllText(f));
                T.Check(!code.Contains("Scribe") && !code.Contains("ExposeData") && !code.Contains("IExposable"), Path.GetFileName(f) + " saves nothing (no persisted test state, no \"disposable\" marker)");
                T.Check(!code.Contains("NetworkWorldComponent"), Path.GetFileName(f) + " writes no persistence root");
                T.Check(!Regex.IsMatch(code, @"\bPrefs\.\w+\s*="), Path.GetFileName(f) + " changes no preference");
            }
            string runner = Code(Src("Diagnostics/RuntimePhysicalTests/PhysicalTestRunner.cs"));
            T.Check(Regex.IsMatch(runner, @"public static void PumpFrame\(\)\s*\{\s*PhysicalRun r = active;\s*if \(r == null\) return;"), "idle cost: one static null check per frame");
            string world = Src("Core/NetworkWorldComponent.cs");
            T.Check(world.Contains("Diagnostics.RuntimePhysicalTests.PhysicalTestSession.PumpFrame();"), "the world component pumps the tier once per frame");
            T.Check(!Code(world).Contains("PhysicalTestSession.Start"), "nothing but a dev action starts a physical run");
            string adapter = Code(Src("Integration/Physical/RimWorldPhysicalWorldPort.cs"));
            T.Check(adapter.Contains("public int visitTicksOverride = -1;") && !adapter.Contains("Scribe"), "the visit-length override is runtime only");
            T.Check(Code(Src("Diagnostics/RuntimePhysicalTests/PhysicalTestRunner.cs")).Contains("if (port.visitTicksOverride > 0) port.visitTicksOverride = -1;"), "and every run resets it when it ends");
        }

        // ================================================================== isolation and the M1 rule

        private static void SafeSuitesIsolated()
        {
            foreach (string f in PhysicalLifecycleTests.SourceFiles("Diagnostics/RuntimeTests"))
            {
                string code = Code(File.ReadAllText(f));
                string name = Path.GetFileName(f);
                foreach (string forbidden in new[] { "Integration.Physical", "RimWorldPhysicalWorldPort", "PhysicalWorld", "RuntimePhysicalTests", "PawnProjection", "RetainedPawnRegistry" })
                {
                    T.Check(!Regex.IsMatch(code, @"\b" + Regex.Escape(forbidden) + @"\b"), "safe runtime file " + name + " never names " + forbidden);
                }
            }
            string sandbox = Src("Diagnostics/RuntimeTests/RuntimeTestSandbox.cs");
            T.Check(sandbox.Contains("physicalPort = Physical") && sandbox.Contains("public readonly FakePhysicalWorldPort Physical"), "the safe sandbox runs over the fake port only");
            T.Check(!Code(Src("Diagnostics/RuntimeTests/RuntimeTestPlans.cs")).Contains("Phyx"), "no safe plan includes a physical scenario");
            string runtime = Src("Core/NetworkRuntime.cs");
            T.Check(runtime.Contains("Ctx.physicalPort = PhysicalWorld;"), "the real adapter is selected by the LIVE runtime only");
            T.Check(!Regex.IsMatch(Code(Src("Diagnostics/SoakHarness.cs")), @"\bPhysicalWorld\b|RimWorldPhysicalWorldPort"), "the soak never reaches the real adapter");
        }

        private static void M1Represented()
        {
            // The predicate itself, over real Pawn objects (never spawned): bound + alive + Deployed or Stored, by REFERENCE.
            TestNet n = new TestNet(9431);
            NetworkActor a = ContractorTests.Make(n, new ContractorTemplate
            {
                templateId = "m1", provenance = TemplateProvenance.Custom, displayName = "M1 Solo", form = ContractorForm.Solo, startingExperience = ExperienceBand.Veteran,
                startingFame = FameBand.Local, doctrineStyle = "Professional", specialties = new List<string> { "escort" }
            });
            KnownCharacter c = n.ctx.characters.Get(a.bindings.embodies);
            // Real pawns carry the thing id the binding persisted (the post-load validation requires the pointer and the persisted id to agree).
            Pawn bound = new Pawn(), twin = new Pawn();
            bound.thingIDNumber = 4242;
            twin.thingIDNumber = 4243;
            c.pawn = new PawnRef { pawn = bound, thingIdNumber = 4242, defName = "Human", boundTick = 1, agedThroughTick = 1 };
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            r.Rebuild();
            c.custody = CustodyState.Unmaterialized;
            T.Check(!r.Reserves(bound), "Unmaterialized is not retained");
            c.custody = CustodyState.Deployed;
            T.Check(r.Reserves(bound), "Deployed: reserved while spawned (M1: in force before vanilla's exit passes it)");
            c.custody = CustodyState.Stored;
            T.Check(r.Reserves(bound), "Stored: reserved");
            T.Check(!r.Reserves(twin), "another Pawn object is never reserved (reference equality, never the thing id)");
            T.Check(!r.Reserves(null), "null is not reserved");
            c.status = CharacterStatus.Dead;
            T.Check(!r.Reserves(bound), "a dead person's pawn is not retained");
            c.status = CharacterStatus.Active;
            r.inert = true;
            T.Check(!r.Reserves(bound), "prepared for removal: the registry reserves nobody");
            r.inert = false;
            c.pawn = new PawnRef { pawn = twin, thingIdNumber = 4243 };
            T.Check(!r.Reserves(bound), "the index never outlives the binding: only the CURRENT binding's pawn counts");
            r.Rebuild();
            T.Check(r.Reserves(twin) && r.RetainedCount() == 1, "rebuilt from the stores: the current binding is the one retained pawn");

            // The adapter places a pawn only when the registry ALREADY covers it, before vanilla's spawn; the part keeps no pawn list.
            string adapter = Code(Src("Integration/Physical/RimWorldPhysicalWorldPort.cs"));
            int refusal = adapter.IndexOf("string refusal = PlacementRefusal(p, mapId, faction);", StringComparison.Ordinal);
            int spawn = adapter.IndexOf("GenSpawn.Spawn(p, entry, map);", StringComparison.Ordinal);
            T.Check(refusal > 0 && spawn > refusal, "Place checks its preconditions before vanilla's spawn");
            T.Check(adapter.Contains("if (!Registry.Reserves(p)) return \"M1 precondition"), "the M1 precondition: the registry must cover the pawn before placement");
            // The runtime-QA correction: FinalizeInit runs BEFORE the load's cross-references resolve, so it builds only the DURABLE thing-id stage; the
            // pointer stage is the world component's PostLoadInit (Phase31QaTests.LoadOrderScan pins both).
            T.Check(Regex.IsMatch(adapter, @"Registry = new RetainedPawnRegistry\(ctx\);\s*[^}]*Registry\.RebuildEarly\(\);"), "the runtime's durable thing-id stage is built when the runtime is built (FinalizeInit), before the first tick");
            string registry = Code(Src("Integration/Physical/RetainedPawnRegistry.cs"));
            T.Check(Regex.IsMatch(registry, @"public override void ExposeData\(\)\s*\{\s*base\.ExposeData\(\);\s*\}"), "the Network-owned quest part persists no pawn list");
            T.Check(registry.Contains("public sealed class QuestPart_NetworkRetainedPawns : QuestPart"), "the saved class name is stable");
            T.Check(registry.Contains("ReferenceEquals(c.pawn.pawn, p)") && registry.Contains("RetainedCustody(c)"), "the predicate is reference equality plus retained custody");
            T.Check(!adapter.Contains("Harmony") && !registry.Contains("Harmony"), "no patch: the reservation is vanilla's own quest mechanism");
        }

        private static void FactionRelease()
        {
            TestNet n = new TestNet(9432);
            NetworkActor a = ContractorTests.Make(n, new ContractorTemplate
            {
                templateId = "fr", provenance = TemplateProvenance.Custom, displayName = "Faction Solo", form = ContractorForm.Solo, startingExperience = ExperienceBand.Veteran,
                startingFame = FameBand.Local, doctrineStyle = "Professional", specialties = new List<string> { "escort" }
            });
            n.ctx.Spatial.EnsureInitialized(a);
            KnownCharacter c = n.ctx.characters.Get(a.bindings.embodies);
            PhysicalEpisode e;
            CommandResult res = n.ctx.Lifecycle.Plan(Diagnostics.RuntimeTests.Suites.PhysicalRuntimeSuite.Request(a, new[] { c }), out e);
            T.Check(res.ok, "planned");
            n.ctx.Lifecycle.Materialize(e);
            T.Check(e.faction != null && e.faction.IsValid && n.physical.factions[e.faction.loadId], "Materialize made the episode's encounter faction first");
            T.Eq(c.opRole, e.members[0].seatRole, "the member's seat is the person's stored role");
            n.physical.ThrowOn("faction-release", 1);
            n.physical.ExitNormally(c.pawn, 80);
            n.ctx.Lifecycle.Reconcile(e, "exit");
            T.Check(e.consequencesApplied && !e.releaseApplied, "a failing faction hand-back keeps RELEASE pending");
            T.Check(!AuthorityGate.CanSimulateAbstractly(c), "and abstract authority stays closed");
            T.Check(n.physical.factions[e.faction.loadId], "the faction was not released yet");
            n.ctx.Lifecycle.FinishPending(e);
            T.Check(e.IsComplete && !n.physical.factions[e.faction.loadId], "the retry hands it back and completes");
            T.Check(AuthorityGate.CanSimulateAbstractly(c), "only then does authority reopen");
            T.Eq(0, n.physical.Count("pass "), "no PassToWorld for a pawn vanilla already passed");
        }

        // ================================================================== source scans (the 3.1 brief § 27)

        private static readonly string[] PawnApis = { "PawnGenerator", "GenSpawn", "LordMaker", "MakeNewLord", "FactionGenerator", "AgeTickMothballed", "DamageUntilDowned", "DamageUntilDead", "CapturedBy(", "DeinitAndRemoveMap", "RemoveHediff", "AddHediff", "SetFaction", "Find.WorldPawns" };

        private static void PawnApisScoped()
        {
            foreach (string f in PhysicalLifecycleTests.AllSources())
            {
                string rel = Rel(f);
                if (InTier(rel) || InAdapter(rel)) continue;
                string code = Code(File.ReadAllText(f));
                foreach (string api in PawnApis) T.Check(!code.Contains(api), "pawn API " + api + " only in the adapter or the physical tier (" + rel + ")");
            }
            // Within the adapter, the actions are where the design puts them.
            T.Check(Src("Integration/Physical/PawnProjection.cs").Contains("forceGenerateNewPawn: true") && Src("Integration/Physical/PawnProjection.cs").Contains("canGeneratePawnRelations: false"), "first creation forces a new pawn and generates no relations");
            T.Check(!Code(Src("Integration/Physical/PawnProjection.cs")).Contains("minChanceToRedressWorldPawn"), "production creation never asks vanilla to redress");
        }

        private static void PassToWorldScoped()
        {
            List<string> callers = new List<string>();
            foreach (string f in PhysicalLifecycleTests.AllSources())
            {
                string code = Code(File.ReadAllText(f));
                if (Regex.IsMatch(code, @"WorldPawns\.PassToWorld\(")) callers.Add(Rel(f));
            }
            T.Eq(3, callers.Count, "only the production adapter, 022's owned warden and explicit guarded QA lab reset call PassToWorld (" + string.Join(", ", callers.ToArray()) + ")");
            T.Check(callers.Exists(x => x.EndsWith("Integration/Physical/RimWorldPhysicalWorldPort.cs", StringComparison.Ordinal)) &&
                callers.Exists(x => x.EndsWith("RuntimePhysicalTests/PhysicalCustodyScenarios.cs", StringComparison.Ordinal)) &&
                callers.Exists(x => x.EndsWith("RuntimePhysicalTests/QaLab.cs", StringComparison.Ordinal)), "exactly three bounded callers; no generic scenario removal path");
            QaLabTests.AssertDestructionBoundary(Code(Src("Diagnostics/RuntimePhysicalTests/QaLab.cs")));
            string custody = Code(Src("Diagnostics/RuntimePhysicalTests/PhysicalCustodyScenarios.cs"));
            T.Eq(1, Regex.Matches(custody, @"WorldPawns\.PassToWorld\(").Count, "the scenario has one additional call only");
            T.Check(custody.Contains("Find.WorldPawns.PassToWorld(warden, PawnDiscardDecideMode.Discard)") &&
                custody.Contains("refusal = TestFixtures.DisposeRefusal(warden, runId, ctx);") && custody.Contains("if (refusal == null)"), "the exception is discard-only and ownership guarded, never a bound contractor transfer");
            string adapter = Code(Src("Integration/Physical/RimWorldPhysicalWorldPort.cs"));
            T.Check(Regex.IsMatch(adapter, @"PassToWorldCheck check = PawnObserver\.CheckPass\(p\);\s*if \(check != PassToWorldCheck\.Allowed\) throw"), "and it re-checks the § 7.5 precondition first (never a world pawn again)");
            string observer = Code(Src("Integration/Physical/PawnObserver.cs"));
            T.Check(observer.Contains("Find.WorldPawns.Contains(p)) return PassToWorldCheck.AlreadyInWorldPawns"), "an already-passed world pawn is never allowed");
        }

        private static void NoDestroyBound()
        {
            foreach (string f in PhysicalLifecycleTests.AllSources())
            {
                string rel = Rel(f);
                string code = Code(File.ReadAllText(f));
                if (!InTier(rel) && !InAdapter(rel)) continue;
                MatchCollection discards = Regex.Matches(code, @"\.Discard\(");
                MatchCollection destroys = Regex.Matches(code, @"\.Destroy\(");
                if (rel.EndsWith("RuntimePhysicalTests/PhysicalTestWorld.cs", StringComparison.Ordinal))
                {
                    T.Eq(1, discards.Count, "the tier discards in ONE place");
                    T.Check(Regex.IsMatch(code, @"refusal = DisposeRefusal\(p, runId, ctx\);\s*if \(refusal != null\) return false;\s*p\.Discard\(true\);"), "and only after DisposeRefusal proved the pawn is its own");
                    T.Check(code.Contains("bound to a Network person (registry)") && code.Contains("not tagged by this run") && code.Contains("a world pawn"), "a bound, untagged or world pawn is refused");
                    T.Eq(1, destroys.Count, "the tier destroys one thing: its own test-site world object");
                    T.Check(code.Contains("parent.Destroy();"), "the test site");
                }
                else if (rel.EndsWith("RuntimePhysicalTests/QaLab.cs", StringComparison.Ordinal))
                {
                    T.Eq(0, discards.Count, "QA lab reset never calls Pawn.Discard directly");
                    T.Eq(1, destroys.Count, "one explicit dedicated-map reset destruction call only");
                    QaLabTests.AssertDestructionBoundary(code);
                }
                else
                {
                    T.Eq(0, discards.Count, rel + " discards nothing");
                    T.Eq(0, destroys.Count, rel + " destroys nothing");
                }
                T.Check(!Regex.IsMatch(code, @"\.Kill\("), rel + " kills nothing directly (dev damage goes through vanilla's damage helpers)");
            }
        }

        private static void NoFameInProjection()
        {
            string model = Code(Src("Domain/Physical/ProjectionModel.cs"));
            string projection = Code(Src("Integration/Physical/PawnProjection.cs"));
            foreach (string word in new[] { "Fame", "fame", "reputation", "Reputation", "visibility", "Visibility", "CareerRecord", "career" })
            {
                T.Check(!model.Contains(word), "the projection model reads no " + word);
                T.Check(!projection.Contains(word), "the projection adapter reads no " + word);
            }
            string experience = Code(Src("Domain/Contractors/ContractorService.cs"));
            Match m = Regex.Match(experience, @"public static ExperienceBand Experience\(NetworkActor a\)\s*\{(?<body>[^}]*)\}");
            T.Check(m.Success && !m.Groups["body"].Value.Contains("reputation") && !m.Groups["body"].Value.Contains("fame"), "the capability band (experience) reads no fame");
        }

        private static void NoIdentityRewrite()
        {
            string[] forbidden = { "AddDirectRelation", "RemoveDirectRelation", "GainTrait", "RemoveTrait", "AddGene", "RemoveGene", "SetXenotype", "TryGainMemory", "ageBiologicalTicksInt", "opinion" };
            Regex pawnWrite = new Regex(@"\b(?:p|pawn|d|stored|x)\.(gender|Name|kindDef)\s*=(?!=)|\bstory\.(Childhood|Adulthood)\s*=(?!=)|\b\w+\.(passion)\s*=(?!=)");
            foreach (string f in PhysicalLifecycleTests.AllSources())
            {
                string rel = Rel(f);
                if (!InTier(rel) && !InAdapter(rel) && !rel.Contains("/Domain/Physical/")) continue;
                string code = Code(File.ReadAllText(f));
                foreach (string w in forbidden) T.Check(!code.Contains(w), "no relation, passion, trait, backstory, gene, gender, thought or age rewrite (" + w + " in " + rel + ")");
                foreach (Match m in pawnWrite.Matches(code))
                {
                    // The one name write is the established-name pin on a fresh, unbound candidate (§ 6.3), in the projection.
                    string what = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                    bool namePin = what == "Name" && rel.EndsWith("Integration/Physical/PawnProjection.cs", StringComparison.Ordinal);
                    T.Check(namePin, "no " + what + " write on a pawn (" + rel + ")");
                }
                if (Regex.IsMatch(code, @"\.levelInt\s*=")) T.Check(rel.EndsWith("Integration/Physical/PawnProjection.cs", StringComparison.Ordinal), "a skill's base level is written only by the one allowed correction (" + rel + ")");
            }
            string projection = Code(Src("Integration/Physical/PawnProjection.cs"));
            T.Check(Regex.IsMatch(projection, @"if \(s == null \|\| s\.TotallyDisabled \|\| v\.correctBaseTo <= s\.levelInt\) return false;\s*s\.levelInt = Mathf\.Clamp\(v\.correctBaseTo, 0, 20\);"), "the correction raises only, never on a disabled skill");
        }

        private static void NoSpikeNoHarmony()
        {
            foreach (string f in PhysicalLifecycleTests.AllSources())
            {
                string rel = Rel(f);
                string code = Code(File.ReadAllText(f));
                foreach (string w in new[] { "S31Spike", "S31World", "S31Ids", "S31Ownership", "S31Run", "Diagnostics.Spikes", "QuestPart_ReservePawns" }) T.Check(!code.Contains(w), "no spike code in production (" + w + " in " + rel + ")");
                foreach (string w in new[] { "HarmonyLib", "new Harmony(", "HarmonyPatch", "0Harmony" }) T.Check(!code.Contains(w), "no Harmony (" + w + " in " + rel + ")");
            }
            T.Check(!Directory.Exists(Path.Combine(PhysicalLifecycleTests.Root, "Diagnostics/Spikes")), "the spike folder is not part of this branch");
        }

        /// <summary>RT-PHYS-030, the source half: nothing writes an actor's seed, its contractor specialties or the stored role after creation.</summary>
        private static void OriginFactsWrittenOnce()
        {
            foreach (string f in PhysicalLifecycleTests.AllSources())
            {
                string rel = Rel(f);
                string code = Code(File.ReadAllText(f));
                if (Regex.IsMatch(code, @"\ba\.seed\s*=(?!=)")) T.Check(rel.EndsWith("Domain/Actors/ActorService.cs", StringComparison.Ordinal) || rel.EndsWith("Domain/Contractors/ContractorService.cs", StringComparison.Ordinal), "an actor's seed is assigned only at creation (" + rel + ")");
                if (Regex.IsMatch(code, @"profile\.specialties\.(Add|AddRange|Clear|Remove|Insert)\(|profile\.specialties\s*=(?!=)")) T.Check(rel.EndsWith("Domain/Contractors/ContractorService.cs", StringComparison.Ordinal), "contractor specialties are written only at Instantiate (" + rel + ")");
                if (Regex.IsMatch(code, @"\.opRole\s*=(?!=)"))
                {
                    T.Check(rel.EndsWith("Domain/Contractors/ContractorService.cs", StringComparison.Ordinal) || rel.EndsWith("Domain/Physical/PhysicalLifecycleService.cs", StringComparison.Ordinal), "the role is stored only at Instantiate or lazily at the first Plan (" + rel + ")");
                }
            }
            string lifecycle = Code(Src("Domain/Physical/PhysicalLifecycleService.cs"));
            T.Check(lifecycle.Contains("if (c.opRole == OperationalRole.Unset && a.bindings.embodies == c.id) c.opRole = RoleDerivation.ForSolo(a);"), "the lazy store happens only while unset, from origin facts");
            string service = Code(Src("Domain/Contractors/ContractorService.cs"));
            Match inst = Regex.Match(service, @"profile\.specialties\.AddRange\(");
            T.Check(inst.Success, "Instantiate copies the template's specialties once");
            string model = Code(Src("Domain/Physical/ProjectionModel.cs"));
            Match derive = Regex.Match(model, @"public static OperationalRole SoloRole\(int actorSeed, IList<string> specialties\)\s*\{(?<body>[^}]*)\}");
            T.Check(derive.Success && !Regex.IsMatch(derive.Groups["body"].Value, @"\b(sim|experience|doctrine|funds|fame|morale|career|status)\b"), "the derivation reads only the seed and the specialties");
        }

        private static void MonitorReadOnly()
        {
            string monitor = Code(Src("Diagnostics/EpisodeMonitor.cs"));
            foreach (string w in new[] { "ButtonText", "ButtonImage", "Reconcile(", "Plan(", "Materialize(", "FinishPending(", "Quarantine(", "Wake(", ".custody =", ".state =", "Registry.", "PassToWorld", "Normalize(" })
            {
                T.Check(!monitor.Contains(w), "the Episode Monitor has no mutating call or button (" + w + ")");
            }
            T.Check(monitor.Contains("DescribeBinding("), "it reads pawns only through the adapter's plain-text description");
            T.Check(!Regex.IsMatch(monitor, @"\bPawn\b"), "it never touches a Pawn");
            T.Check(monitor.Contains("\"Episode Monitor (read-only)\""), "the menu says read-only");
        }

        private static void SaveFormatFive()
        {
            T.Eq(5, SaveMigrations.Current, "Phase 3.1 keeps save format 5 (no new durable field)");
            string runtime = Code(Src("Core/NetworkRuntime.cs"));
            T.Check(!Regex.IsMatch(Code(Src("Integration/Physical/RetainedPawnRegistry.cs")), @"Scribe_"), "the registry's index is runtime-only");
            T.Check(runtime.Contains("PhysicalWorld = new Integration.Physical.RimWorldPhysicalWorldPort(Ctx);"), "the adapter is built from the runtime, not loaded");
        }
    }
}
