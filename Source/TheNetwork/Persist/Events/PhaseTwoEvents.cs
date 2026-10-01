using System.Collections.Generic;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Persist.Events
{
    /// <summary>
    /// Phase 2 event keys (EVENTS_AND_HISTORY § 2). Only events that Phase 2 actually publishes. Five
    /// additions to the catalog are recorded there: <c>Contractor.Created</c> (a world-generated
    /// newcomer), <c>Contractor.Ended</c> (a Solo died or an organization had no possible successor;
    /// the richer Phase 6 lifecycle keeps its own keys), <c>Contractor.OriginLost</c>,
    /// <c>Contract.Unfilled</c> and <c>Opportunity.FollowUpCreated</c> (Consequence Engine v0).
    /// </summary>
    public static partial class EventKeys
    {
        public const string ContractorCreated = "Contractor.Created";
        public const string ContractorEnded = "Contractor.Ended";
        public const string ContractorOriginLost = "Contractor.OriginLost";
        public const string ContractorCasualties = "Contractor.Casualties";
        public const string ContractorCaptured = "Contractor.Captured";
        public const string ContractorMissing = "Contractor.Missing";
        public const string ContractorStranded = "Contractor.Stranded";
        public const string CharacterKilled = "KnownCharacter.Killed";
        public const string CharacterPromoted = "KnownCharacter.Promoted";
        public const string LeaderKilled = "Leader.Killed";
        public const string LeaderSucceeded = "Leader.Succeeded";
        public const string MoraleShifted = "Organization.MoraleShifted";

        // Phase 2.75 (careers): published only for meaningful transitions, never for hidden score drift.
        public const string ContractorFameChanged = "Contractor.FameChanged";
        public const string ContractorAdvanced = "Contractor.Advanced";

        public const string ContractPosted = "Contract.Posted";
        public const string ContractOfferReceived = "Contract.OfferReceived";
        public const string ContractQuoted = "Contract.Quoted";
        public const string ContractRefused = "Contract.Refused";
        public const string ContractUnfilled = "Contract.Unfilled";
        public const string ContractAwarded = "Contract.Awarded";
        public const string ContractDelayed = "Contract.Delayed";
        public const string ContractRenegotiationRequested = "Contract.RenegotiationRequested";
        public const string ContractCompleted = "Contract.Completed";
        public const string ContractPartiallyCompleted = "Contract.PartiallyCompleted";
        public const string ContractFailed = "Contract.Failed";
        public const string ContractCancelled = "Contract.Cancelled";
        public const string ContractVoided = "Contract.Voided";
        public const string ContractExpired = "Contract.Expired";
        public const string PaymentReceived = "Payment.Received";
        public const string PaymentDefaulted = "Payment.Defaulted";

        public const string OperationStarted = "Operation.Started";
        public const string OperationResolved = "Operation.Resolved";

        public const string OpportunityFollowUpCreated = "Opportunity.FollowUpCreated";
    }

    /// <summary>
    /// Every contract event, with the snapshots the narrative needs (item label, names), so history
    /// and letters read correctly without resolving anything.
    /// </summary>
    public sealed class ContractEvent : NetworkEvent
    {
        public ContractId contract;
        public OfferId offer;
        public OperationId operation;
        public ActorId issuer;
        public ActorId contractor;
        public ActorId broker;
        public string kindKey;
        public string itemDefName;
        public string itemLabel;
        public int quantity;
        public int delivered;
        public int silver;
        public int insuranceSilver;
        public string causeKey;
        public string bandKey;
        public List<string> reasonKeys = new List<string>();
        public string contractorName;
        public string brokerName;
        public ContractId parent;

        public override void ExposeData()
        {
            base.ExposeData();
            NetScribe.Look(ref contract, "contract");
            NetScribe.Look(ref offer, "offer");
            NetScribe.Look(ref operation, "operation");
            NetScribe.Look(ref issuer, "issuer");
            NetScribe.Look(ref contractor, "contractor");
            NetScribe.Look(ref broker, "broker");
            Scribe_Values.Look(ref kindKey, "kind");
            Scribe_Values.Look(ref itemDefName, "itemDef");
            Scribe_Values.Look(ref itemLabel, "itemLabel");
            Scribe_Values.Look(ref quantity, "quantity", 0);
            Scribe_Values.Look(ref delivered, "delivered", 0);
            Scribe_Values.Look(ref silver, "silver", 0);
            Scribe_Values.Look(ref insuranceSilver, "insuranceSilver", 0);
            Scribe_Values.Look(ref causeKey, "cause");
            Scribe_Values.Look(ref bandKey, "band");
            NetScribe.LookStringList(ref reasonKeys, "reasons");
            Scribe_Values.Look(ref contractorName, "contractorName");
            Scribe_Values.Look(ref brokerName, "brokerName");
            NetScribe.Look(ref parent, "parent");
        }
    }

    /// <summary>An abstract operation started or resolved. Counts are abstract headcount, never pawns.</summary>
    public sealed class OperationEvent : NetworkEvent
    {
        public OperationId operation;
        public ContractId contract;
        public ActorId contractor;
        public string bandKey;
        public int secured;
        public int requested;
        public int killed;
        public int wounded;
        public int captured;
        public int missing;
        public string contractorName;
        public string itemLabel;
        public string flavorKey;

        public override void ExposeData()
        {
            base.ExposeData();
            NetScribe.Look(ref operation, "operation");
            NetScribe.Look(ref contract, "contract");
            NetScribe.Look(ref contractor, "contractor");
            Scribe_Values.Look(ref bandKey, "band");
            Scribe_Values.Look(ref secured, "secured", 0);
            Scribe_Values.Look(ref requested, "requested", 0);
            Scribe_Values.Look(ref killed, "killed", 0);
            Scribe_Values.Look(ref wounded, "wounded", 0);
            Scribe_Values.Look(ref captured, "captured", 0);
            Scribe_Values.Look(ref missing, "missing", 0);
            Scribe_Values.Look(ref contractorName, "contractorName");
            Scribe_Values.Look(ref itemLabel, "itemLabel");
            Scribe_Values.Look(ref flavorKey, "flavor");
        }
    }

    /// <summary>Something happened to a contractor or one of its Known Characters.</summary>
    public sealed class ContractorEvent : NetworkEvent
    {
        public ActorId actor;
        public CharacterId character;
        public CharacterId successor;
        public ContractId contract;
        public OperationId operation;
        public string actorName;
        public string characterName;
        public string successorName;
        public int killed;
        public int wounded;
        public int captured;
        public int missing;
        public string descriptorKey;
        public string reasonKey;
        public bool leader;

        public override void ExposeData()
        {
            base.ExposeData();
            NetScribe.Look(ref actor, "actor");
            NetScribe.Look(ref character, "character");
            NetScribe.Look(ref successor, "successor");
            NetScribe.Look(ref contract, "contract");
            NetScribe.Look(ref operation, "operation");
            Scribe_Values.Look(ref actorName, "actorName");
            Scribe_Values.Look(ref characterName, "characterName");
            Scribe_Values.Look(ref successorName, "successorName");
            Scribe_Values.Look(ref killed, "killed", 0);
            Scribe_Values.Look(ref wounded, "wounded", 0);
            Scribe_Values.Look(ref captured, "captured", 0);
            Scribe_Values.Look(ref missing, "missing", 0);
            Scribe_Values.Look(ref descriptorKey, "descriptor");
            Scribe_Values.Look(ref reasonKey, "reason");
            Scribe_Values.Look(ref leader, "leader", false);
        }
    }
}
