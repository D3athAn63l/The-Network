using System;
using System.Collections.Generic;
using RimWorld;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Integration.Physical
{
    /// <summary>
    /// The Network's pawn quest tags (PHYSICAL_LIFECYCLE § 7.3): a SIGNAL-ROUTING AID ONLY, never identity and never completion evidence.
    /// Vanilla sends "&lt;tag&gt;.&lt;Signal&gt;" for them (Killed, LeftMap, Arrested, ...). Every route proves the pawn through the persisted
    /// binding by reference equality (P3-INV-012); a pawn that carries a tag but no binding is ignored.
    /// </summary>
    public static class PhysicalTags
    {
        /// <summary>The episode routing tag, stripped by RELEASE.</summary>
        public const string EpisodePrefix = "TheNetwork.Ep.";

        /// <summary>The person routing tag, kept while the pawn is bound (custody signals after the episode, 3.2).</summary>
        public const string CharacterPrefix = "TheNetwork.Char.";

        public static string Episode(EpisodeId id)
        {
            return EpisodePrefix + id.Value;
        }

        public static string Character(CharacterId id)
        {
            return CharacterPrefix + id.Value;
        }

        public static bool IsNetworkPawnTag(string tag)
        {
            return tag != null && (tag.StartsWith(EpisodePrefix, StringComparison.Ordinal) || tag.StartsWith(CharacterPrefix, StringComparison.Ordinal));
        }

        /// <summary>Parses "&lt;prefix&gt;&lt;id&gt;[.&lt;Signal&gt;]" into the id and the signal part. False for anything else.</summary>
        public static bool TryParse(string signalTag, string prefix, out int id, out string signal)
        {
            id = 0;
            signal = null;
            if (signalTag == null || !signalTag.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string rest = signalTag.Substring(prefix.Length);
            int dot = rest.IndexOf('.');
            string idPart = dot < 0 ? rest : rest.Substring(0, dot);
            signal = dot < 0 ? null : rest.Substring(dot + 1);
            return int.TryParse(idPart, out id) && id > 0;
        }

        public static void Add(Pawn p, string tag)
        {
            if (p == null || tag == null) return;
            QuestUtility.AddQuestTag(ref p.questTags, tag);
        }

        public static bool Has(Pawn p, string tag)
        {
            return p?.questTags != null && p.questTags.Contains(tag);
        }

        public static int Remove(Pawn p, string tag)
        {
            if (p?.questTags == null || tag == null) return 0;
            return p.questTags.RemoveAll(t => t == tag);
        }

        /// <summary>Every Network routing tag on this pawn, except <paramref name="keep"/> (load correction and prepare-for-removal).</summary>
        public static int RemoveAllExcept(Pawn p, ICollection<string> keep)
        {
            if (p?.questTags == null) return 0;
            return p.questTags.RemoveAll(t => IsNetworkPawnTag(t) && (keep == null || !keep.Contains(t)));
        }
    }
}
