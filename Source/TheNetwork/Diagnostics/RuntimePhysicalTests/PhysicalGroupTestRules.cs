using System;
using System.Collections.Generic;
using TheNetwork.Domain.Physical;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    public enum GroupDwellResult { Wait, Complete, Invalid }

    /// <summary>Read-only live observations; no Pawn or vanilla tick behavior is simulated by this policy.</summary>
    public sealed class GroupDwellFacts
    {
        public readonly bool ownedEpisode, activeEpisode, exactMembers, sameBindings, sameRoles, healthySpawned,
            expectedMap, expectedFaction, sharedLord, reserved, notFree, unchangedIdentities, unchangedProjections, protectionUnbroken;

        public GroupDwellFacts(bool ownedEpisode, bool activeEpisode, bool exactMembers, bool sameBindings, bool sameRoles,
            bool healthySpawned, bool expectedMap, bool expectedFaction, bool sharedLord, bool reserved, bool notFree,
            bool unchangedIdentities, bool unchangedProjections, bool protectionUnbroken)
        {
            this.ownedEpisode = ownedEpisode; this.activeEpisode = activeEpisode; this.exactMembers = exactMembers;
            this.sameBindings = sameBindings; this.sameRoles = sameRoles; this.healthySpawned = healthySpawned;
            this.expectedMap = expectedMap; this.expectedFaction = expectedFaction; this.sharedLord = sharedLord;
            this.reserved = reserved; this.notFree = notFree; this.unchangedIdentities = unchangedIdentities;
            this.unchangedProjections = unchangedProjections; this.protectionUnbroken = protectionUnbroken;
        }
    }

    /// <summary>Continuity of an already-real person. Current capability and creation RoleSpec are deliberately absent.</summary>
    public sealed class GroupRetainedFacts
    {
        public readonly bool named, sameCharacter, samePawn, sameBinding, sameRole, reserved, noReprojection, skillsUncorrected, historyPreserved;

        public GroupRetainedFacts(bool named, bool sameCharacter, bool samePawn, bool sameBinding, bool sameRole,
            bool reserved, bool noReprojection, bool skillsUncorrected, bool historyPreserved)
        {
            this.named = named; this.sameCharacter = sameCharacter; this.samePawn = samePawn; this.sameBinding = sameBinding;
            this.sameRole = sameRole; this.reserved = reserved; this.noReprojection = noReprojection;
            this.skillsUncorrected = skillsUncorrected; this.historyPreserved = historyPreserved;
        }
    }

    /// <summary>Actual custody and existing Episode truth, read without repairing the prisoner or creating an identity.</summary>
    public sealed class GroupPendingCaptureFacts
    {
        public readonly bool ownedEpisode, exactMember, samePawn, samePawnRef, sameThingId, sameRole, ownedMap, liveSpawned,
            actualPrisoner, observedPlayerPrisoner, reserved, temporaryReserved, anonymous, unchangedIdentities, uncommitted, pendingPeer, conservedHeadcount;

        public GroupPendingCaptureFacts(bool ownedEpisode, bool exactMember, bool samePawn, bool samePawnRef, bool sameThingId, bool sameRole,
            bool ownedMap, bool liveSpawned, bool actualPrisoner, bool observedPlayerPrisoner, bool reserved, bool temporaryReserved,
            bool anonymous, bool unchangedIdentities, bool uncommitted, bool pendingPeer, bool conservedHeadcount)
        {
            this.ownedEpisode = ownedEpisode; this.exactMember = exactMember; this.samePawn = samePawn; this.samePawnRef = samePawnRef;
            this.sameThingId = sameThingId; this.sameRole = sameRole; this.ownedMap = ownedMap; this.liveSpawned = liveSpawned;
            this.actualPrisoner = actualPrisoner; this.observedPlayerPrisoner = observedPlayerPrisoner; this.reserved = reserved;
            this.temporaryReserved = temporaryReserved; this.anonymous = anonymous; this.unchangedIdentities = unchangedIdentities;
            this.uncommitted = uncommitted; this.pendingPeer = pendingPeer; this.conservedHeadcount = conservedHeadcount;
        }
    }

    /// <summary>Pure runtime-QA ownership and selection rules. Reads existing Episode truth; creates no identity or persisted QA state.</summary>
    public static class GroupQaRules
    {
        public const int MaterializationDwellTicks = 240;

        /// <summary>The held physical person must remain genuine across the atomic batch and named-reservation handoff too.</summary>
        public static bool CaptureCustodyHolds(GroupPendingCaptureFacts facts)
        {
            return facts != null && facts.ownedEpisode && facts.exactMember && facts.samePawn && facts.samePawnRef && facts.sameThingId
                && facts.sameRole && facts.ownedMap && facts.liveSpawned && facts.actualPrisoner && facts.observedPlayerPrisoner
                && facts.reserved && facts.conservedHeadcount;
        }

        public static bool PendingCaptureHolds(GroupPendingCaptureFacts facts, bool requirePendingPeers)
        {
            return CaptureCustodyHolds(facts) && facts.temporaryReserved && facts.anonymous && facts.unchangedIdentities
                && facts.uncommitted && (!requirePendingPeers || facts.pendingPeer);
        }

        /// <summary>Later custody recovery cannot erase a failed prerequisite earlier in this runtime run.</summary>
        public static bool CaptureFailureLatched(bool alreadyFailed, bool prerequisitesHold)
        {
            return alreadyFailed || !prerequisitesHold;
        }

        /// <summary>A named but never-bound origin pin is still a new candidate, not a retained Pawn.</summary>
        public static bool HasRetainedPawn(int characterId, int boundThingId, int resolvedThingId)
        {
            return characterId > 0 && boundThingId > 0 && boundThingId == resolvedThingId;
        }

        public static bool CreationPlacementHolds(RoleSpec creationSpec, RoleCandidate candidate)
        {
            return creationSpec != null && candidate != null && RoleRules.Verify(creationSpec, candidate).holds;
        }

        public static bool RetainedPlacementHolds(GroupRetainedFacts facts)
        {
            return facts != null && facts.named && facts.sameCharacter && facts.samePawn && facts.sameBinding && facts.sameRole
                && facts.reserved && facts.noReprojection && facts.skillsUncorrected && facts.historyPreserved;
        }

        /// <summary>Waiting consumes ordinary game ticks. Invalid evidence wins even at the completion boundary.</summary>
        public static GroupDwellResult EvaluateDwell(int startTick, int nowTick, GroupDwellFacts facts)
        {
            if (startTick < 0 || nowTick < startTick || facts == null || !facts.ownedEpisode || !facts.activeEpisode
                || !facts.exactMembers || !facts.sameBindings || !facts.sameRoles || !facts.healthySpawned || !facts.expectedMap
                || !facts.expectedFaction || !facts.sharedLord || !facts.reserved || !facts.notFree || !facts.unchangedIdentities
                || !facts.unchangedProjections || !facts.protectionUnbroken) return GroupDwellResult.Invalid;
            return (long)nowTick - startTick >= MaterializationDwellTicks ? GroupDwellResult.Complete : GroupDwellResult.Wait;
        }

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
