using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Persist
{
    // Actor capability components (DATA_MODEL § 4.2). These are data; services interpret them. The
    // full type names in this namespace are frozen once shipped (SAVE_AND_MIGRATION § 3). Phase 1
    // uses IssuerProfile (the player), FixerProfile and IntelSourceProfile. The other components named
    // in the data model arrive with their phases.

    public abstract class ActorComponent : IExposable
    {
        public abstract string Key { get; }

        public abstract void ExposeData();
    }

    /// <summary>Creates, offers and funds work. The player holds one from the start.</summary>
    public sealed class IssuerProfile : ActorComponent
    {
        public int budgetBand = 1;
        public List<string> preferredKinds = new List<string>();
        public float legitimacy = 0.6f;
        public float paysOnTime = 0.8f;

        public override string Key => "IssuerProfile";

        public override void ExposeData()
        {
            Scribe_Values.Look(ref budgetBand, "budgetBand", 1);
            NetScribe.LookStringList(ref preferredKinds, "preferredKinds");
            Scribe_Values.Look(ref legitimacy, "legitimacy", 0.6f);
            Scribe_Values.Look(ref paysOnTime, "paysOnTime", 0.8f);
        }
    }

    /// <summary>
    /// Brokering capability (DATA_MODEL § 4.3). Phase 1 stores the profile imported from the cast
    /// template; the brokerage role itself (quotes, deposits, insurance) is Phase 2.
    /// </summary>
    public sealed class FixerProfile : ActorComponent
    {
        public List<string> specialties = new List<string>();
        public ReachBand contractorReach = ReachBand.Local;
        public ReachBand marketAccess = ReachBand.Local;
        public string feePolicyKey;
        public Band feeBand = Band.Medium;
        public string brokeragePolicyKey;
        public string depositPolicyKey;
        public string insurancePolicyKey;
        public string quotePolicyKey;
        public string replacementPolicyKey;
        public List<ActorId> clients = new List<ActorId>();

        public override string Key => "FixerProfile";

        public override void ExposeData()
        {
            NetScribe.LookStringList(ref specialties, "specialties");
            NetScribe.LookEnum(ref contractorReach, "contractorReach", ReachBand.Local);
            NetScribe.LookEnum(ref marketAccess, "marketAccess", ReachBand.Local);
            Scribe_Values.Look(ref feePolicyKey, "feePolicy");
            NetScribe.LookEnum(ref feeBand, "feeBand", Band.Medium);
            Scribe_Values.Look(ref brokeragePolicyKey, "brokeragePolicy");
            Scribe_Values.Look(ref depositPolicyKey, "depositPolicy");
            Scribe_Values.Look(ref insurancePolicyKey, "insurancePolicy");
            Scribe_Values.Look(ref quotePolicyKey, "quotePolicy");
            Scribe_Values.Look(ref replacementPolicyKey, "replacementPolicy");
            NetScribe.LookIntList(ref clients, "clients", a => a.Value, v => new ActorId(v));
        }
    }

    /// <summary>
    /// Can provide Intel (DATA_MODEL § 4.2). Bands and replaceable policy keys, never formulas. The
    /// policy code that reads them lives in <c>Domain.Intel.SourcePolicies</c>.
    /// </summary>
    public sealed class IntelSourceProfile : ActorComponent
    {
        public List<string> specialties = new List<string>();
        public List<string> coverage = new List<string>();
        public Band speedBand = Band.Medium;
        public Band reliabilityBand = Band.Medium;
        public Band feeBand = Band.Medium;
        public string feePolicyKey;
        public string continuationPolicyKey;
        public float discretion = 0.5f;

        /// <summary>True for a profile derived from a faction's signals (faction contacts, Phase 1).</summary>
        public bool derived;

        public override string Key => "IntelSourceProfile";

        public override void ExposeData()
        {
            NetScribe.LookStringList(ref specialties, "specialties");
            NetScribe.LookStringList(ref coverage, "coverage");
            NetScribe.LookEnum(ref speedBand, "speedBand", Band.Medium);
            NetScribe.LookEnum(ref reliabilityBand, "reliabilityBand", Band.Medium);
            NetScribe.LookEnum(ref feeBand, "feeBand", Band.Medium);
            Scribe_Values.Look(ref feePolicyKey, "feePolicy");
            Scribe_Values.Look(ref continuationPolicyKey, "continuationPolicy");
            Scribe_Values.Look(ref discretion, "discretion", 0.5f);
            Scribe_Values.Look(ref derived, "derived", false);
        }
    }
}
