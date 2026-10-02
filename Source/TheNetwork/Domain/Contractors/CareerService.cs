using System;
using System.Collections.Generic;
using System.Text;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Operations;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;

namespace TheNetwork.Domain.Contractors
{
    /// <summary>
    /// What a contractor is working toward next (Phase 2.75). A pure, deterministic read of state that
    /// already exists; never stored, never an effect. Future systems (Phase 4B compensation, hiring)
    /// consume it; today it only explains.
    /// </summary>
    public enum CareerNeed : byte
    {
        None = 0,
        Recovery = 1,
        Capital = 2,
        Equipment = 3,
        Expansion = 4,
        Mobility = 5,
        Mastery = 6,
        Prestige = 7
    }

    /// <summary>
    /// Stable keys of the derived descriptors. A Tag says "the state that already exists qualifies": it
    /// is read from fame, funds, experience, equipment and mobility, never stored, and never a modifier.
    /// Nothing in the resolver, pricing or willingness reads a Tag (a source scan in the test run holds
    /// that), so a Tag can never apply an advantage the underlying skill or equipment already applies.
    /// </summary>
    public static class CareerTags
    {
        public const string WellEquipped = "WellEquipped";
        public const string Wealthy = "Wealthy";
        public const string EliteCombat = "EliteCombat";
        public const string BattleTested = "BattleTested";
        public const string LongRange = "LongRange";
        public const string RapidTransport = "RapidTransport";
        public const string HeavyLift = "HeavyLift";
        public const string SpacerCapable = "SpacerCapable";
        public const string LegendaryReputation = "LegendaryReputation";

        /// <summary>
        /// RESERVED for Phase 3 and the compensation phases. No durable augmentation state exists in
        /// Phase 2.75, so it is never emitted (a test and a soak invariant hold that).
        /// </summary>
        public const string Augmented = "Augmented";

        /// <summary>Every key Phase 2.75 can emit (not <see cref="Augmented"/>).</summary>
        public static readonly string[] Emitted =
        {
            WellEquipped, Wealthy, EliteCombat, BattleTested, LongRange, RapidTransport, HeavyLift, SpacerCapable, LegendaryReputation
        };
    }

    /// <summary>Why equipment cannot advance right now (None = it can).</summary>
    public enum AdvancementBlock : byte
    {
        None = 0,
        NotActive = 1,
        Committed = 2,
        TopTier = 3,
        Recovering = 4,
        Cooldown = 5,
        NeedsReputation = 6,
        NeedsFunds = 7
    }

    /// <summary>Every way contractor funds move, so the soak can reconcile them exactly.</summary>
    public enum FundsFlow : byte
    {
        Starting = 0,
        Credit = 1,
        ClawBack = 2,
        Upkeep = 3,
        Repair = 4,
        Recruitment = 5,
        Advancement = 6,
        Dev = 7
    }

    /// <summary>Runtime tallies (never saved) the diagnostics and the soak read.</summary>
    public sealed class CareerCounters
    {
        public readonly long[] flows = new long[8];
        public int outcomesApplied;
        public int fameChanges;
        public int advancements;
        public int advancedWhileCommitted;
        public int failures;
        public int advancementRuns;

        /// <summary>Applied outcomes by frozen danger (tenths) and the reputation they earned (diagnostics: how the work is distributed).</summary>
        public readonly int[] dangerTenths = new int[11];
        public readonly long[] gainByTenth = new long[11];
        public int zeroGainOutcomes;

        public long Flow(FundsFlow f) => flows[(int)f];

        /// <summary>The net of every flow: what the contractors' funds must have changed by.</summary>
        public long Net
        {
            get
            {
                long n = 0;
                for (int i = 0; i < flows.Length; i++) n += flows[i];
                return n;
            }
        }
    }

