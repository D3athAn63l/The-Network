using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.Domain.Relations;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Domain.Contracts
{
    /// <summary>Reason keys for refusals (persisted strings; the UI phrases them).</summary>
    public static class RefusalReasons
    {
        public const string TooDangerous = "TooDangerous";
        public const string Overcommitted = "Overcommitted";
        public const string BadBlood = "BadBlood";
        public const string Exhausted = "Exhausted";
        public const string AgainstDoctrine = "AgainstDoctrine";
        public const string PoliticalRisk = "PoliticalRisk";
        public const string PayTooLow = "PayTooLow";
        public const string YouOweUs = "YouOweUs";
        public const string Injuries = "Injuries";
        public const string Unavailable = "Unavailable";
        public const string NoCapability = "NoCapability";
        public const string OutOfRange = "OutOfRange";
    }

    public sealed class WillingnessDecision
    {
        public bool accept;
        public List<string> reasonKeys = new List<string>();
        public List<string> conditions = new List<string>();
        public float danger;
        public float appetite;
        public float preparedness;
        public string relationKey;
        public bool coinUsed;
    }

    /// <summary>
    /// The deterministic willingness model (SIMULATION § 5.1). Each factor contributes and may veto with
    /// a reason key. Only a borderline danger call uses a coin, from NetRng(contract.seed, "bid", …), so
    /// the same contractor asked about the same contract always answers the same way. The player is never
    /// run through this model.
    /// </summary>
    public static class Willingness
    {
        public const float Borderline = 0.05f;

        public static WillingnessDecision Evaluate(DomainContext ctx, NetworkActor a, Contract c, ItemFacts f)
        {
            WillingnessDecision d = new WillingnessDecision();
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            if (sim == null)
            {
                d.reasonKeys.Add(RefusalReasons.Unavailable);
                return d;
            }
            int count = c.Quantity;

            // Availability and workload.
            switch (ctx.Contractors.AvailabilityOf(a))
            {
                case Availability.Recovering: d.reasonKeys.Add(RefusalReasons.Injuries); break;
                case Availability.Exhausted: d.reasonKeys.Add(RefusalReasons.Exhausted); break;
                case Availability.Committed: d.reasonKeys.Add(RefusalReasons.Overcommitted); break;
                case Availability.Unavailable:
                case Availability.Ended: d.reasonKeys.Add(RefusalReasons.Unavailable); break;
            }

            // Relationship: contractor → issuer.
            RelationView rel = ctx.Relations.Get(a.id, c.parties.issuer);
            d.relationKey = ctx.Relations.Descriptor(a.id, c.parties.issuer);
            if (rel.betrayals > 0 || rel.standing <= -40f || (rel.defaults > 0 && rel.trust < 0.35f)) d.reasonKeys.Add(RefusalReasons.BadBlood);
            if (ctx.Procurement != null && ctx.Procurement.OwesContractor(c.parties.issuer, a.id)) d.reasonKeys.Add(RefusalReasons.YouOweUs);

            // Danger against appetite. The estimate reads no randomness.
            ResolverInputs est = ctx.Operations.Estimate(a, c, f);
            d.preparedness = est.preparedness;
            d.danger = Resolver.Danger(Resolver.Edge(est));
            float goods = Valuation.GoodsBasis(f, count);
            Doctrine doc = sim.doctrine;
            float appetite = 0.35f + 0.4f * (1f - doc.caution) + 0.1f * (sim.morale.confidence - 0.5f);
            switch (sim.morale.descriptor)
            {
                case MoraleDescriptor.Reckless: appetite += 0.3f; break;
                case MoraleDescriptor.Confident: appetite += 0.05f; break;
                case MoraleDescriptor.Cautious: appetite -= 0.08f; break;
                case MoraleDescriptor.Shaken: appetite -= 0.15f; break;
                case MoraleDescriptor.Desperate: appetite += 0.1f; break;
            }
            appetite += 0.1f * Math.Min(1f, c.request.premiumContribution / Math.Max(1f, goods));
            appetite += 0.1f * (rel.trust - 0.5f);
            appetite -= 0.2f * ctx.Contractors.WoundedShare(a);
            d.appetite = appetite;
            if (d.danger > appetite + Borderline)
            {
                d.reasonKeys.Add(RefusalReasons.TooDangerous);
            }
            else if (d.danger > appetite - Borderline)
            {
                d.coinUsed = true;
                NetRng coin = new NetRng(c.seed, "bid." + a.id.Value, c.biddingRound);
                if (!coin.Chance(0.5f)) d.reasonKeys.Add(RefusalReasons.TooDangerous);
            }
            else if (sim.morale.descriptor == MoraleDescriptor.Shaken && d.danger > 0.35f)
            {
                d.reasonKeys.Add(RefusalReasons.TooDangerous);
            }

            // Doctrine: generic signals only.
            if (f.isWeapon && doc.cruelty < 0.15f && doc.professionalism < 0.5f) d.reasonKeys.Add(RefusalReasons.AgainstDoctrine);
            else if (!f.tradeable && doc.discretion < 0.2f) d.reasonKeys.Add(RefusalReasons.AgainstDoctrine);

            // Political: bound to an origin faction that is hostile to the issuer.
            if (sim.origin != null && sim.origin.IsValid && !sim.originLost && doc.loyalty > 0.6f)
            {
                FactionFacts origin = ctx.Actors.FindFaction(sim.origin.loadId);
                if (origin != null && origin.hostileToPlayer && !origin.defeated) d.reasonKeys.Add(RefusalReasons.PoliticalRisk);
            }

            // Pay: a job too small for this outfit, even with the client's contribution.
            if (goods + c.request.premiumContribution < ContractorPricing.MinimumJob(a) * 0.35f) d.reasonKeys.Add(RefusalReasons.PayTooLow);

            // Capability and logistics.
            if (sim.equipment.tier < RequiredEquipmentTier(f.techLevel)) d.reasonKeys.Add(RefusalReasons.NoCapability);
            float diff = Valuation.Difficulty(f, count);
            int needRange = diff > 0.6f ? (int)Band.Medium : (diff > 0.3f ? (int)Band.Low : 0);
            if ((int)sim.mobility.rangeBand < needRange) d.reasonKeys.Add(RefusalReasons.OutOfRange);

            d.accept = d.reasonKeys.Count == 0;
            if (d.accept)
            {
                if (rel.trust < 0.4f) d.conditions.Add("CashUpFront");
                if (!f.tradeable) d.conditions.Add("NoQuestionsAsked");
                if (sim.commitments.Count > 0) d.conditions.Add("AfterCurrentJob");
                if (d.danger > 0.6f) d.conditions.Add("RiskAcknowledged");
            }
            return d;
        }

        /// <summary>Equipment tier (1..5) a contractor needs to handle goods of a tech level (0..7).</summary>
        public static int RequiredEquipmentTier(int techLevel)
        {
            if (techLevel >= 7) return 4;
            if (techLevel == 6) return 3;
            if (techLevel == 5) return 2;
            return 1;
        }
    }
}
