using System;
using System.Collections.Generic;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using Verse;

namespace TheNetwork.Domain.Consequences
{
    /// <summary>A rule that fired and waits for its job to generate the follow-up content.</summary>
    public sealed class PendingFollowUp : IExposable
    {
        public string ruleKey;
        public ContractId contract;
        public OperationId operation;
        public ActorId contractor;
        public string contractorName;
        public long sourceEventSeq;
        public int seed;
        public int firedTick;
        public int dueTick;
        public int lostCount;
        public int depth;
        public string flavorKey;

        public void ExposeData()
        {
            Scribe_Values.Look(ref ruleKey, "rule");
            NetScribe.Look(ref contract, "contract");
            NetScribe.Look(ref operation, "operation");
            NetScribe.Look(ref contractor, "contractor");
            Scribe_Values.Look(ref contractorName, "contractorName");
            Scribe_Values.Look(ref sourceEventSeq, "eventSeq", 0L);
            Scribe_Values.Look(ref seed, "seed", 0);
            Scribe_Values.Look(ref firedTick, "fired", 0);
            Scribe_Values.Look(ref dueTick, "due", 0);
            Scribe_Values.Look(ref lostCount, "lost", 0);
            Scribe_Values.Look(ref depth, "depth", 0);
            Scribe_Values.Look(ref flavorKey, "flavor");
        }
    }

