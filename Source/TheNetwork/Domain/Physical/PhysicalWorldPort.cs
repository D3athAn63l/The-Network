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

        /// <summary>
        /// When the pawn stopped being physically present, if the adapter saw it happen (the synchronous LeftMap of this session); -1 when
        /// unknown (a map removal, or a reload in between). Runtime data, never persisted: the commit uses it so a stored pawn's
        /// <c>agedThroughTick</c> is the tick it stopped ticking, not the later tick reconciliation ran (§ 6.4: never under-aged).
        /// </summary>
        public int exitTick = -1;

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
    /// call. Only <see cref="Allowed"/> permits the call. <see cref="AlreadyInWorldPawns"/> completes the release action as an
    /// observed no-op (vanilla made the transition; never a second pass). Every other answer means the action has NOT completed: it
    /// is diagnosed and fails the RELEASE stage (never forced, never skipped), so the cursor stays and the stage is retried.
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

    /// <summary>
    /// What a first materialization asks the physical side to create (§ 6.5): durable Network truth only, built by
    /// <see cref="ProjectionPolicy"/>. Fame, the reputation score and visibility are never part of it (P3-INV-019).
    /// </summary>
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

        /// <summary>Competence within the role (§ 6.5): the actor's current experience band. Never fame.</summary>
        public ExperienceBand capability = ExperienceBand.Green;

        /// <summary>The abstract equipment tier (1–5): which existing kind or loadout class is requested (O-3). Not an item list.</summary>
        public int equipmentTier = 2;

        /// <summary>Seeds the first creation only (§ 5.3); a rematerialization never asks for a creation.</summary>
        public int seed;

        /// <summary>The episode's temporary encounter faction (§ 13.2), when one exists.</summary>
        public FactionRef faction;
    }

    /// <summary>
    /// THE port through which lifecycle code observes and acts on physical truth (§ 21.1, the ports pattern of comms, payment and
    /// the catalog). Production code calls only this interface, so the real adapter (3.1, <c>Integration/Physical</c>) and the scriptable
    /// test fake exercise the same lifecycle. Every action is phrased so that it can be made idempotent by OBSERVED state.
    ///
    /// Phase 3.1 extends the 3.0 shape narrowly: the temporary encounter faction (S10, § 13.2) has no other way in: the episode's
    /// <c>faction</c> is durable Network truth the lifecycle writes, while creating and releasing a vanilla faction is a physical action
    /// only the port may perform, and placement must put the pawn into that faction.
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

        /// <summary>
        /// The episode's temporary encounter faction (S10, § 13.2): returns <paramref name="current"/> when it still resolves to a live
        /// temporary faction, otherwise creates one (hidden, temporary, no settlement, relations seeded once). The lifecycle records the
        /// result on the episode. Throws when no suitable faction can exist (the episode then places nobody).
        /// </summary>
        FactionRef EnsureEncounterFaction(EpisodeId episode, ActorId actor, FactionRef current, int seededGoodwill);

        /// <summary>RELEASE, episode level: hand the faction back to vanilla's own temporary-faction removal. Idempotent by observed state.</summary>
        void ReleaseEncounterFaction(FactionRef faction);

        /// <summary>
        /// Place the bound pawn at the episode's anchor, in the episode's encounter faction. False when placement failed (the member stays
        /// unplaced). A retained pawn's reservation must already cover it (M1, ADR-053): placement never removes it.
        /// </summary>
        bool Place(PawnRef pawn, EpisodeId episode, TileRef tile, int mapId, FactionRef faction);

        /// <summary>Classify the bound pawn now (§ 9.3). Pure read.</summary>
        PhysicalObservation Observe(PawnRef pawn, EpisodeId episode);

        /// <summary>RELEASE: store-time normalization of a returned named pawn (S12). Idempotent by observed state.</summary>
        void Normalize(PawnRef pawn);

        /// <summary>
        /// RELEASE: PROVE the retained reservation of a named pawn (§ 7.4). Under M1 the registry already covered the pawn before its exit, so
        /// this establishes nothing new; a reservation that cannot be proven throws, and RELEASE stays pending. Idempotent.
        /// </summary>
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
            : base("No physical world is available here: '" + action + "' was refused. No contractor pawn is created, spawned, moved, reserved or passed.")
        {
        }
    }

    /// <summary>
    /// The truthful-aging catch-up stopped part-way (§ 6.4): <see cref="appliedTicks"/> of the interval were applied to the pawn. The lifecycle
    /// advances <c>agedThroughTick</c> by exactly that much (the pawn really is that much older) and places nobody.
    /// </summary>
    public sealed class AgingIncompleteException : InvalidOperationException
    {
        public readonly long appliedTicks;

        public AgingIncompleteException(long applied, Exception inner)
            : base("aging stopped after " + applied + " ticks: " + (inner?.Message ?? "unknown"), inner)
        {
            appliedTicks = applied;
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
    /// The FAIL-CLOSED port: never available, every action throws <see cref="PhysicalWorldUnavailableException"/>, nothing resolves,
    /// and an observation is always <see cref="ObservedKind.Unknown"/> (which never ends a member: authority stays blocked, nothing is
    /// invented). It references no RimWorld API. Phase 3.0's live game held it; Phase 3.1 keeps it for any context without a real world
    /// (the soak, headless contexts) and as the safe default of a <see cref="DomainContext"/>.
    /// </summary>
    public sealed class UnavailablePhysicalWorldPort : IPhysicalWorldPort
    {
        /// <summary>Refused requests (diagnostics; always 0 in a live 3.0 game, because no production path asks).</summary>
        public int refused;

        public bool Available => false;
        public string Name => "Unavailable (fail-closed)";

        private Exception Refuse(string action)
        {
            refused++;
            return new PhysicalWorldUnavailableException(action);
        }

        public PawnRef Create(ProjectionRequest request) { throw Refuse("Create"); }
        public bool Resolves(PawnRef pawn) { return false; }
        public void CatchUpAge(PawnRef pawn, long elapsedTicks) { throw Refuse("CatchUpAge"); }
        public FactionRef EnsureEncounterFaction(EpisodeId episode, ActorId actor, FactionRef current, int seededGoodwill) { throw Refuse("EnsureEncounterFaction"); }
        public void ReleaseEncounterFaction(FactionRef faction) { throw Refuse("ReleaseEncounterFaction"); }
        public bool Place(PawnRef pawn, EpisodeId episode, TileRef tile, int mapId, FactionRef faction) { throw Refuse("Place"); }
        public PhysicalObservation Observe(PawnRef pawn, EpisodeId episode) { return PhysicalObservation.Of(ObservedKind.Unknown); }
        public void Normalize(PawnRef pawn) { throw Refuse("Normalize"); }
        public void EnsureRetained(PawnRef pawn) { throw Refuse("EnsureRetained"); }
        public PassToWorldCheck CheckPassToWorld(PawnRef pawn) { return PassToWorldCheck.Unknown; }
        public void PassToWorld(PawnRef pawn) { throw Refuse("PassToWorld"); }
        public void StripEpisodeTag(PawnRef pawn, EpisodeId episode) { throw Refuse("StripEpisodeTag"); }
    }
}
