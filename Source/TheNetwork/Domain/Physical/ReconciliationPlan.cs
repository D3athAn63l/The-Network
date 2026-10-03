using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Operations;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;

namespace TheNetwork.Domain.Physical
{
    /// <summary>
    /// The closed vocabulary of the atomic commit (PHYSICAL_LIFECYCLE § 15.2 step 5, § 15.6): every durable assignment a
    /// reconciliation may make, and nothing else. There is deliberately no kind for an event, a job, a letter, a log line, a
    /// service call or a port action: those cannot be expressed inside the commit at all (VALIDATE refuses an undefined kind).
    /// </summary>
    public enum CommitOpKind : byte
    {
        /// <summary>The member is Done with its terminal outcome (set once).</summary>
        MemberDone = 0,

        /// <summary>Named death (§ 10.1): the shared fate rule, plus custody Released.</summary>
        CharacterKilled = 1,

        /// <summary>The shared wound rule (abstract recovery: <c>woundedUntilTick</c>).</summary>
        CharacterWounded = 2,

        /// <summary>The shared capture rule (the abstract casualty vocabulary; physical held custody is 3.2).</summary>
        CharacterCaptured = 3,

        /// <summary>The shared missing rule (the abstract casualty vocabulary).</summary>
        CharacterMissing = 4,

        /// <summary>Gone with no evidence (§ 17): status Lost, custody Lost. Never "home", never regenerated.</summary>
        CharacterLost = 5,

        /// <summary>Returned: custody Stored, and the binding's aging bookmark set (§ 6.4).</summary>
        CharacterStored = 6,

        /// <summary>Never placed: custody back to what it was before the episode (Stored when bound, else Unmaterialized).</summary>
        CharacterReverted = 7,

        /// <summary>Prepare-for-removal settle (§ 20): a non-terminal person becomes OutOfCustody(Unknown); nothing is invented.</summary>
        CharacterDetached = 8,

        /// <summary>Clears the membership link: ONLY in a commit with no release action at all (§ 8.1 rule 5).</summary>
        CharacterUnlink = 9,

        /// <summary>An anonymous member comes back to the organization's headcount (healthy, or a wounded bucket).</summary>
        AnonymousBack = 10,

        /// <summary>An anonymous member is lost (killed or gone): it leaves the checked-out headcount and nothing returns.</summary>
        AnonymousLost = 11,

        /// <summary>The shared morale shock by loss share (§ 15.5), the share computed from the state the earlier steps left.</summary>
        MoraleShock = 12,

        /// <summary>The shared morale descriptor re-evaluation, with its publication spec when it shifts.</summary>
        DescriptorCheck = 13,

        /// <summary>The tier entries a promotion search looked at (the shared succession rule).</summary>
        SuccessionProbes = 14,

        /// <summary>A Veteran promoted into a new record (name decided in PLAN; the id drawn here, the allocator snapshotted).</summary>
        Promotion = 15,

        OldLeaderExit = 16,
        NewLeader = 17,

        /// <summary>The durable half of an actor's end (the job cancel and spatial clean-up are RELEASE's, the event PUBLISH's).</summary>
        ActorEnded = 18,

        /// <summary>A returned Solo's hidden anchor, written once, by direct assignment (§ 12.2): no travel, no job, no facade.</summary>
        SoloAnchor = 19,

        /// <summary>The episode's result for its linked operation (§ 5.3 "operation marker").</summary>
        OperationMarker = 20,

        /// <summary>Appends one prebuilt publication spec to the outbox (data, not an event).</summary>
        Publication = 21,

        /// <summary>The episode is Closed: state, ticks, reason.</summary>
        EpisodeClosed = 22,

        /// <summary>The post-commit stage markers start as the plan says (false unless the stage has nothing to do).</summary>
        StageMarkers = 23,

        /// <summary>Derived strength is recomputed after the people changed.</summary>
        SimDirty = 24
    }

    /// <summary>One durable assignment of the plan. Plain data: the Applier interprets it; nothing here runs code.</summary>
    public sealed class CommitOp
    {
        public CommitOpKind kind;
        public KnownCharacter character;
        public EpisodeMember member;
        public MemberOutcome outcome;
        public ObservedKind observed;
        public Tier tier;
        public int woundDays;
        public string key;
        public PublicationSpec spec;
        public TileRef tile;
        public bool flag;

        /// <summary>StageMarkers only: FOLLOW-UP has nothing to do (no linked operation).</summary>
        public bool followUpDone;

        public PhysicalResolution resolution;
        public int killed;
        public int captured;
        public int missing;

        public override string ToString()
        {
            return kind + (character != null ? " " + character.id : "") + (member != null && !member.IsNamed ? " slot" + member.slot : "") + (key != null ? " " + key : "");
        }
    }

