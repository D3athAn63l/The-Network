using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Domain.Physical
{
    /// <summary>Role capacity only: no anonymous identity, Pawn or persisted roster.</summary>
    public struct RoleCapacity
    {
        public readonly OperationalRole role;
        public readonly int count;

        public RoleCapacity(OperationalRole role, int count) { this.role = role; this.count = count; }
    }

    /// <summary>The immutable, derived Composition v1 recipe. Changing v1 requires a version/design decision.</summary>
    public sealed class CompositionTemplate
    {
        public readonly int actorSeed;
        public readonly int originCapacity;
        public readonly ReadOnlyCollection<RoleCapacity> weights;

        internal CompositionTemplate(int actorSeed, int originCapacity, List<RoleCapacity> weights)
        {
            this.actorSeed = actorSeed;
            this.originCapacity = originCapacity;
            this.weights = weights.AsReadOnly();
        }
    }

    public struct OrganizationRoleAssignment
    {
        public readonly KnownCharacter character;
        public readonly OperationalRole role;

        public OrganizationRoleAssignment(KnownCharacter character, OperationalRole role) { this.character = character; this.role = role; }
    }

    /// <summary>
    /// ADR-057 / readiness § 2.1. The recipe reads ONLY immutable seed, origin capacity and original specialties.
    /// Apportionment reads a separate live headcount; neither recipe nor quotas contain anonymous person identities.
    /// </summary>
    public static class OrganizationCompositionV1
    {
        public const int Version = 1;
        public const int MaxRoleEntries = 8;

        public static bool IsRole(OperationalRole role) { return role >= OperationalRole.Leader && role <= OperationalRole.Specialist; }

        public static bool TryDerive(int actorSeed, int originCapacity, IList<string> originalSpecialties,
            out CompositionTemplate template, out string refusal)
        {
            template = null;
            refusal = null;
            if (originCapacity != 3 && originCapacity != 7 && originCapacity != 14 && originCapacity != 32)
            { refusal = "UnknownOrganizationOriginCapacity"; return false; }
            List<OperationalRole> roles = RoleDerivation.Candidates(originalSpecialties);
            roles.Remove(OperationalRole.Unset);
            roles.Remove(OperationalRole.Leader);
            if (!roles.Contains(OperationalRole.Rifleman)) roles.Add(OperationalRole.Rifleman);
            if (roles.Count > MaxRoleEntries - 1)
            {
                roles.Sort((a, b) =>
                {
                    uint ha = StableRoleHash(actorSeed, originCapacity, a), hb = StableRoleHash(actorSeed, originCapacity, b);
                    int c = ha.CompareTo(hb);
                    return c != 0 ? c : a.CompareTo(b);
                });
                roles.RemoveRange(MaxRoleEntries - 1, roles.Count - (MaxRoleEntries - 1));
            }
            roles.Sort();
            List<RoleCapacity> weights = new List<RoleCapacity> { new RoleCapacity(OperationalRole.Leader, 1) };
            foreach (OperationalRole role in roles)
                weights.Add(new RoleCapacity(role, role == OperationalRole.Rifleman || role == OperationalRole.Technician || role == OperationalRole.Logistician ? 2 : 1));
            template = new CompositionTemplate(actorSeed, originCapacity, weights);
            return true;
        }

        private static uint StableRoleHash(int seed, int capacity, OperationalRole role)
        {
            return unchecked((uint)NetHash.Combine(NetHash.Combine(NetHash.Combine(seed, capacity), "composition.v1"), role.ToString()));
        }

        /// <summary>Full role quotas: one operational Leader, largest remainder for the other roles, enum-order ties.</summary>
        public static bool TryQuotas(CompositionTemplate template, int living, out List<RoleCapacity> quotas, out string refusal)
        {
            quotas = null;
            refusal = null;
            if (template == null || living < 0) { refusal = "InvalidCompositionHeadcount"; return false; }
            List<RoleCapacity> nonLeader = new List<RoleCapacity>();
            foreach (RoleCapacity w in template.weights) if (w.role != OperationalRole.Leader) nonLeader.Add(w);
            List<RoleCapacity> rest;
            if (!Allocate(Math.Max(0, living - 1), nonLeader, false, out rest, out refusal)) return false;
            quotas = new List<RoleCapacity> { new RoleCapacity(OperationalRole.Leader, living > 0 ? 1 : 0) };
            quotas.AddRange(rest);
            return true;
        }

        /// <summary>
        /// Plans every missing role before any write. The caller stores assignments once. Origin cohort includes dead/history
        /// records ordered by CharacterId, never current standing/status. A bound Unset role cannot be reconstructed safely.
        /// </summary>
        public static bool TryPlanRoleInitialization(NetworkActor actor, IList<KnownCharacter> organizationPeople,
            out List<OrganizationRoleAssignment> assignments, out string refusal)
        {
            assignments = null;
            refusal = null;
            OrganizationProfile org = actor?.Get<OrganizationProfile>();
            ContractorProfile profile = actor?.Get<ContractorProfile>();
            if (actor == null || !actor.id.IsValid || actor.kind != ActorKind.Organization || !ContractorService.IsNpcContractor(actor)
                || org == null || profile == null || organizationPeople == null)
            { refusal = "NotNpcOrganization"; return false; }
            CompositionTemplate template;
            if (!TryDerive(actor.seed, org.capacity, profile.specialties, out template, out refusal)) return false;
            List<KnownCharacter> people = new List<KnownCharacter>(), origin = new List<KnownCharacter>();
            HashSet<int> ids = new HashSet<int>();
            foreach (KnownCharacter c in organizationPeople)
            {
                if (c == null || c.org != actor.id) continue;
                if (!c.id.IsValid || !ids.Add(c.id.Value)) { refusal = "InvalidOrganizationCharacterIds"; return false; }
                if (c.opRole != OperationalRole.Unset && !IsRole(c.opRole)) { refusal = "InvalidOperationalRole"; return false; }
                if (c.opRole == OperationalRole.Unset && c.pawn != null && c.pawn.IsBound)
                { refusal = "BoundOrganizationRoleUnknown"; return false; }
                people.Add(c);
                if (c.createdTick == actor.foundedTick) origin.Add(c);
            }
            if (origin.Count > org.capacity) { refusal = "OriginCohortExceedsCapacity"; return false; }
            origin.Sort((a, b) => a.id.Value.CompareTo(b.id.Value));
            people.Sort((a, b) => a.id.Value.CompareTo(b.id.Value));
            List<RoleCapacity> quotas;
            if (!TryQuotas(template, org.capacity, out quotas, out refusal)) return false;
            List<OperationalRole> slots = new List<OperationalRole>();
            foreach (RoleCapacity quota in quotas) for (int i = 0; i < quota.count; i++) slots.Add(quota.role);
            Dictionary<int, OperationalRole> originRoles = new Dictionary<int, OperationalRole>();
            for (int i = 0; i < origin.Count; i++) originRoles.Add(origin[i].id.Value, slots[i]);
            assignments = new List<OrganizationRoleAssignment>();
            foreach (KnownCharacter c in people)
            {
                if (c.opRole != OperationalRole.Unset) continue;
                OperationalRole role;
                if (!originRoles.TryGetValue(c.id.Value, out role))
                {
                    uint h = unchecked((uint)NetHash.Combine(NetHash.Combine(NetHash.Combine(actor.seed, org.capacity), c.id.Value), "organization.role.v1.later"));
                    role = slots[(int)(h % (uint)slots.Count)];
                }
                assignments.Add(new OrganizationRoleAssignment(c, role));
            }
            return true;
        }

        /// <summary>Largest remainder; with caps, each weight is also its maximum residual seat capacity.</summary>
        internal static bool Allocate(int total, IList<RoleCapacity> weights, bool capped, out List<RoleCapacity> result, out string refusal)
        {
            result = new List<RoleCapacity>();
            refusal = null;
            long sum = 0;
            foreach (RoleCapacity w in weights)
            {
                if (w.count < 0) { refusal = "NegativeRoleCapacity"; return false; }
                sum += w.count;
            }
            if (total < 0 || (total > 0 && sum == 0) || (capped && total > sum))
            { refusal = "InsufficientRoleCapacity"; return false; }
            long[] remainder = new long[weights.Count];
            int allocated = 0;
            for (int i = 0; i < weights.Count; i++)
            {
                long product = (long)total * weights[i].count;
                int count = sum == 0 ? 0 : (int)(product / sum);
                remainder[i] = sum == 0 ? 0 : product % sum;
                result.Add(new RoleCapacity(weights[i].role, count));
                allocated += count;
            }
            List<int> order = new List<int>();
            for (int i = 0; i < weights.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int c = remainder[b].CompareTo(remainder[a]);
                return c != 0 ? c : weights[a].role.CompareTo(weights[b].role);
            });
            foreach (int i in order)
            {
                if (allocated == total) break;
                if (weights[i].count == 0 || (capped && result[i].count >= weights[i].count)) continue;
                result[i] = new RoleCapacity(result[i].role, result[i].count + 1);
                allocated++;
            }
            if (allocated != total) { refusal = "RoleApportionmentNotConserved"; return false; }
            return true;
        }
    }

    public struct OrganizationRoleSeats
    {
        public readonly OperationalRole role;
        public readonly int pinned;
        public readonly int anonymous;
        public OrganizationRoleSeats(OperationalRole role, int pinned, int anonymous) { this.role = role; this.pinned = pinned; this.anonymous = anonymous; }
    }

    /// <summary>A live derived snapshot, never saved; pins remain identities even if they exceed proportional quotas.</summary>
    public sealed class OrganizationSeats
    {
        public readonly ActorId actor;
        public readonly int living;
        public readonly int healthyAnonymous;
        public readonly int anonymous;
        public readonly ReadOnlyCollection<KnownCharacter> pins;
        public readonly ReadOnlyCollection<OrganizationRoleSeats> roles;
        internal OrganizationSeats(ActorId actor, int living, int healthy, int anonymous, List<KnownCharacter> pins, List<OrganizationRoleSeats> roles)
        {
            this.actor = actor; this.living = living; healthyAnonymous = healthy; this.anonymous = anonymous;
            this.pins = pins.AsReadOnly(); this.roles = roles.AsReadOnly();
        }
    }

    public struct OrganizationMissionMember
    {
        public readonly CharacterId character;
        public readonly OperationalRole role;
        public readonly Tier tier;
        /// <summary>Policy eligibility only. Actual P0 still requires a successful player-visible placement latch.</summary>
        public readonly bool mayConcretizeByPresence;
        public bool IsNamed => character.IsValid;
        public OrganizationMissionMember(CharacterId character, OperationalRole role, Tier tier, bool mayConcretizeByPresence)
        { this.character = character; this.role = role; this.tier = tier; this.mayConcretizeByPresence = mayConcretizeByPresence; }
    }

    public sealed class OrganizationMission
    {
        public readonly ReadOnlyCollection<OrganizationMissionMember> members;
        public readonly ReadOnlyCollection<RoleCapacity> optionalShortages;
        internal OrganizationMission(List<OrganizationMissionMember> members, List<RoleCapacity> shortages)
        { this.members = members.AsReadOnly(); optionalShortages = shortages.AsReadOnly(); }
    }

    /// <summary>Actual living membership, seat conservation and ≤8 role-correct mission selection. Reads only.</summary>
    public static class OrganizationSeatPolicy
    {
        public const int SmallMaximum = 6;
        public const int MidSizeMaximum = 12;
        public const int MaxMissionMembers = 8;

        public static bool IsCurrentMember(KnownCharacter character, ActorId organization)
        {
            return character != null && character.org == organization && character.IsAlive && character.status != CharacterStatus.Defected
                && character.status != CharacterStatus.Retired && character.role != CharacterRole.Retired;
        }

        public static bool IsAvailablePin(KnownCharacter character)
        {
            return character != null && character.status == CharacterStatus.Active && character.quarantinedReason == null
                && AuthorityGate.CanSimulateAbstractly(character);
        }

        public static bool MayConcretizeByPresence(int living, OperationalRole role)
        {
            return OrganizationCompositionV1.IsRole(role) && ((living >= 2 && living <= SmallMaximum)
                || (living > SmallMaximum && living <= MidSizeMaximum && role != OperationalRole.Rifleman));
        }

        public static bool TryApportion(NetworkActor actor, IList<KnownCharacter> organizationPeople, out OrganizationSeats seats, out string refusal)
        {
            seats = null;
            refusal = null;
            OrganizationProfile org = actor?.Get<OrganizationProfile>();
            ContractorProfile profile = actor?.Get<ContractorProfile>();
            if (actor == null || !actor.id.IsValid || actor.kind != ActorKind.Organization || !ContractorService.IsNpcContractor(actor)
                || org == null || profile == null || organizationPeople == null || org.knownMembers == null)
            { refusal = "NotNpcOrganization"; return false; }
            CompositionTemplate template;
            if (!OrganizationCompositionV1.TryDerive(actor.seed, org.capacity, profile.specialties, out template, out refusal)) return false;
            Dictionary<int, KnownCharacter> byId = new Dictionary<int, KnownCharacter>();
            foreach (KnownCharacter c in organizationPeople)
            {
                if (c == null || c.org != actor.id) continue;
                if (!c.id.IsValid || byId.ContainsKey(c.id.Value)) { refusal = "InvalidOrganizationCharacterIds"; return false; }
                byId.Add(c.id.Value, c);
            }
            HashSet<int> known = new HashSet<int>();
            List<KnownCharacter> pins = new List<KnownCharacter>();
            Dictionary<OperationalRole, int> pinCounts = new Dictionary<OperationalRole, int>();
            foreach (CharacterId id in org.knownMembers)
            {
                KnownCharacter c;
                if (!id.IsValid || !known.Add(id.Value) || !byId.TryGetValue(id.Value, out c))
                { refusal = "UnresolvedOrganizationMembership"; return false; }
                if (!IsCurrentMember(c, actor.id)) continue;
                if (!OrganizationCompositionV1.IsRole(c.opRole)) { refusal = "OrganizationRoleUnknown"; return false; }
                pins.Add(c);
                pinCounts[c.opRole] = Count(pinCounts, c.opRole) + 1;
            }
            // A current person omitted from membership cannot silently become an anonymous spare seat.
            foreach (KnownCharacter c in byId.Values)
                if (IsCurrentMember(c, actor.id) && !known.Contains(c.id.Value)) { refusal = "CurrentOrganizationMemberUnlisted"; return false; }
            int healthy, wounded, committed;
            if (!TryCountTiers(org.tiers, false, out healthy, out wounded, out refusal)) return false;
            int unused;
            if (!TryCountTiers(org.committed, true, out committed, out unused, out refusal)) return false;
            long anonymous = (long)healthy + wounded + committed, living = anonymous + pins.Count;
            if (living > int.MaxValue) { refusal = "OrganizationHeadcountOverflow"; return false; }
            List<RoleCapacity> quotas;
            if (!OrganizationCompositionV1.TryQuotas(template, (int)living, out quotas, out refusal)) return false;
            List<RoleCapacity> residual = new List<RoleCapacity>();
            foreach (RoleCapacity q in quotas) residual.Add(new RoleCapacity(q.role, Math.Max(0, q.count - Count(pinCounts, q.role))));
            List<RoleCapacity> anonymousSeats;
            if (!OrganizationCompositionV1.Allocate((int)anonymous, residual, true, out anonymousSeats, out refusal)) return false;
            List<OrganizationRoleSeats> roleSeats = new List<OrganizationRoleSeats>();
            foreach (RoleCapacity r in anonymousSeats) roleSeats.Add(new OrganizationRoleSeats(r.role, Count(pinCounts, r.role), r.count));
            // Existing pins outside the v1 selected roles remain intact and consume living membership.
            foreach (KeyValuePair<OperationalRole, int> pin in pinCounts)
                if (!roleSeats.Exists(r => r.role == pin.Key)) roleSeats.Add(new OrganizationRoleSeats(pin.Key, pin.Value, 0));
            roleSeats.Sort((a, b) => a.role.CompareTo(b.role));
            pins.Sort((a, b) => a.id.Value.CompareTo(b.id.Value));
            int conserved = 0;
            foreach (OrganizationRoleSeats r in roleSeats) conserved += r.pinned + r.anonymous;
            if (conserved != living) { refusal = "OrganizationSeatCountNotConserved"; return false; }
            seats = new OrganizationSeats(actor.id, (int)living, healthy, (int)anonymous, pins, roleSeats);
            return true;
        }

        /// <summary>
        /// Required needs refuse on shortages; optional needs shrink. Living unavailable pins never become abstract vacancies.
        /// Other active anonymous slot roles consume residual capacity before healthy-seat reduction. The caller supplies the
        /// operation commitment check via namedIsAvailable in addition to the invariant durable availability checks here.
        /// </summary>
        public static bool TrySelectMission(NetworkActor actor, OrganizationSeats seats, IList<RoleCapacity> required,
            IList<RoleCapacity> optional, IList<RoleCapacity> activeAnonymousRoleSlots, Func<KnownCharacter, bool> namedIsAvailable,
            out OrganizationMission mission, out string refusal)
        {
            mission = null;
            refusal = null;
            OrganizationProfile org = actor?.Get<OrganizationProfile>();
            if (actor == null || !actor.IsActive || org == null || seats == null || seats.actor != actor.id)
            { refusal = "InvalidOrganizationSeatSnapshot"; return false; }
            List<RoleCapacity> requiredNeeds, optionalNeeds;
            int requiredTotal, optionalTotal;
            if (!NormalizeNeeds(required, out requiredNeeds, out requiredTotal, out refusal)
                || !NormalizeNeeds(optional, out optionalNeeds, out optionalTotal, out refusal)) return false;
            if (requiredTotal + (long)optionalTotal > MaxMissionMembers || requiredTotal + optionalTotal == 0)
            { refusal = "MissionSizeOutsideBounds"; return false; }
            Dictionary<OperationalRole, int> capacity = new Dictionary<OperationalRole, int>();
            foreach (OrganizationRoleSeats r in seats.roles) capacity[r.role] = r.anonymous;
            List<RoleCapacity> active;
            int activeTotal;
            if (!NormalizeNeeds(activeAnonymousRoleSlots, out active, out activeTotal, out refusal)) return false;
            foreach (RoleCapacity slot in active)
            {
                if (slot.count > Count(capacity, slot.role)) { refusal = "ActiveAnonymousSlotsExceedRoleCapacity"; return false; }
                capacity[slot.role] -= slot.count;
            }
            List<RoleCapacity> remaining = new List<RoleCapacity>();
            foreach (KeyValuePair<OperationalRole, int> role in capacity) remaining.Add(new RoleCapacity(role.Key, role.Value));
            remaining.Sort((a, b) => a.role.CompareTo(b.role));
            List<RoleCapacity> availableRoles;
            if (!OrganizationCompositionV1.Allocate(seats.healthyAnonymous, remaining, true, out availableRoles, out refusal)) return false;
            capacity.Clear();
            foreach (RoleCapacity role in availableRoles) capacity[role.role] = role.count;
            int healthy, wounded;
            if (!TryCountTiers(org.tiers, false, out healthy, out wounded, out refusal)) return false;
            if (healthy != seats.healthyAnonymous) { refusal = "OrganizationSeatsChanged"; return false; }
            int[] tiers = new int[3];
            foreach (TierCount tier in org.tiers) tiers[(int)tier.tier] = tier.healthy;
            HashSet<int> selected = new HashSet<int>();
            List<OrganizationMissionMember> members = new List<OrganizationMissionMember>();
            List<RoleCapacity> shortages = new List<RoleCapacity>();
            int discretionary = Math.Max(0, OrganizationProfile.MaxKnownMembers - seats.pins.Count);
            if (!Select(requiredNeeds, true, seats, namedIsAvailable, capacity, tiers, selected, members, shortages, ref discretionary, out refusal)) return false;
            if (!Select(optionalNeeds, false, seats, namedIsAvailable, capacity, tiers, selected, members, shortages, ref discretionary, out refusal)) return false;
            if (members.Count == 0) { refusal = "NoAvailableMissionSeats"; return false; }
            mission = new OrganizationMission(members, shortages);
            return true;
        }

        private static bool Select(IList<RoleCapacity> needs, bool required, OrganizationSeats seats, Func<KnownCharacter, bool> namedIsAvailable,
            Dictionary<OperationalRole, int> capacity, int[] tiers, HashSet<int> selected, List<OrganizationMissionMember> members,
            List<RoleCapacity> shortages, ref int discretionary, out string refusal)
        {
            refusal = null;
            foreach (RoleCapacity need in needs)
            {
                int left = need.count;
                foreach (KnownCharacter pin in seats.pins)
                {
                    if (left == 0) break;
                    if (pin.opRole != need.role || selected.Contains(pin.id.Value) || !IsAvailablePin(pin)
                        || (namedIsAvailable != null && !namedIsAvailable(pin))) continue;
                    selected.Add(pin.id.Value);
                    members.Add(new OrganizationMissionMember(pin.id, pin.opRole, Tier.Regular, false));
                    left--;
                }
                bool p0 = MayConcretizeByPresence(seats.living, need.role);
                while (left > 0 && Count(capacity, need.role) > 0)
                {
                    if (p0 && discretionary == 0) break;
                    int tier = Array.FindIndex(tiers, count => count > 0);
                    if (tier < 0) break;
                    tiers[tier]--;
                    capacity[need.role]--;
                    if (p0) discretionary--;
                    members.Add(new OrganizationMissionMember(CharacterId.None, need.role, (Tier)tier, p0));
                    left--;
                }
                if (left > 0)
                {
                    if (required) { refusal = "RequiredMissionRoleUnavailable:" + need.role; return false; }
                    shortages.Add(new RoleCapacity(need.role, left));
                }
            }
            return true;
        }

        private static int Count(Dictionary<OperationalRole, int> counts, OperationalRole role)
        { int count; return counts.TryGetValue(role, out count) ? count : 0; }

        private static bool NormalizeNeeds(IList<RoleCapacity> needs, out List<RoleCapacity> normalized, out int total, out string refusal)
        {
            normalized = new List<RoleCapacity>(); total = 0; refusal = null;
            if (needs == null) return true;
            Dictionary<OperationalRole, int> counts = new Dictionary<OperationalRole, int>();
            foreach (RoleCapacity need in needs)
            {
                if (!OrganizationCompositionV1.IsRole(need.role) || need.count < 0 || total + (long)need.count > int.MaxValue)
                { refusal = "InvalidMissionRoleNeeds"; return false; }
                total += need.count;
                counts[need.role] = Count(counts, need.role) + need.count;
            }
            foreach (KeyValuePair<OperationalRole, int> need in counts) if (need.Value > 0) normalized.Add(new RoleCapacity(need.Key, need.Value));
            normalized.Sort((a, b) => a.role.CompareTo(b.role));
            return true;
        }

        private static bool TryCountTiers(IList<TierCount> tiers, bool committed, out int healthy, out int wounded, out string refusal)
        {
            healthy = 0; wounded = 0; refusal = null;
            if (tiers == null) { refusal = "MissingOrganizationTierCounts"; return false; }
            HashSet<Tier> seen = new HashSet<Tier>();
            foreach (TierCount tier in tiers)
            {
                if (tier == null || tier.tier < Tier.Recruit || tier.tier > Tier.Veteran || !seen.Add(tier.tier)
                    || tier.healthy < 0 || tier.wounded < 0 || (committed && tier.wounded != 0)
                    || healthy + (long)tier.healthy > int.MaxValue || wounded + (long)tier.wounded > int.MaxValue)
                { refusal = "InvalidOrganizationTierCounts"; return false; }
                healthy += tier.healthy; wounded += tier.wounded;
            }
            return true;
        }
    }
}
