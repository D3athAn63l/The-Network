using Verse;

namespace TheNetwork.Kernel
{
    /// <summary>The injected time source (ARCHITECTURE § 4 rule 4). Domain code never reads Find.TickManager.</summary>
    public interface IClock
    {
        int Now { get; }
    }

    public sealed class GameClock : IClock
    {
        public int Now
        {
            get
            {
                TickManager tm = Current.Game?.tickManager;
                return tm != null ? tm.TicksGame : 0;
            }
        }
    }

    /// <summary>A settable clock for headless tests and dev tools.</summary>
    public sealed class ManualClock : IClock
    {
        public int Now { get; set; }
    }

    public static class Ticks
    {
        public const int PerHour = 2500;
        public const int PerDay = 60000;
        public const int PerQuadrum = PerDay * 15;
        public const int PerYear = PerDay * 60;

        public static float ToDays(int ticks)
        {
            return ticks / (float)PerDay;
        }
    }
}
