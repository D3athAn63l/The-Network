using System;
using System.Collections.Generic;
using TheNetwork.Domain.Physical;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    /// <summary>Pure runtime-QA ownership and selection rules. Reads existing Episode truth; creates no identity or persisted QA state.</summary>
    public static class GroupQaRules
    {
        public static bool OwnedByRun(PhysicalEpisode episode, string runId, string scenarioId)
        {
            return episode?.cause != null && !string.IsNullOrEmpty(runId) && !string.IsNullOrEmpty(scenarioId)
                && string.Equals(episode.cause.devKey, PhysicalTestIds.DevKey(runId, scenarioId), StringComparison.Ordinal);
        }

        /// <summary>The greatest valid EpisodeId of exactly this family. Duplicate relevant ids are ambiguous and never guessed.</summary>
        public static PhysicalEpisode Latest(IList<PhysicalEpisode> episodes, string family)
        {
            if (episodes == null || string.IsNullOrEmpty(family)) return null;
            PhysicalEpisode latest = null;
            HashSet<int> seen = new HashSet<int>();
            foreach (PhysicalEpisode episode in episodes)
            {
                if (episode == null || !episode.id.IsValid || !string.Equals(PhysicalTestIds.ScenarioOf(episode.cause?.devKey), family, StringComparison.Ordinal)) continue;
                if (!seen.Add(episode.id.Value)) return null;
                if (latest == null || episode.id.Value > latest.id.Value) latest = episode;
            }
            return latest;
        }

        /// <summary>The latest supported group/succession checkpoint; ambiguity in either exact family prevents a fallback guess.</summary>
        public static PhysicalEpisode LatestCheckpoint(IList<PhysicalEpisode> episodes)
        {
            if (episodes == null) return null;
            PhysicalEpisode latest = null;
            HashSet<int> seen = new HashSet<int>();
            foreach (PhysicalEpisode episode in episodes)
            {
                if (episode == null || !episode.id.IsValid) continue;
                string family = PhysicalTestIds.ScenarioOf(episode.cause?.devKey);
                if (family != "RT-PHYX-030" && family != "RT-PHYX-031") continue;
                if (!seen.Add(episode.id.Value)) return null;
                if (latest == null || episode.id.Value > latest.id.Value) latest = episode;
            }
            return latest;
        }

        /// <summary>Cleanup cannot discard a Pawn still bound by unreleased durable Episode truth, even if a derived registry missed it.</summary>
        public static bool HasUnreleasedBinding(IList<PhysicalEpisode> episodes, int thingId)
        {
            if (episodes == null || thingId <= 0) return false;
            foreach (PhysicalEpisode episode in episodes)
            {
                if (episode == null || episode.releaseApplied || episode.members == null) continue;
                foreach (EpisodeMember member in episode.members)
                    if (member != null && member.IsBound && member.pawn.thingIdNumber == thingId) return true;
            }
            return false;
        }

        /// <summary>A soft retention target needs at most six new humans per fixture; a one-human shortfall uses a two-person group.</summary>
        public static int RetentionRequestSize(int current, int target)
        {
            if ((target != 150 && target != 300) || current < 0 || current >= target) return 0;
            return Math.Max(2, Math.Min(6, target - current));
        }
    }
}
