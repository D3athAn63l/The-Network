using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Ports;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;

namespace TheNetwork.Domain.Contractors
{
    /// <summary>
    /// The daily per-contractor upkeep job and the weekly population manager (SIMULATION § 4.1, § 7).
    /// One small staggered job per NPC contractor per day, O(1) each; nothing per tick, no pawns.
    /// </summary>
    public sealed class UpkeepService
    {
        public const string PopulationJob = "population.weekly";
        public const int PopulationPeriod = Ticks.PerDay * 7;

        private readonly DomainContext ctx;

        public UpkeepService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        // ================================================================== daily upkeep

        public void UpkeepJob(ScheduledJob job)
        {
            NetworkActor a = ctx.actors.Get(new ActorId(job.target));
            if (!ContractorService.IsNpcContractor(a) || a.status != ActorStatus.Active || a.quarantinedReason != null) return;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            RunUpkeep(a, sim);
            int next = Math.Max(ctx.Now + Ticks.PerHour, NextDue(a, sim));
            sim.nextUpkeepTick = next;
            ctx.scheduler.Schedule(ContractorService.UpkeepJob, next, a.id.Value);
        }

        /// <summary>
        /// Repair (validator): recreates a missing contractor.upkeep job for every live NPC contractor.
        /// A saved due tick that is still ahead (and plausible) is kept; a past-due one runs soon after a
        /// short deterministic delay; a missing or impossible one gets the normal stagger. Ended and
        /// quarantined contractors get none, and a contractor that has its job is left alone (the kind is
        /// a singleton), so this never duplicates.
        /// </summary>
        public int EnsureUpkeepJobs(List<string> findings)
        {
            int repaired = 0, now = ctx.Now;
            List<NetworkActor> all = ctx.actors.actors;
            for (int i = 0; i < all.Count; i++)
            {
                NetworkActor a = all[i];
                if (!ContractorService.IsNpcContractor(a) || a.status != ActorStatus.Active || a.quarantinedReason != null) continue;
                if (ctx.scheduler.Has(ContractorService.UpkeepJob, a.id.Value)) continue;
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                if (sim == null) continue;
                int due;
                string how;
                if (sim.nextUpkeepTick > now && sim.nextUpkeepTick <= now + MaxUpkeepAhead)
                {
                    due = sim.nextUpkeepTick;
                    how = "kept its due tick";
                }
                else if (sim.nextUpkeepTick > 0 && sim.nextUpkeepTick <= now)
                {
                    due = now + 1 + (NetHash.Combine(a.seed, "upkeep.repair") & 0x7fffffff) % Ticks.PerHour;
                    how = "past due; runs shortly";
                }
                else
                {
                    due = NetScheduler.StaggeredDue(now + 1, a.seed, ContractorService.UpkeepJob, Ticks.PerDay);
                    how = "no valid due tick; staggered";
                }
                sim.nextUpkeepTick = due;
                ctx.scheduler.Schedule(ContractorService.UpkeepJob, due, a.id.Value);
                repaired++;
                if (findings != null) findings.Add("Contractor " + a.id + ": upkeep job missing; recreated (" + how + ").");
            }
            return repaired;
        }

        /// <summary>The furthest ahead a saved upkeep due tick can plausibly be (one period plus jitter, with margin).</summary>
        public const int MaxUpkeepAhead = Ticks.PerDay * 2;

        private int NextDue(NetworkActor a, ContractorSimulation sim)
        {
            NetRng rng = new NetRng(a.seed, "upkeep.jitter", sim.opsCompleted + sim.lastUpkeepTick / Ticks.PerDay);
            int jitter = rng.Range(-Ticks.PerDay / 10, Ticks.PerDay / 10 + 1);
            return ctx.Now + Ticks.PerDay + jitter;
        }

