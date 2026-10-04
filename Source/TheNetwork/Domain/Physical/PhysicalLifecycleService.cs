using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Operations;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;

namespace TheNetwork.Domain.Physical
{
    /// <summary>What a caller asks an episode to be (PHYSICAL_LIFECYCLE § 5.3). In Phase 3.0 only tests and the sandbox ask.</summary>
    public sealed class EpisodeRequest
    {
        public ActorId actor;
        public string purposeKey;
        public EpisodeCause cause = new EpisodeCause();

        /// <summary>Named people (Known Characters of the actor).</summary>
        public List<CharacterId> named = new List<CharacterId>();

        /// <summary>Anonymous slots by tier (an organization's headcount; never a fake record).</summary>
        public List<TierCount> anonymous = new List<TierCount>();

        public TileRef where;
        public int mapId = -1;
    }

    /// <summary>Lifecycle counters (runtime diagnostics; never persisted).</summary>
    public sealed class LifecycleCounters
    {
        public int planned;
        public int refusedPlans;
        public int created;
        public int rematerialized;
        public int placed;
        public int unresolvedBindings;
        public int materializeFaults;
        public int wakeups;
        public int duplicateWakeups;
        public int deferredWakeups;
        public int blindWakeups;
        public int commits;
        public int commitFailures;
        public int invalidPlans;
        public int quarantined;
        public int releaseActions;
        public int passedToWorld;
        public int passSkippedAlreadyWorld;
        public int passRefused;
        public int releasesCompleted;
        public int followUps;
        public int published;
        public int publishFaultsAfterAccept;
        public int stageFailures;
        public int completed;
        public int settled;
        public int signalWakeups;

        /// <summary>A placement that did not report success but left the bound pawn physically present (spawned or dead): treated as Present.</summary>
        public int placementRecovered;

        /// <summary>A bound pawn positively gone after binding (discarded): resolved through the existing Lost semantics, never NeverPlaced.</summary>
        public int placementLost;

        /// <summary>A bound pawn held by a vanilla owner or unobservable after a placement attempt: the episode was quarantined, nothing invented.</summary>
        public int placementFailClosed;

        /// <summary>A truthful-aging step threw (its progress is not provable): the episode was quarantined and nothing was replayed.</summary>
        public int agingUncertain;

        /// <summary>A retained named person observed as an actual Free world pawn (M1 failed): the episode was quarantined, never Returned.</summary>
        public int reservationBroken;

        public override string ToString()
        {
            return "planned " + planned + " (refused " + refusedPlans + "), created " + created + ", rematerialized " + rematerialized + ", placed " + placed
                + " (recovered " + placementRecovered + ", lost " + placementLost + ", failed closed " + placementFailClosed + ")"
                + ", aging uncertain " + agingUncertain + ", reservation broken " + reservationBroken
                + ", wake-ups " + wakeups + " (duplicate " + duplicateWakeups + ", deferred " + deferredWakeups + ", blind " + blindWakeups + ")"
                + ", commits " + commits + " (failed " + commitFailures + ", invalid " + invalidPlans + "), quarantined " + quarantined
                + ", release actions " + releaseActions + " (passed " + passedToWorld + ", already world " + passSkippedAlreadyWorld + ", refused " + passRefused + ")"
                + ", releases " + releasesCompleted + ", follow-ups " + followUps + ", published " + published + ", stage failures " + stageFailures
                + ", completed " + completed + ", settled " + settled + ", signal wake-ups " + signalWakeups;
        }
    }

    /// <summary>
    /// The abstract ↔ physical lifecycle brain (PHYSICAL_LIFECYCLE § 8, § 15): Physical Episodes, materialization over the
    /// <see cref="IPhysicalWorldPort"/>, and reconciliation as OBSERVE → DECIDE → PLAN → VALIDATE → ATOMIC DURABLE COMMIT →
    /// RELEASE → FOLLOW-UP → PUBLISH, exactly once. Each post-commit stage has its own explicit durable marker
    /// (<c>releaseApplied</c>, <c>followUpApplied</c>, the outbox cursor and <c>publishedTick</c>) and is resumed from it, never
    /// inferred from side-effect state.
    ///
    /// Phase 3.1: the live game holds the real adapter (<c>Integration/Physical/RimWorldPhysicalWorldPort</c>); the ONLY production caller
    /// of <see cref="Plan"/> and <see cref="Materialize"/> is the session-armed physical test tier (a dev trigger, § 22.1), so ordinary
    /// gameplay creates no episode and no job runs: zero idle cost. Tests and the sandbox drive this same service over a scriptable fake
    /// port. A retained named pawn is covered by the registry reservation from its binding on (M1): the reservation already exists when
    /// vanilla passes it into WorldPawns, and RELEASE only proves it.
    /// </summary>
    public sealed class PhysicalLifecycleService
    {
        public const string WatchJob = "episode.watch";

        /// <summary>The Open-episode watch (§ 15.1, § 18.2): one job per incomplete episode, every 250 ticks.</summary>
        public const int WatchPeriod = 250;

        /// <summary>After the bounded retries an episode is watched slowly (it stays reported, never auto-resolved).</summary>
        public const int SlowWatchPeriod = 2500;

        /// <summary>The quarantine key prefixes the correction pass added (the persisted <c>quarantineKey</c> string; no schema change).</summary>
        public const string QuarantinePlacement = "PlacementUnresolved";
        public const string QuarantineReservation = "ReservationBroken";
        public const string QuarantineAge = "AgeTruthUncertain";

        /// <summary>
        /// A quarantine no watch, load or wake-up resolves by observing the world: a person whose AGE TRUTH is uncertain cannot be made certain by
        /// looking at a pawn (§ 6.4). It is reported and kept; clearing it is an owner decision, never an inference.
        /// </summary>
        public static bool IsHardQuarantine(string key)
        {
            return key != null && key.StartsWith(QuarantineAge, StringComparison.Ordinal);
        }

        /// <summary>Group size bound (§ 5.1, § 18.2).</summary>
        public const int MaxMembers = 8;

        /// <summary>The long-open warning (§ 15.2): reported, never auto-closed.</summary>
        public const int LongOpenTicks = 30 * Ticks.PerDay;

        private readonly DomainContext ctx;

        /// <summary>Runtime re-entrancy guard (§ 15.2 step 0): a wake-up that arrives mid-commit or mid-stage is queued.</summary>
        private bool busy;

        private readonly List<EpisodeId> deferred = new List<EpisodeId>();

        public readonly LifecycleCounters counters = new LifecycleCounters();

        // ------------------------------------------------------------------ test-only hooks (runtime only; never persisted, never set by production)

        /// <summary>Throw inside the next commit after this many of its steps (§ 15.7). One-shot.</summary>
        public int commitFaultAfter = -1;

