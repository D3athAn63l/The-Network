using System;
using System.Collections.Generic;
using RimWorld;
using TheNetwork.Domain.Physical;
using Verse;

namespace TheNetwork.Integration.Physical
{
    /// <summary>Runtime-only bounded-query diagnostics. Missing optional evidence never invents identity or changes custody.</summary>
    public sealed class ConcretizationEvidenceScan
    {
        public ConcretizationEvidence evidence;
        public int battlesExamined;
        public int entriesExamined;
        public int relationRecordsExamined;
        public int unsupportedEntries;
        public bool truncatedBattles;
        public bool truncatedEntries;
        public bool truncatedRelations;
        public bool invalidWindow;
        public bool collectionFailed;
    }

    /// <summary>
    /// S27 collector: terminal reconciliation only, <=8 candidates supplied by the Episode caller. No stored history, watcher, world-pawn traversal or PlayLog.
    /// All engine access lives here; public current ownership and exact audited combat shapes are the only S3/S4 inputs.
    /// </summary>
    public static class ConcretizationEvidenceCollector
    {
        public static ConcretizationEvidence Collect(Pawn candidate, int episodeCreatedGameTick, int nowGameTick = -1)
        {
            return Scan(candidate, episodeCreatedGameTick, nowGameTick).evidence;
        }

        public static ConcretizationEvidenceScan Scan(Pawn candidate, int episodeCreatedGameTick, int nowGameTick = -1)
        {
            Faction playerFaction = null;
            List<Battle> battles = null;
            int origin = -1;
            bool unavailable = false;
            try
            {
                TickManager ticks = Find.TickManager;
                if (ticks != null)
                {
                    origin = ticks.gameStartAbsTick;
                    if (nowGameTick < 0) nowGameTick = ticks.TicksGame;
                }
                playerFaction = Faction.OfPlayer;
                battles = Find.BattleLog?.Battles;
            }
            catch (Exception) { unavailable = true; }
            ConcretizationEvidenceScan result = Scan(candidate, episodeCreatedGameTick, nowGameTick, origin, playerFaction, battles);
            result.collectionFailed |= unavailable;
            return result;
        }

        /// <summary>Same production algorithm with explicit source objects/time, permitting headless tests without replacing engine predicates.</summary>
        public static ConcretizationEvidenceScan Scan(Pawn candidate, int episodeCreatedGameTick, int nowGameTick,
            int gameStartAbsTick, Faction playerFaction, IList<Battle> battles)
        {
            ConcretizationEvidenceScan result = new ConcretizationEvidenceScan();
            if (candidate == null) return result;
            try { if (candidate.Discarded) return result; }
            catch (Exception) { result.collectionFailed = true; return result; }

            // S4 predicates are independent of the optional log window. A missing/pruned log cannot suppress a positive colony stake.
            CollectStake(candidate, playerFaction, result);
            int startAbs, endAbs;
            if (!CombatEvidenceRules.TryAbsoluteWindow(episodeCreatedGameTick, nowGameTick, gameStartAbsTick, out startAbs, out endAbs))
            {
                result.invalidWindow = true;
                return result;
            }
            if (battles == null) return result;
            try
            {
                int battleCount = Math.Min(CombatEvidenceRules.MaxBattles, battles.Count);
                result.truncatedBattles = battles.Count > battleCount;
                for (int i = 0; i < battleCount; i++)
                {
                    result.battlesExamined++;
                    Battle battle = battles[i];
                    List<LogEntry> entries = battle?.Entries;
                    if (entries == null) continue;
                    int count = Math.Min(CombatEvidenceRules.MaxEntriesPerBattle, entries.Count);
                    result.truncatedEntries |= entries.Count > count;
                    for (int j = 0; j < count && result.entriesExamined < CombatEvidenceRules.MaxEntriesPerCandidate; j++)
                    {
                        result.entriesExamined++;
                        if (CombatPair(entries[j], candidate, playerFaction, startAbs, endAbs, result))
                        {
                            result.evidence |= ConcretizationEvidence.PlayerCombat;
                            return result;
                        }
                    }
                }
            }
            catch (Exception) { result.collectionFailed = true; }
            return result;
        }

        private static void CollectStake(Pawn candidate, Faction playerFaction, ConcretizationEvidenceScan result)
        {
            try
            {
                if (candidate.def?.race != null && candidate.def.race.Humanlike && candidate.records != null
                    && PawnUtility.EverBeenColonistOrTameAnimal(candidate)) result.evidence |= ConcretizationEvidence.FormerColonist;
            }
            catch (Exception) { result.collectionFailed = true; }
            try
            {
                List<DirectPawnRelation> relations = candidate.relations?.DirectRelations;
                if (relations == null) return;
                int count = Math.Min(CombatEvidenceRules.MaxDirectRelations, relations.Count);
                result.truncatedRelations = relations.Count > count;
                for (int i = 0; i < count; i++)
                {
                    result.relationRecordsExamined++;
                    DirectPawnRelation relation = relations[i];
                    if (relation?.def != null && IsPlayerSide(relation.otherPawn, candidate, playerFaction))
                    {
                        result.evidence |= ConcretizationEvidence.PlayerRelation;
                        return;
                    }
                }
            }
            catch (Exception) { result.collectionFailed = true; }
        }

        private static bool CombatPair(LogEntry entry, Pawn candidate, Faction playerFaction, int startAbs, int endAbs, ConcretizationEvidenceScan result)
        {
            if (entry == null || playerFaction == null) return false;
            try
            {
                if (!CombatEvidenceRules.InWindow(entry.Timestamp, startAbs, endAbs)) return false;
                Type type = entry.GetType();
                CombatEvidenceKind kind;
                if (type == typeof(BattleLogEntry_MeleeCombat) || type == typeof(BattleLogEntry_RangedFire) || type == typeof(BattleLogEntry_ExplosionImpact))
                    kind = CombatEvidenceKind.Endpoints;
                else if (type == typeof(BattleLogEntry_RangedImpact)) kind = CombatEvidenceKind.RangedImpact;
                else { result.unsupportedEntries++; return false; }

                Pawn first = null, second = null, third = null;
                int count = 0;
                foreach (Thing concern in entry.GetConcerns())
                {
                    Pawn pawn = concern as Pawn;
                    if (pawn == null) return false;
                    if (count == 0) first = pawn;
                    else if (count == 1) second = pawn;
                    else if (count == 2) third = pawn;
                    else return false; // fourth yield proves unsupported shape; never enumerate an unbounded modded concern sequence
                    count++;
                }
                return CombatEvidenceRules.Matches(kind, candidate, first, second, third, count,
                    IsPlayerSide(first, candidate, playerFaction), IsPlayerSide(second, candidate, playerFaction));
            }
            catch (Exception) { result.collectionFailed = true; return false; }
        }

        private static bool IsPlayerSide(Pawn other, Pawn candidate, Faction playerFaction)
        {
            return other != null && !ReferenceEquals(other, candidate) && !other.Discarded && playerFaction != null
                && (ReferenceEquals(other.Faction, playerFaction) || ReferenceEquals(other.HostFaction, playerFaction));
        }
    }
}
