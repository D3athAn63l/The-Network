using Verse;

namespace TheNetwork.Kernel
{
    /// <summary>
    /// The persisted ID counters (DATA_MODEL § 1.1). <c>nextId</c> feeds every entity kind;
    /// events and scheduler jobs have their own ordering counters.
    /// </summary>
    public sealed class IdAllocator : IExposable
    {
        private int nextId = 1;
        private long nextEventSeq = 1;
        private long nextJobSeq = 1;

        public int PeekNextId => nextId;
        public long PeekNextEventSeq => nextEventSeq;
        public long PeekNextJobSeq => nextJobSeq;

        public int NextId()
        {
            return nextId++;
        }

        public long NextEventSeq()
        {
            return nextEventSeq++;
        }

        public long NextJobSeq()
        {
            return nextJobSeq++;
        }

        /// <summary>Repair: never hand out an ID at or below one already in use (validator).</summary>
        public bool EnsureAbove(int usedId)
        {
            if (usedId >= nextId)
            {
                nextId = usedId + 1;
                return true;
            }
            return false;
        }

        public void EnsureJobSeqAbove(long usedSeq)
        {
            if (usedSeq >= nextJobSeq) nextJobSeq = usedSeq + 1;
        }

        public void EnsureEventSeqAbove(long usedSeq)
        {
            if (usedSeq >= nextEventSeq) nextEventSeq = usedSeq + 1;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref nextId, "nextId", 1);
            Scribe_Values.Look(ref nextEventSeq, "nextEventSeq", 1L);
            Scribe_Values.Look(ref nextJobSeq, "nextJobSeq", 1L);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (nextId < 1) nextId = 1;
                if (nextEventSeq < 1) nextEventSeq = 1;
                if (nextJobSeq < 1) nextJobSeq = 1;
            }
        }
    }
}
