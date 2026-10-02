using System;
using System.Collections.Generic;
using System.Text;
using TheNetwork.Domain.Actors;
using TheNetwork.Persist;

namespace TheNetwork.Domain.Contractors
{
    /// <summary>
    /// A read of how careers are spread across the active contractors (dev dump and soak report): fame and
    /// experience bands, equipment tiers, needs, Tags, funds and reputation extremes, upgrades so far. It reads
    /// state only and never changes anything.
    /// </summary>
    public sealed class CareerDistribution
    {
        public int active;
        public readonly int[] fame = new int[5];
        public readonly int[] experience = new int[6];
        public readonly int[] tiers = new int[CareerPolicy.MaxTier + 1];
        public readonly Dictionary<CareerNeed, int> needs = new Dictionary<CareerNeed, int>();
        public readonly Dictionary<string, int> tags = new Dictionary<string, int>();
        public readonly List<int> funds = new List<int>();
        public readonly List<int> scores = new List<int>();
        public long upgrades;
        public long classifiedJobs;
        public long earnings;
        public readonly List<int> jobs = new List<int>();
        public int worked;

        public static CareerDistribution Of(DomainContext ctx)
        {
            CareerDistribution d = new CareerDistribution();
            for (int i = 0; i < ctx.actors.actors.Count; i++)
            {
                NetworkActor a = ctx.actors.actors[i];
                if (!ContractorService.IsNpcContractor(a) || !a.IsActive) continue;
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                d.active++;
                d.fame[(int)a.reputation.fame]++;
                d.experience[(int)ContractorService.Experience(a)]++;
                d.tiers[CareerPolicy.ClampTier(sim.equipment.tier)]++;
                CareerNeed need = ctx.Career != null ? ctx.Career.CurrentNeed(a) : CareerNeed.None;
                int n;
                d.needs.TryGetValue(need, out n);
                d.needs[need] = n + 1;
                if (ctx.Career != null)
                {
                    List<string> t = ctx.Career.Tags(a);
                    for (int k = 0; k < t.Count; k++)
                    {
                        d.tags.TryGetValue(t[k], out n);
                        d.tags[t[k]] = n + 1;
                    }
                }
                d.funds.Add(sim.funds);
                d.scores.Add(a.reputation.score);
                d.upgrades += sim.career.advancementCount;
                d.classifiedJobs += sim.career.Classified;
                d.jobs.Add((int)Math.Min(int.MaxValue, sim.career.Classified));
                if (sim.career.Classified > 0) d.worked++;
                d.earnings += sim.career.careerEarnings;
            }
            d.funds.Sort();
            d.scores.Sort();
            d.jobs.Sort();
            return d;
        }

        private static string Span(List<int> sorted)
        {
            if (sorted.Count == 0) return "-";
            return sorted[0] + " / " + sorted[sorted.Count / 2] + " / " + sorted[sorted.Count - 1];
        }

        public string FundsSpan => Span(funds);
        public string ScoreSpan => Span(scores);

        public int Median(List<int> sorted) => sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];

        public string Text()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("  fame:");
            for (int i = 0; i < fame.Length; i++) sb.Append(" " + (FameBand)i + "=" + fame[i]);
            sb.AppendLine();
            sb.Append("  experience:");
            for (int i = 0; i < experience.Length; i++) sb.Append(" " + (ExperienceBand)i + "=" + experience[i]);
            sb.AppendLine();
            sb.Append("  equipment tier:");
            for (int i = CareerPolicy.MinTier; i < tiers.Length; i++) sb.Append(" " + i + "=" + tiers[i]);
            sb.AppendLine();
            sb.Append("  need:");
            foreach (CareerNeed need in Enum.GetValues(typeof(CareerNeed)))
            {
                int n;
                needs.TryGetValue(need, out n);
                sb.Append(" " + need + "=" + n);
            }
            sb.AppendLine();
            sb.Append("  tags:");
            for (int i = 0; i < CareerTags.Emitted.Length; i++)
            {
                int n;
                tags.TryGetValue(CareerTags.Emitted[i], out n);
                sb.Append(" " + CareerTags.Emitted[i] + "=" + n);
            }
            sb.AppendLine();
            sb.AppendLine("  work: " + worked + " of " + active + " contractors have finished at least one job; jobs per contractor min/median/max " + Span(jobs));
            sb.AppendLine("  funds min/median/max " + FundsSpan + "; reputation min/median/max " + ScoreSpan + "; equipment upgrades " + upgrades + "; jobs classified " + classifiedJobs + "; career earnings " + earnings);
            return sb.ToString();
        }
    }
}
