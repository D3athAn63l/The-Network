using System;
using TheNetwork.Kernel;

namespace TheNetwork.Domain.Physical
{
    /// <summary>Positive optional identity evidence, collected only for terminal Episode reconciliation. P0 and mandatory S1 use their own placement/outcome facts.</summary>
    [Flags]
    public enum ConcretizationEvidence : byte
    {
        None = 0,
        DeliberateIdentification = 1,
        PlayerCombat = 2,
        PlayerRelation = 4,
        FormerColonist = 8
    }

    /// <summary>Runtime-only same-Pawn facts supplied by the physical port. No character is created by collecting them.</summary>
    public sealed class PhysicalPromotionFacts
    {
        public NameSnapshot name;
        public ConcretizationEvidence evidence = ConcretizationEvidence.None;
    }

    /// <summary>Semantics selected by the adapter only after an EXACT source-qualified vanilla type match.</summary>
    public enum CombatEvidenceKind : byte
    {
        Unsupported = 0,
        Endpoints = 1,
        RangedImpact = 2
    }

    /// <summary>Pure S27 shape/time/explicit-identification predicates. Unknown facts provide no evidence.</summary>
    public static class CombatEvidenceRules
    {
        public const int MaxCandidates = 8;
        public const int MaxBattles = 32;
        public const int MaxEntriesPerBattle = 128;
        public const int MaxEntriesPerCandidate = MaxBattles * MaxEntriesPerBattle;
        public const int MaxConcerns = 4;
        public const int MaxDirectRelations = 128;

        /// <summary>
        /// Exactly two distinct endpoints, or the RangedImpact sequence {initiator, actual, original} with actual==original.
        /// Null/non-Pawn/extra concerns must already have been rejected by the adapter. A current player side is a contact proxy, not historical hostility.
        /// </summary>
        public static bool Matches<T>(CombatEvidenceKind kind, T candidate, T first, T second, T third, int count,
            bool firstPlayerSide, bool secondPlayerSide) where T : class
        {
            if (candidate == null || first == null || second == null || ReferenceEquals(first, second)) return false;
            if (kind == CombatEvidenceKind.Endpoints)
            {
                if (count != 2 || third != null) return false;
            }
            else if (kind == CombatEvidenceKind.RangedImpact)
            {
                if (count != 3 || third == null || !ReferenceEquals(second, third)) return false;
            }
            else return false;
            return (ReferenceEquals(first, candidate) && secondPlayerSide)
                || (ReferenceEquals(second, candidate) && firstPlayerSide);
        }

        /// <summary>Log entries use absolute ticks; Episode timestamps use game-relative ticks. Unknown/overflow/future Episode origins fail closed.</summary>
        public static bool TryAbsoluteWindow(int createdGameTick, int nowGameTick, int gameStartAbsTick, out int startAbs, out int endAbs)
        {
            startAbs = endAbs = -1;
            if (createdGameTick < 0 || nowGameTick < createdGameTick || gameStartAbsTick <= 0) return false;
            long start = (long)gameStartAbsTick + createdGameTick;
            long end = (long)gameStartAbsTick + nowGameTick;
            if (start < 0 || end > int.MaxValue) return false;
            startAbs = (int)start;
            endAbs = (int)end;
            return true;
        }

        public static bool InWindow(int timestamp, int startAbs, int endAbs)
        {
            return startAbs >= 0 && endAbs >= startAbs && timestamp >= startAbs && timestamp <= endAbs;
        }

        /// <summary>S2 seam only: planned authoritative individual subject identifying this EXACT Pawn. There is no production producer in this slice.</summary>
        public static bool DeliberatelyIdentifies<T>(T candidate, T plannedSubject, bool individualSubject) where T : class
        {
            return individualSubject && candidate != null && ReferenceEquals(candidate, plannedSubject);
        }
    }
}
