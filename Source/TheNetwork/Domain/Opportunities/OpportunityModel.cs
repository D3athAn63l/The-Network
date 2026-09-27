using System.Collections.Generic;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Domain.Opportunities
{
    // World truth (DATA_MODEL § 8): what actually exists, where, how much, who holds it, when it
    // expires. Committed at generation; never recomputed.

    public enum OpportunityState : byte
    {
        Latent = 0,
        Revealed = 1,
        Materialized = 2,
        Engaged = 3,
        Claimed = 4,
        Abandoned = 5,
        Expired = 6,
        LostToCompetitor = 7,
        Destroyed = 8,
        Vanished = 9,
        Invalidated = 10,
        Closed = 11
    }

    public enum OpportunityOrigin : byte
    {
        IntelResolution = 0,
        ConsequenceRule = 1,
        WorldEvent = 2,
        Debug = 3
    }

    /// <summary>Candidate source contexts (ARCHITECTURE § 6.14.1). Phase 1 can express the cache kinds.</summary>
    public enum SourceKind : byte
    {
        SameSourceFaction = 0,
        SimilarTechFaction = 1,
        HostileFaction = 2,
        NeutralOwner = 3,
        Trader = 4,
        Pirates = 5,
        Mechanoids = 6,
        AncientSite = 7,
        Salvage = 8,
        AbandonedCache = 9,
        NoCredibleSource = 10
    }

    public static class Archetypes
    {
        public const string GuardedCache = "GuardedCache";
    }

    /// <summary>Threat profile keys; the SiteAdapter maps them onto vanilla SitePartDefs.</summary>
    public static class ThreatProfiles
    {
        public const string None = "None";
        public const string Outpost = "Outpost";
        public const string BanditCamp = "BanditCamp";
        public const string AmbushHidden = "AmbushHidden";
        public const string Manhunters = "Manhunters";
        public const string SleepingMechanoids = "SleepingMechanoids";
    }

    public sealed class SourceContext : IExposable
    {
        public SourceKind kind = SourceKind.NoCredibleSource;
        public FactionRef holder;
        public ActorId holderActor;
        public Stance holderStance = Stance.None;
        public List<string> evidence = new List<string>();

        public void ExposeData()
        {
            NetScribe.LookEnum(ref kind, "kind", SourceKind.NoCredibleSource);
            Scribe_Deep.Look(ref holder, "holder");
            NetScribe.Look(ref holderActor, "holderActor");
            NetScribe.LookEnum(ref holderStance, "holderStance", Stance.None);
            NetScribe.LookStringList(ref evidence, "evidence");
        }
    }

    public sealed class ThreatSpec : IExposable
    {
        public float points;
        public FactionRef factionUsed;
        public string profileKey = ThreatProfiles.None;

        public void ExposeData()
        {
            Scribe_Values.Look(ref points, "points", 0f);
            Scribe_Deep.Look(ref factionUsed, "faction");
            Scribe_Values.Look(ref profileKey, "profile");
        }
    }

    /// <summary>
    /// Claim accounting (STATE_MACHINES § 2.2). Approximate by design: recovered = what was on the map
    /// at generation minus what the latest sample still finds, plus cargo of transporters that left
    /// after that sample. Samples measure what is left, so nothing that returns is counted twice.
    /// </summary>
    public sealed class Engagement : IExposable
    {
        public int firstEngagedTick = -1;
        public int initialOnMap = -1;
        public int lastRemaining = -1;
        public int lastSampleTick = -1;
        public int caravanDepartures;
        public int caravanTally;
        public int podTally;
        public List<int> countedTransporters = new List<int>();
        public int recovered;
        public RecoveredBand recoveredBand = RecoveredBand.None;
        public bool settled;
        public ActorId claimedBy;

        public void ExposeData()
        {
            Scribe_Values.Look(ref firstEngagedTick, "firstEngagedTick", -1);
            Scribe_Values.Look(ref initialOnMap, "initialOnMap", -1);
            Scribe_Values.Look(ref lastRemaining, "lastRemaining", -1);
            Scribe_Values.Look(ref lastSampleTick, "lastSampleTick", -1);
            Scribe_Values.Look(ref caravanDepartures, "caravanDepartures", 0);
            Scribe_Values.Look(ref caravanTally, "caravanTally", 0);
            Scribe_Values.Look(ref podTally, "podTally", 0);
            Scribe_Collections.Look(ref countedTransporters, "countedTransporters", LookMode.Value);
            Scribe_Values.Look(ref recovered, "recovered", 0);
            NetScribe.LookEnum(ref recoveredBand, "recoveredBand", RecoveredBand.None);
            Scribe_Values.Look(ref settled, "settled", false);
            NetScribe.Look(ref claimedBy, "claimedBy");
            if (countedTransporters == null) countedTransporters = new List<int>();
        }
    }

    public sealed class Opportunity : IExposable
    {
        public OpportunityId id;
        public string archetypeKey = Archetypes.GuardedCache;
        public OpportunityOrigin origin = OpportunityOrigin.IntelResolution;
        public EntityRef originRef;
        public OpportunityId parentOpportunity;
        public int lineageDepth;
        public SourceContext sourceContext = new SourceContext();
        public List<OpportunityPayload> payload = new List<OpportunityPayload>();
        public TileRef location;
        public ThreatSpec threat = new ThreatSpec();
        public int expiresTick = -1;
        public OpportunityState state = OpportunityState.Latent;
        public int stateTick;
        public WorldObjectRef site;
        public Engagement engagement = new Engagement();
        public int seed;
        public int committedAtTick;
        public int endedTick = -1;
        public int closeDueTick = -1;
        public string outcomeKey;
        public LeadId lead;
        public IntelRequestId intel;
        public bool expiryWarned;
        public string quarantinedReason;

        public bool IsTerminal
        {
            get
            {
                switch (state)
                {
                    case OpportunityState.Claimed:
                    case OpportunityState.Abandoned:
                    case OpportunityState.Expired:
                    case OpportunityState.LostToCompetitor:
                    case OpportunityState.Destroyed:
                    case OpportunityState.Vanished:
                    case OpportunityState.Invalidated:
                    case OpportunityState.Closed:
                        return true;
                    default:
                        return false;
                }
            }
        }

        public ItemPayload Target
        {
            get
            {
                for (int i = 0; i < payload.Count; i++)
                {
                    ItemPayload p = payload[i] as ItemPayload;
                    if (p != null && p.role == PayloadRole.Target) return p;
                }
                return null;
            }
        }

        public int TargetCount => Target?.count ?? 0;

        public void ExposeData()
        {
            NetScribe.Look(ref id, "id");
            Scribe_Values.Look(ref archetypeKey, "archetype");
            NetScribe.LookEnum(ref origin, "origin", OpportunityOrigin.IntelResolution);
            NetScribe.Look(ref originRef, "originRef");
            NetScribe.Look(ref parentOpportunity, "parentOpportunity");
            Scribe_Values.Look(ref lineageDepth, "lineageDepth", 0);
            Scribe_Deep.Look(ref sourceContext, "sourceContext");
            NetScribe.LookListTolerant(ref payload, "payload", "opportunities.payload");
            Scribe_Deep.Look(ref location, "location");
            Scribe_Deep.Look(ref threat, "threat");
            Scribe_Values.Look(ref expiresTick, "expiresTick", -1);
            bool badState = false;
            NetScribe.LookEnum(ref state, "state", OpportunityState.Latent, ref badState);
            Scribe_Values.Look(ref stateTick, "stateTick", 0);
            Scribe_Deep.Look(ref site, "site");
            Scribe_Deep.Look(ref engagement, "engagement");
            Scribe_Values.Look(ref seed, "seed", 0);
            Scribe_Values.Look(ref committedAtTick, "committedAtTick", 0);
            Scribe_Values.Look(ref endedTick, "endedTick", -1);
            Scribe_Values.Look(ref closeDueTick, "closeDueTick", -1);
            Scribe_Values.Look(ref outcomeKey, "outcome");
            NetScribe.Look(ref lead, "lead");
            NetScribe.Look(ref intel, "intel");
            Scribe_Values.Look(ref expiryWarned, "expiryWarned", false);
            Scribe_Values.Look(ref quarantinedReason, "quarantined");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (sourceContext == null) sourceContext = new SourceContext();
                if (threat == null) threat = new ThreatSpec();
                if (engagement == null) engagement = new Engagement();
                if (badState && quarantinedReason == null) quarantinedReason = "MalformedEnum:state";
            }
        }

        public override string ToString()
        {
            return id + " " + state + " " + archetypeKey + " " + (Target == null ? "-" : Target.count + "x " + Target.LabelSnapshot);
        }
    }

    public sealed class OpportunityStore : IExposable
    {
        public List<Opportunity> opportunities = new List<Opportunity>();

        private readonly Dictionary<int, Opportunity> byId = new Dictionary<int, Opportunity>();

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref opportunities, "opportunities", "opportunities");
        }

        public void RebuildIndex()
        {
            byId.Clear();
            for (int i = 0; i < opportunities.Count; i++)
            {
                if (opportunities[i] != null && opportunities[i].id.IsValid) byId[opportunities[i].id.Value] = opportunities[i];
            }
        }

        public void Add(Opportunity o)
        {
            opportunities.Add(o);
            byId[o.id.Value] = o;
        }

        public Opportunity Get(OpportunityId id)
        {
            Opportunity o;
            return id.IsValid && byId.TryGetValue(id.Value, out o) ? o : null;
        }

        public void Remove(Opportunity o)
        {
            opportunities.Remove(o);
            byId.Remove(o.id.Value);
        }
    }
}
