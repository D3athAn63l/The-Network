using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using TheNetwork.Settings;
using Verse;

namespace TheNetwork.Domain.Contractors
{
    /// <summary>Contract kind keys (soft references to NetworkContractKindDef). Phase 2 has one.</summary>
    public static class ContractKinds
    {
        public const string Procurement = "Procurement";
    }

    /// <summary>Derived availability of an NPC contractor; never stored.</summary>
    public enum Availability : byte
    {
        Available = 0,
        Committed = 1,
        Recovering = 2,
        Exhausted = 3,
        Unavailable = 4,
        Ended = 5
    }

    /// <summary>What an operation checked out of a contractor: generic headcount and named people.</summary>
    public sealed class ForceCommitment
    {
        public List<TierCount> forces = new List<TierCount>();
        public List<CharacterId> characters = new List<CharacterId>();

        public int Headcount
        {
            get
            {
                int n = characters.Count;
                for (int i = 0; i < forces.Count; i++) n += forces[i].healthy;
                return n;
            }
        }
    }

    /// <summary>Abstract losses to apply to a contractor after a resolved operation.</summary>
    public sealed class CasualtyReport
    {
        /// <summary>Per tier, parallel to the committed forces: killed, wounded, captured, missing.</summary>
        public List<TierCount> killed = new List<TierCount>();
        public List<TierCount> wounded = new List<TierCount>();
        public List<TierCount> captured = new List<TierCount>();
        public List<TierCount> missing = new List<TierCount>();
        public List<CharacterFate> fates = new List<CharacterFate>();
        public int woundDays = 8;

        public static int Sum(List<TierCount> l)
        {
            int n = 0;
            for (int i = 0; i < l.Count; i++) n += l[i].healthy;
            return n;
        }
    }

    public enum Fate : byte
    {
        Unharmed = 0,
        Wounded = 1,
        Killed = 2,
        Captured = 3,
        Missing = 4
    }

    public sealed class CharacterFate : IExposable
    {
        public CharacterId character;
        public Fate fate;

        public void ExposeData()
        {
            NetScribe.Look(ref character, "character");
            NetScribe.LookEnum(ref fate, "fate", Fate.Unharmed);
        }
    }

    /// <summary>
    /// NPC contractors (ARCHITECTURE § 6.6.1, § 6.8; DATA_MODEL § 6): instantiation from the world's
    /// cast snapshot, the Solo and organization compositions, derived strength, experience and
    /// availability, force checkout, casualties and minimal succession. Never runs for the player.
    /// Nothing here creates a pawn.
    /// </summary>
    public sealed class ContractorService
    {
        public const string UpkeepJob = "contractor.upkeep";

        private static NamePools pools;

        private readonly DomainContext ctx;

        public ContractorService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        internal static NamePools Pools
        {
            get
            {
                if (pools == null)
                {
                    try
                    {
                        pools = NamePools.FromDefs();
                    }
                    catch (Exception)
                    {
                        pools = null;
                    }
                    if (pools == null || !pools.IsUsable) pools = NamePools.Fallback();
                }
                return pools;
            }
        }

        public static bool IsNpcContractor(NetworkActor a)
        {
            return a != null && a.kind != ActorKind.PlayerProxy && a.Has<ContractorSimulation>() && a.Has<ContractorProfile>();
        }

        public static bool IsSolo(NetworkActor a)
        {
            return a != null && a.kind == ActorKind.Individual && !a.Has<OrganizationProfile>();
        }

        private int SeedFor(ActorId id, string salt)
        {
            return NetHash.Combine(NetHash.Combine(ctx.networkSeed, id.Value), salt);
        }

        // ================================================================== instantiation

        /// <summary>
        /// Instantiates every contractor entry of the world's own cast snapshot that has no actor yet.
        /// Idempotent: a start-up that failed part-way, or a Phase 1 world loading Phase 2 for the first
        /// time, simply completes it. The global roster in ModSettings is never read here.
        /// </summary>
        public int InstantiateFromSnapshot()
        {
            int made = 0;
            for (int i = 0; i < ctx.cast.entries.Count; i++)
            {
                CastEntry e = ctx.cast.entries[i];
                if (e.kind != CastEntryKind.Contractor || e.actor.IsValid || e.contractor == null) continue;
                NetworkActor a = Instantiate(e.contractor, ProvenanceSource.GlobalCast, e.templateId);
                e.actor = a.id;
                made++;
            }
            return made;
        }

        /// <summary>A persistent world-generated newcomer (SIMULATION § 7). Returns null if none could be made.</summary>
        public NetworkActor CreateWorldGenerated(int index, string reasonKey)
        {
            List<string> avoid = new List<string>();
            for (int i = 0; i < ctx.actors.actors.Count; i++) avoid.Add(ctx.actors.actors[i].name.display);
            int castSeed = NetHash.Combine(ctx.networkSeed, "population.newcomer");
            CastGenerator gen = new CastGenerator(Pools, castSeed, () => null, avoid);
            ContractorTemplate t = gen.GenerateContractor(index);
            // Newcomers are mostly obscure: the world's famous names already exist.
            if (t.startingFame > FameBand.Local) t.startingFame = FameBand.Local;
            NetworkActor a = Instantiate(t, ProvenanceSource.WorldGenerated, "world:" + index);
            ContractorEvent e = EventFactory.Make<ContractorEvent>(EventKeys.ContractorCreated, Importance.Minor, a.id.Ref);
            e.actor = a.id;
            e.actorName = a.name.Display;
            e.reasonKey = reasonKey;
            ctx.bus.Publish(e);
            return a;
        }

