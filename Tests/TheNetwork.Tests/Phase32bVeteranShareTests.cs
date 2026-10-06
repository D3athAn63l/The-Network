using System;
using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Tests
{
    /// <summary>Anonymous tiers retain their experience mix while the same humans are checked out.</summary>
    public static class Phase32bVeteranShareTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Phys32b.VeteranShare_MixedWoundedPartialAndFullCheckoutReturn", MixedWounded));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.VeteranShare_AllRegularCommittedKeepsGreen", AllRegular));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.VeteranShare_AllMixedCommittedUsesActualTierRatio", AllMixed));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.VeteranShare_ServiceCheckoutReturnKeepsCapability", ServiceCheckout));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.VeteranShare_ActualVeteranLossChangesCapability", VeteranLoss));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.VeteranShare_GenuinelyEmptyKeepsExistingFallback", Empty));
        }

        private sealed class Fixture
        {
            public TestNet net;
            public NetworkActor actor;
            public OrganizationProfile org;
        }

        private static Fixture Crew(int recruits, int regulars, int veterans, float skill = 0.3f)
        {
            TestNet net = new TestNet(32702);
            NetworkActor actor = new NetworkActor
            {
                id = new ActorId(net.ids.NextId()), kind = ActorKind.Organization,
                seed = 123, name = NameSnapshot.Org("Anonymous tier crew")
            };
            OrganizationProfile org = new OrganizationProfile { capacity = 32 };
            org.tiers.Add(new TierCount(Tier.Recruit, recruits));
            org.tiers.Add(new TierCount(Tier.Regular, regulars));
            org.tiers.Add(new TierCount(Tier.Veteran, veterans));
            actor.Add(org);
            actor.Add(new ContractorProfile());
            actor.Add(new ContractorSimulation { skill = skill });
            net.ctx.actors.Add(actor);
            return new Fixture { net = net, actor = actor, org = org };
        }

        // Prepare the existing generic checkout representation, including a matching ForceCommitment
        // so production Return can restore these exact source tiers. Committed humans use healthy.
        private static ForceCommitment Move(Fixture f, int recruits, int regulars, int veterans)
        {
            int[] counts = { recruits, regulars, veterans };
            Tier[] tiers = { Tier.Recruit, Tier.Regular, Tier.Veteran };
            ForceCommitment force = new ForceCommitment();
            for (int i = 0; i < tiers.Length; i++)
            {
                int count = counts[i];
                if (count == 0) continue;
                TierCount available = f.org.TierOf(tiers[i]);
                T.Check(count > 0 && count <= available.healthy, "checkout uses existing healthy humans of the same tier");
                available.healthy -= count;
                f.org.TierOf(tiers[i], true).healthy += count;
                force.forces.Add(new TierCount(tiers[i], count));
            }
            return force;
        }

        private static void Return(Fixture f, ForceCommitment force, CasualtyReport losses = null)
        {
            f.net.ctx.Contractors.Return(f.actor, new OperationId(f.net.ids.NextId()), force, losses);
        }

        private static void SameCapability(Fixture f, float share, ExperienceBand band, string state)
        {
            T.Eq(share, ContractorService.VeteranShare(f.org), state + " preserves the exact anonymous Veteran ratio");
            T.Eq(band, ContractorService.Experience(f.actor), state + " preserves ExperienceBand");
        }

        private static void MixedWounded()
        {
            Fixture f = Crew(3, 7, 4);
            ContractorService.AddWounded(f.org, Tier.Recruit, 1, f.net.clock.Now + Ticks.PerDay);
            ContractorService.AddWounded(f.org, Tier.Regular, 2, f.net.clock.Now + Ticks.PerDay);
            ContractorService.AddWounded(f.org, Tier.Veteran, 2, f.net.clock.Now + Ticks.PerDay);
            float share = 6f / 19f;
            ExperienceBand band = ContractorService.Experience(f.actor);
            SameCapability(f, share, band, "healthy and wounded roster");

            ForceCommitment partial = Move(f, 1, 2, 1);
            T.Eq(10, f.org.Healthy, "partial checkout leaves ten available humans");
            T.Eq(4, f.org.Committed, "partial checkout records four generic humans");
            T.Eq(5, f.org.Wounded, "recovering humans stay in their source tiers");
            SameCapability(f, share, band, "partial mixed checkout");

            ForceCommitment rest = Move(f, 2, 5, 3);
            T.Eq(0, f.org.Healthy, "all remaining healthy humans are checked out");
            T.Eq(14, f.org.Committed, "all fourteen healthy source humans are committed");
            SameCapability(f, share, band, "full healthy checkout with wounded still recovering");

            Return(f, partial);
            T.Eq(4, f.org.Healthy, "partial return restores four humans");
            T.Eq(10, f.org.Committed, "partial return leaves the other checkout intact");
            SameCapability(f, share, band, "partial return");
            Return(f, rest);
            T.Eq(14, f.org.Healthy, "all healthy humans return once");
            T.Eq(0, f.org.Committed, "all committed source counts are removed on return");
            T.Eq(5, f.org.Wounded, "existing wounded population is unchanged by checkout and return");
            T.Eq(3, f.org.TierOf(Tier.Recruit).healthy, "Recruit source stock restored");
            T.Eq(7, f.org.TierOf(Tier.Regular).healthy, "Regular source stock restored");
            T.Eq(4, f.org.TierOf(Tier.Veteran).healthy, "Veteran source stock restored");
            SameCapability(f, share, band, "complete return");
        }

        private static void AllRegular()
        {
            Fixture f = Crew(0, 2, 0);
            SameCapability(f, 0f, ExperienceBand.Green, "two available Regulars at fixture skill 0.3");
            ForceCommitment force = Move(f, 0, 2, 0);
            T.Eq(0, f.org.Healthy + f.org.Wounded, "no anonymous roster stock remains available");
            T.Eq(2, f.org.Committed, "the two actual Regulars still exist in committed stock");
            SameCapability(f, 0f, ExperienceBand.Green, "all Regulars committed");
            Return(f, force);
            T.Eq(2, f.org.Healthy, "the same two Regulars return");
            T.Eq(0, f.org.Committed, "Regular commitment is fully returned");
            SameCapability(f, 0f, ExperienceBand.Green, "all Regulars returned");
        }

        private static void AllMixed()
        {
            Fixture f = Crew(3, 5, 2);
            float share = 2f / 10f;
            ExperienceBand band = ContractorService.Experience(f.actor);
            SameCapability(f, share, band, "available mixed roster");
            ForceCommitment force = Move(f, 3, 5, 2);
            T.Eq(0, f.org.Healthy + f.org.Wounded, "all tier population is represented only by committed stock");
            T.Eq(10, f.org.Committed, "all ten source humans remain represented");
            T.Eq(2, f.org.TierOf(Tier.Veteran, true).healthy, "committed Veterans retain their actual source tier");
            SameCapability(f, share, band, "all mixed humans committed without the empty fallback");
            Return(f, force);
            T.Eq(10, f.org.Healthy, "all ten mixed humans return");
            T.Eq(0, f.org.Committed, "mixed checkout is cleared on return");
            SameCapability(f, share, band, "mixed humans returned");
        }

        private static void ServiceCheckout()
        {
            float[] skills = { 0.05f, 0.3f, 0.65f, 0.95f };
            for (int i = 0; i < skills.Length; i++)
            {
                Fixture f = Crew(6, 7, 5, skills[i]);
                ContractorService.AddWounded(f.org, Tier.Recruit, 1, f.net.clock.Now + Ticks.PerDay);
                ContractorService.AddWounded(f.org, Tier.Regular, 2, f.net.clock.Now + Ticks.PerDay);
                ContractorService.AddWounded(f.org, Tier.Veteran, 1, f.net.clock.Now + Ticks.PerDay);
                float share = 6f / 22f;
                ExperienceBand band = ContractorService.Experience(f.actor);
                SameCapability(f, share, band, "production checkout baseline at skill " + skills[i]);
                OperationId op = new OperationId(f.net.ids.NextId());
                ForceCommitment force = f.net.ctx.Contractors.Checkout(f.actor, op, 0.6f);
                T.Check(force.Headcount > 0, "production Checkout sends actual generic humans");
                T.Check(f.org.Healthy > 0 && f.org.Committed > 0, "production Checkout splits the healthy stock");
                T.Eq(18, f.org.Healthy + f.org.Committed, "production Checkout conserves healthy anonymous population");
                T.Eq(4, f.org.Wounded, "production Checkout leaves wounded humans recovering");
                SameCapability(f, share, band, "production Checkout at skill " + skills[i]);
                f.net.ctx.Contractors.Return(f.actor, op, force, null);
                T.Eq(18, f.org.Healthy, "production Return restores every unharmed generic human");
                T.Eq(0, f.org.Committed, "production Return removes committed humans");
                T.Check(!f.actor.Get<ContractorSimulation>().commitments.Contains(op), "production Return clears its operation commitment");
                SameCapability(f, share, band, "production Return at skill " + skills[i]);
            }
        }

        private static void VeteranLoss()
        {
            Fixture f = Crew(0, 4, 6);
            SameCapability(f, 0.6f, ExperienceBand.Seasoned, "six Veterans and four Regulars");
            ForceCommitment force = Move(f, 0, 4, 6);
            SameCapability(f, 0.6f, ExperienceBand.Seasoned, "unchanged humans while committed");
            CasualtyReport losses = new CasualtyReport();
            losses.killed.Add(new TierCount(Tier.Veteran, 5));
            losses.wounded.Add(new TierCount(Tier.Veteran, 1));
            Return(f, force, losses);
            T.Eq(0, f.org.Committed, "resolved casualty report clears the checkout");
            T.Eq(4, f.org.Healthy, "the four surviving Regulars return healthy");
            T.Eq(1, f.org.Wounded, "the surviving Veteran remains counted while recovering");
            T.Eq(1, f.org.TierOf(Tier.Veteran).wounded, "wounded survivor retains Veteran tier");
            SameCapability(f, 1f / 5f, ExperienceBand.Experienced, "actual five-Veteran loss changes population and capability");
        }

        private static void Empty()
        {
            OrganizationProfile empty = new OrganizationProfile();
            T.Eq(0.5f, ContractorService.VeteranShare(empty), "genuinely empty lists retain the existing fallback");
            empty.tiers.Add(new TierCount(Tier.Recruit, 0));
            empty.tiers.Add(new TierCount(Tier.Veteran, 0));
            empty.committed.Add(new TierCount(Tier.Regular, 0));
            empty.committed.Add(new TierCount(Tier.Veteran, 0));
            T.Eq(0.5f, ContractorService.VeteranShare(empty), "zero-count entries are still genuinely empty");
            Fixture f = Crew(0, 0, 0);
            SameCapability(f, 0.5f, ExperienceBand.Seasoned, "genuinely empty fixture at skill 0.3 retains existing capability fallback");
        }
    }
}
