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
        public const string CharacterVanished = "KnownCharacter.Vanished";
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
