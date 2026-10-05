using System.Collections.Generic;
using RimWorld;
using Verse;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    /// <summary>RT-PHYX-022 only: remove its newly archived vanilla message before disposing its own warden.</summary>
    public static class EnslavementFixtureCleanup
    {
        public static bool TryRemoveMessage(Archive archive, HashSet<IArchivable> before, Pawn prisoner, Pawn warden,
            out int removed, out string refusal)
        {
            removed = 0;
            refusal = null;
            if (archive == null || before == null || prisoner == null || warden == null || ReferenceEquals(prisoner, warden))
            {
                refusal = "missing archive snapshot or distinct fixture pawns";
                return false;
            }
            Message owned = null;
            foreach (IArchivable entry in archive.ArchivablesListForReading)
            {
                Message message = entry as Message;
                if (message?.lookTargets?.targets == null || !message.lookTargets.targets.Exists(t => ReferenceEquals(t.Thing, warden))) continue;
                List<RimWorld.Planet.GlobalTargetInfo> targets = message.lookTargets.targets;
                // Object identity + the exact vanilla target order/type, never translated-text matching.
                // Validate the entire snapshot delta before mutation; an unexpected reference preserves the fixture.
                if (before.Contains(message) || message.def != MessageTypeDefOf.NeutralEvent || targets.Count != 2 ||
                    !ReferenceEquals(targets[0].Thing, prisoner) || !ReferenceEquals(targets[1].Thing, warden) || owned != null)
                {
                    refusal = "unexpected or ambiguous archived warden reference";
                    return false;
                }
                owned = message;
            }
            // Vanilla may reject enslavement (or throw before messaging). There is then no message to remove.
            if (owned == null) return true;
            if (!archive.Remove(owned))
            {
                refusal = "the identified enslavement message could not be removed";
                return false;
            }
            // Messages has no public single-live-message removal API. The unarchived view can expire normally,
            // with an empty valid target list so even its temporary UI view cannot reference the discarded pawn.
            owned.lookTargets = new LookTargets();
            removed = 1;
            return true;
        }
    }
}
