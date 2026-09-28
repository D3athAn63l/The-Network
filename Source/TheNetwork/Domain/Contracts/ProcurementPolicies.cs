using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Domain.Contracts
{
    // Replaceable procurement policy (SIMULATION § 5.3, COMPATIBILITY § 2.4). Every number here is
    // tuning, not a rule of the data model. Pure functions of plain facts: headless-testable, and never
    // named after a mod or an item.

    /// <summary>
    /// Sanity valuation and rarity signals. Market value is one input, never the whole balance: missing
    /// or implausibly low values are raised by fallbacks (category median, recipe input value), and the
    /// final price can never fall below what buying the goods would cost.
    /// </summary>
    public static class Valuation
    {
        /// <summary>Buying from a trader costs at least this multiple of market value; procurement never undercuts it.</summary>
        public const float TraderMarkup = 1.4f;

        public const float MaxUnitValue = 1000000f;
        public const int MaxPrice = 100000000;

        public static float UnitValue(ItemFacts f)
        {
            float mv = Sane(f.marketValue);
            float input = Sane(f.recipeInputValue);
            float median = Sane(f.categoryMedian);
            float v = mv;
            if (v <= 0f) v = input > 0f ? input * 1.3f : (median > 0f ? median : 10f);
            // Worth less than what goes into it: the market value is broken low.
            if (input > 0f && v < input * 1.1f) v = input * 1.1f;
            // A hard-to-source item priced like scrap: lift it toward its category.
            if (median > 0f && (f.unique || !f.tradeable) && v < median * 0.1f) v = median * 0.1f;
            return Math.Min(MaxUnitValue, Math.Max(0.5f, v));
        }

        private static float Sane(float x)
        {
            return float.IsNaN(x) || float.IsInfinity(x) || x < 0f ? 0f : x;
        }

        public static int GoodsBasis(ItemFacts f, int count)
        {
            return Clamp(Math.Ceiling(UnitValue(f) * Math.Max(1, count)));
        }

        /// <summary>What buying the goods would cost: the quote never goes below this.</summary>
        public static int MarketFloor(ItemFacts f, int count)
        {
            float unit = Math.Max(Math.Min(MaxUnitValue, Sane(f.marketValue)), UnitValue(f));
            return Clamp(Math.Ceiling(unit * Math.Max(1, count) * TraderMarkup));
        }

        /// <summary>
        /// Sourcing difficulty, 0..1, from generic rarity signals only: uniqueness, tradeability,
        /// craftability, tech level, unit value and volume.
        /// </summary>
        public static float Difficulty(ItemFacts f, int count)
        {
            float d = 0f;
            if (f.unique) d += 0.5f;
            if (!f.tradeable) d += 0.2f;
            if (!f.craftable && !f.mineable) d += 0.1f;
            if (f.techLevel >= 7) d += 0.35f;
            else if (f.techLevel == 6) d += 0.2f;
            else if (f.techLevel == 5) d += 0.1f;
            float unit = UnitValue(f);
            if (unit >= 2000f) d += 0.2f;
            else if (unit >= 500f) d += 0.1f;
            float stacks = Math.Max(1, count) / (float)Math.Max(1, f.stackLimit);
            d += Math.Min(0.15f, 0.04f * (float)Math.Log(1f + stacks, 2));
            if (f.unique && count > 1) d += 0.1f * (float)Math.Log(count, 2);
            return ContractorService.Clamp(d, 0f, 1f);
        }

        /// <summary>
        /// Market threat (SIMULATION § 3.6): no site, so danger comes from rarity and value. Disasters
        /// here mean being robbed, cheated or ambushed.
        /// </summary>
        public static float MarketThreat(ItemFacts f, int count)
        {
            float diff = Difficulty(f, count);
            float goods = GoodsBasis(f, count);
            return 0.6f + 1.2f * (float)Math.Log10(1f + goods / 300f) + 30f * diff * diff;
        }

        /// <summary>The largest order a contract may carry (stacks × stack limit).</summary>
        public static int MaxQuantity(ItemFacts f, ContractKindRules rules)
        {
            int stacks = rules?.maxQuantityStacks ?? 40;
            return f.unique ? 3 : Math.Max(1, Math.Min(100000, Math.Max(1, f.stackLimit) * stacks));
        }

        public static int Clamp(double v)
        {
            if (double.IsNaN(v) || v <= 0) return 0;
            return v >= MaxPrice ? MaxPrice : (int)v;
        }

        /// <summary>Knowledge topics for a procurement: the item first (weighted double), then the market.</summary>
        public static List<string> Topics(ItemFacts f)
        {
            return new List<string> { Knowledge.Topics.Thing(f.defName), Knowledge.Topics.Market };
        }

        /// <summary>How well a contractor's specialties fit the item (generic signals), 0..1.</summary>
        public static float Specialization(ContractorProfile p, ItemFacts f)
        {
            if (p == null) return 0f;
            List<string> wanted = new List<string>();
            if (f.isWeapon || f.isApparel) wanted.Add("combat acquisition");
            if (f.techLevel >= 5) wanted.Add("tech retrieval");
            if (!f.tradeable || f.unique) { wanted.Add("infiltration"); wanted.Add("extraction"); }
            if (f.isResource || f.mineable) { wanted.Add("logistics"); wanted.Add("salvage"); }
            if (wanted.Count == 0) { wanted.Add("logistics"); wanted.Add("recovery"); }
            int hits = 0;
            for (int i = 0; i < wanted.Count; i++) if (p.specialties.Contains(wanted[i])) hits++;
            return Math.Min(1f, hits / (float)Math.Min(2, wanted.Count));
        }
    }

    /// <summary>
    /// The brokering Fixer's side (SIMULATION § 5.3): fee, coordination, market access, contingency,
    /// deposit share, insurance offer, quote validity, reach, and the cancellation and replacement rules.
    /// Driven by the FixerProfile policy keys frozen at import.
    /// </summary>
    public static class FixerPolicies
    {
        public static string Style(string policyKey, string fallback)
        {
            if (string.IsNullOrEmpty(policyKey)) return fallback;
            int dot = policyKey.LastIndexOf('.');
            return dot >= 0 && dot < policyKey.Length - 1 ? policyKey.Substring(dot + 1) : policyKey;
        }

        public static string Brokerage(FixerProfile fp) => Style(fp?.brokeragePolicyKey, "Standard");

        public static string InsuranceStyle(FixerProfile fp) => fp?.insurancePolicyKey == null ? null : Style(fp.insurancePolicyKey, null);

        public static float FeeShare(Band feeBand)
        {
            switch (feeBand)
            {
                case Band.VeryLow: return 0.03f;
                case Band.Low: return 0.05f;
                case Band.High: return 0.11f;
                case Band.VeryHigh: return 0.15f;
                default: return 0.08f;
            }
        }

        public static float CoordinationShare(string brokerage)
        {
            switch (brokerage)
            {
                case "Lean": return 0.02f;
                case "Premium": return 0.07f;
                default: return 0.04f;
            }
        }

        /// <summary>Wide market access finds the goods cheaper; a narrow one costs extra.</summary>
        public static float MarketAccessShare(ReachBand access)
        {
            switch (access)
            {
                case ReachBand.Minimal: return 0.04f;
                case ReachBand.Local: return 0.02f;
                case ReachBand.Wide: return -0.02f;
                case ReachBand.Vast: return -0.03f;
                default: return 0f;
            }
        }

        public static float ContingencyShare(float danger) => 0.03f + 0.05f * danger;

        /// <summary>
        /// The deposit share from the Fixer's brokerage style (about half, as a starting point, never a
        /// law), raised for dangerous work and for clients the contractor does not trust.
        /// </summary>
        public static float DepositShare(string brokerage, float danger, float contractorTrust)
        {
            float b = brokerage == "Lean" ? 0.6f : (brokerage == "Premium" ? 0.4f : 0.5f);
            b += 0.1f * danger - 0.2f * (contractorTrust - 0.5f);
            return ContractorService.Clamp(b, 0.3f, 0.7f);
        }

        /// <summary>The optional cover, or null when this Fixer offers none. Coverage is always below 1.</summary>
        public static Insurance InsuranceOffer(FixerProfile fp, int deposit, float danger)
        {
            string style = InsuranceStyle(fp);
            if (style == null || deposit <= 0) return null;
            Insurance ins = new Insurance { policyKey = fp.insurancePolicyKey };
            ins.covers.Add(Causes.OperationFailed);
            ins.covers.Add(Causes.CatastrophicLoss);
            ins.covers.Add(Causes.ContractorLost);
            if (style == "Generous")
            {
                ins.coverage = 0.65f;
                ins.premium = Math.Max(1, (int)Math.Ceiling(deposit * (0.16f + 0.35f * danger)));
                ins.covers.Add(Causes.PartialShortfall);
                ins.covers.Add(Causes.PreWorkLoss);
            }
            else
            {
                ins.coverage = 0.4f;
                ins.premium = Math.Max(1, (int)Math.Ceiling(deposit * (0.1f + 0.25f * danger)));
            }
            return ins;
        }

        /// <summary>Quote validity (quote policy "Standard"): three to six days.</summary>
        public static int ValidityTicks(NetRng rng)
        {
            return Ticks.PerDay * 3 + (int)(rng.Value() * Ticks.PerDay * 3);
        }

        /// <summary>How many contractors the Fixer can reach for one contract (SIMULATION § 5.2, capped at 12).</summary>
        public static int CandidateCap(ReachBand reach)
        {
            switch (reach)
            {
                case ReachBand.Minimal: return 4;
                case ReachBand.Local: return 6;
                case ReachBand.Regional: return 8;
                case ReachBand.Wide: return 10;
                default: return 12;
            }
        }

        public static string RefundPolicyKey(string brokerage) => "refund." + brokerage;

        /// <summary>
        /// Share of the deposit (and premium) returned when the issuer cancels after award. Before the
        /// operation reaches Engaged only; afterwards cancellation is not a player command.
        /// </summary>
        public static float CancelRefundShare(string refundPolicyKey, bool preparing)
        {
            string style = Style(refundPolicyKey, "Standard");
            if (!preparing) return style == "Premium" ? 0.1f : 0f;
            switch (style)
            {
                case "Lean": return 0.1f;
                case "Premium": return 0.4f;
                default: return 0.25f;
            }
        }

        /// <summary>
        /// What the Fixer does when the contractor disappears before the work starts (STATE_MACHINES § 4.2):
        /// no universal rule. Premium and Standard brokers look for a replacement first; if none, they refund
        /// all or half of the deposit. Lean brokers refund a quarter and leave the rest to insurance.
        /// </summary>
        public static void Replacement(string replacementPolicyKey, string brokerage, out bool tryReplacement, out float refundShare)
        {
            switch (brokerage)
            {
                case "Premium":
                    tryReplacement = true;
                    refundShare = 1f;
                    break;
                case "Lean":
                    tryReplacement = false;
                    refundShare = 0.25f;
                    break;
                default:
                    tryReplacement = true;
                    refundShare = 0.5f;
                    break;
            }
        }
    }

    /// <summary>Cause keys (persisted strings).</summary>
    public static class Causes
    {
        public const string OperationFailed = "OperationFailed";
        public const string CatastrophicLoss = "CatastrophicLoss";
        public const string ContractorLost = "ContractorLost";
        public const string PartialShortfall = "PartialShortfall";
        public const string PreWorkLoss = "PreWorkLoss";
        public const string ContractorWalked = "ContractorWalked";
        public const string UndeliverableNoHome = "UndeliverableNoHome";
        public const string DefMissing = "DefMissing";
        public const string ItemCannotBeProduced = "ItemCannotBeProduced";
        public const string KindMissing = "KindMissing";
        public const string BrokerGone = "BrokerGone";
        public const string PreparingForRemoval = "PreparingForRemoval";
        public const string IssuerCancelled = "IssuerCancelled";
        public const string IssuerCancelledAfterAward = "IssuerCancelledAfterAward";
        public const string IssuerCancelledDuringRenegotiation = "IssuerCancelledDuringRenegotiation";
        public const string NoOffers = "NoOffers";
        public const string PartialAccepted = "PartialAccepted";
        public const string PartialHandover = "PartialHandover";
        public const string ReducedScope = "ReducedScope";
    }

    /// <summary>The contractor's own bid contribution (SIMULATION § 5.3), kept apart from the Fixer's.</summary>
    public static class ContractorPricing
    {
        /// <summary>The smallest job worth this contractor's time (form × fame).</summary>
        public static int MinimumJob(NetworkActor a)
        {
            OrganizationProfile org = a.Get<OrganizationProfile>();
            int people = org == null ? 1 : org.Healthy + org.Wounded + org.Committed + org.knownMembers.Count;
            int b = people <= 1 ? 80 : (people <= 2 ? 120 : (people <= 5 ? 200 : (people <= 10 ? 350 : 600)));
            return (int)(b * (1f + 0.3f * (int)a.reputation.fame));
        }

        public static float SpeedFactor(Band speed)
        {
            switch (speed)
            {
                case Band.VeryLow: return 1.3f;
                case Band.Low: return 1.15f;
                case Band.High: return 0.9f;
                case Band.VeryHigh: return 0.8f;
                default: return 1f;
            }
        }

        public static float LogisticsPenalty(MobilityProfile m, float difficulty)
        {
            float range = m == null ? 0.2f : Math.Max(0f, 0.2f - 0.05f * (int)m.rangeBand);
            return range + 0.1f * difficulty;
        }

        /// <summary>Contractor components: goods, sourcing, risk, capability, logistics, urgency, profit.</summary>
        public static List<QuoteComponent> Bid(NetworkActor a, ItemFacts f, int count, float danger, string relationDescriptor, out int etaTicks, NetRng rng)
        {
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            Doctrine d = sim.doctrine;
            float diff = Valuation.Difficulty(f, count);
            int goods = Valuation.GoodsBasis(f, count);
            List<QuoteComponent> c = new List<QuoteComponent>();
            Add(c, QuoteComponentKind.GoodsBasis, goods, a.id, "GoodsBasis");
            Add(c, QuoteComponentKind.Acquisition, goods * (0.6f * diff + (f.unique ? 1.5f : 0f)), a.id, f.unique ? "UniqueItem" : (diff > 0.3f ? "HardToSource" : "Sourcing"));
            Add(c, QuoteComponentKind.Risk, goods * danger * (0.3f + 0.4f * d.greed) + 30f * danger, a.id, danger > 0.6f ? "HighRisk" : "Risk");
            int exp = (int)ContractorService.Experience(a);
            Add(c, QuoteComponentKind.Capability, goods * (0.03f * exp + 0.04f * (int)a.reputation.fame), a.id, "Reputation");
            int range = (int)sim.mobility.rangeBand;
            Add(c, QuoteComponentKind.Logistics, (50f + 0.04f * goods) * (1.4f - 0.1f * range), a.id, "Transport");
            if (sim.commitments.Count > 0) Add(c, QuoteComponentKind.Urgency, goods * 0.05f * sim.commitments.Count, a.id, "Workload");
            float margin = 0.08f + 0.17f * d.greed;
            switch (sim.morale.descriptor)
            {
                case MoraleDescriptor.Desperate: margin *= 0.7f; break;
                case MoraleDescriptor.Shaken: margin *= 1.1f; break;
                case MoraleDescriptor.Confident: margin *= 1.05f; break;
            }
            switch (relationDescriptor)
            {
                case "Trusted": margin *= 0.9f; break;
                case "Friendly": margin *= 0.95f; break;
                case "Rival": margin *= 1.15f; break;
                case "BitterRival": margin *= 1.3f; break;
            }
            float basis = 0f;
            for (int i = 0; i < c.Count; i++) basis += c[i].amount;
            int profit = Valuation.Clamp(basis * margin);
            int total = Sum(c) + profit;
            int minimum = MinimumJob(a);
            if (total < minimum) profit += minimum - total;
            Add(c, QuoteComponentKind.Profit, profit, a.id, total < minimum ? "MinimumJob" : "Margin");

            float days = 2f + 6f * diff + 1.5f * (float)Math.Log10(1f + goods / 500f);
            days *= SpeedFactor(sim.mobility.speedBand) * (1f + 0.25f * sim.commitments.Count) * (1f + 0.1f * (2 - range));
            days *= rng.Range(0.9f, 1.1f);
            etaTicks = (int)(ContractorService.Clamp(days, 2f, 30f) * Ticks.PerDay);
            return c;
        }

        public static int Sum(List<QuoteComponent> c)
        {
            long s = 0;
            for (int i = 0; i < c.Count; i++) s += c[i].amount;
            return s >= Valuation.MaxPrice ? Valuation.MaxPrice : (int)Math.Max(0, s);
        }

        public static void Add(List<QuoteComponent> c, QuoteComponentKind kind, float amount, ActorId by, string reasonKey)
        {
            int v = amount >= 0f ? Valuation.Clamp(Math.Round(amount)) : -Valuation.Clamp(Math.Round(-amount));
            if (v == 0) return;
            QuoteComponent q = new QuoteComponent { kind = kind, amount = v, contributedBy = by };
            if (reasonKey != null) q.reasonKeys.Add(reasonKey);
            c.Add(q);
        }
    }
}
