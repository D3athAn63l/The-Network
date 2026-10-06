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
        protected OrganizationProfile org;
        protected readonly List<Pawn> groupPawns = new List<Pawn>();
        private readonly HashSet<int> namedBeforePlacement = new HashSet<int>();
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
            foreach (EpisodeMember member in e.members) if (member.IsNamed) namedBeforePlacement.Add(member.character.Value);
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
            v.Check(shared != null && shared.temporary && shared.def.hidden, "one valid hidden temporary encounter faction for the group");
            foreach (EpisodeMember member in e.members)
            {
                Pawn pawn = member.pawn?.pawn;
                groupPawns.Add(pawn);
                KnownCharacter known = member.IsNamed ? ctx.characters.Get(member.character) : null;
                if (known != null) testPeople.Add(known.id.Value);
                v.Check(pawn != null && pawn.Spawned && pawn.Map == map && pawn.Faction == shared, "seat " + member.slot + ": same shared faction and successful placement on owned map");
                v.Check(pawn != null && port.Registry.Reserves(pawn), "seat " + member.slot + ": protected before and during placement");
                v.Check(known == null || known.opRole == member.seatRole, "seat " + member.slot + ": persisted operational role matches mission role");
                if (pawn != null) v.Check(RoleRules.Verify(RoleRules.SpecFor(member.seatRole, ContractorService.Experience(a)), PawnRoleReader.Snapshot(pawn)).holds,
                    "seat " + member.slot + ": returned Pawn satisfies " + member.seatRole + " without social-history modification");
                if (!member.IsNamed) v.Check(synthetic ? member.playerVisibleTick >= 0 : member.playerVisibleTick == -1,
                    "seat " + member.slot + ": " + (synthetic ? "synthetic visible placement is latched" : "ordinary dev-map presence is not invented as P0"));
                if (!member.IsNamed) v.Check(port.Registry.IsTemporaryReserved(pawn), "anonymous seat " + member.slot + ": Episode temporary reservation, no dummy person");
            }
            p = groupPawns.Count > 0 ? groupPawns[0] : null;
            v.Check(ctx.characters.characters.Count == charactersBefore, "Plan and placement create zero KnownCharacters; promotion waits for terminal commit");
            v.Check(ContractorService.Headcount(a, ctx.characters) == humanBefore, "checkout conserves total living humans");
            everyFrame = ObserveProtection;
            return StepResult.Next;
        }

        protected void ObserveProtection()
        {
            if (e == null) return;
            for (int i = 0; i < e.members.Count && i < groupPawns.Count; i++)
            {
                Pawn pawn = groupPawns[i];
                if (pawn == null || pawn.Dead || pawn.Discarded) continue;
                if (!e.releaseApplied && !port.Registry.Reserves(pawn)) reservationGaps++;
                if (!e.releaseApplied && IsActualFree(pawn)) freeFrames++;
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
        protected GroupCaptureRun(string family, NetworkRuntime rt, string runId) : base(family, rt, runId) { }

        protected StepResult ArrestAnonymous()
        {
            capturedMember = e.members[0];
            captive = capturedMember.pawn?.pawn;
            if (capturedMember.IsNamed || captive == null || !captive.Spawned || !TestSite.IsTestMap(captive.Map))
            { v.Fail("arrest requires this run's anonymous placed member"); return StepResult.Abort; }
            captive.guest.CapturedBy(Faction.OfPlayer);
            arrestedTick = PhysLog.Tick;
            v.Check(captive.IsPrisonerOfColony, "real vanilla CapturedBy made the anonymous Pawn a colony prisoner on the owned map");
            return StepResult.Next;
        }

        protected StepResult CheckPendingCapture()
        {
            if (PhysLog.Tick - arrestedTick < 2 * PhysicalLifecycleService.WatchPeriod) return StepResult.Wait;
            lc.Reconcile(e, "owned QA pending-peer check");
            bool peers = false;
            foreach (EpisodeMember m in e.members) if (m != capturedMember && m.outcome == MemberOutcome.Pending && m.pawn?.pawn?.Spawned == true) peers = true;
            v.Check(peers && !e.consequencesApplied && e.state == EpisodeState.Open, "peers remain Pending; whole-Episode terminal commit has not occurred");
            v.Check(ctx.characters.characters.Count == charactersBefore && !capturedMember.IsNamed, "zero early CharacterStore identity/custody commit for the arrested anonymous member");
            v.Check(ReferenceEquals(captive, capturedMember.pawn?.pawn) && !captive.Discarded && port.Registry.IsTemporaryReserved(captive), "the SAME arrested Pawn remains protected by its durable Episode slot");
            v.Check(ContractorService.Headcount(a, ctx.characters) == humanBefore, "pending capture neither invents nor subtracts a human");
            return peers && !e.consequencesApplied ? StepResult.Next : StepResult.Abort;
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
            Then("return only ordinary peers; leave the captive untouched", () => ExitPeers(captive));
            Then("wait for whole-Episode atomic promotion", WaitGroup, 20000);
            Then("check exactly one same-Pawn held identity", () => { CheckPromotion(); return StepResult.Next; });
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
        public Phyx030GroupVerify(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-030"), runId, rt) { }
        protected override void Script()
        {
            Then("verify loaded group bindings and reservation categories", () => { VerifyLoaded(); return StepResult.Next; });
            Then("follow a saved incomplete Episode read-only; vanilla peers leave naturally", () =>
            {
                if (!follow || selected == null) return StepResult.Next;
                if (selected.state == EpisodeState.Quarantined) { v.Fail("saved group quarantined: " + selected.quarantineKey); return StepResult.Abort; }
                return selected.IsComplete ? StepResult.Next : StepResult.Wait;
            }, 60000);
            Then("verify terminal truth and all retained group identities", () => { VerifyTerminal(); return StepResult.Next; });
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
