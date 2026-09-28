using System.Collections.Generic;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Knowledge;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Domain.Operations
{
    // Execution kept separate from agreement (DATA_MODEL § 10, STATE_MACHINES § 6). A procurement
    // contract runs through one abstract Operation: no pawn, no map, no combat. Persisted type names are
    // frozen once shipped.

    public enum OpPhase : byte
    {
        Preparing = 0,
        Transit = 1,
        Engaged = 2,
        Returning = 3,
        Delivering = 4,
        Done = 5
    }

    public enum OpStatus : byte
    {
        Running = 0,
        Delayed = 1,
        Troubled = 2,
        Physical = 3,   // reserved for Phase 3 deployments; never entered in Phase 2
        Resolved = 4,
        Aborted = 5
    }

    /// <summary>Coarse outcome bands (SIMULATION § 3.3). Ordered from best to worst.</summary>
    public enum OutcomeBand : byte
    {
        Triumph = 0,
        Success = 1,
        CostlySuccess = 2,
        Partial = 3,
        Failure = 4,
        Disaster = 5
    }

    /// <summary>A scheduled step. The scheduler job mirrors it; the checkpoint is the persisted truth.</summary>
    public sealed class Checkpoint : IExposable
    {
        public const string Prep = "prep";
        public const string Arrive = "arrive";
        public const string Resolve = "resolve";
        public const string Return = "return";
        public const string Deliver = "deliver";

        public string key;
        public int dueTick;
        public bool done;

        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref dueTick, "due", 0);
            Scribe_Values.Look(ref done, "done", false);
        }
    }

    /// <summary>
    /// Snapshot taken when engagement begins (SIMULATION § 3.2). Reloading or world changes afterwards
    /// cannot change the outcome.
    /// </summary>
    public sealed class ResolverInputs : IExposable
    {
        public float forcePower;
        public float threatPower;
        public float preparedness;
        public float intelQuality = 0.5f;
        public float logisticsPenalty;
        public float moraleFactor = 1f;
        public bool reckless;
        public float caution = 0.5f;
        public float cruelty;
        public float professionalism = 0.5f;
        public float sponsorship;
        public float leadership;
        public float specialization;
        public string opposition;
        public float difficulty;
        public bool market = true;
        public int headcount;
        public int frozenTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref forcePower, "force", 0f);
            Scribe_Values.Look(ref threatPower, "threat", 0f);
            Scribe_Values.Look(ref preparedness, "preparedness", 0f);
            Scribe_Values.Look(ref intelQuality, "intel", 0.5f);
            Scribe_Values.Look(ref logisticsPenalty, "logistics", 0f);
            Scribe_Values.Look(ref moraleFactor, "morale", 1f);
            Scribe_Values.Look(ref reckless, "reckless", false);
            Scribe_Values.Look(ref caution, "caution", 0.5f);
            Scribe_Values.Look(ref cruelty, "cruelty", 0f);
            Scribe_Values.Look(ref professionalism, "professionalism", 0.5f);
            Scribe_Values.Look(ref sponsorship, "sponsorship", 0f);
            Scribe_Values.Look(ref leadership, "leadership", 0f);
            Scribe_Values.Look(ref specialization, "specialization", 0f);
            Scribe_Values.Look(ref opposition, "opposition");
            Scribe_Values.Look(ref difficulty, "difficulty", 0f);
            Scribe_Values.Look(ref market, "market", true);
            Scribe_Values.Look(ref headcount, "headcount", 0);
            Scribe_Values.Look(ref frozenTick, "tick", 0);
        }
    }

    /// <summary>Committed once, at the resolve checkpoint. Later checkpoints only apply it.</summary>
    public sealed class OperationOutcome : IExposable
    {
        public OutcomeBand band = OutcomeBand.Success;
        public float edge;
        public float roll;
        public bool retreated;
        public int requested;
        public int secured;
        public List<TierCount> killed = new List<TierCount>();
        public List<TierCount> wounded = new List<TierCount>();
        public List<TierCount> captured = new List<TierCount>();
        public List<TierCount> missing = new List<TierCount>();
        public List<CharacterFate> fates = new List<CharacterFate>();
        public int woundDays = 8;
        public int delayTicks;
        public List<TopicGain> knowledgeGains = new List<TopicGain>();

        /// <summary>The exact goods secured (def, stuff, count, quality), delivered as committed.</summary>
        public List<ItemPayload> securedPayload = new List<ItemPayload>();

        /// <summary>Missing / Captured / Stranded when the operation cannot come home on its own; else null.</summary>
        public string troubledKey;

        /// <summary>A narrative key for letters and history ("market.robbed", "market.ambushed", …).</summary>
        public string flavorKey;

        /// <summary>The contractor reported mid-operation that the job is much worse than expected.</summary>
        public bool worseThanExpected;

        public int committedTick;

        public int Killed => CasualtyReport.Sum(killed) + CountFates(Fate.Killed);
        public int Wounded => CasualtyReport.Sum(wounded) + CountFates(Fate.Wounded);
        public int Captured => CasualtyReport.Sum(captured) + CountFates(Fate.Captured);
        public int Missing => CasualtyReport.Sum(missing) + CountFates(Fate.Missing);

        private int CountFates(Fate f)
        {
            int n = 0;
            for (int i = 0; i < fates.Count; i++) if (fates[i].fate == f) n++;
            return n;
        }

        public CasualtyReport ToReport()
        {
            return new CasualtyReport { killed = killed, wounded = wounded, captured = captured, missing = missing, fates = fates, woundDays = woundDays };
        }

        public void ExposeData()
        {
            NetScribe.LookEnum(ref band, "band", OutcomeBand.Failure);
            Scribe_Values.Look(ref edge, "edge", 0f);
            Scribe_Values.Look(ref roll, "roll", 0f);
            Scribe_Values.Look(ref retreated, "retreated", false);
            Scribe_Values.Look(ref requested, "requested", 0);
            Scribe_Values.Look(ref secured, "secured", 0);
            NetScribe.LookListTolerant(ref killed, "killed", "operations.killed");
            NetScribe.LookListTolerant(ref wounded, "wounded", "operations.wounded");
            NetScribe.LookListTolerant(ref captured, "captured", "operations.captured");
            NetScribe.LookListTolerant(ref missing, "missing", "operations.missing");
            NetScribe.LookListTolerant(ref fates, "fates", "operations.fates");
            Scribe_Values.Look(ref woundDays, "woundDays", 8);
            Scribe_Values.Look(ref delayTicks, "delay", 0);
            NetScribe.LookListTolerant(ref knowledgeGains, "knowledge", "operations.knowledge");
            NetScribe.LookListTolerant(ref securedPayload, "payload", "operations.payload");
            Scribe_Values.Look(ref troubledKey, "troubled");
            Scribe_Values.Look(ref flavorKey, "flavor");
            Scribe_Values.Look(ref worseThanExpected, "worse", false);
            Scribe_Values.Look(ref committedTick, "tick", 0);
        }
    }

    /// <summary>
    /// An operation's hidden geography (SPATIAL § 6), committed when it starts. The existing checkpoints
    /// stay the timeline: the contractor sets out at Prep, is at the work region from Arrive, and heads
    /// for <see cref="returnTo"/> after Resolve. It explains WHERE; the resolver still decides WHAT.
    /// Null on operations started before Phase 2.5 (their lifecycle is unchanged).
    /// </summary>
    public sealed class OperationSpatialPlan : IExposable
    {
        /// <summary>The contractor's real anchor when the operation started.</summary>
        public TileRef origin;

        /// <summary>The approximate area where the work takes place (not a site, not a world object).</summary>
        public TileRef workRegion;

        /// <summary>Where the group heads after the work.</summary>
        public TileRef returnTo;

        /// <summary>Where trouble or disaster struck (committed at resolution; consequences read it).</summary>
        public TileRef incident;

        /// <summary>A concurrent job of an organization: a detachment went; the main body's anchor did not move.</summary>
        public bool detached;

        /// <summary>Why the plan degraded (no route, no work region), or null.</summary>
        public string fallbackKey;

        public void ExposeData()
        {
            Scribe_Deep.Look(ref origin, "origin");
            Scribe_Deep.Look(ref workRegion, "work");
            Scribe_Deep.Look(ref returnTo, "returnTo");
            Scribe_Deep.Look(ref incident, "incident");
            Scribe_Values.Look(ref detached, "detached", false);
            Scribe_Values.Look(ref fallbackKey, "fallback");
        }
    }

    public sealed class Operation : IExposable
    {
        public const string ProcureKind = "Procure";

        public OperationId id;
        public ContractId contract;
        public ActorId contractor;
        public string contractorName;
        public string kindKey = ProcureKind;
        public OpPhase phase = OpPhase.Preparing;
        public OpStatus status = OpStatus.Running;
        public List<TierCount> forces = new List<TierCount>();
        public List<CharacterId> characters = new List<CharacterId>();

        /// <summary>"market" for ordinary procurement; a site target is a later-phase objective.</summary>
        public string targetKey = "market";

        public int startedTick;
        public int endedTick = -1;
        public int plannedTicks;
        public List<Checkpoint> checkpoints = new List<Checkpoint>();
        public int seed;
        public int rerollNonce;
        public float danger;
        public ResolverInputs frozenInputs;
        public OperationOutcome outcome;
        public int troubledDeadlineTick = -1;
        public string abortReasonKey;
        public bool outcomeApplied;
        public string quarantinedReason;

        /// <summary>Hidden geography (Phase 2.5); null for operations from an older save.</summary>
        public OperationSpatialPlan spatial;

        public bool IsFinished => status == OpStatus.Aborted || phase == OpPhase.Done;

        public Checkpoint Find(string key)
        {
            for (int i = 0; i < checkpoints.Count; i++) if (checkpoints[i].key == key) return checkpoints[i];
            return null;
        }

        public ForceCommitment Commitment()
        {
            return new ForceCommitment { forces = forces, characters = characters };
        }

        public void ExposeData()
        {
            NetScribe.Look(ref id, "id");
            NetScribe.Look(ref contract, "contract");
            NetScribe.Look(ref contractor, "contractor");
            Scribe_Values.Look(ref contractorName, "contractorName");
            Scribe_Values.Look(ref kindKey, "kind");
            bool badPhase = false, badStatus = false;
            NetScribe.LookEnum(ref phase, "phase", OpPhase.Preparing, ref badPhase);
            NetScribe.LookEnum(ref status, "status", OpStatus.Running, ref badStatus);
            NetScribe.LookListTolerant(ref forces, "forces", "operations.forces");
            NetScribe.LookIntList(ref characters, "characters", c => c.Value, v => new CharacterId(v));
            Scribe_Values.Look(ref targetKey, "target", "market");
            Scribe_Values.Look(ref startedTick, "startedTick", 0);
            Scribe_Values.Look(ref endedTick, "endedTick", -1);
            Scribe_Values.Look(ref plannedTicks, "planned", 0);
            NetScribe.LookListTolerant(ref checkpoints, "checkpoints", "operations.checkpoints");
            Scribe_Values.Look(ref seed, "seed", 0);
            Scribe_Values.Look(ref rerollNonce, "rerollNonce", 0);
            Scribe_Values.Look(ref danger, "danger", 0f);
            Scribe_Deep.Look(ref frozenInputs, "inputs");
            Scribe_Deep.Look(ref outcome, "outcome");
            Scribe_Values.Look(ref troubledDeadlineTick, "troubledDeadline", -1);
            Scribe_Values.Look(ref abortReasonKey, "abortReason");
            Scribe_Values.Look(ref outcomeApplied, "applied", false);
            Scribe_Values.Look(ref quarantinedReason, "quarantined");
            Scribe_Deep.Look(ref spatial, "spatial");
            if (Scribe.mode == LoadSaveMode.LoadingVars && (badPhase || badStatus) && quarantinedReason == null)
            {
                quarantinedReason = badPhase ? "MalformedEnum:phase" : "MalformedEnum:status";
            }
        }

        public override string ToString()
        {
            return id + " " + contractorName + " " + phase + "/" + status + (outcome != null ? " " + outcome.band : "");
        }
    }

    /// <summary>The "operations" store slot (DATA_MODEL § 3).</summary>
    public sealed class OperationStore : IExposable
    {
        public List<Operation> operations = new List<Operation>();

        private readonly Dictionary<int, Operation> byId = new Dictionary<int, Operation>();

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref operations, "operations", "operations");
        }

        public void RebuildIndex()
        {
            byId.Clear();
            for (int i = 0; i < operations.Count; i++) if (operations[i] != null && operations[i].id.IsValid) byId[operations[i].id.Value] = operations[i];
        }

        public void Add(Operation o)
        {
            operations.Add(o);
            byId[o.id.Value] = o;
        }

        public Operation Get(OperationId id)
        {
            Operation o;
            return id.IsValid && byId.TryGetValue(id.Value, out o) ? o : null;
        }

        public void Remove(Operation o)
        {
            operations.Remove(o);
            byId.Remove(o.id.Value);
        }

        public int Count => operations.Count;
    }
}
