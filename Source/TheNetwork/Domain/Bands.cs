namespace TheNetwork.Domain
{
    // Coarse bands used across the model (DATA_MODEL § 4–8). They persist by name; new members are
    // appended, never inserted, and renames need a migration (DATA_MODEL § 17.5).

    /// <summary>A generic five-step band for profile values (fee, speed, reliability).</summary>
    public enum Band : byte
    {
        VeryLow = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        VeryHigh = 4
    }

    /// <summary>How far a Fixer's contacts or geography reach.</summary>
    public enum ReachBand : byte
    {
        Minimal = 0,
        Local = 1,
        Regional = 2,
        Wide = 3,
        Vast = 4
    }

    /// <summary>Public reputation tier (EVENTS_AND_HISTORY § 6). Fame is not capability.</summary>
    public enum FameBand : byte
    {
        Unknown = 0,
        Local = 1,
        Established = 2,
        Famous = 3,
        Legendary = 4
    }

    /// <summary>Operational capability tier (master § 28). Separate from fame.</summary>
    public enum ExperienceBand : byte
    {
        Green = 0,
        Experienced = 1,
        Seasoned = 2,
        Veteran = 3,
        Elite = 4,
        Legendary = 5
    }

    /// <summary>A lead's reported danger (DATA_MODEL § 8).</summary>
    public enum ThreatBand : byte
    {
        Unknown = 0,
        Negligible = 1,
        Light = 2,
        Moderate = 3,
        Heavy = 4,
        Extreme = 5
    }

    /// <summary>A descriptor of the source's confidence, never a probability (master § 70).</summary>
    public enum ConfidenceBand : byte
    {
        VeryLow = 0,
        Low = 1,
        Moderate = 2,
        High = 3,
        VeryHigh = 4
    }

    /// <summary>How much of the target payload the player took away (STATE_MACHINES § 2.2).</summary>
    public enum RecoveredBand : byte
    {
        None = 0,
        Little = 1,
        Some = 2,
        Most = 3,
        All = 4
    }

    /// <summary>A holder's real stance toward the player at generation time. Never forced.</summary>
    public enum Stance : byte
    {
        None = 0,
        Hostile = 1,
        Neutral = 2,
        Friendly = 3
    }

    public static class BandUtility
    {
        public static ThreatBand ThreatBandFor(float points)
        {
            if (points <= 0f) return ThreatBand.Negligible;
            if (points < 200f) return ThreatBand.Negligible;
            if (points < 500f) return ThreatBand.Light;
            if (points < 1000f) return ThreatBand.Moderate;
            if (points < 2000f) return ThreatBand.Heavy;
            return ThreatBand.Extreme;
        }

        public static ThreatBand Shift(ThreatBand band, int steps)
        {
            if (band == ThreatBand.Unknown) return band;
            int v = (int)band + steps;
            if (v < (int)ThreatBand.Negligible) v = (int)ThreatBand.Negligible;
            if (v > (int)ThreatBand.Extreme) v = (int)ThreatBand.Extreme;
            return (ThreatBand)v;
        }

        public static RecoveredBand RecoveredBandFor(int recovered, int initial)
        {
            if (recovered <= 0 || initial <= 0) return RecoveredBand.None;
            if (recovered >= initial) return RecoveredBand.All;
            float share = recovered / (float)initial;
            if (share < 0.25f) return RecoveredBand.Little;
            if (share < 0.6f) return RecoveredBand.Some;
            if (share < 0.95f) return RecoveredBand.Most;
            return RecoveredBand.All;
        }

        public static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        public static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }
}
