using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Settings;

namespace TheNetwork.Tests
{
    public static class Phase32bCompositionTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Phys32b.CompositionV1_GoldenRecipeAndRepresentativeForms", GoldenRecipe));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.CompositionV1_FrozenHashSelectionAndRoleBound", RoleBound));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.CompositionV1_MutableActorFactsDoNotDefineRecipe", ImmutableRecipe));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Seats_ConservationAndPinsOverrideQuotas", Conservation));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Seats_CurrentLivingMembershipAndMalformedRefusal", CurrentMembership));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Seats_DeadPinVacatesSeatWithoutDeletingIdentity", DeadPin));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Mission_UsesMatchingPinsAndHealthySourceTiers", Mission));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Mission_UnavailablePinCannotBecomeStranger", UnavailablePin));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Mission_OptionalShortagesShrinkRequiredShortagesRefuse", MissionShortages));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Mission_ActiveSlotsAndUnknownCommitmentsConsumeCapacity", ActiveSlots));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Mission_DiscretionaryTargetAndSizePolicy", SizePolicy));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Roles_EagerAndLegacyCohortAssignmentMatch", RoleInitialization));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Roles_RankHistoryAndFirstUseTimeNeverChangeIdentity", StableCohort));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Roles_SuccessionReloadKeepsMedicRole", SuccessionReload));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Roles_BoundUnsetAndMalformedOriginsFailBeforeWrites", RoleRefusals));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Composition_SourceIsPureAndAddsNoPersistedRoster", SourcePure));
        }

        private static CompositionTemplate Template(int capacity = 7, params string[] specialties)
        {
            CompositionTemplate template;
            string refusal;
            T.Check(OrganizationCompositionV1.TryDerive(123, capacity, specialties, out template, out refusal), "derived Composition v1: " + refusal);
            return template;
        }

        private static string Key(IEnumerable<RoleCapacity> entries)
        { return string.Join(",", entries.Select(entry => entry.role + ":" + entry.count).ToArray()); }

        private static NetworkActor Actor(int anonymous, params string[] specialties)
        {
            NetworkActor actor = new NetworkActor { id = new ActorId(100), kind = ActorKind.Organization, seed = 123, foundedTick = 10 };
            actor.Add(new ContractorProfile { specialties = specialties.ToList() });
            actor.Add(new ContractorSimulation());
            actor.Add(new OrganizationProfile { capacity = 7, tiers = new List<TierCount> { new TierCount(Tier.Regular, anonymous) } });
            return actor;
        }

        private static KnownCharacter Pin(NetworkActor actor, List<KnownCharacter> people, int id, OperationalRole role, CharacterStatus status = CharacterStatus.Active)
        {
            KnownCharacter character = new KnownCharacter { id = new CharacterId(id), org = actor.id, opRole = role, createdTick = actor.foundedTick, status = status };
            actor.Get<OrganizationProfile>().knownMembers.Add(character.id);
            people.Add(character);
            return character;
        }

        private static OrganizationSeats Seats(NetworkActor actor, List<KnownCharacter> people)
        {
            OrganizationSeats seats;
            string refusal;
            T.Check(OrganizationSeatPolicy.TryApportion(actor, people, out seats, out refusal), "seat apportionment succeeds: " + refusal);
            return seats;
        }

        private static int Anonymous(OrganizationSeats seats, OperationalRole role)
        { return seats.roles.Where(seat => seat.role == role).Sum(seat => seat.anonymous); }

        private static bool Select(NetworkActor actor, OrganizationSeats seats, RoleCapacity[] required, RoleCapacity[] optional,
            out OrganizationMission mission, out string refusal, RoleCapacity[] active = null, Func<KnownCharacter, bool> available = null)
        { return OrganizationSeatPolicy.TrySelectMission(actor, seats, required, optional, active, available, out mission, out refusal); }

        private static void GoldenRecipe()
        {
            CompositionTemplate crew = Template(7, "escort", "medical");
            T.Eq("Leader:1,Rifleman:2,Heavy:1,Medic:1", Key(crew.weights), "accepted escort/medical weights");
            List<RoleCapacity> quotas;
            string refusal;
            T.Check(OrganizationCompositionV1.TryQuotas(crew, 5, out quotas, out refusal), "crew quotas");
            T.Eq("Leader:1,Rifleman:2,Heavy:1,Medic:1", Key(quotas), "accepted five-person crew");
            CompositionTemplate salvage = Template(14, "salvage", "medical", "logistics");
            T.Check(OrganizationCompositionV1.TryQuotas(salvage, 8, out quotas, out refusal), "technical quotas");
            T.Eq("Leader:1,Rifleman:2,Medic:1,Technician:2,Logistician:2", Key(quotas), "accepted eight-person technical team");
            foreach (int capacity in new[] { 3, 7, 14, 32 })
            {
                CompositionTemplate form = Template(capacity, "escort", "medical");
                T.Check(OrganizationCompositionV1.TryQuotas(form, capacity, out quotas, out refusal), "origin capacity supported");
                T.Eq(capacity, quotas.Sum(q => q.count), "full origin quotas conserved");
                T.Eq(1, quotas.Single(q => q.role == OperationalRole.Leader).count, "one operational leader capacity");
            }
            CompositionTemplate invalid;
            T.Check(!OrganizationCompositionV1.TryDerive(123, 5, new[] { "medical" }, out invalid, out refusal), "unknown origin form refuses instead of inventing one");
            T.Eq("UnknownOrganizationOriginCapacity", refusal, "diagnostic states malformed origin");
            T.Eq("Leader:1,Rifleman:2,Specialist:1", Key(Template(7, "custom specialty").weights), "existing unknown-specialty fallback retained");
        }

        private static void RoleBound()
        {
            string[] all = { "combat acquisition", "demolition", "medical", "scouting", "vacuum work", "salvage", "logistics", "animal handling" };
            T.Eq("Leader:1,Marksman:1,Rifleman:2,Heavy:1,Medic:1,Scout:1,Engineer:1,Specialist:1", Key(Template(32, all).weights), "frozen v1 hash trimming vector");
            for (int seed = -64; seed < 64; seed++)
            {
                CompositionTemplate a, b;
                string refusal;
                T.Check(OrganizationCompositionV1.TryDerive(seed, 32, all, out a, out refusal), "all-specialty recipe");
                T.Check(OrganizationCompositionV1.TryDerive(seed, 32, all.Reverse().Concat(all).ToArray(), out b, out refusal), "permuted duplicate specialty recipe");
                T.Eq(Key(a.weights), Key(b.weights), "input ordering/duplicates never alter role set");
                T.Eq(8, a.weights.Count, "maximum eight distinct recipe entries");
                T.Eq(8, a.weights.Select(w => w.role).Distinct().Count(), "role entries distinct");
            }
        }

        private static void ImmutableRecipe()
        {
            NetworkActor actor = Actor(4, "escort", "medical");
            CompositionTemplate before = Template(actor.Get<OrganizationProfile>().capacity, actor.Get<ContractorProfile>().specialties.ToArray());
            actor.Get<OrganizationProfile>().tiers[0].healthy = 300;
            actor.Get<OrganizationProfile>().leader = new CharacterId(900);
            actor.Get<ContractorSimulation>().skill = 1f;
            actor.Get<ContractorSimulation>().funds = 999999;
            actor.Get<ContractorSimulation>().doctrine.professionalism = 0f;
            actor.reputation.SetBand(FameBand.Legendary);
            actor.endedTick = 1000000;
            CompositionTemplate after = Template(actor.Get<OrganizationProfile>().capacity, actor.Get<ContractorProfile>().specialties.ToArray());
            T.Eq(Key(before.weights), Key(after.weights), "mutating all live facts does not change origin recipe");
            T.Throws(() => ((IList<RoleCapacity>)before.weights).Add(new RoleCapacity(OperationalRole.Medic, 99)), "derived weights are read-only");
        }

        private static void Conservation()
        {
            for (int generic = 0; generic <= 40; generic++)
            for (int medics = 0; medics <= 7; medics++)
            {
                NetworkActor actor = Actor(generic, "escort", "medical");
                List<KnownCharacter> people = new List<KnownCharacter>();
                for (int i = 0; i < medics; i++) Pin(actor, people, 200 + i, OperationalRole.Medic);
                OrganizationSeats seats = Seats(actor, people);
                T.Eq(generic + medics, seats.living, "actual living count conserved");
                T.Eq(generic + medics, seats.roles.Sum(s => s.pinned + s.anonymous), "all quotas sum to real current humans");
                T.Eq(generic, seats.roles.Sum(s => s.anonymous), "anonymous count preserved exactly");
                T.Eq(medics, seats.roles.Sum(s => s.pinned), "pins never clipped at six or below role quota");
                T.Check(seats.roles.All(s => s.anonymous >= 0 && s.pinned >= 0), "no negative seats");
            }
            NetworkActor five = Actor(4, "escort", "medical");
            List<KnownCharacter> named = new List<KnownCharacter>();
            Pin(five, named, 201, OperationalRole.Medic);
            T.Eq(0, Anonymous(Seats(five, named), OperationalRole.Medic), "pinned Medic fills its role quota");
        }

        private static void CurrentMembership()
        {
            NetworkActor actor = Actor(4, "escort", "medical");
            List<KnownCharacter> people = new List<KnownCharacter>();
            foreach (CharacterStatus status in Enum.GetValues(typeof(CharacterStatus))) Pin(actor, people, 200 + (int)status, OperationalRole.Rifleman, status);
            actor.Get<OrganizationProfile>().tiers[0].wounded = 2;
            actor.Get<OrganizationProfile>().committed.Add(new TierCount(Tier.Veteran, 3));
            OrganizationSeats seats = Seats(actor, people);
            T.Eq(13, seats.living, "4 healthy+2 wounded+3 committed+Active/Wounded/Captured/Missing pins");
            T.Eq(4, seats.pins.Count, "dead/lost/retired/defected historical people excluded");
            T.Check(people.Count == 8, "history preserved");
            actor.Get<OrganizationProfile>().knownMembers.Add(new CharacterId(999));
            string refusal;
            T.Check(!OrganizationSeatPolicy.TryApportion(actor, people, out seats, out refusal), "unresolved membership refuses");
            actor.Get<OrganizationProfile>().knownMembers.RemoveAt(actor.Get<OrganizationProfile>().knownMembers.Count - 1);
            actor.Get<OrganizationProfile>().tiers[0].healthy = -1;
            T.Check(!OrganizationSeatPolicy.TryApportion(actor, people, out seats, out refusal), "negative source stock refuses");
        }

        private static void DeadPin()
        {
            NetworkActor actor = Actor(4, "escort", "medical");
            List<KnownCharacter> people = new List<KnownCharacter>();
            KnownCharacter medic = Pin(actor, people, 201, OperationalRole.Medic);
            T.Eq(0, Anonymous(Seats(actor, people), OperationalRole.Medic), "living Medic consumes quota");
            medic.status = CharacterStatus.Dead;
            T.Eq(1, Anonymous(Seats(actor, people), OperationalRole.Medic), "dead pin vacates living role capacity");
            T.Eq(OperationalRole.Medic, medic.opRole, "death retains historical identity");
            T.Eq(1, actor.Get<OrganizationProfile>().knownMembers.Count, "no history record deleted");
            medic.status = CharacterStatus.Active;
            actor.Get<OrganizationProfile>().tiers[0].healthy = 20;
            T.Eq(1, Seats(actor, people).pins.Count, "large size never deletes an old living pin");
        }

        private static void Mission()
        {
            NetworkActor actor = Actor(4, "escort", "medical");
            List<KnownCharacter> people = new List<KnownCharacter>();
            KnownCharacter medic = Pin(actor, people, 201, OperationalRole.Medic);
            actor.Get<OrganizationProfile>().tiers.Clear();
            actor.Get<OrganizationProfile>().tiers.Add(new TierCount(Tier.Veteran, 3));
            actor.Get<OrganizationProfile>().tiers.Add(new TierCount(Tier.Recruit, 1));
            OrganizationMission mission;
            string refusal;
            T.Check(Select(actor, Seats(actor, people), new[] { new RoleCapacity(OperationalRole.Medic, 1), new RoleCapacity(OperationalRole.Rifleman, 2) }, null, out mission, out refusal), "role-correct mission: " + refusal);
            T.Eq(medic.id, mission.members.Single(m => m.role == OperationalRole.Medic).character, "same matching known person chosen");
            T.Eq(3, mission.members.Count, "bounded requested subset");
            T.Eq(Tier.Recruit, mission.members.First(m => !m.IsNamed).tier, "stable source-tier order independent of stored order");
            T.Eq(Tier.Veteran, mission.members.Last(m => !m.IsNamed).tier, "next healthy tier used exactly once");
            T.Eq(4, actor.Get<OrganizationProfile>().Healthy, "selection reads without spending stock");
            T.Check(mission.members.Where(m => !m.IsNamed).All(m => m.mayConcretizeByPresence), "small anonymous seats policy-eligible, not yet promoted");
            T.Check(!Select(actor, Seats(actor, people), new[] { new RoleCapacity(OperationalRole.Rifleman, 9) }, null, out mission, out refusal), "nine-member request refuses");
        }

        private static void UnavailablePin()
        {
            foreach (CharacterStatus status in new[] { CharacterStatus.Wounded, CharacterStatus.Captured, CharacterStatus.Missing })
            {
                NetworkActor actor = Actor(4, "escort", "medical");
                List<KnownCharacter> people = new List<KnownCharacter>();
                Pin(actor, people, 201, OperationalRole.Medic, status);
                OrganizationMission mission;
                string refusal;
                T.Check(!Select(actor, Seats(actor, people), new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out mission, out refusal), "unavailable " + status + " Medic keeps seat, no stranger substitutes");
            }
            NetworkActor deployed = Actor(4, "escort", "medical");
            List<KnownCharacter> pins = new List<KnownCharacter>();
            KnownCharacter medic = Pin(deployed, pins, 201, OperationalRole.Medic);
            medic.episode = new EpisodeId(20);
            OrganizationMission absent;
            string why;
            T.Check(!Select(deployed, Seats(deployed, pins), new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out absent, out why), "deployed Medic cannot become anonymous Medic");
            medic.episode = EpisodeId.None;
            T.Check(!Select(deployed, Seats(deployed, pins), new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out absent, out why, available: c => false), "operation-committed matching pin remains unavailable");
        }

        private static void MissionShortages()
        {
            NetworkActor actor = Actor(3, "escort");
            List<KnownCharacter> people = new List<KnownCharacter>();
            Pin(actor, people, 201, OperationalRole.Heavy);
            OrganizationMission mission;
            string refusal;
            T.Check(Select(actor, Seats(actor, people), new[] { new RoleCapacity(OperationalRole.Rifleman, 1) }, new[] { new RoleCapacity(OperationalRole.Medic, 1) }, out mission, out refusal), "optional shortage shrinks");
            T.Eq(1, mission.members.Count, "no wrong-role Heavy forced into Medic need");
            T.Eq(OperationalRole.Medic, mission.optionalShortages[0].role, "specific missing role diagnostic");
            T.Check(!Select(actor, Seats(actor, people), new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out mission, out refusal), "required absent Medic refuses");
            T.Check(mission == null, "failed plan yields no partial mission");
        }

        private static void ActiveSlots()
        {
            NetworkActor actor = Actor(2, "escort", "medical");
            actor.Get<OrganizationProfile>().committed.Add(new TierCount(Tier.Regular, 2));
            OrganizationSeats seats = Seats(actor, new List<KnownCharacter>());
            OrganizationMission mission;
            string refusal;
            T.Check(!Select(actor, seats, new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out mission, out refusal,
                new[] { new RoleCapacity(OperationalRole.Medic, 1) }), "other active Medic slot consumes actual role capacity");
            T.Check(!Select(actor, seats, new[] { new RoleCapacity(OperationalRole.Rifleman, 3) }, null, out mission, out refusal), "abstractly committed humans are not healthy anonymous vacancies");
            T.Eq(4, seats.living, "commitments count exactly once toward current membership");
        }

        private static void SizePolicy()
        {
            T.Check(OrganizationSeatPolicy.MayConcretizeByPresence(6, OperationalRole.Rifleman), "small boundary includes ordinary rank-and-file");
            T.Check(!OrganizationSeatPolicy.MayConcretizeByPresence(7, OperationalRole.Rifleman), "mid boundary ordinary rank-and-file require strong evidence");
            T.Check(OrganizationSeatPolicy.MayConcretizeByPresence(12, OperationalRole.Medic), "mid top boundary permits role-defining P0");
            T.Check(!OrganizationSeatPolicy.MayConcretizeByPresence(13, OperationalRole.Medic), "large boundary no anonymous P0");
            NetworkActor actor = Actor(6, "escort", "medical");
            List<KnownCharacter> people = new List<KnownCharacter>();
            for (int i = 0; i < 6; i++) Pin(actor, people, 201 + i, OperationalRole.Rifleman);
            OrganizationMission mission;
            string refusal;
            T.Check(!Select(actor, Seats(actor, people), new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out mission, out refusal), "full discretionary target refuses new remembered mid-size specialist");
            actor.Get<OrganizationProfile>().tiers[0].healthy++;
            T.Check(Select(actor, Seats(actor, people), new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out mission, out refusal), "large anonymous strong-only slot can be placed despite full cap: " + refusal);
            T.Check(!mission.members[0].mayConcretizeByPresence, "large specialist presence creates no obligation");
            Pin(actor, people, 299, OperationalRole.Medic);
            T.Eq(7, Seats(actor, people).pins.Count, "strong overflow people remain full members");
        }

        private static void RoleInitialization()
        {
            foreach (ContractorForm form in new[] { ContractorForm.Duo, ContractorForm.Crew, ContractorForm.Team, ContractorForm.Company })
            {
                TestNet n = new TestNet(3109);
                NetworkActor actor = PhysicalLifecycleTests.Make(n, form, "roles");
                List<KnownCharacter> people = n.ctx.characters.characters.Where(c => c.org == actor.id).ToList();
                string first = string.Join(",", people.OrderBy(c => c.id.Value).Select(c => c.opRole.ToString()).ToArray());
                T.Eq(OperationalRole.Leader, n.ctx.characters.Get(actor.Get<OrganizationProfile>().leader).opRole, "original lowest-id leader gets operational Leader");
                T.Check(people.All(c => c.opRole != OperationalRole.Unset), "original leader/lieutenant/specialist roles initialized eagerly");
                foreach (KnownCharacter person in people) person.opRole = OperationalRole.Unset;
                n.clock.Now = 1000000;
                T.Eq(people.Count, n.ctx.Contractors.EnsureOrganizationRoles(), "old-save Unset roles initialized without observing time");
                T.Eq(first, string.Join(",", people.OrderBy(c => c.id.Value).Select(c => c.opRole.ToString()).ToArray()), "eager and legacy assignment identical");
                T.Eq(0, n.ctx.Contractors.EnsureOrganizationRoles(), "write-once compatibility pass idempotent");
            }
        }

        private static void StableCohort()
        {
            NetworkActor actor = Actor(0, "escort", "medical");
            List<KnownCharacter> people = new List<KnownCharacter>();
            for (int i = 0; i < 5; i++) Pin(actor, people, 201 + i, OperationalRole.Unset);
            List<OrganizationRoleAssignment> before, after;
            string refusal;
            T.Check(OrganizationCompositionV1.TryPlanRoleInitialization(actor, people, out before, out refusal), "baseline origin-role plan");
            people[0].status = CharacterStatus.Dead;
            people[1].role = CharacterRole.Leader;
            actor.Get<OrganizationProfile>().leader = people[1].id;
            people[2].status = CharacterStatus.Defected;
            people.Reverse();
            T.Check(OrganizationCompositionV1.TryPlanRoleInitialization(actor, people, out after, out refusal), "historical cohort-role plan");
            T.Eq(string.Join(",", before.Select(a => a.character.id.Value + ":" + a.role).ToArray()), string.Join(",", after.Select(a => a.character.id.Value + ":" + a.role).ToArray()), "death/defection/order/current rank never reassign origin slots");
            T.Check(people.All(c => c.opRole == OperationalRole.Unset), "planning itself performs no role writes");
            KnownCharacter later = Pin(actor, people, 299, OperationalRole.Unset);
            later.createdTick++;
            T.Check(OrganizationCompositionV1.TryPlanRoleInitialization(actor, people, out before, out refusal), "later legacy person hash fallback");
            later.role = CharacterRole.Leader;
            later.status = CharacterStatus.Captured;
            T.Check(OrganizationCompositionV1.TryPlanRoleInitialization(actor, people, out after, out refusal), "later legacy rank/history change");
            T.Eq(before.Single(a => a.character == later).role, after.Single(a => a.character == later).role, "later unknown historic slot gets stable immutable hash role");
        }

        private static void SuccessionReload()
        {
            TestNet n = new TestNet(3111);
            NetworkActor actor = PhysicalLifecycleTests.Make(n, ContractorForm.Company, "succession");
            OrganizationProfile org = actor.Get<OrganizationProfile>();
            KnownCharacter medic = n.ctx.characters.Get(org.knownMembers[1]);
            medic.opRole = OperationalRole.Medic;
            medic.role = CharacterRole.Leader;
            org.leader = medic.id;
            ActorId actorId = actor.id;
            CharacterId medicId = medic.id;
            PhysicalLifecycleTests.SaveLoad(n);
            actor = n.ctx.actors.Get(actorId);
            medic = n.ctx.characters.Get(medicId);
            n.ctx.Contractors.EnsureOrganizationRoles();
            T.Eq(CharacterRole.Leader, medic.role, "organizational succession survives real Scribe reload");
            T.Eq(OperationalRole.Medic, medic.opRole, "Medic remains Medic after rank change/reload");
            T.Eq(medicId, actor.Get<OrganizationProfile>().leader, "same organizational person retained");
            foreach (KnownCharacter person in n.ctx.characters.characters.Where(c => c.org == actor.id)) person.opRole = OperationalRole.Unset;
            PhysicalLifecycleTests.SaveLoad(n);
            T.Check(n.ctx.Contractors.EnsureOrganizationRoles() > 0, "old-save Unset cohort initialized after real Scribe load");
            T.Eq(5, SaveMigrations.Current, "composition requires no save bump");
        }

        private static void RoleRefusals()
        {
            NetworkActor actor = Actor(0, "medical");
            List<KnownCharacter> people = new List<KnownCharacter>();
            Pin(actor, people, 201, OperationalRole.Unset);
            KnownCharacter bound = Pin(actor, people, 202, OperationalRole.Unset);
            bound.pawn = new PawnRef { thingIdNumber = 500 };
            List<OrganizationRoleAssignment> assignments;
            string refusal;
            T.Check(!OrganizationCompositionV1.TryPlanRoleInitialization(actor, people, out assignments, out refusal), "bound unknown role refuses instead of rewriting physical identity");
            T.Check(assignments == null && people.All(c => c.opRole == OperationalRole.Unset), "refusal leaves complete cohort unchanged");
            bound.opRole = OperationalRole.Medic;
            T.Check(OrganizationCompositionV1.TryPlanRoleInitialization(actor, people, out assignments, out refusal), "already-set bound role allowed and preserved");
            T.Eq(1, assignments.Count, "only missing unbound role planned");
            for (int i = 0; i < 7; i++) Pin(actor, people, 300 + i, OperationalRole.Unset);
            T.Check(!OrganizationCompositionV1.TryPlanRoleInitialization(actor, people, out assignments, out refusal), "origin people exceeding capacity refuse");
            T.Eq("OriginCohortExceedsCapacity", refusal, "malformed-origin diagnostic");
        }

        private static void SourcePure()
        {
            string source = PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src("Domain/Physical/OrganizationComposition.cs"));
            foreach (string forbidden in new[] { "Verse.", "RimWorld.", "Scribe", "IExposable", "Find.", "PawnGenerator", "new KnownCharacter", "Harmony", "Rand." })
                T.Check(!source.Contains(forbidden), "composition/selection introduces no " + forbidden);
            T.Check(!Regex.IsMatch(source, @"\.opRole\s*=(?!=)"), "role planner reads only; existing service owns write-once storage");
            string derivation = source.Substring(source.IndexOf("public static bool TryDerive", StringComparison.Ordinal), source.IndexOf("private static uint StableRoleHash", StringComparison.Ordinal) - source.IndexOf("public static bool TryDerive", StringComparison.Ordinal));
            foreach (string mutable in new[] { "Headcount", "morale", "reputation", "doctrine", "skill", "equipment", "funds", "leader", "Now" })
                T.Check(!derivation.Contains(mutable), "frozen origin recipe reads no mutable " + mutable);
            T.Eq(1, OrganizationCompositionV1.Version, "frozen composition version");
            T.Eq(8, OrganizationSeatPolicy.MaxMissionMembers, "bounded physical group size");
        }
    }
}
