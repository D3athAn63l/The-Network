using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TheNetwork.Diagnostics.RuntimePhysicalTests;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Tests
{
    /// <summary>Retained admission and dwell state rules; ordinary real map/Lord/social ticks still require owner runtime evidence.</summary>
    public static class Phase32bRematerializationDwellTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Dwell_RetainedClassificationNeedsExistingExactBinding", RetainedClassification));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Dwell_StricterCurrentBandCannotRevokeRetainedRole", RetainedRole));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Dwell_FirstCreationStillUsesOriginalRoleSpec", FirstCreation));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Dwell_RetainedContinuityRejectsEachMissingFact", RetainedFacts));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Dwell_LifecycleReloadAndStricterBandReuseSameKnownPawn", LifecycleRevisit));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Dwell_OnlyCompletesAfter240OrdinaryGameTicks", DwellBoundary));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Dwell_BrokenInvariantAbortsEvenAtCompletionBoundary", DwellFailure));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Dwell_FactsAndEvaluationAreReadOnly", ImmutableFacts));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Dwell_RuntimeCreationAndRetainedChecksStaySeparate", RuntimeRoleWiring));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Dwell_Only026AndFirst027WaitBeforeVanillaExit", RuntimeDwellWiring));
        }

        private static string Source(string relative)
        { return PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src(relative)); }

        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            T.Check(at >= 0, "source boundary exists: " + signature);
            if (at < 0) return "";
            int open = source.IndexOf('{', at), depth = 0;
            for (int i = open; i >= 0 && i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            T.Check(false, "source boundary closes: " + signature);
            return "";
        }

        private static RoleCandidate Medic()
        {
            RoleCandidate candidate = new RoleCandidate
            {
                name = "Already real Medic", gender = "Female", childhood = "Actual childhood", adulthood = "Actual adulthood",
                xenotype = "Actual xenotype", bioAgeTicks = 30, genes = 2, hediffs = 1
            };
            candidate.skills["Medicine"] = new SkillFacts { levelBase = 3, aptitude = 0, passion = 2 };
            candidate.traits.Add("ActualTrait");
            return candidate;
        }

        private static GroupRetainedFacts Retained(bool[] flags = null)
        {
            if (flags == null) flags = Enumerable.Repeat(true, 9).ToArray();
            return new GroupRetainedFacts(flags[0], flags[1], flags[2], flags[3], flags[4], flags[5], flags[6], flags[7], flags[8]);
        }

        private static GroupDwellFacts Dwell(bool[] flags = null)
        {
            if (flags == null) flags = Enumerable.Repeat(true, 14).ToArray();
            return new GroupDwellFacts(flags[0], flags[1], flags[2], flags[3], flags[4], flags[5], flags[6],
                flags[7], flags[8], flags[9], flags[10], flags[11], flags[12], flags[13]);
        }

        private static void RetainedClassification()
        {
            T.Check(GroupQaRules.HasRetainedPawn(7, 71001, 71001), "existing named person's exact durable bound Pawn is retained");
            foreach (int[] values in new[]
            {
                new[] { 7, 0, 0 }, new[] { 0, 71001, 71001 }, new[] { 7, 71001, 71002 },
                new[] { 7, 71001, 0 }, new[] { -7, 71001, 71001 }, new[] { 7, -1, -1 }
            })
                T.Check(!GroupQaRules.HasRetainedPawn(values[0], values[1], values[2]), "unknown/anonymous/mismatching binding is not historical admission: " + string.Join(",", values));
            T.Check(!GroupQaRules.HasRetainedPawn(7, 0, 0), "initially unbound named leader still needs authoritative first creation");
        }

        private static void RetainedRole()
        {
            RoleCandidate pawnTruth = Medic();
            RoleSpec original = RoleRules.SpecFor(OperationalRole.Medic, ExperienceBand.Green);
            RoleSpec stricter = RoleRules.SpecFor(OperationalRole.Medic, ExperienceBand.Seasoned);
            T.Check(GroupQaRules.CreationPlacementHolds(original, pawnTruth), "original low-band Medic was valid when first created");
            T.Check(!RoleRules.Verify(stricter, pawnTruth).holds, "counterfactual today's stricter skill floor would reject this same already-real Medic");
            string history = pawnTruth.IdentityKey();
            int medicine = pawnTruth.skills["Medicine"].levelBase;
            T.Check(GroupQaRules.RetainedPlacementHolds(Retained()), "durable role survives stricter current capability when continuity holds");
            T.Eq(medicine, pawnTruth.skills["Medicine"].levelBase, "continued admission does not correct a skill again");
            T.Eq(history, pawnTruth.IdentityKey(), "continued admission leaves actual identity/history untouched");
            ParameterInfo[] parameters = typeof(GroupQaRules).GetMethod("RetainedPlacementHolds").GetParameters();
            T.Check(parameters.Length == 1 && parameters[0].ParameterType == typeof(GroupRetainedFacts), "retained admission accepts no current capability band or role spec");
        }

        private static void FirstCreation()
        {
            RoleCandidate candidate = Medic();
            RoleSpec captured = RoleRules.SpecFor(OperationalRole.Medic, ExperienceBand.Green);
            RoleSpec later = RoleRules.SpecFor(OperationalRole.Medic, ExperienceBand.Veteran);
            T.Check(GroupQaRules.CreationPlacementHolds(captured, candidate), "new candidate checked against captured creation-time spec");
            T.Check(!GroupQaRules.CreationPlacementHolds(later, candidate), "new candidate never bypasses a genuinely stricter first-creation requirement");
            candidate.skills["Medicine"].totallyDisabled = true;
            T.Check(!GroupQaRules.CreationPlacementHolds(captured, candidate), "hard first-generation capability rule is still authoritative at low band");
            T.Check(!GroupQaRules.CreationPlacementHolds(null, candidate) && !GroupQaRules.CreationPlacementHolds(captured, null), "unknown new-candidate/spec facts refuse first-generation acceptance");
        }

        private static void RetainedFacts()
        {
            T.Check(GroupQaRules.RetainedPlacementHolds(Retained()), "all required retained continuity facts hold");
            T.Check(!GroupQaRules.RetainedPlacementHolds(null), "unknown retained facts do not assert continuity");
            for (int broken = 0; broken < 9; broken++)
            {
                bool[] flags = Enumerable.Repeat(true, 9).ToArray();
                flags[broken] = false;
                T.Check(!GroupQaRules.RetainedPlacementHolds(Retained(flags)), "retained continuity fails on required invariant " + broken);
            }
        }

        private static void LifecycleRevisit()
        {
            TestNet net = new TestNet(32417);
            NetworkActor actor = new NetworkActor { id = new ActorId(net.ids.NextId()), kind = ActorKind.Organization, seed = 123,
                foundedTick = net.clock.Now, name = NameSnapshot.Org("Retained role fixture") };
            actor.Add(new ContractorProfile { specialties = new List<string> { "escort", "medical" } });
            actor.Add(new ContractorSimulation { skill = 0.3f });
            OrganizationProfile org = new OrganizationProfile { capacity = 7 };
            org.tiers.Add(new TierCount(Tier.Regular, 4));
            KnownCharacter leader = new KnownCharacter { id = new CharacterId(net.ids.NextId()), org = actor.id,
                role = CharacterRole.Leader, opRole = OperationalRole.Leader, createdTick = actor.foundedTick, name = NameSnapshot.Person("Existing", null, "Leader") };
            org.leader = leader.id;
            org.knownMembers.Add(leader.id);
            actor.Add(org);
            net.ctx.actors.Add(actor);
            net.ctx.characters.Add(leader);
            net.physical.playerVisiblePlacement = true;
            PhysicalEpisode first;
            EpisodeRequest request = Request(actor);
            T.Check(net.ctx.Lifecycle.PlanGroup(request, new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out first).ok, "first anonymous Medic seat planned");
            T.Eq(1, net.ctx.Lifecycle.Materialize(first), "first candidate physically materialized");
            ExperienceBand creationBand = net.physical.requests[0].capability;
            T.Eq(ExperienceBand.Green, creationBand, "real first request uses original low capability");
            RoleCandidate physicalSkills = Medic();
            T.Check(GroupQaRules.CreationPlacementHolds(RoleRules.SpecFor(OperationalRole.Medic, creationBand), physicalSkills), "candidate skill fixture satisfies original creation spec");
            PawnRef binding = first.members[0].pawn;
            object token = net.physical.TokenOf(binding);
            net.physical.ExitNormally(binding, 19);
            T.Check(net.ctx.Lifecycle.Reconcile(first, "retained fixture first return") && first.members[0].IsNamed, "real terminal lifecycle promotes same anonymous Medic");
            CharacterId medicId = first.members[0].character;
            ActorId actorId = actor.id;
            PhysicalLifecycleTests.SaveLoad(net);
            actor = net.ctx.actors.Get(actorId);
            KnownCharacter medic = net.ctx.characters.Get(medicId);
            actor.Get<ContractorSimulation>().skill = 1f;
            ExperienceBand currentBand = ContractorService.Experience(actor);
            T.Check(RoleRules.FloorFor(currentBand) > RoleRules.FloorFor(creationBand), "organization's mutable capability changed to stricter current band");
            T.Check(!RoleRules.Verify(RoleRules.SpecFor(medic.opRole, currentBand), physicalSkills).holds, "old detail would fail today's stricter admission floor");
            int creates = net.physical.creates, requests = net.physical.requests.Count;
            string history = physicalSkills.IdentityKey();
            net.clock.Now += 100;
            PhysicalEpisode second;
            T.Check(net.ctx.Lifecycle.PlanGroup(Request(actor), new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out second).ok, "remembered Medic selected despite changed organization capability");
            T.Eq(medicId, second.members[0].character, "same durable CharacterId selected");
            T.Eq(1, net.ctx.Lifecycle.Materialize(second), "same retained Medic rematerializes under stricter current band");
            T.Eq(creates, net.physical.creates, "no replacement Pawn is generated");
            T.Eq(requests, net.physical.requests.Count, "rematerialization issues no new projection request");
            T.Check(ReferenceEquals(token, net.physical.TokenOf(second.members[0].pawn)) && second.members[0].pawn.thingIdNumber == binding.thingIdNumber, "exact same physical token/binding survives real Scribe load and rematerialization");
            T.Eq(OperationalRole.Medic, medic.opRole, "persisted operational role is never reprojected");
            T.Eq(3, physicalSkills.skills["Medicine"].levelBase, "low original skill fixture is never corrected to today's floor");
            T.Eq(history, physicalSkills.IdentityKey(), "stricter rematerialization leaves established physical history untouched");
        }

        private static EpisodeRequest Request(NetworkActor actor)
        {
            return new EpisodeRequest { actor = actor.id, purposeKey = "RetainedRoleRegression", mapId = 73,
                where = new TileRef { tileId = 19 }, cause = new EpisodeCause { devKey = "Phys32bRetainedRoleFixture" } };
        }

        private static void DwellBoundary()
        {
            T.Eq(240, GroupQaRules.MaterializationDwellTicks, "single stable owner-observable dwell duration");
            GroupDwellFacts facts = Dwell();
            T.Eq(GroupDwellResult.Wait, GroupQaRules.EvaluateDwell(100, 100, facts), "dwell cannot advance immediately");
            T.Eq(GroupDwellResult.Wait, GroupQaRules.EvaluateDwell(100, 339, facts), "239 ordinary game ticks still waits");
            T.Eq(GroupDwellResult.Complete, GroupQaRules.EvaluateDwell(100, 340, facts), "exactly 240 ordinary game ticks completes");
            T.Eq(GroupDwellResult.Complete, GroupQaRules.EvaluateDwell(100, 400, facts), "late frame may complete after bounded interval");
            T.Eq(GroupDwellResult.Wait, GroupQaRules.EvaluateDwell(0, 239, facts), "zero game-origin tick is valid but still bounded");
            T.Eq(GroupDwellResult.Complete, GroupQaRules.EvaluateDwell(int.MaxValue - 240, int.MaxValue, facts), "valid high game ticks do not overflow elapsed comparison");
        }

        private static void DwellFailure()
        {
            for (int broken = 0; broken < 14; broken++)
            {
                bool[] flags = Enumerable.Repeat(true, 14).ToArray();
                flags[broken] = false;
                T.Eq(GroupDwellResult.Invalid, GroupQaRules.EvaluateDwell(100, 100, Dwell(flags)), "lost required ownership/protection fact aborts immediately: " + broken);
                T.Eq(GroupDwellResult.Invalid, GroupQaRules.EvaluateDwell(100, 340, Dwell(flags)), "completion time never masks broken required fact: " + broken);
            }
            T.Eq(GroupDwellResult.Invalid, GroupQaRules.EvaluateDwell(100, 340, null), "unknown dwell observations are not a pass");
            T.Eq(GroupDwellResult.Invalid, GroupQaRules.EvaluateDwell(-1, 340, Dwell()), "unknown start tick refuses dwell");
            T.Eq(GroupDwellResult.Invalid, GroupQaRules.EvaluateDwell(100, -1, Dwell()), "unknown current tick refuses dwell");
            T.Eq(GroupDwellResult.Invalid, GroupQaRules.EvaluateDwell(100, 99, Dwell()), "regressing game tick refuses dwell");
        }

        private static void ImmutableFacts()
        {
            foreach (Type type in new[] { typeof(GroupDwellFacts), typeof(GroupRetainedFacts) })
            {
                FieldInfo[] flags = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
                T.Eq(type == typeof(GroupDwellFacts) ? 14 : 9, flags.Length, "explicit invariant flags: " + type.Name);
                T.Check(flags.All(f => f.IsInitOnly && f.FieldType == typeof(bool)), "observation facts are readonly bool data: " + type.Name);
            }
            GroupDwellFacts facts = Dwell();
            bool[] before = typeof(GroupDwellFacts).GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => (bool)f.GetValue(facts)).ToArray();
            for (int tick = 100; tick <= 340; tick++) GroupQaRules.EvaluateDwell(100, tick, facts);
            T.Check(before.SequenceEqual(typeof(GroupDwellFacts).GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => (bool)f.GetValue(facts))), "dwell evaluation writes no observation facts");
            string rules = Source("Diagnostics/RuntimePhysicalTests/PhysicalGroupTestRules.cs");
            string evaluate = Body(rules, "public static GroupDwellResult EvaluateDwell(");
            T.Check(!Regex.IsMatch(evaluate, @"\b(?:Verse|RimWorld|TickManager|Thread|Sleep|GenSpawn|Reconcile|ApplyCorrection)\b"), "pure dwell evaluator drives no vanilla ticks, repair, skills or outcomes");
        }

        private static void RuntimeRoleWiring()
        {
            string scenarios = Source("Diagnostics/RuntimePhysicalTests/PhysicalGroupScenarios.cs");
            string placement = Body(scenarios, "protected StepResult PlaceGroup(");
            int materialize = placement.IndexOf("lc.Materialize(e)", StringComparison.Ordinal);
            int plan = placement.IndexOf("lc.PlanGroup(", StringComparison.Ordinal);
            T.Check(placement.IndexOf("HasRetainedPawn(", StringComparison.Ordinal) >= 0 && placement.IndexOf("HasRetainedPawn(", StringComparison.Ordinal) < materialize, "retained classification captures existing exact binding before Materialize");
            T.Check(plan >= 0 && placement.IndexOf("RoleRules.SpecFor(", StringComparison.Ordinal) > plan && placement.IndexOf("RoleRules.SpecFor(", StringComparison.Ordinal) < materialize, "new-candidate creation spec is frozen after Plan and before physical projection");
            T.Check(placement.Contains("RetainedPlacementHolds(") && placement.Contains("CreationPlacementHolds("), "actual runtime uses separate retained/new admission helpers");
            T.Check(!placement.Contains("RoleRules.Verify("), "runtime placement never reapplies current-band Verify to every bound member");
            string admission = placement.Substring(placement.IndexOf("foreach (PlacementBaseline baseline in placement)", StringComparison.Ordinal));
            string retainedAdmission = Body(admission, "if (baseline.retained)");
            T.Check(retainedAdmission.Contains("RetainedPlacementHolds(") && !Regex.IsMatch(retainedAdmission, @"\b(?:CreationPlacementHolds|RoleRules|SpecFor|Experience|ApplyCorrection)\b"), "retained runtime branch cannot indirectly reintroduce current-band first-generation admission");
            string rules = Source("Diagnostics/RuntimePhysicalTests/PhysicalGroupTestRules.cs");
            string retained = Body(rules, "public static bool RetainedPlacementHolds(");
            T.Check(!Regex.IsMatch(retained, @"\b(?:RoleRules|RoleSpec|ExperienceBand|ContractorService|ApplyCorrection)\b"), "retained helper has no mutable capability admission or correction");
            string creation = Body(rules, "public static bool CreationPlacementHolds(");
            T.Check(creation.Contains("RoleRules.Verify("), "first-generation helper retains authoritative role Verify");
            string lifecycle = Body(Source("Domain/Physical/PhysicalLifecycleService.cs"), "private bool Bind(");
            int firstCreation = lifecycle.IndexOf("PawnRef made = Port.Create(", StringComparison.Ordinal);
            string retainedBind = firstCreation < 0 ? "" : lifecycle.Substring(0, firstCreation);
            T.Check(retainedBind.Contains("m.pawn = c.pawn.Copy();") && !Regex.IsMatch(retainedBind, @"\b(?:RoleRules|ApplyCorrection|SpecFor)\b"), "production retained binding reuses existing Pawn without role re-projection");
        }

        private static void RuntimeDwellWiring()
        {
            string scenarios = Source("Diagnostics/RuntimePhysicalTests/PhysicalGroupScenarios.cs");
            string first = Body(scenarios, "public sealed class Phyx026SmallFirst : GroupRun");
            string second = Body(scenarios, "public sealed class Phyx027SmallSecond : GroupRun");
            foreach (string run in new[] { first, second })
            {
                int dwell = run.IndexOf("DwellGroup", StringComparison.Ordinal), exit = run.IndexOf("ExitPeers()", StringComparison.Ordinal);
                T.Check(dwell >= 0 && exit > dwell, "bounded dwell runs before first vanilla ExitMap transition");
                T.Eq(1, Regex.Matches(run, @"\bDwellGroup\b").Count, "only first materialization/rematerialization waits; final full repeat stays quick");
            }
            foreach (string type in new[] { "public sealed class Phyx028LargePresence : GroupRun", "public class GroupCaptureRun : GroupRun", "public sealed class Phyx030GroupSave : GroupCaptureRun", "public sealed class Phyx031RoleSuccession : GroupRun", "public sealed class Phyx032RetentionBuild : GroupRun" })
                T.Check(!Body(scenarios, type).Contains("DwellGroup"), "correction adds no broad dwell expansion: " + type);
            string dwellBody = Body(scenarios, "protected StepResult DwellGroup(");
            T.Check(dwellBody.Contains("EvaluateDwell(") && dwellBody.Contains("StepResult.Wait") && dwellBody.Contains("StepResult.Abort"), "runtime step waits on pure policy and fails closed on bad observations");
            T.Check(!Regex.IsMatch(dwellBody, @"\b(?:Sleep|TickSingle|DoSingleTick|Teleport|ExitMap|GenSpawn|ApplyCorrection)\s*\("), "dwell does not spin ticks or move/repair Pawns to satisfy checks");
            string observation = Body(scenarios, "private GroupDwellFacts ObserveDwell(");
            foreach (string required in new[] { "Registry.Reserves", "IsActualFree", "whereMapId", "releaseApplied", "seatRole" })
                T.Check(observation.Contains(required), "dwell reads real binding/map/protection invariant: " + required);
            T.Check(!Regex.IsMatch(observation, @"\b(?:GenSpawn|ExitMap|ApplyCorrection|Reconcile|Kill|Destroy|Discard)\s*\("), "dwell observation never repairs its physical subject");
        }
    }
}