        /// <summary>
        /// Builds one contractor actor from a template: a Solo (Individual + embodied Known Character) or
        /// an organization (Organization + OrganizationProfile). IssuerProfile only when the template's
        /// explicit canIssueWork is set. Everything is committed at the end, in one step.
        /// </summary>
        public NetworkActor Instantiate(ContractorTemplate t, ProvenanceSource source, string templateId)
        {
            bool solo = t.form == ContractorForm.Solo || t.form == ContractorForm.Specialist;
            NetworkActor a = new NetworkActor
            {
                id = new ActorId(ctx.ids.NextId()),
                kind = solo ? ActorKind.Individual : ActorKind.Organization,
                foundedTick = ctx.Now,
                name = new NameSnapshot { display = t.displayName, nick = t.nickname },
                provenance = new Provenance { source = source, templateId = templateId, importedTick = ctx.Now }
            };
            a.seed = SeedFor(a.id, "actor");
            a.reputation.fame = t.startingFame;
            NetRng rng = new NetRng(a.seed, "contractor.create");

            ContractorProfile profile = new ContractorProfile { capability = CapabilitySource.NpcSimulation, registeredTick = ctx.Now };
            profile.kinds.Add(ContractKinds.Procurement);
            profile.specialties.AddRange(t.specialties ?? new List<string>());
            a.Add(profile);

            ContractorSimulation sim = BuildSimulation(t, rng, solo);
            a.Add(sim);
            if (t.canIssueWork) a.Add(new IssuerProfile { budgetBand = solo ? 0 : 1 });

            List<KnownCharacter> people = new List<KnownCharacter>();
            CastGenerator names = new CastGenerator(Pools, NetHash.Combine(a.seed, "names"), () => null, null);
            if (solo)
            {
                KnownCharacter c = NewCharacter(a.name.Copy(), CharacterRole.Freelancer, a.id, 0.25f + 0.1f * (int)t.startingFame);
                c.embodiedBy = a.id;
                c.org = ActorId.None;
                a.bindings.embodies = c.id;
                people.Add(c);
                sim.skill = Clamp(BandCenter(t.startingExperience) + rng.Range(-0.03f, 0.03f), 0.05f, 1f);
            }
            else
            {
                OrganizationProfile org = BuildRoster(t, rng, a, names, people);
                a.Add(org);
                float vetShare = VeteranShare(org);
                sim.skill = Clamp((BandCenter(t.startingExperience) - 0.4f * vetShare) / 0.6f + rng.Range(-0.02f, 0.02f), 0.05f, 1f);
            }
            sim.careerStage = StartingCareer(t, rng);
            sim.morale.descriptor = MoraleModel.Evaluate(sim, MoraleDescriptor.Steady);

            // Committed together, so a failure part-way never leaves a half-built contractor.
            ctx.actors.Add(a);
            for (int i = 0; i < people.Count; i++) ctx.characters.Add(people[i]);
            ScheduleFirstUpkeep(a, sim);
            // Hidden spatial truth as soon as world data allows (otherwise at the next start-up or upkeep).
            ctx.Spatial?.EnsureInitialized(a);
            StateVersion.Bump();
            return a;
        }

        private ContractorSimulation BuildSimulation(ContractorTemplate t, NetRng rng, bool solo)
        {
            ContractorSimulation sim = new ContractorSimulation();
            int exp = (int)t.startingExperience;
            int tier = 1 + exp * 4 / 5 + (t.form == ContractorForm.Company ? 1 : 0) + (rng.Chance(0.25f) ? 1 : 0) - (rng.Chance(0.2f) ? 1 : 0);
            sim.equipment.tier = Math.Max(1, Math.Min(5, tier));
            sim.equipment.condition = rng.Range(0.72f, 1f);
            sim.equipment.specialties.AddRange(t.specialties ?? new List<string>());
            sim.doctrine = DoctrineFor(t.doctrineStyle, rng);
            sim.morale.cohesion = solo ? 1f : rng.Range(0.55f, 0.82f);
            sim.morale.confidence = Clamp(rng.Range(0.45f, 0.68f) + 0.03f * exp, 0f, 1f);
            sim.morale.fatigue = rng.Range(0.03f, 0.2f);
            sim.funds = StartingFunds(t.form, rng) * (1 + (int)t.startingFame) / 2 + StartingFunds(t.form, rng) / 2;
            sim.mobility = MobilityFor(t, solo);
            // Origin: an existing faction the contractor came from, or none. Missing content is ignored.
            List<Ports.FactionFacts> factions = ctx.world.LiveFactions();
            Ports.FactionFacts origin = null;
            string hint = t.originHints?.factionDefName;
            if (!string.IsNullOrEmpty(hint))
            {
                for (int i = 0; i < factions.Count && origin == null; i++) if (!factions[i].isPlayer && factions[i].defName == hint) origin = factions[i];
            }
            if (origin == null && rng.Chance(0.55f))
            {
                List<Ports.FactionFacts> humans = new List<Ports.FactionFacts>();
                for (int i = 0; i < factions.Count; i++)
                {
                    Ports.FactionFacts f = factions[i];
                    if (!f.isPlayer && f.humanlike && !f.hidden && !f.defeated && !f.temporary) humans.Add(f);
                }
                if (humans.Count > 0) origin = humans[rng.Range(0, humans.Count)];
            }
            if (origin != null)
            {
                sim.origin = ActorService.RefFrom(origin);
                sim.originSnapshot = origin.name;
            }
            return sim;
        }

        private static int StartingFunds(ContractorForm form, NetRng rng)
        {
            switch (form)
            {
                case ContractorForm.Solo:
                case ContractorForm.Specialist: return rng.RangeInclusive(300, 1400);
                case ContractorForm.Duo: return rng.RangeInclusive(700, 2400);
                case ContractorForm.Crew: return rng.RangeInclusive(1400, 3800);
                case ContractorForm.Team: return rng.RangeInclusive(2800, 7500);
                default: return rng.RangeInclusive(7000, 18000);
            }
        }