        /// <summary>Interrupt the next PUBLISH after this many accepted specs (an interruption, as a load would). One-shot.</summary>
        public int publishInterruptAfter = -1;

        /// <summary>Called with "before" right before the commit's first assignment and "restored" right after a failed commit was restored.</summary>
        public Action<string> commitBoundary;

        public PhysicalLifecycleService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        private IPhysicalWorldPort Port => ctx.physicalPort;

        private bool PortAvailable => ctx.physicalPort != null && ctx.physicalPort.Available;

        // ================================================================== PLAN (abstract → physical, step 1)

        /// <summary>
        /// Creates a Planned episode (§ 8.1): chooses its members and takes them out of abstract authority (named custody
        /// Deployed + the exclusive membership link; anonymous headcount checked out to it; a linked operation handed over).
        /// Refused, with nothing changed, unless every person is abstractly simulatable right now (P3-INV-001: nobody can be in
        /// two episodes, or in an episode and an operation). Refused outright in a live 3.0 game (no physical port).
        /// </summary>
        public CommandResult Plan(EpisodeRequest r, out PhysicalEpisode episode)
        {
            episode = null;
            CommandResult check = CheckPlan(r);
            if (!check.ok)
            {
                counters.refusedPlans++;
                return check;
            }
            NetworkActor a = ctx.actors.Get(r.actor);
            OrganizationProfile org = a.Get<OrganizationProfile>();
            Operation op = r.cause.operation.IsValid ? ctx.operations.Get(r.cause.operation) : null;
            List<KnownCharacter> people = new List<KnownCharacter>();
            for (int i = 0; i < r.named.Count; i++) people.Add(ctx.characters.Get(r.named[i]));

            DurableSnapshot snapshot = new DurableSnapshot();
            for (int i = 0; i < people.Count; i++) snapshot.Capture(people[i]);
            snapshot.Capture(a).Capture(op).Capture(ctx.ids);
            PhysicalEpisode e = null;
            try
            {
                e = new PhysicalEpisode
                {
                    id = new EpisodeId(ctx.ids.NextId()),
                    actor = a.id,
                    purposeKey = r.purposeKey,
                    cause = new EpisodeCause { contract = r.cause.contract, operation = r.cause.operation, opportunity = r.cause.opportunity, devKey = r.cause.devKey },
                    state = EpisodeState.Planned,
                    createdTick = ctx.Now,
                    whereTile = r.where?.Copy(),
                    whereMapId = r.mapId
                };
                e.seed = NetHash.Combine(NetHash.Combine(a.seed, "episode"), e.id.Value);
                int slot = 0;
                for (int i = 0; i < people.Count; i++)
                {
                    KnownCharacter c = people[i];
                    // A Solo's operational role, stored lazily from IMMUTABLE origin facts (§ 6.6.5, P3-INV-030): the same value whenever it
                    // is first needed. An organization's people get theirs with composition (3.2).
                    if (c.opRole == OperationalRole.Unset && a.bindings.embodies == c.id) c.opRole = RoleDerivation.ForSolo(a);
                    e.members.Add(new EpisodeMember { character = c.id, slot = slot++, tier = Tier.Regular, seatRole = c.opRole, pawn = c.pawn?.Copy() });
                    c.custody = CustodyState.Deployed;
                    c.episode = e.id;
                }
                for (int i = 0; i < r.anonymous.Count; i++)
                {
                    TierCount t = r.anonymous[i];
                    for (int k = 0; k < t.healthy; k++) e.members.Add(new EpisodeMember { slot = slot++, tier = t.tier });
                    if (t.healthy <= 0) continue;
                    org.TierOf(t.tier).healthy -= t.healthy;
                    org.TierOf(t.tier, true).healthy += t.healthy;
                }
                if (op != null)
                {
                    op.status = OpStatus.Physical;
                    op.physicalEpisode = e.id;
                    op.physicalResolution = PhysicalResolution.None;
                    op.physicalSteps = 0;
                }
                ctx.episodes.Add(e);
            }
            catch (Exception ex)
            {
                snapshot.Restore();
                if (e != null && ctx.episodes.Get(e.id) != null) ctx.episodes.Remove(e);
                counters.refusedPlans++;
                return CommandResult.Fail("PlanFailed", ex.Message);
            }
            // Runtime and scheduler effects, after the durable transition: the operation's own deadline is suspended while
            // its episode owns it (§ 15.4), the strength cache is recomputed, and the episode is watched.
            if (op != null) ctx.scheduler.Cancel(OperationService.TroubledJob, op.id.Value);
            a.Get<ContractorSimulation>()?.MarkDirty();
            EnsureWatch(e);
            counters.planned++;
            StateVersion.Bump();
            episode = e;
            return CommandResult.Ok;
        }

