using System;
using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Operations;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    /// <summary>
    /// A READ-ONLY invariant scan of a Network world (the live one for RT-LIVE-006). It deliberately does not use
    /// <c>NetValidator.Run</c>: that one repairs (it reschedules jobs, quarantines duplicates, ends vanished faction proxies, prunes
    /// history, applies missed career results), and a test must never silently repair the owner's save. This scan only reads raw
    /// fields and reports. A violation is an impossible state; a note is a state a repair would normally resolve.
    /// </summary>
    public static class LiveInvariants
    {
        public sealed class Result
        {
            public readonly List<string> Violations = new List<string>();
            public readonly List<string> Notes = new List<string>();
            public int Actors, Contracts, LedgerRecords, Operations, Jobs;

            public string Counted => Actors + " actors, " + Contracts + " contracts (" + LedgerRecords + " ledger records), " + Operations + " operations, " + Jobs + " scheduled jobs";
        }

        public static Result Scan(DomainContext ctx, IdAllocator ids, NetScheduler scheduler)
        {
            Result r = new Result();
            CheckIds(ctx, ids, r);
            CheckActors(ctx, r);
            CheckContracts(ctx, r);
            CheckOperations(ctx, r);
            CheckScheduler(ctx, scheduler, r);
            return r;
        }

        private static void CheckIds(DomainContext ctx, IdAllocator ids, Result r)
        {
            HashSet<int> seen = new HashSet<int>();
            int next = ids == null ? int.MaxValue : ids.PeekNextId;
            for (int i = 0; i < ctx.actors.actors.Count; i++) Id(ctx.actors.actors[i].id.Value, "actor", seen, next, r);
            for (int i = 0; i < ctx.characters.characters.Count; i++) Id(ctx.characters.characters[i].id.Value, "character", seen, next, r);
            for (int i = 0; i < ctx.intel.requests.Count; i++) Id(ctx.intel.requests[i].id.Value, "intel request", seen, next, r);
            for (int i = 0; i < ctx.intel.leads.Count; i++) Id(ctx.intel.leads[i].id.Value, "lead", seen, next, r);
            for (int i = 0; i < ctx.opportunities.opportunities.Count; i++) Id(ctx.opportunities.opportunities[i].id.Value, "opportunity", seen, next, r);
            for (int i = 0; i < ctx.contracts.contracts.Count; i++) Id(ctx.contracts.contracts[i].id.Value, "contract", seen, next, r);
            for (int i = 0; i < ctx.contracts.offers.Count; i++) Id(ctx.contracts.offers[i].id.Value, "offer", seen, next, r);
            for (int i = 0; i < ctx.operations.operations.Count; i++) Id(ctx.operations.operations[i].id.Value, "operation", seen, next, r);
        }

        private static void Id(int id, string kind, HashSet<int> seen, int next, Result r)
        {
            if (id <= 0) r.Violations.Add("A " + kind + " has no id.");
            else if (!seen.Add(id)) r.Violations.Add("Id " + id + " is used twice (" + kind + ").");
            else if (id >= next) r.Violations.Add("Id " + id + " (" + kind + ") is not below the allocator's next id " + next + ".");
        }

        private static void CheckActors(DomainContext ctx, Result r)
        {
            r.Actors = ctx.actors.actors.Count;
            int proxies = 0;
            for (int i = 0; i < ctx.actors.actors.Count; i++)
            {
                NetworkActor a = ctx.actors.actors[i];
                if (a.kind == ActorKind.PlayerProxy) proxies++;
                if (a.reputation == null)
                {
                    r.Violations.Add("Actor " + a.id + " has no reputation object.");
                    continue;
                }
                if (a.reputation.score < 0 || a.reputation.score > CareerPolicy.ScoreCap) r.Violations.Add("Actor " + a.id + ": reputation score " + a.reputation.score + " is outside 0.." + CareerPolicy.ScoreCap + ".");
                if (a.reputation.fame != CareerPolicy.FameFor(a.reputation.score)) r.Violations.Add("Actor " + a.id + ": fame band " + a.reputation.fame + " disagrees with score " + a.reputation.score + ".");
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                if (sim == null) continue;
                if (sim.funds < -CareerPolicy.FundsBound || sim.funds > CareerPolicy.FundsBound) r.Violations.Add("Contractor " + a.id + ": funds " + sim.funds + " are outside the bound.");
                if (sim.equipment != null && (sim.equipment.tier < CareerPolicy.MinTier || sim.equipment.tier > CareerPolicy.MaxTier)) r.Violations.Add("Contractor " + a.id + ": equipment tier " + sim.equipment.tier + " is outside " + CareerPolicy.MinTier + ".." + CareerPolicy.MaxTier + ".");
                if (sim.career == null) r.Violations.Add("Contractor " + a.id + " has no career record.");
                else if (sim.career.triumphs < 0 || sim.career.successes < 0 || sim.career.partials < 0 || sim.career.failures < 0 || sim.career.disasters < 0 || sim.career.careerEarnings < 0) r.Violations.Add("Contractor " + a.id + ": a career counter is negative.");
                if (ContractorService.IsNpcContractor(a) && a.IsActive && (sim.spatial == null || !sim.spatial.IsInitialized)) r.Notes.Add("Contractor " + a.id + " has no initialized hidden position (a reconciliation would place it).");
            }
            if (ctx.actors.actors.Count > 0 && proxies != 1) r.Violations.Add("Expected exactly one PlayerProxy, found " + proxies + ".");
        }

        private static void CheckContracts(DomainContext ctx, Result r)
        {
            r.Contracts = ctx.contracts.contracts.Count;
            for (int i = 0; i < ctx.contracts.contracts.Count; i++)
            {
                Contract c = ctx.contracts.contracts[i];
                int held = 0;
                for (int k = 0; k < c.ledger.Count; k++)
                {
                    MoneyRecord m = c.ledger[k];
                    r.LedgerRecords++;
                    if (m.silver < 0) r.Violations.Add("Contract " + c.id + ": ledger record " + k + " has negative silver.");
                    else if (m.silver == 0 && !m.fullReversal) r.Notes.Add("Contract " + c.id + ": ledger record " + k + " moves no silver.");
                    int heldBefore = held;
                    held += m.contractorSilver;
                    bool ok;
                    switch (m.direction)
                    {
                        case MoneyDirection.PlayerPaid: ok = m.contractorSilver >= 0 && m.contractorSilver <= m.silver && (m.purpose != MoneyPurpose.InsurancePremium || m.contractorSilver == 0); break;
                        case MoneyDirection.PlayerRefunded: ok = m.contractorSilver <= 0 && (m.purpose == MoneyPurpose.Refund || m.contractorSilver == 0) && (m.fullReversal ? -m.contractorSilver <= heldBefore : -m.contractorSilver <= m.silver); break;
                        default: ok = m.contractorSilver == 0; break;
                    }
                    if (!ok) r.Violations.Add("Contract " + c.id + ": ledger record " + k + " (" + m.direction + " " + m.purpose + " " + m.silver + ") carries impossible contractor attribution " + m.contractorSilver + ".");
                }
                if (held < 0 || held > c.ExternalCharged()) r.Violations.Add("Contract " + c.id + ": the contractor holds " + held + " of " + c.ExternalCharged() + " paid in.");
                if (c.ExternalRefunded() > c.TotalFunding() || c.TotalFunding() < 0) r.Violations.Add("Contract " + c.id + ": " + c.ExternalRefunded() + " was returned out of a funding position of " + c.TotalFunding() + ".");
                if (c.status == ContractStatus.Voided && held != 0) r.Violations.Add("Contract " + c.id + ": voided, yet its contractor still holds " + held + ".");
                if (c.IsTerminal && c.HasPendingRefund() && c.status != ContractStatus.Voided && c.status != ContractStatus.Cancelled && c.status != ContractStatus.Failed) r.Notes.Add("Contract " + c.id + " is closed with a refund still pending delivery.");
            }
        }

        private static void CheckOperations(DomainContext ctx, Result r)
        {
            r.Operations = ctx.operations.operations.Count;
            for (int i = 0; i < ctx.operations.operations.Count; i++)
            {
                Operation op = ctx.operations.operations[i];
                if (op.quarantinedReason != null) continue;
                if (op.careerOutcomeApplied && !op.careerEligible) r.Notes.Add("Operation " + op.id + " carries a career result although it predates careers.");
                if (op.IsFinished && op.careerEligible && op.outcome != null && !op.careerOutcomeApplied) r.Notes.Add("Operation " + op.id + " finished without its career result applied (a validation would apply it).");
                if (op.careerOutcomeApplied && op.outcome == null) r.Violations.Add("Operation " + op.id + " has a career result applied but no committed outcome.");
            }
        }

        private static void CheckScheduler(DomainContext ctx, NetScheduler scheduler, Result r)
        {
            if (scheduler == null) return;
            foreach (ScheduledJob j in scheduler.AllJobs)
            {
                r.Jobs++;
                if (!scheduler.IsKnownKind(j.kind)) { r.Notes.Add("Job " + j + " is of a kind this build does not know."); continue; }
                if (j.kind != null && (j.kind.StartsWith("contract.", StringComparison.Ordinal) || j.kind.StartsWith("operation.", StringComparison.Ordinal) || j.kind.StartsWith("consequence.", StringComparison.Ordinal)))
                {
                    if (!ctx.Procurement.JobTargetExists(j)) r.Notes.Add("Job " + j + " has no target (a validation would remove it).");
                }
            }
        }
    }
}
