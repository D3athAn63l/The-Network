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
    /// Network. "Not spawned" is never evidence of anything: Returned needs WorldFree plus exit evidence, and a world pawn has left the
    /// episode map by construction (map removal included, which sends no LeftMap).
    ///
    /// After ADR-053 (M1) the registry reserves a retained named person from its binding on, so for such a person WorldFree means vanilla
    /// sees the pawn as ReservedByQuest by the Network's OWN registry and no other quest. An actual <c>Free</c> world pawn is NOT a return for
    /// it: it is the reservation failing, reported as ReservationBroken (P3-INV-032) and never healed here. The decision itself is the pure
    /// <see cref="WorldPawnRules"/>; this class only gathers the plain facts. A pawn the registry never covers keeps the ordinary reading.
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
            // Phase 3.2A: the holder is read POSITIVELY from vanilla's own state, in the § 9.3 order, so a held person's custody record says
            // who holds them. Allegiance to a permanent faction other than the player's (a captor recruited them) is a fact of its own.
            o.otherAllegiance = p.Faction != null && !p.Faction.IsPlayer && !p.Faction.temporary;
            if (p.IsPrisonerOfColony || p.IsSlaveOfColony)
            {
                o.kind = ObservedKind.HeldByPlayer;
                o.holder = p.IsSlaveOfColony ? HeldKind.PlayerSlave : HeldKind.PlayerPrisoner;
                return o;
            }
            if (p.Faction != null && p.Faction.IsPlayer)
            {
                o.kind = ObservedKind.JoinedPlayer;
                o.holder = HeldKind.PlayerColonist;
                return o;
            }
            if (PawnUtility.IsKidnappedPawn(p))
            {
                o.kind = ObservedKind.Kidnapped;
                o.holder = HeldKind.Kidnapped;
                return o;
            }
            if (p.HostFaction != null && (p.IsPrisoner || p.IsSlave))
            {
                o.kind = ObservedKind.HeldByOther;
                o.holder = HeldKind.OtherFaction;
                return o;
            }
            if (p.IsCaravanMember())
            {
                o.kind = ObservedKind.InCaravan;
                Caravan caravan = p.GetCaravan();
                o.holder = caravan == null || caravan.Faction == null ? HeldKind.Unknown : caravan.Faction.IsPlayer ? HeldKind.PlayerCaravan : HeldKind.OtherFaction;
                return o;
            }
            if (PawnUtility.IsTravelingInTransportPodWorldObject(p))
            {
                o.kind = ObservedKind.InTransport;
                o.holder = HeldKind.Transport;
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
                WorldPawnFacts facts = new WorldPawnFacts
                {
                    retained = registry != null && registry.Reserves(p),
                    situation = SituationOf(s),
                    otherQuestReserves = s == WorldPawnSituation.ReservedByQuest && OtherQuestReserves(p, registryQuest)
                };
                o.kind = WorldPawnRules.KindOf(facts);
                if (o.kind == ObservedKind.WorldFree)
                {
                    o.exitEvidence = true;
                    o.exitTick = exitTick;
                    o.mapId = -1;
                }
                else if (o.kind == ObservedKind.ReservationBroken)
                {
                    o.note = WorldPawnRules.BrokenReservation + " (vanilla situation " + s + ")";
                }
                return o;
            }
            o.kind = ObservedKind.Unknown;
            return o;
        }

        /// <summary>Vanilla's situation reduced to what the pure rule reads. Only an actual <c>Free</c> maps to Free.</summary>
        public static WorldSituation SituationOf(WorldPawnSituation s)
        {
            if (s == WorldPawnSituation.Free) return WorldSituation.Free;
            if (s == WorldPawnSituation.ReservedByQuest) return WorldSituation.ReservedByQuest;
            return s == WorldPawnSituation.None ? WorldSituation.None : WorldSituation.Other;
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
