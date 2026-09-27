using System;
using System.Collections.Generic;

namespace TheNetwork.Kernel
{
    /// <summary>
    /// The Network's private pseudo-random generator (SIMULATION § 6.2).
    ///
    /// A stream is identified by (seed, stream key, index). The same triple always produces the same
    /// sequence, independent of Verse.Rand, of the mod list and of anything else the game draws. Network
    /// decisions never use Verse.Rand; vanilla generators that need it run under Rand.PushState with a
    /// seed derived from here, and their results are committed at once.
    /// </summary>
    public sealed class NetRng
    {
        private ulong state;

        public NetRng(int seed, string streamKey, int index = 0)
        {
            unchecked
            {
                ulong s = ((ulong)(uint)seed << 32) ^ (uint)NetHash.String(streamKey);
                s = NetHash.Mix64(s ^ (0x9E3779B97F4A7C15UL * (ulong)(uint)(index + 1)));
                state = s;
            }
        }

        /// <summary>SplitMix64 step.</summary>
        public ulong NextULong()
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15UL;
                ulong z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>A value in [0, 1).</summary>
        public double NextDouble()
        {
            return (NextULong() >> 11) * (1.0 / 9007199254740992.0);
        }

        public float Value()
        {
            return (float)NextDouble();
        }

        /// <summary>An int in [minInclusive, maxExclusive). Returns minInclusive for an empty range.</summary>
        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            ulong span = (ulong)((long)maxExclusive - minInclusive);
            return (int)(minInclusive + (long)(NextULong() % span));
        }

        /// <summary>An int in [minInclusive, maxInclusive].</summary>
        public int RangeInclusive(int minInclusive, int maxInclusive)
        {
            return Range(minInclusive, maxInclusive + 1);
        }

        public float Range(float minInclusive, float maxInclusive)
        {
            return minInclusive + (float)NextDouble() * (maxInclusive - minInclusive);
        }

        public bool Chance(float probability)
        {
            if (probability <= 0f) return false;
            if (probability >= 1f) return true;
            return NextDouble() < probability;
        }

        /// <summary>Approximately normal, mean 0, standard deviation about 1 (sum of four uniforms).</summary>
        public float NormalLike()
        {
            double sum = NextDouble() + NextDouble() + NextDouble() + NextDouble();
            return (float)((sum - 2.0) * Math.Sqrt(3.0));
        }

        /// <summary>
        /// Index drawn with probability proportional to weights. Non-positive weights never win.
        /// Returns -1 if every weight is non-positive.
        /// </summary>
        public int WeightedIndex(IList<float> weights)
        {
            double total = 0;
            for (int i = 0; i < weights.Count; i++)
            {
                if (weights[i] > 0f) total += weights[i];
            }
            if (total <= 0) return -1;
            double pick = NextDouble() * total;
            double acc = 0;
            int last = -1;
            for (int i = 0; i < weights.Count; i++)
            {
                if (weights[i] <= 0f) continue;
                acc += weights[i];
                last = i;
                if (pick < acc) return i;
            }
            return last;
        }

        public T Pick<T>(IList<T> items)
        {
            if (items == null || items.Count == 0) return default(T);
            return items[Range(0, items.Count)];
        }

        /// <summary>A derived 32-bit seed, for Rand.PushState around vanilla generators.</summary>
        public int NextSeed()
        {
            return unchecked((int)NextULong());
        }
    }
}
