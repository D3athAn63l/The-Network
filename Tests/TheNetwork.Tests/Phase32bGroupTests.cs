using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>Stage 3 group placement truth only. Promotion/terminal evidence acceptance has separate tests.</summary>
    public static class Phase32bGroupTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_PlanChoosesRolesAndChecksOutExactlyOnce", PlanRoles));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_RejectsPreselectedAndNonNpcRequestsWithoutMutation", Refusals));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_OneIncompleteGroupButCustodyDoesNotSpendAnonymousSeats", IncompletePolicy));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_RequiredUnavailablePinNeverReplaced", UnavailablePin));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_MaxEightPhysicalMembers", BoundedGroup));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_OneEncounterFactionAndAnonymousRoleRequests", Materialization));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_TemporaryBindingPrecedesPlacement", ReservationOrder));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_TestMapDoesNotLatchP0OrCreatePeople", TestMap));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_VisibleSmallMidLargeEligibility", SizeBoundaries));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_PlacementUsesCurrentSizeThenKeepsLatchedDecision", PlacementSize));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_DiscretionaryBudgetRefusesNewRememberedSpecialist", ChangedBudget));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_FailedAndUnspawnedHaveNoP0", FailedPlacement));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_PositiveSpawnRecoveryCanLatchP0", RecoveredPlacement));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_InterruptedAnonymousBindingUsesSamePawn", InterruptedBinding));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_UnconfirmedBindingStaysBlockedThroughRetryLimit", BindingNotificationFailure));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_OrdinarySlotsForgetPawnOnlyAfterReleaseCompletes", ReleaseClearsOrdinarySlot));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_LoadedOrdinaryReleaseExpiresFakeCoverage", LoadedOrdinaryRelease));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_InvalidPlacementEvidenceAndCrossEpisodeAliasReportOnly", InvalidEvidence));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_SaveLoadPreservesActualPlacementWithoutPromoting", PlacementSaveLoad));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Group_AdditiveMemberFieldsDefaultUnknownOnOldSave", AbsentFields));
        }

        private sealed class Fixture
        {
            public TestNet net;
            public NetworkActor actor;
            public OrganizationProfile org;
            public KnownCharacter leader;
        }

        private static Fixture Crew(int living = 5)
        {
            TestNet net = new TestNet(32117);
            NetworkActor actor = new NetworkActor
            {
                id = new ActorId(net.ids.NextId()), kind = ActorKind.Organization, seed = 123, foundedTick = net.clock.Now,
                name = new NameSnapshot { display = "Headless role crew" }
            };
            actor.Add(new ContractorProfile { specialties = new List<string> { "escort", "medical" } });
            ContractorSimulation sim = new ContractorSimulation { skill = 0.7f };
            sim.equipment.tier = 4;
            actor.Add(sim);
            OrganizationProfile org = new OrganizationProfile { capacity = living > 12 ? 32 : living > 6 ? 14 : 7 };
            org.tiers.Add(new TierCount(Tier.Regular, living - 1));
            KnownCharacter leader = new KnownCharacter
            {
                id = new CharacterId(net.ids.NextId()), org = actor.id, role = CharacterRole.Leader,
                opRole = OperationalRole.Leader, createdTick = actor.foundedTick, name = new NameSnapshot { display = "Known leader" }
            };
            org.leader = leader.id;
            org.knownMembers.Add(leader.id);
            actor.Add(org);
            net.ctx.actors.Add(actor);
            net.ctx.characters.Add(leader);
            return new Fixture { net = net, actor = actor, org = org, leader = leader };
        }

        private static EpisodeRequest Request(Fixture fixture)
        {
            return new EpisodeRequest
            {
                actor = fixture.actor.id, purposeKey = "HeadlessGroupVisit", mapId = 71, where = new TileRef { tileId = 17 },
                cause = new EpisodeCause { devKey = "Phys32bGroupFixture" }
            };
        }

        private static PhysicalEpisode Plan(Fixture fixture, params RoleCapacity[] required)
        {
            PhysicalEpisode episode;
            CommandResult result = fixture.net.ctx.Lifecycle.PlanGroup(Request(fixture), required, null, out episode);
            T.Check(result.ok && episode != null, "group planned: " + result);
            return episode;
        }

        private static PhysicalEpisode Typical(Fixture fixture)
        {
            return Plan(fixture, new RoleCapacity(OperationalRole.Leader, 1), new RoleCapacity(OperationalRole.Medic, 1), new RoleCapacity(OperationalRole.Rifleman, 1));
        }

        private static void PlanRoles()
        {
            Fixture f = Crew();
            int peopleBefore = f.net.ctx.characters.Count;
            PhysicalEpisode episode = Typical(f);
            T.Eq(3, episode.members.Count, "bounded three-person mission");
            T.Eq(f.leader.id, episode.members.Single(m => m.seatRole == OperationalRole.Leader).character, "existing matching leader chosen");
            T.Check(episode.members.Where(m => !m.IsNamed).Select(m => m.seatRole).OrderBy(r => r).SequenceEqual(new[] { OperationalRole.Rifleman, OperationalRole.Medic }), "anonymous slots have accepted role seats");
            T.Eq(2, f.org.Healthy, "two anonymous units checked out");
            T.Eq(2, f.org.Committed, "exactly two committed anonymous units");
            T.Eq(peopleBefore, f.net.ctx.characters.Count, "group planning creates no anonymous person records");
            T.Eq(episode.id, f.leader.episode, "known person has the exclusive Episode link");
            T.Check(episode.members.All(m => m.playerVisibleTick == -1 && !m.p0Eligible), "planning cannot invent placement evidence");
            T.Eq(0, f.net.physical.creates, "planning does not create physical Pawns");
        }

        private static void Refusals()
        {
            foreach (bool named in new[] { true, false })
            {
                Fixture f = Crew();
                EpisodeRequest request = Request(f);
                if (named) request.named.Add(f.leader.id);
                else request.anonymous.Add(new TierCount(Tier.Regular, 1));
                int next = f.net.ids.PeekNextId;
                PhysicalEpisode episode;
                CommandResult result = f.net.ctx.Lifecycle.PlanGroup(request, new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out episode);
                T.Check(!result.ok && episode == null, "caller preselected " + (named ? "named" : "anonymous") + " members refused");
                T.Eq(4, f.org.Healthy, "refusal spends no healthy source stock");
                T.Eq(0, f.org.Committed, "refusal creates no commitment");
                T.Eq(next, f.net.ids.PeekNextId, "refusal allocates no entity id");
            }
            Fixture ended = Crew();
            ended.actor.status = ActorStatus.Dissolved;
            PhysicalEpisode absent;
            T.Check(!ended.net.ctx.Lifecycle.PlanGroup(Request(ended), new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out absent).ok, "ended organization refuses");
            Fixture proxy = Crew();
            proxy.actor.kind = ActorKind.PlayerProxy;
            T.Check(!proxy.net.ctx.Lifecycle.PlanGroup(Request(proxy), new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out absent).ok, "player proxy cannot request NPC group");
        }

        private static void IncompletePolicy()
        {
            foreach (EpisodeState state in new[] { EpisodeState.Planned, EpisodeState.Open, EpisodeState.Closed, EpisodeState.Quarantined })
            {
                Fixture f = Crew();
                PhysicalEpisode first = Typical(f);
                first.state = state;
                first.releaseApplied = false;
                f.net.ctx.episodes.RebuildIndex();
                int next = f.net.ids.PeekNextId;
                PhysicalEpisode second;
                T.Check(!f.net.ctx.Lifecycle.PlanGroup(Request(f), new[] { new RoleCapacity(OperationalRole.Heavy, 1) }, null, out second).ok, "existing " + state + " group owns residual seats until release");
                T.Eq(next, f.net.ids.PeekNextId, "refusing concurrent group creates nothing");
            }
            Fixture held = Crew();
            PhysicalEpisode custody = new PhysicalEpisode
            {
                id = new EpisodeId(held.net.ids.NextId()), actor = held.actor.id, purposeKey = CustodyRules.Purpose,
                state = EpisodeState.Open, cause = new EpisodeCause { devKey = "HeadlessNamedCustody" }
            };
            custody.members.Add(new EpisodeMember { character = held.leader.id, seatRole = held.leader.opRole });
            held.leader.episode = custody.id;
            held.leader.custody = CustodyState.OutOfCustody;
            held.leader.status = CharacterStatus.Captured;
            held.net.ctx.episodes.Add(custody);
            PhysicalEpisode group;
            T.Check(held.net.ctx.Lifecycle.PlanGroup(Request(held), new[] { new RoleCapacity(OperationalRole.Rifleman, 1) }, null, out group).ok, "named custody Episode does not create/spend another anonymous detachment");
            T.Check(group != null && group.members.All(m => !m.IsNamed), "held leader remains unavailable while anonymous matching role can leave");
        }

        private static void UnavailablePin()
        {
            Fixture f = Crew();
            KnownCharacter medic = NewMember(f, OperationalRole.Medic);
            f.org.tiers[0].healthy--;
            medic.status = CharacterStatus.Captured;
            medic.custody = CustodyState.OutOfCustody;
            PhysicalEpisode episode;
            T.Check(!f.net.ctx.Lifecycle.PlanGroup(Request(f), new[] { new RoleCapacity(OperationalRole.Medic, 1) }, null, out episode).ok, "captured Medic still pins seat, no anonymous replacement");
            T.Eq(0, f.net.physical.creates, "wrong-role known Pawn is never generated/substituted");
            T.Eq(2, f.net.ctx.characters.Count, "refusal does not delete existing identity");
        }

        private static void BoundedGroup()
        {
            Fixture f = Crew(20);
            PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Leader, 1), new RoleCapacity(OperationalRole.Rifleman, 6), new RoleCapacity(OperationalRole.Medic, 1));
            T.Eq(8, episode.members.Count, "maximum eight mission members accepted");
            T.Eq(8, f.net.ctx.Lifecycle.Materialize(episode), "all eight members physically placed");
            Fixture oversized = Crew(20);
            PhysicalEpisode missing;
            T.Check(!oversized.net.ctx.Lifecycle.PlanGroup(Request(oversized), new[] { new RoleCapacity(OperationalRole.Rifleman, 9) }, null, out missing).ok, "nine-member mission refused before creation");
            T.Eq(0, oversized.net.physical.creates, "oversized mission creates zero Pawns");
        }

        private static void Materialization()
        {
            Fixture f = Crew();
            PhysicalEpisode episode = Typical(f);
            int peopleBefore = f.net.ctx.characters.Count;
            T.Eq(3, f.net.ctx.Lifecycle.Materialize(episode), "whole selected group placed");
            T.Eq(1, f.net.physical.factionsCreated, "one temporary encounter faction for group");
            T.Check(episode.faction != null && episode.faction.IsValid, "valid durable temporary faction reference");
            T.Check(episode.members.All(m => f.net.physical.TokenOf(m.pawn).factionId == episode.faction.loadId), "all selected Pawns use same encounter shell");
            T.Eq(2, f.net.physical.requests.Count(r => !r.character.IsValid), "only actual anonymous slots receive anonymous projection");
            foreach (ProjectionRequest request in f.net.physical.requests.Where(r => !r.character.IsValid))
            {
                EpisodeMember member = episode.members.Single(m => m.slot == request.slot);
                T.Eq(member.seatRole, request.role, "anonymous request carries required slot role");
                T.Eq(ContractorService.Experience(f.actor), request.capability, "actor's actual capability drives role skill floor");
                T.Eq(4, request.equipmentTier, "actor equipment tier drives allowed kind selection");
                T.Check(request.name == null, "anonymous creation invents no Network person name");
                T.Eq(episode.faction.loadId, request.faction.loadId, "anonymous request belongs to same temporary encounter faction");
            }
            T.Eq(peopleBefore, f.net.ctx.characters.Count, "materialization does not create KnownCharacters");
            T.Eq(0, f.net.ctx.Lifecycle.Materialize(episode), "repeat materialization does not place/create again");
            T.Eq(1, f.net.physical.factionsCreated, "repeat never creates permanent or duplicate organization faction");
        }

        private static void ReservationOrder()
        {
            Fixture f = Crew();
            PhysicalEpisode episode = Typical(f);
            f.net.ctx.Lifecycle.Materialize(episode);
            foreach (EpisodeMember member in episode.members.Where(m => !m.IsNamed))
            {
                int thing = member.pawn.thingIdNumber;
                int create = f.net.physical.actions.IndexOf("create " + thing);
                int bind = f.net.physical.actions.IndexOf("bind-episode " + thing);
                int place = f.net.physical.actions.IndexOf("place " + thing);
                T.Check(create >= 0 && bind > create && place > bind, "durable anonymous binding/reservation precedes Place");
                T.Eq(episode.id, f.net.physical.TokenOf(member.pawn).temporaryEpisode, "temporary reservation belongs to exact Episode");
                f.net.physical.ExitNormally(member.pawn, 17);
                T.Eq(ObservedKind.WorldFree, f.net.physical.Observe(member.pawn, episode.id).kind, "same anonymous Pawn has safe observed exit while teammate remains Pending");
            }
            T.Check(episode.members.Any(m => m.IsNamed && m.outcome == MemberOutcome.Pending), "ordinary teammate still active; no identity commit");
            T.Eq(1, f.net.ctx.characters.Count, "temporary reservation created no dummy person");
        }

        private static void TestMap()
        {
            Fixture f = Crew();
            T.Check(!f.net.physical.playerVisiblePlacement, "scripted test/dev map default is not a player-visible encounter");
            PhysicalEpisode episode = Typical(f);
            f.net.ctx.Lifecycle.Materialize(episode);
            T.Check(episode.members.Where(m => !m.IsNamed).All(m => m.playerVisibleTick == -1 && !m.p0Eligible), "synthetic placement alone is not P0");
            T.Eq(1, f.net.ctx.characters.Count, "no people created from test map presence");
        }

        private static void SizeBoundaries()
        {
            foreach (int living in new[] { 5, 6, 7, 12, 13, 20 })
            {
                Fixture f = Crew(living);
                f.net.physical.playerVisiblePlacement = true;
                PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Medic, 1), new RoleCapacity(OperationalRole.Rifleman, 1));
                f.net.ctx.Lifecycle.Materialize(episode);
                EpisodeMember medic = episode.members.Single(m => m.seatRole == OperationalRole.Medic);
                EpisodeMember rifleman = episode.members.Single(m => m.seatRole == OperationalRole.Rifleman);
                T.Eq(living <= 12, medic.p0Eligible, "Medic placement P0 at size " + living);
                T.Eq(living <= 6, rifleman.p0Eligible, "ordinary Rifleman P0 at size " + living);
                T.Check(medic.playerVisibleTick >= 0 && rifleman.playerVisibleTick >= 0, "successful actual visible placement is recorded even when strong-only");
                T.Eq(1, f.net.ctx.characters.Count, "placement at size " + living + " performs no identity commit");
            }
            Fixture pair = Crew(2);
            pair.net.physical.playerVisiblePlacement = true;
            PhysicalEpisode two = Plan(pair, new RoleCapacity(OperationalRole.Rifleman, 1));
            pair.net.ctx.Lifecycle.Materialize(two);
            T.Check(two.members[0].p0Eligible, "two-person organization lower boundary admits its actual ordinary seat");
        }

        private static void PlacementSize()
        {
            Fixture f = Crew(13);
            f.net.physical.playerVisiblePlacement = true;
            PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Medic, 1), new RoleCapacity(OperationalRole.Rifleman, 1));
            f.org.tiers[0].healthy = 0; // positive fixture casualty before physical placement: leader + two committed slots remain.
            f.net.ctx.Lifecycle.Materialize(episode);
            T.Check(episode.members.All(m => m.p0Eligible), "eligibility uses actual living membership at placement, not initial plan size");
            Fixture large = Crew(13);
            large.net.physical.playerVisiblePlacement = true;
            PhysicalEpisode placed = Plan(large, new RoleCapacity(OperationalRole.Rifleman, 1));
            large.net.ctx.Lifecycle.Materialize(placed);
            T.Check(!placed.members[0].p0Eligible, "large presence initially ineligible");
            int tick = placed.members[0].playerVisibleTick;
            large.org.tiers[0].healthy = 0;
            large.net.clock.Now += 100;
            large.net.ctx.Lifecycle.Materialize(placed);
            T.Check(!placed.members[0].p0Eligible && placed.members[0].playerVisibleTick == tick, "later size/time changes cannot rewrite latched visible decision");
        }

        private static KnownCharacter NewMember(Fixture fixture, OperationalRole role)
        {
            KnownCharacter person = new KnownCharacter
            {
                id = new CharacterId(fixture.net.ids.NextId()), org = fixture.actor.id, role = CharacterRole.Member,
                opRole = role, createdTick = fixture.actor.foundedTick + 1, name = new NameSnapshot { display = "Existing " + role }
            };
            fixture.org.knownMembers.Add(person.id);
            fixture.net.ctx.characters.Add(person);
            return person;
        }

        private static void ChangedBudget()
        {
            Fixture f = Crew(7);
            f.net.physical.playerVisiblePlacement = true;
            PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Medic, 1));
            for (int i = 0; i < 5; i++) { NewMember(f, OperationalRole.Rifleman); f.org.tiers[0].healthy--; }
            T.Eq(6, f.org.knownMembers.Count, "discretionary target fills after planning without adding a human");
            int peopleBefore = f.net.ctx.characters.Count;
            f.net.ctx.Lifecycle.Materialize(episode);
            T.Check(episode.members.All(m => !m.p0Eligible && m.state != MemberState.Present), "full current budget refuses new remembered specialist placement");
            T.Eq(0, f.net.physical.places, "budget refusal does not show player a changing anonymous specialist");
            T.Eq(peopleBefore, f.net.ctx.characters.Count, "placement refusal preserves all existing pins");
        }

        private static void FailedPlacement()
        {
            Fixture f = Crew();
            f.net.physical.playerVisiblePlacement = true;
            f.net.physical.failPlace = true;
            PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Medic, 1));
            f.net.ctx.Lifecycle.Materialize(episode);
            T.Check(episode.members.All(m => !m.p0Eligible && m.playerVisibleTick == -1), "generated but unspawned failed placement never invents encounter");
            T.Eq(1, f.net.ctx.characters.Count, "unspawned candidate creates no persistent person");
            Fixture createFault = Crew();
            createFault.net.physical.playerVisiblePlacement = true;
            createFault.net.physical.ThrowOn("create");
            PhysicalEpisode absent = Plan(createFault, new RoleCapacity(OperationalRole.Medic, 1));
            createFault.net.ctx.Lifecycle.Materialize(absent);
            T.Check(absent.members.All(m => !m.p0Eligible && m.playerVisibleTick == -1), "failed generation cannot be P0");
        }

        private static void RecoveredPlacement()
        {
            foreach (bool throws in new[] { false, true })
            {
                Fixture f = Crew();
                f.net.physical.playerVisiblePlacement = true;
                f.net.physical.failPlace = true;
                f.net.physical.failPlaceThrows = throws;
                f.net.physical.onPlaceFailed = token => { token.spawned = true; token.mapId = 71; };
                PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Medic, 1));
                T.Eq(1, f.net.ctx.Lifecycle.Materialize(episode), "positive spawned observation recovers placement after " + (throws ? "throw" : "false return"));
                T.Check(episode.members[0].p0Eligible && episode.members[0].playerVisibleTick >= 0, "actual recovered visible spawn may latch P0");
                T.Eq(1, f.net.ctx.characters.Count, "recovered placement still commits no identity early");
            }
        }

        private static void PlacementSaveLoad()
        {
            Fixture f = Crew();
            f.net.physical.playerVisiblePlacement = true;
            PhysicalEpisode episode = Typical(f);
            f.net.ctx.Lifecycle.Materialize(episode);
            EpisodeId id = episode.id;
            int[] things = episode.members.Select(m => m.pawn.thingIdNumber).ToArray();
            int[] ticks = episode.members.Select(m => m.playerVisibleTick).ToArray();
            bool[] eligibility = episode.members.Select(m => m.p0Eligible).ToArray();
            PhysicalLifecycleTests.SaveLoad(f.net);
            PhysicalEpisode loaded = f.net.ctx.episodes.Get(id);
            T.Check(loaded.members.Select(m => m.pawn.thingIdNumber).SequenceEqual(things), "same slot PawnRefs survive real Scribe reload");
            T.Check(loaded.members.Select(m => m.playerVisibleTick).SequenceEqual(ticks), "actual placement timestamps persist");
            T.Check(loaded.members.Select(m => m.p0Eligible).SequenceEqual(eligibility), "eligibility persists rather than inferred from post-load map state");
            T.Eq(1, f.net.ctx.characters.Count, "save/load adds no phantom anonymous Character");
            T.Eq(5, SaveMigrations.Current, "safe additive placement fields keep save format 5");
        }

        private static void InterruptedBinding()
        {
            Fixture f = Crew();
            PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Rifleman, 1));
            EpisodeMember member = episode.members[0];
            // Reproduce the durable edge after Create/binding, before the Created marker and registry notification.
            PawnRef pawn = f.net.physical.Create(new ProjectionRequest
            {
                episode = episode.id, actor = f.actor.id, slot = member.slot, tier = member.tier, role = member.seatRole
            });
            member.pawn = pawn;
            T.Eq(1, f.net.ctx.Lifecycle.Materialize(episode), "interrupted bound anonymous member resumes placement");
            T.Eq(1, f.net.physical.creates, "bound slot is never regenerated");
            T.Check(ReferenceEquals(pawn, member.pawn), "durable slot keeps the exact original PawnRef");
            T.Eq(episode.id, f.net.physical.TokenOf(pawn).temporaryEpisode, "resumed binding gets reservation before Place");
            T.Eq(1, f.net.ctx.characters.Count, "resuming creates no placeholder identity");
        }

        private static void ReleaseClearsOrdinarySlot()
        {
            foreach (MemberOutcome outcome in new[] { MemberOutcome.Returned, MemberOutcome.Killed, MemberOutcome.Lost, MemberOutcome.NeverPlaced })
            {
                Fixture f = Crew();
                PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Rifleman, 1));
                EpisodeMember member = episode.members[0];
                f.net.physical.ThrowOn("faction-release");
                if (outcome == MemberOutcome.NeverPlaced) f.net.physical.failPlace = true;
                f.net.ctx.Lifecycle.Materialize(episode);
                PawnRef pawn = member.pawn;
                if (outcome != MemberOutcome.NeverPlaced)
                {
                    if (outcome == MemberOutcome.Returned) f.net.physical.ExitNormally(pawn, 17);
                    else if (outcome == MemberOutcome.Killed) f.net.physical.Die(pawn);
                    else f.net.physical.Vanish(pawn);
                    f.net.ctx.Lifecycle.Reconcile(episode, "headless ordinary group exit");
                }
                T.Eq(outcome, member.outcome, "positive ordinary slot outcome: " + outcome);
                T.Check(episode.consequencesApplied && !episode.releaseApplied, "failed final faction release leaves RELEASE incomplete: " + outcome);
                T.Check(member.IsBound && ReferenceEquals(pawn, member.pawn), "durable slot binding remains until every release action succeeds: " + outcome);
                f.net.ctx.Lifecycle.FinishPending(episode);
                T.Check(episode.releaseApplied && episode.IsComplete, "retry finishes release and publication: " + outcome);
                T.Check(member.pawn == null && !member.IsNamed, "completed history cannot retain anonymous Pawn roster: " + outcome);
                T.Eq(0, f.org.Committed, "committed stock settled once: " + outcome);
                T.Eq(outcome == MemberOutcome.Returned || outcome == MemberOutcome.NeverPlaced ? 4 : 3, f.org.Healthy, "observed outcome conserves remaining humans: " + outcome);
                T.Eq(1, f.net.ctx.characters.Count, "ordinary release creates no persistent person: " + outcome);
                List<string> findings = new List<string>();
                EpisodeChecks.Report(f.net.ctx, findings);
                T.Check(!findings.Any(s => s.Contains("release cursor") || s.Contains("member's cursor")), "completed cleared slot keeps valid historical release cursor: " + outcome);
                f.net.ctx.Lifecycle.FinishPending(episode);
                T.Eq(0, f.org.Committed, "repeated completion spends no second unit: " + outcome);
            }
        }

        private static void BindingNotificationFailure()
        {
            Fixture f = Crew();
            f.net.physical.playerVisiblePlacement = true;
            PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Rifleman, 1));
            EpisodeMember member = episode.members[0];
            f.net.physical.ThrowOn("bind", PhysicalEpisode.MaxAttempts + 1);
            T.Eq(0, f.net.ctx.Lifecycle.Materialize(episode), "failed binding notification places nobody");
            PawnRef pawn = member.pawn;
            T.Check(member.IsBound && member.state == MemberState.Created, "write-once binding is kept after notification failure");
            for (int retry = 0; retry <= PhysicalEpisode.MaxAttempts; retry++)
            {
                if (retry > 0) T.Check(!f.net.ctx.Lifecycle.Reconcile(episode, "headless binding retry"), "failed notification retry cannot commit " + retry);
                T.Check(episode.state == EpisodeState.Quarantined && episode.quarantineKey == "GroupBindingUnconfirmed", "binding safety quarantine survives retry count " + retry);
                T.Check(!episode.consequencesApplied && !episode.releaseApplied && ReferenceEquals(pawn, member.pawn), "retry preserves bound slot without outcome/release " + retry);
                T.Eq(0, f.net.physical.places, "unconfirmed binding never reaches Place " + retry);
                T.Eq(0, f.net.physical.passCalls, "unconfirmed binding never reaches PassToWorld " + retry);
                T.Eq(1, f.org.Committed, "unconfirmed binding keeps source stock committed " + retry);
            }
            T.Check(f.net.ctx.Lifecycle.Reconcile(episode, "headless notification repaired"), "positive notification repair can finally reconcile unspawned member");
            T.Check(episode.IsComplete && member.outcome == MemberOutcome.NeverPlaced && member.pawn == null, "confirmed unused slot releases normally without persistent anonymous binding");
            T.Eq(1, f.net.physical.creates, "notification retries never regenerate the bound Pawn");
            T.Eq(0, f.net.physical.places, "successful repair does not invent a prior spawn");
            T.Eq(1, f.net.physical.passCalls, "only after confirmation may unused Pawn pass to vanilla");
            T.Check(member.playerVisibleTick == -1 && !member.p0Eligible, "notification retries create no presence evidence");
            T.Eq(4, f.org.Healthy, "never-placed source unit returned exactly once");
            T.Eq(0, f.org.Committed, "no source commitment survives completion");
            T.Eq(1, f.net.ctx.characters.Count, "failure and retry create no dummy identity");
        }

        private static void LoadedOrdinaryRelease()
        {
            Fixture f = Crew();
            PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Rifleman, 1));
            f.net.ctx.Lifecycle.Materialize(episode);
            f.net.physical.ExitNormally(episode.members[0].pawn, 17);
            f.net.physical.ThrowOn("faction-release");
            f.net.ctx.Lifecycle.Reconcile(episode, "headless reload before release completion");
            T.Check(episode.consequencesApplied && !episode.releaseApplied, "closed ordinary Episode saved while final release pending");
            PhysicalLifecycleTests.SaveLoad(f.net);
            PhysicalEpisode loaded = f.net.ctx.episodes.Get(episode.id);
            T.Check(!ReferenceEquals(episode, loaded), "Scribe actually replaced the Episode object");
            PawnRef pawn = loaded.members[0].pawn;
            f.net.physical.BreakReservation(pawn);
            T.Eq(ObservedKind.ReservationBroken, f.net.physical.Observe(pawn, loaded.id).kind, "loaded unreleased Episode still owns temporary protection");
            f.net.physical.RepairReservation(pawn);
            f.net.ctx.Lifecycle.FinishPending(loaded);
            T.Check(loaded.IsComplete && loaded.members[0].pawn == null, "loaded release clears ephemeral anonymous slot");
            T.Check(!episode.releaseApplied, "old detached fixture Episode deliberately remains unreleased");
            f.net.physical.BreakReservation(pawn);
            T.Eq(ObservedKind.WorldFree, f.net.physical.Observe(pawn, loaded.id).kind, "current loaded RELEASE expires coverage despite stale diagnostic Episode reference");
            T.Eq(1, f.net.ctx.characters.Count, "post-load ordinary release leaves no anonymous identity record");
            OrganizationProfile loadedOrg = f.net.ctx.actors.Get(f.actor.id).Get<OrganizationProfile>();
            T.Eq(4, loadedOrg.Healthy, "reload release does not return same anonymous unit twice");
            T.Eq(0, loadedOrg.Committed, "loaded source stock fully settled");
        }

        private static void InvalidEvidence()
        {
            Fixture f = Crew();
            PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Rifleman, 1));
            f.net.ctx.Lifecycle.Materialize(episode);
            EpisodeMember member = episode.members[0];
            PawnRef pawn = member.pawn;
            member.p0Eligible = true;
            member.playerVisibleTick = -1;
            PhysicalEpisode alias = new PhysicalEpisode
            {
                id = new EpisodeId(f.net.ids.NextId()), actor = f.actor.id, cause = new EpisodeCause { devKey = "HeadlessAlias" }
            };
            alias.members.Add(new EpisodeMember { slot = 0, seatRole = OperationalRole.Rifleman, pawn = pawn.Copy() });
            f.net.ctx.episodes.Add(alias);
            List<string> findings = new List<string>();
            EpisodeChecks.Report(f.net.ctx, findings);
            T.Check(findings.Any(s => s.Contains("P0 eligibility lacks placement or operational-role evidence")), "latched P0 without actual timestamp is reported");
            T.Check(findings.Any(s => s.Contains("two unreleased Episode owners")), "cross-Episode Pawn alias is reported");
            T.Check(member.p0Eligible && member.playerVisibleTick == -1 && ReferenceEquals(pawn, member.pawn), "validator preserves contradictory placement evidence and binding");
            findings.Clear();
            member.playerVisibleTick = f.net.clock.Now;
            member.seatRole = OperationalRole.Unset;
            EpisodeChecks.Report(f.net.ctx, findings);
            T.Check(findings.Any(s => s.Contains("P0 eligibility lacks placement or operational-role evidence")), "latched P0 without valid seat role is also reported");
            T.Eq(1, f.net.ctx.characters.Count, "report does not repair aliases by manufacturing people");
        }

        private static void AbsentFields()
        {
            EpisodeMember member = new EpisodeMember { slot = 1, tier = Tier.Regular, seatRole = OperationalRole.Medic, playerVisibleTick = 12345, p0Eligible = true };
            string path = Path.GetTempFileName();
            try
            {
                Scribe.saver.InitSaving(path, "fixture");
                try { Scribe_Deep.Look(ref member, "member"); } finally { Scribe.saver.FinalizeSaving(); }
                XmlDocument xml = new XmlDocument();
                xml.Load(path);
                foreach (XmlNode node in xml.SelectNodes("//playerVisibleTick|//p0Eligible")) node.ParentNode.RemoveChild(node);
                xml.Save(path);
                EpisodeMember old = null;
                Scribe.loader.InitLoading(path);
                try { Scribe_Deep.Look(ref old, "member"); } finally { Scribe.loader.FinalizeLoading(); }
                T.Check(old != null && old.seatRole == OperationalRole.Medic, "old member shape still loads its existing role");
                T.Eq(-1, old.playerVisibleTick, "absent placement timestamp is unknown");
                T.Check(!old.p0Eligible, "absent old-save eligibility never invents an encounter");
            }
            finally { File.Delete(path); }
        }
    }
}
