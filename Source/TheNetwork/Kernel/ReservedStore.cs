using Verse;

namespace TheNetwork.Kernel
{
    /// <summary>
    /// A store slot the root layout reserves for a later phase (DATA_MODEL § 3, IMPLEMENTATION_PHASES § 3).
    /// It writes an empty node so the save layout never shifts, and holds no behaviour. The phase that
    /// fills the slot replaces this type at the same XML label; an old save then loads an empty store.
    /// </summary>
    public sealed class ReservedStore : IExposable
    {
        public void ExposeData()
        {
            // Intentionally empty: the node itself is the reservation.
        }
    }
}
