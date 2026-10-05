using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Persist.Events
{
    /// <summary>
    /// Phase 3 event keys (PHYSICAL_LIFECYCLE § 15.5). Published only by the PUBLISH stage of a reconciled episode, from its
    /// durable outbox, after the atomic commit; in Phase 3.0 no production path opens an episode, so the live game never
    /// publishes them.
    /// </summary>
    public static partial class EventKeys
    {
        public const string EpisodeClosed = "Episode.Closed";

        /// <summary>A named person is Lost (§ 17). This IS the design's "KnownCharacter.Lost": no second key exists for it.</summary>
        public const string CharacterVanished = "KnownCharacter.Vanished";

        // Phase 3.2A (held custody). Person-level ContractorEvents, published only by the PUBLISH stage of a reconciled episode. The three new
        // keys below record PHYSICAL truth in the journal; none is fed to the History chronicle, letters, relations or the Consequence Engine,
        // because who in the world LEARNS of a capture by the player, a recruitment or a release is a later knowledge phase (physical truth ≠
        // causal awareness). A capture by another faction reuses the existing Contractor.Captured (§ 15.3), recorded exactly as the abstract
        // capture of a contractor already is (never with the contract, so no second contract letter); a death while held reuses
        // KnownCharacter.Killed; a loss reuses KnownCharacter.Vanished.

        /// <summary>The player took a named person prisoner or slave (§ 15.3 "Character.CapturedByPlayer"). reasonKey = the HeldKind.</summary>
        public const string CharacterCapturedByPlayer = "KnownCharacter.CapturedByPlayer";

        /// <summary>The player recruited a named person (§ 8.2: JoinedPlayer ⇒ status Defected).</summary>
        public const string CharacterDefected = "KnownCharacter.Defected";

        /// <summary>
        /// A held person is a free world pawn again and back in the Network's custody (Stored). The Network observes only THAT they are free,
        /// never why (released, escaped, rescued), so this is deliberately not the design's Contractor.Rescued, which stays reserved for a rescue
        /// episode that knows its own cause.
        /// </summary>
        public const string CharacterFreed = "KnownCharacter.Freed";
    }

    /// <summary>An episode closed: who, why, and the per-outcome counts (history and diagnostics read it; it changes nothing).</summary>
    public sealed class EpisodeEvent : NetworkEvent
    {
        public EpisodeId episode;
        public ActorId actor;
        public string actorName;
        public string purposeKey;
        public string reasonKey;
        public int returned;
        public int killed;
        public int lost;
        public int neverPlaced;

        public override void ExposeData()
        {
            base.ExposeData();
            NetScribe.Look(ref episode, "episode");
            NetScribe.Look(ref actor, "actor");
            Scribe_Values.Look(ref actorName, "actorName");
            Scribe_Values.Look(ref purposeKey, "purpose");
            Scribe_Values.Look(ref reasonKey, "reason");
            Scribe_Values.Look(ref returned, "returned", 0);
            Scribe_Values.Look(ref killed, "killed", 0);
            Scribe_Values.Look(ref lost, "lost", 0);
            Scribe_Values.Look(ref neverPlaced, "neverPlaced", 0);
        }
    }
}