        /// <summary>
        /// One upkeep pass. A contractor more than a period overdue (the mod was absent, a long pause)
        /// runs once, scaled by the elapsed time, instead of many times (SIMULATION § 1.2).
        /// </summary>
        public void RunUpkeep(NetworkActor a, ContractorSimulation sim)
        {
            int now = ctx.Now;
            int last = sim.lastUpkeepTick < 0 ? now - Ticks.PerDay : sim.lastUpkeepTick;
            float days = Math.Max(0.25f, Math.Min(10f, (now - last) / (float)Ticks.PerDay));
            sim.lastUpkeepTick = now;
            OrganizationProfile org = a.Get<OrganizationProfile>();
            ContractorService contractors = ctx.Contractors;

            // 0. Whether the player can ask this contractor for Intel.
            ctx.Contractors.RefreshIntelSource(a);

            // 1. Heal.
            if (org != null)
            {
                for (int i = org.woundedRecovery.Count - 1; i >= 0; i--)
                {
                    RecoveryBucket b = org.woundedRecovery[i];
                    if (b.dueTick > now) continue;
                    TierCount t = org.TierOf(b.tier);
                    int healed = Math.Min(b.count, t.wounded);
                    t.wounded -= healed;
                    t.healthy += healed;
                    org.woundedRecovery.RemoveAt(i);
                }
                for (int i = 0; i < org.knownMembers.Count; i++) HealCharacter(ctx.characters.Get(org.knownMembers[i]), now);
            }
            else
            {
                HealCharacter(contractors.Embodied(a), now);
            }

            // 2. Morale drifts toward a baseline lowered by recent losses.
            int headcount = org == null ? 1 : Math.Max(1, org.Healthy + org.Wounded + org.Committed + org.knownMembers.Count);
            ActorRecordSummary summary = ctx.summaries?.Get(a.id);
            float recentLosses = summary == null ? 0f : Math.Min(1f, summary.Recent("casualties.taken", now) / headcount);
            MoraleModel.Drift(sim, days, sim.commitments.Count > 0, recentLosses);

            // 3. Funds and equipment.
            int upkeep = (int)Math.Round((org == null ? 2f : 3f) * headcount * days);
            sim.funds -= upkeep;
            int repairCost = 20 * sim.equipment.tier;
            if (sim.equipment.condition < 1f && sim.funds > repairCost * 3)
            {
                float restored = Math.Min(1f - sim.equipment.condition, 0.04f * days);
                sim.equipment.condition += restored;
                sim.funds -= (int)Math.Round(repairCost * restored / 0.04f);
            }

            // 4. Recruitment (organizations below capacity with the funds to pay for it).
            if (org != null)
            {
                int total = org.Healthy + org.Wounded + org.Committed + org.knownMembers.Count;
                bool due = org.recruitment.lastRecruitTick < 0 || now - org.recruitment.lastRecruitTick >= 3 * Ticks.PerDay;
                if (total < org.capacity && sim.funds > 250 && due)
                {
                    NetRng rng = new NetRng(a.seed, "recruit", org.recruitment.recruitedTotal + now / Ticks.PerDay);
                    if (rng.Chance(0.5f))
                    {
                        int n = Math.Min(org.capacity - total, org.capacity >= 20 ? 2 : 1);
                        org.TierOf(Tier.Recruit).healthy += n;
                        org.recruitment.recruitedTotal += n;
                        org.recruitment.lastRecruitTick = now;
                        sim.funds -= 60 * n;
                    }
                }

                // 5. Promotion from survived operations.
                if (sim.opsSincePromotion >= 2)
                {
                    Promote(org, Tier.Recruit, Tier.Regular, sim.opsSincePromotion >= 4 ? 2 : 1);
                    if (sim.opsSincePromotion >= 4) Promote(org, Tier.Regular, Tier.Veteran, 1);
                    sim.opsSincePromotion = 0;
                }
            }

            // 6. Career and retirement pressure (the retirement itself is Phase 6).
            float age = (now - a.foundedTick) / (float)Ticks.PerYear;
            float prosperity = sim.funds > 5000 ? 0.001f : 0f;
            sim.retirementPressure = ContractorService.Clamp(sim.retirementPressure + (0.0008f + 0.0004f * Math.Min(age, 10f) + 0.004f * recentLosses - prosperity) * days, 0f, 1f);
            UpdateCareer(a, sim);

            // 7. Origin faction still exists? (It may vanish; the contractor carries on.)
            if (sim.origin != null && !sim.originLost && sim.origin.IsValid)
            {
                List<FactionFacts> live = ctx.world.LiveFactions();
                bool found = false;
                for (int i = 0; i < live.Count && !found; i++) found = live[i].loadId == sim.origin.loadId;
                if (!found)
                {
                    sim.originLost = true;
                    ContractorEvent e = EventFactory.Make<ContractorEvent>(EventKeys.ContractorOriginLost, Importance.Minor, a.id.Ref);
                    e.actor = a.id;
                    e.actorName = a.name.Display;
                    e.reasonKey = sim.originSnapshot;
                    ctx.bus.Publish(e);
                }
            }

            sim.MarkDirty();
            contractors.MoraleShiftCheck(a, sim);
        }

