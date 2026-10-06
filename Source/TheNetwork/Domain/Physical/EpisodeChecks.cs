using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Operations;
using TheNetwork.Kernel;

namespace TheNetwork.Domain.Physical
{
    /// <summary>
    /// The validator's Phase 3 findings (PHYSICAL_LIFECYCLE § 16.5): REPORT, never repair-by-guessing. The only thing it may change
    /// is DERIVED state (it rebuilds the episode index). It never invents custody, never declares anyone Returned, never heals,
    /// never regenerates a pawn, never fabricates or completes an episode and never clears a dangerous contradiction: every finding
    /// is left in place, visible in the validation report, and the authority gate keeps the people involved blocked.
    /// </summary>
    public static class EpisodeChecks
    {
        public static int Report(DomainContext ctx, List<string> findings)
        {
            if (ctx?.episodes == null) return 0;
            int before = findings.Count;
            EpisodeStore store = ctx.episodes;
            store.RebuildIndex(); // derived only
            int now = ctx.Now;

            Dictionary<int, PhysicalEpisode> incompleteByMember = new Dictionary<int, PhysicalEpisode>();
            Dictionary<int, PhysicalEpisode> unreleasedByPawn = new Dictionary<int, PhysicalEpisode>();
            HashSet<int> ids = new HashSet<int>();
            for (int i = 0; i < store.episodes.Count; i++)
            {
                PhysicalEpisode e = store.episodes[i];
                if (e == null) continue;
                if (!ids.Add(e.id.Value)) findings.Add("Episode id " + e.id + " is used twice.");
                if (ctx.actors.Get(e.actor) == null) findings.Add("Episode " + e.id + ": actor " + e.actor + " is missing.");
                if (e.consequencesApplied && e.committedTick < 0) findings.Add("Episode " + e.id + ": consequences applied but no committed tick.");
                if (e.consequencesApplied && e.state != EpisodeState.Closed) findings.Add("Episode " + e.id + ": consequences applied while " + e.state + ".");
                if (e.state == EpisodeState.Closed && !e.consequencesApplied) findings.Add("Episode " + e.id + ": Closed without its commit flag.");
                if (e.state != EpisodeState.Closed && (e.releaseApplied || e.followUpApplied || e.PublishDone)) findings.Add("Episode " + e.id + ": a stage marker is set before the commit.");
                if (e.publishCursor < 0 || e.publishCursor > e.publications.Count) findings.Add("Episode " + e.id + ": publish cursor " + e.publishCursor + " outside its outbox of " + e.publications.Count + ".");
                if (e.PublishDone && (e.publications.Count > 0 || e.publishCursor != 0)) findings.Add("Episode " + e.id + ": published, yet the outbox still holds " + e.publications.Count + ".");
                if (e.publications.Count > PhysicalEpisode.MaxPublications) findings.Add("Episode " + e.id + ": outbox over its bound (" + e.publications.Count + ").");
                if (e.state == EpisodeState.Quarantined) findings.Add("Episode " + e.id + " is quarantined (" + e.quarantineKey + "); its people stay blocked.");
                if (e.IsActive && e.openedTick >= 0 && now - e.openedTick > PhysicalLifecycleService.LongOpenTicks) findings.Add("Episode " + e.id + " has been open for more than 30 days.");
                if (e.HasPendingStage && e.closedTick >= 0 && now - e.closedTick > PhysicalLifecycleService.LongOpenTicks)
                {
                    findings.Add("Episode " + e.id + ": a post-commit stage is unfinished after 30 days (release " + e.releaseApplied + ", follow-up " + e.followUpApplied + ", published " + e.PublishDone + "); reported, never completed by guessing.");
                }

                HashSet<int> pawns = new HashSet<int>();
                for (int k = 0; k < e.members.Count; k++)
                {
                    EpisodeMember m = e.members[k];
                    if (m == null) continue;
                    bool clearedOrdinaryHistory = e.releaseApplied && !m.IsNamed && !m.IsBound && OrganizationCompositionV1.IsRole(m.seatRole) && m.releaseStep > 0;
                    int actions = ReleasePolicy.ActionsFor(m.IsBound || clearedOrdinaryHistory, m.IsNamed, m.outcome).Length;
                    if (m.releaseStep > actions) findings.Add("Episode " + e.id + " " + m + ": release cursor " + m.releaseStep + " beyond its " + actions + " actions.");
                    if (e.releaseApplied && m.releaseStep < actions) findings.Add("Episode " + e.id + " " + m + ": released, yet the member's cursor is " + m.releaseStep + " of " + actions + ".");
                    if (e.state == EpisodeState.Closed && m.state != MemberState.Done) findings.Add("Episode " + e.id + " " + m + ": Closed with a member not Done.");
                    if (m.IsBound && m.pawn.thingIdNumber > 0 && !pawns.Add(m.pawn.thingIdNumber)) findings.Add("Episode " + e.id + ": two members share " + m.pawn + ".");
                    if (!e.releaseApplied && m.IsBound && m.pawn.thingIdNumber > 0)
                    {
                        PhysicalEpisode owner;
                        if (unreleasedByPawn.TryGetValue(m.pawn.thingIdNumber, out owner) && owner != e)
                            findings.Add("Pawn " + m.pawn.thingIdNumber + " has two unreleased Episode owners (" + owner.id + ", " + e.id + ").");
                        else unreleasedByPawn[m.pawn.thingIdNumber] = e;
                    }
                    if (m.p0Eligible && (m.playerVisibleTick < 0 || !OrganizationCompositionV1.IsRole(m.seatRole)))
                        findings.Add("Episode " + e.id + " " + m + ": P0 eligibility lacks placement or operational-role evidence.");
                    if (!m.IsNamed) continue;
                    KnownCharacter c = ctx.characters.Get(m.character);
                    if (c == null)
                    {
                        findings.Add("Episode " + e.id + ": member " + m.character + " has no record.");
                        continue;
                    }
                    if (!e.releaseApplied && c.episode != e.id) findings.Add("Episode " + e.id + ": " + c.id + " is a member but points to " + c.episode + ".");
                    if (!e.IsComplete)
                    {
                        PhysicalEpisode other;
                        if (incompleteByMember.TryGetValue(c.id.Value, out other) && other != e) findings.Add(c.id + " is a member of two incomplete episodes (" + other.id + ", " + e.id + ").");
                        else incompleteByMember[c.id.Value] = e;
                    }
                }

                if (e.cause.operation.IsValid && !e.IsComplete)
                {
                    Operation op = ctx.operations?.Get(e.cause.operation);
                    if (op == null) findings.Add("Episode " + e.id + ": linked operation " + e.cause.operation + " is missing.");
                    else if (e.state != EpisodeState.Closed && (op.status != OpStatus.Physical || op.physicalEpisode != e.id)) findings.Add("Episode " + e.id + ": linked operation " + op.id + " is " + op.status + ", not held by it.");
                }
            }

            for (int i = 0; i < ctx.characters.characters.Count; i++)
            {
                KnownCharacter c = ctx.characters.characters[i];
                if (c == null) continue;
                bool bound = c.pawn != null && c.pawn.IsBound;
                if (c.episode.IsValid)
                {
                    PhysicalEpisode e = store.Get(c.episode);
                    if (e == null) findings.Add(c.id + " points to episode " + c.episode + ", which does not exist (the gate keeps it blocked).");
                    else if (e.MemberFor(c.id) == null) findings.Add(c.id + " points to episode " + e.id + " without being one of its members.");
                    else if (e.releaseApplied) findings.Add(c.id + " still points to " + e.id + " after its release completed.");
                }
                PhysicalEpisode own = c.episode.IsValid ? store.Get(c.episode) : null;
                if (c.custody == CustodyState.Deployed && (own == null || own.state == EpisodeState.Closed)) findings.Add(c.id + " is Deployed with no Planned, Open or Quarantined episode.");
                if (c.custody == CustodyState.Stored && !bound) findings.Add(c.id + " is Stored with no pawn binding.");
                if (bound && c.custody == CustodyState.Unmaterialized) findings.Add(c.id + " has a pawn binding but custody Unmaterialized.");
                if (bound && c.status == CharacterStatus.Dead && c.custody != CustodyState.Released && !c.episode.IsValid) findings.Add(c.id + " is dead with a binding but custody " + c.custody + ".");
                if (c.custody == CustodyState.OutOfCustody && c.heldBy == HeldKind.None) findings.Add(c.id + " is OutOfCustody with no holder recorded.");
                // Phase 3.2A: impossible custody combinations (reported; the gate keeps the people blocked, nothing is repaired).
                if (c.custody == CustodyState.OutOfCustody && !bound) findings.Add(c.id + " is held by vanilla (OutOfCustody) with no pawn binding: no holder can be observed.");
                if (c.custody == CustodyState.OutOfCustody && c.heldSinceTick < 0) findings.Add(c.id + " is held by vanilla with no heldSinceTick.");
                if (c.custody != CustodyState.OutOfCustody && c.heldBy != HeldKind.None)
                {
                    findings.Add(c.id + " is " + c.custody + " but still records a vanilla holder (" + c.heldBy + ").");
                }
                if (c.custody != CustodyState.OutOfCustody && c.heldSinceTick >= 0)
                {
                    findings.Add(c.id + " is " + c.custody + " but still records a live heldSinceTick (" + c.heldSinceTick + ").");
                }
                if (c.custody == CustodyState.Stored && c.status == CharacterStatus.Defected) findings.Add(c.id + " is Defected (recruited by the player) yet Stored: a recruited person is never stored back.");
            }

            for (int i = 0; i < store.episodes.Count; i++)
            {
                PhysicalEpisode e = store.episodes[i];
                if (e == null || !CustodyRules.IsCustodyEpisode(e)) continue;
                if (e.members.Count != 1 || !e.members[0].IsNamed) findings.Add("Custody episode " + e.id + " must hold exactly one named person (" + e.members.Count + ").");
                if (e.cause.operation.IsValid || e.faction != null && e.faction.IsValid) findings.Add("Custody episode " + e.id + " carries an operation or an encounter faction.");
            }

            if (ctx.operations != null)
            {
                for (int i = 0; i < ctx.operations.operations.Count; i++)
                {
                    Operation op = ctx.operations.operations[i];
                    if (op == null || op.IsFinished) continue;
                    if (op.status == OpStatus.Physical)
                    {
                        PhysicalEpisode e = store.Get(op.physicalEpisode);
                        if (e == null || e.IsComplete) findings.Add("Operation " + op.id + " is Physical but no incomplete episode holds it.");
                    }
                    if (op.outcomeApplied) continue;
                    // Operation/person exclusivity (ADR-039 extended): a person on a live operation is in no OTHER episode.
                    for (int k = 0; k < op.characters.Count; k++)
                    {
                        PhysicalEpisode e;
                        if (incompleteByMember.TryGetValue(op.characters[k].Value, out e) && e.cause.operation != op.id)
                        {
                            findings.Add(op.characters[k] + " is on operation " + op.id + " and in episode " + e.id + ".");
                        }
                    }
                }
            }
            return findings.Count - before;
        }
    }
}
