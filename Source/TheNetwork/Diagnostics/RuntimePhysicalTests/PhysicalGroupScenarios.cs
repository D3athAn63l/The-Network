using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;
using Verse.AI.Group;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    /// <summary>Owned, deliberate live fixtures. No anonymous roster or persisted runner state.</summary>
    public static class GroupFixtures
    {
        public static NetworkActor Create(DomainContext ctx, string runId, int living, bool company)
        {
            if (ctx == null || living < 2 || (!company && living > 6)) throw new ArgumentException("invalid owned group fixture");
            ActorId id = new ActorId(ctx.ids.NextId());
            NetworkActor a = new NetworkActor { id = id, kind = ActorKind.Organization, seed = NetHash.Combine(ctx.networkSeed, id.Value),
                foundedTick = ctx.Now, name = NameSnapshot.Org("PHYX group " + runId + " " + id.Value),
                provenance = new Provenance { source = ProvenanceSource.Content, templateId = PhysicalTestIds.RunTag(runId), importedTick = ctx.Now } };
            ContractorProfile profile = ContractorService.CreateOriginProfile(new[] { "escort", "medical" }, ctx.Now);
            a.Add(profile);
            a.Add(new ContractorSimulation { skill = 0.3f });
            KnownCharacter leader = new KnownCharacter { id = new CharacterId(ctx.ids.NextId()), org = id,
                name = NameSnapshot.Person("PHYX", "Leader", "Fixture" + id.Value), role = CharacterRole.Leader,
                opRole = OperationalRole.Leader, createdTick = ctx.Now, statusTick = ctx.Now, notability = 0.5f };
            OrganizationProfile org = new OrganizationProfile { capacity = company ? 32 : 7, leader = leader.id };
            org.knownMembers.Add(leader.id);
            org.tiers.Add(new TierCount(Tier.Regular, living - 1));
            a.Add(org);
            ctx.actors.Add(a);
            ctx.characters.Add(leader);
            StateVersion.Bump();
            return a;
        }

        public static List<RoleCapacity> SmallVisit(bool all)
        {
            List<RoleCapacity> roles = new List<RoleCapacity> { new RoleCapacity(OperationalRole.Leader, 1),
                new RoleCapacity(OperationalRole.Medic, 1), new RoleCapacity(OperationalRole.Rifleman, all ? 2 : 1) };
            if (all) roles.Add(new RoleCapacity(OperationalRole.Heavy, 1));
            return roles;
        }
    }

    /// <summary>Production group Plan/Materialize plus explicit synthetic P0 on the owned TestSite only.</summary>
    public abstract class GroupRun : PhysicalRun
    {
        private sealed class PlacementBaseline
        {
            public EpisodeMember member;
            public CharacterId character;
            public OperationalRole role;
            public Pawn pawn;
            public int thingId;
            public KnownCharacter known;
            public PawnRef knownBinding;
            public RoleSpec creationSpec;
            public RoleCandidate skills;
            public List<DirectPawnRelation> relations;
            public List<Thought_Memory> memories;
            public List<Trait> traits;
            public object relationTracker, recordTracker, memoryTracker;
            public bool retained;
        }

        protected OrganizationProfile org;
        protected readonly List<Pawn> groupPawns = new List<Pawn>();
        private readonly HashSet<int> namedBeforePlacement = new HashSet<int>();
        private readonly List<PlacementBaseline> placement = new List<PlacementBaseline>();
        private int dwellStartTick = -1, dwellCharacters, dwellProjections, dwellCreated;
        private PhysicalEpisode dwellEpisode;
        private Map dwellMap;
        private Faction dwellFaction;
        private Lord dwellLord;
        private bool dwellActive, dwellInvalid;
        protected int humanBefore, charactersBefore, commitsBefore;
        protected int freeFrames, reservationGaps;

        protected GroupRun(string family, NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get(family), runId, rt) { }

        protected StepResult CreateFixture(int living, bool company)
        {
            a = GroupFixtures.Create(ctx, runId, living, company);
            org = a.Get<OrganizationProfile>();
            c = ctx.characters.Get(org.leader);
            testPeople.Add(c.id.Value);
            humanBefore = ContractorService.Headcount(a, ctx.characters);
            v.Check(org.knownMembers.Count == 1 && humanBefore == living, "owned fixture: one known leader and " + (living - 1) + " abstract people, no anonymous roster");
            OrganizationSeats seats;
            string why;
            bool apportioned = OrganizationSeatPolicy.TryApportion(a, ctx.characters.characters, out seats, out why);
            v.Check(apportioned, "Composition v1 apportions the fixture (" + why + ")");
            if (!apportioned) return StepResult.Abort;
            if (living == 5)
            {
                Dictionary<OperationalRole, int> counts = new Dictionary<OperationalRole, int>();
                foreach (OrganizationRoleSeats seat in seats.roles) counts[seat.role] = seat.pinned + seat.anonymous;
                v.Check(counts.ContainsKey(OperationalRole.Leader) && counts[OperationalRole.Leader] == 1
                    && counts.ContainsKey(OperationalRole.Rifleman) && counts[OperationalRole.Rifleman] == 2
                    && counts.ContainsKey(OperationalRole.Medic) && counts[OperationalRole.Medic] == 1
                    && counts.ContainsKey(OperationalRole.Heavy) && counts[OperationalRole.Heavy] == 1,
                    "five living seats: Leader 1, Rifleman 2, Medic 1, Heavy 1");
            }
            v.Note("This run deliberately creates an owned organization; its real identities remain in this disposable save.");
            return StepResult.Next;
        }

        protected StepResult PlaceGroup(IList<RoleCapacity> required, bool synthetic)
        {
            string report;
            Map map = TestSite.Ensure(out report);
            if (map == null) { v.Fail(report); return StepResult.Abort; }
            charactersBefore = ctx.characters.characters.Count;
            commitsBefore = lc.counters.commits;
            PhysicalEpisode planned;
            EpisodeRequest request = new EpisodeRequest { actor = a.id, purposeKey = "Visit", where = TileRef.Of(map.Tile), mapId = map.uniqueID };
            request.cause.devKey = PhysicalTestIds.DevKey(runId, info.id);
            CommandResult result = lc.PlanGroup(request, required, null, out planned);
            if (!result.ok) { v.Fail("production group Plan refused: " + result); return StepResult.Abort; }
            e = planned;
            episodes.Add(e);
            groupPawns.Clear();
            namedBeforePlacement.Clear();
            placement.Clear();
            dwellStartTick = -1;
            dwellActive = dwellInvalid = false;
            foreach (EpisodeMember member in e.members) if (member.IsNamed) namedBeforePlacement.Add(member.character.Value);
            // This is the actual first-projection boundary: Plan has checked out anonymous seats, Materialize has not created anyone.
            ExperienceBand creationBand = ContractorService.Experience(a);
            int projectionsBeforePlacement = port.counters.projections;
            int newCandidates = 0;
            foreach (EpisodeMember member in e.members)
            {
                KnownCharacter known = member.IsNamed ? ctx.characters.Get(member.character) : null;
                Pawn priorPawn = known?.pawn?.pawn;
                bool retained = known?.pawn?.IsBound == true && GroupQaRules.HasRetainedPawn(known.id.Value, known.pawn.thingIdNumber, priorPawn?.thingIDNumber ?? 0);
                if (known?.pawn?.IsBound == true && !retained)
                { v.Fail("pre-placement retained binding does not resolve exactly for " + member.character); return StepResult.Abort; }
                PlacementBaseline baseline = new PlacementBaseline { member = member, character = member.character, role = member.seatRole,
                    known = known, knownBinding = known?.pawn, pawn = retained ? priorPawn : null, thingId = retained ? priorPawn.thingIDNumber : 0,
                    retained = retained, creationSpec = retained ? null : RoleRules.SpecFor(member.seatRole, creationBand) };
                if (retained)
                {
                    baseline.skills = PawnRoleReader.Snapshot(priorPawn);
                    baseline.relations = CopyReferences(priorPawn.relations?.DirectRelations);
                    baseline.memories = CopyReferences(priorPawn.needs?.mood?.thoughts?.memories?.Memories);
                    baseline.traits = CopyReferences(priorPawn.story?.traits?.allTraits);
                    baseline.relationTracker = priorPawn.relations;
                    baseline.recordTracker = priorPawn.records;
                    baseline.memoryTracker = priorPawn.needs?.mood?.thoughts?.memories;
                }
                else newCandidates++;
                placement.Add(baseline);
            }
            int placed;
            if (synthetic)
            {
                v.Note("SYNTHETIC P0: the owned dev TestSite is not naturally player-visible. This scoped injection tests promotion policy; genuine owner-home-map visibility remains separately gated and pending.");
                using (port.OverrideTestVisibility(e.id, map.uniqueID)) placed = lc.Materialize(e);
            }
            else placed = lc.Materialize(e);
            if (e.faction != null && e.faction.loadId >= 0) runFactions.Add(e.faction.loadId);
            bool live = placed == e.members.Count && placed > 0 && placed <= PhysicalLifecycleService.MaxMembers && e.state == EpisodeState.Open;
            v.Check(live, "production materialization placed the bounded whole group: " + placed + "/" + e.members.Count + ", " + e.state);
            if (!live) return StepResult.Abort;
            Faction shared = e.faction.Resolve();
            bool placementValid = v.Check(shared != null && shared.temporary && shared.def.hidden, "one valid hidden temporary encounter faction for the group");
            bool expectedProjections = port.counters.projections == projectionsBeforePlacement + newCandidates;
            placementValid &= v.Check(expectedProjections, "only the " + newCandidates + " previously unbound candidates were projected; retained people were not regenerated");
            foreach (PlacementBaseline baseline in placement)
            {
                EpisodeMember member = baseline.member;
                Pawn pawn = member.pawn?.pawn;
                groupPawns.Add(pawn);
                KnownCharacter known = member.IsNamed ? ctx.characters.Get(member.character) : null;
                if (known != null) testPeople.Add(known.id.Value);
                placementValid &= v.Check(pawn != null && pawn.Spawned && pawn.Map == map && pawn.Faction == shared, "seat " + member.slot + ": same shared faction and successful placement on owned map");
                placementValid &= v.Check(pawn != null && port.Registry.Reserves(pawn), "seat " + member.slot + ": protected before and during placement");
                placementValid &= v.Check(known == null || known.opRole == member.seatRole, "seat " + member.slot + ": persisted operational role matches mission role");
                if (baseline.retained)
                {
                    GroupRetainedFacts facts = new GroupRetainedFacts(member.IsNamed, member.character == baseline.character && ReferenceEquals(known, baseline.known),
                        ReferenceEquals(pawn, baseline.pawn), ReferenceEquals(known?.pawn, baseline.knownBinding)
                            && known?.pawn?.thingIdNumber == baseline.thingId && member.pawn?.thingIdNumber == baseline.thingId,
                        member.seatRole == baseline.role && known?.opRole == baseline.role, pawn != null && port.Registry.Reserves(pawn)
                            && port.Registry.CharacterOf(pawn) == baseline.character,
                        expectedProjections, SkillsUncorrected(baseline.skills, pawn), HistoryPreserved(baseline, pawn));
                    placementValid &= v.Check(GroupQaRules.RetainedPlacementHolds(facts), "seat " + member.slot
                        + ": retained CharacterId, same Pawn/binding and durable " + member.seatRole + "; no replacement, skill correction or social-history sanitation");
                }
                else
                {
                    placementValid &= v.Check(pawn != null && GroupQaRules.CreationPlacementHolds(baseline.creationSpec, PawnRoleReader.Snapshot(pawn)),
                        "seat " + member.slot + ": first candidate satisfies its captured creation spec " + baseline.creationSpec.Describe());
                    baseline.pawn = pawn;
                    baseline.thingId = pawn?.thingIDNumber ?? 0;
                    baseline.knownBinding = known?.pawn;
                }
                if (!member.IsNamed) v.Check(synthetic ? member.playerVisibleTick >= 0 : member.playerVisibleTick == -1,
                    "seat " + member.slot + ": " + (synthetic ? "synthetic visible placement is latched" : "ordinary dev-map presence is not invented as P0"));
                if (!member.IsNamed) v.Check(port.Registry.IsTemporaryReserved(pawn), "anonymous seat " + member.slot + ": Episode temporary reservation, no dummy person");
            }
            p = groupPawns.Count > 0 ? groupPawns[0] : null;
            placementValid &= v.Check(ctx.characters.characters.Count == charactersBefore, "Plan and placement create zero KnownCharacters; promotion waits for terminal commit");
            placementValid &= v.Check(ContractorService.Headcount(a, ctx.characters) == humanBefore, "checkout conserves total living humans");
            everyFrame = ObserveProtection;
            return placementValid ? StepResult.Next : StepResult.Abort;
        }

        private static List<T> CopyReferences<T>(IList<T> values)
        {
            return values == null ? null : new List<T>(values);
        }

        private static bool ReferencesRemain<T>(IList<T> before, IList<T> after)
        {
            if (before == null) return after == null;
            if (after == null) return false;
            foreach (T value in before) if (!after.Contains(value)) return false;
            return true;
        }

        private static bool SkillsUncorrected(RoleCandidate before, Pawn pawn)
        {
            if (before == null || pawn == null) return false;
            RoleCandidate after = PawnRoleReader.Snapshot(pawn);
            if (before.skills.Count != after.skills.Count) return false;
            foreach (KeyValuePair<string, SkillFacts> skill in before.skills)
            {
                SkillFacts current = after.Skill(skill.Key);
                if (current == null || current.levelBase != skill.Value.levelBase || current.passion != skill.Value.passion) return false;
            }
            return true;
        }

        // Compare concrete history immediately across Materialize only. Normal dwell ticks may add memories, social history and skill XP.
        private static bool HistoryPreserved(PlacementBaseline before, Pawn pawn)
        {
            return pawn != null && ReferenceEquals(before.relationTracker, pawn.relations) && ReferenceEquals(before.recordTracker, pawn.records)
                && ReferenceEquals(before.memoryTracker, pawn.needs?.mood?.thoughts?.memories)
                && ReferencesRemain(before.relations, pawn.relations?.DirectRelations)
                && ReferencesRemain(before.memories, pawn.needs?.mood?.thoughts?.memories?.Memories)
                && ReferencesRemain(before.traits, pawn.story?.traits?.allTraits);
        }

        protected void ObserveProtection()
        {
            for (int i = 0; e?.members != null && i < e.members.Count && i < groupPawns.Count; i++)
            {
                Pawn pawn = groupPawns[i];
                if (pawn == null || pawn.Dead || pawn.Discarded) continue;
                if (!e.releaseApplied && !port.Registry.Reserves(pawn)) reservationGaps++;
                if (!e.releaseApplied && IsActualFree(pawn)) freeFrames++;
            }
            if (dwellActive && !dwellInvalid && GroupQaRules.EvaluateDwell(dwellStartTick, PhysLog.Tick, ObserveDwell()) == GroupDwellResult.Invalid)
                FailDwell();
        }

        protected StepResult DwellGroup()
        {
            if (dwellStartTick < 0)
            {
                dwellStartTick = PhysLog.Tick;
                dwellEpisode = e;
                dwellMap = groupPawns.Count > 0 ? groupPawns[0]?.Map : null;
                dwellFaction = e?.faction?.Resolve();
                dwellLord = groupPawns.Count > 0 ? groupPawns[0]?.GetLord() : null;
                dwellCharacters = ctx.characters.characters.Count;
                dwellProjections = port.counters.projections;
                dwellCreated = lc.counters.created;
                dwellActive = true;
                v.Note("LIVE DWELL: wait " + GroupQaRules.MaterializationDwellTicks + " ordinary game ticks on the owned TestSite; normal vanilla movement/jobs/social behavior continues.");
            }
            if (dwellInvalid) return StepResult.Abort;
            GroupDwellResult result = GroupQaRules.EvaluateDwell(dwellStartTick, PhysLog.Tick, ObserveDwell());
            if (result == GroupDwellResult.Invalid) { FailDwell(); return StepResult.Abort; }
            if (result == GroupDwellResult.Wait) return StepResult.Wait;
            dwellActive = false;
            v.Check(true, "live group remained owned, on-map, under the same shared vanilla Lord and continuously reserved for "
                + (PhysLog.Tick - dwellStartTick) + " game ticks; zero replacement projections or new identities");
            return StepResult.Next;
        }

        private GroupDwellFacts ObserveDwell()
        {
            bool exact = e?.members != null && e.members.Count == placement.Count && placement.Count == groupPawns.Count && placement.Count > 0;
            bool bindings = exact, roles = exact, healthy = exact, map = dwellMap != null && TestSite.IsTestMap(dwellMap), faction = dwellFaction != null;
            bool lord = dwellLord?.LordJob is LordJob_VisitColony && dwellLord.Map == dwellMap && dwellLord.faction == dwellFaction
                && dwellLord.ownedPawns.Count == placement.Count;
            bool reserved = exact, notFree = exact;
            for (int i = 0; i < placement.Count; i++)
            {
                PlacementBaseline baseline = placement[i];
                EpisodeMember member = baseline.member;
                Pawn pawn = baseline.pawn;
                KnownCharacter known = baseline.character.IsValid ? ctx.characters.Get(baseline.character) : null;
                exact &= e?.members != null && e.members.Contains(member) && member.slot == i && member.character == baseline.character;
                bindings &= pawn != null && i < groupPawns.Count && ReferenceEquals(groupPawns[i], pawn) && ReferenceEquals(member.pawn?.pawn, pawn)
                    && member.pawn?.thingIdNumber == baseline.thingId && pawn.thingIDNumber == baseline.thingId
                    && (!baseline.character.IsValid || ReferenceEquals(known, baseline.known) && ReferenceEquals(known?.pawn, baseline.knownBinding)
                        && ReferenceEquals(known?.pawn?.pawn, pawn) && known?.pawn?.thingIdNumber == baseline.thingId
                        && e != null && known.episode == e.id && known.custody == CustodyState.Deployed && !AuthorityGate.CanSimulateAbstractly(known));
                roles &= member.seatRole == baseline.role && (!baseline.character.IsValid || known?.opRole == baseline.role);
                healthy &= pawn != null && !pawn.Destroyed && !pawn.Discarded && !pawn.Dead && pawn.Spawned
                    && member.state == MemberState.Present && member.outcome == MemberOutcome.Pending;
                map &= pawn != null && pawn.Map == dwellMap && e?.whereMapId == dwellMap?.uniqueID;
                faction &= pawn != null && pawn.Faction == dwellFaction && pawn.HostFaction == null && !pawn.IsPrisoner && !pawn.IsSlave;
                lord &= pawn != null && dwellLord != null && ReferenceEquals(pawn.GetLord(), dwellLord) && dwellLord.ownedPawns.Contains(pawn);
                reserved &= pawn != null && port.Registry.Reserves(pawn) && (member.IsNamed ? port.Registry.CharacterOf(pawn) == member.character
                    : port.Registry.IsTemporaryReserved(pawn) && !port.Registry.CharacterOf(pawn).IsValid);
                notFree &= pawn != null && !IsActualFree(pawn);
            }
            return new GroupDwellFacts(e != null && ReferenceEquals(e, dwellEpisode) && PhysicalTestSession.IsActiveOwnedEpisode(e)
                    && ReferenceEquals(ctx.episodes.Get(e.id), e), e != null && e.state == EpisodeState.Open && !e.releaseApplied && !e.consequencesApplied,
                exact, bindings, roles, healthy, map, faction, lord, reserved, notFree, ctx.characters.characters.Count == dwellCharacters,
                port.counters.projections == dwellProjections && lc.counters.created == dwellCreated, freeFrames == 0 && reservationGaps == 0);
        }

        private void FailDwell()
        {
            dwellInvalid = true;
            dwellActive = false;
            v.Fail("live materialization dwell lost required ownership/continuity/protection at elapsed tick " + (PhysLog.Tick - dwellStartTick)
                + "; Episode " + e?.id + " state " + e?.state + ", releaseApplied " + e?.releaseApplied + ", active owned " + PhysicalTestSession.IsActiveOwnedEpisode(e)
                + ", identities " + ctx.characters.characters.Count + "/" + dwellCharacters + ", projections " + port.counters.projections + "/" + dwellProjections
                + ", Free frames " + freeFrames + ", reservation gaps " + reservationGaps + ". No Pawn was repaired; everything is preserved.");
            GroupDwellFacts facts = ObserveDwell();
            v.Note("DWELL invariants: owned Episode " + facts.ownedEpisode + ", active/unreleased " + facts.activeEpisode + ", exact members " + facts.exactMembers
                + ", same bindings " + facts.sameBindings + ", same roles " + facts.sameRoles + ", healthy/spawned " + facts.healthySpawned
                + ", expected map " + facts.expectedMap + ", encounter faction " + facts.expectedFaction + ", exact shared Lord " + facts.sharedLord
                + ", reserved " + facts.reserved + ", not Free " + facts.notFree + ", unchanged identities " + facts.unchangedIdentities
                + ", unchanged projections " + facts.unchangedProjections + ", protection unbroken " + facts.protectionUnbroken);
            foreach (PlacementBaseline baseline in placement)
            {
                EpisodeMember member = baseline.member;
                Pawn pawn = member.pawn?.pawn;
                KnownCharacter known = member.IsNamed ? ctx.characters.Get(member.character) : null;
                v.Note("DWELL seat " + member.slot + ", CharacterId " + member.character + ", ThingID " + pawn?.thingIDNumber + "/expected " + baseline.thingId
                    + ", role " + member.seatRole + "/expected " + baseline.role + ", known role " + known?.opRole + ", same Pawn " + ReferenceEquals(pawn, baseline.pawn)
                    + ", binding " + member.pawn?.thingIdNumber + ", spawned " + pawn?.Spawned + ", destroyed " + pawn?.Destroyed + ", discarded " + pawn?.Discarded + ", dead " + pawn?.Dead
                    + ", map " + pawn?.Map?.uniqueID + "/expected " + dwellMap?.uniqueID + ", faction " + pawn?.Faction?.loadID + "/expected " + dwellFaction?.loadID
                    + ", HostFaction " + pawn?.HostFaction?.loadID + ", prisoner " + pawn?.IsPrisoner + ", slave " + pawn?.IsSlave
                    + ", reserved " + (pawn != null && port.Registry.Reserves(pawn)) + ", temporary " + (pawn != null && port.Registry.IsTemporaryReserved(pawn))
                    + ", Free " + (pawn != null && IsActualFree(pawn)) + ", same Lord " + (pawn != null && ReferenceEquals(pawn.GetLord(), dwellLord))
                    + ", Episode " + e?.state + ", releaseApplied " + e?.releaseApplied + ", elapsed " + (PhysLog.Tick - dwellStartTick));
            }
        }

        protected StepResult ExitPeers(Pawn except = null)
        {
            foreach (Pawn pawn in groupPawns)
            {
                if (ReferenceEquals(pawn, except) || pawn == null || !pawn.Spawned || pawn.Dead) continue;
                EpisodeMember owned = e.members.Find(m => ReferenceEquals(m.pawn?.pawn, pawn));
                PhysicalObservation observed = owned == null ? null : port.Observe(owned.pawn, e.id);
                if (!PhysicalTestSession.IsActiveOwnedEpisode(e) || owned == null || !TestSite.IsTestMap(pawn.Map)
                    || pawn.IsPrisoner || pawn.IsSlave || pawn.HostFaction != null || pawn.Faction?.IsPlayer == true
                    || pawn.carryTracker?.CarriedThing != null || observed?.kind != ObservedKind.Spawned)
                { v.Fail("refused to exit a pawn outside this run's ordinary owned visitors"); return StepResult.Abort; }
                pawn.ExitMap(false, Rot4.Invalid);
            }
            return StepResult.Next;
        }

        protected StepResult WaitGroup()
        {
            if (e.state == EpisodeState.Quarantined) { v.Fail("group quarantined: " + e.quarantineKey); return StepResult.Abort; }
            return e.IsComplete ? StepResult.Next : StepResult.Wait;
        }

        protected void CheckGroupReturn(int promoted)
        {
            v.Check(e.IsComplete && lc.counters.commits == commitsBefore + 1, "one terminal commit and complete RELEASE/FOLLOW-UP/PUBLISH");
            v.Check(ctx.characters.characters.Count == charactersBefore + promoted, "exactly " + promoted + " selective new identities, no unrelated people");
            v.Check(ContractorService.Headcount(a, ctx.characters) == humanBefore, "return/promotion preserves the exact living-human count");
            for (int i = 0; i < e.members.Count; i++)
            {
                EpisodeMember member = e.members[i];
                Pawn pawn = groupPawns[i];
                v.Check(member.outcome == MemberOutcome.Returned, "seat " + member.slot + " returned from positive vanilla exit evidence");
                if (member.IsNamed)
                {
                    KnownCharacter known = ctx.characters.Get(member.character);
                    testPeople.Add(member.character.Value);
                    v.Check(known != null && ReferenceEquals(known.pawn?.pawn, pawn) && known.opRole == member.seatRole && known.org == a.id,
                        "seat " + member.slot + ": same Pawn, exact role and organization provenance");
                    v.Check(known != null && known.custody == CustodyState.Stored && !known.episode.IsValid && AuthorityGate.CanSimulateAbstractly(known),
                        "seat " + member.slot + ": Stored, authority resumes only after release");
                    v.Check(pawn != null && port.Registry.Reserves(pawn) && !port.Registry.IsTemporaryReserved(pawn), "seat " + member.slot + ": named retention takes over without a temporary orphan");
                    if (!namedBeforePlacement.Contains(member.character.Value))
                        v.Check(known != null && pawn != null && known.name.Display == pawn.Name.ToStringFull, "seat " + member.slot + ": promoted actual Pawn name preserved");
                }
                else
                {
                    v.Check(!port.Registry.Reserves(pawn) && member.pawn == null, "ordinary anonymous return loses reservation and durable slot PawnRef");
                }
            }
            v.Check(freeFrames == 0 && reservationGaps == 0, "no protection gap before release (Free frames " + freeFrames + ", gaps " + reservationGaps + ")");
            CheckIntegrity();
        }

        protected void CheckIntegrity()
        {
            v.Check(port.Registry.Audit().Count == 0 && port.Registry.AuditTemporary().Count == 0, "zero named/temporary binding integrity findings");
            v.Check(port.Registry.RetainedCount() == port.Registry.DurableRetainedCount(), "every durable retention obligation is covered");
            List<string> findings = new List<string>();
            EpisodeChecks.Report(ctx, findings);
            v.Check(findings.Count == 0, "zero Episode integrity findings" + (findings.Count > 0 ? ": " + findings[0] : ""));
        }
    }

    public sealed class Phyx026SmallFirst : GroupRun
    {
        public Phyx026SmallFirst(NetworkRuntime rt, string runId) : base("RT-PHYX-026", rt, runId) { }
        protected override void Script()
        {
            Then("create the owned five-person crew", () => CreateFixture(5, false));
            Then("place Leader, Medic and Rifleman with explicitly synthetic P0", () => PlaceGroup(GroupFixtures.SmallVisit(false), true));
            Then("the small crew's anonymous seats are P0 eligible", () => { foreach (EpisodeMember m in e.members) if (!m.IsNamed) v.Check(m.p0Eligible, "seat " + m.slot + ": small-crew P0 eligibility latched"); return StepResult.Next; });
            Then("observe the live materialized crew for 240 ordinary game ticks", DwellGroup);
            Then("return every visitor through vanilla ExitMap", () => ExitPeers());
            Then("wait for whole-Episode reconciliation", WaitGroup, 20000);
            Then("check two new people, same Pawns and conserved humans", () => { CheckGroupReturn(2); return StepResult.Next; });
        }
    }

    public sealed class Phyx027SmallSecond : GroupRun
    {
        private readonly Dictionary<int, Pawn> prior = new Dictionary<int, Pawn>();
        private int projectionsBefore;
        public Phyx027SmallSecond(NetworkRuntime rt, string runId) : base("RT-PHYX-027", rt, runId) { }
        protected override void Script()
        {
            Then("find the exact owned first-visit crew", () =>
            {
                PhysicalEpisode first = GroupQaRules.Latest(ctx.episodes.episodes, "RT-PHYX-026");
                a = first == null ? null : ctx.actors.Get(first.actor);
                org = a?.Get<OrganizationProfile>();
                if (first == null || !first.IsComplete || org == null) { v.Gap("run RT-PHYX-026 to completion first"); return StepResult.Abort; }
                humanBefore = ContractorService.Headcount(a, ctx.characters);
                if (humanBefore != 5) { v.Gap("the original crew's live membership changed; preserve it and rerun 026 with a fresh fixture"); return StepResult.Abort; }
                foreach (EpisodeMember m in first.members)
                {
                    KnownCharacter k = ctx.characters.Get(m.character);
                    if (k?.pawn?.pawn == null || !OrganizationSeatPolicy.IsAvailablePin(k)) { v.Gap("the first-visit identity is unavailable: " + m.character); return StepResult.Abort; }
                    prior.Add(k.id.Value, k.pawn.pawn);
                    testPeople.Add(k.id.Value);
                }
                projectionsBefore = port.counters.projections;
                return StepResult.Next;
            });
            Then("place all five suitable seats; named matching roles first", () => PlaceGroup(GroupFixtures.SmallVisit(true), true));
            Then("the Medic and Rifleman are the original Pawns", () =>
            {
                int reused = 0;
                foreach (EpisodeMember m in e.members)
                {
                    Pawn old;
                    if (m.IsNamed && prior.TryGetValue(m.character.Value, out old)) { reused++; v.Check(ReferenceEquals(old, m.pawn?.pawn), m.character + ": same identity and Pawn object, never replaced"); }
                }
                v.Check(reused == prior.Count && port.counters.projections == projectionsBefore + 2, "all three remembered people reused; only the two previously abstract seats generated");
                v.Note("R-50 remains OPEN; this short ordinary return does not validate long-held aging or rescue.");
                return StepResult.Next;
            });
            Then("observe retained and new seats for 240 ordinary game ticks", DwellGroup);
            Then("return the crew through vanilla ExitMap", () => ExitPeers());
            Then("wait for terminal reconciliation", WaitGroup, 20000);
            Then("complete the five-person known crew without extra humans", () => { CheckGroupReturn(2); v.Check(org.knownMembers.Count == 5 && org.Healthy + org.Wounded + org.Committed == 0, "five real known people, zero abstract copies"); return StepResult.Next; });
            Then("record every now-known seat for the full-crew repeat", () =>
            {
                prior.Clear();
                foreach (CharacterId id in org.knownMembers) { KnownCharacter k = ctx.characters.Get(id); prior.Add(id.Value, k.pawn.pawn); }
                projectionsBefore = port.counters.projections;
                return StepResult.Next;
            });
            Then("repeat all five seats with no regenerated replacements", () => PlaceGroup(GroupFixtures.SmallVisit(true), false));
            Then("all five bindings are the same humans", () =>
            {
                foreach (EpisodeMember m in e.members)
                {
                    Pawn old;
                    v.Check(m.IsNamed && prior.TryGetValue(m.character.Value, out old) && ReferenceEquals(old, m.pawn?.pawn), m.character + ": full-crew repeat preserves the exact Pawn object");
                }
                v.Check(port.counters.projections == projectionsBefore, "full-crew repeat generates zero new Pawns");
                return StepResult.Next;
            });
            Then("return the full-crew repeat", () => ExitPeers());
            Then("wait for its exactly-once completion", WaitGroup, 20000);
            Then("zero new identities on the full repeat", () => { CheckGroupReturn(0); return StepResult.Next; });
        }
    }

    public sealed class Phyx028LargePresence : GroupRun
    {
        public Phyx028LargePresence(NetworkRuntime rt, string runId) : base("RT-PHYX-028", rt, runId) { }
        protected override void Script()
        {
            Then("create an owned sixteen-person company", () => CreateFixture(16, true));
            Then("place six anonymous Riflemen with synthetic player visibility", () => PlaceGroup(new[] { new RoleCapacity(OperationalRole.Rifleman, 6) }, true));
            Then("large-company presence supplies no discretionary P0", () => { foreach (EpisodeMember m in e.members) v.Check(!m.IsNamed && !m.p0Eligible, "ordinary company rank-and-file remains anonymous despite visible presence"); return StepResult.Next; });
            Then("return the six anonymous visitors", () => ExitPeers());
            Then("wait for terminal reconciliation", WaitGroup, 20000);
            Then("zero new identities; stock restored once", () => { CheckGroupReturn(0); v.Check(org.knownMembers.Count == 1 && org.Healthy == 15 && org.Committed == 0, "only the original leader remains known; fifteen abstract people, no hidden persistent roster"); return StepResult.Next; });
        }
    }

    public class GroupCaptureRun : GroupRun
    {
        protected EpisodeMember capturedMember;
        protected Pawn captive;
        protected int arrestedTick = -1;
        private PhysicalEpisode captureEpisode;
        private PawnRef capturedBinding;
        private int captiveThingId, capturedSlot;
        private OperationalRole capturedRole;
        private Map captureMap;
        private bool captureGuardActive, captureFailed, capturePeersExited;
        private PhysicalObservation captureObservation;
        protected GroupCaptureRun(string family, NetworkRuntime rt, string runId) : base(family, rt, runId) { }

        protected StepResult ArrestAnonymous()
        {
            capturedMember = e?.members != null && e.members.Count > 0 ? e.members[0] : null;
            captive = capturedMember?.pawn?.pawn;
            if (!PhysicalTestSession.IsActiveOwnedEpisode(e) || capturedMember == null || capturedMember.IsNamed || captive == null
                || !captive.Spawned || !TestSite.IsTestMap(captive.Map) || captive.guest == null)
            { v.Fail("arrest requires this run's anonymous placed member"); return StepResult.Abort; }
            Building_Bed bed;
            string report;
            if (!TestCompound.TryPreparePrisoner(e, capturedMember, captive, out bed, out report))
            { v.Fail("custody fixture preparation refused: " + report); return StepResult.Abort; }
            v.Note(report);
            captureEpisode = e;
            capturedBinding = capturedMember.pawn;
            captiveThingId = captive.thingIDNumber;
            capturedSlot = capturedMember.slot;
            capturedRole = capturedMember.seatRole;
            captureMap = captive.Map;
            captive.guest.CapturedBy(Faction.OfPlayer);
            arrestedTick = PhysLog.Tick;
            captureGuardActive = true;
            everyFrame = ObserveCaptureProtection;
            if (!TestCompound.TryClaimPrisonerBed(captive, bed, out report))
            { CaptureFacts(); FailCapture("legitimate prisoner-bed claim refused: " + report); return StepResult.Abort; }
            v.Note(report);
            if (!GuardCapture(true)) return StepResult.Abort;
            v.Check(true, "real vanilla CapturedBy made the SAME anonymous Pawn a colony prisoner in a validated TestCompound cell with its real claimed prisoner bed");
            return StepResult.Next;
        }

        protected StepResult CheckPendingCapture()
        {
            if (!GuardCapture(true)) return StepResult.Abort;
            if (PhysLog.Tick - arrestedTick < 2 * PhysicalLifecycleService.WatchPeriod) return StepResult.Wait;
            lc.Reconcile(e, "owned QA pending-peer check");
            if (!GuardCapture(true)) return StepResult.Abort;
            v.Check(true, "sustained real HeldByPlayer / PlayerPrisoner custody across two watches while ordinary peers remain Pending; open Episode has zero early identity/commit, exact binding, temporary reservation and conserved humans");
            return StepResult.Next;
        }

        private void ObserveCaptureProtection()
        {
            ObserveProtection();
            GuardCapture(!capturePeersExited);
        }

        private GroupPendingCaptureFacts CaptureFacts()
        {
            bool exact = capturedMember != null && e?.members != null && e.members.Contains(capturedMember) && capturedMember.slot == capturedSlot;
            bool samePawn = captive != null && ReferenceEquals(capturedMember?.pawn?.pawn, captive) && groupPawns.Contains(captive);
            bool pendingPeer = false;
            if (e?.members != null)
                foreach (EpisodeMember member in e.members)
                {
                    Pawn peer = member?.pawn?.pawn;
                    if (!ReferenceEquals(member, capturedMember) && member?.outcome == MemberOutcome.Pending && peer != null && peer.Spawned
                        && !peer.Dead && peer.Map == captureMap && !peer.IsPrisoner && !peer.IsSlave && peer.HostFaction == null) pendingPeer = true;
                }
            try { captureObservation = exact ? port.Observe(capturedMember.pawn, e.id) : null; }
            catch (Exception ex) { captureObservation = new PhysicalObservation { kind = ObservedKind.Unknown, note = ex.Message }; }
            return new GroupPendingCaptureFacts(e != null && ReferenceEquals(e, captureEpisode) && PhysicalTestSession.IsActiveOwnedEpisode(e)
                    && ReferenceEquals(ctx.episodes.Get(e.id), e), exact, samePawn, ReferenceEquals(capturedMember?.pawn, capturedBinding),
                captive != null && captive.thingIDNumber == captiveThingId && capturedMember?.pawn?.thingIdNumber == captiveThingId,
                capturedMember?.seatRole == capturedRole, captureMap != null && TestSite.IsTestMap(captureMap) && captive?.Map == captureMap && e?.whereMapId == captureMap.uniqueID,
                captive != null && captive.Spawned && !captive.Dead && !captive.Destroyed && !captive.Discarded,
                captive?.IsPrisonerOfColony == true, captureObservation?.kind == ObservedKind.HeldByPlayer && captureObservation.holder == HeldKind.PlayerPrisoner,
                captive != null && port.Registry.Reserves(captive) && reservationGaps == 0 && freeFrames == 0,
                captive != null && port.Registry.IsTemporaryReserved(captive), capturedMember != null && !capturedMember.IsNamed,
                ctx.characters.characters.Count == charactersBefore, e != null && e.state == EpisodeState.Open && !e.consequencesApplied && !e.releaseApplied
                    && lc.counters.commits == commitsBefore, pendingPeer, ContractorService.Headcount(a, ctx.characters) == humanBefore);
        }

        private bool GuardCapture(bool requirePendingPeers)
        {
            if (!captureGuardActive) return true;
            if (captureFailed) return false;
            GroupPendingCaptureFacts facts = CaptureFacts();
            bool holds = !requirePendingPeers && e?.consequencesApplied == true
                ? GroupQaRules.CaptureCustodyHolds(facts) : GroupQaRules.PendingCaptureHolds(facts, requirePendingPeers);
            if (GroupQaRules.CaptureFailureLatched(captureFailed, holds)) FailCapture("required invariant is false (owned=" + facts.ownedEpisode
                + ", exact member=" + facts.exactMember + ", same role=" + facts.sameRole + ", owned map=" + facts.ownedMap
                + ", live=" + facts.liveSpawned + ", real/observed prisoner=" + facts.actualPrisoner + "/" + facts.observedPlayerPrisoner
                + ", reservation continuous=" + facts.reserved + ", uncommitted=" + facts.uncommitted + ", pending peer=" + facts.pendingPeer
                + ", conserved humans=" + facts.conservedHeadcount + ")");
            return !captureFailed;
        }

        private void FailCapture(string reason)
        {
            if (captureFailed) return;
            captureFailed = GroupQaRules.CaptureFailureLatched(captureFailed, false);
            v.Fail((info.id == "RT-PHYX-030" ? "030B" : "029") + " custody prerequisite lost " + (e?.consequencesApplied == true ? "during terminal handoff" : "before terminal batch")
                + ": " + reason + "; prisoner=" + captive?.IsPrisonerOfColony + ", observation=" + captureObservation?.kind + "/" + captureObservation?.holder
                + ", Pawn #" + captive?.thingIDNumber + "/expected " + captiveThingId + ", binding #" + capturedMember?.pawn?.thingIdNumber
                + ", same Pawn=" + ReferenceEquals(captive, capturedMember?.pawn?.pawn) + ", same PawnRef=" + ReferenceEquals(capturedBinding, capturedMember?.pawn)
                + ", slot=" + capturedMember?.slot + ", role=" + capturedMember?.seatRole + ", spawned=" + captive?.Spawned + ", map=" + captive?.Map?.uniqueID
                + ", dead=" + captive?.Dead + ", discarded=" + captive?.Discarded + ", HostFaction=" + captive?.HostFaction?.loadID
                + ", reserved=" + (captive != null && port.Registry.Reserves(captive)) + ", temporary=" + (captive != null && port.Registry.IsTemporaryReserved(captive))
                + ", anonymous=" + (capturedMember != null && !capturedMember.IsNamed) + ", identities=" + ctx.characters.characters.Count + "/" + charactersBefore
                + ", Episode=" + e?.id + "/" + e?.state + ", consequencesApplied=" + e?.consequencesApplied + ", releaseApplied=" + e?.releaseApplied
                + ", tick=" + PhysLog.Tick + ", elapsed=" + (PhysLog.Tick - arrestedTick) + ". Run aborted; no repair or downstream promotion checks; everything is preserved.");
        }

        protected StepResult ExitCapturePeers()
        {
            if (!GuardCapture(true)) return StepResult.Abort;
            StepResult result = ExitPeers(captive);
            if (result != StepResult.Next) return result;
            capturePeersExited = true;
            return GuardCapture(false) ? StepResult.Next : StepResult.Abort;
        }

        protected StepResult WaitCapturedGroup()
        {
            return GuardCapture(false) ? WaitGroup() : StepResult.Abort;
        }

        protected bool GuardCaptureForPromotion()
        {
            return GuardCapture(false);
        }

        protected bool GuardPendingCapture()
        {
            return GuardCapture(true);
        }

        protected void CheckPromotion()
        {
            KnownCharacter known = capturedMember.IsNamed ? ctx.characters.Get(capturedMember.character) : null;
            if (known != null) testPeople.Add(known.id.Value);
            v.Check(e.IsComplete && lc.counters.commits == commitsBefore + 1 && ctx.characters.characters.Count == charactersBefore + 1, "exactly ONE identity created inside one terminal commit");
            v.Check(known != null && ReferenceEquals(known.pawn?.pawn, captive) && known.name.Display == captive.Name.ToStringFull,
                "promoted identity binds the SAME Pawn and its actual existing name");
            v.Check(known != null && known.org == a.id && known.opRole == capturedMember.seatRole && org.knownMembers.Contains(known.id), "correct role, organizational provenance and existing membership store");
            v.Check(known != null && known.status == CharacterStatus.Captured && known.custody == CustodyState.OutOfCustody && known.heldBy == HeldKind.PlayerPrisoner,
                "Captured / OutOfCustody(PlayerPrisoner), no abstract copy");
            v.Check(known != null && !AuthorityGate.CanSimulateAbstractly(known) && captive.IsPrisonerOfColony, "vanilla custody remains untouched and abstract authority stays closed");
            v.Check(port.Registry.Reserves(captive) && !port.Registry.IsTemporaryReserved(captive), "temporary reservation transitions to named held retention without a gap");
            v.Check(known != null && PhysicalTags.Has(captive, PhysicalTags.Character(known.id)) && !PhysicalTags.Has(captive, PhysicalTags.Episode(e.id)), "RELEASE installs the character routing tag and removes the old Episode tag");
            v.Check(ctx.scheduler.Has(PhysicalLifecycleService.CustodyWatchJob, PhysicalLifecycleService.CustodyWatchTarget), "custody watch exists after RELEASE");
            v.Check(ContractorService.Headcount(a, ctx.characters) == humanBefore && org.Healthy == 14 && org.Committed == 0, "one held named human plus fourteen abstract people plus leader, no duplicate headcount");
            v.Check(freeFrames == 0 && reservationGaps == 0, "no reservation gap while peers were Pending or during promotion");
            CheckIntegrity();
            v.Note("The captured person deliberately remains held on the owned test map. Cleanup preserves identities and custody.");
        }
        protected override void Script() { }
    }

    public sealed class Phyx029LargeCapture : GroupCaptureRun
    {
        public Phyx029LargeCapture(NetworkRuntime rt, string runId) : base("RT-PHYX-029", rt, runId) { }
        protected override void Script()
        {
            Then("create the owned company", () => CreateFixture(16, true));
            Then("place three anonymous company Riflemen without P0", () => PlaceGroup(new[] { new RoleCapacity(OperationalRole.Rifleman, 3) }, false));
            Then("arrest one anonymous member using vanilla custody", ArrestAnonymous);
            Then("observe temporary reservation across two watches while peers remain Pending", CheckPendingCapture, 1500);
            Then("return only ordinary peers; leave the captive untouched", ExitCapturePeers);
            Then("wait for whole-Episode atomic promotion", WaitCapturedGroup, 20000);
            Then("check exactly one same-Pawn held identity", () => { if (!GuardCaptureForPromotion()) return StepResult.Abort; CheckPromotion(); return StepResult.Next; });
        }
    }

    public sealed class Phyx030GroupSave : GroupCaptureRun
    {
        private readonly bool pending;
        public Phyx030GroupSave(NetworkRuntime rt, string runId, bool pending) : base("RT-PHYX-030", rt, runId) { this.pending = pending; }
        protected override void Script()
        {
            Then("create an owned save/load fixture", () => CreateFixture(pending ? 16 : 5, pending));
            Then("place the save/load group", () => PlaceGroup(pending ? (IList<RoleCapacity>)new[] { new RoleCapacity(OperationalRole.Rifleman, 3) } : GroupFixtures.SmallVisit(false), !pending));
            if (pending)
            {
                Then("arrest one anonymous member", ArrestAnonymous);
                Then("verify anonymous retention while peers remain Pending", CheckPendingCapture, 1500);
            }
            else
            {
                Then("return the first-visit crew", () => ExitPeers());
                Then("wait for promotion and complete release", WaitGroup, 20000);
                Then("check the concretized identities before saving", () => { CheckGroupReturn(2); return StepResult.Next; });
            }
            Then("pause at the owner-assisted SAVE checkpoint", () =>
            {
                if (pending && !GuardPendingCapture()) return StepResult.Abort;
                Find.TickManager.Pause();
                v.Note((pending ? "030B" : "030A") + " SAVE checkpoint: SAVE, return to main menu, LOAD, run 030V VERIFY without arming. Time may resume on load; correctness never depends on pause persisting.");
                v.Note("The arm and runner are never saved. This production Episode's existing cause identifies the checkpoint: " + e.cause.devKey);
                CheckIntegrity();
                return StepResult.Next;
            });
        }
    }

    /// <summary>Read-only loaded-state evidence. Never drives an exit, generates, reconciles or repairs.</summary>
    public sealed class Phyx030GroupVerify : PhysicalRun
    {
        private PhysicalEpisode selected;
        private bool follow;
        private EpisodeMember loadedCaptive;
        private PawnRef loadedCaptiveBinding;
        private Pawn loadedCaptivePawn;
        private int loadedCaptiveThingId, loadedCharacters;
        private bool loadedCaptureGuard, loadedCaptureFailed;
        public Phyx030GroupVerify(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-030"), runId, rt) { }
        protected override void Script()
        {
            Then("verify loaded group bindings and reservation categories", () => { VerifyLoaded(); return loadedCaptureFailed ? StepResult.Abort : StepResult.Next; });
            Then("follow a saved incomplete Episode read-only; vanilla peers leave naturally", () =>
            {
                if (!follow || selected == null) return StepResult.Next;
                if (!GuardLoadedCapture()) return StepResult.Abort;
                if (selected.state == EpisodeState.Quarantined) { v.Fail("saved group quarantined: " + selected.quarantineKey); return StepResult.Abort; }
                return selected.IsComplete ? StepResult.Next : StepResult.Wait;
            }, 60000);
            Then("verify terminal truth and all retained group identities", () => { if (loadedCaptureFailed) return StepResult.Abort; VerifyTerminal(); return StepResult.Next; });
        }

        private void VerifyLoaded()
        {
            v.Check(!PhysicalTestSession.Arm.IsArmedFor(Current.Game), "the session arm is cleared after load");
            v.Check(lc.counters.created == 0 && port.counters.projections == 0 && port.counters.placements == 0, "load has generated, replaced and placed zero Pawns");
            v.Check(port.Registry.pointersResolved, "resolved pointer index is available after cross references; early durable bridge safety has separate headless/load-order proof");
            v.Check(port.Registry.RetainedCount() == port.Registry.DurableRetainedCount(), "every named and temporary durable retention obligation is covered");
            v.Check(port.Registry.Audit().Count == 0 && port.Registry.AuditTemporary().Count == 0, "zero binding integrity findings after load");
            selected = GroupQaRules.LatestCheckpoint(ctx.episodes.episodes);
            if (selected == null) { v.Gap("no unambiguous 030 SAVE checkpoint or 031 succession Episode exists"); return; }
            episodes.Add(selected);
            follow = !selected.IsComplete;
            if (!follow) v.Note("The saved Episode completed before the owner ran 030V; verifying durable terminal truth. This does not prove the instantaneous pre-commit load state.");
            else
            {
                if (PhysicalTestIds.ScenarioOf(selected.cause?.devKey) == "RT-PHYX-030"
                    && ctx.actors.Get(selected.actor)?.Get<OrganizationProfile>()?.capacity == 32)
                {
                    loadedCaptureGuard = true;
                    loadedCaptive = selected.members.Find(m => m?.slot == 0);
                    loadedCaptiveBinding = loadedCaptive?.pawn;
                    loadedCaptivePawn = loadedCaptiveBinding?.pawn;
                    loadedCaptiveThingId = loadedCaptiveBinding?.thingIdNumber ?? 0;
                    loadedCharacters = ctx.characters.characters.Count;
                    everyFrame = () => { GuardLoadedCapture(); };
                    if (!GuardLoadedCapture()) return;
                    v.Note("030B loaded custody: read-only proof of the exact saved slot-0 captive. Vanilla peers may leave naturally; no current-run arm, arrest, relocation, claim, reconciliation or repair occurs.");
                }
                foreach (EpisodeMember m in selected.members)
                {
                    Pawn pawn = m.pawn?.pawn;
                    v.Check(pawn != null && !pawn.Discarded && pawn.thingIDNumber == m.pawn.thingIdNumber, "saved member binding resolves to its same durable Pawn");
                    if (pawn != null && !pawn.Dead) v.Check(port.Registry.Reserves(pawn), "saved live member protected before terminal commit");
                    if (!m.IsNamed && pawn != null && !pawn.Dead) v.Check(port.Registry.IsTemporaryReserved(pawn), "saved anonymous member uses Episode reservation without a temporary Character");
                }
                v.Note("Only production and vanilla continue this Episode. Stopping the QA verifier never cancels it; an incomplete Episode still blocks destructive tests.");
            }
        }

        private bool GuardLoadedCapture()
        {
            if (!loadedCaptureGuard) return true;
            if (loadedCaptureFailed) return false;
            if (selected.IsComplete) return true;
            NetworkActor actor = ctx.actors.Get(selected.actor);
            bool pendingPeer = selected.members.Exists(m => !ReferenceEquals(m, loadedCaptive) && m?.outcome == MemberOutcome.Pending);
            PhysicalObservation observed;
            try { observed = loadedCaptiveBinding == null ? null : port.Observe(loadedCaptiveBinding, selected.id); }
            catch (Exception ex) { observed = new PhysicalObservation { kind = ObservedKind.Unknown, note = ex.Message }; }
            GroupPendingCaptureFacts facts = new GroupPendingCaptureFacts(ReferenceEquals(ctx.episodes.Get(selected.id), selected)
                    && PhysicalTestIds.ScenarioOf(selected.cause?.devKey) == "RT-PHYX-030" && actor?.Get<OrganizationProfile>()?.capacity == 32,
                loadedCaptive != null && selected.members.Contains(loadedCaptive) && loadedCaptive.slot == 0
                    && selected.members.FindAll(m => m?.slot == 0).Count == 1,
                loadedCaptivePawn != null && ReferenceEquals(loadedCaptive?.pawn?.pawn, loadedCaptivePawn),
                ReferenceEquals(loadedCaptive?.pawn, loadedCaptiveBinding), loadedCaptiveThingId > 0 && loadedCaptive?.pawn?.thingIdNumber == loadedCaptiveThingId
                    && loadedCaptivePawn?.thingIDNumber == loadedCaptiveThingId, loadedCaptive?.seatRole == OperationalRole.Rifleman,
                loadedCaptivePawn?.Map != null && TestSite.IsTestMap(loadedCaptivePawn.Map) && loadedCaptivePawn.Map.uniqueID == selected.whereMapId,
                loadedCaptivePawn != null && loadedCaptivePawn.Spawned && !loadedCaptivePawn.Dead && !loadedCaptivePawn.Destroyed && !loadedCaptivePawn.Discarded,
                loadedCaptivePawn?.IsPrisonerOfColony == true, observed?.kind == ObservedKind.HeldByPlayer && observed.holder == HeldKind.PlayerPrisoner,
                loadedCaptivePawn != null && port.Registry.Reserves(loadedCaptivePawn), loadedCaptivePawn != null && port.Registry.IsTemporaryReserved(loadedCaptivePawn),
                loadedCaptive != null && !loadedCaptive.IsNamed, ctx.characters.characters.Count == loadedCharacters && ContractorService.CurrentNamedCount(actor, ctx.characters) == 1,
                selected.state == EpisodeState.Open && !selected.consequencesApplied && !selected.releaseApplied, pendingPeer,
                ContractorService.Headcount(actor, ctx.characters) == 16);
            bool holds = selected.consequencesApplied ? GroupQaRules.CaptureCustodyHolds(facts) : GroupQaRules.PendingCaptureHolds(facts, false);
            loadedCaptureFailed = GroupQaRules.CaptureFailureLatched(loadedCaptureFailed, holds);
            if (!loadedCaptureFailed) return true;
            v.Fail("030V saved 030B custody prerequisite lost before complete terminal release: prisoner=" + loadedCaptivePawn?.IsPrisonerOfColony
                + ", observation=" + observed?.kind + "/" + observed?.holder + ", Pawn #" + loadedCaptivePawn?.thingIDNumber + "/saved " + loadedCaptiveThingId
                + ", spawned=" + loadedCaptivePawn?.Spawned + ", map=" + loadedCaptivePawn?.Map?.uniqueID + ", anonymous=" + (loadedCaptive != null && !loadedCaptive.IsNamed)
                + ", temporary=" + (loadedCaptivePawn != null && port.Registry.IsTemporaryReserved(loadedCaptivePawn)) + ", Episode=" + selected.state
                + ", consequencesApplied=" + selected.consequencesApplied + ", releaseApplied=" + selected.releaseApplied + ", tick=" + PhysLog.Tick
                + ". Read-only verifier aborted once; everything is preserved and production continues independently.");
            return false;
        }

        private void VerifyTerminal()
        {
            if (selected == null) return;
            v.Check(selected.IsComplete, "saved Episode finished all durable stages exactly once");
            NetworkActor actor = ctx.actors.Get(selected.actor);
            OrganizationProfile roster = actor?.Get<OrganizationProfile>();
            if (roster == null) { v.Fail("saved organization is missing"); return; }
            KnownCharacter checkpointMedic = null;
            foreach (EpisodeMember member in selected.members)
            {
                if (!member.IsNamed)
                {
                    v.Check(member.outcome == MemberOutcome.Returned && member.pawn == null, "ordinary anonymous terminal member has no persistent Pawn copy");
                    continue;
                }
                KnownCharacter expected = ctx.characters.Get(member.character);
                v.Check(expected != null, member.character + ": the expected checkpoint identity exists");
                if (expected == null) continue;
                testPeople.Add(expected.id.Value);
                v.Check(expected.org == actor.id && expected.opRole == member.seatRole, member.character + ": checkpoint provenance and role are preserved");
                v.Check(expected.pawn != null && expected.pawn.IsBound && member.pawn != null && member.pawn.IsBound
                    && expected.pawn.thingIdNumber == member.pawn.thingIdNumber, member.character + ": both durable bindings retain the same checkpoint thing id");
                if (member.outcome == MemberOutcome.Killed || member.outcome == MemberOutcome.Lost)
                {
                    v.Check(expected.status == (member.outcome == MemberOutcome.Killed ? CharacterStatus.Dead : CharacterStatus.Lost), member.character + ": terminal identity status remains final");
                    continue; // Dead/lost identity history does not impose living-Pawn retention on vanilla.
                }
                Pawn pawn = expected.pawn?.pawn;
                v.Check(pawn != null && !pawn.Discarded && member.pawn != null && ReferenceEquals(pawn, member.pawn.pawn)
                    && pawn.thingIDNumber == expected.pawn.thingIdNumber, member.character + ": same live Pawn resolves from the saved member and person bindings");
                if (member.outcome == MemberOutcome.Returned)
                    v.Check(expected.custody == CustodyState.Stored && !expected.episode.IsValid && AuthorityGate.CanSimulateAbstractly(expected)
                        && pawn != null && Find.WorldPawns.Contains(pawn) && !pawn.IsPrisoner && !pawn.IsSlave && pawn.HostFaction == null,
                        member.character + ": saved Returned outcome remains Stored, unlinked and genuinely returned to world custody");
                else if (member.outcome == MemberOutcome.HeldByPlayer)
                    v.Check(expected.status == CharacterStatus.Captured && expected.custody == CustodyState.OutOfCustody
                        && expected.heldBy == HeldKind.PlayerPrisoner && !expected.episode.IsValid && !AuthorityGate.CanSimulateAbstractly(expected)
                        && pawn != null && pawn.IsPrisonerOfColony,
                        member.character + ": saved arrest remains Captured / OutOfCustody(PlayerPrisoner), a real prisoner with closed abstract authority");
                else v.Fail(member.character + ": unsupported outcome in this fixed group checkpoint: " + member.outcome);
                if (member.seatRole == OperationalRole.Medic) checkpointMedic = expected;
            }
            bool succession = PhysicalTestIds.ScenarioOf(selected.cause?.devKey) == "RT-PHYX-031";
            if (succession)
                v.Check(checkpointMedic != null && roster.leader == checkpointMedic.id && checkpointMedic.role == CharacterRole.Leader
                    && checkpointMedic.opRole == OperationalRole.Medic, "saved succession specifically preserves Medic as organizational Leader with operational Medic");
            int expectedHumans = succession ? 4 : roster.capacity == 32 ? 16 : 5;
            int expectedKnown = succession || roster.capacity == 32 ? 2 : 3;
            v.Check(ContractorService.Headcount(actor, ctx.characters) == expectedHumans && ContractorService.CurrentNamedCount(actor, ctx.characters) == expectedKnown
                && roster.Healthy + roster.Wounded + roster.Committed == expectedHumans - expectedKnown,
                "loaded checkpoint has exact named + anonymous human conservation, no phantom abstract copies");
            HashSet<int> ids = new HashSet<int>();
            foreach (CharacterId id in roster.knownMembers)
            {
                KnownCharacter known = ctx.characters.Get(id);
                v.Check(known != null, id + ": every listed identity exists");
                if (known == null || !known.IsAlive) continue;
                if (known.pawn == null || !known.pawn.IsBound)
                {
                    v.Check(selected.MemberFor(id) == null && known.custody == CustodyState.Unmaterialized && !known.episode.IsValid
                        && known.firstEncounterTick < 0 && known.org == actor.id && OrganizationCompositionV1.IsRole(known.opRole),
                        id + ": only an unencountered, unmaterialized origin pin may remain abstract without a Pawn; an expected/Stored/Deployed/held binding may never disappear");
                    continue;
                }
                Pawn pawn = known.pawn.pawn;
                testPeople.Add(id.Value);
                v.Check(pawn != null && !pawn.Discarded && pawn.thingIDNumber == known.pawn.thingIdNumber && ids.Add(known.pawn.thingIdNumber),
                    id + ": same unique Pawn binding, no phantom replacement");
                v.Check(port.Registry.Reserves(pawn) && !port.Registry.IsTemporaryReserved(pawn), id + ": durable named retention/custody survived load");
                EpisodeMember evidence = null;
                foreach (PhysicalEpisode old in ctx.episodes.episodes)
                    if (old != null && old.actor == actor.id && old.cause != null && PhysicalTestIds.IsTestDevKey(old.cause.devKey))
                        foreach (EpisodeMember m in old.members) if (m.character == id) evidence = m;
                v.Check(evidence != null && evidence.seatRole == known.opRole, id + ": operational role equals durable mission seat role after reload");
                if (known.role == CharacterRole.Leader && evidence?.seatRole == OperationalRole.Medic)
                    v.Check(known.opRole == OperationalRole.Medic, "succession/reload: organizational Leader remains operational Medic");
                if (known.custody == CustodyState.OutOfCustody)
                    v.Check(!AuthorityGate.CanSimulateAbstractly(known) && known.heldBy != HeldKind.None, id + ": still vanilla-held and never abstract");
            }
            v.Check(port.Registry.Audit().Count == 0 && port.Registry.AuditTemporary().Count == 0, "zero integrity findings in completed loaded state");
            List<string> findings = new List<string>();
            EpisodeChecks.Report(ctx, findings);
            v.Check(findings.Count == 0, "zero Episode integrity findings in loaded checkpoint" + (findings.Count > 0 ? ": " + findings[0] : ""));
            v.Note("Checkpoint conservation uses this scenario's fixed owned fixture and durable named/anonymous truth; no independent QA baseline is persisted.");
        }
    }

    public sealed class Phyx031RoleSuccession : GroupRun
    {
        private KnownCharacter medic;
        private Pawn originalMedic;
        public Phyx031RoleSuccession(NetworkRuntime rt, string runId) : base("RT-PHYX-031", rt, runId) { }
        protected override void Script()
        {
            Then("create the owned five-person crew", () => CreateFixture(5, false));
            Then("concretize Leader, Medic and Rifleman", () => PlaceGroup(GroupFixtures.SmallVisit(false), true));
            Then("return the first group", () => ExitPeers());
            Then("wait for first terminal commit", WaitGroup, 20000);
            Then("select the remembered Medic as this fixture's succession candidate", () =>
            {
                CheckGroupReturn(2);
                foreach (CharacterId id in org.knownMembers)
                {
                    KnownCharacter k = ctx.characters.Get(id);
                    if (k?.opRole == OperationalRole.Medic) medic = k;
                }
                if (medic == null) { v.Fail("the concretized Medic is absent"); return StepResult.Abort; }
                originalMedic = medic.pawn.pawn;
                medic.notability = 0.95f; // Mutable standing on this owned fixture only; OperationalRole is never rewritten.
                return StepResult.Next;
            });
            Then("rematerialize the same Leader and Medic", () => PlaceGroup(new[] { new RoleCapacity(OperationalRole.Leader, 1), new RoleCapacity(OperationalRole.Medic, 1) }, false));
            Then("vanilla dev damage kills only the owned leader", () =>
            {
                Pawn leader = null;
                foreach (EpisodeMember m in e.members) if (m.character == org.leader) leader = m.pawn?.pawn;
                if (leader == null || !leader.Spawned || !TestSite.IsTestMap(leader.Map)) { v.Fail("owned leader is not present"); return StepResult.Abort; }
                HealthUtility.DamageUntilDead(leader);
                v.Check(leader.Dead && ReferenceEquals(medic.pawn.pawn, originalMedic), "only the owned leader died; same Medic Pawn");
                return StepResult.Next;
            });
            Then("return the surviving Medic through vanilla ExitMap", () => ExitPeers());
            Then("one group commit performs ordinary organizational succession", WaitGroup, 20000);
            Then("Medic becomes organizational Leader without operational-role drift", () =>
            {
                v.Check(org.leader == medic.id && medic.role == CharacterRole.Leader && medic.opRole == OperationalRole.Medic,
                    "CharacterRole.Leader and OperationalRole.Medic remain separate");
                v.Check(ReferenceEquals(medic.pawn.pawn, originalMedic) && medic.custody == CustodyState.Stored, "same Medic Pawn and binding after leadership change");
                v.Check(ContractorService.Headcount(a, ctx.characters) == humanBefore - 1, "exactly one dead human, no role/headcount duplicate");
                CheckIntegrity();
                Find.TickManager.Pause();
                v.Note("SAVE this succession checkpoint, LOAD, then run 030V VERIFY without arming. Reload role checks read the persisted Episode seat; R-50 remains OPEN.");
                return StepResult.Next;
            });
        }
    }

    /// <summary>Explicitly armed bounded real retained-population fixture; no identity is removed to meet the target.</summary>
    public sealed class Phyx032RetentionBuild : GroupRun
    {
        private readonly int target;
        private int fixtures;
        private bool returning;
        public Phyx032RetentionBuild(NetworkRuntime rt, string runId, int target) : base("RT-PHYX-032", rt, runId) { this.target = target; }
        protected override void Script()
        {
            Then("build a bounded real retained population near " + target + " (one group per frame)", () =>
            {
                if (returning)
                {
                    if (!e.IsComplete) return WaitGroup();
                    CheckGroupReturn(e.members.Count - 1);
                    returning = false;
                    return StepResult.Wait;
                }
                int count = port.Registry.NamedRetainedCount();
                int size = GroupQaRules.RetentionRequestSize(count, target);
                if (size == 0) return StepResult.Next;
                if (fixtures >= 150) { v.Fail("retention fixture exceeded its 150-organization bound"); return StepResult.Abort; }
                fixtures++;
                StepResult made = CreateFixture(size, false);
                if (made != StepResult.Next) return made;
                OrganizationSeats seats;
                string why;
                if (!OrganizationSeatPolicy.TryApportion(a, ctx.characters.characters, out seats, out why)) { v.Fail(why); return StepResult.Abort; }
                List<RoleCapacity> required = new List<RoleCapacity>();
                foreach (OrganizationRoleSeats role in seats.roles) if (role.pinned + role.anonymous > 0) required.Add(new RoleCapacity(role.role, role.pinned + role.anonymous));
                made = PlaceGroup(required, true);
                if (made != StepResult.Next) return made;
                made = ExitPeers();
                if (made != StepResult.Next) return made;
                returning = true;
                return StepResult.Wait;
            }, 400000);
            Then("measure real registry rebuilding and reservation lookups, then pause for SAVE", () =>
            {
                Stopwatch watch = Stopwatch.StartNew();
                RegistryLoadReport report = port.Registry.Rebuild();
                watch.Stop();
                v.Note("MEASURED in this running game: registry rebuild " + watch.Elapsed.TotalMilliseconds.ToString("0.000") + " ms; " + report);
                RetentionObservation.Measure(ctx, port.Registry, v);
                v.Check(port.Registry.NamedRetainedCount() >= target, "retained identity count reached the soft observation region " + target + "; count above 150 is never itself a failure");
                CheckIntegrity();
                Find.TickManager.Pause();
                v.Note("SAVE and record file bytes manually; LOAD, run 032V OBSERVE without arming. Real TPS, save-size delta and load elapsed require owner measurements and are not claimed by this run.");
                return StepResult.Next;
            });
        }
    }

    public static class RetentionObservation
    {
        public static void Measure(DomainContext ctx, RetainedPawnRegistry registry, PhysicalVerdict v)
        {
            List<Pawn> sample = new List<Pawn>();
            foreach (KnownCharacter known in ctx.characters.characters)
                if (sample.Count < 300 && RetainedPawnRegistry.RetainedCustody(known) && known.pawn?.pawn != null) sample.Add(known.pawn.pawn);
            if (sample.Count == 0) { v.Gap("no retained Pawn sample exists"); return; }
            int covered = 0;
            Stopwatch watch = Stopwatch.StartNew();
            for (int repeat = 0; repeat < 100; repeat++) foreach (Pawn pawn in sample) if (registry.Reserves(pawn)) covered++;
            watch.Stop();
            v.Check(covered == sample.Count * 100, "bounded retained sample: every reservation lookup succeeds");
            v.Note("MEASURED in this running game: " + (sample.Count * 100) + " reservation lookups in " + watch.Elapsed.TotalMilliseconds.ToString("0.000")
                + " ms; named retained " + registry.NamedRetainedCount() + ", temporary retained " + registry.TemporaryRetainedCount() + ".");
            v.Note("Registry/lookups are measured; real TPS and save bytes are manual owner observations, pending until recorded. No count-based identity pruning.");
        }
    }

    public sealed class Phyx032RetentionVerify : PhysicalRun
    {
        public Phyx032RetentionVerify(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-032"), runId, rt) { }
        protected override void Script()
        {
            Then("observe retained coverage and integrity after owner SAVE/LOAD, read-only", () =>
            {
                v.Check(!PhysicalTestSession.Arm.IsArmedFor(Current.Game), "the physical arm is cleared on load");
                v.Check(port.Registry.pointersResolved && port.Registry.RetainedCount() == port.Registry.DurableRetainedCount(), "resolved registry covers every durable named/temporary obligation");
                v.Check(port.Registry.Audit().Count == 0 && port.Registry.AuditTemporary().Count == 0, "zero retained binding integrity findings");
                int count = port.Registry.NamedRetainedCount();
                if (count < 150) v.Gap("fewer than 150 named retained Pawns: use 032A BUILD first");
                RetentionObservation.Measure(ctx, port.Registry, v);
                v.Note("No rebuild, generation, pruning or save automation in this verifier. Record real save-size delta/load/TPS separately.");
                return StepResult.Next;
            });
        }
    }
}
