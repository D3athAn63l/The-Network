using System.Collections.Generic;
using RimWorld;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Domain.Intel
{
    // The player's interest (IntelRequest) and the reported perception (Lead), DATA_MODEL § 8.
    //
    // INTEL IS TOPIC-ONLY. No type in this file has a quantity, amount, target count, minimum or stack
    // field, and no policy or command reads one. How much exists is decided by the opportunity
    // generator. A headless test reflects over these types to keep it that way.

    public enum IntelState : byte
    {
        Submitted = 0,
        Searching = 1,
        AwaitingDecision = 2,
        Concluded = 3,
        Cancelled = 4,
        Invalidated = 5,
        Closed = 6
    }

    public enum TopicKind : byte
    {
        Item = 0
    }

    public enum Confidentiality : byte
    {
        Private = 0
    }

    public enum MoneyDirection : byte
    {
        PlayerPaid = 0,
        PlayerRefunded = 1,

        /// <summary>Silver already paid, carried to a linked contract (a replacement); no silver moved.</summary>
        Transferred = 2
    }

    /// <summary>A silver movement recorded in the same step as the movement (DATA_MODEL § 16).</summary>
    public sealed class MoneyRecord : IExposable
    {
        public int tick;
        public int silver;
        public MoneyDirection direction;
        public string noteKey;

        /// <summary>The search round this payment funds (or the round being refunded).</summary>
        public int round;

        /// <summary>A refund that could not be delivered yet (no home map); retried by a job.</summary>
        public bool pending;

        public void ExposeData()
        {
            Scribe_Values.Look(ref tick, "tick", 0);
            Scribe_Values.Look(ref silver, "silver", 0);
            NetScribe.LookEnum(ref direction, "direction", MoneyDirection.PlayerPaid);
            Scribe_Values.Look(ref noteKey, "note");
            Scribe_Values.Look(ref round, "round", 0);
            Scribe_Values.Look(ref pending, "pending", false);
        }
    }

    /// <summary>WHAT the player asks about. Never how much.</summary>
    public sealed class IntelTopic : IExposable
    {
        public TopicKind kind = TopicKind.Item;
        public DefRef<ThingDef> thing;

        public void ExposeData()
        {
            NetScribe.LookEnum(ref kind, "kind", TopicKind.Item);
            Scribe_Deep.Look(ref thing, "thing");
        }

        public string DefName => thing?.defName;
        public string Label => thing == null ? "?" : thing.LabelSnapshot;
    }

    /// <summary>
    /// The source's policies, frozen at submission (STATE_MACHINES § 1). Never re-read from the
    /// source afterwards, so a later change to the source cannot alter a running search.
    /// </summary>
    public sealed class SearchTerms : IExposable
    {
        public string feePolicyKey;
        public string continuationPolicyKey;
        public Band speedBand = Band.Medium;
        public Band reliabilityBand = Band.Medium;
        public Band feeBand = Band.Medium;
        public int initialFee;
        public int continuationFee;
        public int roundsPerSegment = 3;
        public int maxRounds = 12;
        public int cancelRefundPercent = 50;
        public string sourceName;
        public string sourceKindKey;

        public void ExposeData()
        {
            Scribe_Values.Look(ref feePolicyKey, "feePolicy");
            Scribe_Values.Look(ref continuationPolicyKey, "continuationPolicy");
            NetScribe.LookEnum(ref speedBand, "speedBand", Band.Medium);
            NetScribe.LookEnum(ref reliabilityBand, "reliabilityBand", Band.Medium);
            NetScribe.LookEnum(ref feeBand, "feeBand", Band.Medium);
            Scribe_Values.Look(ref initialFee, "initialFee", 0);
            Scribe_Values.Look(ref continuationFee, "continuationFee", 0);
            Scribe_Values.Look(ref roundsPerSegment, "roundsPerSegment", 3);
            Scribe_Values.Look(ref maxRounds, "maxRounds", 12);
            Scribe_Values.Look(ref cancelRefundPercent, "cancelRefundPercent", 50);
            Scribe_Values.Look(ref sourceName, "sourceName");
            Scribe_Values.Look(ref sourceKindKey, "sourceKind");
        }
    }

    /// <summary>One search: ONE request, ZERO..MANY leads (DATA_MODEL § 8).</summary>
    public sealed class IntelRequest : IExposable
    {
        public IntelRequestId id;
        public ActorId requester;
        public ActorId source;
        public IntelTopic topic = new IntelTopic();
        public SearchTerms terms = new SearchTerms();
        public List<MoneyRecord> fees = new List<MoneyRecord>();
        public IntelState state = IntelState.Submitted;
        public int submittedTick;
        public int endedTick = -1;
        public int round;
        public int segmentStartRound = 1;
        public int roundStartedTick = -1;
        public int nextRoundDueTick = -1;
        public int closeDueTick = -1;
        public int seed;
        public int rerollNonce;

        /// <summary>World threat basis frozen when the running round started, so a reload resolves it identically.</summary>
        public float roundThreatBasis;

        public List<LeadId> leads = new List<LeadId>();
        public string endReasonKey;
        public Confidentiality confidentiality = Confidentiality.Private;
        public string quarantinedReason;

        public bool IsTerminal => state == IntelState.Concluded || state == IntelState.Cancelled || state == IntelState.Invalidated || state == IntelState.Closed;
        public bool IsActive => state == IntelState.Submitted || state == IntelState.Searching || state == IntelState.AwaitingDecision;

        public void ExposeData()
        {
            NetScribe.Look(ref id, "id");
            NetScribe.Look(ref requester, "requester");
            NetScribe.Look(ref source, "source");
            Scribe_Deep.Look(ref topic, "topic");
            Scribe_Deep.Look(ref terms, "terms");
            NetScribe.LookListTolerant(ref fees, "fees", "intel.fees");
            bool badState = false;
            NetScribe.LookEnum(ref state, "state", IntelState.Submitted, ref badState);
            Scribe_Values.Look(ref submittedTick, "submittedTick", 0);
            Scribe_Values.Look(ref endedTick, "endedTick", -1);
            Scribe_Values.Look(ref round, "round", 0);
            Scribe_Values.Look(ref segmentStartRound, "segmentStartRound", 1);
            Scribe_Values.Look(ref roundStartedTick, "roundStartedTick", -1);
            Scribe_Values.Look(ref nextRoundDueTick, "nextRoundDueTick", -1);
            Scribe_Values.Look(ref closeDueTick, "closeDueTick", -1);
            Scribe_Values.Look(ref seed, "seed", 0);
            Scribe_Values.Look(ref rerollNonce, "rerollNonce", 0);
            Scribe_Values.Look(ref roundThreatBasis, "roundThreatBasis", 0f);
            NetScribe.LookIntList(ref leads, "leads", l => l.Value, v => new LeadId(v));
            Scribe_Values.Look(ref endReasonKey, "endReason");
            NetScribe.LookEnum(ref confidentiality, "confidentiality", Confidentiality.Private);
            Scribe_Values.Look(ref quarantinedReason, "quarantined");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (topic == null) topic = new IntelTopic();
                if (terms == null) terms = new SearchTerms();
                // An unreadable state is never guessed: the entity is kept but skipped (SAVE_AND_MIGRATION § 7).
                if (badState && quarantinedReason == null) quarantinedReason = "MalformedEnum:state";
            }
        }

        public int TotalPaid()
        {
            int s = 0;
            for (int i = 0; i < fees.Count; i++) if (fees[i].direction == MoneyDirection.PlayerPaid) s += fees[i].silver;
            return s;
        }

        public int TotalRefunded()
        {
            int s = 0;
            for (int i = 0; i < fees.Count; i++) if (fees[i].direction == MoneyDirection.PlayerRefunded) s += fees[i].silver;
            return s;
        }

        public override string ToString()
        {
            return id + " " + state + " '" + topic.Label + "' r" + round;
        }
    }

    /// <summary>The hidden relation between a report and the truth (master § 12). Phase 1 subset plus reserved names.</summary>
    public enum LeadDivergence : byte
    {
        Accurate = 0,
        Partial = 1,
        Outdated = 2,
        Bad = 3,
        Misinformation = 4, // Phase 5; never drawn in Phase 1
        Trap = 5,
        Jackpot = 6,
        Complication = 7,
        Contested = 8       // Phase 4; never drawn in Phase 1
    }

    public enum LeadState : byte
    {
        Active = 0,
        Pursued = 1,
        Stale = 2,
        Closed = 3
    }

    public sealed class ReportedCargo : IExposable
    {
        public DefRef<ThingDef> thing;
        public int low = -1;
        public int high = -1;

        public void ExposeData()
        {
            Scribe_Deep.Look(ref thing, "thing");
            Scribe_Values.Look(ref low, "low", -1);
            Scribe_Values.Look(ref high, "high", -1);
        }
    }

    /// <summary>
    /// What the source tells the player. Perception only; it may be wrong. The estimate of how much of
    /// the item is there is the SOURCE's estimate of the opportunity, never an input the player gave.
    /// </summary>
    public sealed class LeadReport : IExposable
    {
        public string archetypeKey;
        public int estimateLow = -1;
        public int estimateHigh = -1;
        public List<ReportedCargo> otherCargo = new List<ReportedCargo>();
        public bool unknownExtra;
        public ThreatBand threatBand = ThreatBand.Unknown;
        public TileRef location;

        /// <summary>The source's travel estimate from home, committed when the lead is made (master § 11 "Distance").</summary>
        public int travelTicks = -1;

        public string holderText;
        public string sourceKindKey;
        public int expiresAroundTick = -1;
        public ConfidenceBand confidence = ConfidenceBand.Moderate;

        public void ExposeData()
        {
            Scribe_Values.Look(ref archetypeKey, "archetype");
            Scribe_Values.Look(ref estimateLow, "estimateLow", -1);
            Scribe_Values.Look(ref estimateHigh, "estimateHigh", -1);
            NetScribe.LookListTolerant(ref otherCargo, "otherCargo", "intel.leads.cargo");
            Scribe_Values.Look(ref unknownExtra, "unknownExtra", false);
            NetScribe.LookEnum(ref threatBand, "threat", ThreatBand.Unknown);
            Scribe_Deep.Look(ref location, "location");
            Scribe_Values.Look(ref travelTicks, "travelTicks", -1);
            Scribe_Values.Look(ref holderText, "holder");
            Scribe_Values.Look(ref sourceKindKey, "sourceKind");
            Scribe_Values.Look(ref expiresAroundTick, "expiresAround", -1);
            NetScribe.LookEnum(ref confidence, "confidence", ConfidenceBand.Moderate);
        }
    }

    public sealed class Lead : IExposable
    {
        public LeadId id;
        public IntelRequestId intel;
        public OpportunityId opportunity;
        public ActorId reportedBy;
        public int reportedTick;
        public float reliability;
        public LeadDivergence divergence = LeadDivergence.Accurate;
        public int round;
        public LeadReport reported = new LeadReport();
        public LeadState state = LeadState.Active;
        public int stateTick;
        public string quarantinedReason;

        public void ExposeData()
        {
            NetScribe.Look(ref id, "id");
            NetScribe.Look(ref intel, "intel");
            NetScribe.Look(ref opportunity, "opportunity");
            NetScribe.Look(ref reportedBy, "reportedBy");
            Scribe_Values.Look(ref reportedTick, "reportedTick", 0);
            Scribe_Values.Look(ref reliability, "reliability", 0f);
            NetScribe.LookEnum(ref divergence, "divergence", LeadDivergence.Accurate);
            Scribe_Values.Look(ref round, "round", 0);
            Scribe_Deep.Look(ref reported, "reported");
            bool badState = false;
            NetScribe.LookEnum(ref state, "state", LeadState.Active, ref badState);
            Scribe_Values.Look(ref stateTick, "stateTick", 0);
            Scribe_Values.Look(ref quarantinedReason, "quarantined");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (reported == null) reported = new LeadReport();
                if (badState && quarantinedReason == null) quarantinedReason = "MalformedEnum:state";
            }
        }
    }

    public sealed class IntelStore : IExposable
    {
        public List<IntelRequest> requests = new List<IntelRequest>();
        public List<Lead> leads = new List<Lead>();

        private readonly Dictionary<int, IntelRequest> requestById = new Dictionary<int, IntelRequest>();
        private readonly Dictionary<int, Lead> leadById = new Dictionary<int, Lead>();

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref requests, "requests", "intel.requests");
            NetScribe.LookListTolerant(ref leads, "leads", "intel.leads");
        }

        public void RebuildIndex()
        {
            requestById.Clear();
            leadById.Clear();
            for (int i = 0; i < requests.Count; i++) if (requests[i] != null && requests[i].id.IsValid) requestById[requests[i].id.Value] = requests[i];
            for (int i = 0; i < leads.Count; i++) if (leads[i] != null && leads[i].id.IsValid) leadById[leads[i].id.Value] = leads[i];
        }

        public void Add(IntelRequest r)
        {
            requests.Add(r);
            requestById[r.id.Value] = r;
        }

        public void Add(Lead l)
        {
            leads.Add(l);
            leadById[l.id.Value] = l;
        }

        public IntelRequest Get(IntelRequestId id)
        {
            IntelRequest r;
            return id.IsValid && requestById.TryGetValue(id.Value, out r) ? r : null;
        }

        public Lead Get(LeadId id)
        {
            Lead l;
            return id.IsValid && leadById.TryGetValue(id.Value, out l) ? l : null;
        }

        public void Remove(IntelRequest r)
        {
            requests.Remove(r);
            requestById.Remove(r.id.Value);
        }

        public void Remove(Lead l)
        {
            leads.Remove(l);
            leadById.Remove(l.id.Value);
        }
    }
}
