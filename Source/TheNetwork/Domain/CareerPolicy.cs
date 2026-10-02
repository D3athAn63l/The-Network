using System;
using TheNetwork.Domain.Operations;
using TheNetwork.Kernel;

namespace TheNetwork.Domain
{
    /// <summary>
    /// Every career number in one place (ADR-046): the reputation scale, how much a finished job is worth,
    /// the funds bounds, the equipment ladder, the operating reserve, the cooldown and the Tag thresholds.
    /// Pure functions of their arguments, no randomness, no state: a reload or a second call can never
    /// disagree. Nothing else in the mod may carry a career literal.
    /// </summary>
    public static class CareerPolicy
    {
        // ------------------------------------------------------------------ reputation scale

        /// <summary>The lowest score of each fame band. FameBand is DERIVED from the score for every actor.</summary>
        public const int LocalAt = 100;
        public const int EstablishedAt = 300;
        public const int FamousAt = 800;
        public const int LegendaryAt = 2000;

        /// <summary>A safe upper bound: a score can never overflow, whatever is granted.</summary>
        public const int ScoreCap = 1000000;

        public static FameBand FameFor(int score)
        {
            if (score >= LegendaryAt) return FameBand.Legendary;
            if (score >= FamousAt) return FameBand.Famous;
            if (score >= EstablishedAt) return FameBand.Established;
            if (score >= LocalAt) return FameBand.Local;
            return FameBand.Unknown;
        }

        /// <summary>The score a band starts at: what migration and a template's starting fame initialize to.</summary>
        public static int FloorOf(FameBand band)
        {
            switch (band)
            {
                case FameBand.Local: return LocalAt;
                case FameBand.Established: return EstablishedAt;
                case FameBand.Famous: return FamousAt;
                case FameBand.Legendary: return LegendaryAt;
                default: return 0;
            }
        }

        /// <summary>The score at which the next band begins (int.MaxValue for Legendary).</summary>
        public static int NextBandAt(FameBand band)
        {
            switch (band)
            {
                case FameBand.Unknown: return LocalAt;
                case FameBand.Local: return EstablishedAt;
                case FameBand.Established: return FamousAt;
                case FameBand.Famous: return LegendaryAt;
                default: return int.MaxValue;
            }
        }

        public static int ClampScore(long score)
        {
            return score < 0 ? 0 : (score > ScoreCap ? ScoreCap : (int)score);
        }

        // ------------------------------------------------------------------ reputation from work

        public const float DifficultyBase = 4f;
        public const float DifficultySpan = 36f;
        public const float DifficultyExponent = 1.5f;

        public const float TriumphMultiplier = 1.25f;
        public const float SuccessMultiplier = 1f;

        /// <summary>A success that cost people: worth a little less than a clean one.</summary>
        public const float CostlySuccessMultiplier = 0.9f;

        public const float PartialLow = 0.45f;
        public const float PartialHigh = 0.9f;

        /// <summary>
        /// The ceiling of the fame a job of this danger can build (the anti-farming rule): work this easy
        /// stops adding to the score above it, so no number of safe hauls creates a Legendary name. Honest
        /// work of any difficulty can still make a contractor known locally: the ceiling never falls below
        /// <see cref="LocalAt"/>.
        /// </summary>
        public const float CeilingAtFullDanger = 2400f;
        public const float CeilingExponent = 2f;
        public const int CeilingFloor = LocalAt;

        /// <summary>Above the ceiling the gain tapers to nothing over this share of the ceiling (at least <see cref="TaperFloor"/> points).</summary>
        public const float TaperShare = 0.5f;
        public const float TaperFloor = 50f;

        public static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        /// <summary>
        /// How much a job of this danger is worth before the outcome: 4 for a trivial one up to 40 for the
        /// most dangerous. The danger is the operation's own frozen figure (never recomputed from the world).
        /// </summary>
        public static float DifficultyValue(float danger)
        {
            return DifficultyBase + DifficultySpan * (float)Math.Pow(Clamp01(danger), DifficultyExponent);
        }

        /// <summary>The share of a job's value an outcome earns. A partial result earns by how much was secured; a failure or a disaster earns nothing.</summary>
        public static float OutcomeMultiplier(OutcomeBand band, int secured, int requested)
        {
            switch (band)
            {
                case OutcomeBand.Triumph: return TriumphMultiplier;
                case OutcomeBand.Success: return SuccessMultiplier;
                case OutcomeBand.CostlySuccess: return CostlySuccessMultiplier;
                case OutcomeBand.Partial:
                    if (secured <= 0 || requested <= 0) return 0f;
                    return PartialLow + (PartialHigh - PartialLow) * Clamp01(secured / (float)requested);
                default: return 0f;
            }
        }

        /// <summary>The fame score work of this danger can build up to, before it stops counting.</summary>
        public static int WorkCeiling(float danger)
        {
            return Math.Max(CeilingFloor, (int)Math.Round(CeilingAtFullDanger * (float)Math.Pow(Clamp01(danger), CeilingExponent)));
        }

        /// <summary>1 while the score is at or below the work's ceiling, down to 0 at ceiling + taper.</summary>
        public static float Taper(float danger, int currentScore)
        {
            float ceiling = WorkCeiling(danger);
            float over = currentScore - ceiling;
            if (over <= 0f) return 1f;
            float soft = Math.Max(TaperFloor, TaperShare * ceiling);
            return Clamp01(1f - over / soft);
        }

