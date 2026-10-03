using System;
using System.Collections.Generic;
using System.Xml;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using TheNetwork.Settings;
using TheNetwork.UI;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>
    /// Phase 2.75 contractor careers (ADR-046): numeric reputation beneath the fame band, the career record,
    /// contractor money in the existing funds, equipment advancement, derived need and Tags, and the V3 → V4
    /// migration. Every test drives the real services; nothing here asserts a tuning outcome.
    /// </summary>
    public static class CareerTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            // A. Reputation
            t.Add(new KeyValuePair<string, Action>("Career.Reputation_TrivialSuccessGainsLessThanDangerousSuccess", ReputationTrivialVsDangerous));
            t.Add(new KeyValuePair<string, Action>("Career.Reputation_TriumphGainsMoreThanSuccess", ReputationTriumph));
            t.Add(new KeyValuePair<string, Action>("Career.Reputation_NearFullPartialApproachesSuccessButDoesNotExceedIt", ReputationPartial));
            t.Add(new KeyValuePair<string, Action>("Career.Reputation_FailureDoesNotGainPositiveRep", ReputationFailure));
            t.Add(new KeyValuePair<string, Action>("Career.Reputation_DisasterDoesNotGainPositiveRep", ReputationDisaster));
            t.Add(new KeyValuePair<string, Action>("Career.Reputation_BandThresholdsExact", ReputationThresholds));
            t.Add(new KeyValuePair<string, Action>("Career.Reputation_FameAndExperienceRemainIndependent", ReputationIndependent));
            t.Add(new KeyValuePair<string, Action>("Career.Reputation_SaveLoadDoesNotDuplicate", ReputationSaveLoad));
            t.Add(new KeyValuePair<string, Action>("Career.Reputation_OldOperationDoesNotRetroactivelyAward", ReputationOldOperation));
            t.Add(new KeyValuePair<string, Action>("Career.Reputation_RepeatedEasyWorkCannotMakeALegend", ReputationFarming));
            t.Add(new KeyValuePair<string, Action>("Career.Reputation_NeverNegativeAndAlwaysBounded", ReputationBounds));
            t.Add(new KeyValuePair<string, Action>("Career.Events_OnlyBandCrossingsAndAdvancesArePublished", EventsOnlyMeaningful));

            // B. Record
            t.Add(new KeyValuePair<string, Action>("Career.Record_OutcomeAppliedExactlyOnce", RecordOnce));
            t.Add(new KeyValuePair<string, Action>("Career.Record_TracksOutcomeBands", RecordBands));
            t.Add(new KeyValuePair<string, Action>("Career.Record_TracksCasualtiesWithoutDoubleCounting", RecordCasualties));
            t.Add(new KeyValuePair<string, Action>("Career.Record_LegacyOpsRemainUnclassified", RecordLegacy));
            t.Add(new KeyValuePair<string, Action>("Career.Record_ReplacementOperationsCreditCorrectActors", RecordReplacement));
            t.Add(new KeyValuePair<string, Action>("Career.Record_AbortBeforeOutcomeGivesNothing", RecordAbortBefore));
            t.Add(new KeyValuePair<string, Action>("Career.Record_TroubledAppliesOnlyAtTheEnd", RecordTroubled));
            t.Add(new KeyValuePair<string, Action>("Career.Record_RepeatedTerminalCallsRetriesAndDevAdvanceDoNotDuplicate", RecordRepeats));
            t.Add(new KeyValuePair<string, Action>("Career.Record_WrittenOffDisasterCountsAsFailure", RecordWrittenOffDisaster));
            t.Add(new KeyValuePair<string, Action>("Career.Record_FailedCareerCommitRemainsRetryable", RecordFailedCommitRetryable));
            t.Add(new KeyValuePair<string, Action>("Career.Record_FailedCommitPublishesNoDuplicateFameEvent", RecordFailedCommitNoDuplicateEvent));
            t.Add(new KeyValuePair<string, Action>("Career.Record_MissingCareerTargetNeverMarksApplied", RecordMissingTargetNeverApplied));

            // C. Money
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_NormalContractCreditsContractor", WealthNormal));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_FixerFeeDoesNotCreditContractor", WealthFixerFee));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_InsuranceDoesNotInventContractorMoney", WealthInsurance));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_TechnicalInvalidationNoWindfall", WealthVoid));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_RefundSemanticsCorrect", WealthRefund));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_ReplacementTransferMatchesPhase2Ownership", WealthReplacement));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_SaveLoadNoDuplicateCredit", WealthSaveLoad));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_SaturatingArithmeticNoOverflow", WealthSaturating));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_RenegotiationAndBalanceCreditOnce", WealthRenegotiation));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_ReplacementBalanceRefundClawsReplacementExactly", WealthReplacementBalanceRefund));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_ReplacementVoidClawsOnlyReplacementOwnedMoney", WealthReplacementVoid));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_PurposeScopedRefundDoesNotUseInheritedFunding", WealthPurposeScoped));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_PartialInsuranceThenTechnicalVoidLeavesNoWindfall", WealthPartialInsuranceThenVoid));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_ReplacementPartialInsuranceThenVoidTouchesOnlyReplacement", WealthReplacementPartialInsuranceThenVoid));
            t.Add(new KeyValuePair<string, Action>("Career.Wealth_VoidWithNothingLeftToRefundStillReversesContractor", WealthVoidNothingLeftToRefund));
            t.Add(new KeyValuePair<string, Action>("Career.Audit_RefundPathsFollowTheirFunding", AuditRefundPaths));
            t.Add(new KeyValuePair<string, Action>("Career.Audit_SaveLoadAroundTheTerminalTransition", AuditSaveLoadAroundTerminal));

            // D. Advancement
            t.Add(new KeyValuePair<string, Action>("Career.Advancement_RequiresReputation", AdvanceReputation));
            t.Add(new KeyValuePair<string, Action>("Career.Advancement_RequiresFundsPlusReserve", AdvanceFunds));
            t.Add(new KeyValuePair<string, Action>("Career.Advancement_DoesNotRunWhileCommitted", AdvanceCommitted));
            t.Add(new KeyValuePair<string, Action>("Career.Advancement_SpendsFunds", AdvanceSpends));
            t.Add(new KeyValuePair<string, Action>("Career.Advancement_OneTierOnlyPerCooldown", AdvanceCooldown));
            t.Add(new KeyValuePair<string, Action>("Career.Advancement_NeverExceedsEquipmentTierCap", AdvanceCap));
            t.Add(new KeyValuePair<string, Action>("Career.Advancement_SaveLoadStable", AdvanceSaveLoad));
            t.Add(new KeyValuePair<string, Action>("Career.Advancement_RidesTheDailyUpkeepOnly", AdvanceRidesUpkeep));

            // E. Tags
            t.Add(new KeyValuePair<string, Action>("Career.Tags_DerivedNotPersisted", TagsNotPersisted));
            t.Add(new KeyValuePair<string, Action>("Career.Tags_WellEquippedTracksEquipment", TagsWellEquipped));
            t.Add(new KeyValuePair<string, Action>("Career.Tags_EliteCombatTracksExistingCapability", TagsEliteCombat));
            t.Add(new KeyValuePair<string, Action>("Career.Tags_WealthyScalesWithOperatingNeeds", TagsWealthy));
            t.Add(new KeyValuePair<string, Action>("Career.Tags_MobilityTagsReflectMobilityOnly", TagsMobility));
            t.Add(new KeyValuePair<string, Action>("Career.Tags_NoAugmentedWithoutAugmentationTruth", TagsNoAugmented));
            t.Add(new KeyValuePair<string, Action>("Career.Tags_DoNotModifyStrengthOrResolverTwice", TagsNoBonus));
            t.Add(new KeyValuePair<string, Action>("Career.Tags_BattleTestedAndLegendaryFollowTheRecord", TagsRecordAndFame));

            // F. Need
            t.Add(new KeyValuePair<string, Action>("Career.Need_LowFundsReturnsCapital", NeedCapital));
            t.Add(new KeyValuePair<string, Action>("Career.Need_PoorEquipmentReturnsEquipment", NeedEquipment));
            t.Add(new KeyValuePair<string, Action>("Career.Need_RecoveryBeatsLuxuryAdvancement", NeedRecovery));
            t.Add(new KeyValuePair<string, Action>("Career.Need_DeterministicAcrossReload", NeedReload));
            t.Add(new KeyValuePair<string, Action>("Career.Need_FameAndExperienceLagsAndAmbition", NeedOthers));

            // G. Migration
            t.Add(new KeyValuePair<string, Action>("Migration.V3ToV4_PreservesFameBand", MigrateFame));
            t.Add(new KeyValuePair<string, Action>("Migration.V3ToV4_PreservesFundsEquipmentSkillSpatial", MigratePreserves));
            t.Add(new KeyValuePair<string, Action>("Migration.V3ToV4_LegacyOpsNotFabricated", MigrateLegacy));
            t.Add(new KeyValuePair<string, Action>("Migration.V3ToV4_RunningOperationCareerIneligible", MigrateRunning));
            t.Add(new KeyValuePair<string, Action>("Migration.V1ToV4_FullChain", MigrateChain));
        }

        // ================================================================== helpers

        private static ContractorSimulation Sim(NetworkActor a) => a.Get<ContractorSimulation>();

        private static NetworkActor Solo(TestNet n, ExperienceBand exp = ExperienceBand.Experienced, FameBand fame = FameBand.Unknown)
        {
            return ContractorTests.Make(n, ContractorTests.Template(ContractorForm.Solo, exp, fame));
        }

        /// <summary>Sets funds through the money path (tests keep the books balanced).</summary>
        private static void SetFunds(TestNet n, NetworkActor a, int funds)
        {
            ContractorSimulation sim = Sim(a);
            n.ctx.Career.MoveFunds(sim, (long)funds - sim.funds, FundsFlow.Dev);
        }

        /// <summary>Posts to the contractor, runs the job at the forced band to its end.</summary>
        private static Contract Job(TestNet n, NetworkActor fixer, NetworkActor who, OutcomeBand band, int count = 150, bool insure = false, int? secured = null)
        {
            ProcurementDevOverrides.forceBand = band;
            ProcurementDevOverrides.forceNotTroubled = true;
            if (secured.HasValue) ProcurementDevOverrides.forceSecured = secured.Value;
            Contract c = ProcurementTests.Awarded(n, fixer, who, "TestSteel", count, insure);
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            T.Check(c.IsTerminal, "the job ends (" + c.status + ")");
            ProcurementDevOverrides.Clear();
            return c;
        }

        private static int CreditOf(Contract c)
        {
            int s = 0;
            foreach (MoneyRecord m in c.ledger) if (m.contractorSilver > 0) s += m.contractorSilver;
            return s;
        }

        private static int ClawOf(Contract c)
        {
            int s = 0;
            foreach (MoneyRecord m in c.ledger) if (m.contractorSilver < 0) s -= m.contractorSilver;
            return s;
        }

        /// <summary>The books balance: every contractor's funds are exactly the sum of every tallied flow.</summary>
        private static void Books(TestNet n, string label)
        {
            long funds = 0;
            foreach (NetworkActor a in n.ctx.actors.actors) if (ContractorService.IsNpcContractor(a)) funds += Sim(a).funds;
            T.Eq(n.ctx.Career.counters.Net, funds, label + ": contractor funds equal the sum of every tallied flow (nothing credited or spent off the books)");
        }

        private static float QuoteShare(TestNet n, Contract c)
        {
            Offer o = n.ctx.contracts.Get(c.acceptedOffer);
            return o.quote.ContractorShare() / (float)o.quote.finalPrice;
        }

        // ================================================================== A. reputation

        private static void ReputationTrivialVsDangerous()
        {
            int trivial = CareerPolicy.ReputationGain(0.05f, OutcomeBand.Success, 100, 100, 0);
            int hard = CareerPolicy.ReputationGain(0.9f, OutcomeBand.Success, 100, 100, 0);
            T.Check(trivial > 0 && hard > trivial * 5, "a dangerous success is worth far more than a trivial one (" + hard + " vs " + trivial + ")");
            T.Eq(4, (int)Math.Round(CareerPolicy.DifficultyValue(0f)), "the easiest work is worth 4");
            T.Eq(40, (int)Math.Round(CareerPolicy.DifficultyValue(1f)), "the hardest 40");
            T.Check(CareerPolicy.DifficultyValue(0.6f) > CareerPolicy.DifficultyValue(0.3f), "monotonic in danger");

            // Through the real lifecycle: the danger is the operation's own frozen figure.
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            team.reputation.SetScore(0);
            Contract c = Job(n, fixer, team, OutcomeBand.Success);
            Operation op = ProcurementTests.Op(n, c);
            float danger = CareerService.DangerOf(op);
            int expected = CareerPolicy.ReputationGain(danger, OutcomeBand.Success, op.outcome.secured, op.outcome.requested, 0);
            T.Check(expected > 0, "the job earned something (" + expected + " at danger " + danger.ToString("0.00") + ")");
            T.Eq(expected, team.reputation.score, "the score rose by exactly the policy's figure for the operation's frozen danger");
            T.Eq(expected, Sim(team).career.reputationEarned, "and the record says so");
            T.Eq(Resolver.Danger(Resolver.Edge(op.frozenInputs)), danger, "the danger is the one frozen at engagement");
        }

        private static void ReputationTriumph()
        {
            for (float danger = 0.1f; danger <= 1f; danger += 0.15f)
            {
                int s = CareerPolicy.ReputationGain(danger, OutcomeBand.Success, 100, 100, 0);
                int t = CareerPolicy.ReputationGain(danger, OutcomeBand.Triumph, 100, 100, 0);
                T.Check(t > s, "danger " + danger + ": a triumph earns more than a success (" + t + " vs " + s + ")");
            }
            T.Check(CareerPolicy.OutcomeMultiplier(OutcomeBand.Triumph, 100, 100) == 1.25f && CareerPolicy.OutcomeMultiplier(OutcomeBand.Success, 100, 100) == 1f, "1.25 and 1.00");
            T.Check(CareerPolicy.OutcomeMultiplier(OutcomeBand.CostlySuccess, 100, 100) < 1f, "a costly success a little less");
        }

        private static void ReputationPartial()
        {
            float success = CareerPolicy.OutcomeMultiplier(OutcomeBand.Success, 500, 500);
            float nearly = CareerPolicy.OutcomeMultiplier(OutcomeBand.Partial, 499, 500);
            float little = CareerPolicy.OutcomeMultiplier(OutcomeBand.Partial, 20, 500);
            T.Check(nearly < success && nearly > 0.89f, "499 of 500 is nearly a success but never exceeds it (" + nearly + ")");
            T.Check(little < 0.5f && little > 0.4f, "20 of 500 earns substantially less (" + little + ")");
            T.Eq(0f, CareerPolicy.OutcomeMultiplier(OutcomeBand.Partial, 0, 500), "nothing secured earns nothing");
            int a = CareerPolicy.ReputationGain(0.9f, OutcomeBand.Success, 500, 500, 0);
            int b = CareerPolicy.ReputationGain(0.9f, OutcomeBand.Partial, 499, 500, 0);
            int c = CareerPolicy.ReputationGain(0.9f, OutcomeBand.Partial, 20, 500, 0);
            T.Check(b <= a && b >= a - 5 && c < b / 1.5f, "in whole points: " + a + " ≥ " + b + " ≫ " + c);
            for (int secured = 1; secured < 500; secured += 17) T.Check(CareerPolicy.OutcomeMultiplier(OutcomeBand.Partial, secured, 500) <= CareerPolicy.PartialHigh + 0.0001f, "a partial never reaches the success multiplier");
        }

        private static void ReputationFailure()
        {
            T.Eq(0, CareerPolicy.ReputationGain(1f, OutcomeBand.Failure, 0, 100, 0), "policy: a failure earns nothing, whatever the danger");
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            int before = team.reputation.score;
            Contract c = Job(n, fixer, team, OutcomeBand.Failure, 150, false, 0);
            T.Eq(before, team.reputation.score, "the score did not move");
            T.Eq(1, Sim(team).career.failures, "the failure is on the record");
            T.Eq(0, Sim(team).career.reputationEarned, "nothing earned");
        }

        private static void ReputationDisaster()
        {
            T.Eq(0, CareerPolicy.ReputationGain(1f, OutcomeBand.Disaster, 0, 100, 0), "policy: a disaster earns nothing");
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            int before = team.reputation.score;
            Job(n, fixer, team, OutcomeBand.Disaster);
            T.Eq(before, team.reputation.score, "the score did not move");
            T.Eq(1, Sim(team).career.disasters, "the disaster is on the record");
        }

        private static void ReputationThresholds()
        {
            int[] scores = { 0, 99, 100, 299, 300, 799, 800, 1999, 2000, 100000 };
            FameBand[] bands = { FameBand.Unknown, FameBand.Unknown, FameBand.Local, FameBand.Local, FameBand.Established, FameBand.Established, FameBand.Famous, FameBand.Famous, FameBand.Legendary, FameBand.Legendary };
            for (int i = 0; i < scores.Length; i++) T.Eq(bands[i], CareerPolicy.FameFor(scores[i]), "score " + scores[i]);
            T.Eq(0, CareerPolicy.FloorOf(FameBand.Unknown), "Unknown floor");
            T.Eq(100, CareerPolicy.FloorOf(FameBand.Local), "Local floor");
            T.Eq(300, CareerPolicy.FloorOf(FameBand.Established), "Established floor");
            T.Eq(800, CareerPolicy.FloorOf(FameBand.Famous), "Famous floor");
            T.Eq(2000, CareerPolicy.FloorOf(FameBand.Legendary), "Legendary floor");
            foreach (FameBand b in Enum.GetValues(typeof(FameBand))) T.Eq(b, CareerPolicy.FameFor(CareerPolicy.FloorOf(b)), "the floor maps back to " + b);

            // The band follows the score as it moves, and only a crossing changes it.
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Solo(n);
            T.Eq(0, a.reputation.score, "a new Unknown starts at 0");
            n.ctx.Career.AddReputation(a, 99, false);
            T.Eq(FameBand.Unknown, a.reputation.fame, "99 is still Unknown");
            n.ctx.Career.AddReputation(a, 1, false);
            T.Eq(FameBand.Local, a.reputation.fame, "100 is Local");
            n.ctx.Career.AddReputation(a, 700, false);
            T.Eq(FameBand.Famous, a.reputation.fame, "800 is Famous");
            n.ctx.Career.AddReputation(a, 1200, false);
            T.Eq(FameBand.Legendary, a.reputation.fame, "2000 is Legendary");

            // New contractors start at their template's band floor.
            foreach (FameBand b in Enum.GetValues(typeof(FameBand)))
            {
                NetworkActor x = Solo(n, ExperienceBand.Green, b);
                T.Eq(CareerPolicy.FloorOf(b), x.reputation.score, "a " + b + " newcomer starts at its floor");
                T.Eq(b, x.reputation.fame, "and keeps the band");
            }
        }

        private static void ReputationIndependent()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor green = Solo(n, ExperienceBand.Green, FameBand.Legendary);
            T.Eq(FameBand.Legendary, green.reputation.fame, "famous");
            T.Eq(ExperienceBand.Green, ContractorService.Experience(green), "yet green hands: fame is not capability");
            NetworkActor able = Solo(n, ExperienceBand.Elite, FameBand.Unknown);
            ExperienceBand before = ContractorService.Experience(able);
            n.ctx.Career.AddReputation(able, 5000, false);
            T.Eq(FameBand.Legendary, able.reputation.fame, "reputation raised the fame");
            T.Eq(before, ContractorService.Experience(able), "and left the experience alone");
            Sim(green).skill = 0.9f;
            T.Eq(ExperienceBand.Legendary, ContractorService.Experience(green), "skill raises experience");
            T.Eq(FameBand.Legendary, green.reputation.fame, "and leaves the fame alone");
            NetworkActor low = Solo(n, ExperienceBand.Legendary, FameBand.Unknown);
            T.Eq(FameBand.Unknown, low.reputation.fame, "a legendary hand nobody has heard of");
        }

        private static void ReputationSaveLoad()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            team.reputation.SetScore(0); // easy work earns nothing above a trivial ceiling: start where it counts
            Contract c = Job(n, fixer, team, OutcomeBand.Triumph);
            int score = team.reputation.score;
            long classified = Sim(team).career.Classified;
            T.Check(classified == 1 && score > 0, "one result applied (score " + score + ")");
            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            NetworkActor again = n.ctx.actors.Get(team.id);
            Operation op = n.ctx.operations.Get(ProcurementTests.Op(n, n.ctx.contracts.Get(c.id)).id);
            T.Eq(score, again.reputation.score, "the score survives the reload");
            T.Check(op.careerEligible && op.careerOutcomeApplied, "the flags survive");
            T.Check(!n.ctx.Career.CommitOutcome(op), "a reloaded operation cannot be applied again");
            n.ctx.Operations.Finish(op);
            n.ctx.Operations.Abort(op, "Again");
            n.ctx.Career.Validate(new List<string>());
            T.Eq(score, again.reputation.score, "nothing changed after repeated terminal calls and a validation");
            T.Eq(1L, Sim(again).career.Classified, "still one result");
        }

        private static void ReputationOldOperation()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            Contract c = ProcurementTests.Awarded(n, fixer, team);
            Operation op = ProcurementTests.Op(n, c);
            T.Check(op.careerEligible, "an operation started now is eligible");
            op.careerEligible = false; // what a pre-2.75 save loads as
            int score = team.reputation.score;
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Eq(ContractStatus.Fulfilled, c.status, "it finishes as it always did");
            T.Eq(score, team.reputation.score, "and earns no reputation");
            T.Eq(0L, Sim(team).career.Classified, "invents no record");
            T.Check(!op.careerOutcomeApplied, "never marked applied");
            T.Check(CreditOf(c) > 0, "but the contractor was still paid (money is not career credit)");
        }

        private static void ReputationFarming()
        {
            // Trivial work builds a local name at most; middling work stops short of a famous one; only the most
            // dangerous work reaches legend, and slowly. No number of safe hauls can.
            int[] stop = new int[3];
            float[] dangers = { 0.05f, 0.3f, 0.95f };
            for (int d = 0; d < dangers.Length; d++)
            {
                int score = 0;
                for (int i = 0; i < 20000; i++) score += CareerPolicy.ReputationGain(dangers[d], OutcomeBand.Triumph, 100, 100, score);
                stop[d] = score;
            }
            T.Check(stop[0] >= CareerPolicy.LocalAt && stop[0] < CareerPolicy.EstablishedAt, "twenty thousand trivial triumphs make a local name and no more (" + stop[0] + ")");
            T.Check(stop[1] < CareerPolicy.FamousAt, "twenty thousand ordinary jobs never make a famous name (" + stop[1] + ")");
            T.Check(stop[2] >= CareerPolicy.LegendaryAt, "the most dangerous work can make a legend (" + stop[2] + ")");
            int hard = 0, jobs = 0;
            while (hard < CareerPolicy.LegendaryAt && jobs < 100000)
            {
                hard += CareerPolicy.ReputationGain(0.95f, OutcomeBand.Success, 100, 100, hard);
                jobs++;
            }
            T.Check(jobs > 40, "and it takes dozens of such jobs (" + jobs + ")");
        }

        private static void ReputationBounds()
        {
            foreach (OutcomeBand band in Enum.GetValues(typeof(OutcomeBand)))
            {
                for (float danger = -0.5f; danger <= 1.5f; danger += 0.125f)
                {
                    foreach (int score in new[] { 0, 50, 300, 5000, CareerPolicy.ScoreCap })
                    {
                        int g = CareerPolicy.ReputationGain(danger, band, 40, 100, score);
                        T.Check(g >= 0 && g <= 60, "gain " + g + " for " + band + " at danger " + danger + " score " + score + " is in range");
                    }
                }
            }
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Solo(n);
            n.ctx.Career.AddReputation(a, int.MaxValue, false);
            T.Eq(CareerPolicy.ScoreCap, a.reputation.score, "a huge grant saturates at the cap");
            T.Eq(0, n.ctx.Career.AddReputation(a, 10, false), "nothing more can be added");
            T.Eq(0, n.ctx.Career.AddReputation(a, -50, false), "and nothing is ever taken away");
            T.Eq(CareerPolicy.ScoreCap, a.reputation.score, "still capped");
            a.reputation.SetScore(-5);
            T.Eq(0, a.reputation.score, "a negative score is clamped");
        }

        private static void EventsOnlyMeaningful()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Solo(n);
            int before = n.recorder.Count(EventKeys.ContractorFameChanged);
            n.ctx.Career.AddReputation(a, 17, false);
            T.Eq(before, n.recorder.Count(EventKeys.ContractorFameChanged), "+17 hidden points publish nothing");
            n.ctx.Career.AddReputation(a, 90, false);
            T.Eq(before + 1, n.recorder.Count(EventKeys.ContractorFameChanged), "crossing into Local publishes one event");
            n.ctx.Career.AddReputation(a, 10, false);
            T.Eq(before + 1, n.recorder.Count(EventKeys.ContractorFameChanged), "staying in the band publishes none");
            n.ctx.Career.AddReputation(a, 700, false);
            T.Eq(before + 2, n.recorder.Count(EventKeys.ContractorFameChanged), "Local → Famous crosses two bands in one grant: still one event");
            T.Check(n.ledger.records.Exists(r => r.typeKey == EventKeys.ContractorFameChanged), "Famous is remembered in history");
            int records = n.ledger.records.FindAll(r => r.typeKey == EventKeys.ContractorFameChanged).Count;
            T.Eq(1, records, "only the band worth remembering is a history record (Local is a counter)");
            T.Check(!Array.Exists(LetterConsumer.ConsumedKeys, k => k == EventKeys.ContractorFameChanged || k == EventKeys.ContractorAdvanced), "no letter for either");
            T.Check(!Array.Exists(ContractLetterConsumer.ConsumedKeys, k => k == EventKeys.ContractorFameChanged || k == EventKeys.ContractorAdvanced), "nor a contract letter");
        }

        // ================================================================== B. record

        private static void RecordOnce()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            Contract c = Job(n, fixer, team, OutcomeBand.Success);
            CareerRecord r = Sim(team).career;
            T.Eq(1L, r.Classified, "one classified result");
            T.Eq(1, r.successes, "a success");
            Operation op = ProcurementTests.Op(n, c);
            T.Check(op.careerOutcomeApplied, "marked applied");
            int score = team.reputation.score;
            for (int i = 0; i < 3; i++)
            {
                T.Check(!n.ctx.Career.CommitOutcome(op) && !n.ctx.Career.CommitOutcome(op, true), "repeat " + i + ": nothing to apply");
                n.ctx.Operations.Finish(op);
                n.ctx.Operations.Abort(op, "Late");
            }
            T.Eq(1L, r.Classified, "still one");
            T.Eq(score, team.reputation.score, "and the score never moved");
            T.Eq(1, n.ctx.Career.counters.outcomesApplied, "the service counted exactly one application");
            T.Check(Sim(team).opsCompleted >= 1 && r.Classified <= Sim(team).opsCompleted - r.legacyResolved, "the record never classifies more jobs than were resolved");
        }

        private static void RecordBands()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            Dictionary<OutcomeBand, NetworkActor> who = new Dictionary<OutcomeBand, NetworkActor>();
            OutcomeBand[] bands = { OutcomeBand.Triumph, OutcomeBand.Success, OutcomeBand.CostlySuccess, OutcomeBand.Partial, OutcomeBand.Failure, OutcomeBand.Disaster };
            foreach (OutcomeBand b in bands)
            {
                NetworkActor team = ProcurementTests.Reliable(n);
                who[b] = team;
                Job(n, fixer, team, b);
            }
            T.Eq(1, Sim(who[OutcomeBand.Triumph]).career.triumphs, "triumph");
            T.Eq(1, Sim(who[OutcomeBand.Success]).career.successes, "success");
            T.Eq(1, Sim(who[OutcomeBand.CostlySuccess]).career.successes, "a costly success counts as a success");
            T.Eq(1, Sim(who[OutcomeBand.Partial]).career.partials, "partial");
            T.Eq(1, Sim(who[OutcomeBand.Failure]).career.failures, "failure");
            T.Eq(1, Sim(who[OutcomeBand.Disaster]).career.disasters, "disaster");
            foreach (OutcomeBand b in bands) T.Eq(1L, Sim(who[b]).career.Classified, b + ": exactly one job classified");
            Operation tOp = n.ctx.operations.operations.Find(o => o.contractor == who[OutcomeBand.Triumph].id);
            T.Eq(CareerPolicy.ScaledDanger(CareerService.DangerOf(tOp)), Sim(who[OutcomeBand.Triumph]).career.highestDanger, "the highest danger is the finished work's frozen danger");
            T.Eq(0, Sim(who[OutcomeBand.Failure]).career.highestDanger, "a failure sets none: it counts work at least partly done");
            T.Check(Sim(who[OutcomeBand.Triumph]).career.lastOutcomeTick > 0, "the last outcome tick is set");
        }

        private static void RecordCasualties()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            Contract c = Job(n, fixer, team, OutcomeBand.Disaster);
            Operation op = ProcurementTests.Op(n, c);
            OperationOutcome o = op.outcome;
            CareerRecord r = Sim(team).career;
            T.Check(o.Killed + o.Wounded + o.Captured + o.Missing > 0, "the disaster cost people (" + o.Killed + " killed, " + o.Wounded + " wounded, " + o.Captured + " captured, " + o.Missing + " missing)");
            T.Eq(o.Killed + o.Wounded + o.Captured + o.Missing, r.casualtiesTaken, "casualties taken equal the committed outcome, once");
            T.Eq(o.Killed, r.peopleLost, "people lost = killed");
            T.Eq(o.Captured, r.captured, "captured");
            T.Eq(o.Missing, r.missing, "missing");
            n.ctx.Career.CommitOutcome(op);
            n.ctx.Operations.Finish(op);
            T.Eq(o.Killed + o.Wounded + o.Captured + o.Missing, r.casualtiesTaken, "a repeat adds nothing");

            // A second job adds its own, never the first again.
            NetworkActor other = ProcurementTests.Reliable(n);
            Contract c2 = Job(n, fixer, other, OutcomeBand.Disaster);
            OperationOutcome o2 = ProcurementTests.Op(n, c2).outcome;
            T.Eq(o2.Killed + o2.Wounded + o2.Captured + o2.Missing, Sim(other).career.casualtiesTaken, "another contractor's record holds only its own job");
            T.Eq(o.Killed + o.Wounded + o.Captured + o.Missing, r.casualtiesTaken, "and the first is untouched");
        }

        private static void RecordLegacy()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            Sim(team).opsCompleted = 7;
            Sim(team).career.legacyResolved = 7; // what the migration sets
            CareerRecord r = Sim(team).career;
            T.Eq(0L, r.Classified, "nothing classified from before");
            T.Eq(7L, r.Resolved, "seven jobs resolved");
            Job(n, fixer, team, OutcomeBand.Success);
            T.Eq(1L, r.Classified, "only the new job is classified");
            T.Eq(7, r.legacyResolved, "the legacy count is never touched");
            T.Eq(8L, r.Resolved, "and the total is seven plus one");
            T.Check(r.Classified <= Sim(team).opsCompleted - r.legacyResolved, "classified ≤ resolved since the record began");
        }

        private static void RecordReplacement()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Basic");
            NetworkActor first = ProcurementTests.Reliable(n);
            NetworkActor second = ProcurementTests.Reliable(n);
            first.reputation.SetScore(0);
            second.reputation.SetScore(0);
            Contract parent = ProcurementTests.Post(n, fixer, "TestSteel", 150, first);
            Offer o = ProcurementTests.Bid(n, parent);
            T.Check(o != null && n.ctx.Procurement.Accept(o.id, false).ok, "accepted");
            Operation parentOp = ProcurementTests.Op(n, parent);
            n.ctx.Contractors.EndActor(first, "Test");
            ProcurementTests.RunUntil(n, () => parent.IsTerminal, 20);
            T.Check(parentOp.status == OpStatus.Aborted && parentOp.outcome == null, "the first operation was aborted before any outcome");
            T.Check(!parentOp.careerOutcomeApplied, "an abort before an outcome gives no career result");
            T.Eq(1, parent.lineage.children.Count, "a replacement took over");
            Contract child = n.ctx.contracts.Get(parent.lineage.children[0]);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            ProcurementTests.RunUntil(n, () => child.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Eq(ContractStatus.Fulfilled, child.status, "the replacement delivers");
            Operation childOp = ProcurementTests.Op(n, child);
            T.Check(childOp.contractor != first.id, "a different contractor ran it");
            NetworkActor did = n.ctx.actors.Get(childOp.contractor);
            T.Eq(1L, Sim(did).career.Classified, "the replacement's record holds the job");
            T.Eq(0L, Sim(first).career.Classified, "the lost contractor's record holds nothing");
            T.Check(did.reputation.score > 0, "and the replacement earned the reputation");
            T.Eq(0, first.reputation.score, "the lost one earned none");
        }

        private static void RecordAbortBefore()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            Contract c = ProcurementTests.Awarded(n, fixer, team);
            Operation op = ProcurementTests.Op(n, c);
            T.Check(op.outcome == null, "no outcome yet");
            T.Check(n.ctx.Procurement.Cancel(c.id).ok, "cancelled");
            T.Eq(OpStatus.Aborted, op.status, "the operation aborted");
            T.Check(!op.careerOutcomeApplied, "no career result for work that never happened");
            T.Eq(0L, Sim(team).career.Classified, "nothing on the record");
            T.Eq(CareerPolicy.FloorOf(FameBand.Local), team.reputation.score, "no reputation");
        }

        private static void RecordTroubled()
        {
            // Resolved while Troubled: nothing yet. Found: applies at the end of the work. Written off: applies once.
            for (int branch = 0; branch < 2; branch++)
            {
                bool found = branch == 0;
                TestNet n = ProcurementTests.World(0);
                NetworkActor fixer = ProcurementTests.Fixer(n);
                NetworkActor team = ProcurementTests.Reliable(n);
                ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
                ProcurementDevOverrides.forceTroubled = "Missing";
                ProcurementDevOverrides.forceSecured = 0;
                Contract c = ProcurementTests.Awarded(n, fixer, team);
                Operation op = ProcurementTests.Op(n, c);
                ProcurementTests.RunUntil(n, () => op.status == OpStatus.Troubled || c.IsTerminal);
                T.Eq(OpStatus.Troubled, op.status, "branch " + branch + ": resolved but Troubled");
                T.Check(!op.careerOutcomeApplied, "branch " + branch + ": not applied at the first resolution");
                T.Eq(0L, Sim(team).career.Classified, "branch " + branch + ": nothing on the record yet");
                ProcurementDevOverrides.forceTroubledFound = found;
                n.ctx.Operations.DevAdvance(op);
                ProcurementDevOverrides.Clear();
                if (found)
                {
                    T.Check(!op.careerOutcomeApplied || op.IsFinished, "found: applied only when the work ends");
                    ProcurementTests.RunUntil(n, () => c.IsTerminal);
                }
                T.Check(op.IsFinished && op.careerOutcomeApplied, "branch " + branch + ": applied once the operation is over");
                T.Eq(1L, Sim(team).career.Classified, "branch " + branch + ": exactly one result");
                n.ctx.Operations.DevAdvance(op);
                T.Eq(1L, Sim(team).career.Classified, "branch " + branch + ": nothing repeats");
            }
            // Written off: a success-class band counts as a failure (nothing came home).
            TestNet w = ProcurementTests.World(0);
            NetworkActor f2 = ProcurementTests.Fixer(w);
            NetworkActor t2 = ProcurementTests.Reliable(w);
            ProcurementDevOverrides.forceBand = OutcomeBand.Success;
            ProcurementDevOverrides.forceTroubled = "Missing";
            Contract c2 = ProcurementTests.Awarded(w, f2, t2);
            Operation op2 = ProcurementTests.Op(w, c2);
            ProcurementTests.RunUntil(w, () => op2.status == OpStatus.Troubled || c2.IsTerminal);
            ProcurementDevOverrides.forceTroubledFound = false;
            w.ctx.Operations.DevAdvance(op2);
            ProcurementDevOverrides.Clear();
            T.Eq(1, Sim(t2).career.failures, "a written-off group is a failure on the record");
            T.Eq(CareerPolicy.FloorOf(FameBand.Local), t2.reputation.score, "and earns nothing");
        }

        private static void RecordRepeats()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            ProcurementDevOverrides.forceDeliveryFailures = 2;
            Contract c = ProcurementTests.Awarded(n, fixer, team);
            Operation op = ProcurementTests.Op(n, c);
            for (int i = 0; i < 6 && !c.IsTerminal; i++) n.ctx.Operations.DevAdvance(op);
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Eq(ContractStatus.Fulfilled, c.status, "delivered after retries");
            T.Eq(1L, Sim(team).career.Classified, "delivery retries and dev advances applied the result once");
            int score = team.reputation.score;
            n.ctx.Operations.DevAdvance(op);
            n.ctx.Operations.Finish(op);
            T.Eq(score, team.reputation.score, "and nothing moved afterwards");
        }

        /// <summary>A Troubled Disaster that is written off: the committed outcome stays a Disaster, the career says Failure.</summary>
        private static void RecordWrittenOffDisaster()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            team.reputation.SetScore(0);
            ProcurementDevOverrides.forceBand = OutcomeBand.Disaster;
            ProcurementDevOverrides.forceTroubled = "Missing";
            Contract c = ProcurementTests.Awarded(n, fixer, team);
            Operation op = ProcurementTests.Op(n, c);
            ProcurementTests.RunUntil(n, () => op.status == OpStatus.Troubled || c.IsTerminal);
            T.Eq(OpStatus.Troubled, op.status, "a Disaster, and Troubled");
            T.Eq(OutcomeBand.Disaster, op.outcome.band, "the resolver rolled a Disaster");
            T.Eq(0L, Sim(team).career.Classified, "nothing on the record while it is still Troubled");
            ProcurementDevOverrides.forceTroubledFound = false;
            n.ctx.Operations.DevAdvance(op);
            ProcurementDevOverrides.Clear();
            CareerRecord r = Sim(team).career;
            T.Check(op.IsFinished && op.careerOutcomeApplied, "written off: the career result is applied");
            T.Eq(1, r.failures, "the written-off job counts as a Failure");
            T.Eq(0, r.disasters, "and NOT as a Disaster");
            T.Eq(0, r.triumphs + r.successes + r.partials, "nothing else");
            T.Eq(1L, r.Classified, "exactly one classified job");
            T.Eq(0, team.reputation.score, "no reputation");
            T.Eq(0, r.reputationEarned, "none earned");
            T.Eq(0, r.highestDanger, "no danger counted: nothing was done");
            T.Eq(OutcomeBand.Disaster, op.outcome.band, "the committed OperationOutcome is historical truth and still says Disaster");
            OperationOutcome o = op.outcome;
            T.Eq(o.Killed + o.Wounded + o.Captured + o.Missing, r.casualtiesTaken, "its casualties are counted once, from the committed outcome");
            // Exactly once.
            T.Check(!n.ctx.Career.CommitOutcome(op, true) && !n.ctx.Career.CommitOutcome(op, false), "no second application, whatever the flag");
            n.ctx.Operations.DevAdvance(op);
            n.ctx.Operations.Finish(op);
            n.ctx.Career.Validate(new List<string>());
            T.Check(r.failures == 1 && r.disasters == 0 && r.Classified == 1, "repeats, a validation and a dev advance change nothing");
            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            CareerRecord again = Sim(n.ctx.actors.Get(team.id)).career;
            T.Check(again.failures == 1 && again.disasters == 0, "and so does a reload");
            T.Check(WrittenOffDerivation(n, loaded), "a retry derives 'written off' from the operation itself");
        }

        private static bool WrittenOffDerivation(TestNet n, NetworkState s)
        {
            foreach (Operation op in s.operations.operations) if (op.outcome != null && op.outcome.troubledKey != null) return CareerService.WasWrittenOff(op);
            return false;
        }

        private static bool SameRecord(CareerRecord a, CareerRecord b)
        {
            return a.legacyResolved == b.legacyResolved && a.triumphs == b.triumphs && a.successes == b.successes && a.partials == b.partials && a.failures == b.failures && a.disasters == b.disasters
                && a.highestDanger == b.highestDanger && a.careerEarnings == b.careerEarnings && a.casualtiesTaken == b.casualtiesTaken && a.peopleLost == b.peopleLost && a.captured == b.captured
                && a.missing == b.missing && a.reputationEarned == b.reputationEarned && a.lastOutcomeTick == b.lastOutcomeTick && a.lastAdvancementTick == b.lastAdvancementTick && a.advancementCount == b.advancementCount;
        }

        /// <summary>A finished operation whose career result was never applied (as if the lifecycle commit had been missed or failed).</summary>
        private static Operation FinishedWithoutCareerResult(TestNet n, NetworkActor fixer, NetworkActor who, OutcomeBand band, bool troubledWrittenOff)
        {
            ProcurementDevOverrides.forceBand = band;
            if (troubledWrittenOff) ProcurementDevOverrides.forceTroubled = "Missing";
            else ProcurementDevOverrides.forceNotTroubled = true;
            Contract c = ProcurementTests.Awarded(n, fixer, who);
            Operation op = ProcurementTests.Op(n, c);
            op.careerEligible = false; // the lifecycle's commit is skipped, exactly as for an old operation
            if (troubledWrittenOff)
            {
                ProcurementTests.RunUntil(n, () => op.status == OpStatus.Troubled || c.IsTerminal);
                ProcurementDevOverrides.forceTroubledFound = false;
                n.ctx.Operations.DevAdvance(op);
            }
            else ProcurementTests.RunUntil(n, () => c.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Check(op.IsFinished && !op.careerOutcomeApplied && op.outcome != null, "the operation finished and no career result was applied");
            op.careerEligible = true;
            return op;
        }

        /// <summary>
        /// "Applied" means applied: a commit that cannot complete changes NOTHING (no counter, no score, no event), leaves the
        /// flag false, and is applied exactly once when the fault is repaired (the next validation, or any retry).
        /// </summary>
        private static void RecordFailedCommitRetryable()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            team.reputation.SetScore(98);
            Operation op = FinishedWithoutCareerResult(n, fixer, team, OutcomeBand.Triumph, false);
            ContractorSimulation sim = Sim(team);
            PublicReputation rep = team.reputation;
            CareerRecord realRecord = sim.career;
            CareerRecord before = realRecord.Clone();
            int fameEvents = n.recorder.Count(EventKeys.ContractorFameChanged), failures0 = n.ctx.Career.counters.failures, applied0 = n.ctx.Career.counters.outcomesApplied;

            // Fault 1: the reputation is missing (corrupt state). The record would have been changed first by a commit that marks itself done early.
            team.reputation = null;
            T.Check(!n.ctx.Career.CommitOutcome(op), "the commit fails and says so");
            team.reputation = rep;
            T.Check(!op.careerOutcomeApplied, "the flag is NOT set");
            T.Check(SameRecord(before, sim.career), "no counter of the record changed (no partial state)");
            T.Eq(98, rep.score, "the reputation is untouched");
            T.Eq(fameEvents, n.recorder.Count(EventKeys.ContractorFameChanged), "no fame event was published");
            T.Eq(failures0 + 1, n.ctx.Career.counters.failures, "the failure was counted");
            T.Eq(applied0, n.ctx.Career.counters.outcomesApplied, "and nothing counted as applied");

            // Fault 2: the career record is missing.
            sim.career = null;
            T.Check(!n.ctx.Career.CommitOutcome(op), "a second fault fails too");
            sim.career = realRecord;
            T.Check(!op.careerOutcomeApplied && SameRecord(before, sim.career) && rep.score == 98, "and again nothing changed and nothing is marked applied");

            // The fault is repaired: validation applies it, exactly once.
            List<string> findings = new List<string>();
            n.ctx.Career.Validate(findings);
            T.Check(findings.Exists(f => f.Contains("career result")), "the validation found and applied it (" + string.Join("; ", findings.ToArray()) + ")");
            T.Check(op.careerOutcomeApplied, "now it is applied");
            T.Eq(1L, sim.career.Classified, "exactly one result");
            T.Eq(1, sim.career.triumphs, "a triumph");
            int expected = CareerPolicy.ReputationGain(CareerService.DangerOf(op), OutcomeBand.Triumph, op.outcome.secured, op.outcome.requested, 98);
            T.Check(expected > 0, "it earns reputation (" + expected + ")");
            T.Eq(98 + expected, rep.score, "exactly the policy's figure, once");
            T.Eq(fameEvents + 1, n.recorder.Count(EventKeys.ContractorFameChanged), "and one fame event for the band it crossed (Unknown to Local)");
            T.Eq(applied0 + 1, n.ctx.Career.counters.outcomesApplied, "counted once");

            // Nothing repeats: another retry, another validation, a reload.
            T.Check(!n.ctx.Career.CommitOutcome(op), "no retry applies it again");
            List<string> second = new List<string>();
            n.ctx.Career.Validate(second);
            T.Check(!second.Exists(f => f.Contains("career result")), "a second validation finds nothing to do");
            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            NetworkActor again = n.ctx.actors.Get(team.id);
            n.ctx.Career.Validate(new List<string>());
            T.Check(Sim(again).career.Classified == 1 && again.reputation.score == 98 + expected, "and a reload followed by a validation changes nothing");
            T.Eq(fameEvents + 1, n.recorder.Count(EventKeys.ContractorFameChanged), "still exactly one fame event");

            // A written-off Disaster whose commit was missed is retried as what it is: a Failure.
            TestNet w = ProcurementTests.World(0);
            NetworkActor f2 = ProcurementTests.Fixer(w);
            NetworkActor t2 = ProcurementTests.Reliable(w);
            Operation op2 = FinishedWithoutCareerResult(w, f2, t2, OutcomeBand.Disaster, true);
            T.Check(CareerService.WasWrittenOff(op2) && op2.outcome.band == OutcomeBand.Disaster, "a written-off Disaster");
            w.ctx.Career.Validate(new List<string>());
            T.Check(Sim(t2).career.failures == 1 && Sim(t2).career.disasters == 0 && op2.careerOutcomeApplied, "the retry classifies it as a Failure, once");
        }

        private sealed class ThrowingConsumer : IEventConsumer
        {
            public int calls;
            public string Name => "ThrowingConsumer";

            public void Handle(NetworkEvent evt)
            {
                calls++;
                throw new InvalidOperationException("a consumer of the fame event failed");
            }
        }

        /// <summary>The fame event is published AFTER the durable commit: a failing consumer cannot undo, retry or duplicate the result.</summary>
        private static void RecordFailedCommitNoDuplicateEvent()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            team.reputation.SetScore(98);
            ThrowingConsumer thrower = new ThrowingConsumer();
            n.bus.Register(ConsumerOrder.Presentation, thrower, EventKeys.ContractorFameChanged);
            Operation op = FinishedWithoutCareerResult(n, fixer, team, OutcomeBand.Triumph, false);

            // A failed commit publishes nothing.
            PublicReputation rep = team.reputation;
            team.reputation = null;
            n.ctx.Career.CommitOutcome(op);
            team.reputation = rep;
            T.Eq(0, thrower.calls, "the failed commit published no fame event");
            T.Eq(0, n.recorder.Count(EventKeys.ContractorFameChanged), "to nobody");

            // The repaired commit publishes one, whose consumer throws: the result still stands, once.
            T.Check(n.ctx.Career.CommitOutcome(op), "the repaired commit applies");
            T.Check(op.careerOutcomeApplied, "applied");
            T.Eq(1, thrower.calls, "the event reached the (throwing) consumer once");
            T.Eq(1, n.recorder.Count(EventKeys.ContractorFameChanged), "and every other consumer once");
            T.Check(rep.score > 98 && rep.fame == FameBand.Local, "the reputation stands (" + rep.score + ")");
            T.Eq(1L, Sim(team).career.Classified, "the record holds one result");
            T.Check(n.diag.failedConsumers.Count >= 1, "the bus contained the consumer's exception");
            int score = rep.score;
            T.Check(!n.ctx.Career.CommitOutcome(op), "a failing consumer does not make the commit retry");
            n.ctx.Career.Validate(new List<string>());
            T.Check(Sim(team).career.Classified == 1 && rep.score == score && thrower.calls == 1 && n.recorder.Count(EventKeys.ContractorFameChanged) == 1, "nothing is duplicated: not the result, not the event");
        }

        /// <summary>
        /// "Applied" means applied: with no contractor actor, or no ContractorSimulation, to receive the career there is no durable
        /// career mutation, so the flag stays false, nothing changes, the failure is visible, the lifecycle carries on, and the
        /// result is applied exactly once when the target is back. It is never marked applied just to make a validation quiet.
        /// </summary>
        private static void RecordMissingTargetNeverApplied()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            team.reputation.SetScore(98);
            Operation op = FinishedWithoutCareerResult(n, fixer, team, OutcomeBand.Triumph, false);
            ContractorSimulation sim = Sim(team);
            CareerRecord before = sim.career.Clone();
            ActorId realContractor = op.contractor;
            int fameEvents = n.recorder.Count(EventKeys.ContractorFameChanged), failures0 = n.ctx.Career.counters.failures, applied0 = n.ctx.Career.counters.outcomesApplied;

            // The contractor has no ContractorSimulation.
            team.components.Remove(sim);
            T.Check(!n.ctx.Career.CommitOutcome(op), "no simulation: the commit fails and says so");
            T.Check(!op.careerOutcomeApplied, "the flag is NOT set");
            T.Check(SameRecord(before, sim.career) && team.reputation.score == 98, "no career state changed (no record, no score)");
            T.Eq(fameEvents, n.recorder.Count(EventKeys.ContractorFameChanged), "no fame event");
            T.Eq(failures0 + 1, n.ctx.Career.counters.failures, "the failure is counted");
            T.Eq(applied0, n.ctx.Career.counters.outcomesApplied, "and nothing counted as applied");
            T.Check(T.netLog.Exists(l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("MissingContractorSimulation")), "it is visible: one warning that names the missing simulation");
            List<string> unresolved = new List<string>();
            n.ctx.Career.Validate(unresolved);
            T.Check(unresolved.Exists(f => f.Contains("could not be applied")), "a validation reports it as unresolved (" + string.Join("; ", unresolved.ToArray()) + ")");
            T.Check(!op.careerOutcomeApplied && SameRecord(before, sim.career), "and still does not mark it applied");
            n.ctx.Operations.Finish(op); // a repeated terminal call is harmless
            T.Check(!op.careerOutcomeApplied && op.IsFinished, "the operation's lifecycle is unaffected");
            team.components.Add(sim);

            // The operation names a contractor that does not exist.
            NetLog.ResetOnceKeys();
            op.contractor = ActorId.None;
            T.Check(!n.ctx.Career.CommitOutcome(op), "no actor: the commit fails and says so");
            T.Check(!op.careerOutcomeApplied && SameRecord(before, sim.career) && team.reputation.score == 98, "nothing changed and nothing is marked applied");
            T.Eq(failures0 + 3, n.ctx.Career.counters.failures, "counted again (the unresolved validation counted once)");
            T.Check(T.netLog.Exists(l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("MissingCareerActor")), "one warning that names the missing actor");
            T.Eq(fameEvents, n.recorder.Count(EventKeys.ContractorFameChanged), "still no fame event");
            op.contractor = realContractor;

            // The target is back: the result is applied exactly once.
            List<string> findings = new List<string>();
            n.ctx.Career.Validate(findings);
            T.Check(findings.Exists(f => f.Contains("applied now")), "the validation applies it (" + string.Join("; ", findings.ToArray()) + ")");
            T.Check(op.careerOutcomeApplied, "now it is applied");
            T.Eq(1L, sim.career.Classified, "exactly one result");
            T.Eq(1, sim.career.triumphs, "a triumph");
            int expected = CareerPolicy.ReputationGain(CareerService.DangerOf(op), OutcomeBand.Triumph, op.outcome.secured, op.outcome.requested, 98);
            T.Eq(98 + expected, team.reputation.score, "exactly the policy's figure, once");
            T.Eq(fameEvents + 1, n.recorder.Count(EventKeys.ContractorFameChanged), "and one fame event for the band it crossed");
            T.Eq(applied0 + 1, n.ctx.Career.counters.outcomesApplied, "counted once");
            T.Check(!n.ctx.Career.CommitOutcome(op), "no retry applies it again");
            List<string> second = new List<string>();
            n.ctx.Career.Validate(second);
            T.Check(!second.Exists(f => f.Contains("career result")), "a second validation finds nothing to do");
            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            n.ctx.Career.Validate(new List<string>());
            NetworkActor again = n.ctx.actors.Get(team.id);
            T.Check(Sim(again).career.Classified == 1 && again.reputation.score == 98 + expected, "a reload followed by a validation changes nothing");
            T.Eq(fameEvents + 1, n.recorder.Count(EventKeys.ContractorFameChanged), "still exactly one fame event");
        }

        // ================================================================== C. money

        private static void WealthNormal()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            Books(n, "at the start");
            long credit0 = n.ctx.Career.counters.Flow(FundsFlow.Credit);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            Contract c = ProcurementTests.Awarded(n, fixer, team);
            float share = QuoteShare(n, c);
            T.Check(share > 0f && share < 1f, "the quote splits between contractor and Fixer (" + share + ")");
            MoneyRecord deposit = c.ledger[0];
            T.Eq((int)(deposit.silver * share), deposit.contractorSilver, "the deposit credited the contractor's share, on its own ledger record");
            T.Eq((int)(c.ledger[0].silver * share), (int)(n.ctx.Career.counters.Flow(FundsFlow.Credit) - credit0), "and that is exactly what reached the funds");
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Eq(ContractStatus.Fulfilled, c.status, "delivered");
            int expected = 0;
            foreach (MoneyRecord m in c.ledger)
            {
                if (m.direction != MoneyDirection.PlayerPaid || m.purpose == MoneyPurpose.InsurancePremium) continue;
                expected += (int)(m.silver * share);
            }
            T.Eq(expected, CreditOf(c), "deposit + premium + balance, each at the contractor's share");
            T.Eq(expected, c.ContractorHeld(), "held = credited (nothing taken back)");
            T.Eq(expected, (int)(n.ctx.Career.counters.Flow(FundsFlow.Credit) - credit0), "the funds were credited exactly once, never twice");
            T.Eq(expected, Sim(team).career.careerEarnings, "career earnings are the real contractor earnings");
            T.Check(CreditOf(c) < c.ExternalCharged(), "less than the player paid (the Fixer kept its part)");
            Books(n, "after the job");
        }

        private static void WealthFixerFee()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium");
            NetworkActor team = ProcurementTests.Reliable(n);
            Contract c = Job(n, fixer, team, OutcomeBand.Triumph);
            Offer o = n.ctx.contracts.Get(c.acceptedOffer);
            T.Check(o.quote.BrokerShare() > 0, "the Fixer takes a fee (" + o.quote.BrokerShare() + ")");
            T.Check(CreditOf(c) <= o.quote.ContractorShare(), "the contractor holds no more than its own share of the price (" + CreditOf(c) + " of " + o.quote.ContractorShare() + ")");
            T.Check(c.ExternalCharged() - CreditOf(c) >= o.quote.BrokerShare(), "the Fixer's fee is not contractor wealth");
            Books(n, "fixer fee");
        }

        private static void WealthInsurance()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Basic");
            NetworkActor team = ProcurementTests.Reliable(n);
            Contract c = Job(n, fixer, team, OutcomeBand.Failure, 150, true, 0);
            T.Eq(ContractStatus.Failed, c.status, "the job failed");
            MoneyRecord premium = c.ledger.Find(m => m.purpose == MoneyPurpose.InsurancePremium);
            MoneyRecord payout = c.ledger.Find(m => m.purpose == MoneyPurpose.InsurancePayout);
            T.Check(premium != null && payout != null, "an insurance premium and a payout (the insurer's money)");
            if (premium == null || payout == null) return;
            T.Eq(0, premium.contractorSilver, "the premium never reached the contractor");
            T.Eq(0, payout.contractorSilver, "and the payout was not taken from it");
            T.Eq(0L, n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "nothing was clawed back");
            MoneyRecord deposit = c.ledger.Find(m => m.purpose == MoneyPurpose.Deposit);
            T.Eq(deposit.contractorSilver, c.ContractorHeld(), "the contractor kept what it was paid (it failed, the deposit is lost)");
            Books(n, "insured failure");
        }

        private static void WealthVoid()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Basic");
            NetworkActor team = ProcurementTests.Reliable(n);
            int funds0 = Sim(team).funds;
            Contract c = ProcurementTests.Awarded(n, fixer, team, "TestSteel", 150, true);
            int paid = CreditOf(c);
            T.Check(paid > 0 && n.ctx.Career.counters.Flow(FundsFlow.Credit) == paid, "the contractor was paid at award");
            n.ctx.Procurement.Void(c, Causes.DefMissing);
            T.Eq(ContractStatus.Voided, c.status, "voided");
            T.Eq(c.ExternalCharged(), c.ExternalRefunded(), "the player got every silver back");
            T.Eq(0, c.ContractorHeld(), "so the contractor holds none of it: a technical invalidation is no windfall");
            T.Eq(paid, ClawOf(c), "exactly what it was paid was taken back");
            T.Eq(0, Sim(team).career.careerEarnings, "no career earnings from a voided contract");
            T.Eq(n.ctx.Career.counters.Flow(FundsFlow.Credit), -n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "credit and clawback cancel");
            Books(n, "void");
        }

        private static void WealthRefund()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium");
            NetworkActor team = ProcurementTests.Reliable(n);
            Contract c = ProcurementTests.Awarded(n, fixer, team, "TestSteel", 150, false);
            int held = c.ContractorHeld();
            int basis = c.OwnBearingRemaining();
            T.Check(held > 0 && basis > 0, "paid at award");
            T.Check(n.ctx.Procurement.Cancel(c.id).ok, "cancelled while preparing");
            MoneyRecord refund = c.ledger.Find(m => m.direction == MoneyDirection.PlayerRefunded);
            T.Check(refund != null && refund.silver > 0 && refund.silver < basis, "a partial refund by the Fixer's policy (" + (refund != null ? refund.silver : 0) + " of " + basis + ")");
            if (refund == null) return;
            int claw = (int)((long)held * refund.silver / basis);
            T.Eq(-claw, refund.contractorSilver, "the contractor gives back its proportion of the refund, on the refund's own ledger record");
            T.Eq(held - claw, c.ContractorHeld(), "and keeps the rest");
            T.Check(c.ContractorHeld() > 0 && c.ContractorHeld() < held, "a partial refund leaves a partial hold");
            T.Eq(held - claw, Sim(team).career.careerEarnings, "career earnings follow");
            Books(n, "cancel");

            // A refund that cannot be delivered yet is retried; the retry takes nothing more from the contractor.
            TestNet w = ProcurementTests.World(0);
            NetworkActor f2 = ProcurementTests.Fixer(w, "Premium");
            NetworkActor t2 = ProcurementTests.Reliable(w);
            Contract c2 = ProcurementTests.Awarded(w, f2, t2);
            w.pay.canRefund = false;
            w.ctx.Procurement.Cancel(c2.id);
            MoneyRecord r2 = c2.ledger.Find(x => x.direction == MoneyDirection.PlayerRefunded);
            T.Check(r2 != null && r2.pending, "the refund is pending");
            int heldAfter = c2.ContractorHeld();
            long claw0 = w.ctx.Career.counters.Flow(FundsFlow.ClawBack);
            w.pay.canRefund = true;
            w.AdvanceTo(w.clock.Now + 3 * Ticks.PerDay);
            T.Check(!r2.pending, "the retry delivered it");
            T.Eq(heldAfter, c2.ContractorHeld(), "the retry took nothing more");
            T.Eq(claw0, w.ctx.Career.counters.Flow(FundsFlow.ClawBack), "no second clawback");
        }

        private static void WealthReplacement()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Basic");
            NetworkActor first = ProcurementTests.Reliable(n);
            NetworkActor second = ProcurementTests.Reliable(n);
            Contract parent = ProcurementTests.Post(n, fixer, "TestSteel", 150, first);
            Offer o = ProcurementTests.Bid(n, parent);
            T.Check(o != null && n.ctx.Procurement.Accept(o.id, true).ok, "accepted");
            int firstPaid = parent.ContractorHeld();
            T.Check(firstPaid > 0, "the first contractor was paid its deposit share");
            long credit0 = n.ctx.Career.counters.Flow(FundsFlow.Credit);
            n.ctx.Contractors.EndActor(first, "Test");
            ProcurementTests.RunUntil(n, () => parent.IsTerminal, 20);
            T.Eq(1, parent.lineage.children.Count, "replaced");
            Contract child = n.ctx.contracts.Get(parent.lineage.children[0]);
            T.Eq(credit0, n.ctx.Career.counters.Flow(FundsFlow.Credit), "carrying the deposit over credits nobody");
            foreach (MoneyRecord m in child.ledger) if (m.direction == MoneyDirection.TransferIn) T.Eq(0, m.contractorSilver, "a TransferIn carries no contractor money");
            foreach (MoneyRecord m in parent.ledger) if (m.direction == MoneyDirection.TransferOut) T.Eq(0, m.contractorSilver, "nor a TransferOut");
            NetworkActor who = n.ctx.actors.Get(ProcurementTests.Op(n, child).contractor);
            T.Check(who.id != first.id, "another contractor took over");
            T.Eq(0, child.ContractorHeld(), "the replacement holds nothing yet: the deposit was paid to the contractor that was lost");
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            ProcurementTests.RunUntil(n, () => child.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Eq(ContractStatus.Fulfilled, child.status, "delivered");
            MoneyRecord balance = child.ledger.Find(m => m.purpose == MoneyPurpose.Balance && m.direction == MoneyDirection.PlayerPaid);
            float share = QuoteShare(n, child);
            T.Eq((int)(balance.silver * share), child.ContractorHeld(), "the replacement earned only the balance, at its share");
            T.Eq(firstPaid, parent.ContractorHeld(), "the lost contractor's pay is untouched");
            T.Eq(child.ContractorHeld(), Sim(who).career.careerEarnings, "and it is in the replacement's earnings");
            int charged = parent.ExternalCharged() + child.ExternalCharged();
            T.Check(parent.ContractorHeld() + child.ContractorHeld() <= charged, "together they hold no more than the player paid");
            Books(n, "replacement");
        }

        private static void WealthSaveLoad()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            Contract c = ProcurementTests.Awarded(n, fixer, team);
            int funds = Sim(team).funds, held = c.ContractorHeld();
            T.Check(held > 0, "paid at award");
            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            NetworkActor again = n.ctx.actors.Get(team.id);
            Contract c2 = n.ctx.contracts.Get(c.id);
            T.Eq(funds, Sim(again).funds, "the funds survive the reload as they were");
            T.Eq(held, c2.ContractorHeld(), "and so does the ledger attribution");
            List<string> findings = new List<string>();
            n.ctx.Career.Validate(findings);
            T.Eq(0, findings.Count, "a load-time validation changes nothing (" + string.Join("; ", findings.ToArray()) + ")");
            T.Eq(funds, Sim(again).funds, "the ledger is never rescanned into funds");
            long credit0 = n.ctx.Career.counters.Flow(FundsFlow.Credit);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            ProcurementTests.RunUntil(n, () => c2.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Eq(ContractStatus.Fulfilled, c2.status, "finishes after the reload");
            int balanceCredit = 0;
            foreach (MoneyRecord m in c2.ledger) if (m.purpose == MoneyPurpose.Balance) balanceCredit += m.contractorSilver;
            T.Eq(balanceCredit, (int)(n.ctx.Career.counters.Flow(FundsFlow.Credit) - credit0), "only the balance was credited after the reload: no second deposit credit");
            T.Eq(held + balanceCredit, c2.ContractorHeld(), "the total is deposit + balance once each");
            Books(n, "after reload");
        }

        private static void WealthSaturating()
        {
            T.Eq(CareerPolicy.FundsBound, CareerPolicy.AddFunds(int.MaxValue - 5, 100), "adding past the top saturates");
            T.Eq(-CareerPolicy.FundsBound, CareerPolicy.AddFunds(int.MinValue + 5, -100), "subtracting past the bottom saturates");
            T.Eq(CareerPolicy.FundsBound, CareerPolicy.AddFunds(0, long.MaxValue / 2), "a huge delta saturates");
            T.Eq(CareerPolicy.CounterCap, CareerPolicy.AddSaturating(CareerPolicy.CounterCap - 1, 1000000), "counters saturate");
            T.Eq(5, CareerPolicy.AddFunds(2, 3), "ordinary arithmetic is exact");
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Solo(n);
            ContractorSimulation sim = Sim(a);
            SetFunds(n, a, CareerPolicy.FundsBound - 3);
            int applied = n.ctx.Career.Credit(a, 1000);
            T.Eq(3, applied, "a credit near the bound applies only what fits");
            T.Eq(CareerPolicy.FundsBound, sim.funds, "the funds sit at the bound, never wrapped");
            sim.career.careerEarnings = CareerPolicy.CounterCap - 1;
            n.ctx.Career.Credit(a, 50);
            T.Check(sim.career.careerEarnings <= CareerPolicy.CounterCap && sim.career.careerEarnings > 0, "career earnings saturate too");
            T.Eq(0, n.ctx.Career.MoveFunds(sim, 10, FundsFlow.Dev), "nothing more fits");
            SetFunds(n, a, -CareerPolicy.FundsBound + 2);
            T.Eq(-2, n.ctx.Career.MoveFunds(sim, -1000, FundsFlow.Upkeep), "the same at the bottom");
            T.Eq(-CareerPolicy.FundsBound, sim.funds, "bounded below");
            Books(n, "saturated");
            T.Eq(0, n.ctx.Career.ClawBack(a, null, 100), "a clawback with no contract takes nothing");
        }

        private static void WealthRenegotiation()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            ProcurementDevOverrides.forceWorseThanExpected = true;
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            Contract c = ProcurementTests.Awarded(n, fixer, team);
            ProcurementTests.RunUntil(n, () => c.status == ContractStatus.Renegotiating || c.IsTerminal);
            T.Eq(SubStatus.WorseThanExpected, c.subStatus, "the contractor asks for new terms");
            long credit0 = n.ctx.Career.counters.Flow(FundsFlow.Credit);
            T.Check(n.ctx.Procurement.RespondWorse(c.id, WorseChoice.PayMore).ok, "the client pays more");
            MoneyRecord extra = c.ledger.Find(m => m.purpose == MoneyPurpose.Renegotiation);
            T.Check(extra != null && extra.contractorSilver > 0 && extra.contractorSilver < extra.silver, "the contractor's share of the extra, on its record");
            T.Eq(extra.contractorSilver, (int)(n.ctx.Career.counters.Flow(FundsFlow.Credit) - credit0), "credited once");
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Eq(ContractStatus.Fulfilled, c.status, "delivered");
            T.Eq(CreditOf(c), c.ContractorHeld(), "nothing taken back");
            Books(n, "renegotiation");
        }

        /// <summary>
        /// Contractor A takes a job (deposit, and a premium contribution), is lost before the work, and the Fixer hands it
        /// to replacement B on a NEW linked contract whose funding is carried over by TransferOut / TransferIn.
        /// </summary>
        private static Contract ReplacedJob(TestNet n, NetworkActor fixer, NetworkActor a, out Contract parent, int premium = 300, bool insure = false)
        {
            Contract first = ProcurementTests.Post(n, fixer, "TestSteel", 150, a, premium);
            parent = first;
            Offer o = ProcurementTests.Bid(n, first);
            T.Check(o != null && n.ctx.Procurement.Accept(o.id, insure).ok, "A's quote is accepted");
            n.ctx.Contractors.EndActor(a, "Test");
            ProcurementTests.RunUntil(n, () => first.IsTerminal, 20);
            T.Eq(1, first.lineage.children.Count, "A was replaced");
            return first.lineage.children.Count == 0 ? null : n.ctx.contracts.Get(first.lineage.children[0]);
        }

        private static MoneyRecord OnLedger(Contract c, MoneyDirection dir, MoneyPurpose purpose)
        {
            return c.ledger.Find(m => m.direction == dir && m.purpose == purpose);
        }

        /// <summary>
        /// A is paid the original deposit and lost; B inherits only the funding POSITION (credits B nothing), is later paid a real
        /// balance, and that balance is refunded in full: B gives back exactly its share of that balance, however large the
        /// inherited deposit was, and A's money is not touched.
        /// </summary>
        private static void WealthReplacementBalanceRefund()
        {
            for (int pending = 0; pending < 2; pending++)
            {
                TestNet n = ProcurementTests.World(0);
                NetworkActor fixer = ProcurementTests.Fixer(n, "Premium");
                NetworkActor a = ProcurementTests.Reliable(n);
                NetworkActor b = ProcurementTests.Reliable(n);
                int charged0 = n.pay.charged, refunded0 = n.pay.refunded;
                Contract parent;
                Contract child = ReplacedJob(n, fixer, a, out parent);
                if (child == null) return;
                int aHeld = parent.ContractorHeld();
                T.Check(aHeld > 0, "A was paid the original deposit share");
                foreach (MoneyRecord m in child.ledger) if (m.direction == MoneyDirection.TransferIn) T.Eq(0, m.contractorSilver, "the inherited TransferIn credits B with zero");
                NetworkActor who = n.ctx.actors.Get(ProcurementTests.Op(n, child).contractor);
                T.Check(who.id != a.id, "another contractor took over");
                T.Eq(0, child.ContractorHeld(), "B holds nothing from the carried-over deposit");
                T.Eq(0, child.OwnBearingRemaining(), "and the player has paid nothing on B's contract yet");
                T.Check(child.TotalFunding() > 0, "although the contract inherited a funding position of " + child.TotalFunding());

                // B delivers; the balance is paid; the pods never land and the Hold runs out: the balance is refunded in full.
                ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
                ProcurementDevOverrides.forceNotTroubled = true;
                n.delivery.failNextDeliveries = int.MaxValue;
                if (pending == 1) n.pay.canRefund = false;
                ProcurementTests.RunUntil(n, () => child.IsTerminal, 80);
                ProcurementDevOverrides.Clear();
                T.Eq(ContractStatus.Failed, child.status, "undeliverable: the contract fails");
                MoneyRecord balance = OnLedger(child, MoneyDirection.PlayerPaid, MoneyPurpose.Balance);
                MoneyRecord refund = OnLedger(child, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
                T.Check(balance != null && refund != null, "a real balance was paid, then refunded");
                if (balance == null || refund == null) return;
                int credited = balance.contractorSilver;
                T.Check(credited > 0 && credited < balance.silver, "B was credited its share of the balance (" + credited + " of " + balance.silver + ")");
                T.Eq(balance.silver, refund.silver, "the balance is refunded in full: the player's amount is unchanged");
                T.Eq(balance.silver, refund.fromOwnFunding, "the whole refund was drawn from funding the player paid ON THIS contract");
                T.Eq(-credited, refund.contractorSilver, "B loses exactly the contractor share of the refunded balance, not a diluted fraction of it");
                T.Check(child.TotalFunding() > balance.silver, "(the inherited deposit would have diluted it: the funding position was " + child.TotalFunding() + ")");
                T.Eq(0, child.ContractorHeld(), "B ends with nothing from this contract");
                T.Eq(0, Sim(who).career.careerEarnings, "no career earnings");
                T.Eq(aHeld, parent.ContractorHeld(), "A's original contractor-owned money is untouched");
                T.Eq(-credited, (int)n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "the only clawback in the world is B's");
                T.Eq(pending == 1, refund.pending, "pending exactly as the port said");
                T.Eq(pending == 1 ? 0 : balance.silver, n.pay.refunded - refunded0, "the port has delivered the refund, or not yet");
                if (pending == 0) MoneyLineageTests.AssertLineage(n, parent, charged0, refunded0, "delivered");
                Books(n, "replacement balance refund");

                // A pending refund retried, and a reload, never claw twice.
                long claw = n.ctx.Career.counters.Flow(FundsFlow.ClawBack);
                int heldNow = child.ContractorHeld(), fundsB = Sim(who).funds;
                n.pay.canRefund = true;
                n.AdvanceTo(n.clock.Now + 3 * Ticks.PerDay);
                T.Check(!child.HasPendingRefund(), "no refund is pending any more");
                T.Eq(balance.silver, n.pay.refunded - refunded0, "the player received exactly the balance back, once");
                MoneyLineageTests.AssertLineage(n, parent, charged0, refunded0, "after the retry");
                T.Eq(claw, n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "the retry clawed nothing more");
                NetworkState loaded = CorrectionTests.SaveLoad(n);
                CorrectionTests.Swap(n, loaded);
                Contract childAgain = n.ctx.contracts.Get(child.id);
                T.Eq(-credited, OnLedger(childAgain, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund).contractorSilver, "the attribution survives the reload");
                T.Eq(balance.silver, OnLedger(childAgain, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund).fromOwnFunding, "and so does its provenance");
                List<string> findings = new List<string>();
                n.ctx.Career.Validate(findings);
                T.Eq(0, findings.Count, "a validation changes nothing (" + string.Join("; ", findings.ToArray()) + ")");
                n.AdvanceTo(n.clock.Now + 5 * Ticks.PerDay);
                T.Check(Sim(n.ctx.actors.Get(who.id)).funds <= fundsB + 1000000 && childAgain.ContractorHeld() == heldNow, "later days claw nothing either");
                T.Eq(claw, n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "the clawback total never moved");
            }
        }

        /// <summary>A technical invalidation takes back only what the REPLACEMENT was paid: never the lost contractor's pay, never anything for carried-in funding.</summary>
        private static void WealthReplacementVoid()
        {
            // (a) After B was paid a balance: everything B owns on this contract is taken back, nothing of A's.
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Basic");
            NetworkActor a = ProcurementTests.Reliable(n);
            NetworkActor b = ProcurementTests.Reliable(n);
            int charged0 = n.pay.charged, refunded0 = n.pay.refunded;
            Contract parent;
            Contract child = ReplacedJob(n, fixer, a, out parent);
            if (child == null) return;
            int aHeld = parent.ContractorHeld();
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            n.delivery.failCreation = true; // after the balance is charged the item cannot be made: a technical void
            ProcurementTests.RunUntil(n, () => child.IsTerminal, 40);
            ProcurementDevOverrides.Clear();
            T.Eq(ContractStatus.Voided, child.status, "voided technically");
            MoneyRecord balance = OnLedger(child, MoneyDirection.PlayerPaid, MoneyPurpose.Balance);
            MoneyRecord refund = OnLedger(child, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
            T.Check(balance != null && refund != null && balance.contractorSilver > 0, "B was paid a balance, then everything was refunded");
            if (balance == null || refund == null) return;
            T.Eq(child.TransferredIn() + balance.silver, refund.silver, "the player gets back the carried-in funding and the balance (what it held)");
            T.Eq(balance.silver, refund.fromOwnFunding, "only the balance is B's own funding on this contract");
            T.Eq(-balance.contractorSilver, refund.contractorSilver, "B returns exactly what it was paid here");
            T.Eq(0, child.ContractorHeld(), "a full void leaves B nothing: no windfall");
            T.Eq(aHeld, parent.ContractorHeld(), "A keeps what it was paid on the parent contract (frozen replacement semantics)");
            T.Eq(-balance.contractorSilver, (int)n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "and nothing of A's was taken back");
            T.Eq(child.ExternalCharged() + child.TransferredIn(), child.ExternalRefunded(), "everything the contract held went back to the player");
            MoneyLineageTests.AssertLineage(n, parent, charged0, refunded0, "replacement void");
            Books(n, "replacement void");

            // (b) Voided before B was paid anything: B owns nothing here, so nothing is taken from B or A.
            TestNet m = ProcurementTests.World(0);
            NetworkActor f2 = ProcurementTests.Fixer(m, "Premium", "Basic");
            NetworkActor a2 = ProcurementTests.Reliable(m);
            NetworkActor b2 = ProcurementTests.Reliable(m);
            Contract p2;
            Contract c2 = ReplacedJob(m, f2, a2, out p2);
            if (c2 == null) return;
            int a2Held = p2.ContractorHeld();
            long credit0 = m.ctx.Career.counters.Flow(FundsFlow.Credit);
            m.ctx.Procurement.Void(c2, Causes.DefMissing);
            MoneyRecord r2 = OnLedger(c2, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
            T.Check(r2 != null && r2.silver > 0, "the carried-in funding went back to the player");
            T.Eq(0, r2.fromOwnFunding, "none of it was funding the player paid on this contract");
            T.Eq(0, r2.contractorSilver, "so nothing was taken from the replacement");
            T.Eq(0L, m.ctx.Career.counters.Flow(FundsFlow.ClawBack), "nothing was clawed back anywhere");
            T.Eq(a2Held, p2.ContractorHeld(), "and A's pay is untouched");
            T.Eq(credit0, m.ctx.Career.counters.Flow(FundsFlow.Credit), "nobody was credited");
            Books(m, "replacement void before balance");
        }

        /// <summary>The clawback follows the funding actually refunded: carried-in funding is neither in the numerator nor in the denominator.</summary>
        private static void WealthPurposeScoped()
        {
            // A synthetic contract: a deposit inherited from a replaced contract (paid to someone else) and a balance the
            // player paid here, of which this contractor kept 300 of 800.
            TestNet n = ProcurementTests.World(0);
            NetworkActor who = Solo(n);
            SetFunds(n, who, 10000);
            Contract c = new Contract();
            c.ledger.Add(new MoneyRecord { silver = 1000, direction = MoneyDirection.TransferIn, purpose = MoneyPurpose.Deposit });
            c.ledger.Add(new MoneyRecord { silver = 200, direction = MoneyDirection.TransferIn, purpose = MoneyPurpose.Premium });
            c.ledger.Add(new MoneyRecord { silver = 800, direction = MoneyDirection.PlayerPaid, purpose = MoneyPurpose.Balance, contractorSilver = 300 });
            T.Eq(800, c.OwnBearingRemaining(), "only what the player paid HERE is the contractor-bearing funding");
            T.Eq(2000, c.TotalFunding(), "(the funding position is far larger)");
            T.Eq(0, c.OwnDrawnBy(RefundScope.Of(deposit: 1000)), "a refund of the inherited deposit draws nothing of the contractor's");
            T.Eq(0, c.OwnDrawnBy(RefundScope.Of(deposit: 500, premium: 200)), "nor does any share of the inherited deposit and premium");
            T.Eq(800, c.OwnDrawnBy(RefundScope.Of(balance: 800)), "a refund of the balance draws exactly the balance");
            T.Eq(400, c.OwnDrawnBy(RefundScope.Of(deposit: 700, balance: 400)), "a mixed refund draws only its balance part");
            T.Eq(800, c.OwnDrawnBy(RefundScope.Everything), "a full void draws all the player's own payments here");
            T.Eq(0, c.OwnDrawnBy(RefundScope.None), "an insurance payout draws nothing");
            T.Eq(0, c.OwnDrawnBy(null), "no scope, no draw");
            int funds = Sim(who).funds;
            T.Eq(0, n.ctx.Career.ClawBack(who, c, c.OwnDrawnBy(RefundScope.Of(deposit: 1000))), "refunding the inherited deposit takes nothing from the contractor");
            T.Eq(funds, Sim(who).funds, "its funds did not move");
            int claw = n.ctx.Career.ClawBack(who, c, c.OwnDrawnBy(RefundScope.Of(balance: 400)));
            T.Eq(150, claw, "refunding half the balance takes back half of the 300 it kept (the 1,200 inherited does not dilute it)");
            T.Eq(funds - 150, Sim(who).funds, "from its funds");

            // Through the real flows: a replacement that is cancelled while preparing gets back inherited funding only.
            TestNet m = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(m, "Premium");
            NetworkActor a = ProcurementTests.Reliable(m);
            NetworkActor b = ProcurementTests.Reliable(m);
            Contract parent;
            Contract child = ReplacedJob(m, fixer, a, out parent);
            if (child == null) return;
            int aHeld = parent.ContractorHeld();
            int dep = child.Funding(MoneyPurpose.Deposit), prem = child.Funding(MoneyPurpose.Premium);
            T.Check(dep > 0 && prem > 0, "the replacement inherited a deposit and a premium");
            T.Check(m.ctx.Procurement.Cancel(child.id).ok, "the client cancels the replacement while it prepares");
            MoneyRecord refund = OnLedger(child, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
            T.Check(refund != null, "a partial refund by the Fixer's policy");
            T.Eq((int)Math.Round((dep + prem) * 0.4f), refund.silver, "the player's amount is the policy's, unchanged by any attribution");
            T.Eq(0, refund.fromOwnFunding, "none of it was the replacement's own funding");
            T.Eq(0, refund.contractorSilver, "so none of it is clawed from the replacement");
            T.Eq(0L, m.ctx.Career.counters.Flow(FundsFlow.ClawBack), "nothing anywhere");
            T.Eq(aHeld, parent.ContractorHeld(), "and A's pay is untouched");
            Books(m, "replacement cancel");

            // A first-generation cancel (deposit and premium are the player's own payments here) is still proportional.
            TestNet k = ProcurementTests.World(0);
            NetworkActor fx = ProcurementTests.Fixer(k, "Premium");
            NetworkActor team = ProcurementTests.Reliable(k);
            Contract own = ProcurementTests.Post(k, fx, "TestSteel", 150, team, 400);
            Offer q = ProcurementTests.Bid(k, own);
            T.Check(q != null && k.ctx.Procurement.Accept(q.id, false).ok, "accepted");
            int held = own.ContractorHeld(), remaining = own.OwnBearingRemaining();
            T.Check(held > 0 && remaining == own.Funding(MoneyPurpose.Deposit) + own.Funding(MoneyPurpose.Premium), "all of its funding is the player's own payment");
            k.ctx.Procurement.Cancel(own.id);
            MoneyRecord ownRefund = OnLedger(own, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
            T.Eq(ownRefund.silver, ownRefund.fromOwnFunding, "all of the refund is drawn from its own payments");
            T.Eq(-(int)((long)held * ownRefund.silver / remaining), ownRefund.contractorSilver, "and the contractor gives back that same proportion of what it holds");
            Books(k, "own cancel");
        }

        /// <summary>The silver the player paid ON this contract to the contractor-bearing purposes (deposit, premium, renegotiation, balance).</summary>
        private static int OwnBearingPaid(Contract c)
        {
            return c.OwnPaid(MoneyPurpose.Deposit) + c.OwnPaid(MoneyPurpose.Premium) + c.OwnPaid(MoneyPurpose.Renegotiation) + c.OwnPaid(MoneyPurpose.Balance);
        }

        /// <summary>
        /// Insurance reimburses the PLAYER; it must never shield the contractor's pay from a later technical invalidation. A
        /// partial result is accepted, the PartialShortfall payout reimburses the player (a smaller final refund later), then the
        /// item cannot be created and the contract is voided: the player's refund is the existing NetFunding amount, and the
        /// contractor gives back EVERYTHING it still holds from this contract, not a share of that smaller refund.
        /// </summary>
        private static void WealthPartialInsuranceThenVoid()
        {
            for (int pending = 0; pending < 2; pending++)
            {
                TestNet n = ProcurementTests.World(0);
                NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Generous");
                NetworkActor team = ProcurementTests.Reliable(n);
                int charged0 = n.pay.charged, refunded0 = n.pay.refunded;
                ProcurementDevOverrides.forceBand = OutcomeBand.Partial;
                ProcurementDevOverrides.forceSecured = 20;
                ProcurementDevOverrides.forceNotTroubled = true;
                Contract c = ProcurementTests.Awarded(n, fixer, team, "TestSteel", 200, true);
                ProcurementTests.RunUntil(n, () => c.status == ContractStatus.Renegotiating || c.IsTerminal);
                ProcurementDevOverrides.Clear();
                T.Eq(SubStatus.PartialResult, c.subStatus, "a partial result waits for the client");
                T.Check(c.terms.insurance != null && c.terms.insurance.Covers(Causes.PartialShortfall), "a Generous policy that covers a partial shortfall");
                int heldBefore = c.ContractorHeld();
                T.Check(heldBefore > 0 && c.OwnBearingRemaining() > 0, "the contractor was paid at award (" + heldBefore + ")");

                n.delivery.failCreation = true; // the accepted goods cannot be made: a technical invalidation after the payout
                if (pending == 1) n.pay.canRefund = false;
                T.Check(n.ctx.Procurement.RespondPartial(c.id, PartialChoice.AcceptPartial).ok, "the client accepts the partial result");
                T.Eq(ContractStatus.Voided, c.status, "the item cannot be produced: technically voided (" + c.causeKey + ")");
                MoneyRecord payout = OnLedger(c, MoneyDirection.PlayerRefunded, MoneyPurpose.InsurancePayout);
                MoneyRecord refund = OnLedger(c, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
                T.Check(payout != null && refund != null, "the insurer paid out for the shortfall, then the void refunded the player");
                if (payout == null || refund == null) return;
                T.Check(c.ledger.IndexOf(payout) < c.ledger.IndexOf(refund), "the payout came BEFORE the void");
                T.Eq(0, payout.contractorSilver, "the insurance payout took nothing from the contractor");
                T.Eq(0, payout.fromOwnFunding, "and says it drew nothing of the contractor's");

                // The player's side is the existing one: everything it still held, less what the insurer already paid it.
                T.Eq(c.ExternalCharged() - payout.silver, refund.silver, "the void refunds NetFunding: what the player paid, less the insurance payout it already received");
                T.Eq(0, c.NetFunding(), "nothing is left to refund");
                T.Eq(n.pay.charged - charged0, payout.silver + refund.silver, "the player is made whole, and not paid twice");
                T.Eq(pending == 1 ? 0 : n.pay.charged - charged0, n.pay.refunded - refunded0, "(at the port, as far as it has been delivered)");
                T.Check(refund.silver < OwnBearingPaid(c), "the seam is exercised: the player's final refund (" + refund.silver + ") is smaller than the contractor-bearing funding (" + OwnBearingPaid(c) + ")");
                int credited = CreditOf(c);
                T.Check(credited >= heldBefore && (long)credited * refund.fromOwnFunding / OwnBearingPaid(c) < credited, "a clawback proportional to that smaller refund would have left the contractor a windfall");

                // The contractor side is full and exact.
                T.Check(refund.fullReversal, "typed as a full contractor reversal (not inferred from a note)");
                T.Eq(-credited, refund.contractorSilver, "the contractor gives back every silver it held from this contract, to the last one");
                T.Eq(0, c.ContractorHeld(), "it retains nothing");
                T.Eq(0, Sim(team).career.careerEarnings, "its career earnings reflect the full reversal");
                T.Eq(0L, n.ctx.Career.counters.Flow(FundsFlow.Credit) + n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "credit and clawback cancel exactly: no rounding residue");
                T.Eq(-credited, (int)n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "the whole clawback is this one reversal");
                Books(n, "partial insurance then void");

                // The provenance still describes the PLAYER's refund, not the contractor's reversal.
                T.Check(refund.fromOwnFunding >= 0 && refund.fromOwnFunding <= refund.silver, "fromOwnFunding is never above the player's refund (" + refund.fromOwnFunding + " of " + refund.silver + ")");
                T.Eq(refund.silver, refund.fromOwnFunding, "the whole refund was drawn from funding the player paid on this contract (no carried-in funding here)");
                T.Check(-refund.contractorSilver > refund.fromOwnFunding || credited <= refund.fromOwnFunding, "(the reversal is not squeezed into the provenance)");
                T.Eq(pending == 1, refund.pending, "pending exactly as the port said");

                // Neither a pending retry nor a reload nor later days claw anything more, nor touch the payout.
                int payoutSilver = payout.silver;
                n.pay.canRefund = true;
                n.AdvanceTo(n.clock.Now + 3 * Ticks.PerDay);
                T.Check(!c.HasPendingRefund(), "no refund is pending any more");
                T.Eq(n.pay.charged - charged0, n.pay.refunded - refunded0, "the player received every silver back exactly once");
                T.Eq(-credited, (int)n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "the retry clawed nothing more");
                T.Eq(payoutSilver, OnLedger(c, MoneyDirection.PlayerRefunded, MoneyPurpose.InsurancePayout).silver, "the payout was not altered");
                NetworkState loaded = CorrectionTests.SaveLoad(n);
                CorrectionTests.Swap(n, loaded);
                Contract again = n.ctx.contracts.Get(c.id);
                MoneyRecord refundAgain = OnLedger(again, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
                T.Check(refundAgain.fullReversal && refundAgain.contractorSilver == -credited && refundAgain.fromOwnFunding == refund.fromOwnFunding, "the typed reversal and its provenance survive the reload");
                List<string> findings = new List<string>();
                n.ctx.Career.Validate(findings);
                T.Eq(0, findings.Count, "a validation changes nothing (" + string.Join("; ", findings.ToArray()) + ")");
                n.AdvanceTo(n.clock.Now + 5 * Ticks.PerDay);
                T.Eq(0, again.ContractorHeld(), "later days leave it at nothing");
                T.Eq(-credited, (int)n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "the reload clawed nothing twice");
                Books(n, "partial insurance then void, after the reload");
            }
        }

        /// <summary>
        /// A replacement B inherits a funding position (a TransferIn that credited nobody), is paid a real balance, a partial
        /// insurance payout reimburses the player, and the item cannot be made: B returns exactly what B was paid, A keeps what
        /// A was paid, and the carried-in funding is never B's income. A second part reaches the exact under-claw shape with an
        /// oversized payout injected into the replacement's ledger (the real payout is always smaller than the funding carried
        /// in, so only a synthetic one makes the player's final refund smaller than B's own pay).
        /// </summary>
        private static void WealthReplacementPartialInsuranceThenVoid()
        {
            // (a) The real flow.
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Generous");
            NetworkActor a = ProcurementTests.Reliable(n);
            NetworkActor b = ProcurementTests.Reliable(n);
            int charged0 = n.pay.charged, refunded0 = n.pay.refunded;
            Contract parent;
            Contract child = ReplacedJob(n, fixer, a, out parent, 300, true);
            if (child == null) return;
            int aHeld = parent.ContractorHeld();
            T.Check(aHeld > 0, "A was paid at the original award");
            T.Check(child.terms.insurance != null && child.terms.insurance.Covers(Causes.PartialShortfall), "the replacement carries the policy");
            foreach (MoneyRecord m in child.ledger) if (m.direction == MoneyDirection.TransferIn) T.Eq(0, m.contractorSilver, "the inherited TransferIn credits B with zero");
            T.Eq(0, child.ContractorHeld(), "B holds nothing yet");
            ProcurementDevOverrides.forceBand = OutcomeBand.Partial;
            ProcurementDevOverrides.forceSecured = 135;
            ProcurementDevOverrides.forceNotTroubled = true;
            ProcurementTests.RunUntil(n, () => child.status == ContractStatus.Renegotiating || child.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Eq(SubStatus.PartialResult, child.subStatus, "B returns a partial result");
            n.delivery.failCreation = true;
            T.Check(n.ctx.Procurement.RespondPartial(child.id, PartialChoice.AcceptPartial).ok, "the client accepts it");
            T.Eq(ContractStatus.Voided, child.status, "then the item cannot be produced: voided");
            MoneyRecord payout = OnLedger(child, MoneyDirection.PlayerRefunded, MoneyPurpose.InsurancePayout);
            MoneyRecord balance = OnLedger(child, MoneyDirection.PlayerPaid, MoneyPurpose.Balance);
            MoneyRecord refund = OnLedger(child, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
            T.Check(payout != null && balance != null && refund != null && balance.contractorSilver > 0, "an insurance payout, then B was paid a real balance, then the void refund");
            if (payout == null || balance == null || refund == null) return;
            int bPaid = balance.contractorSilver;
            T.Eq(0, payout.contractorSilver, "the payout took nothing from B");
            T.Eq(child.ExternalCharged() + child.TransferredIn() - payout.silver, refund.silver, "the player's refund is the existing NetFunding amount");
            T.Eq(0, child.NetFunding(), "nothing is left to refund");
            T.Check(refund.fullReversal && refund.fromOwnFunding <= refund.silver, "a typed full reversal whose provenance never exceeds the player's refund");
            T.Eq(-bPaid, refund.contractorSilver, "B returns exactly what B was paid on this contract");
            T.Eq(0, child.ContractorHeld(), "B retains nothing");
            T.Eq(aHeld, parent.ContractorHeld(), "A's original money is unchanged");
            T.Eq(-bPaid, (int)n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "the only clawback in the world is B's: nothing of A's, nothing for carried-in funding");
            foreach (MoneyRecord m in child.ledger) if (m.direction == MoneyDirection.TransferIn || m.direction == MoneyDirection.TransferOut) T.Eq(0, m.contractorSilver, "no transfer ever moved contractor money");
            MoneyLineageTests.AssertLineage(n, parent, charged0, refunded0, "replacement partial insurance then void");
            Books(n, "replacement partial insurance then void");
            long claw = n.ctx.Career.counters.Flow(FundsFlow.ClawBack);
            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            n.ctx.Career.Validate(new List<string>());
            n.AdvanceTo(n.clock.Now + 4 * Ticks.PerDay);
            T.Eq(claw, n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "a reload, a validation and later days claw nothing twice");
            T.Eq(aHeld, n.ctx.contracts.Get(parent.id).ContractorHeld(), "and A's money is still untouched");

            // (b) The exact under-claw shape: an oversized payout (synthetic) makes the player's final refund smaller than B's pay.
            TestNet m2 = ProcurementTests.World(0);
            NetworkActor f2 = ProcurementTests.Fixer(m2, "Premium", "Generous");
            NetworkActor a2 = ProcurementTests.Reliable(m2);
            NetworkActor b2 = ProcurementTests.Reliable(m2);
            Contract p2;
            Contract c2 = ReplacedJob(m2, f2, a2, out p2, 300, true);
            if (c2 == null) return;
            int a2Held = p2.ContractorHeld();
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            m2.delivery.failNextDeliveries = int.MaxValue; // the balance is charged, the pods never land; stop before the hold ends
            ProcurementTests.RunUntil(m2, () => c2.Deliver != null && c2.Deliver.balancePaid && c2.Deliver.balanceDue > 0, 40);
            ProcurementDevOverrides.Clear();
            MoneyRecord bal2 = OnLedger(c2, MoneyDirection.PlayerPaid, MoneyPurpose.Balance);
            T.Check(bal2 != null && bal2.contractorSilver > 0 && !c2.IsTerminal, "B was paid a real balance and the contract is still open");
            if (bal2 == null || c2.IsTerminal) return;
            int b2Paid = bal2.contractorSilver;
            int ownPaid = OwnBearingPaid(c2);
            // The insurer's payout is larger than all the funding the replacement inherited: the player's final refund is smaller than B's own balance.
            int oversized = c2.NetFunding() - ownPaid / 2;
            c2.ledger.Add(new MoneyRecord { tick = m2.clock.Now, silver = oversized, direction = MoneyDirection.PlayerRefunded, purpose = MoneyPurpose.InsurancePayout, noteKey = "insurance.payout" });
            int expectedRefund = c2.NetFunding();
            T.Check(expectedRefund > 0 && expectedRefund < ownPaid, "the seam is exercised: the final refund (" + expectedRefund + ") is smaller than B's own funding (" + ownPaid + ")");
            m2.ctx.Procurement.Void(c2, Causes.DefMissing);
            MoneyRecord r2 = c2.ledger.FindLast(m => m.direction == MoneyDirection.PlayerRefunded && m.purpose == MoneyPurpose.Refund);
            T.Check(r2 != null && r2.fullReversal, "a typed full reversal");
            T.Eq(expectedRefund, r2.silver, "the player's refund is the existing amount, unchanged");
            T.Eq(Math.Min(ownPaid, expectedRefund), r2.fromOwnFunding, "its provenance is the player's refund, drawn from its own payments first, not inflated");
            T.Check(-r2.contractorSilver > (long)b2Paid * r2.fromOwnFunding / ownPaid, "the reversal is larger than the share a proportional clawback would have returned");
            T.Eq(-b2Paid, r2.contractorSilver, "B returns everything it was paid on this contract");
            T.Eq(0, c2.ContractorHeld(), "B retains nothing");
            T.Eq(a2Held, p2.ContractorHeld(), "A's money is untouched");
            foreach (MoneyRecord m in c2.ledger) if (m.direction == MoneyDirection.TransferIn) T.Eq(0, m.contractorSilver, "carried funding was never B's income");
            Books(m2, "replacement oversized payout then void");
        }

        /// <summary>A technical invalidation reverses the contractor even when there is nothing left to refund to the player (a 0-silver reversal record).</summary>
        private static void WealthVoidNothingLeftToRefund()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Generous");
            NetworkActor team = ProcurementTests.Reliable(n);
            Contract c = ProcurementTests.Awarded(n, fixer, team, "TestSteel", 150, true);
            int held = c.ContractorHeld();
            T.Check(held > 0 && c.NetFunding() > 0, "the contractor was paid and the player still holds funding");
            // A synthetic insurer payout as large as everything the player holds: nothing is left to refund to the player.
            c.ledger.Add(new MoneyRecord { tick = n.clock.Now, silver = c.NetFunding(), direction = MoneyDirection.PlayerRefunded, purpose = MoneyPurpose.InsurancePayout, noteKey = "insurance.payout" });
            T.Eq(0, c.NetFunding(), "nothing is left to refund to the player");
            int refunded = n.pay.refunded;
            n.ctx.Procurement.Void(c, Causes.DefMissing);
            T.Eq(ContractStatus.Voided, c.status, "voided");
            MoneyRecord r = c.ledger.FindLast(m => m.direction == MoneyDirection.PlayerRefunded && m.purpose == MoneyPurpose.Refund);
            T.Check(r != null && r.fullReversal && r.silver == 0 && !r.pending, "a typed reversal record of 0 silver: nothing to pay, nothing pending");
            T.Eq(refunded, n.pay.refunded, "the player was paid nothing more");
            T.Eq(-held, r.contractorSilver, "the contractor still gave back everything it held");
            T.Eq(0, c.ContractorHeld(), "it retains nothing");
            T.Eq(0, Sim(team).career.careerEarnings, "no career earnings");
            Books(n, "nothing left to refund");
            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            n.ctx.Career.Validate(new List<string>());
            n.AdvanceTo(n.clock.Now + 4 * Ticks.PerDay);
            T.Eq(-held, (int)n.ctx.Career.counters.Flow(FundsFlow.ClawBack), "a reload and later days reverse nothing twice");
        }

        /// <summary>Weakens a team's doctrine so that, asked for new terms and refused, it walks away instead of carrying on.</summary>
        private static void MakeQuitter(NetworkActor who)
        {
            ContractorSimulation s = Sim(who);
            s.doctrine.professionalism = 0.3f;
            s.doctrine.loyalty = 0.2f;
        }

        /// <summary>
        /// The nearby seams of the correction: cancel during a renegotiation, a contractor that walks (own funding, and a
        /// replacement's inherited funding), a contractor lost before the work with no replacement. Each follows the funding
        /// it actually refunds, and none produces a career result for work that never happened.
        /// </summary>
        private static void AuditRefundPaths()
        {
            // Cancel during a renegotiation: nothing is refunded, the contractor keeps what it was paid, no career result.
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium");
            NetworkActor team = ProcurementTests.Reliable(n);
            ProcurementDevOverrides.forceWorseThanExpected = true;
            Contract c = ProcurementTests.Awarded(n, fixer, team);
            Operation op = ProcurementTests.Op(n, c);
            ProcurementTests.RunUntil(n, () => c.status == ContractStatus.Renegotiating || c.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Eq(SubStatus.WorseThanExpected, c.subStatus, "renegotiating");
            int held = c.ContractorHeld(), refunded = n.pay.refunded;
            T.Check(n.ctx.Procurement.Cancel(c.id).ok, "the client cancels during the renegotiation");
            T.Eq(refunded, n.pay.refunded, "nothing is refunded");
            T.Eq(held, c.ContractorHeld(), "so nothing is taken back from the contractor");
            T.Check(!op.careerOutcomeApplied && Sim(team).career.Classified == 0, "an operation aborted before any outcome gives no career result");
            Books(n, "cancel in renegotiation");

            // A contractor that walks (first generation): half the deposit and the premium come back, drawn from the player's own payments.
            TestNet w = ProcurementTests.World(0);
            NetworkActor wf = ProcurementTests.Fixer(w, "Premium");
            NetworkActor quitter = ProcurementTests.Reliable(w);
            Contract wc = ProcurementTests.Post(w, wf, "TestSteel", 150, quitter, 400);
            Offer wo = ProcurementTests.Bid(w, wc);
            T.Check(wo != null && w.ctx.Procurement.Accept(wo.id, false).ok, "accepted");
            Operation wop = ProcurementTests.Op(w, wc);
            MakeQuitter(quitter);
            ProcurementDevOverrides.forceWorseThanExpected = true;
            ProcurementTests.RunUntil(w, () => wc.status == ContractStatus.Renegotiating || wc.IsTerminal);
            ProcurementDevOverrides.Clear();
            int wHeld = wc.ContractorHeld(), wRemaining = wc.OwnBearingRemaining();
            T.Check(w.ctx.Procurement.RespondWorse(wc.id, WorseChoice.Refuse).ok, "the client refuses the new terms");
            T.Eq(Causes.ContractorWalked, wc.outcome?.causeKey, "the contractor walked");
            MoneyRecord wr = OnLedger(wc, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
            T.Check(wr != null && wr.fromOwnFunding == wr.silver, "the whole refund came out of the player's own payments here");
            T.Eq(-(int)((long)wHeld * wr.silver / wRemaining), wr.contractorSilver, "the contractor returns that proportion of its pay");
            T.Check(!wop.careerOutcomeApplied && Sim(quitter).career.Classified == 0, "it walked before any outcome: no career result");
            Books(w, "walked");

            // A replacement that walks: the refund is of inherited funding (paid to the lost contractor), so it claws nothing.
            TestNet r = ProcurementTests.World(0);
            NetworkActor rf = ProcurementTests.Fixer(r, "Premium");
            NetworkActor a = ProcurementTests.Reliable(r);
            NetworkActor b = ProcurementTests.Reliable(r);
            Contract parent;
            Contract child = ReplacedJob(r, rf, a, out parent);
            if (child == null) return;
            int aHeld = parent.ContractorHeld();
            NetworkActor who = r.ctx.actors.Get(ProcurementTests.Op(r, child).contractor);
            MakeQuitter(who);
            ProcurementDevOverrides.forceWorseThanExpected = true;
            ProcurementTests.RunUntil(r, () => child.status == ContractStatus.Renegotiating || child.IsTerminal);
            ProcurementDevOverrides.Clear();
            r.ctx.Procurement.RespondWorse(child.id, WorseChoice.Refuse);
            MoneyRecord rr = OnLedger(child, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
            T.Check(rr != null && rr.silver > 0, "a replacement that walks gives the inherited funding back");
            T.Eq(0, rr.fromOwnFunding, "none of it was the replacement's own funding");
            T.Eq(0, rr.contractorSilver, "so nothing is clawed from it");
            T.Eq(aHeld, parent.ContractorHeld(), "and the lost contractor keeps what it was paid");
            T.Eq(0L, r.ctx.Career.counters.Flow(FundsFlow.ClawBack), "no clawback anywhere");
            Books(r, "replacement walked");

            // Lost before the work, no replacement (a lean Fixer refunds a quarter of the deposit): drawn from the lost contractor's own pay.
            TestNet l = ProcurementTests.World(0);
            NetworkActor lf = ProcurementTests.Fixer(l, "Lean");
            NetworkActor lost = ProcurementTests.Reliable(l);
            Contract lc = ProcurementTests.Post(l, lf, "TestSteel", 150, lost, 200);
            Offer lo = ProcurementTests.Bid(l, lc);
            T.Check(lo != null && l.ctx.Procurement.Accept(lo.id, false).ok, "accepted");
            Operation lop = ProcurementTests.Op(l, lc);
            int lHeld = lc.ContractorHeld(), lRemaining = lc.OwnBearingRemaining();
            l.ctx.Contractors.EndActor(lost, "Test");
            ProcurementTests.RunUntil(l, () => lc.IsTerminal, 20);
            MoneyRecord lr = OnLedger(lc, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
            T.Check(lr != null && lr.silver > 0 && lr.fromOwnFunding == lr.silver, "the policy's refund is drawn from the player's own payments");
            T.Eq(-(int)((long)lHeld * lr.silver / lRemaining), lr.contractorSilver, "proportionally from the lost contractor's pay");
            T.Check(!lop.careerOutcomeApplied, "no career result for an operation lost before the work");
            Books(l, "lost before the work");
        }

        /// <summary>A save/load on either side of the operation's terminal transition applies the career result exactly once.</summary>
        private static void AuditSaveLoadAroundTerminal()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            team.reputation.SetScore(0);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            Contract c = ProcurementTests.Awarded(n, fixer, team);
            Operation op = ProcurementTests.Op(n, c);
            // Resolved, still on the way home, not yet finished: the result has not been applied.
            ProcurementTests.RunUntil(n, () => op.outcome != null && !op.Find(Checkpoint.Return).done || c.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Check(op.outcome != null && !op.careerOutcomeApplied, "resolved and not applied yet (" + op.phase + "/" + op.status + ")");
            NetworkState before = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, before);
            NetworkActor again = n.ctx.actors.Get(team.id);
            Contract c2 = n.ctx.contracts.Get(c.id);
            Operation op2 = n.ctx.operations.Get(op.id);
            T.Check(op2.careerEligible && !op2.careerOutcomeApplied && Sim(again).career.Classified == 0, "the reload kept it eligible and unapplied");
            ProcurementTests.RunUntil(n, () => c2.IsTerminal);
            T.Eq(ContractStatus.Fulfilled, c2.status, "it finishes");
            T.Check(op2.careerOutcomeApplied && Sim(again).career.Classified == 1, "applied once");
            int score = again.reputation.score;
            // And again on the far side of the transition.
            NetworkState after = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, after);
            NetworkActor third = n.ctx.actors.Get(team.id);
            Operation op3 = n.ctx.operations.Get(op.id);
            n.ctx.Career.Validate(new List<string>());
            n.ctx.Operations.Finish(op3);
            n.ctx.Operations.Abort(op3, "Late");
            T.Check(op3.careerOutcomeApplied && Sim(third).career.Classified == 1 && third.reputation.score == score, "a reload after the terminal transition, a validation and repeated terminal calls change nothing");
        }

        // ================================================================== D. advancement

        /// <summary>A quiet contractor at a chosen tier and fame, with funds to spare, at home.</summary>
        private static NetworkActor Ready(TestNet n, int tier, FameBand fame, int funds = 1000000)
        {
            NetworkActor a = Solo(n, ExperienceBand.Seasoned, fame);
            ContractorSimulation sim = Sim(a);
            sim.equipment.tier = tier;
            sim.equipment.condition = 0.95f;
            sim.morale.descriptor = MoraleDescriptor.Steady;
            SetFunds(n, a, funds);
            return a;
        }

        private static AdvancementBlock Block(TestNet n, NetworkActor a)
        {
            int cost, reserve;
            return n.ctx.Career.BlockedBy(a, out cost, out reserve);
        }

        private static void AdvanceReputation()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 2, FameBand.Local);
            T.Eq(AdvancementBlock.NeedsReputation, Block(n, a), "tier 3 needs an Established name");
            T.Check(!n.ctx.Career.RunAdvancement(a) && Sim(a).equipment.tier == 2, "so nothing happens, however rich");
            n.ctx.Career.AddReputation(a, 200, false);
            T.Eq(FameBand.Established, a.reputation.fame, "Established now");
            T.Eq(AdvancementBlock.None, Block(n, a), "it may advance");
            T.Check(n.ctx.Career.RunAdvancement(a) && Sim(a).equipment.tier == 3, "tier 3");
            // The ladder: 1→2 Local, 2→3 Established, 3→4 Famous, 4→5 Legendary.
            T.Eq(FameBand.Local, CareerPolicy.RequiredFame(2), "tier 2: Local");
            T.Eq(FameBand.Established, CareerPolicy.RequiredFame(3), "tier 3: Established");
            T.Eq(FameBand.Famous, CareerPolicy.RequiredFame(4), "tier 4: Famous");
            T.Eq(FameBand.Legendary, CareerPolicy.RequiredFame(5), "tier 5: Legendary");
            T.Eq(500, CareerPolicy.UpgradeCost(1), "~500");
            T.Eq(2000, CareerPolicy.UpgradeCost(2), "~2,000");
            T.Eq(8000, CareerPolicy.UpgradeCost(3), "~8,000");
            T.Eq(30000, CareerPolicy.UpgradeCost(4), "~30,000");
        }

        private static void AdvanceFunds()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 1, FameBand.Local, 0);
            int reserve = CareerService.OperatingReserve(a);
            int cost = CareerPolicy.UpgradeCost(1);
            SetFunds(n, a, cost + reserve - 1);
            T.Eq(AdvancementBlock.NeedsFunds, Block(n, a), "one silver short of cost + reserve");
            T.Check(!n.ctx.Career.RunAdvancement(a), "nothing bought");
            SetFunds(n, a, cost);
            T.Eq(AdvancementBlock.NeedsFunds, Block(n, a), "the price alone is not enough: the reserve must remain");
            SetFunds(n, a, cost + reserve);
            T.Eq(AdvancementBlock.None, Block(n, a), "cost + reserve is");
            T.Check(n.ctx.Career.RunAdvancement(a), "bought");
            T.Eq(reserve, Sim(a).funds, "and exactly the reserve remains");
            T.Check(reserve >= CareerPolicy.ReserveFloor, "the reserve has a floor");
            NetworkActor big = ContractorTests.Make(n, ContractorTests.Template(ContractorForm.Company, ExperienceBand.Seasoned, FameBand.Local));
            T.Check(CareerService.OperatingReserve(big) > reserve * 5, "a company's reserve is far larger: it follows its real upkeep (" + CareerService.OperatingReserve(big) + " vs " + reserve + ")");
        }

        private static void AdvanceCommitted()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor a = Ready(n, 3, FameBand.Famous);
            ContractorSimulation sim = Sim(a);
            sim.equipment.tier = 3;
            T.Eq(AdvancementBlock.None, Block(n, a), "free to advance");
            Contract c = ProcurementTests.Awarded(n, fixer, a, "TestSteel", 100);
            T.Check(sim.commitments.Count > 0, "on a job");
            T.Eq(AdvancementBlock.Committed, Block(n, a), "committed: the operation reads the equipment");
            int tier = sim.equipment.tier;
            n.ctx.Upkeep.UpkeepJob(new ScheduledJob { kind = ContractorService.UpkeepJob, target = a.id.Value });
            T.Eq(tier, sim.equipment.tier, "a daily upkeep does not advance it");
            T.Check(!n.ctx.Career.RunAdvancement(a), "nor a direct call");
            T.Eq(0, n.ctx.Career.counters.advancedWhileCommitted, "the tripwire never tripped");
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Check(sim.commitments.Count == 0, "home again");
            T.Eq(tier, sim.equipment.tier, "it did not change during the job");
        }

        private static void AdvanceSpends()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 2, FameBand.Established, 100000);
            ContractorSimulation sim = Sim(a);
            int funds = sim.funds, cost = CareerPolicy.UpgradeCost(2);
            long flow0 = n.ctx.Career.counters.Flow(FundsFlow.Advancement);
            T.Check(n.ctx.Career.RunAdvancement(a), "advances");
            T.Eq(funds - cost, sim.funds, "the price was paid");
            T.Eq(-cost, (int)(n.ctx.Career.counters.Flow(FundsFlow.Advancement) - flow0), "and tallied");
            T.Eq(3, sim.equipment.tier, "one tier");
            T.Eq(1, sim.career.advancementCount, "counted");
            T.Eq(n.clock.Now, sim.career.lastAdvancementTick, "stamped");
            T.Eq(1, n.recorder.Count(EventKeys.ContractorAdvanced), "one event");
            T.Check(n.ctx.Contractors.Strength(a) > 0f, "strength recomputed from the new kit");
            Books(n, "advancement");
        }

        private static void AdvanceCooldown()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 1, FameBand.Legendary, 10000000);
            ContractorSimulation sim = Sim(a);
            T.Check(n.ctx.Career.RunAdvancement(a), "1 → 2");
            T.Eq(2, sim.equipment.tier, "tier 2");
            T.Eq(AdvancementBlock.Cooldown, Block(n, a), "only one tier per cooldown");
            T.Check(!n.ctx.Career.RunAdvancement(a), "a second call does nothing");
            int start = sim.career.lastAdvancementTick;
            n.clock.Now = start + CareerPolicy.AdvancementCooldownTicks - 1;
            T.Eq(AdvancementBlock.Cooldown, Block(n, a), "one tick before the end");
            n.clock.Now = start + CareerPolicy.AdvancementCooldownTicks;
            T.Eq(AdvancementBlock.None, Block(n, a), "at thirty days it may");
            T.Check(n.ctx.Career.RunAdvancement(a) && sim.equipment.tier == 3, "2 → 3");
            T.Eq(30 * Ticks.PerDay, CareerPolicy.AdvancementCooldownTicks, "thirty in-game days");
        }

        private static void AdvanceCap()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 4, FameBand.Legendary, 100000000);
            ContractorSimulation sim = Sim(a);
            for (int i = 0; i < 12; i++)
            {
                n.ctx.Career.RunAdvancement(a);
                n.clock.Now += CareerPolicy.AdvancementCooldownTicks;
                T.Check(sim.equipment.tier >= 1 && sim.equipment.tier <= CareerPolicy.MaxTier, "tier stays on the ladder (" + sim.equipment.tier + ")");
            }
            T.Eq(5, sim.equipment.tier, "the top");
            T.Eq(AdvancementBlock.TopTier, Block(n, a), "nothing above it");
            sim.equipment.tier = 9; // corrupt
            List<string> findings = new List<string>();
            n.ctx.Career.Validate(findings);
            T.Eq(5, sim.equipment.tier, "a corrupt tier is clamped by the load validation");
            T.Check(findings.Count == 1, "and reported");
            sim.equipment.tier = -3;
            n.ctx.Career.Validate(null);
            T.Eq(1, sim.equipment.tier, "and from below");
        }

        private static void AdvanceSaveLoad()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 1, FameBand.Legendary, 10000000);
            T.Check(n.ctx.Career.RunAdvancement(a), "advanced once");
            int tick = Sim(a).career.lastAdvancementTick;
            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            NetworkActor again = n.ctx.actors.Get(a.id);
            ContractorSimulation sim = Sim(again);
            T.Eq(tick, sim.career.lastAdvancementTick, "the cooldown start survives");
            T.Eq(1, sim.career.advancementCount, "and the count");
            T.Eq(2, sim.equipment.tier, "and the tier");
            T.Eq(AdvancementBlock.Cooldown, Block(n, again), "the cooldown still holds after the reload");
            n.clock.Now = tick + CareerPolicy.AdvancementCooldownTicks;
            T.Check(n.ctx.Career.RunAdvancement(again) && sim.equipment.tier == 3, "and ends on time");
            T.Eq(2, sim.career.advancementCount, "counted again, once");
        }

        private static void AdvanceRidesUpkeep()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 1, FameBand.Legendary, 10000000);
            ContractorSimulation sim = Sim(a);
            int jobs = n.scheduler.Count;
            n.AdvanceTo(n.clock.Now + 2 * Ticks.PerDay);
            T.Check(sim.equipment.tier >= 2, "the existing upkeep advanced it, no new job needed (tier " + sim.equipment.tier + ")");
            T.Check(!n.scheduler.Has("career.advance", a.id.Value), "no scheduler job of its own");
            int tier = sim.equipment.tier;
            n.AdvanceTo(n.clock.Now + 20 * Ticks.PerDay);
            T.Eq(tier, sim.equipment.tier, "and not again inside the cooldown");
            n.AdvanceTo(n.clock.Now + 12 * Ticks.PerDay);
            T.Eq(tier + 1, sim.equipment.tier, "one tier after thirty days");
            T.Eq(2, sim.career.advancementCount, "two advances in all");
            T.Eq(jobs, n.scheduler.Count, "no extra scheduled jobs exist");
        }

        // ================================================================== E. tags

        private static void TagsNotPersisted()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 5, FameBand.Legendary, 100000000);
            Sim(a).skill = 0.9f;
            Sim(a).mobility.modes.AddRange(new[] { "LongRange", "RapidTransport", "HeavyLift", "Orbital" });
            Sim(a).mobility.rangeBand = Band.High;
            List<string> tags = n.ctx.Career.Tags(a);
            T.Check(tags.Count >= 6, "many tags for a top contractor (" + string.Join(",", tags.ToArray()) + ")");
            string path = PersistenceTests.SaveState(CorrectionTests.StateOf(n), 4);
            string text = System.IO.File.ReadAllText(path);
            System.IO.File.Delete(path);
            foreach (string key in CareerTags.Emitted) T.Check(text.IndexOf(key, StringComparison.Ordinal) < 0 || key == "RapidTransport" || key == "HeavyLift" || key == "LongRange", "the tag key '" + key + "' is not in the save");
            T.Check(text.IndexOf("Wealthy", StringComparison.Ordinal) < 0 && text.IndexOf("WellEquipped", StringComparison.Ordinal) < 0 && text.IndexOf("EliteCombat", StringComparison.Ordinal) < 0 && text.IndexOf("BattleTested", StringComparison.Ordinal) < 0 && text.IndexOf("LegendaryReputation", StringComparison.Ordinal) < 0 && text.IndexOf("SpacerCapable", StringComparison.Ordinal) < 0, "no derived tag is saved");
            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            List<string> after = n.ctx.Career.Tags(n.ctx.actors.Get(a.id));
            T.Check(tags.Count == after.Count && tags.TrueForAll(after.Contains), "the same tags are derived again after a reload");
            foreach (System.Reflection.FieldInfo f in typeof(ContractorSimulation).GetFields()) T.Check(!f.Name.ToLowerInvariant().Contains("tags") && f.Name != "tag", "the simulation holds no tag field (" + f.Name + ")");
            foreach (System.Reflection.FieldInfo f in typeof(CareerRecord).GetFields()) T.Check(!f.Name.ToLowerInvariant().Contains("tags") && f.Name != "tag", "nor does the record (" + f.Name + ")");
        }

        private static void TagsWellEquipped()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 3, FameBand.Unknown, 0);
            ContractorSimulation sim = Sim(a);
            T.Check(!n.ctx.Career.HasTag(a, CareerTags.WellEquipped), "tier 3: no");
            sim.equipment.tier = 4;
            T.Check(n.ctx.Career.HasTag(a, CareerTags.WellEquipped), "tier 4: yes");
            sim.equipment.tier = 5;
            T.Check(n.ctx.Career.HasTag(a, CareerTags.WellEquipped), "tier 5: yes");
            sim.equipment.tier = 2;
            T.Check(!n.ctx.Career.HasTag(a, CareerTags.WellEquipped), "back to 2: gone with it");
            T.Eq(CareerPolicy.WellEquippedTier, 4, "the threshold lives in the policy");
        }

        private static void TagsEliteCombat()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 4, FameBand.Unknown, 0);
            ContractorSimulation sim = Sim(a);
            sim.skill = 0.72f;
            T.Eq(ExperienceBand.Elite, ContractorService.Experience(a), "an elite hand");
            T.Check(n.ctx.Career.HasTag(a, CareerTags.EliteCombat), "elite with credible kit: yes (fame is irrelevant)");
            sim.equipment.tier = 2;
            T.Check(!n.ctx.Career.HasTag(a, CareerTags.EliteCombat), "elite with poor kit: no");
            sim.equipment.tier = 4;
            sim.equipment.condition = 0.2f;
            T.Check(!n.ctx.Career.HasTag(a, CareerTags.EliteCombat), "and a wrecked kit: no");
            sim.equipment.condition = 0.9f;
            sim.skill = 0.5f;
            T.Check(!n.ctx.Career.HasTag(a, CareerTags.EliteCombat), "seasoned only: no, whatever the fame, funds and kit");
            n.ctx.Career.AddReputation(a, 5000, false);
            SetFunds(n, a, 50000000);
            sim.equipment.tier = 5;
            T.Check(!n.ctx.Career.HasTag(a, CareerTags.EliteCombat), "fame, money and kit do not make an elite fighter");
            sim.skill = 0.9f;
            T.Check(n.ctx.Career.HasTag(a, CareerTags.EliteCombat), "legendary hands qualify too");
        }

        private static void TagsWealthy()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor solo = Ready(n, 2, FameBand.Unknown, 0);
            NetworkActor company = ContractorTests.Make(n, ContractorTests.Template(ContractorForm.Company, ExperienceBand.Seasoned, FameBand.Unknown));
            int soloNeed = (int)(CareerService.OperatingReserve(solo) * CareerPolicy.WealthyReserveMultiple);
            int companyNeed = (int)(CareerService.OperatingReserve(company) * CareerPolicy.WealthyReserveMultiple);
            T.Check(companyNeed > soloNeed * 4, "a company needs far more to be comfortable (" + companyNeed + " vs " + soloNeed + ")");
            SetFunds(n, solo, soloNeed - 1);
            SetFunds(n, company, soloNeed);
            T.Check(!n.ctx.Career.HasTag(solo, CareerTags.Wealthy), "a solo one silver short: not wealthy");
            SetFunds(n, solo, soloNeed);
            T.Check(n.ctx.Career.HasTag(solo, CareerTags.Wealthy), "a solo at the line: wealthy");
            T.Check(!n.ctx.Career.HasTag(company, CareerTags.Wealthy), "the same money is a company's ordinary running cash");
            SetFunds(n, company, companyNeed);
            T.Check(n.ctx.Career.HasTag(company, CareerTags.Wealthy), "a company needs much more");
        }

        private static void TagsMobility()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 1, FameBand.Unknown, 0);
            ContractorSimulation sim = Sim(a);
            sim.mobility.modes.Clear();
            sim.mobility.modes.Add("Ground");
            sim.mobility.rangeBand = Band.Medium;
            Func<string, bool> has = k => n.ctx.Career.HasTag(a, k);
            T.Check(!has(CareerTags.LongRange) && !has(CareerTags.RapidTransport) && !has(CareerTags.HeavyLift) && !has(CareerTags.SpacerCapable), "ground only: none");
            // Everything else at maximum: still none of the mobility tags.
            n.ctx.Career.AddReputation(a, 9000, false);
            SetFunds(n, a, 99999999);
            sim.equipment.tier = 5;
            sim.skill = 0.95f;
            T.Check(!has(CareerTags.LongRange) && !has(CareerTags.RapidTransport) && !has(CareerTags.HeavyLift) && !has(CareerTags.SpacerCapable), "fame, money, kit and skill grant no mobility tag");
            T.Check(has(CareerTags.LegendaryReputation), "(the fame tag is there)");
            sim.mobility.rangeBand = Band.High;
            T.Check(has(CareerTags.LongRange), "a high range band: long range");
            sim.mobility.modes.Add("RapidTransport");
            T.Check(has(CareerTags.RapidTransport), "the mode: rapid transport");
            sim.mobility.modes.Add("HeavyLift");
            T.Check(has(CareerTags.HeavyLift), "the mode: heavy lift");
            T.Check(!has(CareerTags.SpacerCapable), "no orbital mode: not spacer-capable");
            sim.mobility.modes.Add("Orbital");
            T.Check(has(CareerTags.SpacerCapable), "only a durable orbital mode makes it spacer-capable");
            sim.mobility.modes.Remove("Orbital");
            sim.mobility.modes.Remove("HeavyLift");
            sim.mobility.rangeBand = Band.Medium;
            T.Check(!has(CareerTags.SpacerCapable) && !has(CareerTags.HeavyLift) && !has(CareerTags.LongRange), "and they follow the mobility away again");
        }

        private static void TagsNoAugmented()
        {
            TestNet n = ProcurementTests.World(40);
            foreach (NetworkActor a in n.ctx.actors.actors)
            {
                if (!ContractorService.IsNpcContractor(a)) continue;
                T.Check(!n.ctx.Career.Tags(a).Contains(CareerTags.Augmented), "a generated contractor is never Augmented");
            }
            NetworkActor top = Ready(n, 5, FameBand.Legendary, 99999999);
            Sim(top).skill = 1f;
            Sim(top).mobility.modes.AddRange(new[] { "LongRange", "RapidTransport", "HeavyLift", "Orbital" });
            Sim(top).career.legacyResolved = 5000;
            T.Check(!n.ctx.Career.Tags(top).Contains(CareerTags.Augmented), "nor the greatest contractor imaginable");
            T.Check(!Array.Exists(CareerTags.Emitted, k => k == CareerTags.Augmented), "the key is reserved, never in the emitted set");
            T.Eq("Augmented", CareerTags.Augmented, "(the reserved key exists for Phase 3)");
            T.Eq(9, CareerTags.Emitted.Length, "nine tags are emitted in Phase 2.75");
            foreach (string key in CareerTags.Emitted) T.Check(n.ctx.Career.Tags(top).FindAll(x => x == key).Count <= 1, "no duplicates of " + key);
        }

        private static void TagsNoBonus()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor a = Ready(n, 5, FameBand.Legendary, 99999999);
            Sim(a).skill = 0.9f;
            Contract c = ProcurementTests.Post(n, fixer, "TestSteel", 100, a);
            ItemFacts f = n.cat.Facts("TestSteel");
            float strength = n.ctx.Contractors.Strength(a);
            ResolverInputs inputs = n.ctx.Operations.Estimate(a, c, f);
            float edge = Resolver.Edge(inputs);
            int funds = Sim(a).funds, tier = Sim(a).equipment.tier, score = a.reputation.score;
            float skill = Sim(a).skill;
            for (int i = 0; i < 50; i++)
            {
                n.ctx.Career.Tags(a);
                n.ctx.Career.CurrentNeed(a);
                n.ctx.Career.Describe(a);
            }
            T.Eq(strength, n.ctx.Contractors.Strength(a), "reading tags changes no strength");
            T.Eq(edge, Resolver.Edge(n.ctx.Operations.Estimate(a, c, f)), "nor the resolver's inputs");
            T.Check(Sim(a).funds == funds && Sim(a).equipment.tier == tier && a.reputation.score == score && Sim(a).skill == skill, "nor any state at all");
            // The same contractor with its tags "removed" (mobility and wealth stripped) still resolves on equipment and skill alone.
            T.Check(n.ctx.Career.Tags(a).Count >= 3, "it carries several tags");
            float without = n.ctx.Contractors.Strength(a);
            Sim(a).funds = 0;
            Sim(a).MarkDirty();
            T.Eq(without, n.ctx.Contractors.Strength(a), "losing the Wealthy tag changes no strength");
            Sim(a).mobility.modes.Clear();
            Sim(a).MarkDirty();
            T.Eq(without, n.ctx.Contractors.Strength(a), "nor does losing a mobility tag");
        }

        private static void TagsRecordAndFame()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 1, FameBand.Unknown, 0);
            ContractorSimulation sim = Sim(a);
            T.Check(!n.ctx.Career.HasTag(a, CareerTags.BattleTested), "a newcomer is not battle-tested");
            sim.career.legacyResolved = CareerPolicy.BattleTestedJobs - 1;
            T.Check(!n.ctx.Career.HasTag(a, CareerTags.BattleTested), "one short");
            sim.career.successes = 1;
            T.Check(n.ctx.Career.HasTag(a, CareerTags.BattleTested), "legacy and new jobs both count toward a meaningful record");
            T.Check(!n.ctx.Career.HasTag(a, CareerTags.LegendaryReputation), "not legendary");
            a.reputation.SetBand(FameBand.Legendary);
            T.Check(n.ctx.Career.HasTag(a, CareerTags.LegendaryReputation), "legendary fame");
            a.reputation.SetBand(FameBand.Famous);
            T.Check(!n.ctx.Career.HasTag(a, CareerTags.LegendaryReputation), "only Legendary");
            T.Eq(0, n.ctx.Career.Tags(n.ctx.actors.PlayerProxy).Count, "the player has none");
        }

        // ================================================================== F. need

        private static void NeedCapital()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 2, FameBand.Unknown, 0);
            Sim(a).equipment.tier = 2;
            SetFunds(n, a, CareerService.OperatingReserve(a) - 1);
            T.Eq(CareerNeed.Capital, n.ctx.Career.CurrentNeed(a), "below the operating reserve: capital");
            SetFunds(n, a, CareerService.OperatingReserve(a));
            T.Check(n.ctx.Career.CurrentNeed(a) != CareerNeed.Capital, "at the reserve it is no longer capital");
        }

        private static void NeedEquipment()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 1, FameBand.Famous, 100000);
            T.Eq(CareerNeed.Equipment, n.ctx.Career.CurrentNeed(a), "a famous name with tier-1 kit wants equipment");
            Sim(a).equipment.tier = 4;
            T.Check(n.ctx.Career.CurrentNeed(a) != CareerNeed.Equipment, "with the kit its name supports, it does not");
        }

        private static void NeedRecovery()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Ready(n, 2, FameBand.Famous, 1000000);
            ContractorSimulation sim = Sim(a);
            T.Eq(CareerNeed.Equipment, n.ctx.Career.CurrentNeed(a), "rich and famous with modest kit: equipment");
            T.Eq(AdvancementBlock.None, Block(n, a), "and free to buy it");
            sim.equipment.condition = 0.2f;
            T.Eq(CareerNeed.Recovery, n.ctx.Career.CurrentNeed(a), "kit in ruins: recovery comes first");
            T.Eq(AdvancementBlock.Recovering, Block(n, a), "no luxury purchase while recovering");
            T.Check(!n.ctx.Career.RunAdvancement(a) && sim.equipment.tier == 2, "nothing is bought");
            sim.equipment.condition = 0.9f;
            T.Eq(CareerNeed.Equipment, n.ctx.Career.CurrentNeed(a), "repaired: equipment again");
            T.Check(n.ctx.Career.RunAdvancement(a), "and now it buys");

            // Hurt people beat it too.
            NetworkActor org = ContractorTests.Make(n, ContractorTests.Template(ContractorForm.Team, ExperienceBand.Seasoned, FameBand.Famous));
            Sim(org).equipment.tier = 2;
            Sim(org).equipment.condition = 0.9f;
            SetFunds(n, org, 1000000);
            OrganizationProfile profile = org.Get<OrganizationProfile>();
            TierCount reg = profile.TierOf(Tier.Regular);
            int total = profile.Healthy;
            reg.wounded += total;
            profile.TierOf(Tier.Recruit).wounded += 0;
            T.Check(n.ctx.Contractors.WoundedShare(org) >= CareerPolicy.SeriousWoundedShare || total == 0, "an organization mostly hurt");
            if (total > 0) T.Eq(CareerNeed.Recovery, n.ctx.Career.CurrentNeed(org), "seriously wounded: recovery");
        }

        private static void NeedReload()
        {
            TestNet n = ProcurementTests.World(40);
            Dictionary<int, CareerNeed> before = new Dictionary<int, CareerNeed>();
            Dictionary<int, string> tags = new Dictionary<int, string>();
            foreach (NetworkActor a in n.ctx.actors.actors)
            {
                if (!ContractorService.IsNpcContractor(a)) continue;
                before[a.id.Value] = n.ctx.Career.CurrentNeed(a);
                tags[a.id.Value] = string.Join(",", n.ctx.Career.Tags(a).ToArray());
            }
            NetworkState loaded = CorrectionTests.SaveLoad(n);
            CorrectionTests.Swap(n, loaded);
            int same = 0;
            foreach (KeyValuePair<int, CareerNeed> kv in before)
            {
                NetworkActor a = n.ctx.actors.Get(new ActorId(kv.Key));
                T.Eq(kv.Value, n.ctx.Career.CurrentNeed(a), "contractor " + kv.Key + " has the same need after a reload");
                T.Eq(tags[kv.Key], string.Join(",", n.ctx.Career.Tags(a).ToArray()), "and the same tags");
                T.Eq(kv.Value, n.ctx.Career.CurrentNeed(a), "and asking twice agrees");
                same++;
            }
            T.Check(same >= 40, "forty contractors compared (" + same + ")");
            HashSet<CareerNeed> kinds = new HashSet<CareerNeed>(before.Values);
            T.Check(kinds.Count >= 2, "a generated world shows more than one need (" + kinds.Count + ")");
        }

        private static void NeedOthers()
        {
            TestNet n = ProcurementTests.World(0);
            // Mastery: fame ahead of experience.
            NetworkActor a = Ready(n, 4, FameBand.Established, 1000000);
            Sim(a).skill = 0.05f;
            Sim(a).doctrine.ambition = 0.1f;
            Sim(a).mobility.rangeBand = Band.High;
            T.Eq(CareerNeed.Mastery, n.ctx.Career.CurrentNeed(a), "a name that runs ahead of the hands: mastery");
            // Mobility: ambitious, successful, short-ranged.
            NetworkActor b = Ready(n, 4, FameBand.Established, 1000000);
            Sim(b).skill = 0.6f;
            Sim(b).doctrine.ambition = 0.9f;
            Sim(b).mobility.rangeBand = Band.Low;
            T.Eq(CareerNeed.Mobility, n.ctx.Career.CurrentNeed(b), "ambitious and established but short-ranged: mobility");
            // Prestige: nothing practical lacking.
            NetworkActor c = Ready(n, 5, FameBand.Legendary, 100000000);
            Sim(c).skill = 0.9f;
            Sim(c).doctrine.ambition = 0.9f;
            Sim(c).mobility.rangeBand = Band.High;
            T.Eq(CareerNeed.Prestige, n.ctx.Career.CurrentNeed(c), "everything in order, still ambitious: prestige");
            Sim(c).doctrine.ambition = 0.1f;
            T.Eq(CareerNeed.None, n.ctx.Career.CurrentNeed(c), "content: none");
            // Expansion: an organization with room and drive.
            NetworkActor org = ContractorTests.Make(n, ContractorTests.Template(ContractorForm.Company, ExperienceBand.Seasoned, FameBand.Unknown));
            SetFunds(n, org, 100000000);
            Sim(org).doctrine.ambition = 0.8f;
            Sim(org).doctrine.professionalism = 0.8f;
            Sim(org).equipment.tier = 5;
            OrganizationProfile profile = org.Get<OrganizationProfile>();
            foreach (TierCount t in profile.tiers) t.healthy = 0;
            profile.TierOf(Tier.Regular).healthy = 4;
            T.Eq(CareerNeed.Expansion, n.ctx.Career.CurrentNeed(org), "an organization far below its capacity, with drive: expansion");
            T.Eq(CareerNeed.None, n.ctx.Career.CurrentNeed(n.ctx.actors.PlayerProxy), "the player has no career need");
            // The need has no effect.
            int funds = Sim(a).funds, tier = Sim(a).equipment.tier;
            n.ctx.Career.CurrentNeed(a);
            T.Check(Sim(a).funds == funds && Sim(a).equipment.tier == tier, "asking changes nothing");
        }

        // ================================================================== G. migration

        /// <summary>A world that has seen work: varied fame, funds, an organization, some jobs done and one still running.</summary>
        private static TestNet MigrationWorld(out Operation running, out NetworkActor runner)
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Basic");
            foreach (FameBand b in Enum.GetValues(typeof(FameBand))) ContractorTests.Make(n, ContractorTests.Template(ContractorForm.Crew, ExperienceBand.Seasoned, b));
            NetworkActor busy = ProcurementTests.Reliable(n);
            NetworkActor done = ProcurementTests.Reliable(n);
            Job(n, fixer, done, OutcomeBand.Triumph);
            n.ctx.Career.AddReputation(done, 40, true);
            runner = busy;
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            Contract c = ProcurementTests.Awarded(n, fixer, busy);
            running = ProcurementTests.Op(n, c);
            ProcurementDevOverrides.Clear();
            return n;
        }

        /// <summary>Saves a world and strips everything a Phase 2.75 build adds, leaving exactly a version-3 shaped file.</summary>
        private static string V3Shaped(TestNet n)
        {
            string path = PersistenceTests.SaveState(CorrectionTests.StateOf(n), 3);
            XmlDocument doc = new XmlDocument();
            doc.Load(path);
            int removed = 0;
            foreach (string xpath in new[] { "//reputation/score", "//careerRecord", "//careerEligible", "//careerApplied", "//contractorSilver" })
            {
                foreach (XmlNode node in new List<XmlNode>(Nodes(doc.SelectNodes(xpath))))
                {
                    node.ParentNode.RemoveChild(node);
                    removed++;
                }
            }
            T.Check(removed > 0, "the fixture carried version-4 data to strip (" + removed + " nodes)");
            doc.Save(path);
            return path;
        }

        private static IEnumerable<XmlNode> Nodes(XmlNodeList list)
        {
            foreach (XmlNode node in list) yield return node;
        }

        private static NetworkState LoadFile(string path)
        {
            NetworkState loaded = new NetworkState();
            Scribe.loader.InitLoading(path);
            try
            {
                List<string> failures = new List<string>();
                loaded.ExposeStores(failures);
                T.Eq(0, failures.Count, "the version-3 shaped file loads (" + string.Join("; ", failures.ToArray()) + ")");
            }
            finally
            {
                Scribe.loader.FinalizeLoading();
            }
            loaded.RebuildIndexes();
            return loaded;
        }

        private static void MigrateFame()
        {
            Operation running;
            NetworkActor runner;
            TestNet n = MigrationWorld(out running, out runner);
            Dictionary<int, FameBand> bands = new Dictionary<int, FameBand>();
            foreach (NetworkActor a in n.ctx.actors.actors) bands[a.id.Value] = a.reputation.fame;
            string path = V3Shaped(n);
            NetworkState state = LoadFile(path);
            System.IO.File.Delete(path);
            foreach (NetworkActor a in state.actors.actors) T.Eq(0, a.reputation.score, "a version-3 file has no score");
            MigrationContext mc = new MigrationContext();
            T.Eq(5, SaveMigrations.Run(state, 3, mc, 0), "3 → 4 (→ 5)");
            T.Eq(0, state.diagnostics.failedMigrations.Count, "no migration failed");
            T.Check(mc.log.Exists(l => l.Contains("band floors")), "and says what it did");
            int checkedBands = 0;
            foreach (NetworkActor a in state.actors.actors)
            {
                T.Eq(bands[a.id.Value], a.reputation.fame, a.name.Display + ": the band is exactly what it was (nobody promoted or demoted)");
                T.Eq(CareerPolicy.FloorOf(bands[a.id.Value]), a.reputation.score, a.name.Display + ": the score starts at the band's floor");
                T.Eq(a.reputation.fame, CareerPolicy.FameFor(a.reputation.score), a.name.Display + ": band and score agree");
                checkedBands++;
            }
            T.Check(checkedBands >= 8, "every actor checked (" + checkedBands + ")");
            HashSet<FameBand> seen = new HashSet<FameBand>(bands.Values);
            T.Check(seen.Count >= 4, "the world held several different bands (" + seen.Count + ")");
        }

        private static void MigratePreserves()
        {
            Operation running;
            NetworkActor runner;
            TestNet n = MigrationWorld(out running, out runner);
            n.AdvanceTo(n.clock.Now + 3 * Ticks.PerDay);
            Dictionary<int, string> before = new Dictionary<int, string>();
            foreach (NetworkActor a in n.ctx.actors.actors)
            {
                ContractorSimulation s = Sim(a);
                if (s == null) continue;
                before[a.id.Value] = s.funds + "|" + s.equipment.tier + "|" + s.equipment.condition + "|" + s.skill + "|" + s.careerStage + "|" + string.Join(",", s.mobility.modes.ToArray()) + "|" + s.mobility.rangeBand + "|"
                    + s.spatial.status + "|" + s.spatial.anchor + "|" + s.spatial.destination + "|" + s.commitments.Count + "|" + s.opsCompleted + "|" + a.status + "|" + s.retirementPressure;
            }
            int contracts = n.ctx.contracts.contracts.Count, ops = n.ctx.operations.operations.Count;
            List<string> ledgers = new List<string>();
            foreach (Contract c in n.ctx.contracts.contracts) foreach (MoneyRecord m in c.ledger) ledgers.Add(c.id.Value + ":" + m.direction + ":" + m.purpose + ":" + m.silver);
            List<string> logs = new List<string>();
            foreach (Contract c in n.ctx.contracts.contracts) foreach (FieldLogEntry e in c.fieldLog) logs.Add(c.id.Value + ":" + e.key);
            string path = V3Shaped(n);
            NetworkState state = LoadFile(path);
            System.IO.File.Delete(path);
            SaveMigrations.Run(state, 3, new MigrationContext(), 0);
            foreach (NetworkActor a in state.actors.actors)
            {
                ContractorSimulation s = Sim(a);
                if (s == null) continue;
                string after = s.funds + "|" + s.equipment.tier + "|" + s.equipment.condition + "|" + s.skill + "|" + s.careerStage + "|" + string.Join(",", s.mobility.modes.ToArray()) + "|" + s.mobility.rangeBand + "|"
                    + s.spatial.status + "|" + s.spatial.anchor + "|" + s.spatial.destination + "|" + s.commitments.Count + "|" + s.opsCompleted + "|" + a.status + "|" + s.retirementPressure;
                T.Eq(before[a.id.Value], after, a.name.Display + ": funds, equipment, skill, career stage, mobility, spatial state, commitments and status are untouched");
            }
            T.Eq(contracts, state.contracts.contracts.Count, "every contract kept");
            T.Eq(ops, state.operations.operations.Count, "every operation kept");
            List<string> ledgersAfter = new List<string>();
            foreach (Contract c in state.contracts.contracts) foreach (MoneyRecord m in c.ledger) ledgersAfter.Add(c.id.Value + ":" + m.direction + ":" + m.purpose + ":" + m.silver);
            T.Check(ledgers.Count == ledgersAfter.Count && ledgers.TrueForAll(ledgersAfter.Contains), "every ledger record kept");
            List<string> logsAfter = new List<string>();
            foreach (Contract c in state.contracts.contracts) foreach (FieldLogEntry e in c.fieldLog) logsAfter.Add(c.id.Value + ":" + e.key);
            T.Check(logs.Count == logsAfter.Count && logs.TrueForAll(logsAfter.Contains), "every Field Log kept");
            foreach (Contract c in state.contracts.contracts) T.Eq(0, c.ContractorHeld(), "no contractor attribution is invented on an old ledger (money already credited stays where it is)");
        }

        private static void MigrateLegacy()
        {
            Operation running;
            NetworkActor runner;
            TestNet n = MigrationWorld(out running, out runner);
            Dictionary<int, int> ops = new Dictionary<int, int>();
            foreach (NetworkActor a in n.ctx.actors.actors) if (Sim(a) != null) ops[a.id.Value] = Sim(a).opsCompleted;
            string path = V3Shaped(n);
            NetworkState state = LoadFile(path);
            System.IO.File.Delete(path);
            SaveMigrations.Run(state, 3, new MigrationContext(), 0);
            int resolvedTotal = 0;
            foreach (NetworkActor a in state.actors.actors)
            {
                ContractorSimulation s = Sim(a);
                if (s == null) continue;
                CareerRecord r = s.career;
                T.Eq(ops[a.id.Value], r.legacyResolved, a.name.Display + ": legacyResolved is the old opsCompleted");
                resolvedTotal += r.legacyResolved;
                T.Check(r.triumphs == 0 && r.successes == 0 && r.partials == 0 && r.failures == 0 && r.disasters == 0, a.name.Display + ": no wins or losses invented");
                T.Check(r.careerEarnings == 0 && r.casualtiesTaken == 0 && r.peopleLost == 0 && r.captured == 0 && r.missing == 0 && r.reputationEarned == 0, a.name.Display + ": no income, casualties or reputation invented");
                T.Check(r.highestDanger == 0 && r.lastOutcomeTick == -1 && r.lastAdvancementTick == -1 && r.advancementCount == 0, a.name.Display + ": no danger, outcome or upgrade invented");
                T.Eq(0L, r.Classified, a.name.Display + ": nothing classified");
            }
            T.Check(resolvedTotal >= 1, "the world had resolved jobs to carry over (" + resolvedTotal + ")");
        }

        private static void MigrateRunning()
        {
            Operation running;
            NetworkActor runner;
            TestNet n = MigrationWorld(out running, out runner);
            T.Check(running != null && !running.IsFinished && running.careerEligible, "an operation is running and was eligible in the 2.75 world");
            int op = running.id.Value;
            int contract = running.contract.Value;
            int actor = runner.id.Value;
            string path = V3Shaped(n);
            NetworkState state = LoadFile(path);
            System.IO.File.Delete(path);
            Operation loadedOp = state.operations.Get(new OperationId(op));
            T.Check(loadedOp != null && !loadedOp.careerEligible, "loaded without the flag (what a pre-2.75 save holds)");
            SaveMigrations.Run(state, 3, new MigrationContext(), 0);
            T.Check(!loadedOp.careerEligible && !loadedOp.careerOutcomeApplied, "migrated: still ineligible, lifecycle unchanged");
            T.Check(!loadedOp.IsFinished && loadedOp.phase == running.phase && loadedOp.status == running.status, "and its phase and status are as saved");

            // Continue the game on the migrated state: the old operation finishes with no career credit.
            CorrectionTests.Swap(n, state);
            NetworkActor who = n.ctx.actors.Get(new ActorId(actor));
            int score = who.reputation.score;
            long classified = Sim(who).career.Classified;
            Contract c = n.ctx.contracts.Get(new ContractId(contract));
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementDevOverrides.forceNotTroubled = true;
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            ProcurementDevOverrides.Clear();
            T.Check(c.IsTerminal, "the running contract finishes");
            T.Eq(score, who.reputation.score, "no reputation was invented for the old operation");
            T.Eq(classified, Sim(who).career.Classified, "nor a record");
            T.Check(!loadedOp.careerOutcomeApplied, "never marked applied");

            // An operation started after the migration is eligible and counts.
            NetworkActor fixer = ProcurementTests.Fixer(n);
            who.reputation.SetScore(0);
            Contract fresh = Job(n, fixer, who, OutcomeBand.Triumph);
            Operation freshOp = ProcurementTests.Op(n, fresh);
            T.Check(freshOp.careerEligible && freshOp.careerOutcomeApplied, "a new operation is career-eligible and applied");
            T.Eq(classified + 1, Sim(who).career.Classified, "and is on the record");
            T.Check(who.reputation.score > 0, "and earned reputation");
        }

        private static void MigrateChain()
        {
            Operation running;
            NetworkActor runner;
            TestNet n = MigrationWorld(out running, out runner);
            string path = V3Shaped(n);
            NetworkState state = LoadFile(path);
            System.IO.File.Delete(path);
            MigrationContext mc = new MigrationContext();
            T.Eq(5, SaveMigrations.Run(state, 1, mc, 0), "a version-1 save runs the whole chain to 5");
            T.Eq(0, state.diagnostics.failedMigrations.Count, "no migration failed");
            T.Check(mc.log.Exists(l => l.Contains("PhaseTwoStores")) && mc.log.Exists(l => l.Contains("SpatialContinuity")) && mc.log.Exists(l => l.Contains("ContractorCareers")) && mc.log.Exists(l => l.Contains("PhysicalLifecycle")), "1→2, 2→3, 3→4 and 4→5 all ran, in order");
            int i1 = mc.log.FindIndex(l => l.Contains("PhaseTwoStores")), i2 = mc.log.FindIndex(l => l.Contains("SpatialContinuity")), i3 = mc.log.FindIndex(l => l.Contains("ContractorCareers")), i4 = mc.log.FindIndex(l => l.Contains("PhysicalLifecycle"));
            T.Check(i1 < i2 && i2 < i3 && i3 < i4, "in order");
            foreach (NetworkActor a in state.actors.actors) T.Eq(CareerPolicy.FameFor(a.reputation.score), a.reputation.fame, "band and score agree after the chain");
            // Running the final step a second time is harmless (the version gate prevents it in a game).
            MigrationContext again = new MigrationContext();
            T.Eq(5, SaveMigrations.Run(state, 5, again, 0), "a current save runs nothing");
            T.Eq(0, again.log.Count, "and logs nothing");
            T.Eq(5, SaveMigrations.Registry[SaveMigrations.Registry.Count - 1].To, "the chain ends at 5 (Phase 3.0: bumped exactly once)");
        }
    }
}
