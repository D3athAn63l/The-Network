using System;
using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.Settings;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Diagnostics.RuntimeTests.Suites
{
    /// <summary>
    /// RT-CAR: the Phase 2.75 career spine inside the sandbox: integration-critical representative cases (the exhaustive matrix stays in
    /// the headless suite). Every case runs the production CareerService / ProcurementService; the policy functions are the oracle
    /// (the tests never restate a number).
    /// </summary>
    public static class CareerRuntimeSuite
    {
        public const string Suite = "CAR";

        public static IEnumerable<RuntimeTestCase> Cases(IRuntimeTestHost host)
        {
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-001", "A new eligible operation applies one CareerRecord result", OneResult, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-002", "The applied flag is set only after the durable career commit", AppliedAfterCommit, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-003", "Repeated Finish, retry and validation do not duplicate the result", NoDuplicate, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-004", "Numeric reputation changes and the fame band follows the score", ReputationAndBand, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-005", "Fame stays independent from Experience", FameVsExperience, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-006", "Contractor payment enters funds exactly once", PaymentEntersFundsOnce, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-007", "An ordinary refund claws back proportionally", OrdinaryRefundProportional, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-008", "A replacement's TransferIn is not the replacement's income", TransferInIsNotIncome, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-009", "An insurance payout itself does not claw contractor money", InsurancePayoutNoClaw, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-010", "Insurance payout then technical Void: the current contractor retains zero", PayoutThenVoid, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-011", "Equipment advances one tier only, needing fame, money and reserve, with a cooldown", Advancement, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-012", "CareerNeed is deterministic", NeedDeterministic, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-013", "Tags describe state and change no strength by being read", TagsChangeNothing, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-CAR-014", "Augmented is not emitted without augmentation truth", NoAugmented, true);
        }

        private static ContractorSimulation Sim(NetworkActor a) { return RuntimeTestSandbox.Sim(a); }

        private static void SetFunds(RuntimeTestSandbox sb, NetworkActor a, int funds)
        {
            ContractorSimulation sim = Sim(a);
            sb.Ctx.Career.MoveFunds(sim, (long)funds - sim.funds, FundsFlow.Dev);
        }

        private static Contract Job(RuntimeTestContext ctx, NetworkActor fixer, NetworkActor team, OutcomeBand band, int count = 150, bool insure = false, int? secured = null)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            Contract c = sb.Award(fixer, team, RuntimeTestSandbox.Steel, count, insure);
            ctx.Track(c);
            ctx.Track(sb.OpOf(c));
            ctx.Track(team);
            RuntimeScenarios.RunToEnd(ctx, c, band, true, secured);
            return c;
        }

        // ------------------------------------------------------------------ RT-CAR-001 .. 005

        private static void OneResult(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor team = sb.AddContractor();
            CareerRecord r = Sim(team).career;
            ctx.Assert.Equal(0L, r.Classified, "a new contractor has classified nothing");
            Contract c = Job(ctx, sb.AddFixer(), team, OutcomeBand.Success);
            Operation op = sb.OpOf(c);
            ctx.Assert.True(op.careerEligible, "an operation started by this build is career-eligible");
            ctx.Assert.True(op.careerOutcomeApplied, "its result was applied");
            ctx.Assert.Equal(1L, r.Classified, "exactly one classified result");
            ctx.Assert.Equal(1, r.successes, "a success");
            ctx.Assert.Zero(r.failures + r.disasters + r.partials + r.triumphs, "and nothing else");
            ctx.Assert.Equal(1, sb.Ctx.Career.counters.outcomesApplied, "the service counted one application");
        }

        /// <summary>A finished operation whose career result was never applied (as if its commit had failed): the same technique as the headless suite.</summary>
        private static Operation FinishedUnapplied(RuntimeTestContext ctx, RuntimeTestSandbox sb, NetworkActor team, OutcomeBand band)
        {
            Contract c = sb.Post(sb.AddFixer(), RuntimeTestSandbox.Steel, 150, team);
            ctx.Track(c);
            Offer o = sb.CollectOffer(c);
            ctx.Assert.NotNull(o, "the contractor bids");
            ctx.Assert.True(sb.Ctx.Procurement.Accept(o.id, false).ok, "accepted");
            Operation op = sb.OpOf(c);
            ctx.Track(op);
            op.careerEligible = false; // the lifecycle's own commit is skipped, exactly as for an old operation
            RuntimeScenarios.RunToEnd(ctx, c, band);
            ctx.Assert.True(op.IsFinished && !op.careerOutcomeApplied && op.outcome != null, "the operation finished with no career result applied");
            op.careerEligible = true;
            return op;
        }

        private static void AppliedAfterCommit(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor team = sb.AddContractor();
            team.reputation.SetScore(98);
            ctx.Track(team);
            Operation op = FinishedUnapplied(ctx, sb, team, OutcomeBand.Triumph);
            CareerRecord record = Sim(team).career;
            long classified = record.Classified;
            // A fault in the middle of the commit (a reputation that is missing: corrupt state): nothing may change and nothing may be flagged.
            // Production logs a warning for exactly this fault: it is the provoked, expected behaviour, not a surprise.
            ctx.ExpectLog("could not be applied");
            PublicReputation rep = team.reputation;
            team.reputation = null;
            bool applied;
            try
            {
                applied = sb.Ctx.Career.CommitOutcome(op);
            }
            finally
            {
                team.reputation = rep;
            }
            ctx.Assert.False(applied, "the commit reports the failure");
            ctx.Assert.False(op.careerOutcomeApplied, "the flag is NOT set when the durable commit did not happen");
            ctx.Assert.Equal(classified, record.Classified, "no counter changed");
            ctx.Assert.Equal(98, rep.score, "the reputation is untouched");
            // The fault is repaired: validation applies it, exactly once, and only then is the flag set.
            List<string> findings = new List<string>();
            sb.Ctx.Career.Validate(findings);
            ctx.Assert.True(op.careerOutcomeApplied, "applied once the commit really happened (" + string.Join("; ", findings.ToArray()) + ")");
            ctx.Assert.Equal(classified + 1, record.Classified, "exactly one result");
            // A missing career target (no contractor simulation) is also a failed commit, never an applied one.
            Operation op2 = FinishedUnapplied(ctx, sb, team, OutcomeBand.Success);
            ActorId real = op2.contractor;
            op2.contractor = ActorId.None;
            try
            {
                ctx.Assert.False(sb.Ctx.Career.CommitOutcome(op2), "no contractor actor: the commit fails");
            }
            finally
            {
                op2.contractor = real;
            }
            ctx.Assert.False(op2.careerOutcomeApplied, "and it is not marked applied");
            ctx.Assert.True(sb.Ctx.Career.CommitOutcome(op2), "the repaired target applies it");
        }

        private static void NoDuplicate(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor team = sb.AddContractor();
            Contract c = Job(ctx, sb.AddFixer(), team, OutcomeBand.Success);
            Operation op = sb.OpOf(c);
            CareerRecord r = Sim(team).career;
            long classified = r.Classified;
            int score = team.reputation.score;
            ctx.Assert.Equal(1L, classified, "one result so far");
            sb.Ctx.Operations.Finish(op);
            sb.Ctx.Operations.Abort(op, "Repeat");
            ctx.Assert.False(sb.Ctx.Career.CommitOutcome(op), "a retry applies nothing");
            sb.Ctx.Career.Validate(new List<string>());
            sb.Ctx.Career.Validate(new List<string>());
            ctx.Assert.Equal(classified, r.Classified, "repeats and validations left one result");
            ctx.Assert.Equal(score, team.reputation.score, "and the reputation unchanged");
        }

        private static void ReputationAndBand(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor team = sb.AddContractor();
            team.reputation.SetScore(98);
            ctx.Assert.Equal(CareerPolicy.FameFor(98), team.reputation.fame, "the band is derived from the score");
            Contract c = Job(ctx, sb.AddFixer(), team, OutcomeBand.Success);
            Operation op = sb.OpOf(c);
            int gain = CareerPolicy.ReputationGain(CareerService.DangerOf(op), OutcomeBand.Success, op.outcome.secured, op.outcome.requested, 98);
            ctx.Assert.AtLeast(1, gain, "a success earns reputation (the policy's own figure: " + gain + ")");
            ctx.Assert.Equal(98 + gain, team.reputation.score, "the score moved by exactly the policy's gain, once");
            ctx.Assert.Equal(CareerPolicy.FameFor(team.reputation.score), team.reputation.fame, "the fame band follows the score");
            ctx.Assert.Equal(gain, Sim(team).career.reputationEarned, "the record says how much it earned");
        }

        private static void FameVsExperience(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor team = sb.AddContractor(ContractorForm.Team, ExperienceBand.Seasoned, FameBand.Unknown);
            ExperienceBand exp = ContractorService.Experience(team);
            team.reputation.SetScore(CareerPolicy.LegendaryAt);
            ctx.Assert.Equal(FameBand.Legendary, team.reputation.fame, "fame can be Legendary");
            ctx.Assert.Equal(exp, ContractorService.Experience(team), "and experience did not move with it");
            float skill = Sim(team).skill;
            Sim(team).skill = Math.Min(1f, skill + 0.2f);
            team.reputation.SetScore(0);
            ctx.Assert.Equal(FameBand.Unknown, team.reputation.fame, "fame can fall back to Unknown");
            ctx.Assert.True(Sim(team).skill >= skill, "without touching skill");
            Sim(team).skill = skill;
        }

        // ------------------------------------------------------------------ RT-CAR-006 .. 010: the money

        private static void PaymentEntersFundsOnce(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor team = sb.AddContractor();
            Contract c = sb.Award(sb.AddFixer(), team, RuntimeTestSandbox.Steel, 150, false);
            ctx.Track(c);
            ctx.Track(team);
            int atAward = RuntimeScenarios.CreditOf(c);
            ctx.Assert.AtLeast(1, atAward, "the contractor was credited its share at award");
            ctx.Assert.Equal(atAward, (int)sb.Ctx.Career.counters.Flow(FundsFlow.Credit), "exactly that reached its funds");
            RuntimeScenarios.RunToEnd(ctx, c, OutcomeBand.Triumph);
            int total = RuntimeScenarios.CreditOf(c);
            ctx.Assert.AtLeast(atAward, total, "the balance added its share");
            ctx.Assert.Equal(total, (int)sb.Ctx.Career.counters.Flow(FundsFlow.Credit), "funds were credited exactly the ledger's attribution, once");
            ctx.Assert.Equal(total, Sim(team).career.careerEarnings, "career earnings agree");
            ctx.Assert.Equal(total, c.ContractorHeld(), "and the contractor holds exactly what it was paid");
            long funds, tallied;
            ctx.Assert.True(RuntimeScenarios.BooksBalance(sb, out funds, out tallied), "the books balance (funds " + funds + ", tallied " + tallied + ")");
        }

        private static void OrdinaryRefundProportional(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor fixer = sb.AddFixer("Premium");
            NetworkActor team = sb.AddContractor();
            Contract c = sb.Post(fixer, RuntimeTestSandbox.Steel, 150, team, 400);
            ctx.Track(c);
            ctx.Track(team);
            Offer o = sb.CollectOffer(c);
            ctx.Assert.NotNull(o, "the contractor bids");
            ctx.Assert.True(sb.Ctx.Procurement.Accept(o.id, false).ok, "accepted");
            int held = c.ContractorHeld(), remaining = c.OwnBearingRemaining();
            ctx.Assert.True(held > 0 && remaining == c.Funding(MoneyPurpose.Deposit) + c.Funding(MoneyPurpose.Premium), "all its funding is the player's own payment");
            ctx.Assert.True(sb.Ctx.Procurement.Cancel(c.id).ok, "the client cancels while the work is being prepared");
            MoneyRecord refund = RuntimeScenarios.Find(c, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
            ctx.Assert.NotNull(refund, "the Fixer's policy refunded part of the deposit and premium");
            ctx.Assert.True(refund.silver > 0 && refund.silver < remaining, "a partial refund (" + refund.silver + " of " + remaining + ")");
            ctx.Assert.Equal(refund.silver, refund.fromOwnFunding, "all of it was drawn from the player's own payments");
            ctx.Assert.False(refund.fullReversal, "an ordinary refund is not a full reversal");
            ctx.Assert.Equal(-(int)((long)held * refund.silver / remaining), refund.contractorSilver, "the contractor gives back the same proportion of what it holds");
            ctx.Assert.Equal(held + refund.contractorSilver, c.ContractorHeld(), "and keeps the rest");
        }

        private static Contract ReplacedJob(RuntimeTestContext ctx, RuntimeTestSandbox sb, NetworkActor fixer, NetworkActor a, out Contract parent, bool insure)
        {
            Contract first = sb.Post(fixer, RuntimeTestSandbox.Steel, 150, a, 300);
            parent = first;
            ctx.Track(first);
            Offer o = sb.CollectOffer(first);
            ctx.Assert.NotNull(o, "contractor A bids");
            ctx.Assert.True(sb.Ctx.Procurement.Accept(o.id, insure).ok, "A's quote is accepted");
            sb.Ctx.Contractors.EndActor(a, "RuntimeTest");
            ctx.Assert.True(sb.AdvanceUntil(() => first.IsTerminal, 20 * Ticks.PerDay), "A's contract is replaced");
            ctx.Assert.Equal(1, first.lineage.children.Count, "the Fixer sent a replacement");
            Contract child = sb.Ctx.contracts.Get(first.lineage.children[0]);
            ctx.Track(child);
            return child;
        }

        private static void TransferInIsNotIncome(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor fixer = sb.AddFixer("Premium");
            NetworkActor a = sb.AddContractor();
            NetworkActor b = sb.AddContractor();
            Contract parent;
            Contract child = ReplacedJob(ctx, sb, fixer, a, out parent, false);
            int aHeld = parent.ContractorHeld();
            ctx.Assert.AtLeast(1, aHeld, "A was paid on the original contract");
            ctx.Assert.AtLeast(1, child.TransferredIn(), "the replacement inherited a funding position (" + child.TransferredIn() + ")");
            foreach (MoneyRecord m in child.ledger) if (m.direction == MoneyDirection.TransferIn) ctx.Assert.Zero(m.contractorSilver, "a TransferIn credits no contractor");
            ctx.Assert.Zero(child.ContractorHeld(), "the replacement holds nothing from carried-over funding");
            ctx.Assert.Equal(aHeld, (int)sb.Ctx.Career.counters.Flow(FundsFlow.Credit), "no contractor was credited anything beyond A's original pay");
            ctx.Assert.Equal(aHeld, parent.ContractorHeld(), "and A's pay stands as it was");
        }

        private static void InsurancePayoutNoClaw(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor fixer = sb.AddFixer("Premium", "Basic");
            NetworkActor team = sb.AddContractor();
            Contract c = Job(ctx, fixer, team, OutcomeBand.Failure, 150, true, 0);
            ctx.Assert.Equal(ContractStatus.Failed, c.status, "the job failed");
            MoneyRecord premium = RuntimeScenarios.Find(c, MoneyDirection.PlayerPaid, MoneyPurpose.InsurancePremium);
            MoneyRecord payout = RuntimeScenarios.Find(c, MoneyDirection.PlayerRefunded, MoneyPurpose.InsurancePayout);
            ctx.Assert.True(premium != null && payout != null, "an insurance premium and a payout (the insurer's money)");
            ctx.Assert.Zero(premium.contractorSilver, "the premium never reached the contractor");
            ctx.Assert.Zero(payout.contractorSilver, "and the payout was not taken from it");
            ctx.Assert.Equal(0L, sb.Ctx.Career.counters.Flow(FundsFlow.ClawBack), "nothing was clawed back");
            MoneyRecord deposit = RuntimeScenarios.Find(c, MoneyDirection.PlayerPaid, MoneyPurpose.Deposit);
            ctx.Assert.Equal(deposit.contractorSilver, c.ContractorHeld(), "the contractor kept what it was paid (the deposit is lost on failure)");
        }

        private static void PayoutThenVoid(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor fixer = sb.AddFixer("Premium", "Generous");
            NetworkActor team = sb.AddContractor();
            Contract c;
            try
            {
                ProcurementDevOverrides.forceBand = OutcomeBand.Partial;
                ProcurementDevOverrides.forceSecured = 20;
                ProcurementDevOverrides.forceNotTroubled = true;
                c = sb.Award(fixer, team, RuntimeTestSandbox.Steel, 200, true);
                ctx.Track(c);
                ctx.Track(team);
                ctx.Assert.True(sb.AdvanceUntil(() => c.status == ContractStatus.Renegotiating || c.IsTerminal, 80 * Ticks.PerDay), "the partial result reaches the client");
            }
            finally
            {
                ctx.ClearOverrides();
            }
            ctx.Assert.Equal(SubStatus.PartialResult, c.subStatus, "a partial result waits for the client");
            ctx.Assert.True(c.terms.insurance != null && c.terms.insurance.Covers(Causes.PartialShortfall), "a Generous policy covers a partial shortfall");
            sb.Delivery.mode = SandboxDeliveryMode.CreationFailure; // the accepted goods cannot be made: a technical invalidation after the payout
            ctx.Assert.True(sb.Ctx.Procurement.RespondPartial(c.id, PartialChoice.AcceptPartial).ok, "the client accepts the partial result");
            ctx.Assert.Equal(ContractStatus.Voided, c.status, "the item cannot be produced: technically voided (" + c.causeKey + ")");
            MoneyRecord payout = RuntimeScenarios.Find(c, MoneyDirection.PlayerRefunded, MoneyPurpose.InsurancePayout);
            MoneyRecord refund = RuntimeScenarios.Find(c, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund);
            ctx.Assert.True(payout != null && refund != null, "the insurer paid out for the shortfall, then the void refunded the player");
            ctx.Assert.Zero(payout.contractorSilver, "the payout took nothing from the contractor");
            ctx.Assert.Equal(c.ExternalCharged() - payout.silver, refund.silver, "the player's refund is what it paid less the payout it already received");
            ctx.Assert.Equal(sb.Payment.charged, payout.silver + refund.silver, "the player is made whole, and not paid twice");
            ctx.Assert.True(refund.fullReversal, "the void is typed as a full contractor reversal");
            ctx.Assert.True(refund.fromOwnFunding >= 0 && refund.fromOwnFunding <= refund.silver, "the provenance still describes only the player's refund");
            ctx.Assert.Zero(c.ContractorHeld(), "the current contractor retains none of what it was paid on this contract");
            ctx.Assert.Zero(Sim(team).career.careerEarnings, "its career earnings reflect the full reversal");
            ctx.Assert.Equal(0L, sb.Ctx.Career.counters.Flow(FundsFlow.Credit) + sb.Ctx.Career.counters.Flow(FundsFlow.ClawBack), "credit and clawback cancel exactly");
        }

        // ------------------------------------------------------------------ RT-CAR-011 .. 014

        /// <summary>A quiet contractor at a chosen tier and fame, with funds to spare, at home (no commitment).</summary>
        private static NetworkActor Ready(RuntimeTestSandbox sb, int tier, FameBand fame, int funds)
        {
            NetworkActor a = sb.AddContractor(ContractorForm.Solo, ExperienceBand.Seasoned, fame);
            ContractorSimulation sim = Sim(a);
            sim.equipment.tier = tier;
            sim.equipment.condition = 0.95f;
            sim.morale.descriptor = MoraleDescriptor.Steady;
            SetFunds(sb, a, funds);
            return a;
        }

        private static AdvancementBlock Block(RuntimeTestSandbox sb, NetworkActor a)
        {
            int cost, reserve;
            return sb.Ctx.Career.BlockedBy(a, out cost, out reserve);
        }

        private static void Advancement(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Ready(sb, 1, FameBand.Local, 0);
            ctx.Track(a);
            int reserve = CareerService.OperatingReserve(a);
            int cost = CareerPolicy.UpgradeCost(1);
            ctx.Assert.AtLeast(CareerPolicy.ReserveFloor, reserve, "the operating reserve has a floor");
            // Money: the price alone is not enough, the reserve must remain.
            SetFunds(sb, a, cost + reserve - 1);
            ctx.Assert.Equal(AdvancementBlock.NeedsFunds, Block(sb, a), "one silver short of cost + reserve");
            ctx.Assert.False(sb.Ctx.Career.RunAdvancement(a), "nothing is bought");
            // Fame: money alone is not enough either.
            SetFunds(sb, a, cost + reserve);
            a.reputation.SetScore(0);
            ctx.Assert.Equal(AdvancementBlock.NeedsReputation, Block(sb, a), "without the fame the next tier needs");
            a.reputation.SetScore(CareerPolicy.FloorOf(CareerPolicy.RequiredFame(2)));
            ctx.Assert.Equal(AdvancementBlock.None, Block(sb, a), "with fame, funds and reserve it is possible");
            ctx.Assert.True(sb.Ctx.Career.RunAdvancement(a), "bought");
            ctx.Assert.Equal(2, Sim(a).equipment.tier, "exactly one tier");
            ctx.Assert.Equal(reserve, Sim(a).funds, "and exactly the reserve remains");
            // One tier per cooldown: rich and famous again, still only one.
            SetFunds(sb, a, 100000000);
            a.reputation.SetScore(CareerPolicy.LegendaryAt);
            ctx.Assert.Equal(AdvancementBlock.Cooldown, Block(sb, a), "the cooldown applies");
            ctx.Assert.False(sb.Ctx.Career.RunAdvancement(a), "no second purchase");
            ctx.Assert.Equal(2, Sim(a).equipment.tier, "the tier did not move");
        }

        private static string NeedOf(RuntimeTestSandbox sb)
        {
            NetworkActor a = Ready(sb, 2, FameBand.Established, 100000);
            return sb.Ctx.Career.CurrentNeed(a) + "|" + string.Join(",", sb.Ctx.Career.Tags(a).ToArray());
        }

        private static void NeedDeterministic(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Ready(sb, 2, FameBand.Established, 100000);
            ctx.Track(a);
            CareerNeed first = sb.Ctx.Career.CurrentNeed(a);
            for (int i = 0; i < 20; i++) sb.Ctx.Career.Tags(a);
            ctx.Assert.Equal(first, sb.Ctx.Career.CurrentNeed(a), "the same state always gives the same need, however often it is read");
            // Two scratch worlds with the same seed and the same contractor agree.
            RuntimeTestSandbox twin = new RuntimeTestSandbox(ctx.Id);
            try
            {
                ctx.Assert.Equal(NeedOf(twin), NeedOf(sb), "a second world with the same seed derives the same need and tags");
            }
            finally
            {
                twin.Dispose();
            }
            SetFunds(sb, a, 0);
            ctx.Assert.Equal(CareerNeed.Capital, sb.Ctx.Career.CurrentNeed(a), "with no funds the need is Capital (the fixed order)");
        }

        private static void TagsChangeNothing(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor fixer = sb.AddFixer();
            NetworkActor a = Ready(sb, 5, FameBand.Legendary, 99999999);
            ctx.Track(a);
            Sim(a).skill = 0.9f;
            Contract c = sb.Post(fixer, RuntimeTestSandbox.Steel, 100, a);
            ItemFacts f = sb.Catalog.Facts(RuntimeTestSandbox.Steel);
            float strength = sb.Ctx.Contractors.Strength(a);
            float edge = Resolver.Edge(sb.Ctx.Operations.Estimate(a, c, f));
            int funds = Sim(a).funds, tier = Sim(a).equipment.tier, score = a.reputation.score;
            for (int i = 0; i < 50; i++)
            {
                sb.Ctx.Career.Tags(a);
                sb.Ctx.Career.CurrentNeed(a);
                sb.Ctx.Career.Describe(a);
            }
            ctx.Assert.Equal(strength, sb.Ctx.Contractors.Strength(a), "reading tags changes no strength");
            ctx.Assert.Equal(edge, Resolver.Edge(sb.Ctx.Operations.Estimate(a, c, f)), "nor the resolver's inputs");
            ctx.Assert.True(Sim(a).funds == funds && Sim(a).equipment.tier == tier && a.reputation.score == score, "nor any state");
            ctx.Assert.AtLeast(3, sb.Ctx.Career.Tags(a).Count, "it carries several tags");
            float without = sb.Ctx.Contractors.Strength(a);
            Sim(a).funds = 0;
            Sim(a).MarkDirty();
            ctx.Assert.Equal(without, sb.Ctx.Contractors.Strength(a), "losing the Wealthy tag changes no strength");
        }

        private static void NoAugmented(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor top = Ready(sb, 5, FameBand.Legendary, 99999999);
            Sim(top).skill = 1f;
            NetworkActor company = sb.AddContractor(ContractorForm.Company, ExperienceBand.Legendary, FameBand.Legendary);
            int checkedActors = 0;
            foreach (NetworkActor a in sb.Ctx.actors.actors)
            {
                if (!ContractorService.IsNpcContractor(a)) continue;
                checkedActors++;
                ctx.Assert.False(sb.Ctx.Career.Tags(a).Contains(CareerTags.Augmented), "no contractor is tagged Augmented: there is no augmentation truth yet (" + a.name.Display + ")");
            }
            ctx.Assert.AtLeast(2, checkedActors, "the check covered the top-tier contractors");
            ctx.Assert.False(sb.Ctx.Career.HasTag(top, CareerTags.Augmented), "HasTag agrees");
        }
    }
}
