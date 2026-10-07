using System;

namespace TheNetwork.Domain.Physical
{
    // Two PURE decisions the 3.1 post-review correction pass pulled out of the real adapter so they are proven headlessly: what a bound pawn
    // IS after a placement attempt that did not report success (PlacementRules), and how a world pawn is classified given the M1 reservation
    // (WorldPawnRules). Neither reads a RimWorld object: the adapter gathers plain facts and asks.

    /// <summary>What the lifecycle concludes about a BOUND member after a placement attempt that did not report success (or threw).</summary>
    public enum PlacementVerdictKind : byte
    {
        /// <summary>Alive, not spawned, not discarded, held by nobody: the member was legitimately never placed (RELEASE may pass it, § 7.5).</summary>
        NeverPlaced = 0,

        /// <summary>Physically there despite the nominal failure (spawned, or dead where it stood): the ordinary observation decides it from here.</summary>
        Present = 1,

        /// <summary>Positively gone after it was bound (discarded or destroyed): the existing Lost semantics, never NeverPlaced, never regenerated.</summary>
        Lost = 2,

        /// <summary>Held by a vanilla owner, in a situation this build does not recognise, or unobservable: nothing is decided, the episode is quarantined.</summary>
        FailClosed = 3
    }

    public sealed class PlacementVerdict
    {
        public PlacementVerdictKind kind;
        public string detail;

        public static PlacementVerdict Of(PlacementVerdictKind kind, string detail)
        {
            return new PlacementVerdict { kind = kind, detail = detail };
        }

        public static PlacementVerdict FailClosed(string detail)
        {
            return Of(PlacementVerdictKind.FailClosed, detail);
        }

        public override string ToString()
        {
            return kind + (detail != null ? " (" + detail + ")" : "");
        }
    }

    /// <summary>
    /// After binding, a pawn's physical state is authoritative and an ambiguous failure is NEVER reduced to <see cref="PlacementVerdictKind.NeverPlaced"/>
    /// (the 3.1 correction pass, PHYSICAL_LIFECYCLE § 7.3 rule 7). <c>GenSpawn.Spawn</c> returns the pawn even when <c>Pawn.SpawnSetup</c> discarded it
    /// (an invalid state despawns it and passes it to the world with Discard), and a mod can throw anywhere in it: the return value, and whether
    /// an exception was thrown, prove nothing. Only POSITIVE observation does: the member's observation (§ 9.3) and the § 7.5 three-part check.
    /// </summary>
    public static class PlacementRules
    {
        public static PlacementVerdict Classify(PhysicalObservation o, PassToWorldCheck pass)
        {
            if (o == null) return PlacementVerdict.FailClosed("no observation");
            switch (o.kind)
            {
                case ObservedKind.Gone:
                    return PlacementVerdict.Of(PlacementVerdictKind.Lost, "the bound pawn is discarded or gone (" + o + ")");
                case ObservedKind.Spawned:
                    return PlacementVerdict.Of(PlacementVerdictKind.Present, "the bound pawn is spawned (" + o + ")");
                case ObservedKind.Dead:
                    return PlacementVerdict.Of(PlacementVerdictKind.Present, "the bound pawn is dead where it stood (" + o + "); the ordinary observation records the death");
                case ObservedKind.HeldByPlayer:
                case ObservedKind.JoinedPlayer:
                case ObservedKind.Kidnapped:
                case ObservedKind.HeldByOther:
                case ObservedKind.InCaravan:
                case ObservedKind.InTransport:
                    return PlacementVerdict.FailClosed("held by a vanilla owner (" + o.kind + ")");
                case ObservedKind.WorldOther:
                case ObservedKind.ReservationBroken:
                    return PlacementVerdict.FailClosed("a world pawn in a situation the Network does not own (" + o.kind + ")");
                case ObservedKind.WorldFree:
                case ObservedKind.Unknown:
                case ObservedKind.None:
                    break;
                default:
                    return PlacementVerdict.FailClosed("unrecognised observation " + o.kind);
            }
            // Nothing positive says spawned, dead, gone or held: the § 7.5 check decides, and only a pawn that is alive, unspawned and held by
            // nobody (fresh, or a stored world pawn that never left WorldPawns) was legitimately never placed.
            switch (pass)
            {
                case PassToWorldCheck.Allowed:
                case PassToWorldCheck.AlreadyInWorldPawns:
                    return PlacementVerdict.Of(PlacementVerdictKind.NeverPlaced, "alive, not spawned, not discarded, held by nobody (" + pass + ")");
                case PassToWorldCheck.Spawned:
                    return PlacementVerdict.Of(PlacementVerdictKind.Present, "the bound pawn (or a holder) is spawned (" + pass + ")");
                case PassToWorldCheck.Dead:
                    return PlacementVerdict.Of(PlacementVerdictKind.Present, "the bound pawn is dead (" + pass + ")");
                case PassToWorldCheck.Held:
                    return PlacementVerdict.FailClosed("held by another vanilla owner (" + pass + ")");
                default:
                    return PlacementVerdict.FailClosed("the bound pawn's state cannot be established (" + o.kind + ", " + pass + ")");
            }
        }
    }

    /// <summary>Vanilla's world-pawn situation reduced to what the classification reads (the adapter maps its own enum onto this).</summary>
    public enum WorldSituation : byte
    {
        None = 0,
        Free = 1,
        ReservedByQuest = 2,
        Other = 3
    }

    /// <summary>The plain facts of one living, unspawned, unheld world pawn.</summary>
    public sealed class WorldPawnFacts
    {
        /// <summary>The Network's registry predicate: named retained custody or a living bound unreleased Episode slot requires the existing M1 reservation.</summary>
        public bool retained;

        public WorldSituation situation = WorldSituation.None;

        /// <summary>Some quest OTHER than the Network's registry quest reserves the pawn too (never "ours alone").</summary>
        public bool otherQuestReserves;
    }

    /// <summary>
    /// § 9.3 rows WorldFree / WorldOther, after ADR-053 (M1). For a retained named person the registry reservation is in force from its binding
    /// on, so a successful return is a pawn vanilla sees as <c>ReservedByQuest</c> by the Network's OWN registry. An actual <c>Free</c> is not a
    /// return: it is evidence that the reservation failed (P3-INV-032), reported as <see cref="ObservedKind.ReservationBroken"/>. A pawn the
    /// registry never covers (an ordinary slot after its Episode released it) keeps the ordinary reading: <c>Free</c> is a world pawn vanilla owns, and it is
    /// <see cref="ObservedKind.WorldFree"/>.
    /// </summary>
    public static class WorldPawnRules
    {
        public static ObservedKind KindOf(WorldPawnFacts f)
        {
            if (f == null) throw new ArgumentNullException(nameof(f));
            if (f.retained)
            {
                if (f.situation == WorldSituation.ReservedByQuest) return f.otherQuestReserves ? ObservedKind.WorldOther : ObservedKind.WorldFree;
                if (f.situation == WorldSituation.Free) return ObservedKind.ReservationBroken;
                return ObservedKind.WorldOther;
            }
            return f.situation == WorldSituation.Free ? ObservedKind.WorldFree : ObservedKind.WorldOther;
        }

        /// <summary>The words every diagnostic of a broken reservation uses.</summary>
        public const string BrokenReservation = "a retained named person is an ordinary Free world pawn: the registry reservation (M1, P3-INV-032) was not in force";
    }
}
