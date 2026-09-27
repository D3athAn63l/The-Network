using System.Collections.Generic;
using Verse;

namespace TheNetwork
{
    /// <summary>
    /// Name-pool content for the global cast (DATA_MODEL § 18.4). Every def of the same kind is merged,
    /// so other mods can extend the pools. Names are display data, never identity.
    /// </summary>
    public class NamePoolDef : Def
    {
        public string kind;
        public List<string> wordsA = new List<string>();
        public List<string> wordsB = new List<string>();
        public List<string> suffixes = new List<string>();
        public List<string> firstNames = new List<string>();
        public List<string> lastNames = new List<string>();
        public List<string> nicknames = new List<string>();
    }
}

namespace TheNetwork.Settings
{
    /// <summary>Plain word lists the generator reads (tests build these directly).</summary>
    public sealed class NamePools
    {
        public readonly List<string> orgWordsA = new List<string>();
        public readonly List<string> orgWordsB = new List<string>();
        public readonly List<string> orgSuffixes = new List<string>();
        public readonly List<string> firstNames = new List<string>();
        public readonly List<string> lastNames = new List<string>();
        public readonly List<string> nicknames = new List<string>();

        public bool IsUsable => orgWordsA.Count > 0 && orgWordsB.Count > 0 && firstNames.Count > 0 && lastNames.Count > 0;

        /// <summary>Merges every loaded NamePoolDef by kind, sorted and de-duplicated for determinism.</summary>
        public static NamePools FromDefs()
        {
            NamePools p = new NamePools();
            List<NamePoolDef> defs = DefDatabase<NamePoolDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                NamePoolDef d = defs[i];
                if (d.kind == "Organization")
                {
                    AddAll(p.orgWordsA, d.wordsA);
                    AddAll(p.orgWordsB, d.wordsB);
                    AddAll(p.orgSuffixes, d.suffixes);
                }
                else if (d.kind == "Person")
                {
                    AddAll(p.firstNames, d.firstNames);
                    AddAll(p.lastNames, d.lastNames);
                    AddAll(p.nicknames, d.nicknames);
                }
            }
            p.Normalize();
            if (!p.IsUsable) p = Fallback();
            return p;
        }

        /// <summary>A tiny built-in pool so generation never fails if the content defs are missing.</summary>
        public static NamePools Fallback()
        {
            NamePools p = new NamePools();
            p.orgWordsA.AddRange(new[] { "Grey", "Iron", "Lucky", "Silent", "Red", "Salt" });
            p.orgWordsB.AddRange(new[] { "Compass", "Hand", "Rats", "Lantern", "Anchor", "Coil" });
            p.orgSuffixes.AddRange(new[] { "Company", "Crew", "Outfit" });
            p.firstNames.AddRange(new[] { "Ada", "Brann", "Cato", "Dara", "Emil", "Fen", "Galen", "Hesk" });
            p.lastNames.AddRange(new[] { "Voss", "Marr", "Quill", "Hale", "Stroud", "Kerr" });
            p.nicknames.AddRange(new[] { "Ledger", "Moth", "Nails", "Quiet" });
            p.Normalize();
            return p;
        }

        public void Normalize()
        {
            Dedup(orgWordsA);
            Dedup(orgWordsB);
            Dedup(orgSuffixes);
            Dedup(firstNames);
            Dedup(lastNames);
            Dedup(nicknames);
        }

        private static void AddAll(List<string> into, List<string> from)
        {
            if (from == null) return;
            for (int i = 0; i < from.Count; i++)
            {
                if (!string.IsNullOrEmpty(from[i])) into.Add(from[i].Trim());
            }
        }

        private static void Dedup(List<string> list)
        {
            HashSet<string> seen = new HashSet<string>();
            list.RemoveAll(s => string.IsNullOrEmpty(s) || !seen.Add(s));
            list.Sort(System.StringComparer.Ordinal);
        }
    }
}