    /// <summary>What DECIDE concluded for one member (§ 15.3), with the observation it was decided from.</summary>
    public sealed class MemberDecision
    {
        public EpisodeMember member;
        public KnownCharacter character;
        public MemberOutcome outcome;
        public PhysicalObservation observation;
        public int woundDays;
    }

    /// <summary>
    /// The complete, immutable reconciliation plan (§ 15.2 step 3): the decisions, the succession decision, and the flat ordered
    /// list of every durable assignment the commit will make. Built from a READ-ONLY view of state; building it changes nothing.
    /// The Applier never alters it (its one result, a promoted record, lives on the <see cref="CommitTarget"/>).
    /// </summary>
    public sealed class ReconciliationPlan
    {
        /// <summary>Null for the parity path (an abstract <see cref="CasualtyReport"/> applied through the same Applier).</summary>
        public PhysicalEpisode episode;

        public NetworkActor actor;
        public OrganizationProfile org;
        public ContractorSimulation sim;
        public Operation operation;
        public int now;
        public string closeReasonKey;

        public readonly List<MemberDecision> decisions = new List<MemberDecision>();

        /// <summary>Every character an op writes (members, the old leader, the successor): the character part of the touched set.</summary>
        public readonly List<KnownCharacter> touchedCharacters = new List<KnownCharacter>();

        public FateRules.SuccessionPlan succession;

        /// <summary>The commit adds a record (a promotion): the id allocator and the characters store's membership join the touched set.</summary>
        public bool addsRecord;

        /// <summary>RELEASE has something to do (a member action, or an ended actor's job and spatial clean-up).</summary>
        public bool hasReleaseActions;

        /// <summary>FOLLOW-UP has something to do (a linked operation).</summary>
        public bool hasFollowUp;

        public readonly List<CommitOp> ops = new List<CommitOp>();

        public int returned;
        public int killed;
        public int wounded;
        public int captured;
        public int missing;
        public int lost;
        public int neverPlaced;
        public bool leaderLost;
        public string actorEndKey;

        public int PlannedPublications
        {
            get
            {
                int n = 0;
                for (int i = 0; i < ops.Count; i++)
                {
                    switch (ops[i].kind)
                    {
                        case CommitOpKind.Publication:
                        case CommitOpKind.DescriptorCheck:
                        case CommitOpKind.Promotion:
                        case CommitOpKind.NewLeader:
                        case CommitOpKind.ActorEnded:
                            n++;
                            break;
                    }
                }
                return n;
            }
        }

        public CommitOp Add(CommitOpKind kind)
        {
            CommitOp op = new CommitOp { kind = kind };
            ops.Add(op);
            return op;
        }

        public void Touch(KnownCharacter c)
        {
            if (c != null && !touchedCharacters.Contains(c)) touchedCharacters.Add(c);
        }
    }

    /// <summary>
    /// What the Applier may write besides the plan's own objects: the clock value, the id allocator and the characters store (for a
    /// promotion only), and the outbox. It deliberately has NO bus, scheduler, port, service or context.
    /// </summary>
    public sealed class CommitTarget
    {
        public int now;
        public IdAllocator ids;
        public CharacterStore characters;
        public List<PublicationSpec> outbox;

        /// <summary>The record a Promotion op created (runtime only; read by the NewLeader op that follows).</summary>
        public KnownCharacter promoted;
    }

    /// <summary>A plan that failed VALIDATE: nothing has been touched (§ 15.2 step 4).</summary>
    public sealed class PlanInvalidException : System.Exception
    {
        public readonly string reasonKey;

        public PlanInvalidException(string reasonKey, string detail)
            : base("PlanInvalid:" + reasonKey + (detail != null ? " (" + detail + ")" : ""))
        {
            this.reasonKey = reasonKey;
        }
    }

    /// <summary>A deterministic, test-only throw inside the commit (§ 15.7 fault-injection sweep). Never raised by production.</summary>
    public sealed class InjectedFaultException : System.Exception
    {
        public InjectedFaultException(string where) : base("Injected fault " + where) { }
    }

    /// <summary>The ordered, idempotent RELEASE actions of one member (§ 8.1). Never stored: only the cursor is.</summary>
    public enum ReleaseAction : byte
    {
        Normalize = 0,
        EnsureRetained = 1,

        /// <summary>Pass a bound never-placed pawn to the world, ONLY if the § 7.5 precondition is observed to hold right now.</summary>
        PassToWorldIfAllowed = 2,

        StripTag = 3
    }

