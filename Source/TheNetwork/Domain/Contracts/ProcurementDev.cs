using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;

namespace TheNetwork.Domain.Contracts
{
    /// <summary>
    /// Dev shortcuts (DEBUGGING § 3): each runs the ordinary code path sooner, so the owner never waits
    /// in-game days to reach a state. Never reachable by a player without dev mode.
    /// </summary>
    public sealed partial class ProcurementService
    {
        /// <summary>Runs every remaining bidding pass now and closes the window.</summary>
        public bool DevRunBiddingNow(Contract c)
        {
            if (c == null || c.status != ContractStatus.Bidding) return false;
            ctx.scheduler.Cancel(BiddingJob, c.id.Value);
            c.windowCloseTick = ctx.Now;
            RunBiddingPass(new ScheduledJob { kind = BiddingJob, target = c.id.Value, arg = 3 });
            return true;
        }

        /// <summary>An offer from this contractor, as if it had said yes (willingness bypassed; the quote is built as usual).</summary>
        public Offer DevForceOffer(Contract c, NetworkActor a)
        {
            if (c == null || a == null || !ContractorService.IsNpcContractor(a)) return null;
            ItemFacts f = ctx.catalog.Facts(c.Acquire?.DefName);
            if (f == null) return null;
            if (c.status == ContractStatus.Unfilled)
            {
                ctx.scheduler.Cancel(ExpireJob, c.id.Value);
                c.status = ContractStatus.Bidding;
                c.windowCloseTick = ctx.Now;
            }
            if (c.status != ContractStatus.Bidding) return null;
            WillingnessDecision d = Willingness.Evaluate(ctx, a, c, f);
            d.accept = true;
            d.reasonKeys.Clear();
            if (!c.evaluated.Contains(a.id)) c.evaluated.Add(a.id);
            return MakeOffer(c, a, f, d);
        }

        /// <summary>Tries the pending delivery now (no waiting for the daily retry).</summary>
        public bool DevDeliverNow(Contract c)
        {
            if (c == null || c.IsTerminal || c.Deliver == null || !c.Deliver.InProgress || c.subStatus == SubStatus.AwaitingPayment) return false;
            ctx.scheduler.Cancel(DeliveryJob, c.id.Value);
            TryDeliverNow(c);
            return true;
        }

        /// <summary>Applies the grace default of a waiting decision now.</summary>
        public bool DevDecideNow(Contract c)
        {
            if (c == null || c.IsTerminal || c.subStatus == null || c.decisionDueTick < 0) return false;
            c.decisionDueTick = ctx.Now;
            DecisionJobRun(new ScheduledJob { kind = DecisionJob, target = c.id.Value });
            return true;
        }

        public string Describe(Contract c)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("[TheNetwork] " + c + " seed " + c.seed + " round " + c.biddingRound + (c.quarantinedReason != null ? " QUARANTINED " + c.quarantinedReason : ""));
            sb.AppendLine("  parties: issuer " + c.parties.issuer + ", broker " + ctx.actors.NameOf(c.parties.broker) + ", contractor " + (c.parties.contractor.IsValid ? ctx.actors.NameOf(c.parties.contractor) : "-") + ", invited " + c.parties.invited.Count);
            sb.AppendLine("  lineage: root " + c.lineage.root + " parent " + c.lineage.parent + " inheritedFrom " + c.lineage.inheritedFrom + " relation " + (c.lineage.relationKey ?? "-") + " children " + c.lineage.children.Count + " depth " + c.lineage.depth);
            sb.AppendLine("  window " + c.windowOpenTick + ".." + c.windowCloseTick + ", candidates " + c.candidates.Count + ", evaluated " + c.evaluated.Count);
            foreach (Offer o in OffersOf(c))
            {
                sb.AppendLine("  offer " + o + " round " + o.round + " eta " + o.etaTicks + " danger " + o.basis.danger.ToString("0.00") + " prep " + o.basis.preparedness.ToString("0.00") + (o.basis.newcomer ? " NEWCOMER" : ""));
                if (o.quote == null) continue;
                foreach (QuoteComponent k in o.quote.components) sb.AppendLine("    " + k.kind + " " + k.amount + " by " + ctx.actors.NameOf(k.contributedBy) + " [" + string.Join(",", k.reasonKeys.ToArray()) + "]");
                sb.AppendLine("    final " + o.quote.finalPrice + " (floor " + o.quote.marketFloor + "), deposit " + o.quote.deposit + " (" + o.quote.depositShare.ToString("0.00") + "), balance " + o.quote.balance
                    + (o.quote.insuranceOffer != null ? ", insurance " + o.quote.insuranceOffer.premium + " @" + o.quote.insuranceOffer.coverage.ToString("0.00") : "") + ", valid until " + o.quote.validUntilTick);
            }
            foreach (Refusal r in c.refusals) sb.AppendLine("  refusal " + r.actorName + " round " + r.round + ": " + string.Join(", ", r.reasonKeys.ToArray()));
            foreach (Intel.MoneyRecord m in c.ledger) sb.AppendLine("  money " + m.direction + " " + m.silver + " " + m.noteKey + (m.pending ? " PENDING" : ""));
            Operation op = CurrentOperation(c);
            if (op != null)
            {
                sb.AppendLine("  operation " + op + " seed " + op.seed + " danger " + op.danger.ToString("0.00") + " forces " + op.Commitment().Headcount);
                foreach (Checkpoint cp in op.checkpoints) sb.AppendLine("    checkpoint " + cp.key + " due " + cp.dueTick + (cp.done ? " done" : ""));
                if (op.frozenInputs != null) sb.AppendLine("    inputs: force " + op.frozenInputs.forcePower.ToString("0.0") + " threat " + op.frozenInputs.threatPower.ToString("0.0") + " prep " + op.frozenInputs.preparedness.ToString("0.00") + " edge " + Resolver.Edge(op.frozenInputs).ToString("0.00"));
                if (op.outcome != null) sb.AppendLine("    outcome: " + op.outcome.band + " roll " + op.outcome.roll.ToString("0.00") + " secured " + op.outcome.secured + "/" + op.outcome.requested + " KIA " + op.outcome.Killed + " wounded " + op.outcome.Wounded + " captured " + op.outcome.Captured + " missing " + op.outcome.Missing + " delay " + op.outcome.delayTicks + " troubled " + (op.outcome.troubledKey ?? "-") + " flavor " + op.outcome.flavorKey);
            }
            if (c.Deliver != null) sb.AppendLine("  delivery: preferred " + c.Deliver.preferredMapId + " pending " + c.Deliver.pendingCount + " balanceDue " + c.Deliver.balanceDue + " paid " + c.Deliver.balancePaid + " attempts " + c.Deliver.attempts + " hold since " + c.Deliver.holdSinceTick + " delivered " + c.Deliver.deliveredTick);
            return sb.ToString();
        }
    }
}
