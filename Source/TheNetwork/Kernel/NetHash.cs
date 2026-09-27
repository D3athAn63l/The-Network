namespace TheNetwork.Kernel
{
    /// <summary>
    /// Stable hashing for seeds and stream keys. Independent of Verse, of .NET's randomized
    /// string.GetHashCode and of the mod list, so a seed derived today is the same after a reload,
    /// on another machine and in a headless test.
    /// </summary>
    public static class NetHash
    {
        /// <summary>FNV-1a over UTF-16 code units. Stable across runtimes.</summary>
        public static int String(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        h ^= s[i];
                        h *= 16777619u;
                    }
                }
                return (int)h;
            }
        }

        /// <summary>Order-dependent combination of two values (murmur3-style finalizer).</summary>
        public static int Combine(int a, int b)
        {
            unchecked
            {
                ulong x = ((ulong)(uint)a << 32) | (uint)b;
                return (int)Mix64(x);
            }
        }

        public static int Combine(int a, int b, int c)
        {
            return Combine(Combine(a, b), c);
        }

        public static int Combine(int a, string b)
        {
            return Combine(a, String(b));
        }

        public static ulong Mix64(ulong z)
        {
            unchecked
            {
                z ^= z >> 33;
                z *= 0xff51afd7ed558ccdUL;
                z ^= z >> 33;
                z *= 0xc4ceb9fe1a85ec53UL;
                z ^= z >> 33;
                return z;
            }
        }
    }
}
