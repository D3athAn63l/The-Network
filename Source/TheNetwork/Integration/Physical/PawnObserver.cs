using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Integration.Physical
{
    /// <summary>
    /// The § 9.3 classification of one bound pawn, first match wins. A PURE READ of vanilla state: it changes nothing in the world or the
    /// Network. "Not spawned" is never evidence of anything: Returned needs WorldFree (vanilla already passed the pawn, it is alive, held by
    /// nobody, not the player's, Free or reserved by the Network's OWN registry and no other quest) plus exit evidence, and a world pawn has
    /// left the episode map by construction (map removal included, which sends no LeftMap).
    /// </summary>
    public static class PawnObserver
    {
        public static PhysicalObservation Classify(Pawn p, RetainedPawnRegistry registry, Quest registryQuest, int exitTick)
        {
            if (p == null || p.Discarded || (p.Destroyed && !p.Dead)) return PhysicalObservation.Of(ObservedKind.Gone);
            PhysicalObservation o = new PhysicalObservation { mapId = p.MapHeld?.uniqueID ?? -1 };
            if (p.Dead)
            {
                o.kind = ObservedKind.Dead;
                return o;
            }
            o.downed = p.Downed;
            o.health = p.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
            if (p.IsPrisonerOfColony || p.IsSlaveOfColony)
            {
                o.kind = ObservedKind.HeldByPlayer;
                return o;
            }
            if (p.Faction != null && p.Faction.IsPlayer)
            {
                o.kind = ObservedKind.JoinedPlayer;
                return o;
            }
            if (PawnUtility.IsKidnappedPawn(p))
            {
                o.kind = ObservedKind.Kidnapped;
                return o;
            }
            if (p.HostFaction != null && (p.IsPrisoner || p.IsSlave))
            {
                o.kind = ObservedKind.HeldByOther;
                return o;
            }
            if (p.IsCaravanMember())
            {
                o.kind = ObservedKind.InCaravan;
                return o;
            }
            if (PawnUtility.IsTravelingInTransportPodWorldObject(p))
            {
                o.kind = ObservedKind.InTransport;
                return o;
            }
            if (p.Spawned || p.SpawnedOrAnyParentSpawned)
            {
                o.kind = ObservedKind.Spawned;
                return o;
            }
            WorldPawns wp = Find.WorldPawns;
            if (wp != null && wp.Contains(p))
            {
                WorldPawnSituation s = wp.GetSituation(p);
                bool ours = s == WorldPawnSituation.ReservedByQuest && registry != null && registry.Reserves(p) && !OtherQuestReserves(p, registryQuest);
                if (s == WorldPawnSituation.Free || ours)
                {
                    o.kind = ObservedKind.WorldFree;
                    o.exitEvidence = true;
                    o.exitTick = exitTick;
                    o.mapId = -1;
                    return o;
                }
                o.kind = ObservedKind.WorldOther;
                return o;
            }
            o.kind = ObservedKind.Unknown;
            return o;
        }

        /// <summary>Is the pawn reserved by any active quest other than the Network's registry quest? (Bounded by the active quests.)</summary>
        public static bool OtherQuestReserves(Pawn p, Quest ours)
        {
            List<Quest> active = Find.QuestManager?.ActiveQuestsListForReading;
            if (active == null) return false;
            for (int i = 0; i < active.Count; i++)
            {
                Quest q = active[i];
                if (q == null || q == ours) continue;
                if (q.QuestReserves(p)) return true;
            }
            return false;
        }

        /// <summary>
        /// The § 7.5 three-part precondition of a Network PassToWorld, observed NOW: not spawned (nor a spawned holder), not already a world
        /// pawn, held by no other vanilla owner. Absence of evidence is not enough: an unrecognized holder is Held.
        /// </summary>
        public static PassToWorldCheck CheckPass(Pawn p)
        {
            if (p == null || p.Discarded) return PassToWorldCheck.Unknown;
            if (p.Dead) return PassToWorldCheck.Dead;
            if (p.Spawned || p.SpawnedOrAnyParentSpawned) return PassToWorldCheck.Spawned;
            if (Find.WorldPawns != null && Find.WorldPawns.Contains(p)) return PassToWorldCheck.AlreadyInWorldPawns;
            if (p.IsCaravanMember() || PawnUtility.IsKidnappedPawn(p) || PawnUtility.IsTravelingInTransportPodWorldObject(p) || PawnUtility.IsFactionLeader(p)) return PassToWorldCheck.Held;
            if (p.HostFaction != null || p.IsPrisoner || p.IsSlave) return PassToWorldCheck.Held;
            if (p.ParentHolder != null) return PassToWorldCheck.Held;
            return PassToWorldCheck.Allowed;
        }
    }
}
