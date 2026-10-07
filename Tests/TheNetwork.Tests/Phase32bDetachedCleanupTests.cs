using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using TheNetwork.Diagnostics.RuntimeTests;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>Real Pawn/PawnRef and M1 callbacks with the existing fake world for settlement and release faults; no map/world-tick claim.</summary>
    public static class Phase32bDetachedCleanupTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Detached_ReferenceForgottenOnlyAfterReleaseComplete", ReleaseBoundary));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Detached_NamedAndLegacyNonOperationalReferencesRemain", Exclusions));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Detached_ValidatorReportsStaleHistoryWithoutRepair", Validator));
        }

        private static TV Shell<TV>() { return (TV)FormatterServices.GetUninitializedObject(typeof(TV)); }

        private sealed class RegistryBridgePort : IPhysicalWorldPort, IGroupPhysicalWorldPort
        {
            private readonly DomainContext ctx;
            public readonly FakePhysicalWorldPort fake;
            public readonly RimWorldPhysicalWorldPort real;
            public bool boundDuringStrip = true;
            public bool reservedDuringStrip = true;
            public bool releaseWasPendingDuringStrip = true;

            public RegistryBridgePort(DomainContext ctx, FakePhysicalWorldPort fake)
            {
                this.ctx = ctx;
                this.fake = fake;
                real = new RimWorldPhysicalWorldPort(ctx);
                real.Registry.ResolvePointers();
            }

            public bool Available => fake.Available;
            public string Name => "Detached regression: fake world, actual Pawn and registry callbacks";

            public PawnRef Create(ProjectionRequest request)
            {
                PawnRef binding = fake.Create(request);
                ThingDef def = Shell<ThingDef>();
                def.defName = binding.defName;
                Pawn p = new Pawn { def = def, thingIDNumber = binding.thingIdNumber, Name = new NameSingle("Actual fixture Pawn") };
                p.health = Shell<Pawn_HealthTracker>();
                FieldInfo state = typeof(Pawn_HealthTracker).GetField("healthState", BindingFlags.Instance | BindingFlags.NonPublic);
                state.SetValue(p.health, Enum.Parse(state.FieldType, "Mobile"));
                binding.pawn = p;
                return binding;
            }

            public bool Resolves(PawnRef p) { return fake.Resolves(p); }
            public void CatchUpAge(PawnRef p, long ticks) { fake.CatchUpAge(p, ticks); }
            public FactionRef EnsureEncounterFaction(EpisodeId e, ActorId a, FactionRef f, int goodwill) { return fake.EnsureEncounterFaction(e, a, f, goodwill); }
            public void ReleaseEncounterFaction(FactionRef f) { fake.ReleaseEncounterFaction(f); }
            public bool Place(PawnRef p, EpisodeId e, TileRef tile, int map, FactionRef f)
            {
                bool placed = fake.Place(p, e, tile, map, f);
                if (placed) PhysicalTags.Add(p.pawn, PhysicalTags.Episode(e));
                return placed;
            }
            public PhysicalObservation Observe(PawnRef p, EpisodeId e) { return fake.Observe(p, e); }
            public void Normalize(PawnRef p) { fake.Normalize(p); }
            public void EnsureRetained(PawnRef p) { fake.EnsureRetained(p); }
            public PassToWorldCheck CheckPassToWorld(PawnRef p) { return fake.CheckPassToWorld(p); }
            public void PassToWorld(PawnRef p) { fake.PassToWorld(p); }
            public void StripEpisodeTag(PawnRef p, EpisodeId id)
            {
                PhysicalEpisode e = ctx.episodes.Get(id);
                boundDuringStrip &= e.members.Any(m => ReferenceEquals(m.pawn, p));
                reservedDuringStrip &= real.Registry.Reserves(p.pawn);
                releaseWasPendingDuringStrip &= !e.releaseApplied;
                fake.StripEpisodeTag(p, id);
                real.StripEpisodeTag(p, id);
            }
            public void EpisodeBindingChanged(PhysicalEpisode e, EpisodeMember m)
            {
                fake.EpisodeBindingChanged(e, m);
                real.EpisodeBindingChanged(e, m);
            }
            public bool IsPlayerVisiblePlacement(PhysicalEpisode e, EpisodeMember m) { return false; }
            public void EpisodeReleased(PhysicalEpisode e)
            {
                fake.EpisodeReleased(e);
                real.EpisodeReleased(e);
            }
        }

        private sealed class Fixture
        {
            public readonly TestNet net = new TestNet(320013);
            public readonly NetworkActor actor;
            public readonly OrganizationProfile org;
            public readonly KnownCharacter leader;
            public readonly RegistryBridgePort port;

            public Fixture()
            {
                actor = new NetworkActor { id = new ActorId(net.ids.NextId()), kind = ActorKind.Organization, seed = 123,
                    foundedTick = net.clock.Now, name = NameSnapshot.Org("Detached cleanup crew") };
                actor.Add(new ContractorProfile { specialties = new List<string> { "escort", "medical" } });
                actor.Add(new ContractorSimulation { skill = 0.7f });
                org = new OrganizationProfile { capacity = 7 };
                org.tiers.Add(new TierCount(Tier.Regular, 4));
                leader = new KnownCharacter { id = new CharacterId(net.ids.NextId()), org = actor.id, role = CharacterRole.Leader,
                    opRole = OperationalRole.Leader, createdTick = actor.foundedTick, name = NameSnapshot.Person("Existing", null, "Leader") };
                org.leader = leader.id;
                org.knownMembers.Add(leader.id);
                actor.Add(org);
                net.ctx.actors.Add(actor);
                net.ctx.characters.Add(leader);
                port = new RegistryBridgePort(net.ctx, net.physical);
                net.ctx.physicalPort = port;
            }

            public PhysicalEpisode Materialize(OperationalRole role = OperationalRole.Rifleman)
            {
                PhysicalEpisode e;
                CommandResult result = net.ctx.Lifecycle.PlanGroup(new EpisodeRequest
                {
                    actor = actor.id, purposeKey = "DetachedCleanupRegression", where = new TileRef { tileId = 17 }, mapId = 71,
                    cause = new EpisodeCause { devKey = "DetachedCleanupFixture" }
                }, new[] { new RoleCapacity(role, 1) }, null, out e);
                T.Check(result.ok && e != null, "one role-correct group slot planned: " + result);
                T.Eq(1, net.ctx.Lifecycle.Materialize(e), "the fake world reports one placed real-bound slot");
                return e;
            }
        }

        private static void ReleaseBoundary()
        {
            foreach (string fault in new string[] { null, "strip", "faction-release" })
            {
                Fixture f = new Fixture();
                PhysicalEpisode e = f.Materialize();
                EpisodeMember m = e.members[0];
                PawnRef binding = m.pawn;
                Pawn p = binding.pawn;
                Name actualName = p.Name;
                PhysicalTags.Add(p, "Fixture.Vanilla.Route");
                int people = f.net.ctx.characters.Count, nextId = f.net.ids.PeekNextId;
                T.Check(!m.IsNamed && OrganizationCompositionV1.IsRole(m.seatRole), "the bound slot has no persistent identity");
                T.Check(f.port.real.Registry.Reserves(p) && f.port.real.Registry.EpisodeIndexCount == 1, "actual M1 covers the live anonymous binding before settlement");
                T.Check(f.org.Healthy == 3 && f.org.Committed == 1, "planning checked out exactly one anonymous human");
                if (fault != null) f.net.physical.ThrowOn(fault);

                T.Eq(1, f.net.ctx.Lifecycle.SettleForRemoval(), "prepare-for-removal settles the observed Pending slot");
                T.Eq(MemberOutcome.Detached, m.outcome, "no stronger terminal truth is invented");
                T.Check(e.consequencesApplied && m.state == MemberState.Done, "the same atomic commit settled the slot");
                T.Check(f.org.Healthy == 4 && f.org.Committed == 0 && f.org.Wounded == 0, "AnonymousBack restores exactly the one checked-out human");
                T.Eq(people, f.net.ctx.characters.Count, "detaching creates no KnownCharacter");
                T.Eq(nextId, f.net.ids.PeekNextId, "detaching allocates no person identity");
                if (fault != null)
                {
                    T.Check(!e.releaseApplied && ReferenceEquals(m.pawn, binding), "a failed member or final episode action preserves PawnRef until COMPLETE: " + fault);
                    T.Check(f.port.real.Registry.Reserves(p), "actual temporary reservation persists while RELEASE is pending");
                    T.Eq(fault == "strip" ? (byte)0 : (byte)1, m.releaseStep, "only normally returned actions advance the release cursor");
                }
                else T.Check(e.releaseApplied && m.pawn == null, "successful removal settlement already ran authoritative RELEASE COMPLETE");

                T.Check(f.net.ctx.Lifecycle.FinishPending(e), "normal retry completes all remaining stages");
                T.Check(e.releaseApplied && e.IsComplete && m.pawn == null && !m.IsBound && !m.IsNamed, "completed anonymous Detached history has no PawnRef or identity");
                T.Check(f.port.boundDuringStrip && f.port.reservedDuringStrip && f.port.releaseWasPendingDuringStrip, "routing actions receive the original bound, reserved Pawn before COMPLETE");
                T.Eq(1, f.net.physical.strips, "one successful routing-only strip action");
                T.Check(f.net.physical.passCalls == 0 && f.net.physical.normalizes == 0 && f.net.physical.retains == 0, "Detached performs no insertion, normalization or named-retention action");
                T.Check(!p.Destroyed && !p.Discarded && !p.Dead && ReferenceEquals(binding.pawn, p) && ReferenceEquals(p.Name, actualName), "the actual Pawn, its name and the original external PawnRef remain untouched");
                T.Check(!PhysicalTags.Has(p, PhysicalTags.Episode(e.id)) && PhysicalTags.Has(p, "Fixture.Vanilla.Route"), "only Network Episode routing is stripped");
                T.Check(!f.port.real.Registry.Reserves(p) && f.port.real.Registry.EpisodeIndexCount == 0, "completed release drops actual temporary index ownership");
                T.Eq(0, f.port.real.Registry.RebuildEarly(), "completed history supplies no stale thing-id bridge");
                T.Check(!f.port.real.Registry.Reserves(p), "the early bridge cannot reconstruct the forgotten anonymous Pawn");
                f.port.real.Registry.ResolvePointers();
                f.port.real.Registry.inert = true;
                f.port.real.Registry.Resume();
                T.Check(!f.port.real.Registry.Reserves(p) && f.port.real.Registry.EpisodeIndexCount == 0, "Resume/rebuild cannot resurrect temporary ownership");
                int commits = f.net.ctx.Lifecycle.counters.commits, strips = f.net.physical.strips;
                T.Eq(0, f.net.ctx.Lifecycle.SettleForRemoval(), "repeat removal settles no second batch");
                T.Check(f.net.ctx.Lifecycle.FinishPending(e), "repeat completion is harmless");
                T.Check(f.org.Healthy == 4 && f.org.Committed == 0 && f.net.ctx.Lifecycle.counters.commits == commits
                    && f.net.physical.strips == strips && f.net.physical.creates == 1, "stock, commit, release and creation occur exactly once");
            }
        }

        private static void Exclusions()
        {
            foreach (bool named in new[] { true, false })
            {
                Fixture f = new Fixture();
                PhysicalEpisode e = f.Materialize(named ? OperationalRole.Leader : OperationalRole.Rifleman);
                EpisodeMember m = e.members[0];
                PawnRef binding = m.pawn;
                if (!named) m.seatRole = OperationalRole.Unset; // legacy non-operational anonymous history intentionally retains its reference
                T.Eq(1, f.net.ctx.Lifecycle.SettleForRemoval(), "excluded member settles as Detached");
                T.Check(f.net.ctx.Lifecycle.FinishPending(e), "excluded release completes");
                T.Check(m.outcome == MemberOutcome.Detached && ReferenceEquals(m.pawn, binding), "named or legacy non-operational historical binding is preserved");
                if (named) T.Check(m.IsNamed && f.leader.pawn.SameBinding(binding) && f.port.real.Registry.Reserves(binding.pawn), "continuing named custody keeps its same lifetime Pawn and M1 retention");
                List<string> findings = new List<string>();
                EpisodeChecks.Report(f.net.ctx, findings);
                T.Check(!findings.Any(s => s.Contains("released anonymous operational member")), "the new validator finding excludes legitimate named and non-operational history");
            }
        }

        private static void Validator()
        {
            Fixture f = new Fixture();
            PhysicalEpisode e = f.Materialize();
            EpisodeMember m = e.members[0];
            PawnRef original = m.pawn;
            f.net.ctx.Lifecycle.SettleForRemoval();
            f.net.ctx.Lifecycle.FinishPending(e);
            foreach (MemberOutcome outcome in new[] { MemberOutcome.Returned, MemberOutcome.Killed, MemberOutcome.Lost, MemberOutcome.NeverPlaced, MemberOutcome.Detached })
            {
                m.outcome = outcome;
                m.pawn = original; // malformed completed save: report only, never clear it as a side effect of validation
                m.releaseStep = (byte)ReleasePolicy.ActionsFor(m).Length;
                List<string> findings = new List<string>();
                EpisodeChecks.Report(f.net.ctx, findings);
                T.Check(findings.Count(s => s.Contains("released anonymous operational member")) == 1, "stale released operational reference is reported: " + outcome);
                T.Check(ReferenceEquals(m.pawn, original) && !original.pawn.Destroyed && !original.pawn.Discarded
                    && f.org.Healthy == 4 && f.org.Committed == 0 && f.net.ctx.characters.Count == 1, "validator changes no binding, Pawn, stock or identity");
            }
            foreach (int exclusion in new[] { 0, 1, 2, 3 })
            {
                m.outcome = exclusion == 3 ? MemberOutcome.Missing : MemberOutcome.Detached;
                m.character = exclusion == 0 ? f.leader.id : CharacterId.None;
                m.seatRole = exclusion == 1 ? OperationalRole.Unset : OperationalRole.Rifleman;
                e.releaseApplied = exclusion != 2;
                List<string> findings = new List<string>();
                EpisodeChecks.Report(f.net.ctx, findings);
                T.Check(!findings.Any(s => s.Contains("released anonymous operational member")), "named, non-operational, release-pending or unlisted continuing outcome is excluded: " + exclusion);
                T.Check(ReferenceEquals(m.pawn, original), "excluded reference is never repaired by validation");
            }
        }
    }
}
