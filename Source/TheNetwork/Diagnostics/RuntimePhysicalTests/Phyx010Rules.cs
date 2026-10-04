using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    // The PURE rules of the read-only RT-PHYX-010 verifier ("010V VERIFY — loaded save"). They read plain, already-persisted episode and character
    // data plus a few facts the runner gathers, and decide: which episode is the relevant one, and whether its persisted TERMINAL result is
    // the expected one. They generate, spawn, destroy, discard, reconcile and persist nothing, and no new persisted field backs them: the
    // relevant episode is found from the EXISTING cause (EpisodeCause.devKey, "PhysicalTest:<runId>:RT-PHYX-010").
    //
    // WHY A TERMINAL BRANCH: RimWorld may resume time immediately after a load, even when the checkpoint was saved while paused (the owner's
    // first 010B reload did). The saved episode can then reconcile on the FIRST gameplay tick, before anyone can click the verifier. That is
    // production working as designed (the registry is correct before the first tick), not a failure, so the verifier must be able to validate the
    // persisted terminal result instead of expecting an incomplete episode. It does NOT pretend a terminal state proves the instantaneous
    // pre-reconciliation state at load: that state is established by the 010A / 010B SAVE setup evidence.

    public enum Phyx010Branch
    {
        /// <summary>No RT-PHYX-010 episode exists in this save: nothing relevant to validate (reported as inconclusive, never guessed).</summary>
        None = 0,

        /// <summary>Branch A: a matching episode is still incomplete: verify it and follow it, read-only, to completion.</summary>
        Incomplete = 1,

        /// <summary>Branch B: the relevant episode already completed after the load: validate the persisted terminal result.</summary>
        Terminal = 2,

        /// <summary>More than one completed candidate cannot be told apart by the existing provenance: reported as inconclusive, never guessed.</summary>
        Ambiguous = 3
    }

    public sealed class Phyx010Selection
    {
        public Phyx010Branch branch;

        /// <summary>The relevant episode (Terminal), or null.</summary>
        public PhysicalEpisode relevant;

        /// <summary>The matching episodes that are still incomplete (Incomplete).</summary>
        public readonly List<PhysicalEpisode> incomplete = new List<PhysicalEpisode>();

        /// <summary>A plain explanation, for the log, of how the episode was (or was not) chosen.</summary>
        public string diagnostic;

        public const string Family = "RT-PHYX-010";

        public static bool IsFamily(PhysicalEpisode e)
        {
            return e != null && e.cause != null && PhysicalTestIds.ScenarioOf(e.cause.devKey) == Family;
        }

        /// <summary>
        /// Deterministic selection. Any matching episode still incomplete selects Branch A. Otherwise, among the COMPLETED matching episodes the latest
        /// one is the relevant one: greatest <c>createdTick</c>, and episode ids only break a tie of ids that differ in creation order (ids are allocated
        /// in creation order, so they are never ambiguous). Two completed candidates with the SAME creation tick cannot be told apart by the existing
        /// provenance and are reported Ambiguous. An old completed 010 is never accepted while a newer matching one is incomplete, and an episode of
        /// another scenario is never a candidate. "Some completed 010 exists" is NOT a pass: the chosen episode is then judged clause by clause.
        /// </summary>
        public static Phyx010Selection Select(IList<PhysicalEpisode> episodes)
        {
            Phyx010Selection s = new Phyx010Selection();
            List<PhysicalEpisode> completed = new List<PhysicalEpisode>();
            if (episodes != null)
            {
                for (int i = 0; i < episodes.Count; i++)
                {
                    PhysicalEpisode e = episodes[i];
                    if (!IsFamily(e)) continue;
                    if (e.IsComplete) completed.Add(e);
                    else s.incomplete.Add(e);
                }
            }
            if (s.incomplete.Count > 0)
            {
                s.branch = Phyx010Branch.Incomplete;
                s.diagnostic = s.incomplete.Count + " matching RT-PHYX-010 episode(s) still incomplete: verifying and following them, read-only.";
                return s;
            }
            if (completed.Count == 0)
            {
                s.branch = Phyx010Branch.None;
                s.diagnostic = "this save holds no RT-PHYX-010 episode (none is incomplete and none is complete): there is nothing relevant to validate.";
                return s;
            }
            completed.Sort((a, b) =>
            {
                int c = b.createdTick.CompareTo(a.createdTick);
                return c != 0 ? c : b.id.Value.CompareTo(a.id.Value);
            });
            PhysicalEpisode latest = completed[0];
            if (completed.Count > 1 && completed[1].createdTick == latest.createdTick)
            {
                s.branch = Phyx010Branch.Ambiguous;
                s.diagnostic = "episodes " + latest.id + " and " + completed[1].id + " are both completed RT-PHYX-010 episodes created at tick " + latest.createdTick
                    + ": the existing provenance cannot tell which one this load resumed, so nothing is guessed.";
                return s;
            }
            s.branch = Phyx010Branch.Terminal;
            s.relevant = latest;
            s.diagnostic = completed.Count == 1 ? "the one completed RT-PHYX-010 episode " + latest.id + " is the relevant one."
                : "of " + completed.Count + " completed RT-PHYX-010 episodes, the latest (" + latest.id + ", created at tick " + latest.createdTick + ") is the relevant one; older ones are history.";
            return s;
        }
    }

    /// <summary>The world and registry facts the runner reads for the terminal check (plain values: the rule stays pure and headlessly testable).</summary>
    public sealed class Phyx010WorldFacts
    {
        /// <summary>The binding's pointer resolved to a Pawn.</summary>
        public bool pawnResolved;

        /// <summary>That Pawn's own thing id.</summary>
        public int pawnThingId;

        /// <summary>How many Pawns in the game carry that thing id (a clone would make it more than one).</summary>
        public int pawnsWithThatThingId = 1;

        public bool worldPawn;
        public bool reservedByQuest;
        public bool registryReserves;

        /// <summary>Findings of the registry's read-only binding audit.</summary>
        public int integrityFindings;
    }

    public sealed class Phyx010Clause
    {
        public bool ok;
        public string text;
    }

    /// <summary>The expected persisted TERMINAL result of a completed RT-PHYX-010 episode: Returned, Stored, the same Pawn, reserved, nothing wrong.</summary>
    public static class Phyx010Terminal
    {
        public static List<Phyx010Clause> Judge(PhysicalEpisode e, KnownCharacter c, Phyx010WorldFacts w)
        {
            List<Phyx010Clause> r = new List<Phyx010Clause>();
            Action<bool, string> add = (ok, text) => r.Add(new Phyx010Clause { ok = ok, text = text });
            if (e == null)
            {
                add(false, "there is no episode to judge");
                return r;
            }
            if (w == null) w = new Phyx010WorldFacts();
            add(Phyx010Selection.IsFamily(e), "the episode is an RT-PHYX-010 episode by its existing persisted cause (" + (e.cause?.devKey ?? "no cause") + ")");

            // The episode is terminal, and every stage marker is complete.
            add(e.state == EpisodeState.Closed, "the episode is terminal: Closed (got " + e.state + ")");
            add(e.consequencesApplied, "the commit's consequences were applied exactly once (consequencesApplied)");
            add(e.releaseApplied, "RELEASE is complete (releaseApplied)");
            add(e.followUpApplied, "FOLLOW-UP is complete (followUpApplied)");
            add(e.PublishDone, "PUBLISH is complete (publishedTick " + e.publishedTick + ")");
            add(e.IsComplete, "the episode is COMPLETE (release, follow-up and publication all done)");
            add(e.state != EpisodeState.Quarantined && e.quarantineKey == null, "the episode is not quarantined (" + (e.quarantineKey ?? "no quarantine key") + ")");

            // Exactly one named member, Returned, from the expected world-return observation.
            EpisodeMember m = e.members != null && e.members.Count == 1 ? e.members[0] : null;
            add(m != null, "the episode has exactly one member (the scenario drives one person; got " + (e.members?.Count ?? 0) + ")");
            if (m == null) return r;
            add(m.IsNamed, "the member is a named person");
            add(m.outcome == MemberOutcome.Returned, "the member's outcome is Returned (got " + m.outcome + "; Lost, Killed, Captured, Missing and NeverPlaced are all failures here)");
            add(m.observed == ObservedKind.WorldFree, "the observed truth is the expected world-return observation, WorldFree (got " + m.observed + ")");
            add(m.observed != ObservedKind.ReservationBroken, "the registry reservation did not fail: no ReservationBroken observation");

            // The same durable Pawn: the person's own binding, the member's mirror of it, and the real Pawn.
            bool bound = c != null && c.pawn != null && c.pawn.IsBound;
            add(c != null && m.character == c.id, "the member belongs to the person being checked");
            add(bound, "the person holds a durable Pawn binding");
            if (!bound) return r;
            add(m.pawn != null && m.pawn.IsBound && m.pawn.SameBinding(c.pawn) && m.pawn.thingIdNumber == c.pawn.thingIdNumber,
                "the member's binding is the person's own binding: the SAME Pawn (persisted #" + c.pawn.thingIdNumber + "), no replacement");
            add(w.pawnResolved && w.pawnThingId == c.pawn.thingIdNumber, "the binding resolves to a Pawn whose own thing id is the persisted one (#" + w.pawnThingId + ")");
            add(w.pawnsWithThatThingId == 1, "no duplicated Thing ID: exactly one Pawn carries #" + c.pawn.thingIdNumber + " (" + w.pawnsWithThatThingId + ")");

            // The person: Stored, alive, no active episode link, abstract authority again.
            add(c.IsAlive, "the person is alive");
            add(c.custody == CustodyState.Stored, "custody is Stored (got " + c.custody + ")");
            add(!c.episode.IsValid, "the person has no active episode link (got " + c.episode + ")");
            add(AuthorityGate.CanSimulateAbstractly(c), "abstract authority is open again");

            // The Pawn: a world pawn, reserved by the Network's own registry, no integrity finding.
            add(w.worldPawn, "the Pawn is a world pawn");
            add(w.reservedByQuest, "vanilla sees it as ReservedByQuest");
            add(w.registryReserves, "the Network's registry covers it");
            add(w.integrityFindings == 0, "no binding-integrity finding (" + w.integrityFindings + ")");
            return r;
        }

        /// <summary>The clauses that failed.</summary>
        public static List<Phyx010Clause> Failures(List<Phyx010Clause> clauses)
        {
            List<Phyx010Clause> f = new List<Phyx010Clause>();
            if (clauses != null) for (int i = 0; i < clauses.Count; i++) if (!clauses[i].ok) f.Add(clauses[i]);
            return f;
        }
    }
}
