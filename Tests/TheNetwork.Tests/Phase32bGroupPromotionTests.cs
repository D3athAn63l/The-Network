using System;
using System.Collections.Generic;
using System.Linq;
using TheNetwork.Diagnostics.RuntimeTests;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Tests
{
    /// <summary>Simple group returns and one held member through the actual lifecycle; mixed-fate expansion belongs to 3.2C.</summary>
    public static class Phase32bGroupPromotionTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Phys32b.GroupPromotion_FirstVisitThenReloadedRevisitKeepsSamePeople", FirstVisit));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.GroupPromotion_OneCaptureWaitsForPeersAcrossWakesAndReload", PendingCapture));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.GroupPromotion_LargeOrdinaryReturnStaysEphemeralExactlyOnce", OrdinaryLarge));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.GroupPromotion_MaterialCaptureOverflowsSixWithoutExtraHuman", OverflowCapture));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.GroupPromotion_FailedNamedHandoffKeepsBridgeUntilRelease", FailedHandoff));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.GroupPromotion_LifecycleCommitFaultRestoresAndRetriesOnce", CommitFailure));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.GroupPromotion_OptionalFactsFaultDoesNotInventIdentity", OptionalFactsFailure));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.GroupPromotion_MandatoryP0AndCaptureRequireActualName", MandatoryNames));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.GroupPromotion_OverflowConsumersCountOnlyDistinctCurrentPeople", CurrentConsumers));
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
            TestNet net = new TestNet(32317);
            NetworkActor actor = new NetworkActor
            {
                id = new ActorId(net.ids.NextId()), kind = ActorKind.Organization, seed = 123, foundedTick = net.clock.Now,
                name = NameSnapshot.Org("Lifecycle crew")
            };
            actor.Add(new ContractorProfile { specialties = new List<string> { "escort", "medical" } });
            actor.Add(new ContractorSimulation { skill = 0.7f });
            OrganizationProfile org = new OrganizationProfile { capacity = living > 12 ? 32 : living > 6 ? 14 : 7 };
            org.tiers.Add(new TierCount(Tier.Regular, living - 1));
            KnownCharacter leader = new KnownCharacter
            {
                id = new CharacterId(net.ids.NextId()), org = actor.id, role = CharacterRole.Leader,
                opRole = OperationalRole.Leader, createdTick = actor.foundedTick, name = NameSnapshot.Person("Known", "Leader", "Crew")
            };
            org.leader = leader.id;
            org.knownMembers.Add(leader.id);
            actor.Add(org);
            net.ctx.actors.Add(actor);
            net.ctx.characters.Add(leader);
            return new Fixture { net = net, actor = actor, org = org, leader = leader };
        }

        private static PhysicalEpisode Plan(Fixture f, params RoleCapacity[] required)
        {
            PhysicalEpisode episode;
            EpisodeRequest request = new EpisodeRequest
            {
                actor = f.actor.id, purposeKey = "HeadlessPromotionVisit", mapId = 72, where = new TileRef { tileId = 18 },
                cause = new EpisodeCause { devKey = "Phys32bPromotionFixture" }
            };
            CommandResult result = f.net.ctx.Lifecycle.PlanGroup(request, required, null, out episode);
            T.Check(result.ok && episode != null, "group planned: " + result);
            return episode;
        }

        private static PhysicalEpisode WholeCrew(Fixture f)
        {
            return Plan(f, new RoleCapacity(OperationalRole.Leader, 1), new RoleCapacity(OperationalRole.Rifleman, 2),
                new RoleCapacity(OperationalRole.Heavy, 1), new RoleCapacity(OperationalRole.Medic, 1));
        }

        private static int Living(Fixture f)
        {
            OrganizationSeats seats;
            string refusal;
            T.Check(OrganizationSeatPolicy.TryApportion(f.actor, f.net.ctx.characters.characters, out seats, out refusal), "current living membership remains coherent: " + refusal);
            return seats == null ? -1 : seats.living;
        }

        private static PhysicalEpisode Reload(Fixture f, EpisodeId id)
        {
            ActorId actor = f.actor.id;
            PhysicalLifecycleTests.SaveLoad(f.net);
            f.actor = f.net.ctx.actors.Get(actor);
            f.org = f.actor.Get<OrganizationProfile>();
            f.leader = f.net.ctx.characters.Get(f.org.leader);
            f.net.ctx.Lifecycle.OnLoaded();
            return f.net.ctx.episodes.Get(id);
        }

        private static void ExitAll(Fixture f, PhysicalEpisode episode, EpisodeMember except = null)
        {
            foreach (EpisodeMember member in episode.members)
                if (!ReferenceEquals(member, except)) f.net.physical.ExitNormally(member.pawn, 18);
        }

        private static void SamePawn(Fixture f, EpisodeMember member, PawnRef before, FakePhysicalWorldPort.Token token)
        {
            KnownCharacter c = f.net.ctx.characters.Get(member.character);
            T.Check(c != null && c.pawn != null && c.pawn.thingIdNumber == before.thingIdNumber && c.pawn.defName == before.defName,
                "promotion preserves same physical binding identifiers");
            T.Check(ReferenceEquals(token, f.net.physical.TokenOf(c.pawn)) && ReferenceEquals(token, f.net.physical.TokenOf(member.pawn)),
                "known person and Episode still address exact original physical token");
            T.Eq(member.seatRole, c.opRole, "operational role comes from original seat");
            T.Eq(f.actor.id, c.org, "organization provenance is preserved");
            T.Eq(token.name.Display, c.name.Display, "already-real Pawn's actual full name is preserved");
        }

        private static void FirstVisit()
        {
            Fixture f = Crew();
            f.net.physical.playerVisiblePlacement = true;
            PhysicalEpisode first = WholeCrew(f);
            T.Eq(5, f.net.ctx.Lifecycle.Materialize(first), "first visit places whole five-person crew");
            EpisodeMember[] anonymous = first.members.Where(m => !m.IsNamed).ToArray();
            T.Eq(4, anonymous.Length, "four ephemeral source seats before reconciliation");
            T.Check(anonymous.All(m => m.p0Eligible), "small actual player-visible placement latches all four");
            Dictionary<int, PawnRef> pawns = anonymous.ToDictionary(m => m.slot, m => m.pawn);
            Dictionary<int, FakePhysicalWorldPort.Token> tokens = anonymous.ToDictionary(m => m.slot, m => f.net.physical.TokenOf(m.pawn));
            foreach (EpisodeMember member in anonymous) tokens[member.slot].name = NameSnapshot.Person("Actual" + member.slot, "Given" + member.slot, "Vanilla");
            T.Eq(1, f.net.ctx.characters.Count, "actual placement did not add any identities");
            T.Eq(0, f.net.physical.promotionReads, "facts are not read before terminal reconciliation");
            int humans = Living(f);
            ExitAll(f, first);
            T.Check(f.net.ctx.Lifecycle.Reconcile(first, "headless first return"), "terminal whole return commits identities");
            T.Check(first.IsComplete && first.members.All(m => m.IsNamed), "whole crew now durably named and released");
            T.Eq(5, f.net.ctx.characters.Count, "first encounter creates exactly four people");
            T.Eq(5, f.org.knownMembers.Count, "membership adds every promotion exactly once");
            T.Eq(humans, Living(f), "promotion replaces anonymous source humans, never adds humans");
            T.Eq(0, f.org.Healthy + f.org.Committed, "all source copies are gone after pinning");
            T.Eq(4, f.net.physical.promotionReads, "one read per terminal anonymous candidate");
            foreach (EpisodeMember member in anonymous)
            {
                SamePawn(f, member, pawns[member.slot], tokens[member.slot]);
                KnownCharacter c = f.net.ctx.characters.Get(member.character);
                T.Eq(CustodyState.Stored, c.custody, "returned promoted person is stored");
                T.Check(!c.episode.IsValid && AuthorityGate.CanSimulateAbstractly(c), "release restores abstract authority");
                T.Eq(member.playerVisibleTick, c.firstEncounterTick, "first encounter uses actual placement timestamp");
                int thing = c.pawn.thingIdNumber;
                int handoff = f.net.physical.actions.IndexOf("bind-named " + thing);
                int retained = f.net.physical.actions.IndexOf("retain " + thing);
                T.Check(handoff >= 0 && retained > handoff, "named reservation handoff precedes physical retention proof");
                T.Check(!tokens[member.slot].temporaryEpisode.IsValid && tokens[member.slot].character == c.id, "named M1 takes over temporary reservation");
            }
            CharacterId[] identities = first.members.Select(m => m.character).OrderBy(c => c.Value).ToArray();
            Dictionary<int, int> bindings = first.members.ToDictionary(m => m.character.Value, m => m.pawn.thingIdNumber);
            int creates = f.net.physical.creates;
            Reload(f, first.id);
            f.net.clock.Now += 100;
            PhysicalEpisode second = WholeCrew(f);
            T.Check(second.members.All(m => m.IsNamed), "second visit uses existing role-qualified pins");
            T.Check(second.members.Select(m => m.character).OrderBy(c => c.Value).SequenceEqual(identities), "same five CharacterIds selected after real Scribe reload");
            T.Eq(5, f.net.ctx.Lifecycle.Materialize(second), "second visit rematerializes same crew");
            T.Eq(creates, f.net.physical.creates, "revisit regenerates no physical Pawn");
            T.Check(second.members.All(m => bindings[m.character.Value] == m.pawn.thingIdNumber), "all original bindings reused");
            ExitAll(f, second);
            f.net.ctx.Lifecycle.Reconcile(second, "headless second return");
            T.Check(second.IsComplete, "second visit completes normally");
            T.Eq(5, f.net.ctx.characters.Count, "second visit creates no duplicate identities");
            T.Eq(4, f.net.physical.promotionReads, "named revisit reads no promotion facts");
            T.Eq(humans, Living(f), "revisit keeps same human count");
        }

        private static void PendingCapture()
        {
            Fixture f = Crew();
            f.net.physical.playerVisiblePlacement = true;
            PhysicalEpisode episode = WholeCrew(f);
            f.net.ctx.Lifecycle.Materialize(episode);
            EpisodeMember held = episode.members.Single(m => m.seatRole == OperationalRole.Medic);
            PawnRef pawn = held.pawn;
            FakePhysicalWorldPort.Token token = f.net.physical.TokenOf(pawn);
            f.net.physical.Hold(pawn, ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner);
            for (int i = 0; i < 3; i++) T.Check(!f.net.ctx.Lifecycle.Reconcile(episode, "headless pending capture"), "active teammates keep batch uncommitted");
            T.Eq(0, f.net.physical.promotionReads, "no evidence reads while any peer is Pending");
            T.Eq(1, f.net.ctx.characters.Count, "capture does not create an early dummy identity");
            T.Eq(4, f.org.Committed, "capture still belongs to original anonymous commitment");
            episode = Reload(f, episode.id);
            held = episode.members.Single(m => m.seatRole == OperationalRole.Medic);
            T.Check(!held.IsNamed && held.pawn.thingIdNumber == pawn.thingIdNumber, "uncommitted captured slot survives reload with same PawnRef");
            T.Check(!f.net.ctx.Lifecycle.Reconcile(episode, "headless post-load pending"), "reload cannot create early identity");
            T.Eq(0, f.net.physical.promotionReads, "reload still waits to collect candidate facts");
            int humans = Living(f);
            ExitAll(f, episode, held);
            T.Check(f.net.ctx.Lifecycle.Reconcile(episode, "headless peers returned"), "all terminal peers permit one atomic batch");
            T.Check(episode.IsComplete, "episode completes while one promoted member remains held");
            T.Eq(5, f.net.ctx.characters.Count, "one strong capture and three P0 peers create exactly four identities");
            SamePawn(f, held, pawn, token);
            KnownCharacter captured = f.net.ctx.characters.Get(held.character);
            T.Check(captured.status == CharacterStatus.Captured && captured.custody == CustodyState.OutOfCustody && captured.heldBy == HeldKind.PlayerPrisoner, "actual holder is committed to captured identity");
            T.Check(!captured.episode.IsValid && !AuthorityGate.CanSimulateAbstractly(captured), "held pawn remains vanilla-owned after Episode release");
            T.Eq(1, f.net.ctx.Lifecycle.HeldCount, "custody watch indexes newly captured person");
            T.Eq(4, f.net.physical.promotionReads, "each terminal anonymous candidate read once");
            T.Eq(humans, Living(f), "captured member has no duplicate anonymous healthy copy");
            T.Eq(0, f.org.Healthy + f.org.Committed, "all committed anonymous source copies consumed once");
        }

        private static void OrdinaryLarge()
        {
            Fixture f = Crew(20);
            f.net.physical.playerVisiblePlacement = true;
            PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Rifleman, 4), new RoleCapacity(OperationalRole.Medic, 1));
            f.net.ctx.Lifecycle.Materialize(episode);
            T.Check(episode.members.All(m => !m.p0Eligible), "large visible detachment has no discretionary presence eligibility");
            int humans = Living(f);
            ExitAll(f, episode);
            f.net.ctx.Lifecycle.Reconcile(episode, "headless large ordinary return");
            T.Check(episode.IsComplete && episode.members.All(m => !m.IsNamed && m.pawn == null), "ordinary completed history retains no anonymous Pawn roster");
            T.Eq(1, f.net.ctx.characters.Count, "large presence alone creates zero people");
            T.Eq(19, f.org.Healthy, "all five anonymous units return once");
            T.Eq(0, f.org.Committed, "no stale commitment survives");
            T.Eq(humans, Living(f), "ordinary return conserves humans");
            int reads = f.net.physical.promotionReads;
            f.net.ctx.Lifecycle.Reconcile(episode, "headless duplicate ordinary wake");
            f.net.ctx.Lifecycle.FinishPending(episode);
            T.Eq(19, f.org.Healthy, "duplicate wakes do not restore a second copy");
            T.Eq(reads, f.net.physical.promotionReads, "completed history is never rescanned for identity");
        }

        private static void OverflowCapture()
        {
            Fixture f = Crew(20);
            for (int i = 0; i < 5; i++)
            {
                KnownCharacter pin = new KnownCharacter
                {
                    id = new CharacterId(f.net.ids.NextId()), org = f.actor.id, role = CharacterRole.Member, opRole = OperationalRole.Negotiator,
                    createdTick = f.actor.foundedTick + 1, name = NameSnapshot.Person("Existing" + i, null, "Pin")
                };
                f.net.ctx.characters.Add(pin);
                f.org.knownMembers.Add(pin.id);
                f.org.tiers[0].healthy--;
            }
            T.Eq(6, f.org.knownMembers.Count, "existing living identities fill discretionary target");
            PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Medic, 1), new RoleCapacity(OperationalRole.Rifleman, 1));
            f.net.ctx.Lifecycle.Materialize(episode);
            EpisodeMember held = episode.members.Single(m => m.seatRole == OperationalRole.Medic);
            PawnRef pawn = held.pawn;
            FakePhysicalWorldPort.Token token = f.net.physical.TokenOf(pawn);
            f.net.physical.Hold(pawn, ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner);
            ExitAll(f, episode, held);
            int humans = Living(f);
            T.Check(f.net.ctx.Lifecycle.Reconcile(episode, "headless overflow capture"), "positive material capture bypasses discretionary cap");
            T.Check(episode.IsComplete && held.IsNamed, "same captured Pawn durably named above six");
            T.Eq(7, f.net.ctx.characters.Count, "strong overflow identity is retained without truncation");
            T.Eq(7, f.org.knownMembers.Count, "overflow person is present in complete membership");
            SamePawn(f, held, pawn, token);
            T.Eq(humans, Living(f), "overflow capture neither adds nor loses a human");
            T.Eq(13, f.org.Healthy, "ordinary peer returns; captured source copy does not");
            T.Eq(0, f.org.Committed, "both original commitments settled exactly once");
        }

        private static void FailedHandoff()
        {
            Fixture f = Crew(13);
            PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Medic, 1));
            f.net.ctx.Lifecycle.Materialize(episode);
            EpisodeMember member = episode.members[0];
            PawnRef pawn = member.pawn;
            FakePhysicalWorldPort.Token token = f.net.physical.TokenOf(pawn);
            f.net.physical.Hold(pawn, ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner);
            f.net.physical.ThrowOn("bind");
            T.Check(f.net.ctx.Lifecycle.Reconcile(episode, "headless failed named handoff"), "durable promotion commits before handoff fault");
            KnownCharacter captured = f.net.ctx.characters.Get(member.character);
            T.Check(episode.state == EpisodeState.Closed && episode.consequencesApplied && !episode.releaseApplied, "handoff fault leaves CLOSED with RELEASE pending");
            T.Check(member.IsNamed && captured != null && captured.episode == episode.id, "committed identity stays linked and blocked");
            T.Check(!token.character.IsValid && token.temporaryEpisode == episode.id, "old temporary index bridges until named runtime notification succeeds");
            T.Eq(0, token.tagStrips + token.retainCalls + token.normalized, "no physical release action runs before index handoff");
            f.net.physical.Free(pawn, 18);
            T.Eq(ObservedKind.WorldFree, f.net.physical.Observe(pawn, episode.id).kind, "same Pawn remains protected through temporary Episode bridge");
            int episodes = f.net.ctx.episodes.Count;
            int observations = f.net.physical.observes;
            f.net.ctx.Lifecycle.CustodyWatchRun(new ScheduledJob { kind = PhysicalLifecycleService.CustodyWatchJob, target = PhysicalLifecycleService.CustodyWatchTarget });
            T.Eq(observations, f.net.physical.observes, "custody watch does not observe a person linked to unfinished RELEASE");
            T.Eq(episodes, f.net.ctx.episodes.Count, "custody watch cannot create competing Episode before release");
            f.net.ctx.Lifecycle.FinishPending(episode);
            T.Check(episode.IsComplete && token.character == captured.id && !token.temporaryEpisode.IsValid, "retry switches bridge to normal named M1 and finishes release");
            f.net.ctx.Lifecycle.CustodyWatchRun(new ScheduledJob { kind = PhysicalLifecycleService.CustodyWatchJob, target = PhysicalLifecycleService.CustodyWatchTarget });
            T.Check(captured.status == CharacterStatus.Active && captured.custody == CustodyState.Stored && !captured.episode.IsValid, "only after release may custody watch reconcile actual free return");
            T.Eq(2, f.net.ctx.characters.Count, "handoff retry and custody return create no duplicate identity");
            T.Eq(13, Living(f), "later freed promoted person has no extra restored abstract human");
            T.Eq(1, f.net.physical.creates, "handoff fault never regenerates physical Pawn");
        }

        private static void CommitFailure()
        {
            Fixture f = Crew();
            f.net.physical.playerVisiblePlacement = true;
            PhysicalEpisode episode = WholeCrew(f);
            f.net.ctx.Lifecycle.Materialize(episode);
            ExitAll(f, episode);
            int next = f.net.ids.PeekNextId;
            int humans = Living(f);
            PawnRef[] pawns = episode.members.Select(m => m.pawn).ToArray();
            LiveFingerprint before = null;
            bool restored = false;
            f.net.ctx.Lifecycle.commitBoundary = where =>
            {
                if (where == "before") before = LiveFingerprint.Of(f.net.ctx, f.net.ids, f.net.scheduler, f.net.journal);
                if (where == "restored")
                {
                    restored = true;
                    List<string> differences = before.Diff(LiveFingerprint.Of(f.net.ctx, f.net.ids, f.net.scheduler, f.net.journal));
                    T.Eq(0, differences.Count, "lifecycle fault restores complete touched state: " + string.Join(";", differences.ToArray()));
                }
            };
            f.net.ctx.Lifecycle.commitFaultAfter = 4;
            T.Check(!f.net.ctx.Lifecycle.Reconcile(episode, "headless promotion commit fault"), "interrupted commit reports no durable success");
            f.net.ctx.Lifecycle.commitBoundary = null;
            T.Check(restored, "fault exercised real atomic rollback boundary");
            T.Eq(1, f.net.ctx.characters.Count, "no promoted record survives failed commit");
            T.Eq(1, f.org.knownMembers.Count, "no provisional membership survives");
            T.Eq(next, f.net.ids.PeekNextId, "new CharacterIds rolled back");
            T.Eq(4, f.org.Committed, "anonymous source stock restored");
            T.Eq(humans, Living(f), "failure preserves original humans");
            T.Check(!episode.consequencesApplied && !episode.releaseApplied && episode.members.Count(m => !m.IsNamed) == 4, "anonymous ownership remains uncommitted");
            T.Check(episode.members.Select((m, i) => ReferenceEquals(m.pawn, pawns[i])).All(same => same), "all original slot binding objects remain after rollback");
            T.Check(episode.members.Where(m => !m.IsNamed).All(m => f.net.physical.TokenOf(m.pawn).temporaryEpisode == episode.id), "failed transaction leaves original temporary reservation in force");
            T.Check(f.net.ctx.Lifecycle.Reconcile(episode, "headless clean commit retry"), "same Episode safely retries");
            T.Check(episode.IsComplete, "retry fully completes");
            T.Eq(5, f.net.ctx.characters.Count, "retry creates each identity exactly once");
            T.Eq(next + 4, f.net.ids.PeekNextId, "retry allocates precisely four CharacterIds");
            T.Eq(humans, Living(f), "retry still conserves humans");
        }

        private static void OptionalFactsFailure()
        {
            Fixture f = Crew(13);
            f.net.physical.playerVisiblePlacement = true;
            PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Rifleman, 1));
            f.net.ctx.Lifecycle.Materialize(episode);
            f.net.physical.TokenOf(episode.members[0].pawn).evidence = ConcretizationEvidence.PlayerCombat;
            ExitAll(f, episode);
            f.net.physical.ThrowOn("promotion-facts");
            T.Check(f.net.ctx.Lifecycle.Reconcile(episode, "headless optional facts missing"), "optional evidence failure does not block ordinary known outcome");
            T.Check(episode.IsComplete && !episode.members[0].IsNamed && episode.members[0].pawn == null, "unsupported/unread evidence remains ordinary ephemeral history");
            T.Eq(1, f.net.ctx.characters.Count, "failed optional collector invents zero identities");
            T.Eq(12, f.org.Healthy, "ordinary source human returned exactly once");
            T.Eq(13, Living(f), "optional evidence failure changes no human count");
        }

        private static void MandatoryNames()
        {
            foreach (bool capture in new[] { false, true })
            {
                Fixture f = Crew(capture ? 13 : 5);
                f.net.physical.playerVisiblePlacement = true;
                PhysicalEpisode episode = Plan(f, new RoleCapacity(OperationalRole.Medic, 1));
                f.net.ctx.Lifecycle.Materialize(episode);
                EpisodeMember member = episode.members[0];
                FakePhysicalWorldPort.Token token = f.net.physical.TokenOf(member.pawn);
                if (capture) f.net.physical.Hold(member.pawn, ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner);
                else ExitAll(f, episode);
                int next = f.net.ids.PeekNextId;
                int humans = Living(f);
                f.net.physical.ThrowOn("promotion-facts");
                T.Check(!f.net.ctx.Lifecycle.Reconcile(episode, "headless mandatory facts fault"), "P0/S1 missing actual name fails closed: capture=" + capture);
                token.name = null;
                T.Check(!f.net.ctx.Lifecycle.Reconcile(episode, "headless mandatory name unknown"), "unknown name cannot be replaced with Network-generated name: capture=" + capture);
                T.Check(!episode.consequencesApplied && !episode.releaseApplied && !member.IsNamed && member.IsBound, "mandatory identity remains protected in original anonymous slot");
                T.Eq(1, f.net.ctx.characters.Count, "no partial anonymous identity is added");
                T.Eq(next, f.net.ids.PeekNextId, "name refusal allocates no CharacterId");
                T.Eq(1, f.org.Committed, "failed mandatory promotion keeps source stock committed");
                T.Eq(humans, Living(f), "missing actual name cannot lose or duplicate a human");
                token.name = NameSnapshot.Person("Actual", capture ? "Captured" : "Encountered", "SamePawn");
                T.Check(f.net.ctx.Lifecycle.Reconcile(episode, "headless actual name recovered"), "actual name permits safe terminal retry");
                T.Check(episode.IsComplete && member.IsNamed, "required identity commits only when actual name is known");
                T.Eq(token.name.Display, f.net.ctx.characters.Get(member.character).name.Display, "retry preserves actual recovered Pawn name");
                T.Eq(2, f.net.ctx.characters.Count, "required person is created once after recovery");
                T.Eq(humans, Living(f), "recovered promotion preserves human count");
            }
        }

        private static void CurrentConsumers()
        {
            Fixture f = Crew(20);
            List<KnownCharacter> added = new List<KnownCharacter>();
            for (int i = 0; i < 7; i++)
            {
                KnownCharacter c = new KnownCharacter
                {
                    id = new CharacterId(f.net.ids.NextId()), org = f.actor.id, role = CharacterRole.Member,
                    opRole = OperationalRole.Rifleman, name = NameSnapshot.Person("Overflow" + i, null, "History")
                };
                f.net.ctx.characters.Add(c);
                f.org.knownMembers.Add(c.id);
                f.org.tiers[0].healthy--;
                added.Add(c);
            }
            f.org.knownMembers.Add(added[0].id);
            f.org.knownMembers.Add(added[6].id);
            T.Eq(8, ContractorService.CurrentNamedCount(f.actor, f.net.ctx.characters), "all eight current overflow identities count once despite duplicate history IDs");
            T.Eq(20, ContractorService.Headcount(f.actor, f.net.ctx.characters), "overflow replaces anonymous humans rather than inflating total");
            T.Eq(3, ContractorService.JobCapacity(f.actor, f.net.ctx.characters), "complete overflow membership participates in bounded job formula");
            T.Eq(CareerPolicy.OperatingReserve(true, 20), CareerService.OperatingReserve(f.actor, f.net.ctx.characters), "operating reserve uses canonical living membership");
            added[0].status = CharacterStatus.Dead;
            added[1].status = CharacterStatus.Defected;
            added[2].status = CharacterStatus.Lost;
            added[3].status = CharacterStatus.Retired;
            added[4].org = new ActorId(f.net.ids.NextId());
            added[5].status = CharacterStatus.Wounded;
            added[6].status = CharacterStatus.Missing;
            f.leader.status = CharacterStatus.Captured;
            f.leader.custody = CustodyState.OutOfCustody;
            T.Eq(3, ContractorService.CurrentNamedCount(f.actor, f.net.ctx.characters), "history/dead/defected/retired/former members exclude; held, Missing and Wounded current people remain");
            T.Eq(15, ContractorService.Headcount(f.actor, f.net.ctx.characters), "noncurrent history does not add old NPC humans");
            T.Eq(2, ContractorService.JobCapacity(f.actor, f.net.ctx.characters), "history cannot inflate organization job capacity");
            T.Eq(CareerPolicy.OperatingReserve(true, 15), CareerService.OperatingReserve(f.actor, f.net.ctx.characters), "career reserve uses same noncurrent-member exclusion");
            T.Check(!OrganizationSeatPolicy.IsCurrentMember(added[1], f.actor.id), "recruited Defected person leaves old NPC live membership");
        }
    }
}
