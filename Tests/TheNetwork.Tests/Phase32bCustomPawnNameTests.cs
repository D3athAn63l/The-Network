using System;
using System.Collections.Generic;
using TheNetwork.Diagnostics.RuntimeTests;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Tests
{
    public static class Phase32bCustomPawnNameTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Name_CustomDisplaySupportsMandatorySamePawnPromotion", CustomDisplay));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Name_CustomBlankProvidesNoNameEvidence", BlankDisplay));
        }

        /// <summary>A legitimate Name subtype with display truth and no supported first/nick/last structure.</summary>
        private sealed class CustomName : Name
        {
            private readonly string display;
            public CustomName(string display) { this.display = display; }
            public override string ToStringFull => display;
            public override string ToStringShort => display;
            public override bool IsValid => !string.IsNullOrWhiteSpace(display);
            public override bool Numerical => false;
            public override bool ConfusinglySimilarTo(Name other) { return false; }
            public override void ExposeData() { }
        }

        private sealed class Fixture
        {
            public readonly TestNet net = new TestNet();
            public readonly NetworkActor actor;
            public readonly OrganizationProfile org;
            public readonly PhysicalEpisode episode;
            public readonly EpisodeMember member;
            public readonly Pawn pawn;
            public readonly Name actualName;
            public readonly RimWorldPhysicalWorldPort port;

            public Fixture(string display)
            {
                actor = new NetworkActor { id = new ActorId(net.ids.NextId()), kind = ActorKind.Organization,
                    name = NameSnapshot.Org("Custom-name crew"), seed = 123, foundedTick = net.clock.Now };
                KnownCharacter leader = new KnownCharacter { id = new CharacterId(net.ids.NextId()), org = actor.id,
                    role = CharacterRole.Leader, opRole = OperationalRole.Leader };
                org = new OrganizationProfile { capacity = 7, leader = leader.id };
                org.knownMembers.Add(leader.id);
                org.TierOf(Tier.Regular, true).healthy = 1;
                actor.Add(org); actor.Add(new ContractorProfile()); actor.Add(new ContractorSimulation());
                net.ctx.actors.Add(actor); net.ctx.characters.Add(leader);
                actualName = new CustomName(display);
                pawn = new Pawn { thingIDNumber = 98001, Name = actualName };
                episode = new PhysicalEpisode { id = new EpisodeId(net.ids.NextId()), actor = actor.id,
                    state = EpisodeState.Open, purposeKey = "GroupVisit", createdTick = net.clock.Now - 10 };
                member = new EpisodeMember { slot = 0, tier = Tier.Regular, seatRole = OperationalRole.Rifleman,
                    state = MemberState.Present, pawn = new PawnRef { pawn = pawn, thingIdNumber = pawn.thingIDNumber,
                        defName = "Human", boundTick = episode.createdTick, agedThroughTick = episode.createdTick } };
                episode.members.Add(member); net.ctx.episodes.Add(episode);
                port = new RimWorldPhysicalWorldPort(net.ctx);
            }

            public LiveFingerprint Print()
            { return LiveFingerprint.Of(net.ctx, net.ids, net.scheduler, net.journal); }

            public PhysicalPromotionFacts ReadFacts()
            {
                Game previous = Current.Game;
                try
                {
                    // Optional history is absent; the actual production adapter must preserve the name independently of it.
                    Current.Game = null;
                    return port.ReadPromotionFacts(episode, member);
                }
                finally { Current.Game = previous; }
            }

            public MemberDecision Capture(PhysicalPromotionFacts facts)
            {
                PhysicalObservation observed = new PhysicalObservation { kind = ObservedKind.HeldByPlayer,
                    holder = HeldKind.PlayerPrisoner, health = 1f };
                bool unsupported, captive;
                HeldKind heldBy;
                MemberOutcome outcome = ReconciliationPlanner.Decide(observed, false, true, out unsupported, out heldBy, out captive);
                T.Check(!unsupported && captive && outcome == MemberOutcome.HeldByPlayer, "positive S1 custody requires identity independently of optional flags");
                return new MemberDecision { member = member, observation = observed, outcome = outcome,
                    holder = heldBy, captive = captive, promotionFacts = facts };
            }
        }

        private static void DisplayOnly(string expected, NameSnapshot name)
        {
            T.Check(name != null, "actual nonblank custom Name supplies name facts");
            if (name == null) return;
            T.Eq(expected, name.display, "custom display is preserved exactly, including punctuation and whitespace");
            T.Eq(expected, name.Display, "rendered snapshot uses the exact existing display");
            T.Check(name.first == null && name.nick == null && name.last == null, "unsupported name structure is never fabricated");
        }

        private static void CustomDisplay()
        {
            const string display = "  Dr. «Nox / 7»\nOf clan “The Bond”  ";
            Fixture f = new Fixture(display);
            LiveFingerprint before = f.Print();
            PawnRef binding = f.member.pawn;
            int characters = f.net.ctx.characters.Count, next = f.net.ids.PeekNextId;
            T.Check(!(f.actualName is NameTriple) && !(f.actualName is NameSingle) && f.actualName.IsValid,
                "the regression uses a valid actual Verse.Name subtype outside Triple/Single");
            PhysicalPromotionFacts facts = f.ReadFacts();
            T.Check(facts != null, "actual production ReadPromotionFacts accepts the exact member binding");
            DisplayOnly(f.pawn.Name.ToStringFull, facts?.name);
            T.Eq(ConcretizationEvidence.None, facts.evidence, "optional unavailable history contributes no evidence");
            T.Check(ReferenceEquals(f.actualName, f.pawn.Name) && ReferenceEquals(binding, f.member.pawn)
                && ReferenceEquals(f.pawn, binding.pawn), "evidence collection leaves actual Name, Pawn and bound PawnRef unchanged");
            T.Eq(characters, f.net.ctx.characters.Count, "evidence collection creates no CharacterStore record");
            T.Eq(next, f.net.ids.PeekNextId, "evidence collection allocates no identity");
            T.Check(before.Diff(f.Print()).Count == 0, "evidence collection changes no durable Network state");

            MemberDecision capture = f.Capture(facts);
            ReconciliationPlan plan = ReconciliationPlanner.PlanEpisode(f.net.ctx, f.episode,
                new List<MemberDecision> { capture }, ReconciliationPlanner.CloseReconciled);
            ReconciliationPlanner.Validate(f.net.ctx, plan);
            T.Eq(1, plan.promotedCharacters.Count, "mandatory S1 can plan a custom-display identity without P0 or optional evidence");
            T.Check(!f.member.IsNamed && before.Diff(f.Print()).Count == 0, "PLAN and VALIDATE remain pure");
            CommitTarget target = new CommitTarget { now = f.net.clock.Now, ids = f.net.ids,
                characters = f.net.ctx.characters, outbox = f.episode.publications };
            ReconciliationApplier.Commit(plan, target, -1);
            KnownCharacter promoted = f.net.ctx.characters.Get(f.member.character);
            T.Check(promoted != null && f.episode.consequencesApplied, "guarded atomic commit installs the required identity");
            DisplayOnly(display, promoted?.name);
            T.Check(promoted != null && ReferenceEquals(f.pawn, promoted.pawn?.pawn) && ReferenceEquals(binding, f.member.pawn)
                && ReferenceEquals(f.actualName, f.pawn.Name), "promotion keeps the same Pawn and actual Name without replacing the member binding");
            T.Check(promoted != null && promoted.org == f.actor.id && promoted.opRole == f.member.seatRole,
                "custom-name promotion preserves organizational and operational provenance");
            T.Check(promoted != null && promoted.status == CharacterStatus.Captured && promoted.custody == CustodyState.OutOfCustody
                && promoted.heldBy == HeldKind.PlayerPrisoner && !AuthorityGate.CanSimulateAbstractly(promoted), "mandatory capture uses existing held custody semantics");
            T.Eq(characters + 1, f.net.ctx.characters.Count, "only atomic commit creates the new record");
            T.Eq(next + 1, f.net.ids.PeekNextId, "only atomic commit allocates the identity");
            T.Eq(0, f.org.Committed + f.org.Healthy, "promotion consumes the committed anonymous copy once without restoring a duplicate");
            T.Eq(2, ContractorService.Headcount(f.actor, f.net.ctx.characters), "one original leader plus one promoted held human");
        }

        private static void BlankDisplay()
        {
            foreach (string display in new[] { null, "", " \t\n" })
            {
                Fixture f = new Fixture(display); LiveFingerprint before = f.Print(); PawnRef binding = f.member.pawn;
                PhysicalPromotionFacts facts = f.ReadFacts();
                T.Check(facts != null && facts.name == null, "null or whitespace custom display supplies no name evidence");
                T.Check(ReferenceEquals(f.actualName, f.pawn.Name) && ReferenceEquals(binding, f.member.pawn), "blank custom Name and binding remain untouched");
                MemberDecision capture = f.Capture(facts);
                try
                {
                    ReconciliationPlanner.PlanEpisode(f.net.ctx, f.episode, new List<MemberDecision> { capture }, ReconciliationPlanner.CloseReconciled);
                    T.Check(false, "mandatory identity still refuses genuinely unknown actual name");
                }
                catch (PlanInvalidException ex) { T.Eq("PromotionNameUnknown", ex.reasonKey, "blank name remains fail-closed"); }
                T.Check(before.Diff(f.Print()).Count == 0 && !f.member.IsNamed, "failed blank-name plan creates no identity or durable mutation");
                T.Eq(1, f.org.Committed, "unknown name leaves the original anonymous ownership protected");
            }
        }
    }
}
