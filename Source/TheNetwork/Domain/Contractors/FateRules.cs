using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Settings;

namespace TheNetwork.Domain.Contractors
{
    /// <summary>
    /// The ONE rule set for what a person's fate, a group's loss and an actor's end do to durable state (PHYSICAL_LIFECYCLE
    /// § 15.5): plain assignments and pure decisions, shared by the abstract casualty path (<see cref="ContractorService.ApplyCasualties"/>,
    /// <see cref="ContractorService.RunSuccession"/>, <see cref="ContractorService.EndActor"/>) and by the physical
    /// reconciliation commit. Nothing here publishes, schedules, calls a service, logs, or draws from an unseeded random
    /// source: the decisions that need a deterministic stream (a promoted successor's name, the doctrine drift) take it from
    /// persisted seeds at PLAN time, so a restored-and-retried commit decides exactly the same thing.
    /// </summary>
    public static class FateRules
    {
        /// <summary>Status writes refused because the person was already dead (P3-INV-004). Diagnostics; always 0 in a healthy world.</summary>
        public static int refusedDeadWrites;

        /// <summary>
        /// Death is monotonic (§ 10.1, P3-INV-004): no writer changes a Dead status. Returns false (and changes nothing) when the
        /// write is refused. Every existing caller already checked <c>IsAlive</c>, so this guard never triggers in an abstract flow.
        /// </summary>
        public static bool SetStatus(KnownCharacter c, CharacterStatus s, int now)
        {
            if (c == null) return false;
            if (c.status == CharacterStatus.Dead && s != CharacterStatus.Dead)
            {
                refusedDeadWrites++;
                return false;
            }
            c.status = s;
            c.statusTick = now;
            return true;
        }

        public static void Killed(KnownCharacter c, int now, string causeKey)
        {
            if (!SetStatus(c, CharacterStatus.Dead, now)) return;
            c.diedTick = now;
            c.deathCauseKey = causeKey;
        }

        public static void Wounded(KnownCharacter c, int now, int woundDays)
        {
            if (!SetStatus(c, CharacterStatus.Wounded, now)) return;
            c.woundedUntilTick = now + Math.Max(2, woundDays) * Ticks.PerDay;
        }

        public static void Captured(KnownCharacter c, int now)
        {
            SetStatus(c, CharacterStatus.Captured, now);
        }

        public static void Missing(KnownCharacter c, int now)
        {
            SetStatus(c, CharacterStatus.Missing, now);
        }

        /// <summary>
        /// A person POSITIVELY observed back and free (PHYSICAL_LIFECYCLE § 15.3, a physical return), unhurt: a story status of
        /// Missing or Captured resolves to Active, because the observation is authoritative. Any other status is left as it is: an
        /// Active person stays Active, and a Dead or Lost person is never revived by a return (P3-INV-004). An injured return goes
        /// through <see cref="Wounded"/> instead (which also resolves Missing or Captured, to Wounded).
        /// </summary>
        public static void ReturnedFree(KnownCharacter c, int now)
        {
            if (c == null || (c.status != CharacterStatus.Missing && c.status != CharacterStatus.Captured)) return;
            SetStatus(c, CharacterStatus.Active, now);
        }

        /// <summary>The person is gone with no evidence (§ 17): Lost, never "home", never regenerated.</summary>
        public static void Lost(KnownCharacter c, int now)
        {
            SetStatus(c, CharacterStatus.Lost, now);
        }

        /// <summary>The share of the group lost (killed, captured, missing) against the headcount it had before the loss.</summary>
        public static float LossShare(OrganizationProfile org, int killed, int captured, int missing)
        {
            int headBefore = org == null ? 1 : org.Healthy + org.Wounded + org.Committed + org.knownMembers.Count + killed + captured + missing;
            return headBefore == 0 ? 1f : (killed + captured + missing) / (float)headBefore;
        }