        private CommandResult CheckPlan(EpisodeRequest r)
        {
            if (ctx.episodes == null) return CommandResult.Fail("NoEpisodeStore");
            if (!PortAvailable) return CommandResult.Fail("PhysicalWorldUnavailable", "Phase 3.0 has no physical adapter; no contractor pawn is ever created");
            if (r == null) return CommandResult.Fail("NoRequest");
            NetworkActor a = ctx.actors.Get(r.actor);
            if (a == null || !a.IsActive) return CommandResult.Fail("ActorNotActive", r.actor.ToString());
            if (string.IsNullOrEmpty(r.purposeKey)) return CommandResult.Fail("NoPurpose");
            if (r.cause == null || !r.cause.IsValid) return CommandResult.Fail("NoCause");
            OrganizationProfile org = a.Get<OrganizationProfile>();
            // Anonymous headcount is validated per TIER, summed over every row of the request (two rows of one tier must not each
            // pass against the same headcount), and a negative row is malformed input, never clamped away (§ 5.1 conservation).
            Dictionary<Tier, int> anonymousByTier = new Dictionary<Tier, int>();
            int anonymous = 0;
            for (int i = 0; i < r.anonymous.Count; i++)
            {
                TierCount t = r.anonymous[i];
                if (t == null || t.healthy < 0) return CommandResult.Fail("Headcount", "a negative or missing anonymous row");
                // Bounded accumulation: no row and no running total may pass the episode's cap, so the sums cannot overflow.
                if (t.healthy > MaxMembers || anonymous + t.healthy > MaxMembers) return CommandResult.Fail("MemberCount", "more than " + MaxMembers + " anonymous members");
                int sum;
                anonymousByTier.TryGetValue(t.tier, out sum);
                anonymousByTier[t.tier] = sum + t.healthy;
                anonymous += t.healthy;
            }
            int total = r.named.Count + anonymous;
            if (total == 0 || total > MaxMembers) return CommandResult.Fail("MemberCount", total.ToString());
            Operation op = null;
            if (r.cause.operation.IsValid)
            {
                op = ctx.operations?.Get(r.cause.operation);
                if (op == null || op.IsFinished || op.status != OpStatus.Troubled || op.contractor != a.id) return CommandResult.Fail("OperationNotTroubled", r.cause.operation.ToString());
                if (anonymous > 0) return CommandResult.Fail("AnonymousWithOperation");
            }
            List<CharacterId> busyPeople = ctx.Contractors.Occupied(a, op != null ? op.id : OperationId.None);
            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < r.named.Count; i++)
            {
                KnownCharacter c = ctx.characters.Get(r.named[i]);
                if (c == null) return CommandResult.Fail("CharacterMissing", r.named[i].ToString());
                if (!seen.Add(c.id.Value)) return CommandResult.Fail("DuplicatePerson", c.id.ToString());
                if (c.org != a.id && a.bindings.embodies != c.id) return CommandResult.Fail("NotAMember", c.id.ToString());
                if (!c.IsAlive) return CommandResult.Fail("NotAlive", c.id.ToString());
                // ONE authority (P3-INV-001): someone in any episode (Planned, Open, or Closed with release pending) or held is refused.
                if (!AuthorityGate.CanSimulateAbstractly(c)) return CommandResult.Fail("AlreadyPhysical", c.id + " " + AuthorityGate.AuthorityOf(c));
                if (busyPeople.Contains(c.id)) return CommandResult.Fail("OnOperation", c.id.ToString());
                if (op != null)
                {
                    if (!op.characters.Contains(c.id)) return CommandResult.Fail("NotOnOperation", c.id.ToString());
                }
                else if (c.status != CharacterStatus.Active)
                {
                    return CommandResult.Fail("NotAvailable", c.id + " " + c.status);
                }
                bool bound = c.pawn != null && c.pawn.IsBound;
                if (bound != (c.custody == CustodyState.Stored)) return CommandResult.Fail("BindingCustodyMismatch", c.id + " " + c.custody);
            }
            if (anonymous > 0)
            {
                if (org == null) return CommandResult.Fail("NoHeadcount");
                foreach (KeyValuePair<Tier, int> kv in anonymousByTier)
                {
                    if (FateRules.PeekHealthy(org, kv.Key) < kv.Value) return CommandResult.Fail("Headcount", kv.Key + " " + kv.Value + " requested, " + FateRules.PeekHealthy(org, kv.Key) + " healthy");
                }
            }
            return CommandResult.Ok;
        }

        // ================================================================== MATERIALIZE (Planned → Open)

        /// <summary>
        /// Creates (first time) or rematerializes (the SAME binding, never regenerated: P3-INV-006) every member over the port and
        /// places them. A rematerialized person's pawn is first aged by the FULL interval since <c>agedThroughTick</c>, uncapped
        /// (§ 6.4, P3-INV-022). ≥ 1 placed ⇒ Open; none ⇒ Closed(NeverPlaced) through the same atomic commit.
        ///
        /// A member that could not be created (nothing was ever bound) stays unplaced and closes NeverPlaced (§ 17). A member that WAS bound but
        /// is not reported placed is never assumed NeverPlaced: once bound, its physical state is authoritative, so it is classified from positive
        /// observation (<see cref="SettleBoundButUnplaced"/>). A truthful-aging step that threw quarantines the episode (§ 6.4).
        /// </summary>
        public int Materialize(PhysicalEpisode e)
        {
            if (e == null || e.state != EpisodeState.Planned || !PortAvailable) return 0;
            int now = ctx.Now;
            int present = 0;
            // S10 (§ 13.2): the episode's own temporary encounter faction, created before anyone is bound or placed and recorded on the
            // episode (durable, declared in 3.0). No suitable faction ⇒ nobody is placed: the episode closes NeverPlaced.
            try
            {
                e.faction = Port.EnsureEncounterFaction(e.id, e.actor, e.faction, EncounterGoodwill(e.actor));
            }
            catch (Exception ex)
            {
                counters.materializeFaults++;
                e.lastError = NetScribe.Truncate("EncounterFaction: " + ex.Message, 300);
                NetLog.WarnOnce(LogCategory.Physical, "faction." + e.id.Value, "Episode " + e.id + ": no encounter faction could be made (" + ex.Message + "); nobody is placed.");
                CloseUnplaced(e);
                StateVersion.Bump();
                return 0;
            }
            for (int i = 0; i < e.members.Count; i++)
            {
                EpisodeMember m = e.members[i];
                try
                {
                    if (m.state == MemberState.Planned)
                    {
                        if (!Bind(e, m, now)) continue;
                        m.state = MemberState.Created;
                    }
                    if (m.state == MemberState.Created && Port.Place(m.pawn, e.id, e.whereTile, e.whereMapId, e.faction))
                    {
                        m.state = MemberState.Present;
                        counters.placed++;
                    }
                }
                catch (AgingUncertainException ex)
                {
                    // The age truth of a stored person is no longer provable: nobody is placed and nothing is replayed, closed or invented.
                    QuarantineAgeUncertain(e, m, ex);
                    StateVersion.Bump();
                    return present;
                }
                catch (Exception ex)
                {
                    counters.materializeFaults++;
                    e.lastError = NetScribe.Truncate("Materialize " + m + ": " + ex.Message, 300);
                }
                if (m.state == MemberState.Present) present++;
            }
            return FinishPlanned(e, present);
        }

        /// <summary>
        /// The end of a materialization (and of a Planned episode resolved at load): every member that is BOUND but not Present is classified
        /// from positive observation, never assumed NeverPlaced (<see cref="SettleBoundButUnplaced"/>); then the episode opens, closes
        /// NeverPlaced (nobody bound is physically anywhere) or is quarantined (a bound pawn is held or unobservable).
        /// </summary>
        private int FinishPlanned(PhysicalEpisode e, int present)
        {
            int now = ctx.Now;
            bool lost;
            string failClosed;
            present += SettleBoundButUnplaced(e, out lost, out failClosed);
            if (failClosed != null)
            {
                if (present > 0 && e.openedTick < 0) e.openedTick = now;
                Quarantine(e, QuarantinePlacement + ":" + failClosed);
                StateVersion.Bump();
                return present;
            }
            if (present > 0)
            {
                e.state = EpisodeState.Open;
                e.openedTick = now;
                EnsureWatch(e);
                StateVersion.Bump();
                // A bound pawn that is positively gone resolves through the ordinary observation (Gone ⇒ Lost) at once, so a Closed episode never
                // waits on a RELEASE for a pawn that no longer exists.
                if (lost) Reconcile(e, "placement");
                return present;
            }
            CloseUnplaced(e);
            StateVersion.Bump();
            return present;
        }

