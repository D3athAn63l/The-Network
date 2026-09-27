using System;
using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using TheNetwork.Settings;

namespace TheNetwork.Tests
{
    /// <summary>Phase 2 contractors: instantiation, composition, upkeep, succession, population.</summary>
    public static class ContractorTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Contractors.InstantiatedFromWorldSnapshot", FromSnapshot));
            t.Add(new KeyValuePair<string, Action>("Contractors.SoloAndOrganizationComposition", Composition));
            t.Add(new KeyValuePair<string, Action>("Contractors.CanIssueWorkAddsIssuerOnly", CanIssue));
            t.Add(new KeyValuePair<string, Action>("Contractors.FameIndependentOfCapability", FameVsCapability));
            t.Add(new KeyValuePair<string, Action>("Contractors.PlayerHasNoSimulation", PlayerNotContractor));
            t.Add(new KeyValuePair<string, Action>("Contractors.UpkeepStaggeredOncePerDay", UpkeepStagger));
            t.Add(new KeyValuePair<string, Action>("Contractors.WoundRecoveryAndRecruitment", WoundsAndRecruits));
            t.Add(new KeyValuePair<string, Action>("Contractors.MinimalSuccession", Succession));
            t.Add(new KeyValuePair<string, Action>("Contractors.SoloDeathEndsActor", SoloDeath));
            t.Add(new KeyValuePair<string, Action>("Contractors.LegendaryHasNoPlotArmor", NoPlotArmor));
            t.Add(new KeyValuePair<string, Action>("Contractors.PopulationTopUp", Population));
            t.Add(new KeyValuePair<string, Action>("Contractors.MoraleHysteresis", MoraleHysteresis));
        }

        /// <summary>A world with a generated cast of <paramref name="count"/> contractors, imported and instantiated.</summary>
        public static TestNet WorldWithCast(int count, int seed = 424242, Action<List<ContractorTemplate>> tweak = null)
        {
            TestNet n = new TestNet(seed);
            int ids = 0;
            CastGenerator gen = new CastGenerator(NamePools.Fallback(), 777, () => "tpl-" + (++ids), null);
            GlobalNetworkRoster roster = new GlobalNetworkRoster();
            roster.contractorTemplates.AddRange(gen.GenerateContractors(count));
            tweak?.Invoke(roster.contractorTemplates);
            n.ctx.cast.imported = false;
            n.ctx.Actors.ImportCast(roster, NetworkSettings.CurrentVersion, null);
            n.ctx.Contractors.InstantiateFromSnapshot();
            return n;
        }

        public static List<NetworkActor> Contractors(TestNet n)
        {
            List<NetworkActor> l = new List<NetworkActor>();
            foreach (NetworkActor a in n.ctx.actors.actors) if (ContractorService.IsNpcContractor(a)) l.Add(a);
            return l;
        }

        public static ContractorTemplate Template(ContractorForm form, ExperienceBand exp, FameBand fame, bool canIssue = false, string style = "Professional")
        {
            return new ContractorTemplate
            {
                templateId = Guid.NewGuid().ToString("N"), provenance = TemplateProvenance.Custom, displayName = form + " " + exp + " " + Guid.NewGuid().ToString("N").Substring(0, 4),
                form = form, startingExperience = exp, startingFame = fame, canIssueWork = canIssue, doctrineStyle = style,
                specialties = new List<string> { "combat acquisition" }
            };
        }

        public static NetworkActor Make(TestNet n, ContractorTemplate t)
        {
            return n.ctx.Contractors.Instantiate(t, ProvenanceSource.GlobalCast, t.templateId);
        }

        private static void FromSnapshot()
        {
            TestNet n = WorldWithCast(100);
            List<NetworkActor> cs = Contractors(n);
            T.Eq(100, cs.Count, "about 100 contractor actors from the world snapshot");
            int linked = 0;
            foreach (CastEntry e in n.ctx.cast.entries) if (e.kind == CastEntryKind.Contractor && e.actor.IsValid) linked++;
            T.Eq(100, linked, "every snapshot entry links to its world-local actor");
            HashSet<int> seen = new HashSet<int>();
            foreach (NetworkActor a in cs)
            {
                T.Check(seen.Add(a.id.Value), "world-local ids are unique");
                T.Check(a.provenance.templateId != null && a.provenance.source == ProvenanceSource.GlobalCast, "template id kept as provenance only");
            }
            T.Eq(0, n.ctx.Contractors.InstantiateFromSnapshot(), "instantiation is idempotent");
            T.Eq(100, Contractors(n).Count, "no duplicates on a second pass");
            int solos = 0, orgs = 0;
            foreach (NetworkActor a in cs) if (ContractorService.IsSolo(a)) solos++; else orgs++;
            T.Check(solos > 10 && orgs > 10, "a mix of Solos (" + solos + ") and organizations (" + orgs + ")");
            T.Eq(0, NoPawnFields(), "no pawn or PawnRef field exists on contractor data");
        }

        private static int NoPawnFields()
        {
            int bad = 0;
            Type[] types = { typeof(ContractorSimulation), typeof(OrganizationProfile), typeof(ContractorProfile), typeof(KnownCharacter), typeof(NetworkActor) };
            foreach (Type ty in types)
            {
                foreach (System.Reflection.FieldInfo f in ty.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    string tn = f.FieldType.FullName ?? "";
                    if (tn.Contains("Verse.Pawn") || f.Name.ToLowerInvariant().Contains("pawnref") || tn.Contains("PawnRef")) bad++;
                }
            }
            return bad;
        }

        private static void Composition()
        {
            TestNet n = new TestNet();
            NetworkActor solo = Make(n, Template(ContractorForm.Solo, ExperienceBand.Veteran, FameBand.Local));
            T.Eq(ActorKind.Individual, solo.kind, "Solo is an Individual");
            T.Check(solo.Has<ContractorProfile>() && solo.Has<ContractorSimulation>(), "Solo: ContractorProfile + ContractorSimulation");
            T.Check(!solo.Has<OrganizationProfile>(), "Solo: no OrganizationProfile");
            KnownCharacter self = n.ctx.characters.Get(solo.bindings.embodies);
            T.Check(self != null && self.embodiedBy == solo.id, "Solo embodies a Known Character record");
            T.Eq(CustodyState.Unmaterialized, self.custody, "record only: no pawn");

            NetworkActor org = Make(n, Template(ContractorForm.Company, ExperienceBand.Seasoned, FameBand.Established));
            T.Eq(ActorKind.Organization, org.kind, "company is an Organization");
            OrganizationProfile op = org.Get<OrganizationProfile>();
            T.Check(op != null && org.Has<ContractorProfile>() && org.Has<ContractorSimulation>(), "org: profile + simulation + OrganizationProfile");
            T.Check(op.leader.IsValid && n.ctx.characters.Get(op.leader).role == CharacterRole.Leader, "a leader record");
            T.Check(op.lieutenants.Count <= 2 && op.knownMembers.Count <= OrganizationProfile.MaxKnownMembers, "bounded named people");
            T.Check(op.Healthy >= 12, "company headcount as tiers (" + op.Healthy + ")");
            T.Check(n.ctx.Contractors.Strength(org) > n.ctx.Contractors.Strength(solo), "strength is derived from roster and kit");
            NetworkActor duo = Make(n, Template(ContractorForm.Duo, ExperienceBand.Green, FameBand.Unknown));
            T.Eq(2, duo.Get<OrganizationProfile>().knownMembers.Count, "a duo is two named people");
        }

        private static void CanIssue()
        {
            TestNet n = new TestNet();
            NetworkActor withIssuer = Make(n, Template(ContractorForm.Team, ExperienceBand.Seasoned, FameBand.Local, canIssue: true));
            NetworkActor without = Make(n, Template(ContractorForm.Team, ExperienceBand.Seasoned, FameBand.Local, canIssue: false));
            T.Check(withIssuer.Has<IssuerProfile>(), "canIssueWork adds IssuerProfile");
            T.Check(!without.Has<IssuerProfile>(), "without it, no IssuerProfile");
            T.Check(withIssuer.Has<ContractorProfile>(), "and it is still a contractor");
            n.Advance(Ticks.PerDay * 20);
            T.Eq(0, n.recorder.Count(EventKeys.ContractPosted), "no NPC-issued contracts appear in Phase 2");
        }

        private static void FameVsCapability()
        {
            TestNet n = new TestNet();
            NetworkActor famousGreen = Make(n, Template(ContractorForm.Team, ExperienceBand.Green, FameBand.Famous));
            NetworkActor obscureElite = Make(n, Template(ContractorForm.Team, ExperienceBand.Elite, FameBand.Unknown));
            T.Eq(FameBand.Famous, famousGreen.reputation.fame, "fame from the template");
            T.Check(ContractorService.Experience(famousGreen) <= ExperienceBand.Experienced, "a famous name can be mediocre (" + ContractorService.Experience(famousGreen) + ")");
            T.Check(ContractorService.Experience(obscureElite) >= ExperienceBand.Veteran, "an obscure group can be excellent (" + ContractorService.Experience(obscureElite) + ")");
            T.Eq(CareerStage.Declining, famousGreen.Get<ContractorSimulation>().careerStage, "living on an old name");
            foreach (ExperienceBand b in Enum.GetValues(typeof(ExperienceBand)))
            {
                NetworkActor s = Make(n, Template(ContractorForm.Solo, b, FameBand.Unknown));
                T.Eq(b, ContractorService.Experience(s), "Solo capability band " + b + " derived as set");
                NetworkActor o = Make(n, Template(ContractorForm.Company, b, FameBand.Legendary));
                T.Eq(b, ContractorService.Experience(o), "company capability band " + b + " derived as set, whatever its fame");
            }
        }

        private static void PlayerNotContractor()
        {
            TestNet n = WorldWithCast(30);
            NetworkActor player = n.ctx.actors.PlayerProxy;
            T.Check(player.Has<IssuerProfile>(), "the player issues work");
            T.Check(!player.Has<ContractorProfile>() && !player.Has<ContractorSimulation>() && !player.Has<OrganizationProfile>(), "the player is not a contractor in Phase 2");
            T.Check(!ContractorService.IsNpcContractor(player), "never simulated");
            T.Check(!n.scheduler.Has(ContractorService.UpkeepJob, player.id.Value), "no upkeep job for the player");
        }

        private static void UpkeepStagger()
        {
            TestNet n = WorldWithCast(100);
            int[] perHour = new int[24];
            foreach (NetworkActor a in Contractors(n))
            {
                ScheduledJob j = n.scheduler.Find(ContractorService.UpkeepJob, a.id.Value);
                T.Check(j != null, "one upkeep job per contractor");
                if (j == null) continue;
                T.Check(j.dueTick > n.clock.Now && j.dueTick <= n.clock.Now + Ticks.PerDay + 1, "first run within a day");
                perHour[((j.dueTick - n.clock.Now) / Ticks.PerHour) % 24]++;
            }
            int max = 0;
            foreach (int h in perHour) max = Math.Max(max, h);
            T.Check(max <= 15, "spread across the day, not all at once (busiest hour " + max + ")");
            int before = n.scheduler.Count;
            n.Advance(Ticks.PerDay * 3);
            T.Check(n.scheduler.Count <= before + 1, "upkeep reschedules itself, never piles up");
            foreach (NetworkActor a in Contractors(n))
            {
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                T.Check(sim.lastUpkeepTick > n.clock.Now - Ticks.PerDay * 2, "each ran recently");
            }
        }

        private static void WoundsAndRecruits()
        {
            TestNet n = new TestNet();
            NetworkActor org = Make(n, Template(ContractorForm.Team, ExperienceBand.Seasoned, FameBand.Local));
            OrganizationProfile op = org.Get<OrganizationProfile>();
            ContractorSimulation sim = org.Get<ContractorSimulation>();
            int healthy0 = op.Healthy;
            TierCount reg = op.TierOf(Tier.Regular);
            reg.healthy -= 2;
            ContractorService.AddWounded(op, Tier.Regular, 2, n.clock.Now + Ticks.PerDay * 4);
            T.Eq(2, op.Wounded, "two wounded in a recovery bucket");
            T.Check(n.ctx.Contractors.WoundedShare(org) > 0f, "wounded share visible to willingness");
            n.Advance(Ticks.PerDay * 6);
            T.Eq(0, op.Wounded, "healed by upkeep once due");
            T.Check(op.Healthy >= healthy0, "back on the roster");
            // Losses below capacity with funds: upkeep recruits.
            op.TierOf(Tier.Recruit).healthy = 0;
            op.TierOf(Tier.Regular).healthy = 1;
            op.TierOf(Tier.Veteran).healthy = 0;
            sim.funds = 5000;
            int before = op.Healthy;
            n.Advance(Ticks.PerDay * 20);
            T.Check(op.Healthy > before, "recruits replace losses (" + before + " → " + op.Healthy + ")");
            T.Check(op.recruitment.recruitedTotal > 0, "recruitment recorded");
            for (int i = 0; i < 12; i++) ContractorService.AddWounded(op, Tier.Recruit, 1, n.clock.Now + Ticks.PerDay * (i + 1) * 3);
            T.Check(op.woundedRecovery.Count <= OrganizationProfile.MaxRecoveryBuckets, "recovery buckets stay bounded");
        }

        private static CasualtyReport Kill(CharacterId c)
        {
            CasualtyReport r = new CasualtyReport();
            r.fates.Add(new CharacterFate { character = c, fate = Fate.Killed });
            return r;
        }

        private static void Succession()
        {
            TestNet n = new TestNet();
            NetworkActor org = Make(n, Template(ContractorForm.Company, ExperienceBand.Veteran, FameBand.Famous));
            OrganizationProfile op = org.Get<OrganizationProfile>();
            CharacterId first = op.leader;
            CharacterId lt = op.lieutenants[0];
            n.ctx.Contractors.ApplyCasualties(org, Kill(first), ContractId.None, OperationId.None, false);
            T.Eq(ActorStatus.Active, org.status, "the organization survives its leader");
            T.Eq(lt, op.leader, "a living lieutenant succeeds first");
            T.Eq(1, n.recorder.Count(EventKeys.LeaderKilled), "Leader.Killed published");
            T.Eq(1, n.recorder.Count(EventKeys.LeaderSucceeded), "Leader.Succeeded published");
            T.Check(org.Get<ContractorSimulation>().morale.lastShockTick == n.clock.Now, "succession shocks morale");
            T.Check(n.ledger.records.Exists(r => r.typeKey == EventKeys.LeaderSucceeded), "history records the succession");
            // Kill every named person: an abstract Veteran is promoted into a new record.
            while (true)
            {
                CharacterId l = op.leader;
                foreach (CharacterId c in new List<CharacterId>(op.knownMembers))
                {
                    if (c == l) continue;
                    KnownCharacter k = n.ctx.characters.Get(c);
                    k.status = CharacterStatus.Dead;
                }
                int vets = op.TierOf(Tier.Veteran).healthy + op.TierOf(Tier.Regular).healthy + op.TierOf(Tier.Recruit).healthy;
                n.ctx.Contractors.ApplyCasualties(org, Kill(l), ContractId.None, OperationId.None, false);
                if (org.status != ActorStatus.Active) break;
                KnownCharacter promoted = n.ctx.characters.Get(op.leader);
                T.Check(promoted != null && promoted.IsAlive && promoted.role == CharacterRole.Leader, "a promoted member leads");
                T.Eq(vets - 1, op.TierOf(Tier.Veteran).healthy + op.TierOf(Tier.Regular).healthy + op.TierOf(Tier.Recruit).healthy, "promotion takes one from the headcount");
                if (vets > 40) break;
            }
            T.Eq(ActorStatus.Dissolved, org.status, "with nobody left, the organization dissolves");
            T.Eq("NoSuccessor", org.endReasonKey, "reason recorded");
            T.Check(!n.scheduler.Has(ContractorService.UpkeepJob, org.id.Value), "no more upkeep");
            T.Eq(1, n.recorder.Count(EventKeys.ContractorEnded), "Contractor.Ended published once");
        }

        private static void SoloDeath()
        {
            TestNet n = new TestNet();
            NetworkActor solo = Make(n, Template(ContractorForm.Solo, ExperienceBand.Elite, FameBand.Famous));
            n.ctx.Contractors.ApplyCasualties(solo, Kill(solo.bindings.embodies), ContractId.None, OperationId.None, false);
            T.Eq(ActorStatus.Dissolved, solo.status, "a Solo ends when its person dies");
            T.Eq("Died", solo.endReasonKey, "reason Died");
            T.Eq(CharacterStatus.Dead, n.ctx.characters.Get(solo.bindings.embodies).status, "the record keeps the death");
            T.Eq(Availability.Ended, n.ctx.Contractors.AvailabilityOf(solo), "never available again");
        }

        private static void NoPlotArmor()
        {
            TestNet n = new TestNet();
            NetworkActor legend = Make(n, Template(ContractorForm.Solo, ExperienceBand.Legendary, FameBand.Legendary));
            n.ctx.Contractors.ApplyCasualties(legend, Kill(legend.bindings.embodies), ContractId.None, OperationId.None, false);
            T.Eq(ActorStatus.Dissolved, legend.status, "Legendary fame protects nobody");
            NetworkActor famousOrg = Make(n, Template(ContractorForm.Crew, ExperienceBand.Legendary, FameBand.Legendary));
            OrganizationProfile op = famousOrg.Get<OrganizationProfile>();
            n.ctx.Contractors.ApplyCasualties(famousOrg, Kill(op.leader), ContractId.None, OperationId.None, false);
            T.Eq(1, n.recorder.Count(EventKeys.LeaderKilled), "a Legendary organization's leader dies like anyone's");
        }

        private static void Population()
        {
            TestNet n = WorldWithCast(40);
            n.ctx.tuning.targetContractorCount = 60;
            n.ctx.Upkeep.EnsurePopulationJob();
            int before = n.ctx.Upkeep.ActiveContractorCount();
            n.Advance(Ticks.PerDay * 7 * 8);
            int after = n.ctx.Upkeep.ActiveContractorCount();
            T.Check(after > before && after <= 60, "weekly top-up toward the target (" + before + " → " + after + ")");
            NetworkActor newcomer = null;
            foreach (NetworkActor a in Contractors(n)) if (a.provenance.source == ProvenanceSource.WorldGenerated) newcomer = a;
            T.Check(newcomer != null, "newcomers are world-generated");
            T.Check(newcomer.Has<ContractorProfile>() && newcomer.Has<ContractorSimulation>(), "and real persistent contractors");
            T.Check(n.recorder.Count(EventKeys.ContractorCreated) > 0, "Contractor.Created published");
            // Above target: never deletes anyone.
            n.ctx.tuning.targetContractorCount = 10;
            int count = n.ctx.Upkeep.ActiveContractorCount();
            n.Advance(Ticks.PerDay * 14);
            T.Eq(count, n.ctx.Upkeep.ActiveContractorCount(), "never removes actors to hit the target");

            TestNet a2 = WorldWithCast(40);
            a2.ctx.tuning.targetContractorCount = 60;
            a2.ctx.Upkeep.RunPopulation();
            TestNet b2 = WorldWithCast(40);
            b2.ctx.tuning.targetContractorCount = 60;
            b2.ctx.Upkeep.RunPopulation();
            T.Eq(Contractors(a2)[Contractors(a2).Count - 1].name.Display, Contractors(b2)[Contractors(b2).Count - 1].name.Display, "newcomer generation is deterministic");
        }

        private static void MoraleHysteresis()
        {
            ContractorSimulation sim = new ContractorSimulation { funds = 5000 };
            sim.doctrine.caution = 0.4f;
            sim.morale.confidence = 0.75f;
            sim.morale.descriptor = MoraleModel.Evaluate(sim, MoraleDescriptor.Steady);
            T.Eq(MoraleDescriptor.Confident, sim.morale.descriptor, "confident above the entry threshold");
            sim.morale.confidence = 0.66f;
            T.Eq(MoraleDescriptor.Confident, MoraleModel.Evaluate(sim, sim.morale.descriptor), "kept while above the exit threshold");
            T.Eq(MoraleDescriptor.Steady, MoraleModel.Evaluate(sim, MoraleDescriptor.Steady), "but not entered from Steady at the same value");
            sim.morale.confidence = 0.25f;
            T.Eq(MoraleDescriptor.Shaken, MoraleModel.Evaluate(sim, MoraleDescriptor.Steady), "shaken after a shock");
            sim.morale.confidence = 0.35f;
            T.Eq(MoraleDescriptor.Shaken, MoraleModel.Evaluate(sim, MoraleDescriptor.Shaken), "stays shaken until clearly recovered");
            sim.morale.fatigue = 0.8f;
            T.Eq(MoraleDescriptor.Exhausted, MoraleModel.Evaluate(sim, MoraleDescriptor.Shaken), "exhaustion takes precedence");
            sim.morale.fatigue = 0.1f;
            sim.funds = 50;
            T.Eq(MoraleDescriptor.Desperate, MoraleModel.Evaluate(sim, MoraleDescriptor.Steady), "desperate when funds run out");
        }
    }
}