        /// <summary>The per-operation bookkeeping of a resolved job (only an OPERATION's own resolution does this, never a physical episode alone).</summary>
        public static void OperationBookkeeping(ContractorSimulation sim, bool organization, bool success)
        {
            sim.opsCompleted++;
            if (organization) sim.opsSincePromotion++;
            // Survivors learn from the work, a little more from success.
            sim.skill = ContractorService.Clamp(sim.skill + (success ? 0.012f : 0.006f), 0f, 1f);
            sim.MarkDirty();
        }

        /// <summary>The morale descriptor after a change, or the current one when nothing shifts (pure).</summary>
        public static MoraleDescriptor NextDescriptor(ContractorSimulation sim)
        {
            return MoraleModel.Evaluate(sim, sim.morale.descriptor);
        }

        public static void SetDescriptor(ContractorSimulation sim, MoraleDescriptor after, int now)
        {
            sim.morale.descriptor = after;
            sim.morale.descriptorTick = now;
            sim.MarkDirty();
        }

        /// <summary>The durable half of an actor's end: status, end tick and reason, and the profile suspended. (The scheduler and spatial clean-up and the event are the caller's.)</summary>
        public static void ActorEnded(NetworkActor a, string reasonKey, int now)
        {
            a.status = ActorStatus.Dissolved;
            a.endedTick = now;
            a.endReasonKey = reasonKey;
            ContractorProfile p = a.Get<ContractorProfile>();
            if (p != null) p.suspended = true;
        }

        // ================================================================== succession

        /// <summary>
        /// A succession decided (§ 15.6 "the decision in PLAN"): who leads next (an existing person, or a Veteran promoted from
        /// headcount, whose name is already generated from the actor's seed), or that nobody can and the organization dissolves;
        /// plus the bounded doctrine drift the new leader brings. Pure data: applying it is plain assignments.
        /// </summary>
        public sealed class SuccessionPlan
        {
            public CharacterId oldLeader;
            public KnownCharacter next;
            public bool promote;
            public Tier promoteFrom;
            public NameSnapshot promotedName;
            public float driftCaution;
            public float driftGreed;

            /// <summary>The tiers a promotion looked at, best first (looking at a tier gives the profile an empty entry for it, as it always has).</summary>
            public readonly List<Tier> probedTiers = new List<Tier>();

            public bool Dissolves => next == null && !promote;
        }

        /// <summary>
        /// Decides a succession from state as given (§ SIMULATION 4.5): a living lieutenant first, then the best living Known
        /// Character, then a Veteran promoted from headcount; nobody ⇒ dissolution. <paramref name="eligible"/> says who may lead
        /// (the abstract path: alive, not captured or missing, abstractly simulatable; the physical commit: the same, judged
        /// against the fates it is about to apply). <paramref name="healthyOf"/> gives the headcount a promotion may draw from.
        /// Reads only; the generated name and drift come from the actor's seed and its persisted succession count.
        /// </summary>
        public static SuccessionPlan PlanSuccession(NetworkActor a, OrganizationProfile org, CharacterStore characters, CharacterId oldLeaderId,
            Func<KnownCharacter, bool> eligible, Func<Tier, int> healthyOf, NamePools pools)
        {
            SuccessionPlan plan = new SuccessionPlan { oldLeader = oldLeaderId };
            KnownCharacter next = null;
            for (int pass = 0; pass < 2 && next == null; pass++)
            {
                List<CharacterId> pool = pass == 0 ? org.lieutenants : org.knownMembers;
                for (int i = 0; i < pool.Count; i++)
                {
                    KnownCharacter c = characters.Get(pool[i]);
                    if (c == null || c.id == oldLeaderId || !eligible(c)) continue;
                    if (next == null || c.notability > next.notability) next = c;
                }
            }
            plan.next = next;
            if (next == null)
            {
                for (int t = (int)Tier.Veteran; t >= 0 && !plan.promote; t--)
                {
                    plan.probedTiers.Add((Tier)t);
                    if (healthyOf((Tier)t) > 0)
                    {
                        plan.promote = true;
                        plan.promoteFrom = (Tier)t;
                    }
                }
                if (plan.promote)
                {
                    NetRng rng = new NetRng(a.seed, "succession", org.succession.successions);
                    CastGenerator names = new CastGenerator(pools, NetHash.Combine(a.seed, "names.succession"), () => null, null);
                    string nick;
                    string display = names.GeneratePersonName(rng, out nick);
                    plan.promotedName = new NameSnapshot { display = display, nick = nick };
                }
            }
            // A new leader nudges the group's habits a little (bounded; SIMULATION § 4.3). Seeded by the count AFTER this succession.
            NetRng drift = new NetRng(a.seed, "succession.doctrine", org.succession.successions + 1);
            plan.driftCaution = drift.Range(-0.03f, 0.03f);
            plan.driftGreed = drift.Range(-0.03f, 0.03f);
            return plan;
        }

