using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TheNetwork.Diagnostics.RuntimePhysicalTests;
using TheNetwork.Diagnostics.RuntimeTests;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>Owned compound geometry and custody QA prerequisites. Real prison rooms, pathing and escape AI remain owner runtime evidence.</summary>
    public static class Phase32bCompoundCustodyTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Compound_DeterministicGeometryFitsSmallMap", Geometry));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Compound_VisitorCellsStayOutsidePrison", Visitors));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Compound_RealDoorClosesWallBoundaryAndBedsStayInside", PrisonGeometry));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Compound_ConstructionRefusesOrdinaryColonyMaps", OwnershipWiring));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Compound_EnsureReusesValidStructuresAndVerifiesVanillaPrison", StructureWiring));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.CaptureGuard_EveryPendingPrerequisiteMustHold", PendingFacts));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.CaptureGuard_PeerExitRelaxesOnlyPeerPresence", PeerBoundary));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.CaptureGuard_ActualAndObservedCustodyMustAgree", CustodyAgreement));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.CaptureGuard_NamedTerminalHandoffKeepsPhysicalCustodyRequired", TerminalBoundary));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.CaptureGuard_FailureCannotBeErasedByLaterRecovery", FailureLatch));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.CaptureGuard_FactsAndRulesNeverMutateOrRepair", ReadOnlyPolicy));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.CaptureGuard_EscapeBeforePeersResolveFailsWithoutEarlyIdentity", EscapeModel));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.CaptureGuard_RuntimeChecksBeforeWaitAndAfterReconcile", GuardWiring));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.CaptureGuard_029And030BShareOneLegitimateArrest", SharedFixtureWiring));
        }

        private static string Source(string path)
        { return PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src(path)); }

        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            T.Check(at >= 0, "source boundary exists: " + signature);
            if (at < 0) return "";
            int open = source.IndexOf('{', at), depth = 0;
            for (int i = open; i >= 0 && i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            T.Check(false, "source boundary closes: " + signature);
            return "";
        }

        private static GroupPendingCaptureFacts Facts(bool[] flags = null)
        {
            if (flags == null) flags = Enumerable.Repeat(true, 17).ToArray();
            return new GroupPendingCaptureFacts(flags[0], flags[1], flags[2], flags[3], flags[4], flags[5], flags[6], flags[7],
                flags[8], flags[9], flags[10], flags[11], flags[12], flags[13], flags[14], flags[15], flags[16]);
        }

        private static bool[] Broken(params int[] indices)
        {
            bool[] flags = Enumerable.Repeat(true, 17).ToArray();
            foreach (int i in indices) flags[i] = false;
            return flags;
        }

        private static IntVec3 Center => new IntVec3(TestSite.MapSize / 2, 0, TestSite.MapSize / 2);

        private static void Geometry()
        {
            T.Eq(60, TestSite.MapSize, "bounded dedicated QA map size");
            IntVec3 center = Center;
            List<IntVec3> walls = TestCompound.WallCells(center).ToList();
            List<IntVec3> beds = TestCompound.PrisonBedCells(center).ToList();
            List<IntVec3> visitors = TestCompound.VisitorCells(center).ToList();
            T.Check(walls.SequenceEqual(TestCompound.WallCells(center)) && beds.SequenceEqual(TestCompound.PrisonBedCells(center))
                && visitors.SequenceEqual(TestCompound.VisitorCells(center)), "repeat geometry requests preserve exact coordinates and order");
            T.Eq(walls.Count, walls.Distinct().Count(), "wall geometry contains no duplicate Thing cell");
            T.Eq(beds.Count, beds.Distinct().Count(), "bed geometry contains no duplicate Thing cell");
            foreach (IntVec3 cell in TestCompound.PrisonFootprint(center).Concat(visitors))
                T.Check(cell.x >= 5 && cell.z >= 5 && cell.x < TestSite.MapSize - 5 && cell.z < TestSite.MapSize - 5,
                    "compound stays inside safe map-edge margin: " + cell);
            IntVec3 shifted = center + new IntVec3(-3, 0, 2);
            T.Check(TestCompound.WallCells(shifted).SequenceEqual(walls.Select(c => c + new IntVec3(-3, 0, 2))), "deterministic walls are relative to map center");
            T.Check(TestCompound.PrisonBedCells(shifted).SequenceEqual(beds.Select(c => c + new IntVec3(-3, 0, 2))), "deterministic beds are relative to map center");
            IntVec3 olderCenter = new IntVec3(50, 0, 50);
            foreach (IntVec3 cell in TestCompound.PrisonFootprint(olderCenter).Concat(TestCompound.VisitorCells(olderCenter)))
                T.Check(cell.x >= 5 && cell.z >= 5 && cell.x < 95 && cell.z < 95, "existing loaded 100-map geometry stays bounded without resizing: " + cell);
        }

        private static void Visitors()
        {
            HashSet<IntVec3> visitors = new HashSet<IntVec3>(TestCompound.VisitorCells(Center));
            CellRect prison = TestCompound.PrisonInterior(Center);
            T.Check(visitors.Count >= PhysicalLifecycleService.MaxMembers, "visitor area can host the full bounded eight-Pawn Episode");
            T.Check(!visitors.Any(c => prison.Contains(c)), "ordinary visitors never spawn inside prisoner interior");
            T.Check(!visitors.Overlaps(TestCompound.WallCells(Center)) && !visitors.Contains(TestCompound.PrisonDoorCell(Center)), "visitor area is separate from prison walls and real doorway");
            T.Check(visitors.Contains(Center), "existing center-based visitor placement has an open compound area");
        }

        private static void PrisonGeometry()
        {
            IntVec3 center = Center, door = TestCompound.PrisonDoorCell(center);
            CellRect footprint = TestCompound.PrisonFootprint(center), interior = TestCompound.PrisonInterior(center);
            HashSet<IntVec3> walls = new HashSet<IntVec3>(TestCompound.WallCells(center));
            HashSet<IntVec3> boundary = new HashSet<IntVec3>(footprint.Where(c => !interior.Contains(c)));
            T.Check(boundary.Contains(door) && !walls.Contains(door), "one real door replaces one perimeter wall cell");
            walls.Add(door);
            T.Check(walls.SetEquals(boundary), "walls plus door exactly enclose the full deterministic prison perimeter");
            List<IntVec3> beds = TestCompound.PrisonBedCells(center).ToList();
            T.Eq(2, beds.Count, "two legitimate beds support a second bounded custody fixture without evicting a captive");
            foreach (IntVec3 bed in beds)
                T.Check(interior.Contains(bed) && interior.Contains(bed + IntVec3.North), "whole north-facing real bed fits within prison interior: " + bed);
            T.Check(!beds.Contains(door), "door remains available for ordinary vanilla pathing");
        }

        private static void OwnershipWiring()
        {
            string compound = Source("Diagnostics/RuntimePhysicalTests/TestCompound.cs");
            T.Check(compound.Contains("TestSite.IsTestMap(") && compound.Contains("TestSite.DefRefusal("), "construction accepts only the current narrow dedicated TestSite");
            T.Check(compound.Contains("IsPlayerHome") && compound.Contains("parent.Faction") && compound.Contains("typeof(MapParent)"), "compound refuses home, owned or widened parent semantics");
            string refusal;
            T.Check(!TestCompound.Ensure(null, out refusal) && !string.IsNullOrEmpty(refusal), "unknown construction target refuses without reading or changing a game map");
            T.Check(!TestCompound.Validate(null, out refusal) && !string.IsNullOrEmpty(refusal), "unknown validation target never invents compound validity");
            string ensure = Body(compound, "public static bool Ensure(");
            T.Check(ensure.IndexOf("Refusal(", StringComparison.Ordinal) >= 0 && ensure.IndexOf("Refusal(", StringComparison.Ordinal) < ensure.IndexOf("GenSpawn.Spawn(", StringComparison.Ordinal), "compound checks owned scope before construction");
            T.Check(!Regex.IsMatch(compound, @"\b(?:GameComponent|WorldComponent|ExposeData|MakeWorldObject|AddHediff|PawnGenerator|NextId|CapturedBy)\s*\("), "compound introduces no parallel persistent world object or generated warden and cannot arrest by itself");
            string world = Source("Diagnostics/RuntimePhysicalTests/PhysicalTestWorld.cs");
            string site = Body(world, "public static Map Ensure(");
            T.Check(site.Contains("TestCompound.Ensure(") && site.Contains("new IntVec3(MapSize, 1, MapSize)"), "ordinary TestSite creation uses the bounded map and shared compound");
        }

        private static void StructureWiring()
        {
            string compound = Source("Diagnostics/RuntimePhysicalTests/TestCompound.cs");
            foreach (string token in new[] { "Building_Bed", "ForPrisoners", "IsPrisonCell", "ProperRoom", "TouchesMapEdge", "RoomCanBePrisonCell", "regionAndRoomUpdater" })
                T.Check(compound.Contains(token), "compound checks audited real vanilla prison fact: " + token);
            string ensure = Body(compound, "public static bool Ensure(");
            T.Check(ensure.Contains("Thing existing = Existing(map, piece)") && compound.Contains("ThingDefOf.Wall") && compound.Contains("ThingDefOf.Door"), "existing walls and doors are identified before creating ordinary Things");
            T.Check(ensure.IndexOf("if (Validate(map, out report)) return true;", StringComparison.Ordinal) >= 0
                && ensure.IndexOf("if (Validate(map, out report)) return true;", StringComparison.Ordinal) < ensure.IndexOf("foreach (IntVec3 cell in ConstructionCells(map.Center))", StringComparison.Ordinal),
                "valid existing compound returns before clearing, spawning or changing bed owners");
            int preflight = ensure.IndexOf("if (thing is Pawn)", StringComparison.Ordinal), write = ensure.IndexOf("map.roofGrid.SetRoof(", StringComparison.Ordinal);
            T.Check(preflight >= 0 && write > preflight && ensure.IndexOf("if (!NaturalObstacle(thing))", StringComparison.Ordinal) < write,
                "whole bounded footprint refuses Pawns and foreign Things before the first terrain, roof or destruction write");
            T.Check(ensure.Contains("if (thing == null)") && ensure.Contains("Thing thing = Existing(map, piece);"), "valid existing structures are reused instead of duplicated or replaced");
            string matches = Body(compound, "private static bool Matches(");
            T.Check(matches.Contains("(thing is Building_Door || thing.Rotation == Rot4.North)")
                && matches.Contains("thing.def == piece.def") && matches.Contains("thing.Stuff == piece.stuff")
                && matches.Contains("thing.Position == piece.cell") && matches.Contains("thing.Faction == Faction.OfPlayer"),
                "vanilla door auto-orientation does not invalidate an otherwise exact door; beds and other structures still require their planned rotation");
            string validate = Body(compound, "public static bool Validate(");
            T.Check(validate.Contains("IsPlannedBedCell(map.Center, cell) ? cell.Walkable(map) : cell.Standable(map)")
                && validate.IndexOf("if (Existing(map, piece) == null)", StringComparison.Ordinal) < validate.IndexOf("IsPlannedBedCell(", StringComparison.Ordinal),
                "verified real bed footprints allow vanilla PassThroughOnly walkability; free prison cells remain standable");
            string courtyard = Body(validate, "foreach (IntVec3 cell in VisitorCells(map.Center))");
            T.Check(courtyard.Contains("!cell.Standable(map)"), "bed passability exception never weakens ordinary visitor cells");
            T.Check(compound.Contains("CellRect") && compound.Contains("Pawn") && compound.Contains("Refusal("), "bounded footprint validates occupied cells before changing them");
            T.Check(!Regex.IsMatch(compound, @"\b(?:escapeInterval|prisonBreakMtbDays|nextPrisonBreak|SetFactionDirect|DeinitAndRemoveMap|Discard)\b"), "fixture uses no escape timer, ownership spoof, world-map removal or Pawn discard to force custody");
            string prepare = Body(compound, "public static bool TryPreparePrisoner(");
            string claim = Body(compound, "public static bool TryClaimPrisonerBed(");
            string anonymous = Body(compound, "private static string OwnedAnonymousRefusal(");
            T.Check(prepare.Contains("OwnedAnonymousRefusal(") && anonymous.Contains("PhysicalTestSession.IsActiveOwnedEpisode(") && anonymous.Contains("member.IsNamed"), "bed preparation restricts the exact active anonymous Episode visitor");
            T.Check(claim.Contains("IsPrisonerOfColony") && claim.Contains("ClaimBedIfNonMedical"), "after real capture the legitimate prisoner claims an actual prisoner bed");
            T.Check(prepare.Contains("candidate.OwnersForReading.Count != 0") && claim.IndexOf("foreach (Pawn owner in bed.OwnersForReading)", StringComparison.Ordinal) >= 0
                && claim.IndexOf("foreach (Pawn owner in bed.OwnersForReading)", StringComparison.Ordinal) < claim.IndexOf("ClaimBedIfNonMedical(", StringComparison.Ordinal),
                "second custody fixture selects an unowned bed and refuses another captive's ownership before claiming");
            T.Check(!Regex.IsMatch(prepare + claim, @"\b(?:UnclaimBed|Notify_TuckedIntoBed|CapturedBy)\s*\("), "custody setup does not evict another captive or repeatedly recapture");
        }

        private static void PendingFacts()
        {
            T.Check(GroupQaRules.PendingCaptureHolds(Facts(), true), "intact real-custody pending window is valid");
            T.Check(!GroupQaRules.PendingCaptureHolds(null, true) && !GroupQaRules.CaptureCustodyHolds(null), "unknown custody observations fail closed");
            for (int i = 0; i < 17; i++)
                T.Check(!GroupQaRules.PendingCaptureHolds(Facts(Broken(i)), true), "every named pending custody prerequisite is mandatory: " + i);
        }

        private static void PeerBoundary()
        {
            T.Check(!GroupQaRules.PendingCaptureHolds(Facts(Broken(15)), true), "a Pending peer is required before deliberate peer exit");
            T.Check(GroupQaRules.PendingCaptureHolds(Facts(Broken(15)), false), "only deliberate peer exit permits the pending-peer predicate to relax");
            for (int i = 0; i < 17; i++) if (i != 15)
                T.Check(!GroupQaRules.PendingCaptureHolds(Facts(Broken(i, 15)), false), "after peer exit every remaining prerequisite stays mandatory: " + i);
        }

        private static void CustodyAgreement()
        {
            T.Check(!GroupQaRules.PendingCaptureHolds(Facts(Broken(8)), true), "a stale held observation never overrides actual escaped prisoner state");
            T.Check(!GroupQaRules.PendingCaptureHolds(Facts(Broken(9)), true), "actual prisoner alone never overrides wrong or unknown adapter kind/holder");
            T.Check(!GroupQaRules.CaptureCustodyHolds(Facts(Broken(8))) && !GroupQaRules.CaptureCustodyHolds(Facts(Broken(9))), "both real and positively classified player-prisoner custody remain required after terminal batch");
        }

        private static void TerminalBoundary()
        {
            GroupPendingCaptureFacts promoted = Facts(Broken(11, 12, 13, 14, 15));
            T.Check(!GroupQaRules.PendingCaptureHolds(promoted, false), "completed named identity is not misreported as a still-anonymous pending fixture");
            T.Check(GroupQaRules.CaptureCustodyHolds(promoted), "legitimate atomic identity and reservation handoff retains actual physical custody");
            foreach (int i in Enumerable.Range(0, 11).Concat(new[] { 16 }))
                T.Check(!GroupQaRules.CaptureCustodyHolds(Facts(Broken(i, 11, 12, 13, 14, 15))), "named terminal handoff never relaxes physical ownership/binding/custody/conservation: " + i);
        }

        private static void FailureLatch()
        {
            T.Check(!GroupQaRules.CaptureFailureLatched(false, true), "valid prerequisite does not invent failure");
            bool failed = GroupQaRules.CaptureFailureLatched(false, false);
            T.Check(failed && GroupQaRules.CaptureFailureLatched(failed, true), "one escaped custody frame remains failed even after later recovery");
            T.Check(GroupQaRules.CaptureFailureLatched(true, false), "another bad observation cannot erase prior failure");
        }

        private static void ReadOnlyPolicy()
        {
            FieldInfo[] fields = typeof(GroupPendingCaptureFacts).GetFields(BindingFlags.Instance | BindingFlags.Public);
            T.Eq(17, fields.Length, "all explicit capture prerequisites are named observation facts");
            T.Check(fields.All(f => f.IsInitOnly && f.FieldType == typeof(bool)), "capture facts are immutable read-only bool data");
            GroupPendingCaptureFacts facts = Facts();
            bool[] before = fields.Select(f => (bool)f.GetValue(facts)).ToArray();
            GroupQaRules.PendingCaptureHolds(facts, true);
            GroupQaRules.CaptureCustodyHolds(facts);
            T.Check(before.SequenceEqual(fields.Select(f => (bool)f.GetValue(facts))), "custody policies leave every observed prerequisite unchanged");
            string rules = Source("Diagnostics/RuntimePhysicalTests/PhysicalGroupTestRules.cs");
            foreach (string signature in new[] { "public static bool PendingCaptureHolds(", "public static bool CaptureCustodyHolds(", "public static bool CaptureFailureLatched(" })
                T.Check(!Regex.IsMatch(Body(rules, signature), @"\b(?:Verse|RimWorld|Pawn|CapturedBy|Reconcile|NextId|GenSpawn|SetGuestStatus|TickManager)\b"), "pure custody policy cannot repair fixture or mutate lifecycle: " + signature);
        }

        private static void EscapeModel()
        {
            TestNet net = new TestNet(32517);
            NetworkActor actor = new NetworkActor { id = new ActorId(net.ids.NextId()), kind = ActorKind.Organization, seed = 123,
                foundedTick = net.clock.Now, name = NameSnapshot.Org("Custody prerequisite fixture") };
            actor.Add(new ContractorProfile { specialties = new List<string> { "escort", "medical" } });
            actor.Add(new ContractorSimulation { skill = 0.7f });
            OrganizationProfile org = new OrganizationProfile { capacity = 32 };
            org.tiers.Add(new TierCount(Tier.Regular, 15));
            KnownCharacter leader = new KnownCharacter { id = new CharacterId(net.ids.NextId()), org = actor.id, role = CharacterRole.Leader,
                opRole = OperationalRole.Leader, name = NameSnapshot.Person("Existing", null, "Leader") };
            org.leader = leader.id;
            org.knownMembers.Add(leader.id);
            actor.Add(org);
            net.ctx.actors.Add(actor);
            net.ctx.characters.Add(leader);
            PhysicalEpisode e;
            EpisodeRequest request = new EpisodeRequest { actor = actor.id, purposeKey = "CustodyGuardModel", mapId = 74,
                where = new TileRef { tileId = 20 }, cause = new EpisodeCause { devKey = PhysicalTestIds.DevKey("guard-model", "RT-PHYX-029") } };
            T.Check(net.ctx.Lifecycle.PlanGroup(request, new[] { new RoleCapacity(OperationalRole.Rifleman, 3) }, null, out e).ok, "company pending-capture model planned");
            T.Eq(3, net.ctx.Lifecycle.Materialize(e), "three anonymous company Riflemen placed");
            EpisodeMember captive = e.members[0];
            PawnRef originalBinding = captive.pawn;
            FakePhysicalWorldPort.Token token = net.physical.TokenOf(originalBinding);
            int count = net.ctx.characters.Count, humans = ContractorService.Headcount(actor, net.ctx.characters);
            net.physical.Hold(originalBinding, ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner);
            for (int watch = 0; watch < 2; watch++)
            {
                net.clock.Now += PhysicalLifecycleService.WatchPeriod;
                T.Check(!net.ctx.Lifecycle.Reconcile(e, "headless custody guard watch"), "ordinary peers keep terminal batch pending through watch " + watch);
                T.Check(GroupQaRules.PendingCaptureHolds(ModelFacts(net, actor, e, captive, originalBinding, token, count, humans), true), "actual fake physical/custody truth keeps all pending prerequisites through watch " + watch);
            }
            net.physical.Free(originalBinding, 20);
            PhysicalObservation escaped = net.physical.Observe(originalBinding, e.id);
            T.Eq(ObservedKind.WorldFree, escaped.kind, "escape reports world-free physical truth rather than invented captivity");
            bool failure = GroupQaRules.CaptureFailureLatched(false, GroupQaRules.PendingCaptureHolds(ModelFacts(net, actor, e, captive, originalBinding, token, count, humans), true));
            T.Check(failure && !captive.IsNamed && !e.consequencesApplied && e.members.Skip(1).All(m => m.outcome == MemberOutcome.Pending), "guard detects lost custody before any peer resolves or identity commits");
            T.Eq(count, net.ctx.characters.Count, "failed runtime prerequisite creates no early Character");
            T.Eq(humans, ContractorService.Headcount(actor, net.ctx.characters), "failure reporting conserves humans");
            T.Check(ReferenceEquals(token, net.physical.TokenOf(captive.pawn)) && ReferenceEquals(captive.pawn, originalBinding), "failed prerequisite preserves same token and durable binding for inspection");
            token.spawned = true;
            token.inWorldPawns = false;
            net.physical.Hold(originalBinding, ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner);
            T.Check(GroupQaRules.PendingCaptureHolds(ModelFacts(net, actor, e, captive, originalBinding, token, count, humans), true), "later positive fake custody can recover physical state");
            T.Check(GroupQaRules.CaptureFailureLatched(failure, true), "a recovered physical state cannot turn this failed harness run into PASS");
        }

        // Test token facts verify the policy/lifecycle boundary; they do not simulate real vanilla prison rooms or escape AI.
        private static GroupPendingCaptureFacts ModelFacts(TestNet net, NetworkActor actor, PhysicalEpisode e, EpisodeMember member,
            PawnRef binding, FakePhysicalWorldPort.Token original, int count, int humans)
        {
            FakePhysicalWorldPort.Token token = net.physical.TokenOf(member.pawn);
            PhysicalObservation observation = net.physical.Observe(member.pawn, e.id);
            return new GroupPendingCaptureFacts(GroupQaRules.OwnedByRun(e, "guard-model", "RT-PHYX-029") && ReferenceEquals(net.ctx.episodes.Get(e.id), e),
                e.members.Contains(member) && member.slot == 0, ReferenceEquals(original, token), ReferenceEquals(member.pawn, binding),
                binding.thingIdNumber > 0 && token.thingId == binding.thingIdNumber, member.seatRole == OperationalRole.Rifleman,
                token.mapId == e.whereMapId, token.spawned && !token.dead && !token.gone, token.held,
                observation.kind == ObservedKind.HeldByPlayer && observation.holder == HeldKind.PlayerPrisoner,
                token.registryReserves, token.temporaryEpisode == e.id && !e.releaseApplied, !member.IsNamed, net.ctx.characters.Count == count,
                e.state == EpisodeState.Open && !e.consequencesApplied && !e.releaseApplied,
                e.members.Any(m => m != member && m.outcome == MemberOutcome.Pending && net.physical.TokenOf(m.pawn)?.spawned == true),
                ContractorService.Headcount(actor, net.ctx.characters) == humans);
        }

        private static void GuardWiring()
        {
            string source = Source("Diagnostics/RuntimePhysicalTests/PhysicalGroupScenarios.cs");
            string pending = Body(source, "protected StepResult CheckPendingCapture(");
            int guard = pending.IndexOf("GuardCapture(", StringComparison.Ordinal), wait = pending.IndexOf("StepResult.Wait", StringComparison.Ordinal);
            int reconcile = pending.IndexOf("lc.Reconcile(", StringComparison.Ordinal);
            T.Check(guard >= 0 && wait > guard && reconcile > guard && pending.LastIndexOf("GuardCapture(", StringComparison.Ordinal) > reconcile, "capture prerequisite checked before waiting or reconciling and again after reconcile");
            string observe = Body(source, "private void ObserveCaptureProtection(");
            T.Check(observe.Contains("ObserveProtection(") && observe.Contains("GuardCapture("), "every frame preserves existing reservation sentinel and the real-custody guard");
            string guardBody = Body(source, "private bool GuardCapture(");
            T.Check(guardBody.Contains("CaptureFailureLatched(") && guardBody.Contains("PendingCaptureHolds(") && guardBody.Contains("CaptureCustodyHolds("), "actual guard uses irreversible failure and separates legitimate terminal handoff from pending facts");
            T.Check(!Regex.IsMatch(guardBody + observe, @"\b(?:CapturedBy|TryPreparePrisoner|TryClaimPrisonerBed|SetGuestStatus|GenSpawn|NextId|AddHediff)\s*\("), "guard only observes; it cannot repeatedly arrest, repair, move or invent identities");
            string fail = Body(source, "private void FailCapture(");
            T.Check(fail.Contains("if (captureFailed) return;") && Regex.Matches(fail, @"\bv\.Fail\s*\(").Count == 1,
                "one custody failure emits one diagnostic and preserves the irreversible latch");
            foreach (string signature in new[] { "protected StepResult ExitCapturePeers(", "protected StepResult WaitCapturedGroup(" })
            {
                string body = Body(source, signature);
                T.Check(body.Contains("GuardCapture(") && body.Contains("StepResult.Abort"), "latched custody failure stops the next harness action: " + signature);
            }
        }

        private static void SharedFixtureWiring()
        {
            string source = Source("Diagnostics/RuntimePhysicalTests/PhysicalGroupScenarios.cs");
            string capture = Body(source, "public class GroupCaptureRun : GroupRun");
            string arrest = Body(capture, "protected StepResult ArrestAnonymous(");
            int prepare = arrest.IndexOf("TryPreparePrisoner(", StringComparison.Ordinal), realCapture = arrest.IndexOf("CapturedBy(Faction.OfPlayer)", StringComparison.Ordinal);
            int claim = arrest.IndexOf("TryClaimPrisonerBed(", StringComparison.Ordinal);
            T.Check(prepare >= 0 && realCapture > prepare && claim > realCapture, "one audited shared fixture prepares a real cell before capture and claims the actual bed afterward");
            T.Eq(1, Regex.Matches(capture, @"\bCapturedBy\s*\(").Count, "capture occurs once, never repeated during waiting/checks");
            T.Check(arrest.Contains("everyFrame = ObserveCaptureProtection"), "sustained custody observation starts immediately after legitimate arrest");
            string scenario = Body(source, "public sealed class Phyx029LargeCapture : GroupCaptureRun");
            string save = Body(source, "public sealed class Phyx030GroupSave : GroupCaptureRun");
            T.Check(scenario.Contains("ArrestAnonymous") && save.Contains("ArrestAnonymous") && scenario.Contains("CheckPendingCapture") && save.Contains("CheckPendingCapture"), "029 and 030B use the same real arrest and pending-custody setup");
            T.Check(scenario.Contains("ExitCapturePeers") && scenario.Contains("WaitCapturedGroup"), "029 protects custody across peer exit and terminal wait");
            T.Check(save.IndexOf("GuardPendingCapture()", StringComparison.Ordinal) >= 0
                && save.IndexOf("GuardPendingCapture()", StringComparison.Ordinal) < save.IndexOf("Find.TickManager.Pause()", StringComparison.Ordinal),
                "030B refuses its SAVE checkpoint if custody was lost after the pending wait");
            string loaded = Body(source, "public sealed class Phyx030GroupVerify : PhysicalRun");
            T.Check(loaded.Contains("IsPrisonerOfColony") && loaded.Contains("ObservedKind.HeldByPlayer") && loaded.Contains("HeldKind.PlayerPrisoner"), "030V reads actual pending prisoner custody after load");
            T.Check(!Regex.IsMatch(loaded, @"\b(?:CapturedBy|TryClaimPrisonerBed|TryPreparePrisoner|Reconcile|PlanGroup|Materialize|Destroy|Discard)\s*\("), "loaded verification cannot repair prison fixture or drive saved Episode outcomes");
            string loadedGuard = Body(loaded, "private bool GuardLoadedCapture(");
            T.Check(loadedGuard.Contains("PendingCaptureHolds(facts, false)") && loadedGuard.Contains("CaptureFailureLatched(")
                && !loadedGuard.Contains("IsActiveOwnedEpisode("), "loaded custody follows exact durable saved truth without requiring the cleared current-session arm or artificially preserving Pending peers");
        }
    }
}