        /// <summary>
        /// The reputation one finished job adds: round(value × outcome × taper), never negative (Phase 2.75
        /// has no reputation loss). Market value is not a term: the danger already reflects how hard the
        /// goods were to get.
        /// </summary>
        public static int ReputationGain(float danger, OutcomeBand band, int secured, int requested, int currentScore)
        {
            float basis = DifficultyValue(danger) * OutcomeMultiplier(band, secured, requested);
            if (basis <= 0f) return 0;
            int gain = (int)Math.Round(basis * Taper(danger, currentScore));
            return gain < 0 ? 0 : gain;
        }

        /// <summary>The danger stored in a CareerRecord: whole thousandths.</summary>
        public static int ScaledDanger(float danger)
        {
            return (int)Math.Round(Clamp01(danger) * 1000f);
        }

        // ------------------------------------------------------------------ counters and money bounds

        /// <summary>Every record counter and total saturates here.</summary>
        public const int CounterCap = 1000000000;

        /// <summary>Contractor funds live in [−FundsBound, +FundsBound]; arithmetic saturates and never wraps.</summary>
        public const int FundsBound = 1000000000;

        public static int AddSaturating(int value, long delta)
        {
            long r = (long)value + delta;
            return r > CounterCap ? CounterCap : (r < -CounterCap ? -CounterCap : (int)r);
        }

        public static int AddFunds(int funds, long delta)
        {
            long r = (long)funds + delta;
            return r > FundsBound ? FundsBound : (r < -FundsBound ? -FundsBound : (int)r);
        }

        // ------------------------------------------------------------------ operating reserve

        public const float OrganizationUpkeepPerHead = 3f;
        public const float SoloUpkeepPerHead = 2f;

        /// <summary>The days of upkeep a contractor wants in hand before it spends on itself.</summary>
        public const float ReserveDays = 45f;

        public const int ReserveFloor = 150;

        /// <summary>One day's upkeep: the same rate the daily upkeep pays (UpkeepService), so the reserve follows the real cost.</summary>
        public static float DailyUpkeep(bool organization, int headcount)
        {
            return (organization ? OrganizationUpkeepPerHead : SoloUpkeepPerHead) * Math.Max(1, headcount);
        }

        public static int OperatingReserve(bool organization, int headcount)
        {
            return Math.Max(ReserveFloor, (int)Math.Ceiling(DailyUpkeep(organization, headcount) * ReserveDays));
        }

        // ------------------------------------------------------------------ equipment ladder

        public const int MinTier = 1;
        public const int MaxTier = 5;

        /// <summary>The silver an upgrade from this tier to the next costs (0 at the top).</summary>
        public static int UpgradeCost(int fromTier)
        {
            switch (fromTier)
            {
                case 1: return 500;
                case 2: return 2000;
                case 3: return 8000;
                case 4: return 30000;
                default: return 0;
            }
        }

        /// <summary>The reputation needed to be sold or lent the next tier.</summary>
        public static FameBand RequiredFame(int toTier)
        {
            switch (toTier)
            {
                case 2: return FameBand.Local;
                case 3: return FameBand.Established;
                case 4: return FameBand.Famous;
                case 5: return FameBand.Legendary;
                default: return FameBand.Unknown;
            }
        }

        /// <summary>Between two major upgrades.</summary>
        public const int AdvancementCooldownTicks = 30 * Ticks.PerDay;

        public static int ClampTier(int tier)
        {
            return tier < MinTier ? MinTier : (tier > MaxTier ? MaxTier : tier);
        }

        /// <summary>The highest tier the reputation alone supports.</summary>
        public static int TierSupportedByFame(FameBand fame)
        {
            int tier = MinTier;
            for (int t = MinTier + 1; t <= MaxTier; t++) if (fame >= RequiredFame(t)) tier = t;
            return tier;
        }

        /// <summary>The tier hard-won experience supports (a little more than green hands need).</summary>
        public static int TierSupportedByExperience(ExperienceBand exp)
        {
            return ClampTier(MinTier + (int)exp / 2);
        }

        // ------------------------------------------------------------------ need thresholds

        /// <summary>Equipment condition below this is a recovery matter before any luxury.</summary>
        public const float PoorCondition = 0.35f;

        /// <summary>Share of an organization hurt at once, or lost recently, that makes recovery the need.</summary>
        public const float SeriousWoundedShare = 0.5f;

        public const float SevereRecentLosses = 0.3f;

        /// <summary>An organization below this share of its capacity wants to recruit.</summary>
        public const float ExpansionBelowCapacityShare = 0.75f;

        /// <summary>Ambition plus professionalism a roster-minded organization needs before it wants to grow.</summary>
        public const float ExpansionDrive = 0.8f;

        public const float AmbitionForMobility = 0.5f;
        public const float AmbitionForPrestige = 0.45f;

        // ------------------------------------------------------------------ Tag thresholds

        public const int WellEquippedTier = 4;
        public const int EliteCombatMinTier = 3;
        public const float EliteCombatMinCondition = 0.4f;

        /// <summary>Funds at least this many operating reserves count as comfortably wealthy.</summary>
        public const float WealthyReserveMultiple = 5f;

        /// <summary>Resolved jobs (before and after this build) that make a contractor battle-tested.</summary>
        public const int BattleTestedJobs = 8;
    }
}