        /// <summary>A tier's healthy headcount, read without creating its entry (the pure counterpart of <c>TierOf(t).healthy</c>).</summary>
        public static int PeekHealthy(OrganizationProfile org, Tier t)
        {
            for (int i = 0; i < org.tiers.Count; i++) if (org.tiers[i].tier == t) return org.tiers[i].healthy;
            return 0;
        }

        /// <summary>The tier entries a promotion search touched (idempotent: an existing entry is left as it is).</summary>
        public static void ApplyProbes(OrganizationProfile org, SuccessionPlan plan)
        {
            for (int i = 0; i < plan.probedTiers.Count; i++) org.TierOf(plan.probedTiers[i]);
        }

        /// <summary>The promoted Veteran becomes a Known Character (the id is drawn here, from the allocator the caller owns).</summary>
        public static KnownCharacter ApplyPromotion(NetworkActor a, OrganizationProfile org, SuccessionPlan plan, IdAllocator ids, CharacterStore characters, int now)
        {
            org.TierOf(plan.promoteFrom).healthy--;
            KnownCharacter next = new KnownCharacter
            {
                id = new CharacterId(ids.NextId()),
                name = plan.promotedName,
                role = CharacterRole.Member,
                org = a.id,
                createdTick = now,
                statusTick = now,
                notability = ContractorService.Clamp(0.3f, 0.05f, 1f)
            };
            characters.Add(next);
            if (org.knownMembers.Count < OrganizationProfile.MaxKnownMembers) org.knownMembers.Add(next.id);
            return next;
        }

        /// <summary>The old leader leaves the lieutenants (and the roster, if dead). Runs whether or not anyone succeeds.</summary>
        public static void ApplyOldLeaderExit(OrganizationProfile org, CharacterStore characters, CharacterId oldLeaderId)
        {
            KnownCharacter old = characters.Get(oldLeaderId);
            org.lieutenants.Remove(oldLeaderId);
            if (old != null && !old.IsAlive) org.knownMembers.Remove(oldLeaderId);
        }

        /// <summary>The new leader takes over: roles, notability, the counters, the shock and the drift.</summary>
        public static void ApplyNewLeader(NetworkActor a, OrganizationProfile org, CharacterStore characters, SuccessionPlan plan, KnownCharacter next, int now)
        {
            KnownCharacter old = characters.Get(plan.oldLeader);
            org.lieutenants.Remove(next.id);
            if (!org.knownMembers.Contains(next.id) && org.knownMembers.Count < OrganizationProfile.MaxKnownMembers) org.knownMembers.Add(next.id);
            next.role = CharacterRole.Leader;
            next.notability = ContractorService.Clamp(next.notability + 0.1f, 0f, 1f);
            if (old != null && old.IsAlive) old.role = CharacterRole.Member;
            org.leader = next.id;
            org.succession.successions++;
            org.succession.lastSuccessionTick = now;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            if (sim != null)
            {
                MoraleModel.Shock(sim, 0.1f, true, now);
                sim.doctrine.caution = ContractorService.Clamp(sim.doctrine.caution + plan.driftCaution, 0.02f, 0.98f);
                sim.doctrine.greed = ContractorService.Clamp(sim.doctrine.greed + plan.driftGreed, 0.02f, 0.98f);
                sim.MarkDirty();
            }
        }
    }
}