        /// <summary>
        /// THE bound-pawn rule (§ 7.3 rule 7): once a named pawn is bound, its physical state is authoritative, so a placement that did not report
        /// success (a false return, or a throw: <c>GenSpawn.Spawn</c> returns the pawn even when <c>SpawnSetup</c> discarded it) is never reduced
        /// to NeverPlaced on assumption. Each bound member that is not Present is observed and <see cref="PlacementRules"/> decides:
        /// spawned or dead ⇒ Present; positively gone ⇒ Present-to-be-observed-Gone, which the ordinary path resolves as Lost; alive,
        /// unspawned, undiscarded and held by nobody ⇒ stays unplaced (NeverPlaced); held or unobservable ⇒ <paramref name="failClosed"/>.
        /// Returns how many members became Present. Nothing is created and nothing is passed to the world here.
        /// </summary>
        private int SettleBoundButUnplaced(PhysicalEpisode e, out bool lost, out string failClosed)
        {
            lost = false;
            failClosed = null;
            int became = 0;
            for (int i = 0; i < e.members.Count; i++)
            {
                EpisodeMember m = e.members[i];
                if (m.state == MemberState.Present || !m.IsBound) continue;
                PlacementVerdict v = ClassifyBound(m, e.id);
                switch (v.kind)
                {
                    case PlacementVerdictKind.Present:
                        m.state = MemberState.Present;
                        became++;
                        counters.placed++;
                        counters.placementRecovered++;
                        NetLog.Warn(LogCategory.Physical, "Episode " + e.id + ": placement of " + m + " did not report success, but the bound pawn is physically there (" + v.detail
                            + "): treated as Present, nothing regenerated and nothing closed as NeverPlaced.");
                        break;
                    case PlacementVerdictKind.Lost:
                        m.state = MemberState.Present;
                        became++;
                        lost = true;
                        counters.placementLost++;
                        NetLog.Warn(LogCategory.Physical, "Episode " + e.id + ": the bound pawn of " + m + " is gone after binding (" + v.detail
                            + "): resolved as Lost by the ordinary observation; never NeverPlaced, never regenerated.");
                        break;
                    case PlacementVerdictKind.FailClosed:
                        counters.placementFailClosed++;
                        if (failClosed == null) failClosed = v.detail + " " + m;
                        break;
                }
            }
            return became;
        }

