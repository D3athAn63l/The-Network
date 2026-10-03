using System;
using System.Collections.Generic;
using TheNetwork.Domain.Operations;
using TheNetwork.Kernel;

namespace TheNetwork.Domain.Contracts
{
    /// <summary>
    /// Load-time checks for contracts and operations (SAVE_AND_MIGRATION § 4, DEBUGGING § 4): technical
    /// invalidation of contracts whose item or kind no longer exists (voided with a full refund), and the
    /// scheduler/entity agreement — every live state has the job that moves it on. Repairs are safe:
    /// jobs are state-guarded, so a recreated job can only do what the state allows.
    /// </summary>
    public sealed partial class ProcurementService
    {
        /// <summary>Voids live contracts that can no longer be executed. <paramref name="defExists"/> checks the live DefDatabase.</summary>
        public int InvalidateMissing(Func<string, bool> defExists, List<string> findings)
        {
            int n = 0;
            List<Contract> live = Live();
            for (int i = 0; i < live.Count; i++)
            {
                Contract c = live[i];
                if (c.quarantinedReason != null) continue;
                string def = c.Acquire?.DefName;
                string cause = null;
                if (!ContractKindRegistry.Knows(c.kindKey)) cause = Causes.KindMissing;
                else if (def == null || !defExists(def)) cause = Causes.DefMissing;
                if (cause == null) continue;
                Void(c, cause);
                findings?.Add("Contract " + c.id + " (" + c.Quantity + "x " + c.ItemLabel + "): " + cause + "; voided with a full refund.");
                n++;
            }
            return n;
        }

        /// <summary>Re-creates any job a live contract or operation needs. Returns the number repaired.</summary>
        public int EnsureJobs(List<string> findings)
        {
            int repaired = 0;
            int now = ctx.Now;
            NetScheduler sch = ctx.scheduler;
            List<Contract> all = ctx.contracts.contracts;
            for (int i = 0; i < all.Count; i++)
            {
                Contract c = all[i];
                if (c.quarantinedReason != null) continue;
                if (c.HasPendingRefund() && !sch.Has(RefundJob, c.id.Value))
                {
                    sch.Schedule(RefundJob, now + 1, c.id.Value);
                    repaired += Found(findings, c, "pending refund had no retry job");
                }
                if (c.IsTerminal) continue;
                switch (c.status)
                {
                    case ContractStatus.Posted:
                        if (!sch.Has(BiddingJob, c.id.Value))
                        {
                            Ports.ItemFacts f = c.Acquire == null ? null : ctx.catalog.Facts(c.Acquire.DefName);
                            if (f != null) OpenWindow(c, f);
                            repaired += Found(findings, c, "posted without a bidding window; window opened");
                        }
                        break;
                    case ContractStatus.Bidding:
                        if (now < c.windowCloseTick || c.windowCloseTick < 0)
                        {
                            if (!sch.Has(BiddingJob, c.id.Value))
                            {
                                sch.Schedule(BiddingJob, Math.Max(now + 1, c.windowCloseTick), c.id.Value, 3);
                                repaired += Found(findings, c, "bidding job missing; window close recreated");
                            }
                        }
                        else if (OpenOffers(c).Count > 0)
                        {
                            if (!sch.Has(OffersJob, c.id.Value))
                            {
                                ScheduleOfferExpiry(c);
                                repaired += Found(findings, c, "offer expiry job missing; recreated");
                            }
                        }
                        else
                        {
                            BecomeUnfilled(c);
                            repaired += Found(findings, c, "window closed without offers; now Unfilled");
                        }
                        break;
                    case ContractStatus.Unfilled:
                        if (!sch.Has(ExpireJob, c.id.Value))
                        {
                            sch.Schedule(ExpireJob, Math.Max(now + 1, c.deadlineTick), c.id.Value);
                            repaired += Found(findings, c, "expiry job missing; recreated");
                        }
                        break;
                    default:
                        repaired += EnsureUnderway(c, findings);
                        break;
                }
            }
            return repaired;
        }

        private int EnsureUnderway(Contract c, List<string> findings)
        {
            int now = ctx.Now;
            NetScheduler sch = ctx.scheduler;
            if (c.decisionDueTick > 0 && c.subStatus != null && (c.status == ContractStatus.Renegotiating || c.subStatus == SubStatus.AwaitingPayment) && !sch.Has(DecisionJob, c.id.Value))
            {
                sch.Schedule(DecisionJob, Math.Max(now + 1, c.decisionDueTick), c.id.Value);
                return Found(findings, c, "decision grace job missing; recreated");
            }
            if (c.Deliver != null && c.Deliver.InProgress && c.subStatus != SubStatus.AwaitingPayment && !sch.Has(DeliveryJob, c.id.Value))
            {
                sch.Schedule(DeliveryJob, now + 1, c.id.Value);
                return Found(findings, c, "delivery job missing; retry scheduled");
            }
            Operation op = CurrentOperation(c);
            if (op == null)
            {
                if (c.IsUnderway && c.status != ContractStatus.Renegotiating && (c.Deliver == null || !c.Deliver.InProgress))
                {
                    Void(c, "OperationMissing");
                    return Found(findings, c, "no operation; voided with a full refund");
                }
                return 0;
            }
            if (op.IsFinished || op.quarantinedReason != null) return 0;
            // A Physical operation belongs to its episode (PHYSICAL_LIFECYCLE § 15.4): its jobs are suspended on purpose, and the
            // episode checks report on it. Never "repaired" back onto the abstract path here.
            if (op.status == OpStatus.Physical) return 0;
            if (op.status == OpStatus.Troubled)
            {
                if (!sch.Has(OperationService.TroubledJob, op.id.Value))
                {
                    sch.Schedule(OperationService.TroubledJob, Math.Max(now + 1, op.troubledDeadlineTick), op.id.Value);
                    return Found(findings, c, "troubled deadline job missing; recreated");
                }
                return 0;
            }
            if (c.status != ContractStatus.Renegotiating && ctx.Operations.NextCheckpoint(op) != null && !sch.Has(OperationService.CheckpointJob, op.id.Value))
            {
                ctx.Operations.ScheduleNext(op);
                return Found(findings, c, "operation checkpoint job missing; recreated");
            }
            return 0;
        }

        private static int Found(List<string> findings, Contract c, string what)
        {
            findings?.Add("Contract " + c.id + ": " + what + ".");
            return 1;
        }

        /// <summary>Is a job's target still a live entity (orphan detection)?</summary>
        public bool JobTargetExists(ScheduledJob j)
        {
            if (j.kind.StartsWith("operation.", StringComparison.Ordinal)) return ctx.operations.Get(new OperationId(j.target)) != null;
            return ctx.contracts.Get(new ContractId(j.target)) != null;
        }
    }
}
