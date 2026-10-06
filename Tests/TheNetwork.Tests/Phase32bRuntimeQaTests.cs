using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TheNetwork.Diagnostics.RuntimePhysicalTests;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;

namespace TheNetwork.Tests
{
    /// <summary>Headless QA guard/provenance checks; actual in-game verdicts remain owner acceptance.</summary>
    public static class Phase32bRuntimeQaTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_AppendedStableScenarioIds", StableIds));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_FrontLoadedSaveLabelsRemainOneFamily", SaveLabels));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_GroupHarnessHasNoPersistedQaState", NoPersistedState));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_SyntheticVisibilityIsExactScopedTestSiteOnly", VisibilityScope));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_OwnerProvenanceIsExactRunAndScenario", ExactOwnership));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_LatestSaveEpisodeRejectsFamilyCollisionsAndDuplicates", LatestEpisode));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_UnreleasedBindingBlocksCleanupThroughRelease", ReleaseBoundary));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_RetentionTargetsStayBoundedAndSoft", RetentionTargets));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_LatestSupportedCheckpointIncludesNewerSuccession", LatestCheckpoint));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_PendingCaptureChecksExactOwnerAndNoEarlyIdentity", CaptureWiring));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_CleanupPreservesRegistryAndUnreleasedEpisodeTruth", CleanupWiring));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_SaveVerifiersReadTruthWithoutDrivingOutcomes", ReadOnlyVerifiers));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.RuntimeQa_RetentionMeasurementsAreBoundedAndLabelManualGaps", RetentionWiring));
        }

        private static string Source(string relative)
        {
            return PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src(relative));
        }

        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            T.Check(at >= 0, "source boundary exists: " + signature);
            if (at < 0) return "";
            int open = source.IndexOf('{', at);
            T.Check(open >= 0, "source boundary has a body: " + signature);
            if (open < 0) return "";
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            T.Check(false, "source boundary closes: " + signature);
            return "";
        }

        private static void StableIds()
        {
            string[] original =
            {
                "RT-PHYX-001", "RT-PHYX-002", "RT-PHYX-003", "RT-PHYX-004", "RT-PHYX-005", "RT-PHYX-006",
                "RT-PHYX-007", "RT-PHYX-008", "RT-PHYX-009", "RT-PHYX-010", "RT-PHYX-011", "RT-PHYX-012",
                "RT-PHYX-015", "RT-PHYX-016", "RT-PHYX-020", "RT-PHYX-021", "RT-PHYX-022", "RT-PHYX-023",
                "RT-PHYX-024", "RT-PHYX-025"
            };
            string[] added = { "RT-PHYX-026", "RT-PHYX-027", "RT-PHYX-028", "RT-PHYX-029", "RT-PHYX-030", "RT-PHYX-031", "RT-PHYX-032" };
            T.Check(PhysicalScenarioTable.All.Take(original.Length).Select(s => s.id).SequenceEqual(original), "all earlier ids retain their original order");
            T.Check(PhysicalScenarioTable.All.Skip(original.Length).Select(s => s.id).SequenceEqual(added), "new group ids append after custody 025");
            T.Eq(27, PhysicalScenarioTable.All.Count, "bounded stable physical scenario table");
            T.Eq(27, PhysicalScenarioTable.All.Select(s => s.id).Distinct().Count(), "no reused or duplicate scenario id");
            T.Check(PhysicalScenarioTable.Get("RT-PHYX-009").IsRetired, "retired unsupported-custody id stays retired");
            T.Check(PhysicalScenarioTable.Get("RT-PHYX-013") == null && PhysicalScenarioTable.Get("RT-PHYX-014") == null, "reserved legacy holes stay absent");
            foreach (string id in added)
            {
                PhysicalScenarioInfo info = PhysicalScenarioTable.Get(id);
                T.Check(info != null && info.solo == SoloNeed.None, "new group/fixture scenario never borrows an arbitrary Solo: " + id);
            }
        }

        private static void SaveLabels()
        {
            string ids = Source("Diagnostics/RuntimePhysicalTests/PhysicalTestModel.cs");
            string menu = Source("Diagnostics/RuntimePhysicalTests/PhysicalTestDevActions.cs");
            foreach (string label in new[] { "030A SAVE", "030B SAVE", "030V VERIFY" })
            {
                T.Check(ids.Contains(label) && menu.Contains(label), "unique save/load label is front-loaded in both model and actual menu: " + label);
            }
            T.Check(menu.Contains("PhysicalScenarioTable.Get(\"RT-PHYX-030\")"), "three save commands use the single stable 030 family");
            T.Check(PhysicalScenarioTable.Get("RT-PHYX-030A") == null && PhysicalScenarioTable.Get("RT-PHYX-030B") == null && PhysicalScenarioTable.Get("RT-PHYX-030V") == null, "display suffixes are never new persisted scenario ids");
            T.Check(menu.Contains("StartReadOnly(") && menu.Contains("Phyx030"), "after-load group verifier uses the existing unarmed read-only entry point");
        }

        private static void NoPersistedState()
        {
            foreach (string file in new[] { "PhysicalGroupScenarios.cs", "PhysicalGroupTestRules.cs" })
            {
                string code = Source("Diagnostics/RuntimePhysicalTests/" + file);
                T.Check(!Regex.IsMatch(code, @"\b(?:Scribe(?:_\w+)?|IExposable|ExposeData|NetworkWorldComponent)\b"), file + " persists no independent QA state or root");
                T.Check(!Regex.IsMatch(code, @"\bPrefs\.\w+\s*=(?!=)"), file + " does not change user preferences");
            }
            string adapter = Source("Integration/Physical/RimWorldPhysicalWorldPort.cs");
            string scope = Body(adapter, "private sealed class VisibilityScope : IDisposable");
            T.Check(scope.Contains("public readonly Game game;") && scope.Contains("public readonly PhysicalEpisode ownedEpisode;"), "override owns runtime game/Episode objects only");
            T.Check(!Regex.IsMatch(scope, @"\b(?:Scribe(?:_\w+)?|ExposeData)\b"), "visibility override is never saved");
        }

        private static void VisibilityScope()
        {
            string adapter = Source("Integration/Physical/RimWorldPhysicalWorldPort.cs");
            string visible = Body(adapter, "public bool IsPlayerVisiblePlacement(");
            T.Check(visible.Contains("ReferenceEquals(ctx.episodes?.Get(episode.id), episode)") && visible.Contains("episode.members.Contains(member)"), "visibility requires exact durable Episode/member ownership");
            T.Check(visible.Contains("p.Spawned") && visible.Contains("map.uniqueID != episode.whereMapId") && visible.Contains("member.pawn.thingIdNumber != p.thingIDNumber"), "positive visibility requires actual matching map spawn and exact binding");
            T.Check(visible.Contains("ReferenceEquals(scope.ownedEpisode, episode)") && visible.Contains("scope.mapId == map.uniqueID") && visible.Contains("PhysicalTestSession.IsActiveOwnedEpisode(episode)"), "override applies only to its exact active run/Episode/map");
            T.Check(visible.Contains("scope.synthetic ? testMap") && visible.Contains("if (testMap || !string.IsNullOrEmpty(episode.cause?.devKey)) return false;"), "ordinary test/dev presence cannot become production P0");
            string synthetic = Body(adapter, "public IDisposable OverrideTestVisibility(");
            T.Check(synthetic.Contains("PhysicalTestIds.TestMapDef") && synthetic.Contains("throw new InvalidOperationException") && synthetic.Contains("PushVisibility(e, mapId, true)"), "synthetic P0 is restricted to dedicated owned TestSite");
            string authorization = Body(adapter, "private PhysicalEpisode VisibilityEpisode(");
            T.Check(authorization.Contains("PhysicalTestSession.IsActiveOwnedEpisode(e)"), "visibility scope acquisition rejects another physical run's Episode even while a QA run is active");
            string scope = Body(adapter, "private sealed class VisibilityScope : IDisposable");
            T.Check(scope.Contains("public void Dispose()") && scope.Contains("owner.visibilityScope = restore;"), "scope restores the previous override on Dispose");
            string scenarios = Source("Diagnostics/RuntimePhysicalTests/PhysicalGroupScenarios.cs");
            T.Check(Regex.IsMatch(scenarios, @"using\s*\([^)]*OverrideTestVisibility\("), "group harness activates synthetic visibility only inside using scope");
        }

        private static PhysicalEpisode Episode(int id, string run = "owned-run", string family = "RT-PHYX-030")
        {
            return new PhysicalEpisode { id = new EpisodeId(id), actor = new ActorId(100), cause = new EpisodeCause { devKey = PhysicalTestIds.DevKey(run, family) } };
        }

        private static void ExactOwnership()
        {
            PhysicalEpisode episode = Episode(1);
            T.Check(GroupQaRules.OwnedByRun(episode, "owned-run", "RT-PHYX-030"), "exact persisted provenance selects owner Episode");
            T.Check(!GroupQaRules.OwnedByRun(episode, "other-run", "RT-PHYX-030"), "same scenario in another run is not owned");
            T.Check(!GroupQaRules.OwnedByRun(episode, "owned-run", "RT-PHYX-029"), "same run in another scenario is not owned");
            T.Check(!GroupQaRules.OwnedByRun(episode, "owned", "RT-PHYX-030"), "run-prefix collision does not borrow unrelated members");
            T.Check(!GroupQaRules.OwnedByRun(episode, null, "RT-PHYX-030") && !GroupQaRules.OwnedByRun(episode, "owned-run", ""), "unknown ownership arguments are not positive evidence");
            foreach (string wrong in new[] { "PhysicalTest:owned-run:RT-PHYX-030-extra", "PhysicalTest:other-run:owned-run:RT-PHYX-030", "physicaltest:owned-run:RT-PHYX-030", "PhysicalTest:owned-run:RT-PHYX-030:" })
            {
                episode.cause.devKey = wrong;
                T.Check(!GroupQaRules.OwnedByRun(episode, "owned-run", "RT-PHYX-030"), "altered provenance cannot match owner: " + wrong);
            }
            episode.cause = null;
            T.Check(!GroupQaRules.OwnedByRun(episode, "owned-run", "RT-PHYX-030") && !GroupQaRules.OwnedByRun(null, "owned-run", "RT-PHYX-030"), "missing Episode/cause stays unowned");
        }

        private static void LatestEpisode()
        {
            PhysicalEpisode old = Episode(7), latest = Episode(20, "later-run"), other = Episode(500, family: "RT-PHYX-029"), collision = Episode(600, family: "RT-PHYX-030A");
            old.createdTick = 10000;
            latest.createdTick = 1;
            List<PhysicalEpisode> episodes = new List<PhysicalEpisode> { null, collision, latest, old, other, Episode(0) };
            T.Check(ReferenceEquals(latest, GroupQaRules.Latest(episodes, "RT-PHYX-030")), "greatest valid family EpisodeId wins independently of list order/creation tick");
            episodes.Reverse();
            T.Check(ReferenceEquals(latest, GroupQaRules.Latest(episodes, "RT-PHYX-030")), "selection is stable under reversed enumeration");
            T.Check(GroupQaRules.Latest(episodes, "RT-PHYX-031") == null && GroupQaRules.Latest(episodes, null) == null && GroupQaRules.Latest(null, "RT-PHYX-030") == null, "absent/unknown family never borrows an unrelated completed save");
            episodes.Add(Episode(7, "different-run"));
            T.Check(GroupQaRules.Latest(episodes, "RT-PHYX-030") == null, "duplicate lower relevant ID is ambiguous even if newest ID differs");
            episodes.RemoveAt(episodes.Count - 1);
            episodes.Add(Episode(20, "different-run"));
            T.Check(GroupQaRules.Latest(episodes, "RT-PHYX-030") == null, "duplicate latest relevant ID also refuses selection");
            episodes.RemoveAt(episodes.Count - 1);
            episodes.Add(Episode(500, family: "RT-PHYX-029"));
            T.Check(ReferenceEquals(latest, GroupQaRules.Latest(episodes, "RT-PHYX-030")), "another family's duplicate does not make this save ambiguous");
            T.Check(latest.cause.devKey == PhysicalTestIds.DevKey("later-run", "RT-PHYX-030") && latest.members.Count == 0, "read-only selection mutates no existing Episode data");
        }

        private static void LatestCheckpoint()
        {
            PhysicalEpisode group = Episode(20), succession = Episode(30, family: "RT-PHYX-031"), unrelated = Episode(900, family: "RT-PHYX-032");
            List<PhysicalEpisode> episodes = new List<PhysicalEpisode> { group, succession, unrelated, Episode(1000, family: "RT-PHYX-030V"), null };
            T.Check(ReferenceEquals(succession, GroupQaRules.LatestCheckpoint(episodes)), "newer succession SAVE cannot be hidden behind an older group checkpoint");
            episodes.Add(Episode(20, "duplicate-group-run"));
            T.Check(GroupQaRules.LatestCheckpoint(episodes) == null, "ambiguous old group family cannot silently fall back to a newer succession checkpoint");
            episodes.RemoveAt(episodes.Count - 1);
            episodes.Add(Episode(30, "cross-family-duplicate"));
            T.Check(GroupQaRules.LatestCheckpoint(episodes) == null, "same EpisodeId across both supported checkpoint families is also ambiguous");
            episodes.RemoveAt(episodes.Count - 1);
            episodes.Add(Episode(40, "new-group-run"));
            T.Eq(40, GroupQaRules.LatestCheckpoint(episodes).id.Value, "newer group checkpoint can equally follow earlier succession SAVE");
            T.Check(GroupQaRules.LatestCheckpoint(new[] { unrelated }) == null && GroupQaRules.LatestCheckpoint(null) == null, "no supported checkpoint is inconclusive rather than another scenario's PASS");
        }

        private static void ReleaseBoundary()
        {
            PhysicalEpisode episode = Episode(1);
            EpisodeMember member = new EpisodeMember { slot = 0, seatRole = OperationalRole.Rifleman, pawn = new PawnRef { thingIdNumber = 71001, defName = "Human" } };
            episode.members.Add(member);
            List<PhysicalEpisode> episodes = new List<PhysicalEpisode> { null, episode };
            foreach (EpisodeState state in new[] { EpisodeState.Planned, EpisodeState.Open, EpisodeState.Quarantined, EpisodeState.Closed })
            {
                episode.state = state;
                episode.consequencesApplied = state == EpisodeState.Closed;
                T.Check(GroupQaRules.HasUnreleasedBinding(episodes, 71001), "durable " + state + " anonymous binding blocks cleanup until release");
            }
            member.character = new CharacterId(19);
            T.Check(GroupQaRules.HasUnreleasedBinding(episodes, 71001), "committed promotion still blocks cleanup before named runtime index handoff");
            T.Check(!GroupQaRules.HasUnreleasedBinding(episodes, 71002) && !GroupQaRules.HasUnreleasedBinding(episodes, 0) && !GroupQaRules.HasUnreleasedBinding(null, 71001), "unrelated and unknown physical IDs do not manufacture ownership");
            episode.releaseApplied = true;
            T.Check(!GroupQaRules.HasUnreleasedBinding(episodes, 71001), "only durable RELEASE marker ends Episode cleanup ownership");
            episode.releaseApplied = false;
            member.pawn = null;
            T.Check(!GroupQaRules.HasUnreleasedBinding(episodes, 71001), "stale slot without binding does not own unrelated Pawn");
            member.pawn = new PawnRef { thingIdNumber = 71001, defName = "Human" };
            member.outcome = MemberOutcome.Killed;
            T.Check(GroupQaRules.HasUnreleasedBinding(episodes, 71001), "cleanup preserves bound corpse evidence until release rather than treating outcome as completed cleanup");
        }

        private static void RetentionTargets()
        {
            foreach (int target in new[] { 150, 300 })
            {
                T.Eq(6, GroupQaRules.RetentionRequestSize(0, target), "retention fixture starts with bounded group at target " + target);
                T.Eq(2, GroupQaRules.RetentionRequestSize(target - 1, target), "last one-human shortfall uses valid approximate two-human group");
                T.Eq(0, GroupQaRules.RetentionRequestSize(target, target), "target is observational, already reached needs no new humans");
                T.Eq(0, GroupQaRules.RetentionRequestSize(target + 100, target), "strong identities above target are preserved without another pressure fixture");
                for (int current = 0; current < target; current++)
                {
                    int request = GroupQaRules.RetentionRequestSize(current, target);
                    T.Check(request >= 2 && request <= 6 && current + request <= target + 1, "per-iteration bounded request at " + current + "/" + target);
                }
                int retained = 0, iterations = 0;
                while (retained < target && iterations < target)
                {
                    retained += GroupQaRules.RetentionRequestSize(retained, target);
                    iterations++;
                }
                T.Check(retained >= target && retained <= target + 1 && iterations <= (target + 1) / 2, "bounded requests terminate around soft target " + target);
            }
            T.Eq(0, GroupQaRules.RetentionRequestSize(-1, 150), "unknown current count is refused");
            foreach (int unsupported in new[] { -1, 0, 149, 151, 299, 301, int.MaxValue })
                T.Eq(0, GroupQaRules.RetentionRequestSize(0, unsupported), "no arbitrary retention target is accepted: " + unsupported);
            T.Eq(0, GroupQaRules.RetentionRequestSize(int.MaxValue, 300), "large actual retention count neither overflows nor forces deletion");
        }

        private static void CaptureWiring()
        {
            string runner = Source("Diagnostics/RuntimePhysicalTests/PhysicalTestRunner.cs");
            string owned = Body(runner, "public static bool IsActiveOwnedEpisode(");
            T.Check(owned.Contains("GroupQaRules.OwnedByRun(") && owned.Contains("active") && owned.Contains("runId"), "visibility authorization is wired to exact active runtime run provenance");
            string scenarios = Source("Diagnostics/RuntimePhysicalTests/PhysicalGroupScenarios.cs");
            string capture = Body(scenarios, "protected StepResult ArrestAnonymous(");
            T.Check(capture.Contains("TestSite.IsTestMap(captive.Map)") && capture.Contains("capturedMember.IsNamed") && capture.Contains("captive.guest.CapturedBy(Faction.OfPlayer)"), "capture drives only this owned anonymous TestSite Pawn through real vanilla arrest");
            string pending = Body(scenarios, "protected StepResult CheckPendingCapture(");
            T.Check(pending.Contains("MemberOutcome.Pending") && pending.Contains("!e.consequencesApplied") && pending.Contains("ctx.characters.characters.Count == charactersBefore") && pending.Contains("!capturedMember.IsNamed"), "scenario judges no early identity while peers remain Pending");
            T.Check(pending.Contains("ReferenceEquals(captive, capturedMember.pawn?.pawn)") && pending.Contains("port.Registry.IsTemporaryReserved(captive)"), "pending window checks same Pawn and temporary Episode protection");
            T.Check(!Regex.IsMatch(pending, @"\bnew\s+KnownCharacter\b|ctx\.characters\.Add\("), "capture QA never creates its own dummy Character");
        }

        private static void CleanupWiring()
        {
            string world = Source("Diagnostics/RuntimePhysicalTests/PhysicalTestWorld.cs");
            string refusal = Body(world, "public static string DisposeRefusal(");
            T.Check(refusal.Contains(".Reserves(p)") && refusal.Contains("GroupQaRules.HasUnreleasedBinding("), "cleanup respects current registry reservation and independent unreleased durable Episode binding");
            T.Check(refusal.IndexOf("GroupQaRules.HasUnreleasedBinding(", StringComparison.Ordinal) < refusal.LastIndexOf("return null;", StringComparison.Ordinal), "durable Episode guard executes before disposal authorization");
            string dispose = Body(world, "public static bool TryDispose(");
            int guard = dispose.IndexOf("DisposeRefusal(", StringComparison.Ordinal), discard = dispose.IndexOf("p.Discard(true)", StringComparison.Ordinal);
            T.Check(guard >= 0 && discard > guard && dispose.Contains("if (refusal != null) return false;"), "actual discard cannot bypass the cleanup refusal guards");
        }

        private static void ReadOnlyVerifiers()
        {
            string scenarios = Source("Diagnostics/RuntimePhysicalTests/PhysicalGroupScenarios.cs");
            foreach (string type in new[] { "public sealed class Phyx030GroupVerify : PhysicalRun", "public sealed class Phyx032RetentionVerify : PhysicalRun" })
            {
                string verifier = Body(scenarios, type);
                T.Check(!Regex.IsMatch(verifier, @"\b(?:Reconcile|ReconcileHeld|Materialize|PlanGroup|Discard|Destroy|ExitMap|CapturedBy|NextId|Rebuild)\s*\("), "loaded verifier drives no reconciliation, placement, deletion, creation or registry rebuild: " + type);
                T.Check(!verifier.Contains("GenSpawn") && !verifier.Contains("ctx.characters.Add("), "loaded verifier changes no vanilla/Network identity: " + type);
                T.Check(verifier.Contains(".Audit()") && verifier.Contains(".AuditTemporary()"), "loaded verifier checks both binding categories without repairing them: " + type);
            }
            string group = Body(scenarios, "public sealed class Phyx030GroupVerify : PhysicalRun");
            T.Check(group.Contains("GroupQaRules.LatestCheckpoint(ctx.episodes.episodes)"), "save verification chooses latest explicit group/succession checkpoint with ambiguity refusal");
            T.Check(group.Contains("VerifyLoaded()") && group.Contains("VerifyTerminal()") && group.Contains("selected.IsComplete"), "verifier handles already-completed load and pending production progression without forcing outcomes");
        }

        private static void RetentionWiring()
        {
            string scenarios = Source("Diagnostics/RuntimePhysicalTests/PhysicalGroupScenarios.cs");
            string build = Body(scenarios, "public sealed class Phyx032RetentionBuild : GroupRun");
            T.Check(build.Contains("GroupQaRules.RetentionRequestSize(count, target)") && build.Contains("fixtures >= 150"), "live retention pressure uses bounded soft-target requests with finite fixture count");
            T.Check(!Regex.IsMatch(build, @"\b(?:Discard|Destroy)\s*\("), "retention pressure never deletes identities to improve the count");
            string observation = Body(scenarios, "public static class RetentionObservation");
            T.Check(observation.Contains("sample.Count < 300") && observation.Contains("repeat < 100") && observation.Contains("registry.Reserves(pawn)"), "lookup measurement is bounded to 300 retained samples and 100 repeats");
            T.Check(observation.Contains("Stopwatch.StartNew()") && observation.Contains("Elapsed.TotalMilliseconds"), "runtime cost notes report an actual local timing measurement");
            T.Check(observation.Contains("TPS") && observation.Contains("save bytes") && observation.Contains("manual owner observations, pending until recorded"), "unmeasured TPS/save-size observations stay explicitly pending for the owner");
            T.Check(build.Contains("count above 150 is never itself a failure"), "the soft retention region is not an identity-loss ceiling");
        }
    }
}