    /// <summary>
    /// Contractor careers (ADR-046). It owns no state of its own: reputation lives in PublicReputation,
    /// wealth in ContractorSimulation.funds, equipment in EquipmentProfile, the cumulative summary in
    /// CareerRecord. It decides what a finished job earns, moves contractor money through ONE saturating
    /// path, advances equipment from the existing daily upkeep, and derives CareerNeed and Tags on demand.
    /// </summary>
    public sealed class CareerService
    {
        private readonly DomainContext ctx;
        public readonly CareerCounters counters = new CareerCounters();

        public CareerService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        // ================================================================== funds (one saturating path)

        /// <summary>
        /// Moves a contractor's funds by <paramref name="delta"/>, saturating at the bounds, and tallies the
        /// amount that actually moved under its flow. Every change to <c>sim.funds</c> goes through here.
        /// Returns the signed amount applied.
        /// </summary>
        public int MoveFunds(ContractorSimulation sim, long delta, FundsFlow flow)
        {
            if (sim == null || delta == 0) return 0;
            int before = sim.funds;
            sim.funds = CareerPolicy.AddFunds(before, delta);
            long applied = (long)sim.funds - before;
            counters.flows[(int)flow] += applied;
            return (int)applied;
        }

        /// <summary>A new contractor's opening funds (set once at creation), tallied so the books balance.</summary>
        public void NoteStartingFunds(ContractorSimulation sim)
        {
            if (sim == null) return;
            sim.funds = CareerPolicy.AddFunds(0, sim.funds);
            counters.flows[(int)FundsFlow.Starting] += sim.funds;
        }

