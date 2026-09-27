using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;

namespace TheNetwork.Domain.Contracts
{
    public enum WorseChoice : byte
    {
        PayMore = 0,
        AcceptReduced = 1,
        Refuse = 2
    }

    public enum PartialChoice : byte
    {
        AcceptPartial = 0,
        AcceptAndContinue = 1,
        AskSameContractor = 2,
        Refuse = 3
    }

    /// <summary>
    /// The second half of the procurement machine: what happens once the work is under way. The
    /// operation reports its checkpoints here; the client's decisions (renegotiation, partial result,
    /// balance) and their grace defaults are applied here; delivery, payment and failure close the
    /// contract. Terminal states are never reopened: continuations and replacements are NEW contracts.
    /// </summary>
    public sealed partial class ProcurementService
    {
        // ================================================================== before the work starts

        /// <summary>
        /// The contractor died, dissolved or vanished before engagement. No universal rule: the brokering
        /// Fixer's replacement policy decides (a replacement contractor on a new linked contract, or a full
        /// or partial refund), and insurance may cover the rest when its terms say so.
        /// </summary>
        public void OnContractorLostBeforeWork(Contract c, Operation op)
        {
            if (c == null || c.IsTerminal) return;
            if (op != null) ctx.Operations.Abort(op, Causes.ContractorLost);
            string policy = c.terms?.replacementPolicyKey;
            string brokerage = policy != null && policy.IndexOf('/') >= 0 ? policy.Substring(policy.IndexOf('/') + 1) : "Standard";
            bool tryReplacement;
            float refundShare;
            FixerPolicies.Replacement(policy, brokerage, out tryReplacement, out refundShare);
            ItemFacts f = ctx.catalog.Facts(c.Acquire.DefName);
            NetworkActor replacement = tryReplacement && f != null && IsBroker(ctx.actors.Get(c.parties.broker)) ? FindReplacement(c, f) : null;
            if (replacement != null)
            {
                Replace(c, replacement, f);
                Close(c, ContractStatus.Failed, Causes.PreWorkLoss, EventKeys.ContractFailed, Importance.Notable);
                return;
            }
            int deposit = c.PaidFor(NoteDeposit) + c.PaidFor(NoteTransferred);
            int refund = (int)Math.Round(deposit * refundShare) + c.PaidFor(NotePremium);
            int payout = 0;
            if (c.terms?.insurance != null && c.terms.insurance.Covers(Causes.PreWorkLoss))
            {
                payout = (int)Math.Round((deposit - deposit * refundShare) * c.terms.insurance.coverage);
            }
            if (refund > 0) Refund(c, refund, "refund.replacementPolicy");
            if (payout > 0) Refund(c, payout, "insurance.payout");
            Close(c, ContractStatus.Failed, Causes.PreWorkLoss, EventKeys.ContractFailed, Importance.Notable, refund, payout);
        }