    /// <summary>The "consequences" store slot (DATA_MODEL § 3): rules that fired, waiting for content.</summary>
    public sealed class ConsequenceStore : IExposable
    {
        public List<PendingFollowUp> pending = new List<PendingFollowUp>();
        public int lastFiredTick = -1;
        public int firedTotal;

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref pending, "pending", "consequences");
            Scribe_Values.Look(ref lastFiredTick, "lastFired", -1);
            Scribe_Values.Look(ref firedTotal, "firedTotal", 0);
        }

        public PendingFollowUp Find(ContractId c)
        {
            for (int i = 0; i < pending.Count; i++) if (pending[i].contract == c) return pending[i];
            return null;
        }
    }

    /// <summary>
    /// Consequence Engine v0 (IMPLEMENTATION_PHASES § 5): ONE rule. A procurement lost catastrophically,
    /// or a contractor gone missing or stranded, may leave a LAST KNOWN LOCATION: a Phase 1 opportunity
    /// and vanilla site holding what they had secured (or nothing), believable extra cargo, and whatever
    /// stopped them. Never contractor pawns, survivors, captives or bodies (Phase 3). Firing is decided
    /// when the triggering event is dispatched (seeded by its sequence number); the content is generated
    /// by a job from its own seed. The deposit is not refunded because a follow-up exists.
    /// </summary>
    public sealed class ConsequenceEngine : IEventConsumer
    {
        public const string RuleLastKnownLocation = "LastKnownLocation";
        public const string FollowUpJob = "consequence.followup";
        public const int MaxLineageDepth = 4;
        public const int ActiveBudget = 6;
        public const int CooldownTicks = Ticks.PerDay;

        public static readonly string[] ConsumedKeys = { EventKeys.ContractFailed, EventKeys.ContractorMissing, EventKeys.ContractorStranded };

        private readonly DomainContext ctx;

        public ConsequenceEngine(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        public string Name => "Consequences";

        private ConsequenceStore Store => ctx.consequences;

        public void Handle(NetworkEvent evt)
        {
            if (Store == null) return;
            ContractEvent ce = evt as ContractEvent;
            if (ce != null)
            {
                if (ce.typeKey == EventKeys.ContractFailed && ce.causeKey == Causes.CatastrophicLoss) Consider(ce.contract, 0.6f, evt.seq);
                return;
            }
            ContractorEvent ke = evt as ContractorEvent;
            if (ke != null && ke.contract.IsValid) Consider(ke.contract, 0.75f, evt.seq);
        }

        private void Consider(ContractId id, float chance, long seq)
        {
            Contract c = ctx.contracts.Get(id);
            Operation op = c == null ? null : ctx.Procurement.CurrentOperation(c);
            if (c == null || op?.outcome == null) return;
            if (Store.Find(c.id) != null || Existing(c.id) != null) return; // one per contract
            int depth = c.lineage.depth + 1;
            if (depth > MaxLineageDepth) return;
            if (ActiveCount() + Store.pending.Count >= ActiveBudget) return;
            if (Store.lastFiredTick >= 0 && ctx.Now - Store.lastFiredTick < CooldownTicks) return;
            int seed = NetHash.Combine(NetHash.Combine(ctx.networkSeed, (int)(seq & 0x7fffffff)), "consequence.lkl");
            NetRng rng = new NetRng(seed, "fire");
            bool fire = rng.Chance(chance);
            if (ProcurementDevOverrides.forceFollowUp)
            {
                fire = true;
                ProcurementDevOverrides.forceFollowUp = false;
            }
            if (!fire) return;
            // What the site holds of the contract goods is bounded by what the outcome committed as
            // secured: part or all of it, never more, and nothing at all when nothing was secured.
            int secured = Math.Max(0, op.outcome.secured);
            int lost = secured <= 0 ? 0 : rng.RangeInclusive((secured + 1) / 2, secured);
            Store.pending.Add(new PendingFollowUp
            {
                ruleKey = RuleLastKnownLocation,
                contract = c.id,
                operation = op.id,
                contractor = op.contractor,
                contractorName = op.contractorName,
                sourceEventSeq = seq,
                seed = seed,
                firedTick = ctx.Now,
                dueTick = ctx.Now + Ticks.PerHour * 2,
                lostCount = lost,
                depth = depth,
                flavorKey = op.outcome.flavorKey
            });
            Store.lastFiredTick = ctx.Now;
            Store.firedTotal++;
            ctx.scheduler.Schedule(FollowUpJob, ctx.Now + Ticks.PerHour * 2, c.id.Value);
            StateVersion.Bump();
        }

        /// <summary>consequence.followup: generates the site from the pending rule's own seed, once.</summary>
        public void FollowUpJobRun(ScheduledJob job)
        {
            PendingFollowUp p = Store?.Find(new ContractId(job.target));
            if (p == null) return;
            Store.pending.Remove(p);
            Contract c = ctx.contracts.Get(p.contract);
            ItemFacts f = c == null ? null : ctx.catalog.Facts(c.Acquire?.DefName);
            if (f == null) return;
            // The committed secured payload (def, stuff, quality, count), read back, never rerolled.
            ItemPayload lost = null;
            Operation op = ctx.operations.Get(p.operation);
            if (op?.outcome != null && op.outcome.securedPayload.Count > 0) lost = op.outcome.securedPayload[0];
            string failure;
            Opportunity opp = ctx.Opportunities.GenerateFollowUp(f, p.lostCount, lost, p.seed, c.id.Ref, p.depth, ThreatFor(p.flavorKey), out failure);
            if (opp == null)
            {
                NetLog.Info(LogCategory.Opportunities, "Last known location for " + c.id + " not created: " + failure + ".");
                return;
            }
            OpportunityEvent e = EventFactory.Make<OpportunityEvent>(EventKeys.OpportunityFollowUpCreated, Importance.Notable, opp.id.Ref, c.id.Ref, p.contractor.Ref);
            e.opportunity = opp.id;
            e.source = p.contractor;
            e.holderActor = opp.sourceContext.holderActor;
            e.holderName = ctx.Opportunities.HolderText(opp);
            e.sourceKindKey = opp.sourceContext.kind.ToString();
            e.targetDefName = f.defName;
            e.targetLabel = f.label;
            e.targetCount = opp.TargetCount;
            e.reasonKey = p.flavorKey;
            e.place = opp.location?.Copy();
            e.subjects.Add(c.id.Ref);
            ctx.bus.Publish(e);
        }

        /// <summary>Whatever stopped them: a threat profile chosen from the committed outcome's flavor.</summary>
        private static string ThreatFor(string flavorKey)
        {
            switch (flavorKey)
            {
                case "market.robbed": return ThreatProfiles.BanditCamp;
                case "market.ambushed": return ThreatProfiles.AmbushHidden;
                case "market.seized": return ThreatProfiles.Outpost;
                default: return null;
            }
        }

        private Opportunity Existing(ContractId c)
        {
            List<Opportunity> all = ctx.opportunities.opportunities;
            for (int i = 0; i < all.Count; i++)
            {
                Opportunity o = all[i];
                if (o.origin == OpportunityOrigin.ConsequenceRule && o.originRef == c.Ref) return o;
            }
            return null;
        }

        public int ActiveCount()
        {
            int n = 0;
            List<Opportunity> all = ctx.opportunities.opportunities;
            for (int i = 0; i < all.Count; i++) if (all[i].origin == OpportunityOrigin.ConsequenceRule && !all[i].IsTerminal) n++;
            return n;
        }
    }
}
