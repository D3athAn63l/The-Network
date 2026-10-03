using System;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Domain.Physical
{
    /// <summary>
    /// One observation of one bound pawn (PHYSICAL_LIFECYCLE § 9.3): the first-match classification plus the facts a terminal
    /// decision needs. Read-only data: producing it mutates nothing, in the Network or in the world.
    /// </summary>
    public sealed class PhysicalObservation
    {
        public ObservedKind kind = ObservedKind.Unknown;

        /// <summary>Downed right now (no signal exists for it; it is polled).</summary>
        public bool downed;

        /// <summary>Summary health after tending, 0..1 (§ 10.5). Only its band matters.</summary>
        public float health = 1f;

        /// <summary>The episode's exit evidence (§ 9.3): the pawn left the map legally, or the episode map no longer holds it.</summary>
        public bool exitEvidence;

        /// <summary>The tile of the map the pawn left (the anchor a Returned Solo is written at, § 12.2); null when unknown.</summary>
        public TileRef tile;

        public int mapId = -1;

        public static PhysicalObservation Of(ObservedKind kind)
        {
            return new PhysicalObservation { kind = kind };
        }

        public override string ToString()
        {
            return kind + (downed ? " downed" : "") + (health < 1f ? " hp" + health.ToString("0.00") : "") + (exitEvidence ? " exit" : "");
        }
    }

    /// <summary>
    /// The three-part precondition of a Network <c>PassToWorld</c> (§ 7.5, P3-INV-031), observed positively at the moment of the
    /// call. Only <see cref="Allowed"/> permits the call; every other answer means the action is skipped and diagnosed, never forced.
    /// </summary>
    public enum PassToWorldCheck : byte
    {
        /// <summary>Not spawned, no spawned holder, not in WorldPawns, no other vanilla owner: the pass is the release action.</summary>
        Allowed = 0,

        /// <summary>Spawned, or a parent holder is spawned (precondition 1 fails).</summary>
        Spawned = 1,

        /// <summary>Already a world pawn (precondition 2 fails): vanilla passed it; the Network never passes it again.</summary>
        AlreadyInWorldPawns = 2,

        /// <summary>Another vanilla owner holds it (caravan, transporter, kidnapper, host, faction leader) (precondition 3 fails).</summary>
        Held = 3,

        /// <summary>Dead (precondition 3 fails).</summary>
        Dead = 4,

        /// <summary>Unrecognised or unresolvable: absence of evidence is not enough.</summary>
        Unknown = 5
    }

    /// <summary>What a first materialization asks the physical side to create (§ 6). In 3.0 only the fake port ever receives one.</summary>
    public sealed class ProjectionRequest
    {
        public EpisodeId episode;
        public ActorId actor;

        /// <summary>The named person, or none for an anonymous slot.</summary>
        public CharacterId character;

        public int slot;
        public Tier tier = Tier.Regular;
        public NameSnapshot name;
        public OperationalRole role = OperationalRole.Unset;

        /// <summary>Seeds the first creation only (§ 5.3); a rematerialization never asks for a creation.</summary>
        public int seed;
    }

    /// <summary>
    /// THE port through which lifecycle code observes and acts on physical truth (§ 21.1, the ports pattern of comms, payment and
    /// the catalog). Production code calls only this interface, so the future real adapter (3.1) and the scriptable test fake
    /// exercise the same lifecycle. Every action is phrased so that it can be made idempotent by OBSERVED state.
    ///
    /// Phase 3.0 ships NO real adapter: the live game holds <see cref="UnavailablePhysicalWorldPort"/>, which refuses every action.
    /// </summary>
    public interface IPhysicalWorldPort
    {
        /// <summary>False for the fail-closed production port: the lifecycle then neither plans nor decides anything.</summary>
        bool Available { get; }

        string Name { get; }

        /// <summary>First materialization of a person or a slot: create and return the binding (never spawns).</summary>
        PawnRef Create(ProjectionRequest request);

        /// <summary>Does the binding still resolve to its pawn? (Load and rematerialization evidence; never a decision by itself.)</summary>
        bool Resolves(PawnRef pawn);

        /// <summary>Truthful aging (§ 6.4): bring the retained pawn's biological age forward by the FULL elapsed interval.</summary>
        void CatchUpAge(PawnRef pawn, long elapsedTicks);

        /// <summary>Place the bound pawn at the episode's anchor. False when placement failed (the member stays unplaced).</summary>
        bool Place(PawnRef pawn, EpisodeId episode, TileRef tile, int mapId);

        /// <summary>Classify the bound pawn now (§ 9.3). Pure read.</summary>
        PhysicalObservation Observe(PawnRef pawn, EpisodeId episode);

        /// <summary>RELEASE: store-time normalization of a returned named pawn (S12). Idempotent by observed state.</summary>
        void Normalize(PawnRef pawn);

        /// <summary>RELEASE: prove (or establish) the retained reservation of a named pawn (§ 7.4; the mechanism is S31's). Idempotent.</summary>
        void EnsureRetained(PawnRef pawn);

        /// <summary>RELEASE: the § 7.5 precondition, observed at the moment of asking.</summary>
        PassToWorldCheck CheckPassToWorld(PawnRef pawn);

        /// <summary>RELEASE: pass a bound, never-placed pawn to the world (Decide). Only after <see cref="CheckPassToWorld"/> said Allowed.</summary>
        void PassToWorld(PawnRef pawn);

        /// <summary>RELEASE: strip the episode's routing tag. Cleanup only: NEVER completion evidence (§ 8.1 rule 2). Idempotent.</summary>
        void StripEpisodeTag(PawnRef pawn, EpisodeId episode);
    }

    /// <summary>Thrown by the fail-closed production port: something asked Phase 3.0 production code for real physical work.</summary>
    public sealed class PhysicalWorldUnavailableException : InvalidOperationException
    {
        public PhysicalWorldUnavailableException(string action)
            : base("No physical world adapter exists in this build (Phase 3.0): '" + action + "' was refused. No contractor pawn is ever created, spawned, moved, reserved or passed.")
        {
        }
    }

    /// <summary>A port action whose observed precondition does not hold (§ 7.5): refused, recorded, never forced.</summary>
    public sealed class PhysicalPreconditionException : InvalidOperationException
    {
        public readonly PassToWorldCheck check;

        public PhysicalPreconditionException(string action, PassToWorldCheck check)
            : base(action + " refused: precondition " + check)
        {
            this.check = check;
        }
    }

    /// <summary>
    /// The production port of Phase 3.0 (§ 23 gating): there is no real adapter yet, so it is FAIL-CLOSED. It is never available,
    /// every action throws <see cref="PhysicalWorldUnavailableException"/>, nothing resolves, and an observation is always
    /// <see cref="ObservedKind.Unknown"/> (which never ends a member: authority stays blocked, nothing is invented). It references no
    /// RimWorld API at all.
    /// </summary>
    public sealed class UnavailablePhysicalWorldPort : IPhysicalWorldPort
    {
        /// <summary>Refused requests (diagnostics; always 0 in a live 3.0 game, because no production path asks).</summary>
        public int refused;

        public bool Available => false;
        public string Name => "Unavailable (Phase 3.0)";

        private Exception Refuse(string action)
        {
            refused++;
            return new PhysicalWorldUnavailableException(action);
        }

        public PawnRef Create(ProjectionRequest request) { throw Refuse("Create"); }
        public bool Resolves(PawnRef pawn) { return false; }
        public void CatchUpAge(PawnRef pawn, long elapsedTicks) { throw Refuse("CatchUpAge"); }
        public bool Place(PawnRef pawn, EpisodeId episode, TileRef tile, int mapId) { throw Refuse("Place"); }
        public PhysicalObservation Observe(PawnRef pawn, EpisodeId episode) { return PhysicalObservation.Of(ObservedKind.Unknown); }
        public void Normalize(PawnRef pawn) { throw Refuse("Normalize"); }
        public void EnsureRetained(PawnRef pawn) { throw Refuse("EnsureRetained"); }
        public PassToWorldCheck CheckPassToWorld(PawnRef pawn) { return PassToWorldCheck.Unknown; }
        public void PassToWorld(PawnRef pawn) { throw Refuse("PassToWorld"); }
        public void StripEpisodeTag(PawnRef pawn, EpisodeId episode) { throw Refuse("StripEpisodeTag"); }
    }
}
