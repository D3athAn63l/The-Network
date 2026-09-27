using System;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Settings;

namespace TheNetwork.Domain.Intel
{
    /// <summary>
    /// Replaceable policy code for Intel sources (ARCHITECTURE § 6.13, § 10: "continuation policies,
    /// round durations" are intentionally replaceable). Profiles hold bands and keys; this class turns
    /// them into fees, durations, lead odds and continuation decisions. Nothing here reads a quantity:
    /// the only item inputs are unit-level facts (value, rarity signals).
    /// </summary>
    public static class SourcePolicies
    {
        public const string FeeStandard = "intel.fee.standard";
        public const string FeeDiscount = "intel.fee.discount";
        public const string FeeExchange = "intel.fee.exchange";
        public const string FeeFaction = "intel.fee.faction";

        /// <summary>Keeps searching after a lead without asking, until its round limit.</summary>
        public const string ContAuto = "intel.cont.auto";
        /// <summary>Asks after a lead; continuing is free.</summary>
        public const string ContAskFree = "intel.cont.askFree";
        /// <summary>Asks after a lead; continuing costs the original fee again.</summary>
        public const string ContAskFee = "intel.cont.askFee";
        /// <summary>Asks after a lead; continuing costs a reduced fee.</summary>
        public const string ContAskReduced = "intel.cont.askReduced";
        /// <summary>One lead ends the search.</summary>
        public const string ContSingle = "intel.cont.single";

        public const int CancelRefundPercent = 50;

        // ------------------------------------------------------------------ profiles

        public static IntelSourceProfile ForFixer(FixerTemplate t)
        {
            IntelSourceProfile p = new IntelSourceProfile
            {
                speedBand = t.speedBand,
                reliabilityBand = t.reliabilityBand,
                feeBand = t.feeBand,
                feePolicyKey = t.intelStyle == "Discount" ? FeeDiscount : FeeStandard,
                continuationPolicyKey = ContinuationForStyle(t.intelStyle),
                discretion = 0.5f
            };
            p.specialties.AddRange(t.specialties);
            return p;
        }

        public static string ContinuationForStyle(string intelStyle)
        {
            switch (intelStyle)
            {
                case "Patient": return ContAuto;
                case "Thorough": return ContAskFree;
                case "PayPerRound": return ContAskFee;
                case "Brisk":
                case "Discount": return ContAskReduced;
                default: return ContAskFree;
            }
        }

        public static IntelSourceProfile ForExchange()
        {
            return new IntelSourceProfile
            {
                speedBand = Band.Low,
                reliabilityBand = Band.Low,
                feeBand = Band.Low,
                feePolicyKey = FeeExchange,
                continuationPolicyKey = ContSingle,
                discretion = 0.2f
            };
        }

        /// <summary>A faction may be asked for Intel only if it is a live, non-hostile, humanlike peer.</summary>
        public static bool FactionCanBeContact(FactionFacts f, out string reasonKey)
        {
            reasonKey = null;
            if (f == null) reasonKey = "FactionMissing";
            else if (f.isPlayer) reasonKey = "FactionIsPlayer";
            else if (f.defeated) reasonKey = "FactionDefeated";
            else if (f.hidden) reasonKey = "FactionHidden";
            else if (f.temporary) reasonKey = "FactionTemporary";
            else if (!f.humanlike) reasonKey = "FactionNotHumanlike";
            else if (f.hostileToPlayer) reasonKey = "FactionHostile";
            return reasonKey == null;
        }

