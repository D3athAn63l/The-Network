using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Persist.Events
{
    /// <summary>
    /// Permanent event type keys (EVENTS_AND_HISTORY § 1.6, § 2). Only the Phase 1 events exist.
    /// A key never changes once shipped, even if the C# class is renamed.
    /// </summary>
    public static class EventKeys
    {
        public const string NetworkBootstrapped = "Network.Bootstrapped";
        public const string NetworkLoaded = "Network.Loaded";
        public const string CastImported = "Cast.Imported";

        public const string IntelRequested = "Intel.Requested";
        public const string IntelLeadDelivered = "Intel.LeadDelivered";
        public const string IntelNoLead = "Intel.NoLead";
        public const string IntelSearchContinued = "Intel.SearchContinued";
        public const string IntelConcluded = "Intel.Concluded";
        public const string IntelCancelled = "Intel.Cancelled";
        public const string IntelInvalidated = "Intel.Invalidated";

        public const string OpportunityGenerated = "Opportunity.Generated";
        public const string OpportunityMaterialized = "Opportunity.Materialized";
        public const string OpportunityEngaged = "Opportunity.Engaged";
        public const string OpportunityClaimed = "Opportunity.Claimed";
        public const string OpportunityAbandoned = "Opportunity.Abandoned";
        public const string OpportunityExpired = "Opportunity.Expired";
        public const string OpportunityDestroyed = "Opportunity.Destroyed";
        public const string OpportunityInvalidated = "Opportunity.Invalidated";
        /// <summary>Added in Phase 1 for the optional expiry-warning letter (EVENTS_AND_HISTORY § 2 lists letters as a consumer).</summary>
        public const string OpportunityExpiringSoon = "Opportunity.ExpiringSoon";

        public const string ReferenceInvalidated = "Reference.Invalidated";
    }

    /// <summary>Kernel lifecycle and cast import (diagnostics only).</summary>
    public sealed class SystemEvent : NetworkEvent
    {
        public int count1;
        public int count2;
        public int count3;
        public string note;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref count1, "count1", 0);
            Scribe_Values.Look(ref count2, "count2", 0);
            Scribe_Values.Look(ref count3, "count3", 0);
            Scribe_Values.Look(ref note, "note");
        }
    }

    /// <summary>
    /// Every Intel event. Carries snapshots (item label, source name) so history and letters read
    /// correctly without resolving anything. There is deliberately no quantity field: Intel is
    /// topic-only (DATA_MODEL § 8).
    /// </summary>
    public sealed class IntelEvent : NetworkEvent
    {
        public IntelRequestId request;
        public ActorId source;
        public ActorId requester;
        public LeadId lead;
        public OpportunityId opportunity;
        public int round;
        public int leadsSoFar;
        public int silver;
        public string reasonKey;
        public string topicDefName;
        public string topicLabel;
        public string sourceName;

        public override void ExposeData()
        {
            base.ExposeData();
            NetScribe.Look(ref request, "request");
            NetScribe.Look(ref source, "source");
            NetScribe.Look(ref requester, "requester");
            NetScribe.Look(ref lead, "lead");
            NetScribe.Look(ref opportunity, "opportunity");
            Scribe_Values.Look(ref round, "round", 0);
            Scribe_Values.Look(ref leadsSoFar, "leadsSoFar", 0);
            Scribe_Values.Look(ref silver, "silver", 0);
            Scribe_Values.Look(ref reasonKey, "reason");
            Scribe_Values.Look(ref topicDefName, "topicDef");
            Scribe_Values.Look(ref topicLabel, "topicLabel");
            Scribe_Values.Look(ref sourceName, "sourceName");
        }
    }

    /// <summary>Every Opportunity event, with the snapshots the narrative needs.</summary>
    public sealed class OpportunityEvent : NetworkEvent
    {
        public OpportunityId opportunity;
        public LeadId lead;
        public IntelRequestId request;
        public ActorId source;
        public ActorId holderActor;
        public string holderName;
        public string sourceKindKey;
        public string targetDefName;
        public string targetLabel;
        public int targetCount;
        public int recoveredCount;
        public RecoveredBand recoveredBand;
        public int marketValue;
        public string reasonKey;
        public bool reportHeld;

        public override void ExposeData()
        {
            base.ExposeData();
            NetScribe.Look(ref opportunity, "opportunity");
            NetScribe.Look(ref lead, "lead");
            NetScribe.Look(ref request, "request");
            NetScribe.Look(ref source, "source");
            NetScribe.Look(ref holderActor, "holderActor");
            Scribe_Values.Look(ref holderName, "holderName");
            Scribe_Values.Look(ref sourceKindKey, "sourceKind");
            Scribe_Values.Look(ref targetDefName, "targetDef");
            Scribe_Values.Look(ref targetLabel, "targetLabel");
            Scribe_Values.Look(ref targetCount, "targetCount", 0);
            Scribe_Values.Look(ref recoveredCount, "recoveredCount", 0);
            NetScribe.LookEnum(ref recoveredBand, "recoveredBand", RecoveredBand.None);
            Scribe_Values.Look(ref marketValue, "marketValue", 0);
            Scribe_Values.Look(ref reasonKey, "reason");
            Scribe_Values.Look(ref reportHeld, "reportHeld", false);
        }
    }

    /// <summary>An external reference could no longer be resolved (DATA_MODEL § 2).</summary>
    public sealed class ReferenceEvent : NetworkEvent
    {
        public EntityRef owner;
        public string refKind;
        public string refKey;

        public override void ExposeData()
        {
            base.ExposeData();
            NetScribe.Look(ref owner, "owner");
            Scribe_Values.Look(ref refKind, "refKind");
            Scribe_Values.Look(ref refKey, "refKey");
        }
    }

    public static class EventFactory
    {
        public static T Make<T>(string typeKey, Importance importance, params EntityRef[] subjects) where T : NetworkEvent, new()
        {
            T e = new T { typeKey = typeKey, importance = importance, subjects = new List<EntityRef>() };
            if (subjects != null)
            {
                for (int i = 0; i < subjects.Length; i++)
                {
                    if (subjects[i].IsValid) e.subjects.Add(subjects[i]);
                }
            }
            return e;
        }
    }
}
