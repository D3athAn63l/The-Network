using System;
using System.Collections.Generic;
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

namespace TheNetwork.Diagnostics.RuntimeTests.Suites
{
    /// <summary>
    /// RT-PROC: procurement through the PRODUCTION Procurement / Operation services inside an isolated sandbox. The logical counterpart
    /// of what the owner saw in a real colony (RT-PROC-007: a held balance, funds arriving, the SAME contract resuming and delivering
    /// exactly what was committed), and the exactly-once invariants of closing a contract.
    /// </summary>
    public static class ProcurementRuntimeSuite
    {
        public const string Suite = "PROC";

        public static IEnumerable<RuntimeTestCase> Cases(IRuntimeTestHost host)
        {
            yield return RuntimeTestCase.Immediate(Suite, "RT-PROC-001", "Post, bid, offer, accept: the operation starts", PostToOperation, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PROC-002", "Full success: requested equals secured", FullSuccess, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PROC-003", "The balance is paid exactly once", BalanceOnce, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PROC-004", "Delivery commits the exact payload once", DeliveryOnce, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PROC-005", "The terminal contract closed exactly once", TerminalOnce, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PROC-006", "Repeated terminal calls change nothing", RepeatedTerminalCalls, true);
            yield return RuntimeTestCase.Stepped(Suite, "RT-PROC-007", "Payment hold, funds restored, the same contract resumes", PaymentHoldResumes, true);
            yield return RuntimeTestCase.Stepped(Suite, "RT-PROC-008", "A payment hold survives scheduler passes without duplicate charges or contracts", HoldSurvivesPasses, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PROC-009", "The partial-result decision path is deterministic", PartialDeterministic, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PROC-010", "A technical Void leaves the current contractor no windfall", VoidNoWindfall, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PROC-011", "A real catalog item is priced and bid through production services", RealDefPricing, true);
        }

        // ------------------------------------------------------------------ shared scenario

        private sealed class Job
        {
            public RuntimeTestSandbox Sb;
            public NetworkActor Fixer;
            public NetworkActor Team;
            public Contract Contract;
            public Operation Op;
        }

        /// <summary>A fixer, a willing contractor and an accepted contract (the operation has started).</summary>
        private static Job Start(RuntimeTestContext ctx, string insurance = "None", int count = 150, bool holding = false)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            Job j = new Job { Sb = sb };
            j.Fixer = sb.AddFixer("Standard", insurance);
            j.Team = sb.AddContractor();
            if (holding) RuntimeScenarios.MakeHoldingContractor(j.Team);
            j.Contract = sb.Award(j.Fixer, j.Team, RuntimeTestSandbox.Steel, count, insurance != "None");
            j.Op = sb.OpOf(j.Contract);
            ctx.Track(j.Contract);
            ctx.Track(j.Op);
            ctx.Track(j.Team);
            return j;
        }

        private static Job CompletedJob(RuntimeTestContext ctx)
        {
            Job j = Start(ctx);
            RuntimeScenarios.RunToEnd(ctx, j.Contract, OutcomeBand.Triumph);
            return j;
        }

        // ------------------------------------------------------------------ RT-PROC-001 .. 006

        private static void PostToOperation(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor fixer = sb.AddFixer();
            NetworkActor team = sb.AddContractor();
            Contract c = sb.Post(fixer, RuntimeTestSandbox.Steel, 150, team);
            ctx.Track(c);
            ctx.Assert.True(c.IsSeeking, "a posted contract is seeking a contractor (status " + c.status + ")");
            Offer o = sb.CollectOffer(c);
            ctx.Assert.NotNull(o, "the contractor bids (refusals: " + RuntimeTestSandbox.Refusals(c) + ")");
            CommandResult r = sb.Ctx.Procurement.Accept(o.id, false);
            ctx.Assert.True(r.ok, "the quote is accepted (" + r + ")");
            ctx.Assert.True(!c.IsSeeking && !c.IsTerminal, "the contract is live after acceptance (" + c.status + ")");
            Operation op = sb.OpOf(c);
            ctx.Track(op);
            ctx.Assert.NotNull(op, "an operation started");
            ctx.Assert.Equal(OpPhase.Preparing, op.phase, "the operation is preparing");
            ctx.Assert.True(c.Funding(MoneyPurpose.Deposit) > 0, "a deposit was paid");
            ctx.Assert.Equal(c.ExternalCharged(), sb.Payment.charged, "the sandbox purse was charged exactly what the ledger says");
            ctx.Assert.Equal(1, sb.Ctx.contracts.contracts.Count, "exactly one contract exists");
            ctx.Assert.Equal(1, sb.Ctx.operations.operations.Count, "exactly one operation exists");
        }

        private static void FullSuccess(RuntimeTestContext ctx)
        {
            Job j = CompletedJob(ctx);
            ctx.Assert.NotNull(j.Op.outcome, "the operation committed an outcome");
            ctx.Assert.Equal(OutcomeBand.Triumph, j.Op.outcome.band, "the forced Triumph was the committed band");
            ctx.Assert.Equal(j.Op.outcome.requested, j.Op.outcome.secured, "everything requested was secured");
            ctx.Assert.Equal(150, j.Op.outcome.requested, "the request was 150");
            ctx.Assert.Equal(ContractStatus.Fulfilled, j.Contract.status, "the contract is fulfilled");
            ctx.Assert.Equal(j.Contract.Quantity, j.Contract.Acquire.secured, "the contract's own secured count matches the request");
        }

        private static void BalanceOnce(RuntimeTestContext ctx)
        {
            Job j = CompletedJob(ctx);
            Contract c = j.Contract;
            ctx.Assert.Equal(1, RuntimeScenarios.Count(c, MoneyDirection.PlayerPaid, MoneyPurpose.Balance), "exactly one balance payment record");
            MoneyRecord balance = RuntimeScenarios.Find(c, MoneyDirection.PlayerPaid, MoneyPurpose.Balance);
            ctx.Assert.Equal(c.Deliver.balanceDue, balance.silver, "the record is the balance the delivery asked for");
            ctx.Assert.Equal(c.ExternalCharged(), j.Sb.Payment.charged, "the purse was charged exactly the ledger's total (nothing twice)");
            int paidRecords = 0;
            foreach (MoneyRecord m in c.ledger) if (m.direction == MoneyDirection.PlayerPaid && m.purpose != MoneyPurpose.InsurancePremium) paidRecords++;
            ctx.Assert.AtLeast(paidRecords, j.Sb.Payment.chargeCalls, "one purse charge per payment record");
            ctx.Assert.Equal(0, c.ExternalRefunded(), "nothing was refunded on a delivered contract");
        }

        private static void DeliveryOnce(RuntimeTestContext ctx)
        {
            Job j = CompletedJob(ctx);
            ctx.Assert.Equal(1, j.Sb.Delivery.deliveries, "the goods were handed over once");
            ctx.Assert.Equal(150, j.Sb.Delivery.deliveredItems, "exactly the committed quantity was delivered");
            ctx.Assert.True(j.Sb.Delivery.deliveredDefs.Contains(RuntimeTestSandbox.Steel), "the committed item def was the one delivered (" + string.Join(", ", j.Sb.Delivery.deliveredDefs.ToArray()) + ")");
            ctx.Assert.Equal(150, j.Contract.Acquire.delivered, "the contract recorded the delivery");
            ctx.Assert.Equal(0, j.Sb.Delivery.deliverCalls - j.Sb.Delivery.deliveries, "no failed delivery attempt happened on the way");
        }

        private static void TerminalOnce(RuntimeTestContext ctx)
        {
            Job j = CompletedJob(ctx);
            ctx.Assert.True(j.Contract.IsTerminal, "the contract is terminal");
            ctx.Assert.Equal(ContractStatus.Fulfilled, j.Contract.status, "and Fulfilled");
            ctx.Assert.Equal(1, j.Sb.Events.Count(EventKeys.ContractCompleted), "exactly one Contract.Completed event");
            ctx.Assert.True(j.Contract.closedTick > 0, "it has a closing tick");
            ctx.Assert.Equal(OpPhase.Done, j.Op.phase, "the operation is done");
            ctx.Assert.True(j.Op.IsFinished, "and finished");
        }

        private static void RepeatedTerminalCalls(RuntimeTestContext ctx)
        {
            Job j = CompletedJob(ctx);
            Contract c = j.Contract;
            string before = Digest(j, true);
            j.Sb.Ctx.Procurement.Void(c, Causes.DefMissing);
            j.Sb.Ctx.Operations.Finish(j.Op);
            j.Sb.Ctx.Operations.Abort(j.Op, "Repeat");
            ctx.Assert.False(j.Sb.Ctx.Procurement.PayBalance(c.id).ok, "nothing is owed on a closed contract");
            ctx.Assert.False(j.Sb.Ctx.Procurement.Cancel(c.id).ok, "a closed contract cannot be cancelled");
            j.Sb.Ctx.Career.CommitOutcome(j.Op);
            ctx.Assert.Equal(before, Digest(j, true), "no repeated terminal call changed anything (status, money, deliveries, events, career, funds)");
            string contractBefore = Digest(j, false);
            j.Sb.Advance(10 * Ticks.PerDay);
            ctx.Assert.Equal(contractBefore, Digest(j, false), "ten more days leave the closed contract, its money, its delivery and its career result untouched");
            ctx.Assert.Equal(ContractStatus.Fulfilled, c.status, "still Fulfilled");
        }

        /// <summary>The contract's closed state. With <paramref name="whole"/>, also the event count and the contractor's funds (which ordinary days legitimately move: upkeep).</summary>
        private static string Digest(Job j, bool whole)
        {
            Contract c = j.Contract;
            CareerRecord r = RuntimeTestSandbox.Sim(j.Team).career;
            string s = c.status + "|" + c.ledger.Count + "|" + c.ExternalCharged() + "|" + c.ExternalRefunded() + "|" + j.Sb.Delivery.deliveries + "|" + r.Classified + "|" + j.Team.reputation.score + "|" + j.Op.careerOutcomeApplied;
            return whole ? s + "|" + j.Sb.Events.events.Count + "|" + RuntimeTestSandbox.Sim(j.Team).funds : s;
        }

        // ------------------------------------------------------------------ RT-PROC-007: the owner-inspired scenario

        /// <summary>
        /// The logical counterpart of the owner's real Plasteel contract: the contractor secures everything, the client cannot cover
        /// the balance at delivery, the contractor HOLDS the goods (the real AwaitingPayment path), funds arrive, and the SAME contract
        /// resumes and delivers exactly what was committed. Nothing is hardcoded about the amount: the balance is whatever the
        /// generated terms say, and the invariant is "available below the balance holds; available at or above it resumes".
        /// </summary>
        private static RuntimeStep PaymentHoldResumes(RuntimeTestContext ctx)
        {
            switch (ctx.StepIndex)
            {
                case 1:
                {
                    Job j = Start(ctx, "None", 150, true);
                    int balance = j.Contract.terms.balance;
                    ctx.Assert.AtLeast(1, balance, "the generated terms leave a balance to pay on delivery (price " + j.Contract.terms.price + ", deposit " + j.Contract.terms.deposit + ")");
                    // The deposit path already succeeded at award. Now the client's purse is deliberately a little short of the balance.
                    int available = balance - 1;
                    j.Sb.Payment.silver = available;
                    ctx.Data["job"] = j;
                    ctx.Data["balance"] = balance;
                    ctx.Data["chargedBeforeHold"] = j.Sb.Payment.charged;
                    RuntimeScenarios.RunUntil(ctx, () => j.Contract.subStatus == SubStatus.AwaitingPayment || j.Contract.IsTerminal, "the contract to enter the payment hold", OutcomeBand.Triumph);
                    ctx.Assert.Equal(SubStatus.AwaitingPayment, j.Contract.subStatus, "available (" + available + ") < balance (" + balance + "): the contractor holds the goods (the real AwaitingPayment path), status " + j.Contract.status);
                    ctx.Assert.Equal(balance, j.Contract.Deliver.balanceDue, "the amount owed is the quoted balance");
                    ctx.Assert.False(j.Contract.Deliver.balancePaid, "the balance is not paid");
                    ctx.Assert.Equal(150, j.Contract.Acquire.secured, "the full requested quantity was secured");
                    ctx.Assert.Equal(0, j.Sb.Delivery.deliveries, "nothing was delivered for free");
                    ctx.Assert.Equal(0, j.Sb.Delivery.deliverCalls, "the delivery port was not even asked to drop");
                    ctx.Assert.Equal((int)ctx.Data["chargedBeforeHold"], j.Sb.Payment.charged, "no silver was taken for the held balance");
                    ctx.Assert.Equal(1, j.Sb.Events.Count(EventKeys.PaymentDefaulted), "one Payment.Defaulted event");
                    ctx.Assert.Equal(1, j.Sb.Ctx.contracts.contracts.Count, "no duplicate contract");
                    ctx.Assert.Equal(1, j.Sb.Ctx.operations.operations.Count, "no duplicate operation");
                    CommandResult cannot = j.Sb.Ctx.Procurement.CanPayBalance(j.Contract.id);
                    ctx.Assert.False(cannot.ok, "the client cannot pay yet (" + cannot + ")");
                    return RuntimeStep.Continue();
                }
                case 2:
                {
                    Job j = (Job)ctx.Data["job"];
                    int balance = (int)ctx.Data["balance"];
                    // Funds arrive (the owner mined more silver): available >= balance, and the SAME contract is resumed through the real command.
                    j.Sb.Payment.silver = balance + 1;
                    ctx.Assert.True(j.Sb.Ctx.Procurement.CanPayBalance(j.Contract.id).ok, "with the funds restored the client can pay");
                    CommandResult paid = j.Sb.Ctx.Procurement.PayBalance(j.Contract.id);
                    ctx.Assert.True(paid.ok, "the held contract is resumed (" + paid + ")");
                    return RuntimeStep.Continue();
                }
                default:
                {
                    Job j = (Job)ctx.Data["job"];
                    int balance = (int)ctx.Data["balance"];
                    Contract c = j.Contract;
                    ctx.Assert.Equal(ContractStatus.Fulfilled, c.status, "the same contract is Fulfilled (" + c.status + (c.subStatus != null ? "/" + c.subStatus : "") + ")");
                    ctx.Assert.Equal(150, c.Acquire.delivered, "requested equals delivered");
                    ctx.Assert.Equal(150, j.Sb.Delivery.deliveredItems, "the delivery port received exactly the committed payload");
                    ctx.Assert.Equal(1, j.Sb.Delivery.deliveries, "in one delivery");
                    ctx.Assert.Equal(1, RuntimeScenarios.Count(c, MoneyDirection.PlayerPaid, MoneyPurpose.Balance), "the balance was charged exactly once");
                    ctx.Assert.Equal(balance, RuntimeScenarios.Find(c, MoneyDirection.PlayerPaid, MoneyPurpose.Balance).silver, "and it was the quoted balance");
                    ctx.Assert.Equal(c.ExternalCharged(), j.Sb.Payment.charged, "the purse charge equals the ledger: nothing twice");
                    ctx.Assert.Equal(1, j.Sb.Ctx.contracts.contracts.Count, "still exactly one contract: no restart, no reroll");
                    ctx.Assert.Equal(1, j.Sb.Ctx.operations.operations.Count, "still exactly one operation");
                    ctx.Assert.Equal(1, j.Sb.Events.Count(EventKeys.ContractCompleted), "completed once");
                    return RuntimeStep.Pass();
                }
            }
        }

        private static RuntimeStep HoldSurvivesPasses(RuntimeTestContext ctx)
        {
            if (ctx.StepIndex == 1)
            {
                Job j = Start(ctx, "None", 150, true);
                int balance = j.Contract.terms.balance;
                ctx.Assert.AtLeast(1, balance, "there is a balance to hold on");
                j.Sb.Payment.silver = balance - 1;
                RuntimeScenarios.RunUntil(ctx, () => j.Contract.subStatus == SubStatus.AwaitingPayment || j.Contract.IsTerminal, "the payment hold", OutcomeBand.Triumph);
                ctx.Assert.Equal(SubStatus.AwaitingPayment, j.Contract.subStatus, "the contract is held");
                ctx.Data["job"] = j;
                ctx.Data["charged"] = j.Sb.Payment.charged;
                ctx.Data["chargeCalls"] = j.Sb.Payment.chargeCalls;
                ctx.Data["passes"] = 0;
                return RuntimeStep.Wait("hold, pass 1");
            }
            Job h = (Job)ctx.Data["job"];
            ContractKindRules rules = ProcurementService.Rules(h.Contract);
            int passes = (int)ctx.Data["passes"] + 1;
            ctx.Data["passes"] = passes;
            // Each pass is one day of the sandbox's own scheduler (all of it inside the grace period).
            h.Sb.Advance(Ticks.PerDay);
            ctx.Assert.Equal(SubStatus.AwaitingPayment, h.Contract.subStatus, "still held after " + passes + " day(s) (grace " + rules.awaitingPaymentGraceDays + " days)");
            ctx.Assert.Equal((int)ctx.Data["charged"], h.Sb.Payment.charged, "no charge was taken by a scheduler pass");
            ctx.Assert.Equal((int)ctx.Data["chargeCalls"], h.Sb.Payment.chargeCalls, "no charge was even attempted twice");
            ctx.Assert.Equal(0, h.Sb.Delivery.deliveries, "nothing was delivered while held");
            ctx.Assert.Equal(1, h.Sb.Ctx.contracts.contracts.Count, "no duplicate contract");
            ctx.Assert.Equal(1, h.Sb.Ctx.operations.operations.Count, "no duplicate operation");
            ctx.Assert.Equal(1, h.Sb.Events.Count(EventKeys.PaymentDefaulted), "the default was announced once");
            int safePasses = Math.Max(1, Math.Min(4, (int)rules.awaitingPaymentGraceDays - 1));
            return passes >= safePasses ? RuntimeStep.Pass() : RuntimeStep.Wait("hold, pass " + (passes + 1));
        }

        // ------------------------------------------------------------------ RT-PROC-009 / 010 / 011

        private static string PartialPath(RuntimeTestContext ctx, RuntimeTestSandbox sb, out int secured, out int delivered, out ContractStatus status)
        {
            NetworkActor fixer = sb.AddFixer("Standard", "None");
            NetworkActor team = sb.AddContractor();
            Contract c = sb.Award(fixer, team, RuntimeTestSandbox.Steel, 200, false);
            ctx.Track(c);
            ctx.Track(sb.OpOf(c));
            try
            {
                ProcurementDevOverrides.forceBand = OutcomeBand.Partial;
                ProcurementDevOverrides.forceSecured = 80;
                ProcurementDevOverrides.forceNotTroubled = true;
                ctx.Assert.True(sb.AdvanceUntil(() => c.status == ContractStatus.Renegotiating || c.IsTerminal, 80 * Ticks.PerDay), "the partial result reaches the client");
            }
            finally
            {
                ctx.ClearOverrides();
            }
            ctx.Assert.Equal(SubStatus.PartialResult, c.subStatus, "the client decides on a partial result");
            secured = c.Acquire.secured;
            ctx.Assert.Equal(80, secured, "the forced 80 of 200 was secured");
            ctx.Assert.True(sb.Ctx.Procurement.RespondPartial(c.id, PartialChoice.AcceptPartial).ok, "the client accepts the partial result");
            ctx.Assert.True(sb.AdvanceUntil(() => c.IsTerminal, 40 * Ticks.PerDay), "the contract closes");
            delivered = c.Acquire.delivered;
            status = c.status;
            return c.status + "|" + c.Acquire.secured + "|" + c.Acquire.delivered + "|" + c.ExternalCharged() + "|" + c.ExternalRefunded() + "|" + c.terms.price + "|" + c.terms.deposit + "|" + c.ledger.Count + "|" + sb.Delivery.deliveredItems;
        }

        private static void PartialDeterministic(RuntimeTestContext ctx)
        {
            int s1, d1, s2, d2;
            ContractStatus st1, st2;
            string a = PartialPath(ctx, ctx.RequireSandbox(), out s1, out d1, out st1);
            // Same test id, same seed: a second scratch world must make the same choices and reach the same state.
            RuntimeTestSandbox twin = new RuntimeTestSandbox(ctx.Id);
            string b;
            try
            {
                b = PartialPath(ctx, twin, out s2, out d2, out st2);
            }
            finally
            {
                twin.Dispose();
            }
            ctx.Assert.Equal(ContractStatus.PartiallyFulfilled, st1, "an accepted partial result closes as PartiallyFulfilled");
            ctx.Assert.Equal(80, d1, "exactly the secured part was delivered");
            ctx.Assert.Equal(a, b, "two worlds with the same seed take the same partial path (status, secured, delivered, money, ledger)");
        }

        private static void VoidNoWindfall(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor fixer = sb.AddFixer("Premium", "Basic");
            NetworkActor team = sb.AddContractor();
            Contract c = sb.Award(fixer, team, RuntimeTestSandbox.Steel, 150, true);
            ctx.Track(c);
            ctx.Track(sb.OpOf(c));
            ctx.Track(team);
            int paid = RuntimeScenarios.CreditOf(c);
            ctx.Assert.AtLeast(1, paid, "the contractor was paid at award");
            ctx.Assert.Equal(paid, (int)sb.Ctx.Career.counters.Flow(FundsFlow.Credit), "its funds were credited exactly that");
            sb.Ctx.Procurement.Void(c, Causes.DefMissing);
            ctx.Assert.Equal(ContractStatus.Voided, c.status, "the contract is voided");
            ctx.Assert.Equal(c.ExternalCharged(), c.ExternalRefunded(), "the player got every silver back (a technical invalidation is not an in-world failure)");
            ctx.Assert.Zero(c.ContractorHeld(), "the current contractor holds none of what it was paid on this contract");
            ctx.Assert.Equal(0L, sb.Ctx.Career.counters.Flow(FundsFlow.Credit) + sb.Ctx.Career.counters.Flow(FundsFlow.ClawBack), "credit and clawback cancel exactly");
            ctx.Assert.Zero(RuntimeTestSandbox.Sim(team).career.careerEarnings, "no career earnings from a voided contract");
            long funds, tallied;
            ctx.Assert.True(RuntimeScenarios.BooksBalance(sb, out funds, out tallied), "the books balance (funds " + funds + ", tallied " + tallied + ")");
        }

        private static void RealDefPricing(RuntimeTestContext ctx)
        {
            ItemFacts real = null;
            if (ctx.Host == null || !ctx.Host.TryGetRealItemFacts("Steel", out real) || real == null) ctx.Skip("this host has no real 'Steel' ThingDef facts (not a game, or the def is absent)");
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            ctx.Note("real facts for Steel: market value " + real.marketValue.ToString("0.##") + ", stack limit " + real.stackLimit + ", tech level " + real.techLevel);
            sb.Catalog.Add(real);
            NetworkActor fixer = sb.AddFixer();
            NetworkActor team = sb.AddContractor();
            Contract c = sb.Post(fixer, "Steel", 150, team);
            ctx.Track(c);
            Offer o = sb.CollectOffer(c);
            if (o == null)
            {
                ctx.Warn("no sandbox contractor bid on the real Steel facts (" + RuntimeTestSandbox.Refusals(c) + "): a gameplay/mod-list state, not a defect");
                return;
            }
            ctx.Assert.AtLeast(1, o.quote.finalPrice, "the production pricing quoted the real item above zero (" + o.quote.finalPrice + " for 150)");
            ctx.Assert.True(sb.Ctx.Procurement.Accept(o.id, false).ok, "the quote on a real def is accepted");
            ctx.Assert.Equal("Steel", c.Acquire.DefName, "the contract targets the real def by name");
        }
    }
}