        private static MobilityProfile MobilityFor(ContractorTemplate t, bool solo)
        {
            MobilityProfile m = new MobilityProfile();
            if (t.mobility != null) m.modes.AddRange(t.mobility);
            if (!m.modes.Contains("Ground")) m.modes.Insert(0, "Ground");
            m.rangeBand = m.Has("LongRange") ? Band.High : (solo ? Band.Low : Band.Medium);
            m.speedBand = m.Has("RapidTransport") ? Band.High : Band.Medium;
            m.liftBand = m.Has("HeavyLift") ? Band.High : (solo ? Band.Low : (t.form == ContractorForm.Company ? Band.Medium : Band.Low));
            return m;
        }

        public static Doctrine DoctrineFor(string style, NetRng rng)
        {
            // caution, greed, loyalty, discretion, professionalism, ambition, cruelty
            float[] b;
            switch (style)
            {
                case "Aggressive": b = new[] { 0.25f, 0.5f, 0.5f, 0.35f, 0.45f, 0.7f, 0.55f }; break;
                case "Cautious": b = new[] { 0.8f, 0.4f, 0.6f, 0.6f, 0.6f, 0.35f, 0.2f }; break;
                case "Professional": b = new[] { 0.55f, 0.55f, 0.65f, 0.7f, 0.85f, 0.5f, 0.25f }; break;
                case "Opportunistic": b = new[] { 0.4f, 0.8f, 0.3f, 0.4f, 0.3f, 0.6f, 0.4f }; break;
                case "Scavenger": b = new[] { 0.55f, 0.6f, 0.45f, 0.5f, 0.45f, 0.4f, 0.3f }; break;
                case "Explorer": b = new[] { 0.5f, 0.45f, 0.55f, 0.55f, 0.6f, 0.65f, 0.2f }; break;
                default: b = new[] { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.3f }; break;
            }
            Doctrine d = new Doctrine { style = style };
            d.caution = Noise(b[0], rng);
            d.greed = Noise(b[1], rng);
            d.loyalty = Noise(b[2], rng);
            d.discretion = Noise(b[3], rng);
            d.professionalism = Noise(b[4], rng);
            d.ambition = Noise(b[5], rng);
            d.cruelty = Noise(b[6], rng);
            return d;
        }

        private static float Noise(float v, NetRng rng)
        {
            return Clamp(v + rng.Range(-0.12f, 0.12f), 0.02f, 0.98f);
        }

        private static CareerStage StartingCareer(ContractorTemplate t, NetRng rng)
        {
            int exp = (int)t.startingExperience;
            if (t.startingFame >= FameBand.Famous && exp <= (int)ExperienceBand.Experienced) return CareerStage.Declining;
            if (t.startingFame >= FameBand.Famous && exp >= (int)ExperienceBand.Veteran && rng.Chance(0.2f)) return CareerStage.Declining;
            if (exp >= (int)ExperienceBand.Elite) return CareerStage.Veteran;
            if (exp >= (int)ExperienceBand.Seasoned) return CareerStage.Established;
            return CareerStage.Rising;
        }

        private OrganizationProfile BuildRoster(ContractorTemplate t, NetRng rng, NetworkActor a, CastGenerator names, List<KnownCharacter> people)
        {
            OrganizationProfile org = new OrganizationProfile();
            int generic, lieutenants, extraKnown;
            switch (t.form)
            {
                case ContractorForm.Duo: generic = 0; lieutenants = 1; extraKnown = 0; org.capacity = 3; break;
                case ContractorForm.Crew: generic = rng.RangeInclusive(2, 4); lieutenants = rng.Chance(0.5f) ? 1 : 0; extraKnown = 0; org.capacity = 7; break;
                case ContractorForm.Team: generic = rng.RangeInclusive(5, 9); lieutenants = 1; extraKnown = rng.Chance(0.5f) ? 1 : 0; org.capacity = 14; break;
                default: generic = rng.RangeInclusive(12, 22); lieutenants = 2; extraKnown = rng.RangeInclusive(1, 2); org.capacity = 32; break;
            }
            float vet, reg;
            switch (t.startingExperience)
            {
                case ExperienceBand.Green: vet = 0.05f; reg = 0.3f; break;
                case ExperienceBand.Experienced: vet = 0.15f; reg = 0.45f; break;
                case ExperienceBand.Seasoned: vet = 0.25f; reg = 0.5f; break;
                case ExperienceBand.Veteran: vet = 0.4f; reg = 0.45f; break;
                case ExperienceBand.Elite: vet = 0.55f; reg = 0.4f; break;
                default: vet = 0.65f; reg = 0.3f; break;
            }
            int v = (int)Math.Round(generic * vet);
            int r = (int)Math.Round(generic * reg);
            int rec = Math.Max(0, generic - v - r);
            org.tiers.Add(new TierCount(Tier.Recruit, rec));
            org.tiers.Add(new TierCount(Tier.Regular, r));
            org.tiers.Add(new TierCount(Tier.Veteran, v));

            float leaderNotability = 0.35f + 0.12f * (int)t.startingFame + 0.05f * (int)t.startingExperience;
            KnownCharacter leader = NewPerson(names, rng, CharacterRole.Leader, a.id, leaderNotability);
            people.Add(leader);
            org.leader = leader.id;
            org.knownMembers.Add(leader.id);
            for (int i = 0; i < lieutenants; i++)
            {
                KnownCharacter lt = NewPerson(names, rng, CharacterRole.Lieutenant, a.id, leaderNotability * 0.7f);
                people.Add(lt);
                org.lieutenants.Add(lt.id);
                org.knownMembers.Add(lt.id);
            }
            for (int i = 0; i < extraKnown && org.knownMembers.Count < OrganizationProfile.MaxKnownMembers; i++)
            {
                KnownCharacter m = NewPerson(names, rng, CharacterRole.Specialist, a.id, leaderNotability * 0.5f);
                people.Add(m);
                org.knownMembers.Add(m.id);
            }
            return org;
        }

