using System.Collections.Generic;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Domain.Physical
{
    // The Physical Episode (PHYSICAL_LIFECYCLE § 5.3, § 8.1): the one durable owner of PRESENCE truth while some of an actor's
    // people are (or were about to be) physically present. It owns who is out there, under which cause, since when, what was
    // observed, and whether reconciliation and each post-commit stage has completed. It has NO consequences of its own:
    // death, wounds, headcount, morale, succession, career and money stay with the existing services (§ 15.5).
    //
    // Phase 3.0 builds this brain over a port; no production path creates, spawns or passes a pawn (§ 23, gating).

    /// <summary>The durable Episode machine (§ 8.1). Values are persisted.</summary>
    public enum EpisodeState : byte
    {
        Planned = 0,
        Open = 1,
        Closed = 2,
        Quarantined = 3
    }

    /// <summary>A member's progress (§ 5.3). Values are persisted.</summary>
    public enum MemberState : byte
    {
        Planned = 0,
        Created = 1,
        Present = 2,
        Done = 3
    }

    /// <summary>A member's terminal outcome, set once (§ 5.3, § 15.3). Values are persisted.</summary>
    public enum MemberOutcome : byte
    {
        Pending = 0,
        Returned = 1,
        Killed = 2,
        HeldByPlayer = 3,
        JoinedPlayer = 4,
        Kidnapped = 5,
        HeldByOther = 6,
        Missing = 7,
        Lost = 8,
        NeverPlaced = 9,

        /// <summary>Prepare-for-removal settle (§ 20): a member that was not terminal; nothing physical is invented about it.</summary>
        Detached = 10
    }

    /// <summary>One classification per pawn, evaluated in this order, first match wins (§ 9.3). Values are persisted.</summary>
    public enum ObservedKind : byte
    {
        None = 0,
        Gone = 1,
        Dead = 2,
        HeldByPlayer = 3,
        JoinedPlayer = 4,
        Kidnapped = 5,
        HeldByOther = 6,
        InCaravan = 7,
        InTransport = 8,
        Spawned = 9,
        WorldFree = 10,
        WorldOther = 11,
        Unknown = 12
    }

    /// <summary>What vanilla holds a person as, while their custody is OutOfCustody (§ 8.2). Values are persisted.</summary>
    public enum HeldKind : byte
    {
        None = 0,
        PlayerPrisoner = 1,
        PlayerSlave = 2,
        PlayerColonist = 3,
        Kidnapped = 4,
        OtherFaction = 5,
        PlayerCaravan = 6,
        Transport = 7,
        Unaffiliated = 8,
        Unknown = 9
    }

    /// <summary>
    /// The operational role of a person or seat (§ 6.6): a semantic job, never a class, perk or bonus. <see cref="Unset"/> means "not
    /// yet stored", never "not yet decided": a role is a pure function of immutable origin facts (<see cref="RoleDerivation"/>), so it is
    /// the same whenever it is first stored. Persisted BY NAME (NetScribe.LookEnum), so adding a value needs no format change and an
    /// older build reads an unknown name as Unset. Values are never renumbered.
    /// </summary>
    public enum OperationalRole : byte
    {
        Unset = 0,
        Leader = 1,
        Marksman = 2,
        Rifleman = 3,
        Heavy = 4,
        Breacher = 5,
        Medic = 6,
        Scout = 7,
        Engineer = 8,
        Technician = 9,
        Logistician = 10,
        Negotiator = 11,
        Specialist = 12
    }

    /// <summary>The episode's result for a linked operation, recorded in the commit (§ 5.3 "operation marker").</summary>
    public enum PhysicalResolution : byte
    {
        None = 0,
        Found = 1,
        WrittenOff = 2
    }

    /// <summary>
    /// The binding of a person (or an episode slot) to one pawn (§ 7, P3-INV-006): the pawn POINTER (saved with
    /// saveDestroyedThings, so a dead pawn still resolves), its thingIDNumber (a binding attribute, never identity), the def name
    /// (a sanity check), when it was bound, and <see cref="agedThroughTick"/> (§ 6.4). Write-once per character. In Phase 3.0
    /// the pointer is always null: nothing binds a real pawn; the scriptable test port identifies its tokens by thingIDNumber.
    /// </summary>
    public sealed class PawnRef : IExposable
    {
        public Pawn pawn;
        public int thingIdNumber;
        public string defName;
        public int boundTick = -1;

        /// <summary>The tick up to which the pawn's biological age has been brought current (§ 6.4). Set when it becomes Stored and by the catch-up.</summary>
        public int agedThroughTick = -1;

        public bool IsBound => thingIdNumber > 0 || pawn != null;

        public PawnRef Copy()
        {
            return new PawnRef { pawn = pawn, thingIdNumber = thingIdNumber, defName = defName, boundTick = boundTick, agedThroughTick = agedThroughTick };
        }

        public bool SameBinding(PawnRef other)
        {
            if (other == null) return false;
            if (pawn != null || other.pawn != null) return ReferenceEquals(pawn, other.pawn);
            return thingIdNumber > 0 && thingIdNumber == other.thingIdNumber;
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn", true);
            Scribe_Values.Look(ref thingIdNumber, "thingId", 0);
            Scribe_Values.Look(ref defName, "def");
            Scribe_Values.Look(ref boundTick, "boundTick", -1);
            Scribe_Values.Look(ref agedThroughTick, "agedThrough", -1);
        }

        public override string ToString()
        {
            return IsBound ? "pawn#" + thingIdNumber + (defName != null ? " " + defName : "") : "unbound";
        }
    }

    /// <summary>What caused the appearance: at least one link, or an explicit dev key (§ 5.3).</summary>
    public sealed class EpisodeCause : IExposable
    {
        public ContractId contract;
        public OperationId operation;
        public OpportunityId opportunity;
        public string devKey;

        public bool IsValid => contract.IsValid || operation.IsValid || opportunity.IsValid || !string.IsNullOrEmpty(devKey);

        public void ExposeData()
        {
            NetScribe.Look(ref contract, "contract");
            NetScribe.Look(ref operation, "operation");
            NetScribe.Look(ref opportunity, "opportunity");
            Scribe_Values.Look(ref devKey, "dev");
        }
    }

    /// <summary>
    /// One publication the PUBLISH stage owes (§ 15.2): a compact typed spec written by the commit into the episode's outbox,
    /// enough to build the event later. Stored, never re-derived (a tuning change between save and load must not change it).
    /// </summary>
    public sealed class PublicationSpec : IExposable
    {
        public string typeKey;
        public Importance importance = Importance.Minor;
        public List<EntityRef> subjects = new List<EntityRef>();
        public ActorId actor;
        public string actorName;
        public CharacterId character;
        public string characterName;
        public CharacterId successor;
        public string successorName;
        public ContractId contract;
        public OperationId operation;
        public EpisodeId episode;
        public int killed;
        public int wounded;
        public int captured;
        public int missing;
        public int returned;
        public int neverPlaced;
        public string purposeKey;
        public string reasonKey;
        public string descriptorKey;
        public bool leader;

        public void ExposeData()
        {
            Scribe_Values.Look(ref typeKey, "type");
            NetScribe.LookEnum(ref importance, "importance", Importance.Minor);
            NetScribe.LookEntityRefList(ref subjects, "subjects");
            NetScribe.Look(ref actor, "actor");
            Scribe_Values.Look(ref actorName, "actorName");
            NetScribe.Look(ref character, "character");
            Scribe_Values.Look(ref characterName, "characterName");
            NetScribe.Look(ref successor, "successor");
            Scribe_Values.Look(ref successorName, "successorName");
            NetScribe.Look(ref contract, "contract");
            NetScribe.Look(ref operation, "operation");
            NetScribe.Look(ref episode, "episode");
            Scribe_Values.Look(ref killed, "killed", 0);
            Scribe_Values.Look(ref wounded, "wounded", 0);
            Scribe_Values.Look(ref captured, "captured", 0);
            Scribe_Values.Look(ref missing, "missing", 0);
            Scribe_Values.Look(ref returned, "returned", 0);
            Scribe_Values.Look(ref neverPlaced, "neverPlaced", 0);
            Scribe_Values.Look(ref purposeKey, "purpose");
            Scribe_Values.Look(ref reasonKey, "reason");
            Scribe_Values.Look(ref descriptorKey, "descriptor");
            Scribe_Values.Look(ref leader, "leader", false);
            if (Scribe.mode == LoadSaveMode.LoadingVars && subjects == null) subjects = new List<EntityRef>();
        }

        public override string ToString()
        {
            return typeKey + (actor.IsValid ? " " + actor : "") + (character.IsValid ? " " + character : "");
        }
    }

    /// <summary>One person in an episode: a named character, or an anonymous slot (a tier and an index, never a fake record).</summary>
    public sealed class EpisodeMember : IExposable
    {
        public CharacterId character;
        public int slot;
        public Tier tier = Tier.Regular;
        public OperationalRole seatRole = OperationalRole.Unset;

        /// <summary>The binding (§ 7). For a named member it mirrors the character's own PawnRef (one pawn for life).</summary>
        public PawnRef pawn;

        /// <summary>How many of this member's ordered release actions have SUCCEEDED: the RELEASE cursor (§ 8.1).</summary>
        public byte releaseStep;

        public MemberState state = MemberState.Planned;
        public MemberOutcome outcome = MemberOutcome.Pending;

        /// <summary>The classification the terminal outcome was decided from (diagnostics; it also selects the release actions).</summary>
        public ObservedKind observed = ObservedKind.None;

        public int observedTick = -1;

        public bool IsNamed => character.IsValid;
        public bool IsBound => pawn != null && pawn.IsBound;

        public void ExposeData()
        {
            NetScribe.Look(ref character, "character");
            Scribe_Values.Look(ref slot, "slot", 0);
            NetScribe.LookEnum(ref tier, "tier", Tier.Regular);
            NetScribe.LookEnum(ref seatRole, "seatRole", OperationalRole.Unset);
            Scribe_Deep.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref releaseStep, "releaseStep", (byte)0);
            NetScribe.LookEnum(ref state, "state", MemberState.Planned);
            NetScribe.LookEnum(ref outcome, "outcome", MemberOutcome.Pending);
            NetScribe.LookEnum(ref observed, "observed", ObservedKind.None);
            Scribe_Values.Look(ref observedTick, "observedTick", -1);
        }

        public override string ToString()
        {
            return (IsNamed ? character.ToString() : "slot" + slot + "/" + tier) + " " + state + "/" + outcome;
        }
    }

    /// <summary>
    /// One physical appearance of an actor's people (§ 5.3). The exactly-once flag <see cref="consequencesApplied"/> is the LAST
    /// statement of the guarded commit; each post-commit stage has its OWN explicit marker, written only after the stage's work
    /// completed and never inferred from side-effect state (§ 8.1): <see cref="releaseApplied"/>, <see cref="followUpApplied"/>,
    /// and for PUBLISH the persisted outbox, <see cref="publishCursor"/> and <see cref="publishedTick"/>.
    /// </summary>
    public sealed class PhysicalEpisode : IExposable
    {
        /// <summary>The outbox is bounded (§ 15.2).</summary>
        public const int MaxPublications = 16;

        /// <summary>Bounded reconcile / stage retries before the episode is reported as stuck (never auto-completed).</summary>
        public const int MaxAttempts = 5;

        public EpisodeId id;
        public ActorId actor;
        public string purposeKey;
        public EpisodeCause cause = new EpisodeCause();
        public EpisodeState state = EpisodeState.Planned;
        public int createdTick = -1;
        public int openedTick = -1;
        public int closedTick = -1;
        public string closeReasonKey;
        public string quarantineKey;

        public bool consequencesApplied;
        public int committedTick = -1;

        public bool releaseApplied;
        public int releasedTick = -1;

        public bool followUpApplied;

        public List<PublicationSpec> publications = new List<PublicationSpec>();
        public int publishCursor;
        public int publishedTick = -1;

        public int attempts;
        public string lastError;

        public TileRef whereTile;
        public int whereMapId = -1;
        public FactionRef faction;
        public int seed;
        public List<EpisodeMember> members = new List<EpisodeMember>();

        public bool IsActive => state == EpisodeState.Planned || state == EpisodeState.Open;

        /// <summary>PUBLISH has finished: every spec was accepted by the bus (and the outbox cleared).</summary>
        public bool PublishDone => publishedTick >= 0;

        /// <summary>Closed and every post-commit stage completed: the episode holds nothing more for anyone.</summary>
        public bool IsComplete => state == EpisodeState.Closed && releaseApplied && followUpApplied && PublishDone;

        /// <summary>Closed with consequences applied but a stage still owed (the finish-pending pass resumes it).</summary>
        public bool HasPendingStage => state == EpisodeState.Closed && !IsComplete;

        public EpisodeMember MemberFor(CharacterId c)
        {
            for (int i = 0; i < members.Count; i++) if (members[i].character == c) return members[i];
            return null;
        }

        public void ExposeData()
        {
            NetScribe.Look(ref id, "id");
            NetScribe.Look(ref actor, "actor");
            Scribe_Values.Look(ref purposeKey, "purpose");
            Scribe_Deep.Look(ref cause, "cause");
            NetScribe.LookEnum(ref state, "state", EpisodeState.Quarantined);
            Scribe_Values.Look(ref createdTick, "created", -1);
            Scribe_Values.Look(ref openedTick, "opened", -1);
            Scribe_Values.Look(ref closedTick, "closed", -1);
            Scribe_Values.Look(ref closeReasonKey, "closeReason");
            Scribe_Values.Look(ref quarantineKey, "quarantine");
            Scribe_Values.Look(ref consequencesApplied, "consequencesApplied", false);
            Scribe_Values.Look(ref committedTick, "committedTick", -1);
            Scribe_Values.Look(ref releaseApplied, "releaseApplied", false);
            Scribe_Values.Look(ref releasedTick, "releasedTick", -1);
            Scribe_Values.Look(ref followUpApplied, "followUpApplied", false);
            NetScribe.LookListTolerant(ref publications, "publications", "episodes.publications");
            Scribe_Values.Look(ref publishCursor, "publishCursor", 0);
            Scribe_Values.Look(ref publishedTick, "publishedTick", -1);
            Scribe_Values.Look(ref attempts, "attempts", 0);
            Scribe_Values.Look(ref lastError, "lastError");
            Scribe_Deep.Look(ref whereTile, "whereTile");
            Scribe_Values.Look(ref whereMapId, "whereMap", -1);
            Scribe_Deep.Look(ref faction, "faction");
            Scribe_Values.Look(ref seed, "seed", 0);
            NetScribe.LookListTolerant(ref members, "members", "episodes.members");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (cause == null) cause = new EpisodeCause();
                if (publications == null) publications = new List<PublicationSpec>();
                if (members == null) members = new List<EpisodeMember>();
            }
        }

        public override string ToString()
        {
            return id + " " + actor + " " + purposeKey + " " + state + (closeReasonKey != null ? "(" + closeReasonKey + ")" : "") + " members " + members.Count;
        }
    }

    /// <summary>
    /// The Episode store, in the reserved <c>deployments</c> slot (label unchanged; an old save's empty node loads as an empty
    /// store). The indexes are rebuildable derived state, never durable truth.
    /// </summary>
    public sealed class EpisodeStore : IExposable
    {
        public List<PhysicalEpisode> episodes = new List<PhysicalEpisode>();

        private readonly Dictionary<int, PhysicalEpisode> byId = new Dictionary<int, PhysicalEpisode>();

        /// <summary>Actor id → number of its episodes that are not complete (Planned, Open, Quarantined, or Closed with a stage owed).</summary>
        private readonly Dictionary<int, int> incompleteByActor = new Dictionary<int, int>();

        public int Count => episodes.Count;

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref episodes, "episodes", "episodes");
            if (Scribe.mode == LoadSaveMode.LoadingVars && episodes == null) episodes = new List<PhysicalEpisode>();
        }

        public void RebuildIndex()
        {
            byId.Clear();
            incompleteByActor.Clear();
            for (int i = 0; i < episodes.Count; i++)
            {
                PhysicalEpisode e = episodes[i];
                if (e == null || !e.id.IsValid) continue;
                byId[e.id.Value] = e;
                if (!e.IsComplete)
                {
                    int n;
                    incompleteByActor.TryGetValue(e.actor.Value, out n);
                    incompleteByActor[e.actor.Value] = n + 1;
                }
            }
        }

        public void Add(PhysicalEpisode e)
        {
            episodes.Add(e);
            RebuildIndex();
        }

        public PhysicalEpisode Get(EpisodeId id)
        {
            PhysicalEpisode e;
            return id.IsValid && byId.TryGetValue(id.Value, out e) ? e : null;
        }

        public void Remove(PhysicalEpisode e)
        {
            episodes.Remove(e);
            RebuildIndex();
        }

        /// <summary>O(1): does this actor have an episode that is not complete? (An open episode counts as a job.)</summary>
        public bool HasIncomplete(ActorId actor)
        {
            int n;
            return incompleteByActor.TryGetValue(actor.Value, out n) && n > 0;
        }

        /// <summary>Episodes that still need work (the watch and the finish-pending pass). Bounded: never a scan of anything else.</summary>
        public List<PhysicalEpisode> Incomplete()
        {
            List<PhysicalEpisode> list = new List<PhysicalEpisode>();
            for (int i = 0; i < episodes.Count; i++) if (episodes[i] != null && !episodes[i].IsComplete) list.Add(episodes[i]);
            return list;
        }

        public int MaxId()
        {
            int max = 0;
            for (int i = 0; i < episodes.Count; i++) if (episodes[i] != null && episodes[i].id.Value > max) max = episodes[i].id.Value;
            return max;
        }
    }
}
