using System;
using System.Collections.Generic;
using RimWorld;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Knowledge;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Domain.Operations
{
    /// <summary>A person taking part, as the resolver sees them (record data only, never a pawn).</summary>
    public struct Participant
    {
        public CharacterId id;
        public bool leader;
        public float exposure;
    }

    /// <summary>
    /// The frozen-input abstract resolver (SIMULATION § 3). Pure: the same inputs and the same NetRng
    /// stream give the same outcome, so a reload never rerolls. It is not a combat simulation: it
    /// produces a coarse band and bounded, rounded results per tier.
    /// </summary>
    public static class Resolver
    {
        // Band thresholds (SIMULATION § 3.3, a tunable data table).
        public const float TriumphAt = 1.6f;
        public const float SuccessAt = 0.6f;
        public const float CostlyAt = 0f;
        public const float PartialAt = -0.6f;
        public const float FailureAt = -1.4f;

        public static float Edge(ResolverInputs i)
        {
            float ratio = i.forcePower / Math.Max(1f, i.threatPower);
            return (float)Math.Log(Math.Max(0.01f, ratio), 2)
                + 0.8f * (i.preparedness - 0.5f)
                + 0.4f * (i.intelQuality - 0.5f)
                + 0.3f * (i.moraleFactor - 1f)
                - i.logisticsPenalty
                + i.sponsorship
                + 0.15f * i.leadership
                + 0.2f * i.specialization;
        }

        /// <summary>The estimated danger (0..1) for willingness and pricing. Reads no randomness.</summary>
        public static float Danger(float edge)
        {
            return ContractorService.Clamp(0.5f - 0.3f * edge, 0f, 1f);
        }

        public static float Variance(ResolverInputs i)
        {
            return (i.reckless ? 1.4f : 1f) * (1.2f - 0.4f * i.professionalism);
        }

        public static OutcomeBand Lookup(float roll)
        {
            if (roll >= TriumphAt) return OutcomeBand.Triumph;
            if (roll >= SuccessAt) return OutcomeBand.Success;
            if (roll >= CostlyAt) return OutcomeBand.CostlySuccess;
            if (roll >= PartialAt) return OutcomeBand.Partial;
            if (roll >= FailureAt) return OutcomeBand.Failure;
            return OutcomeBand.Disaster;
        }

        // Per-band ranges: secured share, KIA share, wounded share, capture/missing chance, delay share.
        private static readonly float[] SecuredMin = { 1f, 0.9f, 0.7f, 0.2f, 0f, 0f };
        private static readonly float[] SecuredMax = { 1f, 1f, 1f, 0.7f, 0.2f, 0f };
        private static readonly float[] KiaMin = { 0f, 0f, 0.1f, 0.1f, 0.2f, 0.4f };
        private static readonly float[] KiaMax = { 0.05f, 0.1f, 0.25f, 0.3f, 0.5f, 0.9f };
        private static readonly float[] WoundMin = { 0f, 0.05f, 0.15f, 0.2f, 0.2f, 1f };
        private static readonly float[] WoundMax = { 0.1f, 0.2f, 0.35f, 0.4f, 0.4f, 1f };
        private static readonly float[] CaptureChance = { 0f, 0f, 0.05f, 0.1f, 0.25f, 0.5f };
        private static readonly float[] DelayMin = { -0.1f, 0f, 0.1f, 0.2f, 0.3f, 0.3f };
        private static readonly float[] DelayMax = { 0f, 0.1f, 0.3f, 0.5f, 0.8f, 0.8f };

        private static readonly string[] DisasterFlavors = { "market.robbed", "market.cheated", "market.ambushed", "market.seized" };
        private static readonly string[] FailureFlavors = { "market.dryWell", "market.outbid", "market.turnedBack" };

        /// <summary>
        /// Resolves once. <paramref name="forces"/> and <paramref name="people"/> are what was committed;
        /// a Solo is one participant and no tier counts.
        /// </summary>
        public static OperationOutcome Resolve(ResolverInputs inp, List<TierCount> forces, List<Participant> people, int requested, int plannedTicks, NetRng rng, OutcomeBand? forced = null)
        {
            OperationOutcome o = new OperationOutcome { requested = requested };
            o.edge = Edge(inp);
            o.roll = o.edge + Variance(inp) * rng.NormalLike();
            OutcomeBand band = forced ?? Lookup(o.roll);
            OutcomeBand cargoBand = band;
            // A cautious crew breaks off when it goes badly: fewer deaths, less cargo (SIMULATION § 3.3).
            if (forced == null && inp.caution >= 0.65f && band >= OutcomeBand.Partial && band > OutcomeBand.Triumph)
            {
                o.retreated = true;
                band = band - 1;
            }
            o.band = band;
            int b = (int)band, cb = (int)cargoBand;

            // Cargo secured (randomized rounding keeps small orders fair: no fractional items).
            float share = rng.Range(SecuredMin[cb], SecuredMax[cb]);
            if (o.retreated) share *= 0.8f;
            if (band == OutcomeBand.Triumph) share = 1f;
            float exact = requested * share;
            int secured = (int)Math.Floor(exact);
            if (secured < requested && rng.Chance(exact - secured)) secured++;
            o.secured = Math.Max(0, Math.Min(requested, secured));

            // Market work is mostly not a firefight: casualties scale with how hard the goods are to get.
            float scale = inp.market ? 0.35f + 0.65f * inp.difficulty : 1f;
            for (int i = 0; i < forces.Count; i++)
            {
                TierCount t = forces[i];
                int n = t.healthy;
                if (n <= 0) continue;
                int killed = Round(n * rng.Range(KiaMin[b], KiaMax[b]) * scale, rng);
                killed = Math.Min(n, killed);
                int left = n - killed;
                int lost = 0;
                if (left > 0 && rng.Chance(CaptureChance[b] * scale))
                {
                    lost = Math.Min(left, rng.RangeInclusive(1, Math.Max(1, left / 5)));
                    left -= lost;
                }
                int wounded = band == OutcomeBand.Disaster ? left : Math.Min(left, Round(left * rng.Range(WoundMin[b], WoundMax[b]) * Math.Max(scale, 0.5f), rng));
                if (killed > 0) o.killed.Add(new TierCount(t.tier, killed));
                if (wounded > 0) o.wounded.Add(new TierCount(t.tier, wounded));
                if (lost > 0)
                {
                    if (rng.Chance(0.5f)) o.captured.Add(new TierCount(t.tier, lost));
                    else o.missing.Add(new TierCount(t.tier, lost));
                }
            }

            // Known Characters: a fate draw each, by band and role exposure. No plot armor.
            float kia = (KiaMin[b] + KiaMax[b]) * 0.5f * scale;
            float cap = CaptureChance[b] * scale * 0.5f;
            float wnd = (WoundMin[b] + WoundMax[b]) * 0.5f * Math.Max(scale, 0.5f);
            for (int i = 0; i < people.Count; i++)
            {
                Participant p = people[i];
                float e = p.exposure <= 0f ? 1f : p.exposure;
                float u = rng.Value();
                Fate f;
                if (u < kia * e) f = Fate.Killed;
                else if (u < (kia + cap) * e) f = rng.Chance(0.5f) ? Fate.Captured : Fate.Missing;
                else if (u < (kia + cap) * e + wnd) f = Fate.Wounded;
                else f = Fate.Unharmed;
                o.fates.Add(new CharacterFate { character = p.id, fate = f });
            }

            o.woundDays = rng.RangeInclusive(5, 14) + (band >= OutcomeBand.Failure ? 4 : 0);
            o.delayTicks = band == OutcomeBand.Disaster ? 0 : (int)(plannedTicks * rng.Range(DelayMin[b], DelayMax[b]));

            // Troubled: people who cannot come home on their own (STATE_MACHINES § 6).
            o.troubledKey = TroubledKey(o, forces, people, band, rng);

            if (band == OutcomeBand.Disaster) o.flavorKey = inp.market ? DisasterFlavors[rng.Range(0, DisasterFlavors.Length)] : "site.overrun";
            else if (band == OutcomeBand.Failure) o.flavorKey = inp.market ? FailureFlavors[rng.Range(0, FailureFlavors.Length)] : "site.repelled";
            else if (o.retreated) o.flavorKey = "retreated";
            else o.flavorKey = band <= OutcomeBand.Success ? "clean" : "hard";

            return o;
        }

        private static string TroubledKey(OperationOutcome o, List<TierCount> forces, List<Participant> people, OutcomeBand band, NetRng rng)
        {
            int committed = 0;
            for (int i = 0; i < forces.Count; i++) committed += forces[i].healthy;
            committed += people.Count;
            int captured = o.Captured, missing = o.Missing, killed = o.Killed;
            bool leaderGone = false;
            for (int i = 0; i < o.fates.Count; i++)
            {
                CharacterFate f = o.fates[i];
                if (f.fate != Fate.Captured && f.fate != Fate.Missing) continue;
                for (int k = 0; k < people.Count; k++) if (people[k].id == f.character && (people[k].leader || people.Count == 1)) leaderGone = true;
            }
            if (committed > 0 && (leaderGone || (captured + missing) * 2 >= committed - killed && captured + missing > 0))
            {
                return captured > missing ? "Captured" : "Missing";
            }
            // A disaster far from home can leave the survivors without a way back.
            if (band == OutcomeBand.Disaster && committed - killed - captured - missing > 0 && rng.Chance(0.25f)) return "Stranded";
            return null;
        }

        private static int Round(float x, NetRng rng)
        {
            int n = (int)Math.Floor(x);
            if (rng.Chance(x - n)) n++;
            return n;
        }

        /// <summary>Knowledge from what was actually done: even failures teach, Disasters the most (if anyone survived).</summary>
        public static List<TopicGain> Gains(OutcomeBand band, IList<string> topics)
        {
            float g;
            switch (band)
            {
                case OutcomeBand.Triumph: g = 1.2f; break;
                case OutcomeBand.Success: g = 1f; break;
                case OutcomeBand.CostlySuccess: g = 1.1f; break;
                case OutcomeBand.Partial: g = 1.2f; break;
                case OutcomeBand.Failure: g = 1.4f; break;
                default: g = 1.8f; break;
            }
            List<TopicGain> gains = new List<TopicGain>();
            for (int i = 0; i < topics.Count; i++) gains.Add(new TopicGain { topic = topics[i], amount = i == 0 ? g : g * 0.5f });
            return gains;
        }

        /// <summary>Is the band a success from the contractor's point of view (morale, skill)?</summary>
        public static bool IsSuccess(OutcomeBand b) => b <= OutcomeBand.CostlySuccess;
    }
}
