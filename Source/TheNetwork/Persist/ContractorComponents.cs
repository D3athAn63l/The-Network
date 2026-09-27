using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Persist
{
    // Contractor capability components (DATA_MODEL § 6, ARCHITECTURE § 6.6.1). Three separate things:
    // ContractorProfile (can provide contractor services), ContractorSimulation (is simulated off-map as
    // an NPC contractor) and OrganizationProfile (has an organization's headcount). The player never
    // carries ContractorSimulation or OrganizationProfile; ContractorProfile on the player is Phase 4.
    // Values here are internal: the UI shows descriptors built from them, never the numbers.

    /// <summary>Where a contractor's operational capability is read from.</summary>
    public enum CapabilitySource : byte
    {
        NpcSimulation = 0,
        RealColony = 1
    }

    /// <summary>Can provide contractor services: bid on, accept and perform the listed kinds.</summary>
    public sealed class ContractorProfile : ActorComponent
    {
        public List<string> kinds = new List<string>();
        public List<string> specialties = new List<string>();
        public CapabilitySource capability = CapabilitySource.NpcSimulation;
        public int registeredTick;

        /// <summary>Not taking work at all (Phase 2: set when the contractor ends).</summary>
        public bool suspended;

        public override string Key => "ContractorProfile";

        public bool Supports(string kindKey)
        {
            return kindKey != null && kinds.Contains(kindKey);
        }

        public override void ExposeData()
        {
            NetScribe.LookStringList(ref kinds, "kinds");
            NetScribe.LookStringList(ref specialties, "specialties");
            NetScribe.LookEnum(ref capability, "capability", CapabilitySource.NpcSimulation);
            Scribe_Values.Look(ref registeredTick, "registeredTick", 0);
            Scribe_Values.Look(ref suspended, "suspended", false);
        }
    }

    public enum MoraleDescriptor : byte
    {
        Steady = 0,
        Confident = 1,
        Cautious = 2,
        Shaken = 3,
        Reckless = 4,
        Exhausted = 5,
        Desperate = 6
    }

    public enum CareerStage : byte
    {
        Rising = 0,
        Established = 1,
        Veteran = 2,
        Declining = 3
    }

    public enum Tier : byte
    {
        Recruit = 0,
        Regular = 1,
        Veteran = 2
    }

    /// <summary>Stable personality that drifts slowly from history (SIMULATION § 4.3). All 0..1.</summary>
    public sealed class Doctrine : IExposable
    {
        public string style;
        public float caution = 0.5f;
        public float greed = 0.5f;
        public float loyalty = 0.5f;
        public float discretion = 0.5f;
        public float professionalism = 0.5f;
        public float ambition = 0.5f;
        public float cruelty = 0.3f;

        public void ExposeData()
        {
            Scribe_Values.Look(ref style, "style");
            Scribe_Values.Look(ref caution, "caution", 0.5f);
            Scribe_Values.Look(ref greed, "greed", 0.5f);
            Scribe_Values.Look(ref loyalty, "loyalty", 0.5f);
            Scribe_Values.Look(ref discretion, "discretion", 0.5f);
            Scribe_Values.Look(ref professionalism, "professionalism", 0.5f);
            Scribe_Values.Look(ref ambition, "ambition", 0.5f);
            Scribe_Values.Look(ref cruelty, "cruelty", 0.3f);
        }
    }

    /// <summary>Group state, not pawn mood (SIMULATION § 4.2). The descriptor is persisted with hysteresis.</summary>
    public sealed class OrgMorale : IExposable
    {
        public float cohesion = 0.65f;
        public float confidence = 0.55f;
        public float fatigue = 0.1f;
        public int lastShockTick = -1;
        public MoraleDescriptor descriptor = MoraleDescriptor.Steady;
        public int descriptorTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref cohesion, "cohesion", 0.65f);
            Scribe_Values.Look(ref confidence, "confidence", 0.55f);
            Scribe_Values.Look(ref fatigue, "fatigue", 0.1f);
            Scribe_Values.Look(ref lastShockTick, "lastShockTick", -1);
            NetScribe.LookEnum(ref descriptor, "descriptor", MoraleDescriptor.Steady);
            Scribe_Values.Look(ref descriptorTick, "descriptorTick", 0);
        }
    }

    /// <summary>Abstract kit (DATA_MODEL § 6.2). Leases are Phase 3+ and deliberately absent.</summary>
    public sealed class EquipmentProfile : IExposable
    {
        public int tier = 2;
        public List<string> specialties = new List<string>();
        public float condition = 0.9f;

        public void ExposeData()
        {
            Scribe_Values.Look(ref tier, "tier", 2);
            NetScribe.LookStringList(ref specialties, "specialties");
            Scribe_Values.Look(ref condition, "condition", 0.9f);
        }
    }

    /// <summary>
    /// How far, how fast and what the contractor can move: capability, never owned vehicles
    /// (DATA_MODEL § 6.2). An independent axis from fame, experience and size.
    /// </summary>
    public sealed class MobilityProfile : IExposable
    {
        public List<string> modes = new List<string>();
        public Band rangeBand = Band.Medium;
        public Band speedBand = Band.Medium;
        public Band liftBand = Band.Medium;

        public bool Has(string mode)
        {
            return mode != null && modes.Contains(mode);
        }

        public void ExposeData()
        {
            NetScribe.LookStringList(ref modes, "modes");
            NetScribe.LookEnum(ref rangeBand, "range", Band.Medium);
            NetScribe.LookEnum(ref speedBand, "speed", Band.Medium);
            NetScribe.LookEnum(ref liftBand, "lift", Band.Medium);
        }
    }

    /// <summary>
    /// The abstract off-map state of an NPC contractor, Solo or organization (DATA_MODEL § 6.2). It
    /// never exists on the PlayerProxy.
    /// </summary>
    public sealed class ContractorSimulation : ActorComponent
    {
        public FactionRef origin;
        public string originSnapshot;

        /// <summary>The origin faction no longer resolves; the contractor continues independently.</summary>
        public bool originLost;

        public EquipmentProfile equipment = new EquipmentProfile();
        public Doctrine doctrine = new Doctrine();
        public OrgMorale morale = new OrgMorale();
        public List<OperationId> commitments = new List<OperationId>();
        public int funds;
        public CareerStage careerStage = CareerStage.Rising;
        public float retirementPressure;
        public MobilityProfile mobility = new MobilityProfile();
        public int nextUpkeepTick = -1;
        public int lastUpkeepTick = -1;

        /// <summary>
        /// Hidden operational know-how, 0..1 (a Solo: the person; an organization: its collective
        /// practice). The Green … Legendary experience tier is derived from it and the roster; it is
        /// never the fame tier.
        /// </summary>
        public float skill = 0.3f;

        /// <summary>Operations survived since the last cohort promotion (organizations).</summary>
        public int opsSincePromotion;

        public int opsCompleted;

        /// <summary>Runtime only: derived strength, recomputed after anything that changes it.</summary>
        internal float cachedStrength = -1f;

        public override string Key => "ContractorSimulation";

        public void MarkDirty()
        {
            cachedStrength = -1f;
        }

        public override void ExposeData()
        {
            Scribe_Deep.Look(ref origin, "origin");
            Scribe_Values.Look(ref originSnapshot, "originSnapshot");
            Scribe_Values.Look(ref originLost, "originLost", false);
            Scribe_Deep.Look(ref equipment, "equipment");
            Scribe_Deep.Look(ref doctrine, "doctrine");
            Scribe_Deep.Look(ref morale, "morale");
            NetScribe.LookIntList(ref commitments, "commitments", o => o.Value, v => new OperationId(v));
            Scribe_Values.Look(ref funds, "funds", 0);
            NetScribe.LookEnum(ref careerStage, "career", CareerStage.Rising);
            Scribe_Values.Look(ref retirementPressure, "retirementPressure", 0f);
            Scribe_Deep.Look(ref mobility, "mobility");
            Scribe_Values.Look(ref nextUpkeepTick, "nextUpkeepTick", -1);
            Scribe_Values.Look(ref lastUpkeepTick, "lastUpkeepTick", -1);
            Scribe_Values.Look(ref skill, "skill", 0.3f);
            Scribe_Values.Look(ref opsSincePromotion, "opsSincePromotion", 0);
            Scribe_Values.Look(ref opsCompleted, "opsCompleted", 0);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (equipment == null) equipment = new EquipmentProfile();
                if (doctrine == null) doctrine = new Doctrine();
                if (morale == null) morale = new OrgMorale();
                if (mobility == null) mobility = new MobilityProfile();
                if (commitments == null) commitments = new List<OperationId>();
            }
            cachedStrength = -1f;
        }
    }

    /// <summary>Generic headcount of one tier. Known Characters are counted separately.</summary>
    public sealed class TierCount : IExposable
    {
        public Tier tier;
        public int healthy;

        /// <summary>Kept equal to the sum of this tier's recovery buckets.</summary>
        public int wounded;

        public TierCount()
        {
        }

        public TierCount(Tier tier, int healthy)
        {
            this.tier = tier;
            this.healthy = healthy;
        }

        public void ExposeData()
        {
            NetScribe.LookEnum(ref tier, "tier", Tier.Recruit);
            Scribe_Values.Look(ref healthy, "healthy", 0);
            Scribe_Values.Look(ref wounded, "wounded", 0);
        }
    }

    /// <summary>Aggregated wounded members recovering together (≤ 8 buckets, merged when full).</summary>
    public sealed class RecoveryBucket : IExposable
    {
        public Tier tier;
        public int count;
        public int dueTick;

        public void ExposeData()
        {
            NetScribe.LookEnum(ref tier, "tier", Tier.Recruit);
            Scribe_Values.Look(ref count, "count", 0);
            Scribe_Values.Look(ref dueTick, "due", 0);
        }
    }

    public sealed class RecruitmentState : IExposable
    {
        public int lastRecruitTick = -1;
        public int recruitedTotal;

        public void ExposeData()
        {
            Scribe_Values.Look(ref lastRecruitTick, "lastRecruitTick", -1);
            Scribe_Values.Look(ref recruitedTotal, "recruitedTotal", 0);
        }
    }

    /// <summary>Minimal succession state (Phase 2). Contested succession is Phase 6.</summary>
    public sealed class SuccessionState : IExposable
    {
        public int successions;
        public int lastSuccessionTick = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref successions, "successions", 0);
            Scribe_Values.Look(ref lastSuccessionTick, "lastSuccessionTick", -1);
        }
    }

    /// <summary>
    /// An NPC organization's structure (DATA_MODEL § 6.3): named people as Known Character records,
    /// generic members as headcount by tier. Strength is derived, never persisted.
    /// </summary>
    public sealed class OrganizationProfile : ActorComponent
    {
        public const int MaxKnownMembers = 6;
        public const int MaxLieutenants = 2;
        public const int MaxRecoveryBuckets = 8;

        public CharacterId leader;
        public List<CharacterId> lieutenants = new List<CharacterId>();

        /// <summary>Every named member, leader and lieutenants included (≤ 6).</summary>
        public List<CharacterId> knownMembers = new List<CharacterId>();

        public List<TierCount> tiers = new List<TierCount>();
        public List<RecoveryBucket> woundedRecovery = new List<RecoveryBucket>();

        /// <summary>Generic headcount checked out to running operations.</summary>
        public List<TierCount> committed = new List<TierCount>();

        public int capacity;
        public RecruitmentState recruitment = new RecruitmentState();
        public SuccessionState succession = new SuccessionState();

        public override string Key => "OrganizationProfile";

        public TierCount TierOf(Tier t, bool committedList = false)
        {
            List<TierCount> list = committedList ? committed : tiers;
            for (int i = 0; i < list.Count; i++) if (list[i].tier == t) return list[i];
            TierCount c = new TierCount(t, 0);
            list.Add(c);
            list.Sort((a, b) => a.tier.CompareTo(b.tier));
            return c;
        }

        public int Healthy
        {
            get
            {
                int n = 0;
                for (int i = 0; i < tiers.Count; i++) n += tiers[i].healthy;
                return n;
            }
        }

        public int Wounded
        {
            get
            {
                int n = 0;
                for (int i = 0; i < tiers.Count; i++) n += tiers[i].wounded;
                return n;
            }
        }

        public int Committed
        {
            get
            {
                int n = 0;
                for (int i = 0; i < committed.Count; i++) n += committed[i].healthy;
                return n;
            }
        }

        public override void ExposeData()
        {
            NetScribe.Look(ref leader, "leader");
            NetScribe.LookIntList(ref lieutenants, "lieutenants", c => c.Value, v => new CharacterId(v));
            NetScribe.LookIntList(ref knownMembers, "knownMembers", c => c.Value, v => new CharacterId(v));
            NetScribe.LookListTolerant(ref tiers, "tiers", "actors.roster");
            NetScribe.LookListTolerant(ref woundedRecovery, "woundedRecovery", "actors.roster");
            NetScribe.LookListTolerant(ref committed, "committed", "actors.roster");
            Scribe_Values.Look(ref capacity, "capacity", 0);
            Scribe_Deep.Look(ref recruitment, "recruitment");
            Scribe_Deep.Look(ref succession, "succession");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (recruitment == null) recruitment = new RecruitmentState();
                if (succession == null) succession = new SuccessionState();
            }
        }
    }
}