        private KnownCharacter NewPerson(CastGenerator names, NetRng rng, CharacterRole role, ActorId org, float notability)
        {
            string nick;
            string display = names.GeneratePersonName(rng, out nick);
            return NewCharacter(new NameSnapshot { display = display, nick = nick }, role, org, notability);
        }

        private KnownCharacter NewCharacter(NameSnapshot name, CharacterRole role, ActorId org, float notability)
        {
            return new KnownCharacter
            {
                id = new CharacterId(ctx.ids.NextId()),
                name = name,
                role = role,
                org = org,
                createdTick = ctx.Now,
                statusTick = ctx.Now,
                notability = Clamp(notability, 0.05f, 1f)
            };
        }

        public void ScheduleFirstUpkeep(NetworkActor a, ContractorSimulation sim)
        {
            int due = NetScheduler.StaggeredDue(ctx.Now + 1, a.seed, UpkeepJob, Ticks.PerDay);
            sim.nextUpkeepTick = due;
            sim.lastUpkeepTick = ctx.Now;
            ctx.scheduler.Schedule(UpkeepJob, due, a.id.Value);
        }

        // ================================================================== derived views (never stored)

        public static float BandCenter(ExperienceBand b)
        {
            switch (b)
            {
                case ExperienceBand.Green: return 0.15f;
                case ExperienceBand.Experienced: return 0.3f;
                case ExperienceBand.Seasoned: return 0.45f;
                case ExperienceBand.Veteran: return 0.6f;
                case ExperienceBand.Elite: return 0.73f;
                default: return 0.88f;
            }
        }

        public static ExperienceBand BandFor(float score)
        {
            if (score < 0.22f) return ExperienceBand.Green;
            if (score < 0.37f) return ExperienceBand.Experienced;
            if (score < 0.52f) return ExperienceBand.Seasoned;
            if (score < 0.67f) return ExperienceBand.Veteran;
            if (score < 0.8f) return ExperienceBand.Elite;
            return ExperienceBand.Legendary;
        }

        public static float VeteranShare(OrganizationProfile org)
        {
            int total = 0, vets = 0;
            for (int i = 0; i < org.tiers.Count; i++)
            {
                total += org.tiers[i].healthy + org.tiers[i].wounded;
                if (org.tiers[i].tier == Tier.Veteran) vets += org.tiers[i].healthy + org.tiers[i].wounded;
            }
            return total == 0 ? 0.5f : vets / (float)total;
        }

        /// <summary>Operational capability tier, derived from the simulation (never the fame tier).</summary>
        public static ExperienceBand Experience(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null) return ExperienceBand.Green;
            OrganizationProfile org = a.Get<OrganizationProfile>();
            float score = org == null ? sim.skill : 0.6f * sim.skill + 0.4f * VeteranShare(org);
            return BandFor(score);
        }