        private NetworkActor FindReplacement(Contract c, ItemFacts f)
        {
            ActorId lost = c.parties.contractor;
            ProcurementMode mode = c.request.mode;
            c.request.mode = ProcurementMode.Open;
            List<ActorId> pool;
            try
            {
                pool = SelectCandidates(c, f);
            }
            finally
            {
                c.request.mode = mode;
            }
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i] == lost) continue;
                NetworkActor a = ctx.actors.Get(pool[i]);
                if (a == null || !a.IsActive) continue;
                if (Willingness.Evaluate(ctx, a, c, f).accept) return a;
            }
            return null;
        }

        /// <summary>A NEW contract taking over the obligation (lineage: parent, root, inheritedFrom). The paid deposit carries over.</summary>
        private Contract Replace(Contract c, NetworkActor a, ItemFacts f)
        {
            Contract child = NewContract(c.parties.issuer, c.parties.broker, f, c.Quantity, ProcurementMode.Direct, a.id, c.request.premiumContribution, c.request.wantsInsurance, c.Deliver?.preferredMapId ?? -1, c.Deliver?.preferredMapLabel);
            Link(c, child, ContractLineage.Replacement);
            child.lineage.inheritedFrom = c.id;
            ctx.contracts.Add(child);
            Offer old = ctx.contracts.Get(c.acceptedOffer);
            Offer o = new Offer
            {
                id = new OfferId(ctx.ids.NextId()),
                contract = child.id,
                bidder = a.id,
                bidderName = a.name.Display,
                createdTick = ctx.Now,
                stateTick = ctx.Now,
                round = 1,
                contractorQuote = old?.contractorQuote ?? 0,
                etaTicks = old?.etaTicks ?? Ticks.PerDay * 5,
                quote = CopyQuote(old?.quote),
                state = OfferState.Proposed
            };
            o.basis.newcomer = false;
            o.basis.danger = Resolver.Danger(Resolver.Edge(ctx.Operations.Estimate(a, child, f)));
            o.conditions.Add("ReplacementHonoursQuote");
            o.expiresTick = ctx.Now;
            ctx.contracts.Add(o);
            child.offers.Add(o.id);
            child.status = ContractStatus.Bidding;
            child.biddingRound = 1;
            child.windowOpenTick = child.windowCloseTick = ctx.Now;
            int carried = c.PaidFor(NoteDeposit) + c.PaidFor(NoteTransferred);
            int premium = c.PaidFor(NotePremium);
            c.ledger.Add(Money(carried + premium, MoneyDirection.Transferred, "transferredOut"));
            child.ledger.Add(Money(carried, MoneyDirection.Transferred, NoteTransferred));
            if (premium > 0) child.ledger.Add(Money(premium, MoneyDirection.Transferred, NotePremium));
            Insurance insurance = c.terms?.insurance?.Copy();
            Award(child, o, a, f, insurance, true);
            return child;
        }

        private static ProcurementQuote CopyQuote(ProcurementQuote q)
        {
            if (q == null) return new ProcurementQuote();
            ProcurementQuote n = new ProcurementQuote
            {
                broker = q.broker,
                brokerName = q.brokerName,
                goodsBasis = q.goodsBasis,
                marketFloor = q.marketFloor,
                finalPrice = q.finalPrice,
                depositShare = q.depositShare,
                deposit = q.deposit,
                balance = q.balance,
                etaTicks = q.etaTicks,
                depositPolicyKey = q.depositPolicyKey,
                insuranceOffer = q.insuranceOffer?.Copy(),
                replacementPolicyKey = q.replacementPolicyKey,
                refundPolicyKey = q.refundPolicyKey,
                createdTick = q.createdTick,
                validUntilTick = q.validUntilTick
            };
            for (int i = 0; i < q.components.Count; i++)
            {
                QuoteComponent k = q.components[i];
                n.components.Add(new QuoteComponent { kind = k.kind, amount = k.amount, contributedBy = k.contributedBy, reasonKeys = new List<string>(k.reasonKeys) });
            }
            return n;
        }

        private void Link(Contract parent, Contract child, string relation)
        {
            child.lineage.parent = parent.id;
            child.lineage.root = parent.lineage.root.IsValid ? parent.lineage.root : parent.id;
            child.lineage.spawnedBy = parent.id.Ref;
            child.lineage.relationKey = relation;
            child.lineage.depth = parent.lineage.depth + 1;
            parent.lineage.children.Add(child.id);
        }

        // ================================================================== during the work

        /// <summary>Engagement showed the job is much worse than quoted: the contractor asks for new terms.</summary>
        public void OnWorseThanExpected(Contract c, Operation op, float danger)
        {
            NetRng rng = new NetRng(c.seed, "renegotiate", c.rerollNonce);
            int price = c.terms?.price ?? 0;
            c.renegotiation = new RenegotiationAsk
            {
                reasonKey = SubStatus.WorseThanExpected,
                extraSilver = Math.Max(1, (int)Math.Round(price * rng.Range(0.2f, 0.4f))),
                reducedCount = Math.Max(1, (int)Math.Floor(c.Quantity * rng.Range(0.4f, 0.7f))),
                tick = ctx.Now
            };
            if (c.renegotiation.reducedCount >= c.Quantity) c.renegotiation.reducedCount = Math.Max(1, c.Quantity - 1);
            EnterDecision(c, SubStatus.WorseThanExpected, Rules(c).worseThanExpectedGraceDays);
            ContractEvent e = NewEvent(EventKeys.ContractRenegotiationRequested, Importance.Minor, c);
            e.causeKey = SubStatus.WorseThanExpected;
            e.silver = c.renegotiation.extraSilver;
            e.delivered = c.renegotiation.reducedCount;
            ctx.bus.Publish(e);
        }

        private void EnterDecision(Contract c, string subStatus, float graceDays)
        {
            c.status = ContractStatus.Renegotiating;
            c.subStatus = subStatus;
            c.causeKey = subStatus;
            c.decisionDueTick = ctx.Now + (int)(graceDays * Ticks.PerDay);
            ctx.scheduler.Schedule(DecisionJob, c.decisionDueTick, c.id.Value);
            StateVersion.Bump();
        }

        public CommandResult CanRespondWorse(ContractId id, WorseChoice choice)
        {
            Contract c = ctx.contracts.Get(id);
            if (c == null) return CommandResult.Fail("ContractMissing");
            if (c.status != ContractStatus.Renegotiating || c.subStatus != SubStatus.WorseThanExpected || c.renegotiation == null) return CommandResult.Fail("NoRenegotiation");
            string reason;
            if (!CommsOk(out reason)) return CommandResult.Fail(reason);
            if (choice == WorseChoice.PayMore && !ctx.payment.CanCharge(c.renegotiation.extraSilver, out reason)) return CommandResult.Fail(reason ?? "CannotAfford");
            return CommandResult.Ok;
        }

        /// <summary>The client answers a "worse than expected" report: pay more, accept a reduced scope, or refuse.</summary>
        public CommandResult RespondWorse(ContractId id, WorseChoice choice)
        {
            CommandResult can = CanRespondWorse(id, choice);
            if (!can.ok) return can;
            ApplyWorse(ctx.contracts.Get(id), choice);
            return CommandResult.Ok;
        }

        private void ApplyWorse(Contract c, WorseChoice choice)
        {
            Operation op = CurrentOperation(c);
            RenegotiationAsk ask = c.renegotiation;
            string reason;
            switch (choice)
            {
                case WorseChoice.PayMore:
                    if (!ctx.payment.TryCharge(ask.extraSilver, out reason))
                    {
                        ApplyWorse(c, WorseChoice.AcceptReduced);
                        return;
                    }
                    c.ledger.Add(Money(ask.extraSilver, MoneyDirection.PlayerPaid, NoteRenegotiation));
                    c.terms.price += ask.extraSilver;
                    PayContractor(c, ctx.actors.Get(c.parties.contractor), ask.extraSilver);
                    break;
                case WorseChoice.AcceptReduced:
                    int count = c.Quantity;
                    c.Acquire.count = ask.reducedCount;
                    c.terms.price = Math.Max(c.terms.deposit, (int)Math.Round(c.terms.price * ask.reducedCount / (float)Math.Max(1, count)));
                    c.terms.balance = Math.Max(0, c.terms.price - c.terms.deposit);
                    break;
                case WorseChoice.Refuse:
                    NetworkActor a = ctx.actors.Get(c.parties.contractor);
                    Doctrine d = a?.Get<ContractorSimulation>()?.doctrine;
                    if (d != null && d.professionalism + d.loyalty >= 1.1f)
                    {
                        ContractorSimulation sim = a.Get<ContractorSimulation>();
                        MoraleModel.Shock(sim, 0.05f, false, ctx.Now);
                        ctx.Contractors.MoraleShiftCheck(a, sim);
                        break; // they go ahead on the original terms, grudgingly
                    }
                    if (op != null) ctx.Operations.Abort(op, Causes.ContractorWalked);
                    int refund = (int)Math.Round(c.PaidFor(NoteDeposit) * 0.5f) + c.PaidFor(NotePremium);
                    if (refund > 0) Refund(c, refund, "refund.walked");
                    Close(c, ContractStatus.Failed, Causes.ContractorWalked, EventKeys.ContractFailed, Importance.Notable, refund);
                    return;
            }
            Resume(c, op);
        }

        private void Resume(Contract c, Operation op)
        {
            c.status = ContractStatus.Active;
            c.subStatus = null;
            c.causeKey = null;
            c.decisionDueTick = -1;
            ctx.scheduler.Cancel(DecisionJob, c.id.Value);
            if (op != null && !op.IsFinished) ctx.Operations.Resume(op, c);
            StateVersion.Bump();
        }

        public void OnDelayed(Contract c, Operation op)
        {
            if (c.IsTerminal) return;
            c.status = ContractStatus.Delayed;
            c.causeKey = "Delayed";
            ContractEvent e = NewEvent(EventKeys.ContractDelayed, Importance.Minor, c);
            e.causeKey = "Delayed";
            e.bandKey = op.outcome?.band.ToString();
            ctx.bus.Publish(e);
        }

        /// <summary>Missing, captured or stranded: Troubled is NOT terminal; the deadline resolves it.</summary>
        public void OnTroubled(Contract c, Operation op)
        {
            if (c.IsTerminal) return;
            string key = op.outcome.troubledKey;
            c.status = ContractStatus.Troubled;
            c.subStatus = key;
            c.causeKey = key;
            c.decisionDueTick = op.troubledDeadlineTick;
            NetworkActor a = ctx.actors.Get(op.contractor);
            string eventKey = key == SubStatus.Captured ? EventKeys.ContractorCaptured : (key == SubStatus.Stranded ? EventKeys.ContractorStranded : EventKeys.ContractorMissing);
            ContractorEvent e = EventFactory.Make<ContractorEvent>(eventKey, Importance.Major, op.contractor.Ref, c.id.Ref, op.id.Ref);
            e.actor = op.contractor;
            e.actorName = op.contractorName;
            e.contract = c.id;
            e.operation = op.id;
            e.captured = op.outcome.Captured;
            e.missing = op.outcome.Missing;
            e.reasonKey = key;
            e.descriptorKey = op.outcome.secured > 0 ? "CargoSecured" : "NoCargo";
            ctx.bus.Publish(e);
            StateVersion.Bump();
        }

        public void OnRecovered(Contract c, Operation op)
        {
            c.status = ContractStatus.Active;
            c.subStatus = null;
            c.causeKey = null;
            c.decisionDueTick = -1;
            OnOperationReturned(c, op);
        }

        public void OnWrittenOff(Contract c, Operation op)
        {
            Fail(c, Causes.ContractorLost, Importance.Major);
        }

        // ================================================================== after the work

        /// <summary>The group is home: deliver everything, ask the client about a partial result, or fail.</summary>
        public void OnOperationReturned(Contract c, Operation op)
        {
            if (c.IsTerminal) return;
            if (c.status == ContractStatus.Delayed)
            {
                c.status = ContractStatus.Active;
                c.causeKey = null;
            }
            int secured = op.outcome?.secured ?? 0;
            c.Acquire.secured = secured;
            if (secured >= c.Quantity)
            {
                BeginDelivery(c, secured, c.terms.balance, false, null);
            }
            else if (secured > 0)
            {
                EnterDecision(c, SubStatus.PartialResult, Rules(c).partialResultGraceDays);
                ContractEvent e = NewEvent(EventKeys.ContractRenegotiationRequested, Importance.Minor, c);
                e.causeKey = SubStatus.PartialResult;
                e.delivered = secured;
                e.bandKey = op.outcome.band.ToString();
                ctx.bus.Publish(e);
            }
            else
            {
                ctx.Operations.Finish(op);
                Fail(c, op.outcome != null && op.outcome.band == OutcomeBand.Disaster ? Causes.CatastrophicLoss : Causes.OperationFailed,
                    op.outcome != null && op.outcome.band == OutcomeBand.Disaster ? Importance.Major : Importance.Notable);
            }
        }

        public CommandResult CanRespondPartial(ContractId id, PartialChoice choice)
        {
            Contract c = ctx.contracts.Get(id);
            if (c == null) return CommandResult.Fail("ContractMissing");
            if (c.status != ContractStatus.Renegotiating || c.subStatus != SubStatus.PartialResult) return CommandResult.Fail("NoPartialResult");
            string reason;
            if (!CommsOk(out reason)) return CommandResult.Fail(reason);
            if ((choice == PartialChoice.AcceptAndContinue || choice == PartialChoice.AskSameContractor) && !IsBroker(ctx.actors.Get(c.parties.broker))) return CommandResult.Fail("NoBroker");
            return CommandResult.Ok;
        }

        /// <summary>
        /// The client decides about a partial result (STATE_MACHINES § 3): accept it at a pro-rated price,
        /// accept it and post a NEW linked contract for the rest (open, or to the same contractor), or
        /// refuse it (the goods stay with the contractor; the deposit is not returned).
        /// </summary>
        public CommandResult RespondPartial(ContractId id, PartialChoice choice)
        {
            CommandResult can = CanRespondPartial(id, choice);
            if (!can.ok) return can;
            ApplyPartial(ctx.contracts.Get(id), choice);
            return CommandResult.Ok;
        }

        private void ApplyPartial(Contract c, PartialChoice choice)
        {
            Operation op = CurrentOperation(c);
            int secured = c.Acquire.secured;
            ctx.scheduler.Cancel(DecisionJob, c.id.Value);
            if (choice == PartialChoice.Refuse)
            {
                if (op != null) ctx.Operations.Finish(op);
                Close(c, ContractStatus.Cancelled, "IssuerRefusedPartial", EventKeys.ContractCancelled, Importance.Minor);
                return;
            }
            int remaining = c.Quantity - secured;
            float delivered = secured / (float)Math.Max(1, c.Quantity);
            int prorated = (int)Math.Round(c.terms.price * delivered);
            int depositPaid = c.PaidFor(NoteDeposit) + c.PaidFor(NoteTransferred);
            int balanceDue = Math.Max(0, prorated - depositPaid);
            int payout = 0;
            if (c.terms.insurance != null && c.terms.insurance.Covers(Causes.PartialShortfall))
            {
                payout = (int)Math.Round(depositPaid * (1f - delivered) * c.terms.insurance.coverage);
                if (payout > 0) Refund(c, payout, "insurance.payout");
            }
            c.status = ContractStatus.Active;
            c.subStatus = null;
            c.decisionDueTick = -1;
            if (choice != PartialChoice.AcceptPartial && remaining > 0)
            {
                ItemFacts f = ctx.catalog.Facts(c.Acquire.DefName);
                if (f != null) Continue(c, f, remaining, choice == PartialChoice.AskSameContractor);
            }
            BeginDelivery(c, secured, balanceDue, true, Causes.PartialAccepted);
        }

        /// <summary>A continuation is a NEW contract for the remainder, linked by lineage; the parent is never reopened.</summary>
        private Contract Continue(Contract parent, ItemFacts f, int remaining, bool sameContractor)
        {
            ProcurementMode mode = sameContractor ? ProcurementMode.Direct : ProcurementMode.Open;
            Contract child = NewContract(parent.parties.issuer, parent.parties.broker, f, remaining, mode, sameContractor ? parent.parties.contractor : ActorId.None, 0, parent.request.wantsInsurance, parent.Deliver?.preferredMapId ?? -1, parent.Deliver?.preferredMapLabel);
            Link(parent, child, ContractLineage.Continuation);
            ctx.contracts.Add(child);
            ctx.bus.Publish(NewEvent(EventKeys.ContractPosted, Importance.Minor, child));
            OpenWindow(child, f);
            return child;
        }

        /// <summary>contract.decision: the client did not answer in time; the kind's default applies.</summary>
        public void DecisionJobRun(ScheduledJob job)
        {
            Contract c = ctx.contracts.Get(new ContractId(job.target));
            if (c == null || c.quarantinedReason != null || c.IsTerminal || c.subStatus == null) return;
            if (c.decisionDueTick > ctx.Now)
            {
                ctx.scheduler.Schedule(DecisionJob, c.decisionDueTick, c.id.Value);
                return;
            }
            switch (c.subStatus)
            {
                case SubStatus.WorseThanExpected:
                    if (c.status == ContractStatus.Renegotiating) ApplyWorse(c, WorseChoice.AcceptReduced);
                    break;
                case SubStatus.PartialResult:
                    if (c.status == ContractStatus.Renegotiating) ApplyPartial(c, PartialChoice.AcceptPartial);
                    break;
                case SubStatus.AwaitingPayment:
                    PartialHandover(c);
                    break;
            }
        }

        // ================================================================== delivery and payment

        private void BeginDelivery(Contract c, int count, int balanceDue, bool partial, string partialCause)
        {
            DeliverObjective d = c.Deliver;
            d.pendingCount = count;
            d.balanceDue = balanceDue;
            d.balancePaid = balanceDue <= 0;
            d.partial = partial;
            d.partialCauseKey = partialCause;
            d.attempts = 0;
            TryDeliverNow(c);
        }

        /// <summary>contract.delivery: a retry, or a Hold that may have ended.</summary>
        public void DeliveryJobRun(ScheduledJob job)
        {
            Contract c = ctx.contracts.Get(new ContractId(job.target));
            if (c == null || c.quarantinedReason != null || c.IsTerminal || c.Deliver == null || !c.Deliver.InProgress) return;
            if (c.subStatus == SubStatus.AwaitingPayment) return;
            TryDeliverNow(c);
        }

        /// <summary>
        /// Plan (no side effect) → charge the balance → drop pods. Money only moves when the goods can
        /// actually land. No drop spot: retry daily, then Hold with a letter. No home map at all: Hold at
        /// once. A Hold that outlasts the kind's limit fails the contract; nothing stays stuck forever.
        /// </summary>
        private void TryDeliverNow(Contract c)
        {
            DeliverObjective d = c.Deliver;
            ContractKindRules rules = Rules(c);
            Operation op = CurrentOperation(c);
            int seed = NetHash.Combine(c.seed, "delivery." + d.attempts);
            DeliveryPlan plan = ctx.delivery == null ? new DeliveryPlan { failureKey = "NoDeliveryAdapter" } : ctx.delivery.Plan(d.preferredMapId, seed);
            if (!plan.ok)
            {
                DeliveryFailed(c, plan.failureKey, rules);
                return;
            }
            if (!d.balancePaid)
            {
                string reason;
                if (!ctx.payment.TryCharge(d.balanceDue, out reason))
                {
                    PaymentDefault(c);
                    return;
                }
                d.balancePaid = true;
                c.ledger.Add(Money(d.balanceDue, MoneyDirection.PlayerPaid, NoteBalance));
                PayContractor(c, ctx.actors.Get(c.parties.contractor), d.balanceDue);
                ContractEvent paid = NewEvent(EventKeys.PaymentReceived, Importance.Minor, c);
                paid.silver = d.balanceDue;
                paid.causeKey = "Balance";
                ctx.bus.Publish(paid);
            }
            List<ItemPayload> payload = Payload(op, d.pendingCount);
            DeliveryResult r = payload.Count == 0 ? new DeliveryResult { failureKey = "NoPayload" } : ctx.delivery.Deliver(plan, payload, seed);
            if (!r.ok)
            {
                if (r.thingCreationFailed)
                {
                    ctx.catalog.MarkUnusable(r.failedDefName ?? c.Acquire.DefName, "delivery");
                    Void(c, Causes.ItemCannotBeProduced);
                    return;
                }
                DeliveryFailed(c, r.failureKey, rules);
                return;
            }
            d.deliveredTick = ctx.Now;
            d.deliveredMapId = r.mapId;
            d.deliveredMapLabel = r.mapLabel;
            c.Acquire.delivered += r.delivered;
            d.holdSinceTick = -1;
            c.subStatus = null;
            if (op != null) ctx.Operations.Finish(op);
            NetLog.Info(LogCategory.Delivery, "Delivered " + r.delivered + "x " + c.ItemLabel + " for " + c.id + " to " + r.mapLabel + (plan.rerouted ? " (rerouted)" : "") + ".");
            if (d.partial) Close(c, ContractStatus.PartiallyFulfilled, d.partialCauseKey ?? Causes.PartialAccepted, EventKeys.ContractPartiallyCompleted, Importance.Notable, d.balanceDue);
            else Close(c, ContractStatus.Fulfilled, "Delivered", EventKeys.ContractCompleted, Importance.Notable, d.balanceDue);
        }

        private void DeliveryFailed(Contract c, string failureKey, ContractKindRules rules)
        {
            DeliverObjective d = c.Deliver;
            d.attempts++;
            d.lastFailureKey = failureKey;
            bool hold = failureKey == "NoHomeMap" || d.attempts >= rules.deliveryRetries;
            if (hold && c.subStatus != SubStatus.Hold)
            {
                c.subStatus = SubStatus.Hold;
                d.holdSinceTick = ctx.Now;
                ContractEvent e = NewEvent(EventKeys.ContractDelayed, Importance.Minor, c);
                e.causeKey = "DeliveryHold";
                e.reasonKeys.Add(failureKey ?? "Unknown");
                ctx.bus.Publish(e);
            }
            if (hold && ctx.Now - d.holdSinceTick >= (int)(rules.deliveryHoldMaxDays * Ticks.PerDay))
            {
                int refund = d.balancePaid && d.balanceDue > 0 ? d.balanceDue : 0;
                if (refund > 0) Refund(c, refund, "refund.undeliverable");
                Operation op = CurrentOperation(c);
                if (op != null) ctx.Operations.Finish(op);
                Close(c, ContractStatus.Failed, Causes.UndeliverableNoHome, EventKeys.ContractFailed, Importance.Notable, refund);
                return;
            }
            ctx.scheduler.Schedule(DeliveryJob, ctx.Now + Ticks.PerDay, c.id.Value);
            StateVersion.Bump();
        }

        /// <summary>The exact committed goods (never rerolled), limited to what is handed over.</summary>
        private static List<ItemPayload> Payload(Operation op, int count)
        {
            List<ItemPayload> list = new List<ItemPayload>();
            if (op?.outcome == null) return list;
            int left = count;
            for (int i = 0; i < op.outcome.securedPayload.Count && left > 0; i++)
            {
                ItemPayload p = op.outcome.securedPayload[i];
                int n = Math.Min(left, p.count);
                if (n <= 0) continue;
                list.Add(new ItemPayload { thing = p.thing?.Copy(), stuff = p.stuff?.Copy(), count = n, qualityBand = p.qualityBand, role = p.role });
                left -= n;
            }
            return list;
        }

        /// <summary>
        /// The client cannot pay the balance (STATE_MACHINES § 4.3, Phase-2-safe subset): the contractor
        /// either holds the goods (AwaitingPayment, with a grace timer) or hands over what the deposit
        /// covered. The debt option needs Obligations (Phase 5) and is deferred.
        /// </summary>
        private void PaymentDefault(Contract c)
        {
            ContractEvent e = NewEvent(EventKeys.PaymentDefaulted, Importance.Notable, c);
            e.silver = c.Deliver.balanceDue;
            e.causeKey = "Balance";
            ctx.bus.Publish(e);
            NetworkActor a = ctx.actors.Get(c.parties.contractor);
            Doctrine d = a?.Get<ContractorSimulation>()?.doctrine;
            float trust = ctx.Relations.Get(c.parties.contractor, c.parties.issuer).trust;
            float score = d == null ? 0f : d.professionalism + d.loyalty - d.greed + (trust - 0.5f);
            if (score >= 0.6f)
            {
                PartialHandover(c);
                return;
            }
            c.subStatus = SubStatus.AwaitingPayment;
            c.causeKey = SubStatus.AwaitingPayment;
            c.decisionDueTick = ctx.Now + (int)(Rules(c).awaitingPaymentGraceDays * Ticks.PerDay);
            ctx.scheduler.Schedule(DecisionJob, c.decisionDueTick, c.id.Value);
            ContractEvent hold = NewEvent(EventKeys.ContractDelayed, Importance.Minor, c);
            hold.causeKey = SubStatus.AwaitingPayment;
            hold.silver = c.Deliver.balanceDue;
            ctx.bus.Publish(hold);
            StateVersion.Bump();
        }

        /// <summary>Deliver the goods the payments so far cover; the rest stay with the contractor.</summary>
        private void PartialHandover(Contract c)
        {
            DeliverObjective d = c.Deliver;
            int paid = c.PaidFor(NoteDeposit) + c.PaidFor(NoteTransferred);
            int agreed = paid + d.balanceDue;
            int covered = agreed <= 0 ? d.pendingCount : (int)Math.Floor(d.pendingCount * paid / (float)agreed);
            ctx.scheduler.Cancel(DecisionJob, c.id.Value);
            c.subStatus = null;
            c.decisionDueTick = -1;
            if (covered <= 0)
            {
                Operation op = CurrentOperation(c);
                if (op != null) ctx.Operations.Finish(op);
                Close(c, ContractStatus.Failed, "PlayerDefaulted", EventKeys.ContractFailed, Importance.Notable);
                return;
            }
            d.pendingCount = covered;
            d.balanceDue = 0;
            d.balancePaid = true;
            d.partial = true;
            d.partialCauseKey = Causes.PartialHandover;
            TryDeliverNow(c);
        }

        public CommandResult CanPayBalance(ContractId id)
        {
            Contract c = ctx.contracts.Get(id);
            if (c == null) return CommandResult.Fail("ContractMissing");
            if (c.subStatus != SubStatus.AwaitingPayment || c.IsTerminal) return CommandResult.Fail("NothingOwed");
            string reason;
            if (!CommsOk(out reason)) return CommandResult.Fail(reason);
            if (!ctx.payment.CanCharge(c.Deliver.balanceDue, out reason)) return CommandResult.Fail(reason ?? "CannotAfford");
            return CommandResult.Ok;
        }

        /// <summary>The client settles a held balance: the goods are released.</summary>
        public CommandResult PayBalance(ContractId id)
        {
            CommandResult can = CanPayBalance(id);
            if (!can.ok) return can;
            Contract c = ctx.contracts.Get(id);
            ctx.scheduler.Cancel(DecisionJob, c.id.Value);
            c.subStatus = null;
            c.causeKey = null;
            c.decisionDueTick = -1;
            TryDeliverNow(c);
            return CommandResult.Ok;
        }

        // ================================================================== failure

        /// <summary>An ordinary in-world failure: the deposit is lost; insurance recovers part of it only for covered causes.</summary>
        private void Fail(Contract c, string causeKey, Importance importance)
        {
            if (c.IsTerminal) return;
            int payout = 0;
            Insurance ins = c.terms?.insurance;
            if (ins != null && ins.Covers(causeKey))
            {
                int deposit = c.PaidFor(NoteDeposit) + c.PaidFor(NoteTransferred);
                payout = (int)Math.Round(deposit * ins.coverage);
                if (payout > 0) Refund(c, payout, "insurance.payout");
            }
            Close(c, ContractStatus.Failed, causeKey, EventKeys.ContractFailed, importance, 0, payout);
        }
    }
}
