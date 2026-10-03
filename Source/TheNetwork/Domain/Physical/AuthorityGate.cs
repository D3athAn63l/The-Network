using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Kernel;

namespace TheNetwork.Domain.Physical
{
    /// <summary>Which layer advances a person right now (§ 3.1, § 8.3). Derived, never stored.</summary>
    public enum PersonAuthority : byte
    {
        /// <summary>The Network record is truth: upkeep, recovery, travel, careers, procurement and succession may act.</summary>
        Abstract = 0,

        /// <summary>A member of a Planned or Open episode: the pawn is truth for the facts the projection owns.</summary>
        Physical = 1,

        /// <summary>Reconciled back (custody Stored or held) but the episode's RELEASE has not completed: nobody advances the person yet (P3-INV-029).</summary>
        PendingRelease = 2,

        /// <summary>A vanilla system holds the pawn (prisoner, colonist, kidnapped, caravan, …): the Network only observes.</summary>
        VanillaHeld = 3,

        /// <summary>Released to vanilla (dead, or deliberately released): terminal.</summary>
        Released = 4,

        /// <summary>The binding is gone with no evidence: never "home", diagnosed.</summary>
        Lost = 5
    }

    /// <summary>
    /// THE single authority gate (PHYSICAL_LIFECYCLE § 3.3 A1, P3-INV-002, P3-INV-029). Every abstract writer of person-specific
    /// simulation truth asks <see cref="CanSimulateAbstractly"/> first. It is O(1) and reads only the person's own record: custody
    /// must be Unmaterialized or Stored AND the person must have NO episode membership at all. The membership link is set at
    /// Plan and cleared only when that episode's RELEASE completes, so a Planned episode, an Open one, and a Closed one whose
    /// release is still pending all keep the gate closed: <c>Stored</c> alone is not enough.
    ///
    /// In Phase 3.0 no production path opens an episode, so every person in a live game stays Unmaterialized with no link and
    /// the gate only confirms the existing abstract state (no behaviour changes); tests drive it over a scriptable port.
    /// </summary>
    public static class AuthorityGate
    {
        /// <summary>Abstract writes the gate refused (diagnostics; always 0 in a live 3.0 game).</summary>
        public static int refusedWrites;

        /// <summary>The last refused writer and person (diagnostics).</summary>
        public static string lastRefusal;

        public static bool CanSimulateAbstractly(KnownCharacter c)
        {
            if (c == null || c.episode.IsValid) return false;
            return c.custody == CustodyState.Unmaterialized || c.custody == CustodyState.Stored;
        }

        /// <summary>The gate with a recorded refusal, for a writer that silently skips a person it may not advance.</summary>
        public static bool Allows(KnownCharacter c, string writer)
        {
            if (CanSimulateAbstractly(c)) return true;
            refusedWrites++;
            lastRefusal = writer + " → " + (c == null ? "(none)" : c.id + " " + AuthorityOf(c));
            return false;
        }

        public static PersonAuthority AuthorityOf(KnownCharacter c)
        {
            if (c == null) return PersonAuthority.Lost;
            switch (c.custody)
            {
                case CustodyState.Deployed: return PersonAuthority.Physical;
                case CustodyState.OutOfCustody: return c.episode.IsValid ? PersonAuthority.PendingRelease : PersonAuthority.VanillaHeld;
                case CustodyState.Released: return c.episode.IsValid ? PersonAuthority.PendingRelease : PersonAuthority.Released;
                case CustodyState.Lost: return c.episode.IsValid ? PersonAuthority.PendingRelease : PersonAuthority.Lost;
                default: return c.episode.IsValid ? PersonAuthority.PendingRelease : PersonAuthority.Abstract;
            }
        }

        /// <summary>
        /// A Solo's whereabouts ARE its person's: while that person is not abstract, the actor's hidden spatial entry is frozen
        /// (no catch-up, no relocation, § 12.1). An organization's main body keeps moving (a detachment is like a concurrent job).
        /// </summary>
        public static bool SpatialFrozen(DomainContext ctx, NetworkActor a)
        {
            if (a == null || ctx?.characters == null || !ContractorService.IsSolo(a) || !a.bindings.embodies.IsValid) return false;
            return !CanSimulateAbstractly(ctx.characters.Get(a.bindings.embodies));
        }

        /// <summary>
        /// Some of this actor's people are in an episode that is not complete (or its Solo person is not abstract): an open episode
        /// counts as a job (§ 2.3: careers). O(1) through the store's derived index; O(1) on the record when there is no store.
        /// </summary>
        public static bool HasPhysicalPresence(DomainContext ctx, NetworkActor a)
        {
            if (a == null) return false;
            if (ctx?.episodes != null && ctx.episodes.HasIncomplete(a.id)) return true;
            return SpatialFrozen(ctx, a);
        }
    }
}
