using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TheNetwork.Diagnostics.RuntimePhysicalTests;
using TheNetwork.Diagnostics.RuntimeTests;
using TheNetwork.Diagnostics.RuntimeTests.Suites;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Settings;
using TheNetwork.Core;
using TheNetwork;
using TheNetwork.Persist;

namespace TheNetwork.Tests
{
    /// <summary>
    /// The Phase 3.1 runtime-QA correction pass, part 2 (PR #10): the first projection's PawnKind eligibility (Fix 2), the operational role of an
    /// embodied individual that predates the role field (Fix 3), and the three harness corrections (RT-PHYX-002/015 pass counters, RT-PHYX-009
    /// phase timing, RT-PHYX-010 labels) pinned against the PRODUCTION policies they must agree with. Part 1 is <see cref="Phase31QaTests"/>.
    /// </summary>
    public static class Phase31QaPolicyTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix2_GenericPoolOnly_SpecialPurposeKindsAreRefusedByWhatTheyCarry", GenericPoolOnly));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix2_AnAttractiveGlobalKindNeverLeaksIn", NoAttractiveLeak));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix2_ModdedGenericKindsRemainPossible", ModdedGeneric));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix2_NoGenericKindMeansACleanAbortBeforeBinding", NoGenericKind));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix2_RoleStillHoldsFromAWeakGenericKind", RoleHoldsFromGeneric));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix2_RematerializationNeverChangesTheKind", RematerializationKeepsPawn));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix2_Scan_NoGlobalScanNoBlacklistNoNamedKind", ScanKindSelection));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix3_LegacySoloWithUnsetRoleGetsOneDeterministicRoleBeforeProjection", LegacySolo));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix3_AFixerIsOutsideThePhase31SoloContractorPathAndIsLeftUntouched", FixerOutsideScope));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix3_OnlyAnActualNpcSoloContractorQualifies", PredicateMatrix));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix3_ThePhysicalTestPickerNeverSelectsAFixerAndStaysDeterministic", PickerExcludesFixers));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix3_TheCompatibilityPassSkipsFixersAndStillRepairsLegacyContractors", PassSkipsFixers));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix3_ExistingIdentityFactsAreNeverRewrittenOrCleared", NeverRewrittenOrCleared));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix3_RoleIsDeterministicAndIndependentOfEpisodeTimeFameAndMap", RoleIndependence));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix3_AStoredRoleIsNeverOverwrittenAndABoundPersonNeverReconstructed", RoleNeverOverwritten));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix3_Scan_DerivationReadsOriginFactsOnly", ScanRoleDerivation));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix4_ReturnedReleaseContainsNoPassToWorldAction", ReturnedHasNoPass));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix4_NormalReturnPassesSkipsAndRefusesNothing", NormalReturnCounters));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix4_Scan_Rt002AndRt015AssertZeroNotAnIncrement", ScanPassAssertions));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix5_UnsupportedCustody_QuarantineThenVanillaClearsItAndTheEpisodeReturns", UnsupportedThenCleared));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix5_Scan_Rt009CapturesIntermediateEvidenceBeforeTheMapIsRemoved", ScanRt009));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix6_Rt010LabelsAreFrontLoadedAndTheFamilyIdIsStable", Rt010Labels));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix6_VerifierIsReadOnlyAndTheLoadClearsTheArm", Rt010VerifierReadOnly));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Scan_NoHarmonyNoPhase32NoSaveFormatBump", ScanGlobals));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Docs_RuntimeQaIsRecordedAndTheLoadOrderIsRightEverywhere", DocsConsistent));
        }

        // ================================================================== helpers

        private static NetworkActor Solo(TestNet n, string id) { return PhysicalLifecycleTests.Make(n, ContractorForm.Solo, id); }

        private static KnownCharacter Self(TestNet n, NetworkActor a) { return PhysicalLifecycleTests.Self(n, a); }

        private static string Code(string rel) { return PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src(rel)); }

        private static string Body(string code, string from, string to)
        {
            int a = code.IndexOf(from, StringComparison.Ordinal);
            T.Check(a >= 0, "found " + from);
            if (a < 0) return "";
            int b = code.IndexOf(to, a + from.Length, StringComparison.Ordinal);
            return b < 0 ? code.Substring(a) : code.Substring(a, b - a);
        }

        private static MemberKindFacts Generic(string name, bool basic = false, bool fighter = true, bool ranged = false, bool melee = false, float power = 35f)
        {
            return new MemberKindFacts { defName = name, source = basic ? "basic member kind of F" : "Combat group of F", basic = basic, humanlike = true, toolUser = true, native = true, fighter = fighter, ranged = ranged, melee = melee, combatPower = power };
        }

        // ================================================================== Fix 2: generic member pool

        private static void GenericPoolOnly()
        {
            T.Check(GenericMemberPolicy.Reject(Generic("Plain", true)) == null, "a plain member kind is generic");
            // One row per kind of special-purpose content, each read from a vanilla PawnKindDef field, none from a name.
            Action<string, Action<MemberKindFacts>> special = (why, make) =>
            {
                MemberKindFacts k = Generic("Special", false, true, true, false, 80f);
                make(k);
                string reason = GenericMemberPolicy.Reject(k);
                T.Check(reason != null, "refused: " + why + " (" + reason + ")");
                T.Check(GenericMemberPolicy.Chain(new List<MemberKindFacts> { Generic("Plain", true), k }, RoleClass.Ranged, 3).IndexOf("Special") < 0, "never in the chain: " + why);
            };
            special("not a humanlike person", k => k.humanlike = false);
            special("not a tool user", k => k.toolUser = false);
            special("belongs to another faction", k => k.native = false);
            special("a player-faction kind", k => k.playerKind = true);
            special("a faction leader kind", k => k.factionLeader = true);
            special("a boss kind", k => k.boss = true);
            special("a mutant kind", k => k.mutant = true);
            special("requires a royal title", k => k.titled = true);
            special("a trader kind", k => k.trader = true);
            special("hostile to everything", k => k.hostileToAll = true);
            special("fixed backstories (an ancient, a cultist)", k => k.fixedBackstory = true);
            special("built-in conditions", k => k.builtInConditions = true);
            special("built-in abilities", k => k.builtInAbilities = true);
            special("forced traits", k => k.forcedTraits = true);
            special("forces a non-baseline xenotype", k => k.forcedXenotype = true);
            T.Check(GenericMemberPolicy.Reject(null) != null && GenericMemberPolicy.Reject(new MemberKindFacts()) != null, "no kind and an unnamed kind are refused");
            // The pool is exactly the generic members offered, basic first, deduplicated.
            List<MemberKindFacts> offered = new List<MemberKindFacts>
            {
                Generic("Basic", true), Generic("Guard", false, true, true), Generic("Hunter", false, true, true), Generic("Basic", true),
                new MemberKindFacts { defName = "Boss", humanlike = true, toolUser = true, native = true, boss = true, fighter = true, combatPower = 200f }
            };
            List<string> chain = GenericMemberPolicy.Chain(offered, RoleClass.Ranged, 3);
            T.Eq("Basic", chain[0], "the faction's basic member kind comes first (the primary source)");
            T.Eq(3, chain.Count, "basic plus the best two other generic members, never more than " + GenericMemberPolicy.MaxChain);
            T.Check(chain.IndexOf("Boss") < 0, "the boss is not in the pool");
            T.Eq(1, GenericMemberPolicy.Rejections(offered).Count, "and the refusal is explained (1 refused)");
            T.Eq(3, GenericMemberPolicy.Eligible(offered).Count, "a kind offered twice is judged once");
        }

        private static void NoAttractiveLeak()
        {
            // The owner's run: the global ranking pulled in kinds whose combat power and gear 'fit the role'. A kind that is not in the faction's
            // generic pool is simply not a candidate, however attractive; and an attractive kind that carries special content is refused.
            MemberKindFacts basic = Generic("Plain", true, false, false, false, 35f);
            MemberKindFacts champion = Generic("Champion", false, true, true, true, 65f); // perfect for a Marksman at tier 1: generic content
            MemberKindFacts royal = Generic("RoyalShot", false, true, true, false, 65f);
            royal.titled = true;
            MemberKindFacts ancient = Generic("AncientShot", false, true, true, false, 65f);
            ancient.fixedBackstory = true;
            MemberKindFacts foreign = Generic("OtherFactionShot", false, true, true, false, 65f);
            foreign.native = false;
            List<string> chain = GenericMemberPolicy.Chain(new List<MemberKindFacts> { basic, royal, ancient, foreign, champion }, RoleClass.Ranged, 1);
            T.Eq("Plain", chain[0], "the basic member leads even when another kind scores higher");
            T.Check(chain.Contains("Champion"), "an attractive kind that IS generic content, offered by the faction, may follow the basic one");
            T.Check(!chain.Contains("RoyalShot") && !chain.Contains("AncientShot") && !chain.Contains("OtherFactionShot"), "an equally attractive kind carrying special content, or another faction's, never does");
            // Score only ORDERS the generic pool: a worse-scoring generic kind is still in, a better-scoring special one is still out.
            T.Check(GenericMemberPolicy.Score(royal, RoleClass.Ranged, 1) >= GenericMemberPolicy.Score(basic, RoleClass.Ranged, 1), "the refused kinds really are the 'attractive' ones");
            // Deterministic: the same offer in any order gives the same chain.
            List<MemberKindFacts> a = new List<MemberKindFacts> { basic, champion, Generic("Alpha", false, true, true), Generic("Beta", false, true, true) };
            List<MemberKindFacts> b = new List<MemberKindFacts> { Generic("Beta", false, true, true), Generic("Alpha", false, true, true), champion, basic };
            T.Eq(string.Join(",", GenericMemberPolicy.Chain(a, RoleClass.Ranged, 2).ToArray()), string.Join(",", GenericMemberPolicy.Chain(b, RoleClass.Ranged, 2).ToArray()), "the chain does not depend on the order the faction lists its kinds");
        }

        private static void ModdedGeneric()
        {
            // A modded race and faction are judged by what their kinds ARE, never by a name.
            MemberKindFacts colonist = new MemberKindFacts { defName = "ModRace_Colonist", source = "basic member kind of ModFaction", basic = true, humanlike = true, toolUser = true, native = true, fighter = false, combatPower = 30f };
            MemberKindFacts warden = new MemberKindFacts { defName = "ModRace_Warden", source = "Combat group of ModFaction", humanlike = true, toolUser = true, native = true, fighter = true, melee = true, combatPower = 70f };
            MemberKindFacts shaman = new MemberKindFacts { defName = "ModRace_Shaman", source = "Peaceful group of ModFaction", humanlike = true, toolUser = true, native = true, builtInAbilities = true };
            List<string> chain = GenericMemberPolicy.Chain(new List<MemberKindFacts> { colonist, warden, shaman }, RoleClass.Melee, 2);
            T.Eq("ModRace_Colonist", chain[0], "a modded basic member kind leads");
            T.Check(chain.Contains("ModRace_Warden"), "a modded generic fighter kind is possible");
            T.Check(!chain.Contains("ModRace_Shaman"), "a modded kind with built-in abilities is not generic, by what it carries");
            // Non-human races pass when the race is humanlike (intelligence), by the same fields.
            MemberKindFacts alien = new MemberKindFacts { defName = "Alien_Member", basic = true, humanlike = true, toolUser = true, native = true };
            T.Check(GenericMemberPolicy.Reject(alien) == null, "a modded humanlike race is eligible");
            MemberKindFacts beast = new MemberKindFacts { defName = "Beast", basic = true, humanlike = false, toolUser = false, native = true };
            T.Check(GenericMemberPolicy.Reject(beast) != null, "an animal is not");
        }

        private static void NoGenericKind()
        {
            // The faction offers only special content: the pool is EMPTY, which the projection turns into a clean failure before anything is generated.
            MemberKindFacts boss = Generic("Boss", true);
            boss.boss = true;
            MemberKindFacts leader = Generic("Leader");
            leader.factionLeader = true;
            T.Eq(0, GenericMemberPolicy.Chain(new List<MemberKindFacts> { boss, leader }, RoleClass.Generalist, 1).Count, "no generic member offered: an empty chain");
            T.Eq(0, GenericMemberPolicy.Chain(new List<MemberKindFacts>(), RoleClass.Generalist, 1).Count, "nothing offered: an empty chain");
            T.Eq(0, GenericMemberPolicy.Chain(null, RoleClass.Generalist, 1).Count, "null: an empty chain");
            string project = Code("Integration/Physical/PawnProjection.cs");
            string body = Body(project, "public static ProjectionResult Project(ProjectionRequest r, Faction f)", "private static ProjectionResult Done");
            int abort = body.IndexOf("if (chain.Count == 0)", StringComparison.Ordinal);
            int generate = body.IndexOf("PawnGenerator.GeneratePawn", StringComparison.Ordinal);
            T.Check(abort > 0 && generate > abort, "the projection aborts on an empty pool BEFORE it generates anything");
            T.Check(Regex.IsMatch(body, @"if \(chain\.Count == 0\)\s*\{[^}]*no special-purpose kind is borrowed[^}]*return Done\(res, sw\);"), "with a plain failure reason, and no fallback to a global kind");
            string create = Body(Code("Integration/Physical/RimWorldPhysicalWorldPort.cs"), "public PawnRef Create(ProjectionRequest request)", "public bool Resolves");
            T.Check(create.IndexOf("if (r.pawn == null)", StringComparison.Ordinal) < create.IndexOf("return new PawnRef", StringComparison.Ordinal) && create.Contains("role-constrained creation aborted, nothing bound"), "Create throws before it returns a binding: nothing is bound on a failed projection");
            string faction = Code("Integration/Physical/EncounterFactions.cs");
            T.Check(faction.Contains("FactionMemberKinds.HasGenericPool(d)"), "an encounter faction def must offer a generic member to qualify at all");
        }

        private static RoleCandidate WeakGeneric()
        {
            RoleCandidate c = new RoleCandidate();
            foreach (string s in new[] { "Shooting", "Melee", "Medicine", "Construction", "Crafting", "Social", "Intellectual", "Plants" })
                c.skills[s] = new SkillFacts { levelBase = 3, aptitude = 0, totallyDisabled = false, passion = 0 };
            return c;
        }

        private static void RoleHoldsFromGeneric()
        {
            // Role strength is the ROLE's job: a weak, generic, low-skill candidate satisfies every role and band through the ONE allowed skill raise,
            // so a faction's plain member kind is enough and no boss, royal or ancient kind is ever needed.
            int checkedRoles = 0;
            foreach (OperationalRole role in Enum.GetValues(typeof(OperationalRole)))
            {
                if (role == OperationalRole.Unset) continue;
                foreach (ExperienceBand band in new[] { ExperienceBand.Green, ExperienceBand.Experienced, ExperienceBand.Seasoned, ExperienceBand.Veteran, ExperienceBand.Elite, ExperienceBand.Legendary })
                {
                    RoleSpec spec = RoleRules.SpecFor(role, band);
                    RoleCandidate c = WeakGeneric();
                    RoleVerdict v = RoleRules.Verify(spec, c);
                    T.Check(v.holds || v.Correctable, role + "/" + band + ": a weak generic candidate holds or is correctable (" + v + ")");
                    if (v.Correctable)
                    {
                        T.Check(RoleRules.ApplyCorrection(c, v), role + "/" + band + ": the one raise applies");
                        T.Check(RoleRules.Verify(spec, c).holds, role + "/" + band + ": and the role then holds");
                    }
                    checkedRoles++;
                }
            }
            T.Check(checkedRoles > 60, "every role at every band was checked (" + checkedRoles + ")");
            // Hard capabilities are still rejected, never fixed: an incapable candidate does not hold, whatever kind it came from.
            RoleCandidate incapable = WeakGeneric();
            incapable.disabledWorkTags.Add("Violent");
            T.Check(!RoleRules.Verify(RoleRules.SpecFor(OperationalRole.Rifleman, ExperienceBand.Veteran), incapable).holds && !RoleRules.Verify(RoleRules.SpecFor(OperationalRole.Rifleman, ExperienceBand.Veteran), incapable).Correctable, "an incapability is identity: rejected, never corrected");
        }

        private static void RematerializationKeepsPawn()
        {
            TestNet n = new TestNet(9731);
            NetworkActor a = Solo(n, "kind");
            KnownCharacter c = Self(n, a);
            PhysicalEpisode e1 = PhysicalLifecycleTests.Begin(n, a, new[] { c });
            PawnRef first = c.pawn.Copy();
            T.Eq(1, n.physical.creates, "the first materialization created ONE pawn (one first projection, one kind choice)");
            n.physical.ExitNormally(c.pawn, 80);
            PhysicalLifecycleTests.L(n).Reconcile(e1, "exit");
            T.Check(e1.IsComplete, "the first episode completed");
            n.clock.Now += 300000;
            PhysicalEpisode e2 = PhysicalLifecycleTests.Begin(n, a, new[] { c });
            T.Eq(1, n.physical.creates, "the second materialization created NOTHING: the same pawn rematerializes");
            T.Eq(1, n.physical.requests.Count, "no second projection request was ever made, so no kind was ever chosen again");
            T.Check(c.pawn.SameBinding(first) && c.pawn.thingIdNumber == first.thingIdNumber, "the binding is the same pawn for life");
            T.Check(e2.members[0].state == MemberState.Present, "and it was placed again");
            string project = Code("Integration/Physical/PawnProjection.cs");
            T.Check(!project.Contains("kindDef =") && !project.Contains("kindDef=") && !Regex.IsMatch(project, @"\.kindDef\s*="), "no code path rewrites an existing pawn's kind (a later Anomaly or ideology change is real history)");
        }

        private static void ScanKindSelection()
        {
            string project = Code("Integration/Physical/PawnProjection.cs");
            foreach (string forbidden in new[] { "DefDatabase<PawnKindDef>", "PawnKindDefOf", "KindPolicy", "KindFacts", "KindChain", "Kinds()", "AllDefsListForReading" })
                T.Check(!project.Contains(forbidden), "the projection never scans or names a global kind source (" + forbidden + ")");
            T.Check(project.Contains("FactionMemberKinds.For(f?.def, spec.kindClass, r.equipmentTier)"), "the kind pool is the encounter faction's own");
            string pool = Code("Integration/Physical/FactionMemberKinds.cs");
            T.Check(pool.Contains("def.basicMemberKind") && pool.Contains("def.pawnGroupMakers") && pool.Contains("PawnGroupKindDefOf.Combat") && pool.Contains("PawnGroupKindDefOf.Peaceful"), "candidates are the faction's basic member kind and its Combat and Peaceful groups");
            T.Check(!pool.Contains("DefDatabase<PawnKindDef>.AllDefs") && !pool.Contains("DefDatabase<PawnKindDef>.AllDefsListForReading") && !pool.Contains("PawnKindDefOf"), "and nothing else");
            // No named-def blacklist or whitelist anywhere in the eligibility architecture.
            string[] named = { "Horax", "Highthrall", "Ancient", "Empire", "Yeoman", "Champion", "Trooper", "Sanguophage", "Refugee", "Beggar", "Pilgrim", "Villager", "Mercenary", "Pirate", "Tribal", "Outlander" };
            foreach (string rel in new[] { "Domain/Physical/ProjectionModel.cs", "Integration/Physical/FactionMemberKinds.cs", "Integration/Physical/PawnProjection.cs", "Integration/Physical/EncounterFactions.cs" })
            {
                string code = Regex.Replace(Code(rel), "\"[^\"]*(basic member kind|group of|group)[^\"]*\"", "\"\"");
                // The ONE permitted name: vanilla's own refugee faction as the capability-checked PREFERENCE of the encounter faction def (it still has to
                // pass Qualifies, which now requires a generic member). It is a faction preference, never a kind rule.
                if (rel.EndsWith("EncounterFactions.cs")) code = code.Replace("FactionDefOf.OutlanderRefugee", "");
                foreach (string name in named) T.Check(!code.Contains(name), rel + " names no specific kind or faction (" + name + ")");
            }
            string policy = Body(Code("Domain/Physical/ProjectionModel.cs"), "public static class GenericMemberPolicy", "public struct FirstIdentity");
            T.Check(!Regex.IsMatch(policy, @"defName\s*(==|!=)|\.defName\.Equals|case\s+""") && !policy.Contains("Blacklist") && !policy.Contains("Whitelist") && !policy.Contains("Denylist"), "the policy never branches on a def name");
            string scenario = Code("Diagnostics/RuntimePhysicalTests/PhysicalScenarios.cs");
            T.Check(scenario.Contains("r.pool.Allows(r.pawn.kindDef)") && scenario.Contains("allowed kind provenance"), "RT-PHYX-011 reports the allowed kind provenance and fails any pawn outside the pool");
            T.Check(Code("Domain/Physical/ProjectionModel.cs").Contains("Eligible(candidates)") && !project.Contains("combatPower"), "role strength is the role's job, not the kind's");
        }

        // ================================================================== Fix 3: the operational role of an individual that predates the field

        private static OperationalRole Derived(NetworkActor a) { return RoleDerivation.ForSolo(a); }

        private static void LegacySolo()
        {
            TestNet n = new TestNet(9741);
            NetworkActor a = Solo(n, "legacy");
            KnownCharacter c = Self(n, a);
            OperationalRole origin = c.opRole;
            T.Check(origin != OperationalRole.Unset, "a NEW actor gets its role eagerly (the control)");
            // The legacy fixture: an existing Solo and named person from before the role field was populated; no pawn ever bound.
            c.opRole = OperationalRole.Unset;
            T.Check(c.pawn == null || !c.pawn.IsBound, "no pawn was ever bound");
            T.Eq(1, n.ctx.Contractors.EnsureSoloRoles(), "the compatibility pass stores the missing role");
            T.Eq(origin, c.opRole, "and it is the SAME deterministic role the actor would have been given at creation (from its seed and original specialties)");
            T.Check(c.opRole != OperationalRole.Unset, "never Unset");
            T.Eq(0, n.ctx.Contractors.EnsureSoloRoles(), "idempotent: a second pass changes nothing");
            // First use without the load pass (the in-lifecycle safety net): Plan stores it BEFORE the projection is ever asked.
            c.opRole = OperationalRole.Unset;
            PhysicalEpisode e = PhysicalLifecycleTests.Begin(n, a, new[] { c });
            T.Check(c.opRole != OperationalRole.Unset, "Plan stored the role before materialization");
            T.Eq(origin, c.opRole, "the same role");
            T.Eq(1, n.physical.requests.Count, "one first projection");
            T.Eq(c.opRole, n.physical.requests[0].role, "the projection was asked for the STORED role, never an unknown one");
            T.Eq(c.opRole, e.members[0].seatRole, "and the member's seat is that role");
        }

        private static void FixerOutsideScope()
        {
            // The review finding: a Fixer is an embodied individual with a FixerProfile and NO ContractorProfile. IsSolo (individual, not an
            // organization) counts it, which let the physical tier select a Fixer and PASS without ever exercising a contractor. The previous
            // correction made the role derivation total for it, which is the wrong semantic answer: a Fixer is not a Phase 3.1 candidate and must
            // not silently receive a contractor-style role.
            TestNet n = new TestNet(9742);
            NetworkActor fixer = n.AddFixer("Standard");
            T.Check(fixer != null && fixer.Get<ContractorProfile>() == null && fixer.Has<FixerProfile>() && fixer.bindings.embodies.IsValid, "the fixture: an embodied individual with a FixerProfile and no ContractorProfile");
            KnownCharacter c = n.ctx.characters.Get(fixer.bindings.embodies);
            T.Check(ContractorService.IsSolo(fixer), "IsSolo keeps its GLOBAL meaning (an individual that is not an organization): it still counts the Fixer");
            T.Check(!ContractorService.IsNpcSoloContractor(fixer), "but a Fixer is not an NPC Solo contractor");
            T.Eq(OperationalRole.Unset, c.opRole, "its person starts with the role Unset");
            T.Eq(OperationalRole.Unset, Derived(fixer), "ForSolo invents no contractor role for a Fixer (and there is no Fixer-specific mapping)");
            T.Eq(0, n.ctx.Contractors.EnsureSoloRoles(), "the compatibility pass stores nothing for it");
            T.Eq(OperationalRole.Unset, c.opRole, "its role remains Unset");
            // Plan fails closed rather than inventing a role, and changes nothing.
            PhysicalEpisode e;
            CommandResult res = PhysicalLifecycleTests.L(n).Plan(PhysicalRuntimeSuite.Request(fixer, new[] { c }), out e);
            T.Check(!res.ok && res.reasonKey == "RoleUnderivable", "Plan refuses an embodied person whose role is Unset and not derivable (" + res.reasonKey + ")");
            T.Check(e == null && n.ctx.episodes.episodes.Count == 0, "no episode was created");
            T.Check(c.custody == CustodyState.Unmaterialized && !c.episode.IsValid && c.opRole == OperationalRole.Unset && AuthorityGate.CanSimulateAbstractly(c), "the person is exactly as before: not deployed, no episode, role still Unset, authority still abstract");
            T.Eq(0, n.physical.creates + n.physical.requests.Count, "no pawn was requested or created");
        }

        private static void PredicateMatrix()
        {
            // The REAL predicates, over real instantiated actors.
            TestNet n = new TestNet(9746);
            NetworkActor fixer = n.AddFixer("Standard");
            NetworkActor solo = Solo(n, "pm-solo");
            NetworkActor org = PhysicalLifecycleTests.Make(n, ContractorForm.Company, "pm-org");
            NetworkActor proxy = null;
            foreach (NetworkActor a in n.ctx.actors.actors) if (a.kind == ActorKind.PlayerProxy) proxy = a;
            T.Check(fixer != null && solo != null && org != null && proxy != null, "the fixtures exist (Fixer, Solo contractor, organization contractor, player proxy)");
            T.Check(!ContractorService.IsNpcSoloContractor(fixer), "Individual + FixerProfile only -> false");
            T.Check(solo.kind == ActorKind.Individual && solo.Has<ContractorProfile>() && solo.Has<ContractorSimulation>() && ContractorService.IsNpcSoloContractor(solo), "Individual + ContractorProfile + ContractorSimulation -> true");
            T.Check(ContractorService.IsNpcContractor(org) && !ContractorService.IsSolo(org) && !ContractorService.IsNpcSoloContractor(org), "an organization contractor is a contractor but not a Solo -> false for the Phase 3.1 Solo picker");
            T.Check(!ContractorService.IsNpcSoloContractor(proxy), "the player proxy -> false");
            T.Check(!ContractorService.IsNpcSoloContractor(null), "null -> false");
            // Structural, not an id or a kind guess: a contractor profile without its simulation, or an individual with neither, does not qualify.
            NetworkActor half = new NetworkActor { id = new ActorId(990001), kind = ActorKind.Individual };
            half.Add(new ContractorProfile());
            T.Check(ContractorService.IsSolo(half) && !ContractorService.IsNpcContractor(half) && !ContractorService.IsNpcSoloContractor(half), "a ContractorProfile without a ContractorSimulation -> false");
            NetworkActor bare = new NetworkActor { id = new ActorId(990002), kind = ActorKind.Individual };
            T.Check(!ContractorService.IsNpcSoloContractor(bare), "an individual with no profile at all -> false");
            // The derivation follows the same boundary.
            T.Check(Derived(solo) != OperationalRole.Unset, "the Solo contractor derives a role");
            foreach (NetworkActor other in new[] { fixer, org, proxy, half, bare }) T.Eq(OperationalRole.Unset, Derived(other), "no role for " + other.kind + (other.Has<FixerProfile>() ? " (Fixer)" : ""));
            // IsSolo's own, global meaning is untouched.
            T.Check(ContractorService.IsSolo(fixer) && ContractorService.IsSolo(solo) && !ContractorService.IsSolo(org) && !ContractorService.IsSolo(proxy), "IsSolo still means 'an individual that is not an organization' for every other system that uses it");
        }

        private static void PickerExcludesFixers()
        {
            TestNet n = new TestNet(9747);
            NetworkActor fixer = n.AddFixer("Standard");   // created FIRST: the lower actor id
            NetworkActor solo = Solo(n, "pick");
            KnownCharacter cf = n.ctx.characters.Get(fixer.bindings.embodies), cs = Self(n, solo);
            T.Check(fixer.id.Value < solo.id.Value, "the Fixer has the LOWER actor id (" + fixer.id.Value + " < " + solo.id.Value + ")");
            T.Check(cf.IsAlive && cs.IsAlive && cf.status == CharacterStatus.Active && cs.status == CharacterStatus.Active && AuthorityGate.CanSimulateAbstractly(cf) && AuthorityGate.CanSimulateAbstractly(cs), "both are alive and abstractly eligible");
            // Control: the global IsSolo meaning would have offered the Fixer first (the lowest eligible id), which is the defect.
            List<NetworkActor> byId = new List<NetworkActor>(n.ctx.actors.actors);
            byId.Sort((x, y) => x.id.Value.CompareTo(y.id.Value));
            NetworkActor firstGlobal = null;
            foreach (NetworkActor a in byId) if (a.IsActive && ContractorService.IsSolo(a) && a.bindings.embodies.IsValid) { firstGlobal = a; break; }
            T.Check(ReferenceEquals(firstGlobal, fixer), "control: 'an individual that is not an organization' would have taken the Fixer first");
            NetworkActor chosen;
            string why;
            KnownCharacter picked = SoloPicker.Pick(n.ctx, SoloNeed.Fresh, null, out chosen, out why);
            T.Check(picked != null && ReferenceEquals(picked, cs) && ReferenceEquals(chosen, solo), "the Phase 3.1 picker selects the NPC Solo CONTRACTOR, not the lower-id Fixer");
            T.Check(SoloPicker.Pick(n.ctx, SoloNeed.Any, null, out chosen, out why) == cs && ReferenceEquals(chosen, solo), "SoloNeed.Any selects the contractor too");
            T.Check(SoloPicker.Pick(n.ctx, SoloNeed.Fresh, null, out chosen, out why) == cs, "and it is deterministic: the same answer every time");
            // With the contractor excluded or unavailable the picker returns NOTHING; it never falls back to the Fixer.
            HashSet<int> excluded = new HashSet<int> { cs.id.Value };
            T.Check(SoloPicker.Pick(n.ctx, SoloNeed.Fresh, excluded, out chosen, out why) == null && chosen == null && why != null, "the contractor excluded: nothing is picked (never the Fixer)");
            // The existing checks still hold (the narrowing is purely semantic): a non-abstract contractor is not offered either.
            cs.custody = CustodyState.Deployed;
            cs.episode = new EpisodeId(7);
            T.Check(SoloPicker.Pick(n.ctx, SoloNeed.Fresh, null, out chosen, out why) == null, "a contractor that is not abstractly simulatable is still refused");
            cs.custody = CustodyState.Unmaterialized;
            cs.episode = EpisodeId.None;
            cs.status = CharacterStatus.Dead;
            T.Check(SoloPicker.Pick(n.ctx, SoloNeed.Fresh, null, out chosen, out why) == null, "a dead person is still refused");
            cs.status = CharacterStatus.Active;
            T.Check(SoloPicker.Pick(n.ctx, SoloNeed.Fresh, null, out chosen, out why) == cs, "and the contractor is offered again once it is eligible");
            // A save with only Fixers has no Phase 3.1 candidate.
            TestNet only = new TestNet(9748);
            only.AddFixer("Standard");
            only.AddFixer("Quick");
            T.Check(SoloPicker.Pick(only.ctx, SoloNeed.Any, null, out chosen, out why) == null && chosen == null, "a world with only Fixers offers no Phase 3.1 Solo");
            T.Check(why != null && why.Contains("NPC Solo contractor") && why.Contains("Fixers"), "and says why in words: " + why);
            string picker = Body(Code("Diagnostics/RuntimePhysicalTests/PhysicalTestWorld.cs"), "public static class SoloPicker", "public static class RedressProbe");
            T.Check(picker.Contains("ContractorService.IsNpcSoloContractor(a)") && !picker.Contains("ContractorService.IsSolo(a)") && picker.Contains("all.Sort((a, b) => a.id.Value.CompareTo(b.id.Value))"), "the picker uses the explicit predicate and still takes the lowest id");
            foreach (string kept in new[] { "!a.IsActive", "a.bindings.embodies.IsValid", "!c.IsAlive", "c.status != CharacterStatus.Active", "AuthorityGate.CanSimulateAbstractly(c)", "ctx.Contractors.Occupied(a, OperationId.None)", "bound != (c.custody == CustodyState.Stored)", "need == SoloNeed.Fresh", "need == SoloNeed.Stored" })
                T.Check(picker.Contains(kept), "the picker still checks " + kept);
        }

        private static void PassSkipsFixers()
        {
            TestNet n = new TestNet(9749);
            NetworkActor fixer = n.AddFixer("Standard");
            NetworkActor solo = Solo(n, "legacy2");
            KnownCharacter cf = n.ctx.characters.Get(fixer.bindings.embodies), cs = Self(n, solo);
            OperationalRole origin = cs.opRole;
            T.Check(origin != OperationalRole.Unset, "control: a NEW contractor gets its role eagerly");
            cs.opRole = OperationalRole.Unset; // the legacy contractor: an existing Solo from before the role was populated
            T.Eq(OperationalRole.Unset, cf.opRole, "the Fixer is Unset too, with no pawn binding");
            T.Check((cf.pawn == null || !cf.pawn.IsBound) && (cs.pawn == null || !cs.pawn.IsBound), "neither was ever bound");
            T.Eq(1, n.ctx.Contractors.EnsureSoloRoles(), "exactly ONE role was stored: the contractor's");
            T.Eq(OperationalRole.Unset, cf.opRole, "the Fixer's opRole remains Unset");
            T.Check(cs.opRole != OperationalRole.Unset && cs.opRole == Derived(solo) && cs.opRole == origin, "the legacy contractor got the deterministic role it would have been given at creation");
            T.Eq(0, n.ctx.Contractors.EnsureSoloRoles(), "idempotent: nothing more to store");
            T.Eq(OperationalRole.Unset, cf.opRole, "the Fixer still Unset after every pass");
        }

        private static void NeverRewrittenOrCleared()
        {
            TestNet n = new TestNet(9750);
            NetworkActor fixer = n.AddFixer("Standard");
            NetworkActor solo = Solo(n, "keep2"), bound = Solo(n, "bound2");
            KnownCharacter cf = n.ctx.characters.Get(fixer.bindings.embodies), cs = Self(n, solo), cb = Self(n, bound);
            // A role an EARLIER build of this PR stored on a Fixer: harmless stale test-era data. Not cleared, not rewritten, whatever it is.
            foreach (OperationalRole stale in new[] { OperationalRole.Specialist, OperationalRole.Medic, OperationalRole.Rifleman })
            {
                cf.opRole = stale;
                n.ctx.Contractors.EnsureSoloRoles();
                T.Eq(stale, cf.opRole, "a pre-existing Fixer role (" + stale + ") is neither cleared nor rewritten, although Fixers are no longer backfilled");
            }
            // A stored contractor role is left alone (even when it differs from what the derivation would give).
            OperationalRole derived = Derived(solo);
            OperationalRole other = derived == OperationalRole.Medic ? OperationalRole.Scout : OperationalRole.Medic;
            cs.opRole = other;
            // A bound contractor is never reconstructed.
            cb.opRole = OperationalRole.Unset;
            cb.pawn = new PawnRef { pawn = null, thingIdNumber = 4242, defName = "Human", boundTick = 1 };
            T.Eq(0, n.ctx.Contractors.EnsureSoloRoles(), "nothing to store: one role is stored, one person is bound, one is a Fixer");
            T.Eq(other, cs.opRole, "the stored contractor role is unchanged");
            T.Eq(OperationalRole.Unset, cb.opRole, "the bound contractor's role is not reconstructed after the fact");
            T.Check(cb.pawn.thingIdNumber == 4242 && cb.pawn.boundTick == 1, "and its binding is untouched");
            string service = Body(Code("Domain/Contractors/ContractorService.cs"), "public int EnsureSoloRoles()", "public static int Headcount");
            T.Check(!Regex.IsMatch(service, @"opRole\s*=\s*(Physical\.)?OperationalRole\.Unset") && !service.Contains("Remove") && !service.Contains("Clear"), "the pass has no code path that clears or rewrites a role to Unset");
            T.Check(service.Contains("IsNpcSoloContractor(a)") && !service.Contains("IsSolo(a)"), "it considers only NPC Solo contractors");
            T.Eq(5, SaveMigrations.Current, "no cleanup migration and no save bump");
        }

        private static void RoleIndependence()
        {
            // Same origin facts, same role: two worlds with the same seed and template.
            TestNet n1 = new TestNet(9743), n2 = new TestNet(9743);
            NetworkActor a1 = Solo(n1, "same"), a2 = Solo(n2, "same");
            T.Eq(Derived(a1), Derived(a2), "the same seed and origin derive the same role in two separate worlds");
            KnownCharacter c = Self(n1, a1);
            OperationalRole first = Derived(a1);
            c.opRole = OperationalRole.Unset;
            n1.ctx.Contractors.EnsureSoloRoles();
            T.Eq(first, c.opRole, "stored once");
            // Nothing that happens afterwards changes it: time, fame, reputation, notability, episodes, maps.
            n1.clock.Now += 9000000;
            a1.reputation.SetBand(FameBand.Legendary);
            c.notability = 0.99f;
            T.Eq(first, Derived(a1), "re-derived after years, a fame and reputation change: the same role");
            for (int i = 0; i < 3; i++)
            {
                PhysicalEpisode e = PhysicalLifecycleTests.Begin(n1, a1, new[] { c });
                T.Eq(first, c.opRole, "episode " + (i + 1) + " (a different id, seed, time and tile) did not change the stored role");
                T.Eq(first, e.members[0].seatRole, "and the seat is that role");
                n1.physical.ExitNormally(c.pawn, 80 + i);
                PhysicalLifecycleTests.L(n1).Reconcile(e, "exit");
                T.Check(e.IsComplete, "episode " + (i + 1) + " completed");
                n1.clock.Now += 200000;
            }
            T.Eq(first, c.opRole, "the stored role never moved");
            T.Eq(1, n1.physical.creates, "and the pawn was created exactly once");
        }

        private static void RoleNeverOverwritten()
        {
            TestNet n = new TestNet(9744);
            NetworkActor a = Solo(n, "keep");
            KnownCharacter c = Self(n, a);
            OperationalRole derived = Derived(a);
            OperationalRole other = derived == OperationalRole.Medic ? OperationalRole.Scout : OperationalRole.Medic;
            c.opRole = other;
            T.Eq(0, n.ctx.Contractors.EnsureSoloRoles(), "a stored role is not a candidate for the pass");
            T.Eq(other, c.opRole, "never overwritten by the compatibility pass");
            PhysicalLifecycleTests.Begin(n, a, new[] { c });
            T.Eq(other, c.opRole, "nor by Plan");
            T.Eq(other, n.physical.requests[0].role, "the projection used the STORED role");
            // A bound person whose role is Unset (never expected: Plan stores it first) is not reconstructed after the fact.
            TestNet n2 = new TestNet(9745);
            NetworkActor b = Solo(n2, "bound");
            KnownCharacter cb = Self(n2, b);
            cb.opRole = OperationalRole.Unset;
            cb.pawn = new PawnRef { pawn = null, thingIdNumber = 4242, defName = "Human", boundTick = 1 };
            T.Eq(0, n2.ctx.Contractors.EnsureSoloRoles(), "a person who ever had a pawn bound is never given a role retroactively");
            T.Eq(OperationalRole.Unset, cb.opRole, "left exactly as stored");
        }

        private static void ScanRoleDerivation()
        {
            string model = Code("Domain/Physical/ProjectionModel.cs");
            string derive = Body(model, "public static class RoleDerivation", "public struct NamePins");
            foreach (string forbidden in new[] { "Pawn", "pawn", "reputation", "fame", "Fame", "notability", "ctx.Now", "clock", "Clock", "episode", "Episode", "map", "Map", "Rand", "NetRng", "skill", "Skill" })
                T.Check(!derive.Contains(forbidden), "the role derivation never reads " + forbidden + " (origin facts only: seed and specialties)");
            T.Check(derive.Contains("NetHash.Combine(actorSeed, \"oprole.v\" + Version)"), "the role is a pure hash of the actor's own seed");
            T.Check(!derive.Contains("Fixer") && !derive.Contains("FixerProfile"), "no Fixer-specific mapping exists in the role derivation (a Fixer is outside Phase 3.1)");
            string forSolo = Body(derive, "public static OperationalRole ForSolo(NetworkActor a)", "}");
            T.Check(forSolo.Contains("if (!ContractorService.IsNpcSoloContractor(a)) return OperationalRole.Unset;") && forSolo.Contains("a.Get<ContractorProfile>().specialties"), "ForSolo is contractor-only: anything else is Unset, the rest reads the contractor profile's origin facts");
            T.Check(Regex.IsMatch(Code("Domain/Contractors/ContractorService.cs"), @"public static bool IsSolo\(NetworkActor a\)\s*\{\s*return a != null && a\.kind == ActorKind\.Individual && !a\.Has<OrganizationProfile>\(\);\s*\}"), "IsSolo's global meaning is unchanged");
            T.Check(Regex.IsMatch(Code("Domain/Contractors/ContractorService.cs"), @"public static bool IsNpcSoloContractor\(NetworkActor a\)\s*\{\s*return IsSolo\(a\) && IsNpcContractor\(a\);\s*\}"), "the Phase 3.1 predicate is exactly IsSolo && IsNpcContractor");
            T.Check(Body(Code("Domain/Physical/PhysicalLifecycleService.cs"), "private CommandResult CheckPlan", "private ").Contains("CommandResult.Fail(\"RoleUnderivable\""), "Plan fails closed on an embodied person whose role cannot be derived");
            string service = Body(Code("Domain/Contractors/ContractorService.cs"), "public int EnsureSoloRoles()", "public static int Headcount");
            T.Check(service.Length > 100, "found the compatibility pass");
            foreach (string forbidden in new[] { "reputation", "Fame", "notability", "ctx.Now", "clock", "Rand", "NetRng", "skill", "Physical.PawnProjection" })
                T.Check(!service.Contains(forbidden), "the compatibility pass never reads " + forbidden);
            T.Check(service.Contains("c.opRole != Physical.OperationalRole.Unset") && service.Contains("c.pawn != null && c.pawn.IsBound"), "it skips a stored role and a bound person");
            string world = Code("Core/NetworkWorldComponent.cs");
            T.Check(Regex.IsMatch(Body(world, "private void Bootstrap()", "private void AfterLoad()"), @"ctx\.Contractors\.EnsureSoloRoles\(\);") && Regex.IsMatch(Body(world, "private void AfterLoad()", "private void ScheduleSweeps()"), @"ctx\.Contractors\.EnsureSoloRoles\(\)"),
                "the pass is part of the normal init (bootstrap) and load compatibility path, not the test harness");
            T.Check(!Code("Diagnostics/RuntimePhysicalTests/PhysicalTestWorld.cs").Contains("opRole =") && !Code("Diagnostics/RuntimePhysicalTests/PhysicalTestRunner.cs").Contains("opRole ="), "the physical test harness never writes a role");
            string lifecycle = Body(Code("Domain/Physical/PhysicalLifecycleService.cs"), "public CommandResult Plan(EpisodeRequest r, out PhysicalEpisode episode)", "private CommandResult CheckPlan");
            T.Check(lifecycle.Contains("c.opRole == OperationalRole.Unset && a.bindings.embodies == c.id") && lifecycle.IndexOf("c.opRole = RoleDerivation.ForSolo(a)", StringComparison.Ordinal) < lifecycle.IndexOf("seatRole = c.opRole", StringComparison.Ordinal), "Plan still stores the role before the member's seat reads it");
            T.Eq(5, SaveMigrations.Current, "no save-version bump to fill an existing v5 field");
        }

        // ================================================================== Fix 4: RT-PHYX-002 / 015 pass counters

        private static void ReturnedHasNoPass()
        {
            Func<bool, bool, MemberOutcome, bool> has = (bound, named, outcome) =>
            {
                foreach (ReleaseAction act in ReleasePolicy.ActionsFor(bound, named, outcome)) if (act == ReleaseAction.PassToWorldIfAllowed) return true;
                return false;
            };
            T.Check(!has(true, true, MemberOutcome.Returned), "a named Returned member's RELEASE list contains NO PassToWorldIfAllowed (vanilla already passed it)");
            T.Check(!has(true, false, MemberOutcome.Returned), "neither does an anonymous Returned member's");
            foreach (MemberOutcome o in Enum.GetValues(typeof(MemberOutcome)))
                if (o != MemberOutcome.NeverPlaced) T.Check(!has(true, true, o) && !has(true, false, o), "no outcome but NeverPlaced ever lists a pass (" + o + ")");
            T.Check(has(true, true, MemberOutcome.NeverPlaced) && has(true, false, MemberOutcome.NeverPlaced), "only a bound NEVER-PLACED member can be passed (the § 7.5 three-part check guards it)");
            T.Eq(3, ReleasePolicy.ActionsFor(true, true, MemberOutcome.Returned).Length, "a named Returned release is { Normalize, EnsureRetained, StripTag }");
        }

        private static void NormalReturnCounters()
        {
            TestNet n = new TestNet(9751);
            NetworkActor a = Solo(n, "pass");
            KnownCharacter c = Self(n, a);
            PhysicalLifecycleService lc = PhysicalLifecycleTests.L(n);
            int passed0 = lc.counters.passedToWorld, skipped0 = lc.counters.passSkippedAlreadyWorld, refused0 = lc.counters.passRefused;
            PhysicalEpisode e = PhysicalLifecycleTests.Begin(n, a, new[] { c });
            n.physical.ExitNormally(c.pawn, 80);
            lc.Reconcile(e, "exit");
            T.Check(e.IsComplete && e.members[0].outcome == MemberOutcome.Returned, "the normal exit was committed Returned and RELEASE completed");
            T.Eq(0, lc.counters.passedToWorld - passed0, "the lifecycle passed 0 pawns");
            T.Eq(0, lc.counters.passSkippedAlreadyWorld - skipped0, "and skipped 0 as 'already a world pawn' (a Returned release has no pass action to skip)");
            T.Eq(0, lc.counters.passRefused - refused0, "and refused 0");
            T.Eq(0, n.physical.passCalls, "the port was never asked to PassToWorld");
            T.Eq(0, n.physical.Count("pass"), "no pass action of any kind was requested");
            T.Check(n.physical.retains == 1 && n.physical.normalizes == 1 && n.physical.strips == 1, "RELEASE ran exactly its three actions (normalize, retain, strip)");
        }

        private static void ScanPassAssertions()
        {
            string scenarios = Code("Diagnostics/RuntimePhysicalTests/PhysicalScenarios.cs");
            T.Check(!Regex.IsMatch(scenarios, @"passSkippedAlreadyWorld\s*>\s*skipped0"), "no scenario expects the 'already world' counter to INCREMENT for a Returned pawn");
            string r2 = Body(scenarios, "public sealed class Phyx002NormalExit", "public sealed class Phyx003DownedRecovery");
            string r15 = Body(scenarios, "public sealed class Phyx015NormalExitM1", "public sealed class Phyx016MapRemovalM1");
            foreach (string r in new[] { r2, r15 })
            {
                T.Check(Regex.IsMatch(r, @"passSkippedAlreadyWorld\s*==\s*skipped0") && Regex.IsMatch(r, @"passRefused\s*==\s*refused0"), "the scenario expects 0 skipped and 0 refused");
                T.Check(r.Contains("port.counters.passes == passes0"), "and 0 Network PassToWorld calls");
                T.Check(r.Contains("contains no Network PassToWorld action at all") || r.Contains("contains no pass action at all"), "and says WHY: a Returned release contains no pass action at all");
            }
            string raw = PhysicalLifecycleTests.Src("Diagnostics/RuntimePhysicalTests/PhysicalScenarios.cs");
            T.Check(raw.Contains("ReleasePolicy, § 7.5, P3-INV-031"), "the explanation cites the policy and the invariant");
            // The PRODUCTION protection is unchanged: the pass branch is still guarded by the three-part check, and the policy still lists no pass for Returned.
            string lifecycle = Code("Domain/Physical/PhysicalLifecycleService.cs");
            T.Check(lifecycle.Contains("if (check == PassToWorldCheck.Allowed)") && lifecycle.Contains("else if (check == PassToWorldCheck.AlreadyInWorldPawns)") && lifecycle.Contains("throw new PhysicalPreconditionException(\"PassToWorld \" + m, check);"), "P3-INV-031: RELEASE's pass guard is unchanged");
            T.Check(Code("Domain/Physical/ReconciliationPlan.cs").Contains("NamedReturned = { ReleaseAction.Normalize, ReleaseAction.EnsureRetained, ReleaseAction.StripTag }"), "and the Returned release list is unchanged");
        }

        // ================================================================== Fix 5: RT-PHYX-009, the two phases

        private static void UnsupportedThenCleared()
        {
            TestNet n = new TestNet(9761);
            NetworkActor a = Solo(n, "arrest");
            KnownCharacter c = Self(n, a);
            PhysicalLifecycleService lc = PhysicalLifecycleTests.L(n);
            PhysicalEpisode e = PhysicalLifecycleTests.Begin(n, a, new[] { c });
            PawnRef binding = c.pawn.Copy();
            int commits0 = lc.counters.commits, passes0 = lc.counters.passedToWorld;
            // PHASE 1 — the dev arrest: held by the player. Evidence is captured while it holds.
            n.physical.Hold(c.pawn, ObservedKind.HeldByPlayer);
            n.physical.TokenOf(c.pawn).held = true;
            lc.Reconcile(e, "watch");
            T.Eq(EpisodeState.Quarantined, e.state, "PHASE 1: the episode is Quarantined");
            T.Check(e.quarantineKey != null && e.quarantineKey.StartsWith("UnsupportedCustody:HeldByPlayer", StringComparison.Ordinal), "PHASE 1: UnsupportedCustody:HeldByPlayer (" + e.quarantineKey + ")");
            T.Check(e.members[0].state == MemberState.Present && e.members[0].outcome == MemberOutcome.Pending && lc.counters.commits == commits0, "PHASE 1: nothing committed, no capture faked");
            T.Check(c.custody == CustodyState.Deployed && !AuthorityGate.CanSimulateAbstractly(c), "PHASE 1: the person stays non-abstract");
            T.Eq(0, n.physical.passCalls, "PHASE 1: no Network PassToWorld");
            // PHASE 2 — vanilla clears the custody (the map is removed: the guest status ends and the pawn is passed to the world).
            n.physical.TokenOf(c.pawn).held = false;
            n.physical.ExitNormally(c.pawn, 80);
            int wakeups0 = lc.counters.wakeups;
            lc.Reconcile(e, "watch");
            T.Check(lc.counters.wakeups > wakeups0, "PHASE 2: the episode was re-observed");
            T.Check(e.IsComplete && e.state != EpisodeState.Quarantined && e.quarantineKey == null, "PHASE 2: it is NOT still quarantined: ending the custody lifted it, and the episode completed");
            T.Eq(MemberOutcome.Returned, e.members[0].outcome, "PHASE 2: through the ordinary Returned path");
            T.Check(c.custody == CustodyState.Stored && AuthorityGate.CanSimulateAbstractly(c), "PHASE 2: Stored, authority open again");
            T.Check(c.pawn.SameBinding(binding) && e.members[0].pawn.SameBinding(c.pawn), "PHASE 2: the same pawn and binding");
            T.Eq(passes0, lc.counters.passedToWorld, "PHASE 2: still no Network PassToWorld");
            T.Eq(0, n.physical.passCalls, "PHASE 2: the port was never asked to pass anything");
        }

        private static void ScanRt009()
        {
            string s = Body(Code("Diagnostics/RuntimePhysicalTests/PhysicalScenarios.cs"), "public sealed class Phyx009UnsupportedCustody", "public sealed class Phyx010SavePoint");
            foreach (string field in new[] { "sawUnsupportedQuarantine", "sawPawnStillPrisoner", "sawAuthorityClosed", "sawNoCommit", "sawNoPassToWorld" })
                T.Check(s.Contains("bool ") && s.Contains(field), "RT-PHYX-009 records " + field);
            T.Check(Regex.IsMatch(s, @"everyFrame\s*=\s*Capture;"), "the evidence is gathered EVERY frame from the arrest on");
            string capture = Body(s, "private void Capture()", "protected override void Finish()");
            T.Check(capture.Contains("if (sawUnsupportedQuarantine) return;") && capture.Contains("sawPawnStillPrisoner = ") && capture.Contains("sawAuthorityClosed = ") && capture.Contains("sawNoCommit = ") && capture.Contains("sawNoPassToWorld = "), "all five facts are captured on the frame the quarantine is first observed");
            T.Check(capture.Contains("if (!armed || removed") , "and nothing is captured after the map removal");
            int wait = s.IndexOf("if (sawUnsupportedQuarantine) return StepResult.Next;", StringComparison.Ordinal);
            int assert1 = s.IndexOf("PHASE 1 — assert the facts captured DURING the unsupported custody", StringComparison.Ordinal);
            int remove = s.IndexOf("TestSite.RemoveMap(false)", StringComparison.Ordinal);
            T.Check(wait > 0 && assert1 > wait && remove > assert1, "the map is removed only AFTER the quarantine was observed and its facts asserted");
            string finish = Body(s, "protected override void Finish()", "RT-PHYX-010");
            T.Check(!Regex.IsMatch(finish, @"EpisodeState\.Quarantined\)?\s*,") || finish.Contains("e.state != EpisodeState.Quarantined"), "the final assertions never require the episode to STILL be quarantined");
            T.Check(!finish.Contains("e.state == EpisodeState.Quarantined") && !finish.Contains("IsPrisonerOfColony"), "nor that the pawn is still a prisoner");
            T.Check(finish.Contains("e.members[0].pawn.SameBinding(c.pawn)") && finish.Contains("port.counters.passes == passes0") && finish.Contains("lc.counters.wakeups > wakeups0") && finish.Contains("CheckReturnedAndStored(e, c, p)"), "they assert re-observation, the ordinary Returned path, the same binding and no Network PassToWorld");
        }

        // ================================================================== Fix 6: RT-PHYX-010 labels

        private static void Rt010Labels()
        {
            string actions = PhysicalLifecycleTests.Src("Diagnostics/RuntimePhysicalTests/PhysicalTestDevActions.cs");
            string[] labels = { PhysicalTestIds.Label010A, PhysicalTestIds.Label010B, PhysicalTestIds.Label010V };
            T.Eq("010A SAVE — visitor spawned [armed]", labels[0], "010A label");
            T.Eq("010B SAVE — post-map [armed]", labels[1], "010B label");
            T.Eq("010V VERIFY — loaded save", labels[2], "010V label");
            foreach (string l in labels) T.Check(actions.Contains("\"" + l + "\""), "the menu shows \"" + l + "\" (the literal equals the constant the instructions use)");
            for (int i = 0; i < labels.Length; i++)
            {
                T.Check(labels[i].Length <= 40, "short enough to survive the narrow menu (" + labels[i].Length + " chars)");
                for (int k = i + 1; k < labels.Length; k++) T.Check(labels[i].Substring(0, 5) != labels[k].Substring(0, 5), "the unique part comes FIRST: " + labels[i].Substring(0, 5) + " vs " + labels[k].Substring(0, 5));
            }
            T.Check(Regex.Matches(actions, @"PhysicalScenarioTable\.Get\(""RT-PHYX-010""\)").Count == 3, "all three still use the stable scenario family id RT-PHYX-010");
            T.Check(!actions.Contains("RT-PHYX-010 — Save/load matrix"), "the old, near-identical labels are gone");
            T.Check(PhysicalScenarioTable.Get("RT-PHYX-010") != null && PhysicalScenarioTable.Get("RT-PHYX-010").id == "RT-PHYX-010", "the table, the log and every report still say RT-PHYX-010");
            T.Check(Regex.IsMatch(actions, @"\[DebugAction\(Cat, ""010A SAVE — visitor spawned \[armed\]""[^\]]*\]\s*public static void Phyx010A\(\)\s*\{\s*PhysicalTestSession\.Start\("), "010A spends the arm");
            T.Check(Regex.IsMatch(actions, @"\[DebugAction\(Cat, ""010B SAVE — post-map \[armed\]""[^\]]*\]\s*public static void Phyx010B\(\)\s*\{\s*PhysicalTestSession\.Start\("), "010B spends the arm");
            T.Check(Regex.IsMatch(actions, @"\[DebugAction\(Cat, ""010V VERIFY — loaded save""[^\]]*\]\s*public static void Phyx010Verify\(\)\s*\{\s*PhysicalTestSession\.StartReadOnly\("), "010V never needs the arm");
            string scenarios = PhysicalLifecycleTests.Src("Diagnostics/RuntimePhysicalTests/PhysicalScenarios.cs");
            T.Check(scenarios.Contains("PhysicalTestIds.Label010V") && scenarios.Contains("does NOT cancel the real production episode"), "the in-run instructions name the real 010V label and explain that stopping the verifier does not cancel a production episode");
        }

        private static void Rt010VerifierReadOnly()
        {
            string scenarios = Code("Diagnostics/RuntimePhysicalTests/PhysicalScenarios.cs");
            string v = Body(scenarios, "public sealed class Phyx010VerifyAfterLoad", "public sealed class Phyx011RoleGeneration");
            T.Check(v.Length > 500, "found the verifier");
            foreach (string write in new[] { "lc.Plan(", "Materialize", "MaterializeOnTestMap", "GenSpawn", "CapturedBy", "RemoveMap", "lc.Reconcile(", "lc.WakeAll", "EnsureQuest", "Registry.Rebuild", "ResolvePointers", "PassToWorld", ".Destroy(", ".Discard(", "Create(", "CatchUp" })
                T.Check(!v.Contains(write), "the verifier is read-only: it never calls " + write);
            string session = Body(Code("Diagnostics/RuntimePhysicalTests/PhysicalTestRunner.cs"), "public static bool StartReadOnly(", "private static void EnsureObserver()");
            T.Check(!session.Contains("Arm.") && session.Contains("Prefs.DevMode"), "starting the read-only runner never spends or needs the arm");
            string world = Code("Core/NetworkWorldComponent.cs");
            T.Check(Body(world, "public override void FinalizeInit(bool fromLoad)", "private void RunLoadMigrations()").Contains("PhysicalTestSession.ResetForNewGame();"), "every load clears the session arm (it is runtime-only and never persisted)");
            T.Check(Body(Code("Diagnostics/RuntimePhysicalTests/PhysicalTestRunner.cs"), "public void Stop()", "private bool ended").Contains("nothing was undone"), "stopping a run undoes nothing (it never cancels a production episode)");
            foreach (string f in PhysicalLifecycleTests.SourceFiles("Diagnostics/RuntimePhysicalTests"))
            {
                string code = PhysicalLifecycleTests.Code(File.ReadAllText(f));
                T.Check(!Regex.IsMatch(code, @"Scribe_\w+\.Look") && !code.Contains("IExposable"), Path.GetFileName(f) + " persists nothing (no test-control object, no 'disposable save' flag)");
            }
        }

        // ================================================================== documentation

        private static string Doc(string rel)
        {
            string root = Environment.GetEnvironmentVariable("THENETWORK_REPO") ?? ".";
            return File.ReadAllText(Path.Combine(root, rel));
        }

        private static void DocsConsistent()
        {
            string lifecycle = Doc("docs/PHYSICAL_LIFECYCLE.md"), testing = Doc("docs/RUNTIME_TESTING.md"), decisions = Doc("docs/DECISIONS.md"), save = Doc("docs/SAVE_AND_MIGRATION.md"), arch = Doc("docs/ARCHITECTURE.md");
            string readme = Doc("README.md"), phases = Doc("docs/IMPLEMENTATION_PHASES.md"), risks = Doc("docs/RISKS.md"), integration = Doc("docs/RIMWORLD_INTEGRATION.md");
            // The pass is recorded.
            T.Check(lifecycle.Contains("## Appendix K: Phase 3.1 runtime-QA correction pass (PR #10)"), "Appendix K exists");
            foreach (string inv in new[] { "P3-INV-037", "P3-INV-038", "P3-INV-039" }) T.Check(lifecycle.Contains("| **" + inv + "** |"), inv + " is in the invariant table");
            T.Check(decisions.Contains("### ADR-055 · Phase 3.1 runtime-QA correction") && decisions.Contains("#adr-055--"), "ADR-055 exists and is linked");
            T.Check(lifecycle.Contains("### K.8 Fixers are not Phase 3.1 Solo contractor candidates") && lifecycle.Contains("k8-fixers-are-not-phase-31-solo-contractor-candidates"), "Appendix K.8 records the Fixer exclusion and its anchor resolves");
            T.Check(lifecycle.IndexOf("### K.7 ", StringComparison.Ordinal) < lifecycle.IndexOf("### K.8 ", StringComparison.Ordinal), "K.8 follows K.7");
            T.Check(!lifecycle.Contains("The derivation is now total for any embodied individual") && !decisions.Contains("The role of an embodied individual is total and stored early"), "no document still says the role derivation is total for a Fixer or any embodied individual");
            T.Check(testing.Contains("A Fixer is never selected") && decisions.Contains("a Fixer is not a Phase 3.1 candidate"), "the owner-facing docs say a Fixer is never selected");
            T.Check(risks.Contains("| R-46 |") && risks.Contains("| R-47 |") && risks.Contains("| R-48 |") && risks.Contains("## R-46 ·"), "the three observed risks are registered");
            // The audited load order, in every document that states it.
            T.Check(lifecycle.Contains("**`World.FinalizeInit(fromLoad: true)`** (`Game.cs:586`)") && lifecycle.Contains("`worldPawns.WorldPawnsTick()` BEFORE any world component"), "PHYSICAL_LIFECYCLE § 16.3 states the audited order");
            int fin = save.IndexOf("| 2 | `FinalizeInit(fromLoad: true)` |", StringComparison.Ordinal), crossRefs = save.IndexOf("| 3 | `ResolvingCrossRefs` |", StringComparison.Ordinal), post = save.IndexOf("| 4 | `PostLoadInit` |", StringComparison.Ordinal);
            T.Check(fin > 0 && crossRefs > fin && post > crossRefs, "SAVE_AND_MIGRATION § 5: FinalizeInit comes BEFORE cross-references and PostLoadInit");
            T.Check(arch.IndexOf("→ FinalizeInit(true)", StringComparison.Ordinal) > 0 && arch.IndexOf("→ FinalizeInit(true)", StringComparison.Ordinal) < arch.IndexOf("→ ResolvingCrossRefs (PawnRef pointers)", StringComparison.Ordinal), "ARCHITECTURE § 7.3 has the same order");
            T.Check(integration.Contains("BEFORE `Scribe.loader.FinalizeLoading()`"), "RIMWORLD_INTEGRATION states FinalizeInit precedes cross-reference resolution");
            foreach (string stale in new[] { "rebuilt from the characters store in `FinalizeInit`", "the runtime index is rebuilt when the runtime is built", "(after cross-references\nresolve, before any first tick)" })
                T.Check(!lifecycle.Contains(stale), "no stale claim remains: " + stale.Replace("\n", " "));
            // Status wording. (The final cleanup pass: the owner has runtime-validated Phase 3.1, so the earlier "not a PASS / in progress" wording is replaced by the validated status,
            // asserted in full by Phys31Fin.Docs_*; this check keeps what the QA pass established: S31 is kept apart, and no document claims more than Phase 3.1.)
            foreach (KeyValuePair<string, string> kv in new[] { new KeyValuePair<string, string>("README", readme), new KeyValuePair<string, string>("PHYSICAL_LIFECYCLE", lifecycle), new KeyValuePair<string, string>("RUNTIME_TESTING", testing), new KeyValuePair<string, string>("IMPLEMENTATION_PHASES", phases), new KeyValuePair<string, string>("DECISIONS", decisions) })
            {
                T.Check(Regex.IsMatch(kv.Value, @"(?i)owner runtime validated"), kv.Key + " states that Phase 3.1 is owner runtime validated");
                T.Check(!Regex.IsMatch(kv.Value, @"(?i)owner physical validation in progress"), kv.Key + " no longer says validation is in progress");
                T.Check(!Regex.IsMatch(kv.Value, @"(?i)Phase 3\.2 (is )?(implemented|started|begun|in progress)"), kv.Key + " does not claim Phase 3.2");
            }
            T.Check(!Regex.IsMatch(testing + phases + readme, @"physical suite \(`RT-PHYX-\*`\) has not yet been run by the owner") && !testing.Contains("**PHYSICAL RUNTIME — NOT YET RUN BY OWNER.**"), "no document still says the owner has not run the physical suite");
            T.Check(lifecycle.Contains("S31 / M1 is owner-runtime VALIDATED and ACCEPTED") && decisions.Contains("**Accepted — owner runtime validated.**"), "S31 / M1 stays recorded as accepted, apart from the Phase 3.1 status");
            // The owner's workflow and labels.
            foreach (string label in new[] { PhysicalTestIds.Label010A, PhysicalTestIds.Label010B, PhysicalTestIds.Label010V }) T.Check(testing.Contains(label.Replace(" [armed]", "")), "RUNTIME_TESTING names the label \"" + label + "\"");
            T.Check(testing.Contains("### 17.3 RT-PHYX-010: the save and load workflow (owner steps)") && testing.Contains("— **NO ARM**") && testing.Contains("Stopping `010V` stops only the read-only QA runner. It does NOT cancel a real production `PhysicalEpisode`."), "the 010 workflow says NO ARM and that stopping 010V does not cancel a production episode");
            T.Check(testing.Contains("fresh disposable save") || testing.Contains("**fresh disposable save**"), "the retest uses a fresh disposable save");
            T.Check(testing.Contains("`RT-PHYX-001`, `002`, `004`, `009`, `010A` + reload + `010V`, `010B` + reload + `010V`, `011`, `015`"), "the reduced retest list is stated");
            T.Check(testing.Contains("0 Network `PassToWorld`, 0 \"skipped as already a world pawn\", 0 refused"), "RT-PHYX-002's expectation is documented as zero");
            T.Check(!Regex.IsMatch(testing, @"\| RT-PHYX-010 \| Save/load matrix \|[^\n]*save point A \(visitor present\)"), "the old 010 row is gone");
            T.Check(readme.Contains("docs/PHYSICAL_LIFECYCLE.md#appendix-k-phase-31-runtime-qa-correction-pass-pr-10") && lifecycle.Contains("## Appendix K:"), "the README links to Appendix K and the anchor target exists");
            T.Eq(5, SaveMigrations.Current, "save format 5, as every document says");
        }

        // ================================================================== globals

        private static void ScanGlobals()
        {
            foreach (string f in PhysicalLifecycleTests.AllSources())
            {
                string code = File.ReadAllText(f);
                T.Check(!code.Contains("HarmonyLib") && !code.Contains("0Harmony"), "no Harmony anywhere in Source (" + Path.GetFileName(f) + ")");
            }
            T.Eq(5, SaveMigrations.Current, "save format 5");
            T.Check(PhysicalScenarioTable.Get("RT-PHYX-013") == null && PhysicalScenarioTable.Get("RT-PHYX-014") == null, "no Phase 3.2 scenario exists");
            foreach (string rel in new[] { "Domain/Physical/BindingRules.cs", "Integration/Physical/FactionMemberKinds.cs" })
                T.Check(!Code(rel).Contains("Scribe") && !Code(rel).Contains("IExposable"), rel + " adds no persisted field");
            // Ids that already existed are unchanged and no id was renumbered.
            string[] ids = { "RT-PHYX-001", "RT-PHYX-002", "RT-PHYX-003", "RT-PHYX-004", "RT-PHYX-005", "RT-PHYX-006", "RT-PHYX-007", "RT-PHYX-008", "RT-PHYX-009", "RT-PHYX-010", "RT-PHYX-011", "RT-PHYX-012", "RT-PHYX-015", "RT-PHYX-016" };
            foreach (string id in ids) T.Check(PhysicalScenarioTable.Get(id) != null && PhysicalScenarioTable.Get(id).id == id, id + " is unchanged");
        }
    }
}
