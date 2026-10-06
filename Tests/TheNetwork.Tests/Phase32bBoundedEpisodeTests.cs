using System;
using System.Collections.Generic;
using System.Linq;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Settings;

namespace TheNetwork.Tests
{
    public static class Phase32bBoundedEpisodeTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Bounds_OversizedLoadedEpisodeQueriesNobodyAndPreservesBindings", Oversized));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Bounds_InvalidPlannedEpisodeCreatesAndPlacesNobody", Planned));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Bounds_DirectValidationRejectsMissingEmptyAndOversizedMembers", Direct));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Bounds_ClosedPendingReleasePreservesCommitWithoutPortActions", Closed));
        }

        private static PhysicalEpisode Episode(TestNet net, out NetworkActor actor)
        {
            actor = PhysicalLifecycleTests.Make(net, ContractorForm.Company, "bounded-load");
            OrganizationProfile org = actor.Get<OrganizationProfile>();
            org.tiers.Clear();
            org.tiers.Add(new TierCount(Tier.Regular, 10));
            org.committed.Clear();
            org.committed.Add(new TierCount(Tier.Regular, 9));
            PhysicalEpisode episode = new PhysicalEpisode
            {
                id = new EpisodeId(net.ids.NextId()), actor = actor.id, state = EpisodeState.Open,
                createdTick = net.clock.Now, purposeKey = "BoundedLoadProbe", cause = new EpisodeCause { devKey = "HeadlessBoundedLoad" }
            };
            for (int i = 0; i < 9; i++)
            {
                PawnRef binding = net.physical.Create(new ProjectionRequest { episode = episode.id, actor = actor.id, slot = i, role = OperationalRole.Rifleman });
                EpisodeMember member = new EpisodeMember { slot = i, tier = Tier.Regular, seatRole = OperationalRole.Rifleman,
                    pawn = binding, state = MemberState.Present, playerVisibleTick = net.clock.Now, p0Eligible = true };
                episode.members.Add(member);
                net.physical.EpisodeBindingChanged(episode, member);
                net.physical.ExitNormally(binding, 1);
            }
            net.ctx.episodes.Add(episode);
            return episode;
        }

        private static void Oversized()
        {
            TestNet net = new TestNet(32301);
            NetworkActor actor;
            PhysicalEpisode episode = Episode(net, out actor);
            PawnRef[] bindings = episode.members.Select(m => m.pawn).ToArray();
            int characters = net.ctx.characters.Count, nextId = net.ids.PeekNextId, actions = net.physical.actions.Count;
            T.Check(!net.ctx.Lifecycle.Reconcile(episode, "loaded-oversized"), "malformed loaded Episode refuses before any terminal evidence query");
            T.Eq(EpisodeState.Quarantined, episode.state, "oversized Episode remains blocked");
            T.Eq("EpisodeMemberCount", episode.quarantineKey, "the exact violated bound is diagnosed");
            T.Eq(0, net.physical.observes, "no Pawn observation occurs outside the eight-member bound");
            T.Eq(0, net.physical.promotionReads, "no ninth-candidate log/name query occurs");
            T.Eq(actions, net.physical.actions.Count, "no placement, release, binding replacement or other physical action occurs");
            T.Eq(nextId, net.ids.PeekNextId, "no character id is allocated");
            T.Eq(characters, net.ctx.characters.Count, "no character is created");
            T.Eq(9, actor.Get<OrganizationProfile>().Committed, "every original checked-out human remains accounted for");
            T.Eq(10, actor.Get<OrganizationProfile>().Healthy, "no abstract human is restored while a Pawn remains owned");
            for (int i = 0; i < bindings.Length; i++)
                T.Check(ReferenceEquals(bindings[i], episode.members[i].pawn) && !episode.members[i].IsNamed, "existing slot binding is preserved: " + i);
            T.Check(!episode.consequencesApplied && !episode.releaseApplied, "no terminal marker is invented");
            T.Eq(0, net.ctx.Lifecycle.SettleForRemoval(), "malformed batch is reported and preserved during removal settlement too");
            T.Eq(0, net.physical.observes, "removal settlement does not evade the observation bound");
        }

        private static void Planned()
        {
            TestNet net = new TestNet(32302);
            NetworkActor actor;
            PhysicalEpisode episode = Episode(net, out actor);
            episode.state = EpisodeState.Planned;
            foreach (EpisodeMember member in episode.members) member.state = MemberState.Created;
            int creates = net.physical.creates;
            T.Eq(0, net.ctx.Lifecycle.Materialize(episode), "oversized Planned batch places nobody");
            T.Eq(creates, net.physical.creates, "existing bindings are never regenerated");
            T.Eq(0, net.physical.places, "no placement outside the bound");
            T.Eq(0, net.physical.factionsCreated, "no encounter faction is created for malformed load state");
            episode.state = EpisodeState.Planned;
            net.ctx.Lifecycle.ResolvePlanned(episode);
            T.Eq(EpisodeState.Quarantined, episode.state, "load resolver enforces the same bound");
            T.Eq(0, net.physical.observes, "load resolver queries no unbounded member set");
            T.Check(episode.members.All(m => m.IsBound), "load failure preserves every existing PawnRef");
        }

        private static void Direct()
        {
            TestNet net = new TestNet(32303);
            NetworkActor actor;
            PhysicalEpisode episode = Episode(net, out actor);
            List<EpisodeMember> original = episode.members;
            foreach (List<EpisodeMember> malformed in new List<EpisodeMember>[] { null, new List<EpisodeMember>(), original })
            {
                episode.members = malformed;
                ReconciliationPlan plan = new ReconciliationPlan { episode = episode, actor = actor };
                string reason = null;
                try { ReconciliationPlanner.Validate(net.ctx, plan); }
                catch (PlanInvalidException ex) { reason = ex.reasonKey; }
                T.Eq("EpisodeMemberCount", reason, "direct validation rejects null, empty and nine-member batches before assignments");
                T.Check(!episode.consequencesApplied && !episode.releaseApplied, "validation changes no durable stage markers");
            }
            episode.members = original;
            T.Eq(9, actor.Get<OrganizationProfile>().Committed, "direct refusal preserves all original stock and bindings");
        }

        private static void Closed()
        {
            TestNet net = new TestNet(32304);
            NetworkActor actor;
            PhysicalEpisode episode = Episode(net, out actor);
            episode.state = EpisodeState.Closed;
            episode.consequencesApplied = true;
            int actions = net.physical.actions.Count;
            PawnRef[] bindings = episode.members.Select(m => m.pawn).ToArray();
            T.Check(!net.ctx.Lifecycle.FinishPending(episode), "oversized Closed batch cannot resume RELEASE");
            T.Eq(EpisodeState.Closed, episode.state, "committed consequence marker is preserved rather than reopening the batch");
            T.Check(episode.consequencesApplied && !episode.releaseApplied && !episode.followUpApplied, "no durable stage advances on refusal");
            T.Check(episode.lastError.Contains("EpisodeMemberCount"), "pending-stage diagnosis identifies the member bound");
            T.Eq(actions, net.physical.actions.Count, "no handoff, release or faction mutation occurs outside the bound");
            T.Eq(0, net.physical.observes, "no member observation occurs");
            T.Eq(0, net.physical.promotionReads, "no identity evidence query occurs");
            for (int i = 0; i < bindings.Length; i++)
                T.Check(ReferenceEquals(bindings[i], episode.members[i].pawn), "Closed refusal preserves slot binding: " + i);
        }
    }
}