        /// <summary>
        /// A faction contact's profile is derived from master § 13 signals: tech level (reach and
        /// speed), goodwill (fee and candour) and geography (nearby factions know more).
        /// </summary>
        public static IntelSourceProfile ForFaction(FactionFacts f)
        {
            Band speed = f.techLevel >= 5 ? Band.High : (f.techLevel >= 4 ? Band.Medium : Band.Low);
            Band reliability = f.goodwill >= 60 ? Band.High : (f.goodwill >= 10 ? Band.Medium : Band.Low);
            if (f.nearestSettlementTiles >= 0 && f.nearestSettlementTiles <= 12 && reliability < Band.VeryHigh) reliability = reliability + 1;
            Band fee = f.goodwill >= 60 ? Band.Low : (f.goodwill >= 0 ? Band.Medium : Band.High);
            return new IntelSourceProfile
            {
                speedBand = speed,
                reliabilityBand = reliability,
                feeBand = fee,
                feePolicyKey = FeeFaction,
                continuationPolicyKey = ContAskFree,
                discretion = 0.4f,
                derived = true
            };
        }

        // ------------------------------------------------------------------ terms

        public static SearchTerms FreezeTerms(IntelSourceProfile p, ItemFacts item, string sourceName, string sourceKindKey)
        {
            SearchTerms t = new SearchTerms
            {
                feePolicyKey = p.feePolicyKey ?? FeeStandard,
                continuationPolicyKey = p.continuationPolicyKey ?? ContAskFree,
                speedBand = p.speedBand,
                reliabilityBand = p.reliabilityBand,
                feeBand = p.feeBand,
                sourceName = sourceName,
                sourceKindKey = sourceKindKey,
                cancelRefundPercent = CancelRefundPercent
            };
            t.initialFee = InitialFee(t.feePolicyKey, t.feeBand, item);
            t.continuationFee = ContinuationFee(t.continuationPolicyKey, t.initialFee);
            RoundLimits(t.continuationPolicyKey, out t.roundsPerSegment, out t.maxRounds);
            return t;
        }

        public static int InitialFee(string feePolicyKey, Band feeBand, ItemFacts item)
        {
            float unitValue = item == null ? 50f : Math.Max(1f, item.marketValue);
            float basis = 40f + (float)Math.Sqrt(unitValue) * 6f;
            if (item != null && item.unique) basis *= 1.5f;
            if (item != null && !item.tradeable && !item.craftable) basis *= 1.2f;
            float mult;
            switch (feeBand)
            {
                case Band.VeryLow: mult = 0.5f; break;
                case Band.Low: mult = 0.75f; break;
                case Band.High: mult = 1.4f; break;
                case Band.VeryHigh: mult = 2.0f; break;
                default: mult = 1f; break;
            }
            float fee = basis * mult;
            if (feePolicyKey == FeeDiscount) fee *= 0.8f;
            else if (feePolicyKey == FeeExchange) fee = 45f + basis * 0.3f;
            else if (feePolicyKey == FeeFaction) fee *= 0.9f;
            int rounded = (int)Math.Round(fee / 5f) * 5;
            return BandUtility.Clamp(rounded, 20, 900);
        }

        public static int ContinuationFee(string continuationPolicyKey, int initialFee)
        {
            if (continuationPolicyKey == ContAskFee) return initialFee;
            if (continuationPolicyKey == ContAskReduced) return Math.Max(10, (int)Math.Round(initialFee * 0.4f / 5f) * 5);
            return 0;
        }

        public static void RoundLimits(string continuationPolicyKey, out int roundsPerSegment, out int maxRounds)
        {
            switch (continuationPolicyKey)
            {
                case ContAuto: roundsPerSegment = 3; maxRounds = 12; break;
                case ContAskFee: roundsPerSegment = 3; maxRounds = 12; break;
                case ContAskReduced: roundsPerSegment = 2; maxRounds = 10; break;
                case ContSingle: roundsPerSegment = 3; maxRounds = 3; break;
                default: roundsPerSegment = 4; maxRounds = 16; break;
            }
        }

        // ------------------------------------------------------------------ rounds

