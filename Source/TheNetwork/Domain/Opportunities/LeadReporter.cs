using System;
using TheNetwork.Domain.Intel;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;
using RimWorld;

namespace TheNetwork.Domain.Opportunities
{
    /// <summary>
    /// Turns the truth into what the source reports, through the hidden divergence (master § 12).
    /// Confidence comes from the source's reliability, never from the divergence, so a confident lead
    /// can still be wrong. Pure and seeded.
    /// </summary>
    public static class LeadReporter
    {
        public static LeadReport Report(Opportunity opp, OpportunityDraft draft, LeadDivergence divergence, Band reliabilityBand, int seed, string holderText)
        {
            NetRng rng = new NetRng(seed, "lead.report");
            LeadReport r = new LeadReport
            {
                archetypeKey = opp.archetypeKey,
                location = opp.location?.Copy(),
                holderText = holderText,
                sourceKindKey = opp.sourceContext.kind.ToString(),
                confidence = SourcePolicies.ConfidenceFor(reliabilityBand, rng)
            };

            // Amount: what the source believes is there.
            int believed;
            switch (divergence)
            {
                case LeadDivergence.Outdated:
                case LeadDivergence.Bad:
                case LeadDivergence.Jackpot:
                    believed = Math.Max(1, draft.nominalCount);
                    break;
                case LeadDivergence.Partial:
                    believed = Math.Max(1, (int)Math.Round(Math.Max(1, draft.targetCount) * (rng.Chance(0.5f) ? rng.Range(0.4f, 0.6f) : rng.Range(1.6f, 2.2f))));
                    break;
                default:
                    believed = Math.Max(1, draft.targetCount);
                    break;
            }
            float spread = Spread(r.confidence);
            r.estimateLow = Math.Max(1, (int)Math.Floor(believed * (1f - spread)));
            r.estimateHigh = Math.Max(r.estimateLow, (int)Math.Ceiling(believed * (1f + spread)));

            // Threat: what the source believes guards it.
            ThreatBand truth = opp.threat.profileKey == ThreatProfiles.None ? ThreatBand.Negligible : BandUtility.ThreatBandFor(opp.threat.points);
            ThreatBand reported = truth;
            switch (divergence)
            {
                case LeadDivergence.Complication:
                    reported = BandUtility.ThreatBandFor(opp.threat.points / 2f);
                    break;
                case LeadDivergence.Trap:
                    reported = rng.Chance(0.6f) ? ThreatBand.Negligible : ThreatBand.Light;
                    break;
                case LeadDivergence.Partial:
                    reported = BandUtility.Shift(truth, rng.Chance(0.5f) ? -1 : 1);
                    break;
            }
            if (r.confidence == ConfidenceBand.VeryLow && rng.Chance(0.35f)) reported = ThreatBand.Unknown;
            r.threatBand = reported;

            // Other cargo: some of it is mentioned, some is a surprise.
            for (int i = 0; i < opp.payload.Count; i++)
            {
                ItemPayload p = opp.payload[i] as ItemPayload;
                if (p == null || p.role != PayloadRole.Extra) continue;
                if (rng.Chance(0.7f))
                {
                    int lo = Math.Max(1, (int)Math.Floor(p.count * (1f - spread)));
                    r.otherCargo.Add(new ReportedCargo { thing = p.thing?.Copy(), low = lo, high = Math.Max(lo, (int)Math.Ceiling(p.count * (1f + spread))) });
                }
                else
                {
                    r.unknownExtra = true;
                }
            }

            // The operational window, as the source estimates it.
            int jitter = (int)(Ticks.PerDay * rng.Range(-1.5f, 1.5f));
            r.expiresAroundTick = opp.expiresTick + jitter;
            return r;
        }

        public static float Spread(ConfidenceBand c)
        {
            switch (c)
            {
                case ConfidenceBand.VeryHigh: return 0.1f;
                case ConfidenceBand.High: return 0.2f;
                case ConfidenceBand.Moderate: return 0.35f;
                case ConfidenceBand.Low: return 0.5f;
                default: return 0.7f;
            }
        }

        /// <summary>Did the report hold up? Used for the source's track record once the player has seen the site.</summary>
        public static bool ReportHeld(LeadDivergence divergence)
        {
            return divergence == LeadDivergence.Accurate || divergence == LeadDivergence.Jackpot;
        }
    }
}