        public float Strength(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null) return 0f;
            if (sim.cachedStrength >= 0f) return sim.cachedStrength;
            float equip = (0.6f + 0.15f * sim.equipment.tier) * (0.6f + 0.4f * sim.equipment.condition);
            float people;
            OrganizationProfile org = a.Get<OrganizationProfile>();
            if (org == null)
            {
                KnownCharacter c = ctx.characters.Get(a.bindings.embodies);
                people = c != null && c.IsAvailable ? 3.5f * (0.5f + sim.skill) : 0f;
            }
            else
            {
                people = 0f;
                for (int i = 0; i < org.tiers.Count; i++) people += org.tiers[i].healthy * TierWeight(org.tiers[i].tier);
                for (int i = 0; i < org.knownMembers.Count; i++)
                {
                    KnownCharacter c = ctx.characters.Get(org.knownMembers[i]);
                    if (c != null && c.IsAvailable) people += c.id == org.leader ? 4f : 3f;
                }
                people *= 0.7f + 0.6f * sim.skill;
            }
            sim.cachedStrength = people * equip * MoraleModel.Factor(sim.morale.descriptor);
            return sim.cachedStrength;
        }

        public static float TierWeight(Tier t)
        {
            switch (t)
            {
                case Tier.Veteran: return 3.5f;
                case Tier.Regular: return 2f;
                default: return 1f;
            }
        }

        public KnownCharacter Embodied(NetworkActor a)
        {
            return a == null ? null : ctx.characters.Get(a.bindings.embodies);
        }

        public KnownCharacter Leader(NetworkActor a)
        {
            OrganizationProfile org = a?.Get<OrganizationProfile>();
            return org == null ? Embodied(a) : ctx.characters.Get(org.leader);
        }

        /// <summary>
        /// How many jobs this contractor can run at once: a Solo one, an organization one per six able
        /// people (1..3). The people already out on jobs count, so taking a job never shrinks the limit.
        /// </summary>
        public static int JobCapacity(NetworkActor a)
        {
            OrganizationProfile org = a?.Get<OrganizationProfile>();
            if (org == null) return 1;
            int people = org.Healthy + org.Committed + org.knownMembers.Count;
            return Math.Max(1, Math.Min(3, people / 6));
        }

        /// <summary>Already running as many jobs as it can: one more would exceed <see cref="JobCapacity"/>.</summary>
        public bool AtCapacity(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            return sim != null && sim.commitments.Count >= JobCapacity(a);
        }

        /// <summary>Checkouts that took a contractor past its job capacity (runtime diagnostic; always 0).</summary>
        public int overCapacityCheckouts;

        public float WoundedShare(NetworkActor a)
        {
            OrganizationProfile org = a?.Get<OrganizationProfile>();
            if (org == null)
            {
                KnownCharacter c = Embodied(a);
                return c != null && c.status == CharacterStatus.Wounded ? 1f : 0f;
            }
            int healthy = org.Healthy, wounded = org.Wounded;
            return healthy + wounded == 0 ? 0f : wounded / (float)(healthy + wounded);
        }

        public Availability AvailabilityOf(NetworkActor a)
        {
            if (a == null || a.status != ActorStatus.Active || a.quarantinedReason != null) return Availability.Ended;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            if (sim == null) return Availability.Unavailable;
            if (IsSolo(a))
            {
                KnownCharacter c = Embodied(a);
                if (c == null || !c.IsAlive) return Availability.Ended;
                if (c.status == CharacterStatus.Wounded) return Availability.Recovering;
                if (c.status != CharacterStatus.Active) return Availability.Unavailable;
            }
            else
            {
                OrganizationProfile org = a.Get<OrganizationProfile>();
                if (org != null && org.Healthy + AvailableKnown(a, org) == 0)
                {
                    // Nobody left at home: everyone is out on a job, hurt, or gone.
                    if (sim.commitments.Count > 0) return Availability.Committed;
                    return org.Wounded > 0 ? Availability.Recovering : Availability.Unavailable;
                }
                if (WoundedShare(a) > 0.5f) return Availability.Recovering;
            }
            if (sim.morale.descriptor == MoraleDescriptor.Exhausted) return Availability.Exhausted;
            if (sim.commitments.Count >= JobCapacity(a)) return Availability.Committed;
            return Availability.Available;
        }

        /// <summary>Known members at home and fit to go: alive, active, and not out on another live operation.</summary>
        private int AvailableKnown(NetworkActor a, OrganizationProfile org)
        {
            List<CharacterId> busy = Occupied(a, OperationId.None);
            int n = 0;
            for (int i = 0; i < org.knownMembers.Count; i++)
            {
                KnownCharacter c = ctx.characters.Get(org.knownMembers[i]);
                if (c != null && c.IsAvailable && !busy.Contains(c.id)) n++;
            }
            return n;
        }

        /// <summary>
        /// The named people of this contractor already out on a live operation (other than
        /// <paramref name="except"/>). Derived from the contractor's operation commitments, never stored:
        /// a person is out from checkout until the operation returns them, so one KnownCharacter is in
        /// at most one live abstract operation at a time.
        /// </summary>
        public List<CharacterId> Occupied(NetworkActor a, OperationId except)
        {
            List<CharacterId> busy = new List<CharacterId>();
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || ctx.operations == null) return busy;
            for (int i = 0; i < sim.commitments.Count; i++)
            {
                if (sim.commitments[i] == except) continue;
                Operations.Operation op = ctx.operations.Get(sim.commitments[i]);
                if (op == null || op.outcomeApplied) continue;
                for (int j = 0; j < op.characters.Count; j++) if (!busy.Contains(op.characters[j])) busy.Add(op.characters[j]);
            }
            return busy;
        }

        /// <summary>Named people listed on more than one live operation (an invariant; always 0).</summary>
        public int DoubleBooked()
        {
            if (ctx.operations == null) return 0;
            Dictionary<CharacterId, int> seen = new Dictionary<CharacterId, int>();
            int n = 0;
            for (int i = 0; i < ctx.operations.operations.Count; i++)
            {
                Operations.Operation op = ctx.operations.operations[i];
                if (op.IsFinished || op.outcomeApplied) continue;
                for (int j = 0; j < op.characters.Count; j++)
                {
                    int k;
                    seen.TryGetValue(op.characters[j], out k);
                    if (k == 1) n++;
                    seen[op.characters[j]] = k + 1;
                }
            }
            return n;
        }

        /// <summary>A broad doctrine label key (master § 29) from the doctrine values and specialties.</summary>
        public static string DoctrineLabel(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null) return "Unknown";
            Doctrine d = sim.doctrine;
            ContractorProfile p = a.Get<ContractorProfile>();
            bool salvage = p != null && (p.specialties.Contains("salvage") || p.specialties.Contains("recovery"));
            bool explore = p != null && (p.specialties.Contains("scouting") || p.specialties.Contains("vacuum work"));
            if (d.caution < 0.35f && d.ambition > 0.55f) return "Aggressive";
            if (d.caution > 0.68f) return "Cautious";
            if (d.professionalism > 0.72f) return "Professional";
            if (d.greed > 0.68f && d.professionalism < 0.45f) return "Opportunistic";
            if (salvage) return "Scavenger";
            if (explore || d.ambition > 0.6f) return "Explorer";
            return d.style ?? "Professional";
        }

        // ================================================================== commitments

        /// <summary>
        /// Checks people out to an operation. Organizations send most of their available headcount and,
        /// for dangerous work or small groups, their leader. A Solo sends itself.
        /// </summary>
        public ForceCommitment Checkout(NetworkActor a, OperationId op, float danger)
        {
            ForceCommitment f = new ForceCommitment();
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            if (sim != null && !sim.commitments.Contains(op))
            {
                if (sim.commitments.Count >= JobCapacity(a))
                {
                    overCapacityCheckouts++;
                    NetLog.WarnOnce(LogCategory.Contracts, "contractor.overcapacity." + a.id.Value, a.name.Display + " was checked out beyond its job capacity (" + sim.commitments.Count + " of " + JobCapacity(a) + ").");
                }
                sim.commitments.Add(op);
            }
            List<CharacterId> busy = Occupied(a, op);
            OrganizationProfile org = a.Get<OrganizationProfile>();
            if (org == null)
            {
                if (a.bindings.embodies.IsValid && !busy.Contains(a.bindings.embodies)) f.characters.Add(a.bindings.embodies);
            }
            else
            {
                float share = Clamp(0.45f + 0.25f * danger, 0.4f, 0.85f) / Math.Max(1, JobCapacity(a) - sim.commitments.Count + 1);
                for (int i = 0; i < org.tiers.Count; i++)
                {
                    TierCount t = org.tiers[i];
                    int take = (int)Math.Ceiling(t.healthy * share);
                    take = Math.Min(take, t.healthy);
                    if (take <= 0) continue;
                    t.healthy -= take;
                    org.TierOf(t.tier, true).healthy += take;
                    f.forces.Add(new TierCount(t.tier, take));
                }
                bool small = org.Healthy + org.Committed <= 4;
                for (int i = 0; i < org.knownMembers.Count; i++)
                {
                    KnownCharacter c = ctx.characters.Get(org.knownMembers[i]);
                    if (c == null || !c.IsAvailable || busy.Contains(c.id) || f.characters.Contains(c.id)) continue;
                    bool isLeader = c.id == org.leader;
                    bool goes = isLeader ? (small || danger > 0.55f) : (small || org.lieutenants.Contains(c.id) || danger > 0.4f);
                    if (goes) f.characters.Add(c.id);
                }
                if (f.Headcount == 0)
                {
                    KnownCharacter l = ctx.characters.Get(org.leader);
                    if (l != null && l.IsAvailable && !busy.Contains(l.id)) f.characters.Add(l.id);
                }
            }
            sim?.MarkDirty();
            return f;
        }

        /// <summary>Returns the survivors of an operation to the roster (unharmed if the operation was aborted).</summary>
        public void Return(NetworkActor a, OperationId op, ForceCommitment f, CasualtyReport losses)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim != null) sim.commitments.Remove(op);
            OrganizationProfile org = a?.Get<OrganizationProfile>();
            if (org != null && f != null)
            {
                for (int i = 0; i < f.forces.Count; i++)
                {
                    TierCount sent = f.forces[i];
                    int lost = Count(losses?.killed, sent.tier) + Count(losses?.captured, sent.tier) + Count(losses?.missing, sent.tier);
                    int hurt = Count(losses?.wounded, sent.tier);
                    TierCount c = org.TierOf(sent.tier, true);
                    c.healthy = Math.Max(0, c.healthy - sent.healthy);
                    int back = Math.Max(0, sent.healthy - lost - hurt);
                    org.TierOf(sent.tier).healthy += back;
                    if (hurt > 0) AddWounded(org, sent.tier, hurt, ctx.Now + Math.Max(1, losses.woundDays) * Ticks.PerDay);
                }
                org.committed.RemoveAll(t => t.healthy <= 0);
            }
            sim?.MarkDirty();
        }

        private static int Count(List<TierCount> l, Tier t)
        {
            if (l == null) return 0;
            int n = 0;
            for (int i = 0; i < l.Count; i++) if (l[i].tier == t) n += l[i].healthy;
            return n;
        }

        public static void AddWounded(OrganizationProfile org, Tier tier, int count, int dueTick)
        {
            if (count <= 0) return;
            org.TierOf(tier).wounded += count;
            for (int i = 0; i < org.woundedRecovery.Count; i++)
            {
                RecoveryBucket b = org.woundedRecovery[i];
                if (b.tier == tier && Math.Abs(b.dueTick - dueTick) <= Ticks.PerDay)
                {
                    b.count += count;
                    b.dueTick = Math.Max(b.dueTick, dueTick);
                    return;
                }
            }
            if (org.woundedRecovery.Count >= OrganizationProfile.MaxRecoveryBuckets)
            {
                // Merge into the latest bucket of the tier (or the latest overall): aggregated, bounded.
                RecoveryBucket latest = null;
                for (int i = 0; i < org.woundedRecovery.Count; i++)
                {
                    RecoveryBucket b = org.woundedRecovery[i];
                    if (b.tier == tier && (latest == null || b.dueTick > latest.dueTick)) latest = b;
                }
                if (latest != null)
                {
                    latest.count += count;
                    latest.dueTick = Math.Max(latest.dueTick, dueTick);
                    return;
                }
                org.woundedRecovery.Sort((a, b) => a.dueTick.CompareTo(b.dueTick));
                RecoveryBucket first = org.woundedRecovery[0];
                org.woundedRecovery.RemoveAt(0);
                org.TierOf(first.tier).wounded -= first.count;
                org.TierOf(first.tier).healthy += first.count;
            }
            org.woundedRecovery.Add(new RecoveryBucket { tier = tier, count = count, dueTick = dueTick });
        }

        // ================================================================== fates, casualties, succession

        /// <summary>
        /// Applies an operation's committed abstract results: headcount losses, wounded buckets, Known
        /// Character fates (records only), morale, and succession or the end of the actor. Legendary
        /// status protects nobody.
        /// </summary>
        public void ApplyCasualties(NetworkActor a, CasualtyReport r, ContractId contract, OperationId op, bool success)
        {
            if (a == null || r == null) return;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            OrganizationProfile org = a.Get<OrganizationProfile>();
            int killed = CasualtyReport.Sum(r.killed), wounded = CasualtyReport.Sum(r.wounded);
            int captured = CasualtyReport.Sum(r.captured), missing = CasualtyReport.Sum(r.missing);
            bool leaderLost = false;
            CharacterId oldLeader = org != null ? org.leader : CharacterId.None;
            for (int i = 0; i < r.fates.Count; i++)
            {
                CharacterFate f = r.fates[i];
                KnownCharacter c = ctx.characters.Get(f.character);
                if (c == null || f.fate == Fate.Unharmed) continue;
                bool isLeader = org != null ? c.id == org.leader : c.id == a.bindings.embodies;
                switch (f.fate)
                {
                    case Fate.Killed:
                        killed++;
                        SetStatus(c, CharacterStatus.Dead);
                        c.diedTick = ctx.Now;
                        c.deathCauseKey = "Operation";
                        PublishCharacter(EventKeys.CharacterKilled, isLeader ? Importance.Major : Importance.Notable, a, c, contract, op, isLeader);
                        if (isLeader && org != null)
                        {
                            leaderLost = true;
                            PublishCharacter(EventKeys.LeaderKilled, Importance.Major, a, c, contract, op, true);
                        }
                        break;
                    case Fate.Wounded:
                        wounded++;
                        SetStatus(c, CharacterStatus.Wounded);
                        c.woundedUntilTick = ctx.Now + Math.Max(2, r.woundDays) * Ticks.PerDay;
                        break;
                    case Fate.Captured:
                        captured++;
                        SetStatus(c, CharacterStatus.Captured);
                        if (isLeader && org != null) leaderLost = true;
                        break;
                    case Fate.Missing:
                        missing++;
                        SetStatus(c, CharacterStatus.Missing);
                        if (isLeader && org != null) leaderLost = true;
                        break;
                }
            }
            if (killed + wounded + captured + missing > 0)
            {
                ContractorEvent e = EventFactory.Make<ContractorEvent>(EventKeys.ContractorCasualties, killed > 0 ? Importance.Notable : Importance.Minor, a.id.Ref, contract.Ref);
                Fill(e, a, contract, op);
                e.killed = killed;
                e.wounded = wounded;
                e.captured = captured;
                e.missing = missing;
                ctx.bus.Publish(e);
                int headBefore = org == null ? 1 : org.Healthy + org.Wounded + org.Committed + org.knownMembers.Count + killed + captured + missing;
                float lossShare = headBefore == 0 ? 1f : (killed + captured + missing) / (float)headBefore;
                if (sim != null) MoraleModel.Shock(sim, lossShare, leaderLost, ctx.Now);
            }
            else if (sim != null && success)
            {
                MoraleModel.Success(sim);
            }
            if (sim != null)
            {
                sim.opsCompleted++;
                if (org != null) sim.opsSincePromotion++;
                // Survivors learn from the work, a little more from success.
                sim.skill = Clamp(sim.skill + (success ? 0.012f : 0.006f), 0f, 1f);
                sim.MarkDirty();
                MoraleShiftCheck(a, sim);
            }
            if (org == null)
            {
                KnownCharacter self = Embodied(a);
                if (self != null && !self.IsAlive) EndActor(a, "Died");
            }
            else if (leaderLost)
            {
                RunSuccession(a, oldLeader);
            }
        }

        private void SetStatus(KnownCharacter c, CharacterStatus s)
        {
            c.status = s;
            c.statusTick = ctx.Now;
        }

        /// <summary>
        /// Minimal succession (SIMULATION § 4.5): a living lieutenant first, then the best living Known
        /// Character, then an abstract Veteran promoted into a new record; with nobody left, the
        /// organization dissolves. The organization keeps its ActorId.
        /// </summary>
        public void RunSuccession(NetworkActor a, CharacterId oldLeaderId)
        {
            OrganizationProfile org = a?.Get<OrganizationProfile>();
            if (org == null || a.status != ActorStatus.Active) return;
            KnownCharacter old = ctx.characters.Get(oldLeaderId);
            KnownCharacter next = null;
            for (int pass = 0; pass < 2 && next == null; pass++)
            {
                List<CharacterId> pool = pass == 0 ? org.lieutenants : org.knownMembers;
                for (int i = 0; i < pool.Count; i++)
                {
                    KnownCharacter c = ctx.characters.Get(pool[i]);
                    if (c == null || c.id == oldLeaderId || !c.IsAlive || c.status == CharacterStatus.Captured || c.status == CharacterStatus.Missing) continue;
                    if (next == null || c.notability > next.notability) next = c;
                }
            }
            if (next == null)
            {
                Tier? from = null;
                for (int t = (int)Tier.Veteran; t >= 0 && from == null; t--)
                {
                    if (org.TierOf((Tier)t).healthy > 0) from = (Tier)t;
                }
                if (from != null)
                {
                    org.TierOf(from.Value).healthy--;
                    NetRng rng = new NetRng(a.seed, "succession", org.succession.successions);
                    CastGenerator names = new CastGenerator(Pools, NetHash.Combine(a.seed, "names.succession"), () => null, null);
                    next = NewPerson(names, rng, CharacterRole.Member, a.id, 0.3f);
                    ctx.characters.Add(next);
                    if (org.knownMembers.Count < OrganizationProfile.MaxKnownMembers) org.knownMembers.Add(next.id);
                    PublishCharacter(EventKeys.CharacterPromoted, Importance.Notable, a, next, ContractId.None, OperationId.None, false);
                }
            }
            org.lieutenants.Remove(oldLeaderId);
            if (old != null && !old.IsAlive) org.knownMembers.Remove(oldLeaderId);
            if (next == null)
            {
                EndActor(a, "NoSuccessor");
                return;
            }
            org.lieutenants.Remove(next.id);
            if (!org.knownMembers.Contains(next.id) && org.knownMembers.Count < OrganizationProfile.MaxKnownMembers) org.knownMembers.Add(next.id);
            next.role = CharacterRole.Leader;
            next.notability = Clamp(next.notability + 0.1f, 0f, 1f);
            if (old != null && old.IsAlive) old.role = CharacterRole.Member;
            org.leader = next.id;
            org.succession.successions++;
            org.succession.lastSuccessionTick = ctx.Now;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            if (sim != null)
            {
                MoraleModel.Shock(sim, 0.1f, true, ctx.Now);
                // A new leader nudges the group's habits a little (bounded; SIMULATION § 4.3).
                NetRng drift = new NetRng(a.seed, "succession.doctrine", org.succession.successions);
                sim.doctrine.caution = Clamp(sim.doctrine.caution + drift.Range(-0.03f, 0.03f), 0.02f, 0.98f);
                sim.doctrine.greed = Clamp(sim.doctrine.greed + drift.Range(-0.03f, 0.03f), 0.02f, 0.98f);
                sim.MarkDirty();
            }
            ContractorEvent e = EventFactory.Make<ContractorEvent>(EventKeys.LeaderSucceeded, Importance.Notable, a.id.Ref, next.id.Ref);
            e.actor = a.id;
            e.actorName = a.name.Display;
            e.character = oldLeaderId;
            e.characterName = old?.name.Display;
            e.successor = next.id;
            e.successorName = next.name.Display;
            ctx.bus.Publish(e);
        }

        /// <summary>The actor ends (a Solo died, or an organization has nobody left to lead it).</summary>
        public void EndActor(NetworkActor a, string reasonKey)
        {
            if (a == null || a.status != ActorStatus.Active) return;
            a.status = ActorStatus.Dissolved;
            a.endedTick = ctx.Now;
            a.endReasonKey = reasonKey;
            ContractorProfile p = a.Get<ContractorProfile>();
            if (p != null) p.suspended = true;
            ctx.scheduler.Cancel(UpkeepJob, a.id.Value);
            ctx.Spatial?.OnActorEnded(a);
            ContractorEvent e = EventFactory.Make<ContractorEvent>(EventKeys.ContractorEnded, Importance.Major, a.id.Ref);
            e.actor = a.id;
            e.actorName = a.name.Display;
            e.reasonKey = reasonKey;
            ctx.bus.Publish(e);
            StateVersion.Bump();
        }

        public void MoraleShiftCheck(NetworkActor a, ContractorSimulation sim)
        {
            MoraleDescriptor before = sim.morale.descriptor;
            MoraleDescriptor after = MoraleModel.Evaluate(sim, before);
            if (after == before) return;
            sim.morale.descriptor = after;
            sim.morale.descriptorTick = ctx.Now;
            sim.MarkDirty();
            ContractorEvent e = EventFactory.Make<ContractorEvent>(EventKeys.MoraleShifted, Importance.Minor, a.id.Ref);
            e.actor = a.id;
            e.actorName = a.name.Display;
            e.descriptorKey = after.ToString();
            e.reasonKey = before.ToString();
            ctx.bus.Publish(e);
        }

        private void PublishCharacter(string key, Importance importance, NetworkActor a, KnownCharacter c, ContractId contract, OperationId op, bool leader)
        {
            ContractorEvent e = EventFactory.Make<ContractorEvent>(key, importance, a.id.Ref, c.id.Ref, contract.Ref);
            Fill(e, a, contract, op);
            e.character = c.id;
            e.characterName = c.name.Display;
            e.leader = leader;
            ctx.bus.Publish(e);
        }

        private static void Fill(ContractorEvent e, NetworkActor a, ContractId contract, OperationId op)
        {
            e.actor = a.id;
            e.actorName = a.name.Display;
            e.contract = contract;
            e.operation = op;
        }

        /// <summary>
        /// Keeps a contractor's Intel contact profile in step with what it is (SourcePolicies.ForContractor)
        /// and whether the player knows it: a famous name, or someone the player has dealt with. Frozen
        /// search terms are never touched: running searches keep what they agreed.
        /// </summary>
        public void RefreshIntelSource(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            ContractorProfile p = a?.Get<ContractorProfile>();
            if (sim == null || p == null || a.kind == ActorKind.PlayerProxy) return;
            ActorId player = ctx.actors.PlayerProxyId;
            bool known = a.reputation.fame >= FameBand.Established
                || ctx.Relations.Get(player, a.id).familiarity > 0f || ctx.Relations.Get(a.id, player).familiarity > 0f;
            IntelSourceProfile fresh = null;
            if (known && a.status == ActorStatus.Active)
            {
                int topics = 0;
                Knowledge.KnowledgeBook book = ctx.knowledge.Get(a.id);
                if (book != null)
                {
                    for (int i = 0; i < book.entries.Count; i++)
                    {
                        if (book.entries[i].topic.StartsWith("thing:", StringComparison.Ordinal) && ctx.Knowledge.Proficiency(a.id, book.entries[i].topic) >= 0.3f) topics++;
                    }
                }
                fresh = Intel.SourcePolicies.ForContractor(p.specialties, Experience(a), a.reputation.fame, sim.mobility.rangeBand, sim.doctrine.professionalism, sim.doctrine.discretion, topics);
            }
            IntelSourceProfile current = a.Get<IntelSourceProfile>();
            if (fresh == null)
            {
                if (current != null) a.components.Remove(current);
                return;
            }
            if (current == null)
            {
                a.Add(fresh);
                return;
            }
            current.speedBand = fresh.speedBand;
            current.reliabilityBand = fresh.reliabilityBand;
            current.feeBand = fresh.feeBand;
            current.feePolicyKey = fresh.feePolicyKey;
            current.continuationPolicyKey = fresh.continuationPolicyKey;
            current.discretion = fresh.discretion;
            current.specialties.Clear();
            current.specialties.AddRange(fresh.specialties);
        }

        public static float Clamp(float v, float min, float max)
        {
            return v < min ? min : (v > max ? max : v);
        }
    }
}