        /// <summary>Observes a BOUND member and classifies it by <see cref="PlacementRules"/>. An observation that cannot be made fails closed.</summary>
        private PlacementVerdict ClassifyBound(EpisodeMember m, EpisodeId episode)
        {
            try
            {
                PhysicalObservation o = Port.Observe(m.pawn, episode);
                PassToWorldCheck pass = Port.CheckPassToWorld(m.pawn);
                return PlacementRules.Classify(o, pass);
            }
            catch (Exception ex)
            {
                return PlacementVerdict.FailClosed("the bound pawn could not be observed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }

        /// <summary>
        /// A truthful-aging step threw (§ 6.4): its progress is not provable, so the episode is quarantined HARD (<see cref="IsHardQuarantine"/>).
        /// The bookmark was advanced only by what completed; the pawn is untouched; the evidence is kept in the persisted key and error.
        /// </summary>
        private void QuarantineAgeUncertain(PhysicalEpisode e, EpisodeMember m, AgingUncertainException ex)
        {
            counters.agingUncertain++;
            e.attempts = PhysicalEpisode.MaxAttempts; // watched slowly: it is reported, never retried
            e.lastError = NetScribe.Truncate("Materialize " + m + ": " + ex.Message, 300);
            Quarantine(e, QuarantineAge + ":" + m.character + " completed " + ex.completedTicks + " uncertain " + ex.uncertainTicks + " bio " + ex.bioTicksBefore + ">" + ex.bioTicksAfter);
        }

        private bool Bind(PhysicalEpisode e, EpisodeMember m, int now)
        {
            if (m.IsNamed)
            {
                KnownCharacter c = ctx.characters.Get(m.character);
                if (c == null) return false;
                if (c.pawn != null && c.pawn.IsBound)
                {
                    // Rematerialization: the binding resolves or the member is not placed. Never a second pawn for this person.
                    if (!Port.Resolves(c.pawn))
                    {
                        counters.unresolvedBindings++;
                        e.lastError = "BindingUnresolved " + c.id;
                        return false;
                    }
                    // Truthful aging (§ 6.4): the FULL interval since agedThroughTick, uncapped, before anything can observe the pawn.
                    // Vanilla's mothball step is NOT atomic (it advances the whole step, then runs the birthdays, with no rollback), so the
                    // bookmark advances only by the steps that RETURNED (every birthday inside them ran). A step that threw has an unprovable
                    // progress: the bookmark is never moved for it, never set to "now", and the interval is never replayed (the episode is
                    // quarantined by Materialize). Any other failure changed nothing and the interval may be retried later.
                    long elapsed = c.pawn.agedThroughTick >= 0 ? (long)now - c.pawn.agedThroughTick : 0L;
                    if (elapsed > 0)
                    {
                        try
                        {
                            Port.CatchUpAge(c.pawn, elapsed);
                        }
                        catch (AgingUncertainException ex)
                        {
                            if (ex.completedTicks > 0) c.pawn.agedThroughTick += (int)Math.Min(ex.completedTicks, elapsed);
                            throw;
                        }
                    }
                    c.pawn.agedThroughTick = now;
                    m.pawn = c.pawn.Copy();
                    counters.rematerialized++;
                    return true;
                }
                PawnRef made = Port.Create(ProjectionPolicy.ForPerson(e, m, c, ctx.actors.Get(e.actor), ctx.networkSeed));
                if (made == null || !made.IsBound) return false;
                made.boundTick = now;
                made.agedThroughTick = now;
                c.pawn = made; // the write-once binding: one pawn for life
                m.pawn = made.Copy();
                counters.created++;
                return true;
            }
            PawnRef slot = Port.Create(new ProjectionRequest { episode = e.id, actor = e.actor, slot = m.slot, tier = m.tier, seed = NetHash.Combine(e.seed, m.slot), faction = e.faction });
            if (slot == null || !slot.IsBound) return false;
            slot.boundTick = now;
            slot.agedThroughTick = now;
            m.pawn = slot;
            counters.created++;
            return true;
        }

        /// <summary>A Planned episode with nobody placed: Closed(NeverPlaced), custody reverted and headcount returned, through the commit.</summary>
        private void CloseUnplaced(PhysicalEpisode e)
        {
            List<MemberDecision> decisions = new List<MemberDecision>();
            for (int i = 0; i < e.members.Count; i++)
            {
                EpisodeMember m = e.members[i];
                decisions.Add(new MemberDecision { member = m, character = m.IsNamed ? ctx.characters.Get(m.character) : null, outcome = MemberOutcome.NeverPlaced });
            }
            if (Guarded(() => Commit(e, decisions, ReconciliationPlanner.CloseNeverPlaced))) FinishPending(e);
        }

        // ================================================================== RECONCILE (physical → abstract, exactly once)

        /// <summary>
        /// The ONE reconciliation entry (§ 15.1): every wake-up (signal, watch, load, dev, a purpose's end) calls it; none is trusted
        /// to say what happened. A Closed episode is a no-op (the gate); a wake-up that arrives while a commit or stage is running
        /// is queued; with no available port nothing is decided (absence of observation is never evidence). Returns true when THIS
        /// call committed the consequences.
        /// </summary>
        public bool Reconcile(PhysicalEpisode e, string wake)
        {
            counters.wakeups++;
            if (e == null) return false;
            if (e.state == EpisodeState.Closed)
            {
                counters.duplicateWakeups++;
                if (e.HasPendingStage) FinishPending(e);
                return false;
            }
            if (busy)
            {
                Defer(e);
                return false;
            }
            if (e.state == EpisodeState.Planned)
            {
                ResolvePlanned(e);
                return false;
            }
            if (!PortAvailable)
            {
                counters.blindWakeups++;
                return false;
            }
            bool committed = Guarded(() => ReconcileCore(e));
            if (committed) FinishPending(e);
            DrainDeferred();
            return committed;
        }

        private bool ReconcileCore(PhysicalEpisode e)
        {
            // A person whose age truth is uncertain is not decided by looking at the world (§ 6.4): the episode is reported, never resolved.
            if (e.state == EpisodeState.Quarantined && IsHardQuarantine(e.quarantineKey)) return false;
            List<MemberDecision> decisions = new List<MemberDecision>();
            bool pending = false;
            string unsupported = null;
            string broken = null;
            string failClosed = null;
            try
            {
                for (int i = 0; i < e.members.Count; i++)
                {
                    EpisodeMember m = e.members[i];
                    MemberDecision d = new MemberDecision { member = m, character = m.IsNamed ? ctx.characters.Get(m.character) : null };
                    if (m.state != MemberState.Present && m.IsBound)
                    {
                        // Bound but never reported placed (§ 7.3 rule 7): its physical state is authoritative, so it is observed, never assumed NeverPlaced.
                        PlacementVerdict v = ClassifyBound(m, e.id);
                        if (v.kind == PlacementVerdictKind.Present || v.kind == PlacementVerdictKind.Lost)
                        {
                            m.state = MemberState.Present; // the ordinary observation below decides it (Spawned ⇒ Pending, Dead ⇒ Killed, Gone ⇒ Lost)
                        }
                        else if (v.kind == PlacementVerdictKind.FailClosed)
                        {
                            counters.placementFailClosed++;
                            if (failClosed == null) failClosed = v.detail + " " + m;
                            d.outcome = MemberOutcome.Pending;
                            pending = true;
                            decisions.Add(d);
                            continue;
                        }
                    }
                    if (m.state != MemberState.Present)
                    {
                        d.outcome = MemberOutcome.NeverPlaced; // never placed: nothing physical happened to it in this episode
                    }
                    else
                    {
                        // 1 OBSERVE (read-only) · 2 DECIDE (positive evidence only)
                        PhysicalObservation o = Port.Observe(m.pawn, e.id);
                        bool unsup;
                        d.observation = o;
                        d.outcome = ReconciliationPlanner.Decide(o, out unsup);
                        if (unsup && unsupported == null) unsupported = o.kind + " " + m;
                        if (o.kind == ObservedKind.ReservationBroken && broken == null)
                        {
                            broken = o.kind + " " + m;
                            e.lastError = NetScribe.Truncate(o.note ?? WorldPawnRules.BrokenReservation, 300);
                        }
                        if (d.outcome == MemberOutcome.Pending) pending = true;
                        if (d.outcome == MemberOutcome.Returned) d.woundDays = ReconciliationPlanner.WoundDaysFor(o);
                    }
                    decisions.Add(d);
                }
            }
            catch (Exception ex)
            {
                RecordFailure(e, ex);
                return false;
            }
            if (broken != null)
            {
                // ADR-053 / P3-INV-032: a retained named person that vanilla sees as an ordinary Free world pawn is the M1 reservation FAILING.
                // It is never a return, and nothing here repairs it (no registry is ensured, nothing is committed): the pawn is untouched and
                // the person stays blocked until the observation says something positive.
                counters.reservationBroken++;
                Quarantine(e, QuarantineReservation + ":" + broken);
                return false;
            }
            if (failClosed != null)
            {
                Quarantine(e, QuarantinePlacement + ":" + failClosed);
                return false;
            }
            if (unsupported != null)
            {
                // § 17: no faked capture support. The pawn is untouched and the person stays blocked.
                Quarantine(e, "UnsupportedCustody:" + unsupported);
                return false;
            }
            if (pending)
            {
                if (e.openedTick >= 0 && ctx.Now - e.openedTick > LongOpenTicks)
                {
                    NetLog.WarnOnce(LogCategory.Physical, "longopen." + e.id.Value, "Episode " + e + " has been open for more than 30 days; it is reported, never auto-closed.");
                }
                return false;
            }
            // An episode whose members were ALL never placed (a quarantined placement that later resolved to "alive, unspawned, held by nobody")
            // decided nothing about a linked operation: it closes NeverPlaced like any other nobody-placed episode, never as a Reconciled one
            // whose operation marker would be written off.
            bool nobodyPlaced = decisions.Count > 0;
            for (int i = 0; i < decisions.Count; i++) if (decisions[i].outcome != MemberOutcome.NeverPlaced) nobodyPlaced = false;
            return Commit(e, decisions, nobodyPlaced ? ReconciliationPlanner.CloseNeverPlaced : ReconciliationPlanner.CloseReconciled);
        }

        /// <summary>3 PLAN · 4 VALIDATE · 5 ATOMIC DURABLE COMMIT. False (with nothing applied) on any failure.</summary>
        private bool Commit(PhysicalEpisode e, List<MemberDecision> decisions, string reasonKey)
        {
            ReconciliationPlan plan;
            try
            {
                plan = ReconciliationPlanner.PlanEpisode(ctx, e, decisions, reasonKey);
                ReconciliationPlanner.Validate(ctx, plan);
            }
            catch (PlanInvalidException ex)
            {
                counters.invalidPlans++;
                if (ex.reasonKey == "ActorMissing")
                {
                    Quarantine(e, "ActorMissing");
                    return false;
                }
                RecordFailure(e, ex);
                return false;
            }
            catch (Exception ex)
            {
                RecordFailure(e, ex);
                return false;
            }
            CommitTarget target = new CommitTarget { now = ctx.Now, ids = ctx.ids, characters = ctx.characters, outbox = e.publications };
            int fault = commitFaultAfter;
            commitFaultAfter = -1;
            try
            {
                commitBoundary?.Invoke("before");
                ReconciliationApplier.Commit(plan, target, fault);
            }
            catch (Exception ex)
            {
                commitBoundary?.Invoke("restored");
                counters.commitFailures++;
                RecordFailure(e, ex);
                return false;
            }
            counters.commits++;
            ctx.episodes.RebuildIndex();
            // Derived runtime state only (never durable truth): a returned Solo's route cache.
            if (plan.org == null && plan.actor != null) ctx.Spatial?.ForgetRoute(plan.actor);
            StateVersion.Bump();
            return true;
        }

        /// <summary>The episode cannot be reconciled safely (§ 8.1): its members stay blocked, the pawns untouched; diagnosed and retried.</summary>
        public void Quarantine(PhysicalEpisode e, string key)
        {
            if (e == null || e.state == EpisodeState.Closed) return;
            if (e.state != EpisodeState.Quarantined) counters.quarantined++;
            e.state = EpisodeState.Quarantined;
            e.quarantineKey = NetScribe.Truncate(key, 200);
            NetLog.WarnOnce(LogCategory.Physical, "quarantine." + e.id.Value + "." + key, "Episode " + e.id + " quarantined (" + key + "): its people stay blocked from abstract simulation; nothing is invented.");
            StateVersion.Bump();
        }

        /// <summary>
        /// A failed step, recorded on the episode. BEFORE the commit (an Open or Quarantined episode, nothing applied) the bounded
        /// retries end in quarantine. AFTER the commit a Closed episode is never re-labelled: it stays Closed with the failed stage's
        /// marker false (its people still linked, so the gate stays closed), is retried by its watch job (slowly once past the
        /// bound) and reported by the validator after 30 days. It is never completed by inference.
        /// </summary>
        private void RecordFailure(PhysicalEpisode e, Exception ex)
        {
            e.attempts++;
            e.lastError = NetScribe.Truncate(ex.GetType().Name + ": " + ex.Message, 300);
            NetLog.WarnOnce(LogCategory.Physical, "episode." + e.id.Value + ".fail." + Math.Min(e.attempts, PhysicalEpisode.MaxAttempts),
                "Episode " + e.id + " step failed (attempt " + e.attempts + "), nothing half-applied; it will be retried: " + e.lastError);
            if (e.state != EpisodeState.Closed && e.attempts >= PhysicalEpisode.MaxAttempts) Quarantine(e, "RetriesExhausted");
        }

        /// <summary>
        /// A Planned episode outside its materialization (a load, § 16.1): resolved by evidence, never by regeneration. A BOUND member that was
        /// created is Present, whether or not its binding still resolves (§ 7.3 rule 7): a pointer that no longer resolves is evidence of LOSS, which the
        /// ordinary observation (Gone) records as Lost, never as NeverPlaced. A bound member that was never created is classified like any other
        /// bound-but-unplaced member.
        /// </summary>
        public void ResolvePlanned(PhysicalEpisode e)
        {
            if (e == null || e.state != EpisodeState.Planned || !PortAvailable || busy) return;
            int present = 0;
            bool unresolved = false;
            for (int i = 0; i < e.members.Count; i++)
            {
                EpisodeMember m = e.members[i];
                if (m.state == MemberState.Created && m.IsBound)
                {
                    m.state = MemberState.Present;
                    if (!Port.Resolves(m.pawn)) unresolved = true;
                }
                if (m.state == MemberState.Present) present++;
            }
            if (present > 0)
            {
                e.state = EpisodeState.Open;
                if (e.openedTick < 0) e.openedTick = ctx.Now;
                EnsureWatch(e);
                StateVersion.Bump();
                // A binding that no longer resolves is positive evidence of loss: observed at once (Gone ⇒ Lost), not left blocked until the next watch.
                if (unresolved) Reconcile(e, "load");
                return;
            }
            FinishPlanned(e, 0);
        }

        // ================================================================== the post-commit stages

        /// <summary>
        /// The finish-pending pass (§ 8.1): resumes whichever stage's EXPLICIT marker is unset, in order RELEASE → FOLLOW-UP →
        /// PUBLISH. A failed stage leaves its marker false and is retried by the watch; nothing is ever inferred from side effects.
        /// </summary>
        public bool FinishPending(PhysicalEpisode e, bool publish = true)
        {
            if (e == null || e.state != EpisodeState.Closed || !e.consequencesApplied) return false;
            if (e.IsComplete) return true;
            if (busy)
            {
                Defer(e);
                return false;
            }
            busy = true;
            try
            {
                if (!e.releaseApplied) RunRelease(e);
                if (e.releaseApplied && !e.followUpApplied) RunFollowUp(e);
                if (publish && e.releaseApplied && e.followUpApplied && !e.PublishDone) RunPublish(e);
            }
            catch (Exception ex)
            {
                counters.stageFailures++;
                RecordFailure(e, ex);
            }
            finally
            {
                busy = false;
            }
            if (e.IsComplete)
            {
                counters.completed++;
                ctx.episodes.RebuildIndex();
                ctx.scheduler.Cancel(WatchJob, e.id.Value);
            }
            else
            {
                EnsureWatch(e);
            }
            StateVersion.Bump();
            DrainDeferred();
            return e.IsComplete;
        }

        /// <summary>
        /// RELEASE (§ 8.1): every member's ordered, idempotent actions from its cursor (the cursor advances only after an action
        /// returned normally), then the episode-level clean-up, then COMPLETE. The authority gate stays closed until COMPLETE.
        /// </summary>
        private void RunRelease(PhysicalEpisode e)
        {
            for (int i = 0; i < e.members.Count; i++)
            {
                EpisodeMember m = e.members[i];
                ReleaseAction[] actions = ReleasePolicy.ActionsFor(m);
                while (m.releaseStep < actions.Length)
                {
                    Perform(e, m, actions[m.releaseStep]);
                    m.releaseStep++;
                    counters.releaseActions++;
                }
            }
            // Episode level (§ 13.2): the encounter faction is handed back to vanilla's own temporary-faction removal (a map removal never
            // queues it). Idempotent by observed state; vanilla then nulls its members' faction, which a reserved pawn tolerates (S31).
            if (e.faction != null && e.faction.IsValid) Port.ReleaseEncounterFaction(e.faction);
            // Episode level (§ 15.6 "S"): an actor this commit ended loses its upkeep job and its spatial journey. Both are guarded by
            // observed state, so a re-run repeats nothing; a contained facade fault is caught by re-checking, not swallowed.
            NetworkActor a = ctx.actors.Get(e.actor);
            if (a != null && a.status != ActorStatus.Active)
            {
                ctx.scheduler.Cancel(ContractorService.UpkeepJob, a.id.Value);
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                if (ctx.Spatial != null && sim != null && sim.spatial.IsInitialized && !SpatialSettled(sim.spatial))
                {
                    ctx.Spatial.OnActorEnded(a);
                    if (!SpatialSettled(sim.spatial)) throw new InvalidOperationException("RELEASE: the spatial clean-up of " + a.id + " did not take effect");
                }
            }
            Complete(e, a);
        }

        private static bool SpatialSettled(SpatialState s)
        {
            return s.destination == null && s.purpose == SpatialPurpose.None && s.status != SpatialStatus.Travelling && s.status != SpatialStatus.OnAssignment;
        }

        private void Perform(PhysicalEpisode e, EpisodeMember m, ReleaseAction action)
        {
            switch (action)
            {
                case ReleaseAction.Normalize:
                    Port.Normalize(m.pawn);
                    break;
                case ReleaseAction.EnsureRetained:
                    Port.EnsureRetained(m.pawn);
                    break;
                case ReleaseAction.PassToWorldIfAllowed:
                    {
                        // § 7.5 (P3-INV-031): only when all three parts hold, observed NOW. Already a world pawn ⇒ never passed again.
                        PassToWorldCheck check = Port.CheckPassToWorld(m.pawn);
                        if (check == PassToWorldCheck.Allowed)
                        {
                            Port.PassToWorld(m.pawn);
                            counters.passedToWorld++;
                        }
                        else if (check == PassToWorldCheck.AlreadyInWorldPawns)
                        {
                            counters.passSkippedAlreadyWorld++;
                        }
                        else
                        {
                            // Spawned, held, dead, unknown: the pawn is NOT released, so this action has NOT completed. It is never
                            // forced and never skipped: the throw leaves the cursor where it is, RELEASE COMPLETE cannot run, the
                            // link stays and the gate stays closed; the stage is retried (one authority, P3-INV-029/031).
                            counters.passRefused++;
                            NetLog.WarnOnce(LogCategory.Physical, "pass." + e.id.Value + "." + m.slot + "." + check,
                                "Episode " + e.id + ": " + m + " cannot be passed to the world (precondition " + check + "); RELEASE stays pending and the person stays blocked.");
                            throw new PhysicalPreconditionException("PassToWorld " + m, check);
                        }
                        break;
                    }
                case ReleaseAction.StripTag:
                    Port.StripEpisodeTag(m.pawn, e.id);
                    break;
            }
        }

        /// <summary>COMPLETE (§ 8.1): the only RELEASE step that writes Network truth beyond cursors, guarded like the commit.</summary>
        private void Complete(PhysicalEpisode e, NetworkActor a)
        {
            List<KnownCharacter> linked = new List<KnownCharacter>();
            for (int i = 0; i < e.members.Count; i++)
            {
                if (!e.members[i].IsNamed) continue;
                KnownCharacter c = ctx.characters.Get(e.members[i].character);
                if (c != null && c.episode == e.id) linked.Add(c);
            }
            DurableSnapshot snapshot = new DurableSnapshot().Capture(e);
            for (int i = 0; i < linked.Count; i++) snapshot.Capture(linked[i]);
            try
            {
                for (int i = 0; i < linked.Count; i++) linked[i].episode = EpisodeId.None;
                e.releaseApplied = true;
                e.releasedTick = ctx.Now;
            }
            catch
            {
                snapshot.Restore();
                throw;
            }
            counters.releasesCompleted++;
            a?.Get<ContractorSimulation>()?.MarkDirty();
        }

        /// <summary>FOLLOW-UP (§ 15.5): the linked operation's re-entrant resolution; the marker is set only after a normal return.</summary>
        private void RunFollowUp(PhysicalEpisode e)
        {
            if (!e.cause.operation.IsValid)
            {
                e.followUpApplied = true;
                return;
            }
            Operation op = ctx.operations?.Get(e.cause.operation);
            if (op == null) throw new InvalidOperationException("FOLLOW-UP: the linked operation " + e.cause.operation + " is missing (reported; never inferred complete)");
            ctx.Operations.OnPhysicalResolved(op);
            e.followUpApplied = true;
            counters.followUps++;
        }

        /// <summary>
        /// PUBLISH (§ 15.2 step 8) over the ACTUAL bus, which has no dedupe key: spec i is submitted, and only after the bus accepted
        /// it (it assigned a sequence number) does the cursor move past it, so an accepted event is never submitted again and a
        /// consumer's contained failure is never redispatched. Nothing here touches a consequence.
        /// </summary>
        private void RunPublish(PhysicalEpisode e)
        {
            int interrupt = publishInterruptAfter;
            publishInterruptAfter = -1;
            int accepted = 0;
            while (e.publishCursor < e.publications.Count)
            {
                if (interrupt >= 0 && accepted >= interrupt) throw new InjectedFaultException("publication interrupted after " + accepted + " accepted");
                NetworkEvent evt = Publications.Build(e.publications[e.publishCursor]);
                try
                {
                    ctx.bus.Publish(evt);
                }
                catch (Exception ex)
                {
                    if (evt.seq == 0) throw; // never accepted: the same spec is retried later
                    counters.publishFaultsAfterAccept++; // accepted: at-most-once beats a duplicate (§ 15.2 rule 4)
                    e.lastError = NetScribe.Truncate("Publish after acceptance: " + ex.Message, 300);
                }
                e.publishCursor++;
                accepted++;
                counters.published++;
            }
            e.publications.Clear();
            e.publishCursor = 0;
            e.publishedTick = ctx.Now;
        }

        // ================================================================== watch, load, removal

        /// <summary>
        /// A signal wake-up (§ 14.3): a vanilla quest-target signal for a BOUND pawn of this episode pulls its watch forward to the next
        /// tick. It decides nothing, reads no final state and mutates nothing but the watch's due tick; the watch then observes.
        /// </summary>
        public void Wake(PhysicalEpisode e, string why)
        {
            if (e == null || e.IsComplete) return;
            counters.signalWakeups++;
            EnsureWatch(e, ctx.Now + 1);
        }

        /// <summary>
        /// The encounter faction's goodwill towards the player, seeded once from the Network's own relation (§ 13.3): the actor's standing
        /// towards the player (−100…100) scaled into 0…60. A Phase 3.1 visit is never seeded hostile (hostility as content is 3.2+).
        /// </summary>
        public int EncounterGoodwill(ActorId actor)
        {
            NetworkActor player = ctx.actors?.PlayerProxy;
            if (player == null || ctx.Relations == null) return 0;
            float standing = ctx.Relations.Get(actor, player.id).standing;
            return Math.Max(0, Math.Min(60, (int)Math.Round(standing * 0.6f)));
        }

        /// <summary>Makes sure an incomplete episode is watched (no later than <paramref name="due"/> when one is given). Never duplicates (singleton kind).</summary>
        public void EnsureWatch(PhysicalEpisode e, int due = -1)
        {
            if (e == null || e.IsComplete || ctx.scheduler == null || !ctx.scheduler.IsKnownKind(WatchJob)) return;
            ScheduledJob existing = ctx.scheduler.Find(WatchJob, e.id.Value);
            if (existing != null && (due < 0 || existing.dueTick <= due)) return;
            ctx.scheduler.Schedule(WatchJob, due >= 0 ? due : ctx.Now + WatchPeriod, e.id.Value);
        }

        /// <summary>episode.watch: exists only while its episode is incomplete; bounded by the member count; never a scan.</summary>
        public void WatchJobRun(ScheduledJob job)
        {
            PhysicalEpisode e = ctx.episodes?.Get(new EpisodeId(job.target));
            if (e == null || e.IsComplete) return;
            try
            {
                if (e.state == EpisodeState.Closed) FinishPending(e);
                else Reconcile(e, "watch");
            }
            catch (Exception ex)
            {
                RecordFailure(e, ex);
            }
            if (!e.IsComplete) ctx.scheduler.Schedule(WatchJob, ctx.Now + (e.attempts >= PhysicalEpisode.MaxAttempts ? SlowWatchPeriod : WatchPeriod), e.id.Value);
        }

        /// <summary>
        /// The load pass (§ 16.1–16.2): rebuilds the derived index and makes sure every incomplete episode is watched from the first
        /// tick. It decides nothing, generates nothing and spawns nothing: a Planned episode is resolved by evidence and a pending
        /// stage resumed from its marker by the watch job, at the first tick. In a live 3.0 game the store is empty: nothing runs.
        /// </summary>
        public int OnLoaded()
        {
            if (ctx.episodes == null) return 0;
            ctx.episodes.RebuildIndex();
            List<PhysicalEpisode> open = ctx.episodes.Incomplete();
            for (int i = 0; i < open.Count; i++) EnsureWatch(open[i], ctx.Now + 1);
            return open.Count;
        }

        /// <summary>
        /// Prepare-for-removal settle (§ 20): every Planned, Open or Quarantined episode is observed and closed Detached through the
        /// same commit: terminal observations apply as in § 15; anything else (or anything unobservable) is Detached, never invented.
        /// RELEASE and FOLLOW-UP run (no publication: the runtime goes inert). Returns the episodes settled.
        /// </summary>
        public int SettleForRemoval()
        {
            if (ctx.episodes == null) return 0;
            int settled = 0;
            List<PhysicalEpisode> open = ctx.episodes.Incomplete();
            for (int i = 0; i < open.Count; i++)
            {
                PhysicalEpisode e = open[i];
                if (e.state == EpisodeState.Closed) continue;
                List<MemberDecision> decisions = new List<MemberDecision>();
                for (int k = 0; k < e.members.Count; k++)
                {
                    EpisodeMember m = e.members[k];
                    MemberDecision d = new MemberDecision { member = m, character = m.IsNamed ? ctx.characters.Get(m.character) : null, outcome = MemberOutcome.NeverPlaced };
                    if (m.state != MemberState.Present && m.IsBound)
                    {
                        // A bound member that was never reported placed is not assumed NeverPlaced (§ 7.3 rule 7): positively gone ⇒ Lost; alive,
                        // unspawned and held by nobody ⇒ NeverPlaced; anything else (held, unobservable, or an uncertain age) ⇒ Detached.
                        PlacementVerdict v = PortAvailable && !IsHardQuarantine(e.quarantineKey) ? ClassifyBound(m, e.id) : PlacementVerdict.FailClosed("not observable");
                        d.outcome = v.kind == PlacementVerdictKind.NeverPlaced ? MemberOutcome.NeverPlaced : v.kind == PlacementVerdictKind.Lost ? MemberOutcome.Lost : MemberOutcome.Detached;
                    }
                    if (m.state == MemberState.Present)
                    {
                        d.outcome = MemberOutcome.Detached;
                        if (PortAvailable)
                        {
                            try
                            {
                                PhysicalObservation o = Port.Observe(m.pawn, e.id);
                                bool unsup;
                                MemberOutcome terminal = ReconciliationPlanner.Decide(o, out unsup);
                                if (!unsup && terminal != MemberOutcome.Pending)
                                {
                                    d.outcome = terminal;
                                    d.observation = o;
                                    if (terminal == MemberOutcome.Returned) d.woundDays = ReconciliationPlanner.WoundDaysFor(o);
                                }
                            }
                            catch (Exception)
                            {
                                d.outcome = MemberOutcome.Detached;
                            }
                        }
                    }
                    decisions.Add(d);
                }
                if (Guarded(() => Commit(e, decisions, ReconciliationPlanner.CloseDetached))) settled++;
            }
            List<PhysicalEpisode> pending = ctx.episodes.Incomplete();
            for (int i = 0; i < pending.Count; i++) if (pending[i].state == EpisodeState.Closed) FinishPending(pending[i], false);
            counters.settled += settled;
            return settled;
        }

        /// <summary>Dev / tests: one wake-up for every incomplete episode (bounded by the store's incomplete episodes).</summary>
        public int WakeAll(string wake)
        {
            if (ctx.episodes == null) return 0;
            List<PhysicalEpisode> open = ctx.episodes.Incomplete();
            for (int i = 0; i < open.Count; i++) Reconcile(open[i], wake);
            return open.Count;
        }

        // ================================================================== re-entrancy

        private bool Guarded(Func<bool> work)
        {
            if (busy) return false;
            busy = true;
            try
            {
                return work();
            }
            finally
            {
                busy = false;
            }
        }

        private void Defer(PhysicalEpisode e)
        {
            counters.deferredWakeups++;
            if (!deferred.Contains(e.id)) deferred.Add(e.id);
        }

        private void DrainDeferred()
        {
            if (busy || deferred.Count == 0) return;
            List<EpisodeId> queued = new List<EpisodeId>(deferred);
            deferred.Clear();
            for (int i = 0; i < queued.Count; i++)
            {
                PhysicalEpisode e = ctx.episodes.Get(queued[i]);
                if (e != null && !e.IsComplete) Reconcile(e, "deferred");
            }
        }

        public string Describe()
        {
            int n = ctx.episodes?.Count ?? 0;
            return "Physical lifecycle: port " + (Port?.Name ?? "none") + (PortAvailable ? "" : " (unavailable)") + ", episodes " + n
                + " (incomplete " + (ctx.episodes?.Incomplete().Count ?? 0) + "); " + counters
                + "; gate refusals " + AuthorityGate.refusedWrites + ", dead-status refusals " + FateRules.refusedDeadWrites;
        }
    }
}