    /// <summary>
    /// The release actions of a member are a PURE function of its persisted outcome and binding (§ 8.1 "never itself stored; only
    /// the cursor is"), so a resumed RELEASE recomputes the same list and continues at <see cref="EpisodeMember.releaseStep"/>.
    /// No row passes an already-passed pawn to the world: a Returned member was observed <c>WorldFree</c> (vanilla passed it), so
    /// its list has no pass at all (§ 7.5, P3-INV-031).
    /// </summary>
    public static class ReleasePolicy
    {
        private static readonly ReleaseAction[] None = new ReleaseAction[0];
        private static readonly ReleaseAction[] NamedReturned = { ReleaseAction.Normalize, ReleaseAction.EnsureRetained, ReleaseAction.StripTag };
        private static readonly ReleaseAction[] AnonymousReturned = { ReleaseAction.StripTag };
        private static readonly ReleaseAction[] NamedNeverPlaced = { ReleaseAction.PassToWorldIfAllowed, ReleaseAction.EnsureRetained, ReleaseAction.StripTag };
        private static readonly ReleaseAction[] AnonymousNeverPlaced = { ReleaseAction.PassToWorldIfAllowed, ReleaseAction.StripTag };
        private static readonly ReleaseAction[] RoutingOnly = { ReleaseAction.StripTag };

        public static ReleaseAction[] ActionsFor(EpisodeMember m)
        {
            return m == null ? None : ActionsFor(m.IsBound, m.IsNamed, m.outcome);
        }

        /// <summary>The same function over the facts it reads, so PLAN can ask it about the outcome it is about to commit.</summary>
        public static ReleaseAction[] ActionsFor(bool bound, bool named, MemberOutcome outcome)
        {
            if (!bound) return None; // nothing physical ever existed for it
            switch (outcome)
            {
                case MemberOutcome.Returned: return named ? NamedReturned : AnonymousReturned;
                case MemberOutcome.NeverPlaced: return named ? NamedNeverPlaced : AnonymousNeverPlaced;
                case MemberOutcome.Lost: return None; // the binding is gone: no physical action is possible
                case MemberOutcome.Pending: return None;
                default: return RoutingOnly; // Killed, Missing, held, Detached: leave custody and corpse to vanilla
            }
        }

        public static bool AnyFor(PhysicalEpisode e)
        {
            for (int i = 0; i < e.members.Count; i++) if (ActionsFor(e.members[i]).Length > 0) return true;
            return false;
        }
    }

    /// <summary>Builds the typed event of a stored publication spec (PUBLISH, § 15.2 step 8). Pure: it reads only the spec.</summary>
    public static class Publications
    {
        public static NetworkEvent Build(PublicationSpec s)
        {
            EntityRef[] subjects = s.subjects.ToArray();
            if (s.typeKey == EventKeys.EpisodeClosed)
            {
                EpisodeEvent e = EventFactory.Make<EpisodeEvent>(s.typeKey, s.importance, subjects);
                e.episode = s.episode;
                e.actor = s.actor;
                e.actorName = s.actorName;
                e.purposeKey = s.purposeKey;
                e.reasonKey = s.reasonKey;
                e.returned = s.returned;
                e.killed = s.killed;
                e.lost = s.missing; // an Episode.Closed spec carries its Lost count in `missing`
                e.neverPlaced = s.neverPlaced;
                return e;
            }
            ContractorEvent c = EventFactory.Make<ContractorEvent>(s.typeKey, s.importance, subjects);
            c.actor = s.actor;
            c.actorName = s.actorName;
            c.character = s.character;
            c.characterName = s.characterName;
            c.successor = s.successor;
            c.successorName = s.successorName;
            c.contract = s.contract;
            c.operation = s.operation;
            c.killed = s.killed;
            c.wounded = s.wounded;
            c.captured = s.captured;
            c.missing = s.missing;
            c.reasonKey = s.reasonKey;
            c.descriptorKey = s.descriptorKey;
            c.leader = s.leader;
            return c;
        }

        /// <summary>The spec of a person-level contractor event, with the same subjects the abstract path gives it.</summary>
        public static PublicationSpec Character(string key, Importance importance, NetworkActor a, KnownCharacter c, ContractId contract, OperationId op, bool leader)
        {
            PublicationSpec s = Actor(key, importance, a, contract, op);
            s.subjects.Clear();
            Subjects(s, a.id.Ref, c.id.Ref, contract.Ref);
            s.character = c.id;
            s.characterName = c.name.Display;
            s.leader = leader;
            return s;
        }

        public static PublicationSpec Actor(string key, Importance importance, NetworkActor a, ContractId contract, OperationId op)
        {
            PublicationSpec s = new PublicationSpec { typeKey = key, importance = importance, actor = a.id, actorName = a.name.Display, contract = contract, operation = op };
            Subjects(s, a.id.Ref, contract.Ref);
            return s;
        }

        public static void Subjects(PublicationSpec s, params EntityRef[] refs)
        {
            for (int i = 0; i < refs.Length; i++) if (refs[i].IsValid) s.subjects.Add(refs[i]);
        }
    }
}
