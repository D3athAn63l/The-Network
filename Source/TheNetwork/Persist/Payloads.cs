using RimWorld;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Persist
{
    public enum PayloadRole : byte
    {
        Target = 0,
        Extra = 1
    }

    /// <summary>What an opportunity actually holds (DATA_MODEL § 8). Polymorphic; names frozen.</summary>
    public abstract class OpportunityPayload : IExposable
    {
        public abstract void ExposeData();
    }

    /// <summary>
    /// An exact item stack committed at generation: def, stuff, count and quality band. The Things are
    /// created from this at materialization; nothing here is ever recomputed.
    /// </summary>
    public sealed class ItemPayload : OpportunityPayload
    {
        public DefRef<ThingDef> thing;
        public DefRef<ThingDef> stuff;
        public int count;

        /// <summary>QualityCategory as an int, or -1 when the def has no quality.</summary>
        public int qualityBand = -1;

        public PayloadRole role = PayloadRole.Target;

        public override void ExposeData()
        {
            Scribe_Deep.Look(ref thing, "thing");
            Scribe_Deep.Look(ref stuff, "stuff");
            Scribe_Values.Look(ref count, "count", 0);
            Scribe_Values.Look(ref qualityBand, "quality", -1);
            NetScribe.LookEnum(ref role, "role", PayloadRole.Target);
        }

        public string LabelSnapshot => thing == null ? "?" : thing.LabelSnapshot;
    }
}