        /// <summary>Round duration from the frozen terms, the topic's rarity signals and seeded variance.</summary>
        public static int RoundDurationTicks(SearchTerms terms, ItemFacts item, int seed, int round)
        {
            float days;
            switch (terms.speedBand)
            {
                case Band.VeryLow: days = 6f; break;
                case Band.Low: days = 4.5f; break;
                case Band.High: days = 2.5f; break;
                case Band.VeryHigh: days = 1.75f; break;
                default: days = 3.5f; break;
            }
            if (item != null)
            {
                if (item.unique) days *= 1.5f;
                else if (!item.tradeable && !item.craftable) days *= 1.3f;
                if (!item.isLudeon) days *= 1.05f;
            }
            NetRng rng = new NetRng(seed, "intel.duration", round);
            days *= rng.Range(0.75f, 1.3f);
            if (days < 0.5f) days = 0.5f;
            return (int)(days * Ticks.PerDay);
        }

        /// <summary>A reliability band as a 0..1 number (internal only).</summary>
        public static float Reliability(Band band)
        {
            switch (band)
            {
                case Band.VeryLow: return 0.3f;
                case Band.Low: return 0.45f;
                case Band.High: return 0.75f;
                case Band.VeryHigh: return 0.88f;
                default: return 0.6f;
            }
        }

        public static float LeadChance(SearchTerms terms, ItemFacts item, int round)
        {
            float p = 0.35f + 0.35f * Reliability(terms.reliabilityBand);
            if (item != null)
            {
                if (item.unique) p *= 0.5f;
                if (item.tradeable) p *= 1.1f;
                if (item.craftable) p *= 1.05f;
            }
            if (round > 1) p *= (float)Math.Pow(0.93, round - 1);
            return p < 0.15f ? 0.15f : (p > 0.85f ? 0.85f : p);
        }

        /// <summary>Hidden divergence class, drawn from the source's reliability (Phase 1 subset).</summary>
        public static LeadDivergence DrawDivergence(NetRng rng, float reliability)
        {
            float[] w =
            {
                60f * reliability,              // Accurate
                18f,                            // Partial
                12f * (1.2f - reliability),     // Outdated
                10f * (1.2f - reliability),     // Bad
                5f,                             // Jackpot
                8f,                             // Complication
                6f * (1.1f - reliability)       // Trap
            };
            switch (rng.WeightedIndex(w))
            {
                case 0: return LeadDivergence.Accurate;
                case 1: return LeadDivergence.Partial;
                case 2: return LeadDivergence.Outdated;
                case 3: return LeadDivergence.Bad;
                case 4: return LeadDivergence.Jackpot;
                case 5: return LeadDivergence.Complication;
                default: return LeadDivergence.Trap;
            }
        }

        /// <summary>The confidence a source expresses: from its reliability plus seeded noise, never from the truth.</summary>
        public static ConfidenceBand ConfidenceFor(Band reliabilityBand, NetRng rng)
        {
            int v = (int)reliabilityBand;
            float roll = rng.Value();
            if (roll < 0.2f) v--;
            else if (roll > 0.8f) v++;
            return (ConfidenceBand)BandUtility.Clamp(v, 0, 4);
        }

        /// <summary>What happens after a round (STATE_MACHINES § 1): next round, ask the player, or end.</summary>
        public static RoundFollowUp AfterRound(SearchTerms terms, IntelRequest r, bool leadThisRound)
        {
            if (r.round >= terms.maxRounds) return RoundFollowUp.ConcludeRoundLimit;
            if (leadThisRound)
            {
                switch (terms.continuationPolicyKey)
                {
                    case ContAuto: return RoundFollowUp.NextRoundNewSegment;
                    case ContSingle: return RoundFollowUp.ConcludeAfterLead;
                    default: return RoundFollowUp.AskPlayer;
                }
            }
            int roundsInSegment = r.round - r.segmentStartRound + 1;
            if (roundsInSegment < terms.roundsPerSegment) return RoundFollowUp.NextRound;
            return r.leads.Count == 0 ? RoundFollowUp.ConcludeNothingCredible : RoundFollowUp.ConcludeExhausted;
        }
    }

    public enum RoundFollowUp : byte
    {
        NextRound,
        NextRoundNewSegment,
        AskPlayer,
        ConcludeAfterLead,
        ConcludeRoundLimit,
        ConcludeNothingCredible,
        ConcludeExhausted
    }
}