        /// <summary>
        /// The contractor's share of silver the client paid: lands in its funds and its career earnings.
        /// Returns what reached the funds (written on the ledger record by the caller, in the same step).
        /// </summary>
        public int Credit(NetworkActor a, int silver)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || silver <= 0) return 0;
            int applied = MoveFunds(sim, silver, FundsFlow.Credit);
            sim.career.careerEarnings = Math.Max(0, CareerPolicy.AddSaturating(sim.career.careerEarnings, applied));
            return applied;
        }

        /// <summary>
        /// Takes back, from what this contractor was paid on THIS contract, the proportion that corresponds to the
        /// part of a refund drawn from the player's OWN payments on this contract (<paramref name="fromOwn"/>, the
        /// refund's typed provenance): the contractor keeps exactly the part of its pay that was not refunded.
        /// Funding a replacement carried in was paid to a previous contractor: it is neither in the numerator nor in
        /// the denominator, so it can neither dilute nor enlarge the clawback. Never more than the contractor holds
        /// here; never for an insurance payout (the insurer paid it). Call it BEFORE the refund record is added to
        /// the ledger. Returns the amount taken back.
        /// </summary>
        public int ClawBack(NetworkActor a, Contract c, int fromOwn)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || c == null || fromOwn <= 0) return 0;
            long held = c.ContractorHeld();
            long basis = c.OwnBearingRemaining();
            if (held <= 0 || basis <= 0) return 0;
            long claw = held * Math.Min((long)fromOwn, basis) / basis;
            return Reverse(sim, Math.Min(claw, held));
        }

        /// <summary>
        /// A technical invalidation: takes back EVERYTHING this contractor still holds of what it was paid on THIS
        /// contract, whatever the player's own refund came to. An earlier insurance payout reimburses the player (so the
        /// player's final refund is smaller) but must never shield the contractor's pay from a later invalidation, so
        /// this is the contractor's whole <see cref="Contract.ContractorHeld"/> and not a share of the refund. It holds
        /// only silver credited on this very contract: a replacement's carried-in funding credited nobody, and money
        /// paid to a lost contractor is on another contract's ledger. Call it BEFORE the refund record is added.
        /// Returns the amount taken back.
        /// </summary>
        public int ClawBackAll(NetworkActor a, Contract c)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || c == null) return 0;
            return Reverse(sim, c.ContractorHeld());
        }

        /// <summary>Moves <paramref name="claw"/> out of the contractor's funds and career earnings; returns what really moved.</summary>
        private int Reverse(ContractorSimulation sim, long claw)
        {
            if (claw <= 0) return 0;
            int applied = -MoveFunds(sim, -claw, FundsFlow.ClawBack);
            sim.career.careerEarnings = Math.Max(0, CareerPolicy.AddSaturating(sim.career.careerEarnings, -applied));
            return applied;
        }

        // ================================================================== reputation

        /// <summary>Adds reputation (never subtracts) and publishes a history event only when the band changes.</summary>
        public int AddReputation(NetworkActor a, int points, bool earned)
        {
            if (a == null || points <= 0) return 0;
            PublicReputation rep = a.reputation;
            FameBand before = rep.fame;
            int old = rep.score;
            rep.SetScore((int)Math.Min((long)old + points, CareerPolicy.ScoreCap));
            int applied = rep.score - old;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            if (earned && sim != null) sim.career.reputationEarned = CareerPolicy.AddSaturating(sim.career.reputationEarned, applied);
            if (rep.fame != before)
            {
                counters.fameChanges++;
                if (a.status == ActorStatus.Active) PublishFame(a, before, rep.fame);
            }
            return applied;
        }

        private void PublishFame(NetworkActor a, FameBand from, FameBand to)
        {
            ContractorEvent e = EventFactory.Make<ContractorEvent>(EventKeys.ContractorFameChanged, to >= FameBand.Famous ? Importance.Notable : Importance.Minor, a.id.Ref);
            e.actor = a.id;
            e.actorName = a.name.Display;
            e.descriptorKey = to.ToString();
            e.reasonKey = from.ToString();
            ctx.bus.Publish(e);
        }

        // ================================================================== the finished job

        /// <summary>
        /// The operation's frozen danger: from the inputs committed at engagement (the very figures the
        /// resolver used), or the danger committed when it started. Never re-read from the world.
        /// </summary>
        public static float DangerOf(Operation op)
        {
            if (op == null) return 0f;
            return op.frozenInputs != null ? Resolver.Danger(Resolver.Edge(op.frozenInputs)) : op.danger;
        }

        /// <summary>
        /// A written-off group, derived from the operation itself: its outcome was Troubled and it never came home (the
        /// Return checkpoint was never done). The live call sites know this directly; a retry from the validator
        /// derives it from this.
        /// </summary>
        public static bool WasWrittenOff(Operation op)
        {
            return op?.outcome != null && op.outcome.troubledKey != null && op.Find(Checkpoint.Return)?.done != true;
        }

        /// <summary>The career result of one operation, computed without touching any state (the plan of one small commit).</summary>
        private sealed class CareerOutcomeDelta
        {
            public NetworkActor actor;
            public ContractorSimulation sim;
            public CareerRecord next;
            public int newScore;
            public FameBand fameBefore;
            public FameBand fameAfter;
            public float danger;
            public int gain;
        }

        /// <summary>
        /// Applies an operation's result to its contractor's career, exactly once. Called from the end of the
        /// lifecycle only: Finish, an Abort after the outcome was committed, and a Troubled group written
        /// off. Never at the first resolution while the group is still Troubled; never for an operation
        /// from before Phase 2.75 (<c>careerEligible</c> false) nor one aborted before it had an outcome.
        ///
        /// <c>careerOutcomeApplied</c> means the durable career mutation really committed. So the result is PLANNED
        /// first, as a pure delta that reads state and changes none (anything that can throw happens here); the small
        /// durable commit (the record's fields and the reputation score) follows, with a snapshot restored if it
        /// somehow fails; only then is the flag set; and the fame event is published AFTER the commit, in its own
        /// guard, so a failing consumer can never cause a retry or a duplicate. A failure anywhere leaves the flag
        /// false and the career state exactly as it was (it is retried by the next validation), and never breaks the
        /// operation's lifecycle. Returns true only when the result was applied by THIS call.
        /// </summary>
        public bool CommitOutcome(Operation op, bool writtenOff = false)
        {
            if (op == null || !op.careerEligible || op.careerOutcomeApplied || op.outcome == null) return false;
            CareerOutcomeDelta delta;
            try
            {
                delta = Plan(op, writtenOff);
            }
            catch (Exception ex)
            {
                return CommitFailed(op, ex);
            }
            CareerRecord snapshot = delta.sim.career.Clone();
            int scoreBefore = delta.actor.reputation.score;
            try
            {
                delta.sim.career.CopyFrom(delta.next);
                delta.actor.reputation.SetScore(delta.newScore);
            }
            catch (Exception ex)
            {
                delta.sim.career.CopyFrom(snapshot);
                delta.actor.reputation.SetScore(scoreBefore);
                return CommitFailed(op, ex);
            }
            op.careerOutcomeApplied = true;
            counters.outcomesApplied++;
            int tenth = (int)Math.Min(10, Math.Max(0, Math.Floor(delta.danger * 10f)));
            counters.dangerTenths[tenth]++;
            counters.gainByTenth[tenth] += delta.gain;
            if (delta.gain == 0) counters.zeroGainOutcomes++;
            if (delta.fameAfter != delta.fameBefore)
            {
                counters.fameChanges++;
                if (delta.actor.status == ActorStatus.Active)
                {
                    try
                    {
                        PublishFame(delta.actor, delta.fameBefore, delta.fameAfter);
                    }
                    catch (Exception ex)
                    {
                        NetLog.WarnOnce(LogCategory.Operations, "career.fame." + op.id.Value, "The fame event for operation " + op.id + " could not be published (the career result stands): " + ex.Message);
                    }
                }
            }
            return true;
        }

        private bool CommitFailed(Operation op, Exception ex)
        {
            counters.failures++;
            NetLog.WarnOnce(LogCategory.Operations, "career.commit." + op.id.Value, "Career result of operation " + op.id + " could not be applied (nothing was changed; it will be retried): " + ex.Message);
            return false;
        }

        /// <summary>
        /// The pure plan: what this operation does to its contractor's career. Reads the operation, the actor, its
        /// record and its score; changes nothing. A career-eligible operation with a committed outcome always has a
        /// contractor and a simulation to receive it (an ended contractor's actor persists): when either is missing the
        /// state is malformed, so this throws and the caller treats it as a failed commit, never as an applied one.
        /// </summary>
        private CareerOutcomeDelta Plan(Operation op, bool writtenOff)
        {
            NetworkActor a = ctx.actors.Get(op.contractor);
            if (a == null) throw new InvalidOperationException("MissingCareerActor: operation " + op.id + " has no contractor actor " + op.contractor + " to receive its career result");
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            if (sim == null) throw new InvalidOperationException("MissingContractorSimulation: operation " + op.id + " has a contractor actor without a ContractorSimulation to receive its career result");
            OperationOutcome o = op.outcome;
            // A group written off never brought the work home: whatever the resolver rolled (even a Disaster), the
            // career records ONE ultimate meaning, a Failure with nothing secured. The committed OperationOutcome
            // keeps its original band: only the career classification differs.
            OutcomeBand band = writtenOff ? OutcomeBand.Failure : o.band;
            int secured = writtenOff ? 0 : o.secured;
            float danger = DangerOf(op);
            CareerRecord next = sim.career.Clone();
            switch (band)
            {
                case OutcomeBand.Triumph: next.triumphs = CareerPolicy.AddSaturating(next.triumphs, 1); break;
                case OutcomeBand.Success:
                case OutcomeBand.CostlySuccess: next.successes = CareerPolicy.AddSaturating(next.successes, 1); break;
                case OutcomeBand.Partial: next.partials = CareerPolicy.AddSaturating(next.partials, 1); break;
                case OutcomeBand.Failure: next.failures = CareerPolicy.AddSaturating(next.failures, 1); break;
                default: next.disasters = CareerPolicy.AddSaturating(next.disasters, 1); break;
            }
            next.casualtiesTaken = CareerPolicy.AddSaturating(next.casualtiesTaken, (long)o.Killed + o.Wounded + o.Captured + o.Missing);
            next.peopleLost = CareerPolicy.AddSaturating(next.peopleLost, o.Killed);
            next.captured = CareerPolicy.AddSaturating(next.captured, o.Captured);
            next.missing = CareerPolicy.AddSaturating(next.missing, o.Missing);
            next.lastOutcomeTick = ctx.Now;
            // The most dangerous work at least partly done, whether or not it still earned reputation.
            if (CareerPolicy.OutcomeMultiplier(band, secured, o.requested) > 0f)
            {
                int scaled = CareerPolicy.ScaledDanger(danger);
                if (scaled > next.highestDanger) next.highestDanger = scaled;
            }
            PublicReputation rep = a.reputation;
            int oldScore = rep.score;
            FameBand before = rep.fame;
            int gain = CareerPolicy.ReputationGain(danger, band, secured, o.requested, oldScore);
            int newScore = CareerPolicy.ClampScore((long)oldScore + gain);
            if (gain > 0) next.reputationEarned = CareerPolicy.AddSaturating(next.reputationEarned, newScore - oldScore);
            return new CareerOutcomeDelta { actor = a, sim = sim, next = next, newScore = newScore, fameBefore = before, fameAfter = CareerPolicy.FameFor(newScore), danger = danger, gain = gain };
        }

        // ================================================================== views of state that already exists

        /// <summary>The standing operating reserve: days of the contractor's real upkeep, scaled by its size.</summary>
        public static int OperatingReserve(NetworkActor a)
        {
            return CareerPolicy.OperatingReserve(a != null && a.Has<OrganizationProfile>(), ContractorService.Headcount(a));
        }

        private float RecentLossShare(NetworkActor a)
        {
            ActorRecordSummary summary = ctx.summaries?.Get(a.id);
            if (summary == null) return 0f;
            return Math.Min(1f, summary.Recent("casualties.taken", ctx.Now) / Math.Max(1, ContractorService.Headcount(a)));
        }

        /// <summary>
        /// What the contractor most needs next, from fame, funds, equipment, health, roster, mobility and
        /// experience, in a fixed order, softened by its doctrine. Deterministic: the same state always gives
        /// the same need, before and after a reload. No effect.
        /// </summary>
        public CareerNeed CurrentNeed(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || !ContractorService.IsNpcContractor(a)) return CareerNeed.None;
            OrganizationProfile org = a.Get<OrganizationProfile>();
            Doctrine d = sim.doctrine;
            FameBand fame = a.reputation.fame;
            ExperienceBand exp = ContractorService.Experience(a);

            if (ctx.Contractors.WoundedShare(a) >= CareerPolicy.SeriousWoundedShare || sim.equipment.condition < CareerPolicy.PoorCondition || RecentLossShare(a) >= CareerPolicy.SevereRecentLosses)
            {
                return CareerNeed.Recovery;
            }
            if (sim.funds < OperatingReserve(a)) return CareerNeed.Capital;
            int supported = Math.Max(CareerPolicy.TierSupportedByFame(fame), CareerPolicy.TierSupportedByExperience(exp));
            if (sim.equipment.tier < supported) return CareerNeed.Equipment;
            if (org != null && ContractorService.Headcount(a) < org.capacity * CareerPolicy.ExpansionBelowCapacityShare && d.ambition + d.professionalism >= CareerPolicy.ExpansionDrive) return CareerNeed.Expansion;
            if (fame >= FameBand.Established && d.ambition >= CareerPolicy.AmbitionForMobility && sim.mobility.rangeBand < Band.High) return CareerNeed.Mobility;
            if ((int)exp < (int)fame) return CareerNeed.Mastery;
            if (fame >= FameBand.Famous && d.ambition >= CareerPolicy.AmbitionForPrestige) return CareerNeed.Prestige;
            return CareerNeed.None;
        }

        /// <summary>
        /// The derived descriptors (stable keys, fixed order), computed from the current state each time and
        /// never stored. They answer "what can they plausibly do": they grant no bonus of any kind.
        /// </summary>
        public List<string> Tags(NetworkActor a)
        {
            List<string> tags = new List<string>();
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || !ContractorService.IsNpcContractor(a)) return tags;
            ExperienceBand exp = ContractorService.Experience(a);
            if (sim.equipment.tier >= CareerPolicy.WellEquippedTier) tags.Add(CareerTags.WellEquipped);
            if (sim.funds >= OperatingReserve(a) * CareerPolicy.WealthyReserveMultiple) tags.Add(CareerTags.Wealthy);
            if (exp >= ExperienceBand.Elite && sim.equipment.tier >= CareerPolicy.EliteCombatMinTier && sim.equipment.condition >= CareerPolicy.EliteCombatMinCondition) tags.Add(CareerTags.EliteCombat);
            if (sim.career.Resolved >= CareerPolicy.BattleTestedJobs) tags.Add(CareerTags.BattleTested);
            if (sim.mobility.rangeBand >= Band.High) tags.Add(CareerTags.LongRange);
            if (sim.mobility.Has("RapidTransport")) tags.Add(CareerTags.RapidTransport);
            if (sim.mobility.Has("HeavyLift")) tags.Add(CareerTags.HeavyLift);
            if (sim.mobility.Has("Orbital")) tags.Add(CareerTags.SpacerCapable);
            if (a.reputation.fame == FameBand.Legendary) tags.Add(CareerTags.LegendaryReputation);
            return tags;
        }

        public bool HasTag(NetworkActor a, string key)
        {
            return Tags(a).Contains(key);
        }

        // ================================================================== equipment advancement

        /// <summary>Why this contractor cannot advance its equipment now (None when it can). Reads state only.</summary>
        public AdvancementBlock BlockedBy(NetworkActor a, out int cost, out int reserve)
        {
            cost = 0;
            reserve = 0;
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null || !ContractorService.IsNpcContractor(a) || !a.IsActive) return AdvancementBlock.NotActive;
            // A live commitment needs the current equipment truth (the operation's inputs read it).
            if (sim.commitments.Count > 0) return AdvancementBlock.Committed;
            int tier = sim.equipment.tier;
            if (tier >= CareerPolicy.MaxTier) return AdvancementBlock.TopTier;
            cost = CareerPolicy.UpgradeCost(tier);
            // The cheap checks first: this runs once per contractor per day, and most days nothing qualifies.
            int last = sim.career.lastAdvancementTick;
            if (last >= 0 && ctx.Now - last < CareerPolicy.AdvancementCooldownTicks) return AdvancementBlock.Cooldown;
            if (a.reputation.fame < CareerPolicy.RequiredFame(tier + 1)) return AdvancementBlock.NeedsReputation;
            reserve = OperatingReserve(a);
            if ((long)sim.funds < (long)cost + reserve) return AdvancementBlock.NeedsFunds;
            // A contractor that is hurt, or whose kit is wrecked, mends first: no luxury purchase while recovering.
            if (CurrentNeed(a) == CareerNeed.Recovery) return AdvancementBlock.Recovering;
            return AdvancementBlock.None;
        }

        /// <summary>
        /// Rides the existing staggered daily upkeep (no scheduler job of its own). At most one tier per
        /// cooldown. Deterministic: no randomness. Returns whether equipment advanced.
        /// </summary>
        public bool RunAdvancement(NetworkActor a)
        {
            counters.advancementRuns++;
            int cost, reserve;
            if (BlockedBy(a, out cost, out reserve) != AdvancementBlock.None) return false;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            if (sim.commitments.Count > 0) counters.advancedWhileCommitted++;
            MoveFunds(sim, -cost, FundsFlow.Advancement);
            sim.equipment.tier = CareerPolicy.ClampTier(sim.equipment.tier + 1);
            sim.career.lastAdvancementTick = ctx.Now;
            sim.career.advancementCount = CareerPolicy.AddSaturating(sim.career.advancementCount, 1);
            sim.MarkDirty();
            counters.advancements++;
            ContractorEvent e = EventFactory.Make<ContractorEvent>(EventKeys.ContractorAdvanced, sim.equipment.tier >= CareerPolicy.WellEquippedTier ? Importance.Notable : Importance.Minor, a.id.Ref);
            e.actor = a.id;
            e.actorName = a.name.Display;
            e.descriptorKey = "Equipment";
            e.reasonKey = sim.equipment.tier.ToString();
            ctx.bus.Publish(e);
            return true;
        }

        // ================================================================== repair, diagnostics

        /// <summary>
        /// Load reconciliation: keeps every contractor's career state inside its bounds and the fame band
        /// consistent with the score (never lowering fame), and applies the result of a finished operation
        /// that somehow missed it. Returns the number of repairs; each is described in <paramref name="findings"/>.
        /// </summary>
        public int Validate(List<string> findings)
        {
            int repaired = 0;
            List<NetworkActor> all = ctx.actors.actors;
            for (int i = 0; i < all.Count; i++)
            {
                NetworkActor a = all[i];
                PublicReputation rep = a.reputation;
                if (rep != null)
                {
                    int floor = CareerPolicy.FloorOf(rep.fame);
                    int fixedScore = Math.Max(rep.score, floor);
                    if (fixedScore != rep.score || CareerPolicy.FameFor(fixedScore) != rep.fame)
                    {
                        if (findings != null) findings.Add("Actor " + a.id + ": reputation score " + rep.score + " did not match fame " + rep.fame + "; reconciled to " + fixedScore + " (fame never lowered).");
                        rep.SetScore(fixedScore);
                        repaired++;
                    }
                }
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                if (sim == null) continue;
                if (sim.equipment.tier < CareerPolicy.MinTier || sim.equipment.tier > CareerPolicy.MaxTier)
                {
                    if (findings != null) findings.Add("Contractor " + a.id + ": equipment tier " + sim.equipment.tier + " outside 1.." + CareerPolicy.MaxTier + "; clamped.");
                    sim.equipment.tier = CareerPolicy.ClampTier(sim.equipment.tier);
                    sim.MarkDirty();
                    repaired++;
                }
                if (sim.funds > CareerPolicy.FundsBound || sim.funds < -CareerPolicy.FundsBound)
                {
                    if (findings != null) findings.Add("Contractor " + a.id + ": funds " + sim.funds + " outside the bound; clamped.");
                    sim.funds = CareerPolicy.AddFunds(0, sim.funds);
                    repaired++;
                }
                CareerRecord r = sim.career;
                if (r.triumphs < 0 || r.successes < 0 || r.partials < 0 || r.failures < 0 || r.disasters < 0 || r.legacyResolved < 0 || r.careerEarnings < 0
                    || r.casualtiesTaken < 0 || r.peopleLost < 0 || r.captured < 0 || r.missing < 0 || r.reputationEarned < 0 || r.advancementCount < 0
                    || r.highestDanger < 0 || r.highestDanger > 1000)
                {
                    if (findings != null) findings.Add("Contractor " + a.id + ": career record had a negative or impossible counter; clamped.");
                    r.legacyResolved = Math.Max(0, r.legacyResolved);
                    r.triumphs = Math.Max(0, r.triumphs);
                    r.successes = Math.Max(0, r.successes);
                    r.partials = Math.Max(0, r.partials);
                    r.failures = Math.Max(0, r.failures);
                    r.disasters = Math.Max(0, r.disasters);
                    r.careerEarnings = Math.Max(0, r.careerEarnings);
                    r.casualtiesTaken = Math.Max(0, r.casualtiesTaken);
                    r.peopleLost = Math.Max(0, r.peopleLost);
                    r.captured = Math.Max(0, r.captured);
                    r.missing = Math.Max(0, r.missing);
                    r.reputationEarned = Math.Max(0, r.reputationEarned);
                    r.advancementCount = Math.Max(0, r.advancementCount);
                    r.highestDanger = Math.Min(1000, Math.Max(0, r.highestDanger));
                    repaired++;
                }
            }
            for (int i = 0; i < ctx.operations.operations.Count; i++)
            {
                Operation op = ctx.operations.operations[i];
                if (op.quarantinedReason != null) continue;
                if (op.careerOutcomeApplied && !op.careerEligible)
                {
                    if (findings != null) findings.Add("Operation " + op.id + ": carries a career result although it predates careers (reported, not changed).");
                    continue;
                }
                // Finished with a committed outcome but its career result never committed (it failed, or it was missed):
                // apply it now. Idempotent: the flag means the durable career mutation really committed.
                if (op.IsFinished && op.careerEligible && !op.careerOutcomeApplied && op.outcome != null)
                {
                    if (CommitOutcome(op, WasWrittenOff(op)))
                    {
                        if (findings != null) findings.Add("Operation " + op.id + ": finished without its career result; applied now.");
                        repaired++;
                    }
                    else if (findings != null)
                    {
                        // Visible, never silently dropped and never marked applied: a missing contractor or simulation stays unresolved.
                        findings.Add("Operation " + op.id + ": finished with a career result that could not be applied (see the warning); it stays unapplied and is retried by the next validation.");
                    }
                }
            }
            return repaired;
        }

        /// <summary>The player-readable-by-developers career readout (dev tools only).</summary>
        public string Describe(NetworkActor a)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            if (sim == null) return "[TheNetwork] " + a + " has no contractor simulation.";
            CareerRecord r = sim.career;
            int cost, reserve;
            AdvancementBlock block = BlockedBy(a, out cost, out reserve);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] Career of " + a + " (" + a.status + ")");
            sb.AppendLine("  fame " + a.reputation.fame + " (score " + a.reputation.score + ", next band at " + (CareerPolicy.NextBandAt(a.reputation.fame) == int.MaxValue ? "-" : CareerPolicy.NextBandAt(a.reputation.fame).ToString()) + "), experience " + ContractorService.Experience(a) + ", stage " + sim.careerStage);
            sb.AppendLine("  jobs: " + sim.opsCompleted + " resolved (" + r.legacyResolved + " before records; " + r.Classified + " classified: " + r.triumphs + " triumph, " + r.successes + " success, " + r.partials + " partial, " + r.failures + " failure, " + r.disasters + " disaster)");
            sb.AppendLine("  record: highest danger " + (r.highestDanger / 1000f).ToString("0.00") + ", earnings " + r.careerEarnings + ", casualties " + r.casualtiesTaken + " (lost " + r.peopleLost + ", captured " + r.captured + ", missing " + r.missing + "), reputation earned " + r.reputationEarned + ", last outcome tick " + r.lastOutcomeTick);
            sb.AppendLine("  funds " + sim.funds + ", operating reserve " + OperatingReserve(a) + ", equipment tier " + sim.equipment.tier + " condition " + sim.equipment.condition.ToString("0.00"));
            sb.AppendLine("  need " + CurrentNeed(a) + ", tags [" + string.Join(", ", Tags(a).ToArray()) + "]");
            sb.AppendLine("  advancement: " + r.advancementCount + " so far, last at tick " + r.lastAdvancementTick + "; now " + (block == AdvancementBlock.None ? "possible (cost " + cost + ")" : "blocked: " + block + (cost > 0 ? " (cost " + cost + ", reserve " + reserve + ")" : "")));
            return sb.ToString();
        }
    }
}