        private static void Promote(OrganizationProfile org, Tier from, Tier to, int n)
        {
            TierCount f = org.TierOf(from);
            int moved = Math.Min(n, f.healthy);
            f.healthy -= moved;
            org.TierOf(to).healthy += moved;
        }

        private void HealCharacter(KnownCharacter c, int now)
        {
            if (c == null || c.status != CharacterStatus.Wounded || c.woundedUntilTick > now) return;
            c.status = CharacterStatus.Active;
            c.statusTick = now;
            c.woundedUntilTick = -1;
        }

        private void UpdateCareer(NetworkActor a, ContractorSimulation sim)
        {
            ExperienceBand exp = ContractorService.Experience(a);
            CareerStage stage;
            if (sim.retirementPressure > 0.6f || (a.reputation.fame >= FameBand.Famous && exp <= ExperienceBand.Experienced)) stage = CareerStage.Declining;
            else if (exp >= ExperienceBand.Elite) stage = CareerStage.Veteran;
            else if (exp >= ExperienceBand.Seasoned) stage = CareerStage.Established;
            else stage = CareerStage.Rising;
            sim.careerStage = stage;
        }

        // ================================================================== population manager

        public void EnsurePopulationJob()
        {
            if (ctx.scheduler.Has(PopulationJob, 0)) return;
            ctx.scheduler.Schedule(PopulationJob, NetScheduler.StaggeredDue(ctx.Now + PopulationPeriod / 2, ctx.networkSeed, PopulationJob, PopulationPeriod), 0);
        }

        public int ActiveContractorCount()
        {
            int n = 0;
            List<NetworkActor> all = ctx.actors.actors;
            for (int i = 0; i < all.Count; i++)
            {
                if (ContractorService.IsNpcContractor(all[i]) && all[i].status == ActorStatus.Active && all[i].quarantinedReason == null) n++;
            }
            return n;
        }

        /// <summary>
        /// Weekly: tops the population up toward the target with persistent world-generated newcomers. It
        /// never deletes an actor to hit the target; deaths and dissolution bring the count down naturally.
        /// </summary>
        public void PopulationJobRun(ScheduledJob job)
        {
            RunPopulation();
            ctx.scheduler.Schedule(PopulationJob, ctx.Now + PopulationPeriod, 0);
        }

        public int RunPopulation()
        {
            int target = Math.Max(0, ctx.tuning.targetContractorCount);
            int count = ActiveContractorCount();
            if (count >= target) return 0;
            int deficit = target - count;
            int add = Math.Min(3, Math.Max(1, deficit / 8));
            for (int i = 0; i < add; i++)
            {
                ctx.Contractors.CreateWorldGenerated(ctx.actors.worldGeneratedCount++, "Population");
            }
            return add;
        }

        public const int NewcomerCooldownTicks = 5 * Ticks.PerDay;
        public const float NewcomerChance = 0.06f;

        /// <summary>
        /// An Open contract's thin candidate pool (or a seeded chance) may bring a NEW persistent
        /// contractor into the world as a bidder (SIMULATION § 5.2, master § 64). Never on every contract:
        /// a cooldown, a population ceiling and the contract's own seed decide.
        /// </summary>
        public NetworkActor TryNewcomerBidder(int contractSeed, int round, bool thinPool)
        {
            bool forced = ProcurementDevOverrides.forceNewcomer;
            ProcurementDevOverrides.forceNewcomer = false;
            int last = ctx.actors.lastNewcomerBidderTick;
            if (!forced && last >= 0 && ctx.Now - last < NewcomerCooldownTicks) return null;
            if (!forced && ActiveContractorCount() >= Math.Max(1, (int)(ctx.tuning.targetContractorCount * 1.25f))) return null;
            if (!forced && !thinPool && !new NetRng(contractSeed, "newcomer", round).Chance(NewcomerChance)) return null;
            ctx.actors.lastNewcomerBidderTick = ctx.Now;
            return ctx.Contractors.CreateWorldGenerated(ctx.actors.worldGeneratedCount++, "NewcomerBidder");
        }
    }
}
