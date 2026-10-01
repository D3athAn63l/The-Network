using System;
using System.Collections.Generic;
using RimWorld;
using TheNetwork.Domain.Intel;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Domain.Contracts
{
    // The generic contract (DATA_MODEL § 9, STATE_MACHINES § 3) and the Phase 2 procurement parts. A
    // contract is a composition: identity, kind, status and lineage in the core; everything else in
    // small parts. Persisted type names are frozen once shipped.

    public enum ContractStatus : byte
    {
        Draft = 0,              // UI only: never persisted as a live contract
        Posted = 1,
        Bidding = 2,
        Unfilled = 3,
        Awarded = 4,
        Active = 5,
        Delayed = 6,
        Renegotiating = 7,
        Troubled = 8,
        Fulfilled = 9,
        PartiallyFulfilled = 10,
        Failed = 11,
        Cancelled = 12,
        Expired = 13,
        Voided = 14
    }

    public enum ProcurementMode : byte
    {
        Open = 0,
        Direct = 1
    }

    public enum OfferState : byte
    {
        Proposed = 0,
        Accepted = 1,
        Declined = 2,
        Withdrawn = 3,
        Expired = 4,
        Superseded = 5
    }

    public static class ContractStates
    {
        /// <summary>Terminal states are never reopened (STATE_MACHINES § 3).</summary>
        public static bool IsTerminal(ContractStatus s) => s >= ContractStatus.Fulfilled;

        /// <summary>Looking for a contractor.</summary>
        public static bool IsSeeking(ContractStatus s) => s == ContractStatus.Posted || s == ContractStatus.Bidding || s == ContractStatus.Unfilled;

        /// <summary>A contractor holds it: awarded and not yet terminal.</summary>
        public static bool IsUnderway(ContractStatus s) => s >= ContractStatus.Awarded && s <= ContractStatus.Troubled;
    }

    /// <summary>Sub-status keys of a live contract (persisted strings).</summary>
    public static class SubStatus
    {
        public const string WorseThanExpected = "WorseThanExpected";
        public const string PartialResult = "PartialResult";
        public const string AwaitingPayment = "AwaitingPayment";
        public const string Hold = "Hold";
        public const string Missing = "Missing";
        public const string Captured = "Captured";
        public const string Stranded = "Stranded";
    }

    /// <summary>Who is involved. Issuer owns the demand; the broker mediates; the contractor does the work.</summary>
    public sealed class Parties : IExposable
    {
        public ActorId issuer;
        public ActorId principal;
        public ActorId beneficiary;
        public ActorId contractor;
        public ActorId broker;

        /// <summary>Direct contract: the known group(s) asked. Empty = Open.</summary>
        public List<ActorId> invited = new List<ActorId>();

        public void ExposeData()
        {
            NetScribe.Look(ref issuer, "issuer");
            NetScribe.Look(ref principal, "principal");
            NetScribe.Look(ref beneficiary, "beneficiary");
            NetScribe.Look(ref contractor, "contractor");
            NetScribe.Look(ref broker, "broker");
            NetScribe.LookIntList(ref invited, "invited", a => a.Value, v => new ActorId(v));
        }
    }

    /// <summary>Optional cover offered by the brokering Fixer (master § 62). Coverage is always below 1.</summary>
    public sealed class Insurance : IExposable
    {
        public string policyKey;
        public float coverage;
        public int premium;

        /// <summary>Failure classes the cover pays for (cause keys); anything else is not covered.</summary>
        public List<string> covers = new List<string>();

        public bool Covers(string causeKey)
        {
            return causeKey != null && covers.Contains(causeKey);
        }

        public Insurance Copy()
        {
            return new Insurance { policyKey = policyKey, coverage = coverage, premium = premium, covers = new List<string>(covers) };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref policyKey, "policy");
            Scribe_Values.Look(ref coverage, "coverage", 0f);
            Scribe_Values.Look(ref premium, "premium", 0);
            NetScribe.LookStringList(ref covers, "covers");
        }
    }

    public sealed class PaymentStep : IExposable
    {
        public const string OnAward = "OnAward";
        public const string OnDelivery = "OnDelivery";

        public string key;
        public int amount;

        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref amount, "amount", 0);
        }
    }

    /// <summary>The agreed terms, copied from the accepted offer's quote at award and never recomputed.</summary>
    public sealed class Terms : IExposable
    {
        public int price;
        public int deposit;
        public int balance;
        public int etaTicks;

        /// <summary>Premium (sponsored) contract: silver the issuer adds; feeds the resolver's sponsorship input.</summary>
        public int premiumContribution;

        /// <summary>Bought cover, or null when the client declined it or none was offered.</summary>
        public Insurance insurance;

        public List<PaymentStep> schedule = new List<PaymentStep>();
        public List<string> conditions = new List<string>();
        public string depositPolicyKey;
        public string refundPolicyKey;
        public string replacementPolicyKey;

        public void ExposeData()
        {
            Scribe_Values.Look(ref price, "price", 0);
            Scribe_Values.Look(ref deposit, "deposit", 0);
            Scribe_Values.Look(ref balance, "balance", 0);
            Scribe_Values.Look(ref etaTicks, "eta", 0);
            Scribe_Values.Look(ref premiumContribution, "premium", 0);
            Scribe_Deep.Look(ref insurance, "insurance");
            NetScribe.LookListTolerant(ref schedule, "schedule", "contracts.schedule");
            NetScribe.LookStringList(ref conditions, "conditions");
            Scribe_Values.Look(ref depositPolicyKey, "depositPolicy");
            Scribe_Values.Look(ref refundPolicyKey, "refundPolicy");
            Scribe_Values.Look(ref replacementPolicyKey, "replacementPolicy");
        }
    }

    /// <summary>What the issuer asked for, kept for reposts, replacements and continuations.</summary>
    public sealed class ContractRequest : IExposable
    {
        public ProcurementMode mode = ProcurementMode.Open;
        public int premiumContribution;
        public bool wantsInsurance;

        public void ExposeData()
        {
            NetScribe.LookEnum(ref mode, "mode", ProcurementMode.Open);
            Scribe_Values.Look(ref premiumContribution, "premium", 0);
            Scribe_Values.Look(ref wantsInsurance, "wantsInsurance", false);
        }
    }

    /// <summary>Small polymorphic objective parts (DATA_MODEL § 9).</summary>
    public abstract class ContractObjective : IExposable
    {
        public abstract void ExposeData();
    }

    /// <summary>Acquire an EXACT item and quantity. Intel never has this shape; procurement does.</summary>
    public sealed class AcquireObjective : ContractObjective
    {
        public DefRef<ThingDef> thing;
        public int count;

        /// <summary>Committed by the operation outcome (never recomputed).</summary>
        public int secured;
        public int delivered;

        public string Label => thing == null ? "?" : thing.LabelSnapshot;
        public string DefName => thing?.defName;

        public override void ExposeData()
        {
            Scribe_Deep.Look(ref thing, "thing");
            Scribe_Values.Look(ref count, "count", 0);
            Scribe_Values.Look(ref secured, "secured", 0);
            Scribe_Values.Look(ref delivered, "delivered", 0);
        }
    }

    /// <summary>
    /// Deliver by drop pod (STATE_MACHINES § 4.4). The preferred destination is a player home map;
    /// the fallback is any home map, then Hold.
    /// </summary>
    public sealed class DeliverObjective : ContractObjective
    {
        public int preferredMapId = -1;
        public string preferredMapLabel;
        public int attempts;
        public int holdSinceTick = -1;
        public int deliveredMapId = -1;
        public int deliveredTick = -1;
        public string deliveredMapLabel;
        public string lastFailureKey;

        // The delivery in progress (committed when the goods are ready, never recomputed).
        public int pendingCount;
        public int balanceDue;
        public bool balancePaid;
        public bool partial;
        public string partialCauseKey;

        public bool InProgress => pendingCount > 0 && deliveredTick < 0;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref preferredMapId, "preferredMap", -1);
            Scribe_Values.Look(ref preferredMapLabel, "preferredMapLabel");
            Scribe_Values.Look(ref attempts, "attempts", 0);
            Scribe_Values.Look(ref holdSinceTick, "holdSince", -1);
            Scribe_Values.Look(ref deliveredMapId, "deliveredMap", -1);
            Scribe_Values.Look(ref deliveredTick, "deliveredTick", -1);
            Scribe_Values.Look(ref deliveredMapLabel, "deliveredMapLabel");
            Scribe_Values.Look(ref lastFailureKey, "lastFailure");
            Scribe_Values.Look(ref pendingCount, "pendingCount", 0);
            Scribe_Values.Look(ref balanceDue, "balanceDue", 0);
            Scribe_Values.Look(ref balancePaid, "balancePaid", false);
            Scribe_Values.Look(ref partial, "partial", false);
            Scribe_Values.Look(ref partialCauseKey, "partialCause");
        }
    }

    /// <summary>A willingness refusal. Not an offer (STATE_MACHINES § 5). Capped per contract.</summary>
    public sealed class Refusal : IExposable
    {
        public ActorId actor;
        public string actorName;
        public List<string> reasonKeys = new List<string>();
        public int tick;
        public int round;

        public void ExposeData()
        {
            NetScribe.Look(ref actor, "actor");
            Scribe_Values.Look(ref actorName, "actorName");
            NetScribe.LookStringList(ref reasonKeys, "reasons");
            Scribe_Values.Look(ref tick, "tick", 0);
            Scribe_Values.Look(ref round, "round", 0);
        }
    }

    /// <summary>What the contractor asked for when it reported trouble mid-contract.</summary>
    public sealed class RenegotiationAsk : IExposable
    {
        public string reasonKey;
        public int extraSilver;
        public int reducedCount;
        public int tick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref reasonKey, "reason");
            Scribe_Values.Look(ref extraSilver, "extraSilver", 0);
            Scribe_Values.Look(ref reducedCount, "reducedCount", 0);
            Scribe_Values.Look(ref tick, "tick", 0);
        }
    }

    /// <summary>Committed at close.</summary>
    public sealed class ContractOutcome : IExposable
    {
        public ContractStatus result;
        public string causeKey;
        public int requested;
        public int delivered;
        public string bandKey;
        public int tick;
        public HistoryRecordId historyRecord;

        public void ExposeData()
        {
            NetScribe.LookEnum(ref result, "result", ContractStatus.Failed);
            Scribe_Values.Look(ref causeKey, "cause");
            Scribe_Values.Look(ref requested, "requested", 0);
            Scribe_Values.Look(ref delivered, "delivered", 0);
            Scribe_Values.Look(ref bandKey, "band");
            Scribe_Values.Look(ref tick, "tick", 0);
            NetScribe.Look(ref historyRecord, "historyRecord");
        }
    }

    /// <summary>Branching creates NEW contracts linked here; a terminal contract is never reopened.</summary>
    public sealed class ContractLineage : IExposable
    {
        public const string Repost = "Repost";
        public const string Continuation = "Continuation";
        public const string Replacement = "Replacement";

        public ContractId root;
        public ContractId parent;
        public ContractId inheritedFrom;
        public EntityRef spawnedBy;
        public string relationKey;
        public List<ContractId> children = new List<ContractId>();
        public int depth;

        public void ExposeData()
        {
            NetScribe.Look(ref root, "root");
            NetScribe.Look(ref parent, "parent");
            NetScribe.Look(ref inheritedFrom, "inheritedFrom");
            NetScribe.Look(ref spawnedBy, "spawnedBy");
            Scribe_Values.Look(ref relationKey, "relation");
            NetScribe.LookIntList(ref children, "children", c => c.Value, v => new ContractId(v));
            Scribe_Values.Look(ref depth, "depth", 0);
        }
    }

    public sealed class Contract : IExposable
    {
        public const int MaxRefusals = 12;

        public ContractId id;
        public string kindKey = Contractors.ContractKinds.Procurement;
        public ContractStatus status = ContractStatus.Posted;
        public int createdTick;
        public int postedTick = -1;
        public int awardedTick = -1;
        public int closedTick = -1;
        public int deadlineTick = -1;
        public int seed;
        public int rerollNonce;
        public Parties parties = new Parties();
        public ContractRequest request = new ContractRequest();
        public Terms terms;
        public List<ContractObjective> objectives = new List<ContractObjective>();
        public List<OfferId> offers = new List<OfferId>();
        public List<Refusal> refusals = new List<Refusal>();
        public OfferId acceptedOffer;
        public List<OperationId> operations = new List<OperationId>();
        public List<MoneyRecord> ledger = new List<MoneyRecord>();
        public ContractOutcome outcome;
        public ContractLineage lineage = new ContractLineage();

        // Bidding bookkeeping (STATE_MACHINES § 3, SIMULATION § 5.2).
        public int biddingRound;
        public int windowOpenTick = -1;
        public int windowCloseTick = -1;
        public List<ActorId> candidates = new List<ActorId>();
        public List<ActorId> evaluated = new List<ActorId>();

        /// <summary>Why a live contract waits (a <see cref="SubStatus"/> key), and until when.</summary>
        public string subStatus;
        public int decisionDueTick = -1;
        public RenegotiationAsk renegotiation;

        /// <summary>Why the contract is Delayed, Troubled or Renegotiating right now (a cause key).</summary>
        public string causeKey;

        public string quarantinedReason;

        /// <summary>
        /// The live Field Log (Phase 2.5): short reports about this job, kept only on a player-issued
        /// contract while it runs, and cleared when it closes (History keeps what lasts).
        /// </summary>
        public List<FieldLogEntry> fieldLog = new List<FieldLogEntry>();

        public bool IsTerminal => ContractStates.IsTerminal(status);
        public bool IsSeeking => ContractStates.IsSeeking(status);
        public bool IsUnderway => ContractStates.IsUnderway(status);
        public ProcurementMode Mode => request.mode;

        public AcquireObjective Acquire
        {
            get
            {
                for (int i = 0; i < objectives.Count; i++) if (objectives[i] is AcquireObjective a) return a;
                return null;
            }
        }

        public DeliverObjective Deliver
        {
            get
            {
                for (int i = 0; i < objectives.Count; i++) if (objectives[i] is DeliverObjective d) return d;
                return null;
            }
        }

        public string ItemLabel => Acquire?.Label ?? "?";
        public int Quantity => Acquire?.count ?? 0;

        // ------------------------------------------------------------------ money (typed; see MoneyDirection)

        /// <summary>Real silver charged from the player on this contract.</summary>
        public int ExternalCharged() => Sum(MoneyDirection.PlayerPaid, null);

        /// <summary>Real silver returned to the player on this contract (refunds and insurance payouts, pending included).</summary>
        public int ExternalRefunded() => Sum(MoneyDirection.PlayerRefunded, null);

        public int TransferredIn() => Sum(MoneyDirection.TransferIn, null);
        public int TransferredOut() => Sum(MoneyDirection.TransferOut, null);

        /// <summary>
        /// Funding of one purpose attributable to this contract: charged here, plus carried in from a
        /// linked contract, minus carried out to one. This is what every policy reads ("the deposit").
        /// </summary>
        public int Funding(MoneyPurpose purpose)
        {
            return Sum(MoneyDirection.PlayerPaid, purpose) + Sum(MoneyDirection.TransferIn, purpose) - Sum(MoneyDirection.TransferOut, purpose);
        }

        /// <summary>All funding attributable to this contract.</summary>
        public int TotalFunding() => ExternalCharged() + TransferredIn() - TransferredOut();

        /// <summary>
        /// The player's position still held by this contract: funding minus what has already been
        /// returned. A technical invalidation refunds exactly this; a replacement carries exactly this.
        /// </summary>
        public int NetFunding() => Math.Max(0, TotalFunding() - ExternalRefunded());

        private int Sum(MoneyDirection direction, MoneyPurpose? purpose)
        {
            int s = 0;
            for (int i = 0; i < ledger.Count; i++)
            {
                MoneyRecord m = ledger[i];
                if (m.direction == direction && (purpose == null || m.purpose == purpose.Value)) s += m.silver;
            }
            return s;
        }

        /// <summary>
        /// What this contract's contractor currently holds of the silver it was paid here: every credit less
        /// every clawback written on the ledger. Never negative, never above what the player paid in.
        /// </summary>
        public int ContractorHeld()
        {
            long s = 0;
            for (int i = 0; i < ledger.Count; i++) s += ledger[i].contractorSilver;
            return s < 0 ? 0 : (s > int.MaxValue ? int.MaxValue : (int)s);
        }

        /// <summary>The purposes whose payment belongs, in part, to the contractor (not the insurer).</summary>
        public static bool IsContractorBearing(MoneyPurpose purpose)
        {
            return purpose == MoneyPurpose.Deposit || purpose == MoneyPurpose.Premium || purpose == MoneyPurpose.Renegotiation || purpose == MoneyPurpose.Balance;
        }

        /// <summary>Silver of one purpose the player paid ON THIS CONTRACT (never what a replacement carried in).</summary>
        public int OwnPaid(MoneyPurpose purpose) => Sum(MoneyDirection.PlayerPaid, purpose);

        /// <summary>
        /// The contractor-bearing silver the player paid on THIS contract and has not yet had refunded out of it.
        /// Funding carried in from a replaced contract is NOT in it: it was paid to a previous contractor, so it
        /// can neither dilute nor enlarge this contractor's clawback. An insurance premium is not in it.
        /// </summary>
        public int OwnBearingRemaining()
        {
            long paid = 0;
            for (int i = 0; i < ledger.Count; i++)
            {
                MoneyRecord m = ledger[i];
                if (m.direction == MoneyDirection.PlayerPaid && IsContractorBearing(m.purpose)) paid += m.silver;
                else if (m.direction == MoneyDirection.PlayerRefunded && m.purpose == MoneyPurpose.Refund) paid -= m.fromOwnFunding;
            }
            return paid < 0 ? 0 : (paid > int.MaxValue ? int.MaxValue : (int)paid);
        }

        /// <summary>
        /// Of the funding a refund draws from, how much is the player's own payment on this contract (as opposed to
        /// funding carried in from a replaced contract). Per purpose, pro rata between own and carried-in funding;
        /// never more than the own bearing silver still held.
        /// </summary>
        public int OwnDrawnBy(RefundScope scope)
        {
            int remaining = OwnBearingRemaining();
            if (remaining <= 0 || scope == null) return 0;
            if (scope.everything) return remaining;
            long own = OwnPart(MoneyPurpose.Deposit, scope.deposit) + OwnPart(MoneyPurpose.Premium, scope.premium)
                + OwnPart(MoneyPurpose.Renegotiation, scope.renegotiation) + OwnPart(MoneyPurpose.Balance, scope.balance);
            return (int)Math.Min(own, remaining);
        }

        private int OwnPart(MoneyPurpose purpose, int draw)
        {
            if (draw <= 0) return 0;
            int funding = Funding(purpose), paid = OwnPaid(purpose);
            if (funding <= 0 || paid <= 0) return 0;
            long part = paid >= funding ? draw : (long)draw * paid / funding;
            return (int)Math.Min(part, draw);
        }

        public bool HasPendingRefund()
        {
            for (int i = 0; i < ledger.Count; i++) if (ledger[i].pending) return true;
            return false;
        }

        public void ExposeData()
        {
            NetScribe.Look(ref id, "id");
            Scribe_Values.Look(ref kindKey, "kind");
            bool badStatus = false;
            NetScribe.LookEnum(ref status, "status", ContractStatus.Posted, ref badStatus);
            Scribe_Values.Look(ref createdTick, "createdTick", 0);
            Scribe_Values.Look(ref postedTick, "postedTick", -1);
            Scribe_Values.Look(ref awardedTick, "awardedTick", -1);
            Scribe_Values.Look(ref closedTick, "closedTick", -1);
            Scribe_Values.Look(ref deadlineTick, "deadlineTick", -1);
            Scribe_Values.Look(ref seed, "seed", 0);
            Scribe_Values.Look(ref rerollNonce, "rerollNonce", 0);
            Scribe_Deep.Look(ref parties, "parties");
            Scribe_Deep.Look(ref request, "request");
            Scribe_Deep.Look(ref terms, "terms");
            NetScribe.LookListTolerant(ref objectives, "objectives", "contracts.objectives");
            NetScribe.LookIntList(ref offers, "offers", o => o.Value, v => new OfferId(v));
            NetScribe.LookListTolerant(ref refusals, "refusals", "contracts.refusals");
            NetScribe.Look(ref acceptedOffer, "acceptedOffer");
            NetScribe.LookIntList(ref operations, "operations", o => o.Value, v => new OperationId(v));
            NetScribe.LookListTolerant(ref ledger, "ledger", "contracts.ledger");
            Scribe_Deep.Look(ref outcome, "outcome");
            Scribe_Deep.Look(ref lineage, "lineage");
            Scribe_Values.Look(ref biddingRound, "biddingRound", 0);
            Scribe_Values.Look(ref windowOpenTick, "windowOpen", -1);
            Scribe_Values.Look(ref windowCloseTick, "windowClose", -1);
            NetScribe.LookIntList(ref candidates, "candidates", a => a.Value, v => new ActorId(v));
            NetScribe.LookIntList(ref evaluated, "evaluated", a => a.Value, v => new ActorId(v));
            Scribe_Values.Look(ref subStatus, "subStatus");
            Scribe_Values.Look(ref decisionDueTick, "decisionDue", -1);
            Scribe_Deep.Look(ref renegotiation, "renegotiation");
            Scribe_Values.Look(ref causeKey, "cause");
            Scribe_Values.Look(ref quarantinedReason, "quarantined");
            NetScribe.LookListTolerant(ref fieldLog, "fieldLog", "contracts.fieldLog");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (fieldLog == null) fieldLog = new List<FieldLogEntry>();
                if (parties == null) parties = new Parties();
                if (request == null) request = new ContractRequest();
                if (lineage == null) lineage = new ContractLineage();
                // An unreadable status is never guessed (SAVE_AND_MIGRATION § 7).
                if (badStatus && quarantinedReason == null) quarantinedReason = "MalformedEnum:status";
                if (Acquire == null && quarantinedReason == null) quarantinedReason = "MissingObjective:Acquire";
            }
        }

        public override string ToString()
        {
            return id + " " + kindKey + " " + status + (subStatus != null ? "/" + subStatus : "") + " " + Quantity + "x " + ItemLabel;
        }
    }

    /// <summary>
    /// One Field Log line: a translation key and the snapshotted words it needs (names, labels, numbers
    /// as text), fixed when written so opening the UI never rewords it. Never a coordinate.
    /// </summary>
    public sealed class FieldLogEntry : IExposable
    {
        public int tick;
        public string key;
        public List<string> args = new List<string>();

        public bool SameAs(FieldLogEntry o)
        {
            if (o == null || o.key != key || o.args.Count != args.Count) return false;
            for (int i = 0; i < args.Count; i++) if (args[i] != o.args[i]) return false;
            return true;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref tick, "tick", 0);
            Scribe_Values.Look(ref key, "key");
            NetScribe.LookStringList(ref args, "args");
            if (Scribe.mode == LoadSaveMode.LoadingVars && args == null) args = new List<string>();
        }
    }

    // ====================================================================== offers and quotes

    public enum QuoteComponentKind : byte
    {
        // Contractor contributions (the Offer's own bid).
        GoodsBasis = 0,
        Acquisition = 1,
        Risk = 2,
        Capability = 3,
        Logistics = 4,
        Urgency = 5,
        Profit = 6,

        // Fixer contributions (the broker's wrap).
        FixerFee = 10,
        MarketAccess = 11,
        Coordination = 12,
        Contingency = 13,
        MarketFloor = 14
    }

    /// <summary>One line of a quote, with who added it. Kept for history and the economy; never shown raw.</summary>
    public sealed class QuoteComponent : IExposable
    {
        public QuoteComponentKind kind;
        public int amount;
        public ActorId contributedBy;
        public List<string> reasonKeys = new List<string>();

        public bool IsContractor => kind < QuoteComponentKind.FixerFee;

        public void ExposeData()
        {
            NetScribe.LookEnum(ref kind, "kind", QuoteComponentKind.GoodsBasis);
            Scribe_Values.Look(ref amount, "amount", 0);
            NetScribe.Look(ref contributedBy, "by");
            NetScribe.LookStringList(ref reasonKeys, "reasons");
        }
    }

    /// <summary>
    /// What the client sees: one coherent price assembled by the broker around a contractor's bid
    /// (DATA_MODEL § 9). Frozen when created; the UI reads it, never recomputes it.
    /// </summary>
    public sealed class ProcurementQuote : IExposable
    {
        public ActorId broker;
        public string brokerName;
        public List<QuoteComponent> components = new List<QuoteComponent>();
        public int goodsBasis;
        public int marketFloor;
        public int finalPrice;
        public float depositShare;
        public int deposit;
        public int balance;
        public int etaTicks;
        public string depositPolicyKey;
        public Insurance insuranceOffer;
        public string replacementPolicyKey;
        public string refundPolicyKey;
        public int createdTick;
        public int validUntilTick;

        public int ContractorShare()
        {
            int s = 0;
            for (int i = 0; i < components.Count; i++) if (components[i].IsContractor) s += components[i].amount;
            return s;
        }

        public int BrokerShare()
        {
            int s = 0;
            for (int i = 0; i < components.Count; i++) if (!components[i].IsContractor) s += components[i].amount;
            return s;
        }

        public void ExposeData()
        {
            NetScribe.Look(ref broker, "broker");
            Scribe_Values.Look(ref brokerName, "brokerName");
            NetScribe.LookListTolerant(ref components, "components", "contracts.quote.components");
            Scribe_Values.Look(ref goodsBasis, "goodsBasis", 0);
            Scribe_Values.Look(ref marketFloor, "marketFloor", 0);
            Scribe_Values.Look(ref finalPrice, "finalPrice", 0);
            Scribe_Values.Look(ref depositShare, "depositShare", 0.5f);
            Scribe_Values.Look(ref deposit, "deposit", 0);
            Scribe_Values.Look(ref balance, "balance", 0);
            Scribe_Values.Look(ref etaTicks, "eta", 0);
            Scribe_Values.Look(ref depositPolicyKey, "depositPolicy");
            Scribe_Deep.Look(ref insuranceOffer, "insurance");
            Scribe_Values.Look(ref replacementPolicyKey, "replacementPolicy");
            Scribe_Values.Look(ref refundPolicyKey, "refundPolicy");
            Scribe_Values.Look(ref createdTick, "created", 0);
            Scribe_Values.Look(ref validUntilTick, "validUntil", 0);
        }
    }

    /// <summary>Why the bidder bid (for UI descriptors and debugging), captured when the offer is made.</summary>
    public sealed class OfferBasis : IExposable
    {
        public string relationKey;
        public string moraleKey;
        public string doctrineKey;
        public string experienceKey;
        public float preparedness;
        public float danger;
        public bool newcomer;

        public void ExposeData()
        {
            Scribe_Values.Look(ref relationKey, "relation");
            Scribe_Values.Look(ref moraleKey, "morale");
            Scribe_Values.Look(ref doctrineKey, "doctrine");
            Scribe_Values.Look(ref experienceKey, "experience");
            Scribe_Values.Look(ref preparedness, "preparedness", 0f);
            Scribe_Values.Look(ref danger, "danger", 0f);
            Scribe_Values.Look(ref newcomer, "newcomer", false);
        }
    }

    /// <summary>The CONTRACTOR's bid, with the Fixer's client-facing quote embedded.</summary>
    public sealed class Offer : IExposable
    {
        public OfferId id;
        public ContractId contract;
        public ActorId bidder;
        public string bidderName;
        public int contractorQuote;
        public int etaTicks;
        public float riskTolerance;
        public List<string> conditions = new List<string>();
        public OfferBasis basis = new OfferBasis();
        public OfferState state = OfferState.Proposed;
        public int createdTick;
        public int stateTick;
        public int expiresTick;
        public int round;
        public ProcurementQuote quote;
        public string quarantinedReason;

        public bool IsOpen => state == OfferState.Proposed;

        public void ExposeData()
        {
            NetScribe.Look(ref id, "id");
            NetScribe.Look(ref contract, "contract");
            NetScribe.Look(ref bidder, "bidder");
            Scribe_Values.Look(ref bidderName, "bidderName");
            Scribe_Values.Look(ref contractorQuote, "contractorQuote", 0);
            Scribe_Values.Look(ref etaTicks, "eta", 0);
            Scribe_Values.Look(ref riskTolerance, "riskTolerance", 0f);
            NetScribe.LookStringList(ref conditions, "conditions");
            Scribe_Deep.Look(ref basis, "basis");
            bool badState = false;
            NetScribe.LookEnum(ref state, "state", OfferState.Proposed, ref badState);
            Scribe_Values.Look(ref createdTick, "created", 0);
            Scribe_Values.Look(ref stateTick, "stateTick", 0);
            Scribe_Values.Look(ref expiresTick, "expires", 0);
            Scribe_Values.Look(ref round, "round", 0);
            Scribe_Deep.Look(ref quote, "quote");
            Scribe_Values.Look(ref quarantinedReason, "quarantined");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (basis == null) basis = new OfferBasis();
                if (badState && quarantinedReason == null) quarantinedReason = "MalformedEnum:state";
                if (quote == null && quarantinedReason == null) quarantinedReason = "MissingQuote";
            }
        }

        public override string ToString()
        {
            return id + " " + bidderName + " " + state + " " + (quote?.finalPrice ?? 0) + " silver";
        }
    }

    /// <summary>Contracts and their offers (the "contracts" store slot, DATA_MODEL § 3).</summary>
    public sealed class ContractStore : IExposable
    {
        public List<Contract> contracts = new List<Contract>();
        public List<Offer> offers = new List<Offer>();

        private readonly Dictionary<int, Contract> contractById = new Dictionary<int, Contract>();
        private readonly Dictionary<int, Offer> offerById = new Dictionary<int, Offer>();

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref contracts, "contracts", "contracts");
            NetScribe.LookListTolerant(ref offers, "offers", "contracts.offers");
        }

        public void RebuildIndex()
        {
            contractById.Clear();
            offerById.Clear();
            for (int i = 0; i < contracts.Count; i++) if (contracts[i] != null && contracts[i].id.IsValid) contractById[contracts[i].id.Value] = contracts[i];
            for (int i = 0; i < offers.Count; i++) if (offers[i] != null && offers[i].id.IsValid) offerById[offers[i].id.Value] = offers[i];
        }

        public void Add(Contract c)
        {
            contracts.Add(c);
            contractById[c.id.Value] = c;
        }

        public void Add(Offer o)
        {
            offers.Add(o);
            offerById[o.id.Value] = o;
        }

        public Contract Get(ContractId id)
        {
            Contract c;
            return id.IsValid && contractById.TryGetValue(id.Value, out c) ? c : null;
        }

        public Offer Get(OfferId id)
        {
            Offer o;
            return id.IsValid && offerById.TryGetValue(id.Value, out o) ? o : null;
        }

        public void Remove(Contract c)
        {
            contracts.Remove(c);
            contractById.Remove(c.id.Value);
        }

        public void Remove(Offer o)
        {
            offers.Remove(o);
            offerById.Remove(o.id.Value);
        }

        public int Count => contracts.Count;
    }

    /// <summary>
    /// Which funding a refund is drawn from (Phase 2.75): typed provenance, never a note key. It lets the contractor's
    /// clawback follow the funding actually refunded and who was actually paid it. <see cref="Everything"/> is a full
    /// technical invalidation; <see cref="None"/> is a payout from the insurer (nothing of the contractor's moves).
    /// The amounts describe the draw only: they never change how much silver the player receives.
    /// </summary>
    public sealed class RefundScope
    {
        public static readonly RefundScope None = new RefundScope();
        public static readonly RefundScope Everything = new RefundScope { everything = true };

        public bool everything;
        public int deposit;
        public int premium;
        public int renegotiation;
        public int balance;

        public static RefundScope Of(int deposit = 0, int premium = 0, int renegotiation = 0, int balance = 0)
        {
            return new RefundScope { deposit = deposit, premium = premium, renegotiation = renegotiation, balance = balance };
        }
    }
}
