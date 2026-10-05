using TheNetwork.Domain.Actors;

namespace TheNetwork.Domain.Physical
{
    /// <summary>What the custody watch does about one held person after observing them (Phase 3.2A, PHYSICAL_LIFECYCLE § 9.4).</summary>
    public enum CustodyTransitionKind : byte
    {
        /// <summary>Nothing changed that the Network records: the person stays held exactly as recorded.</summary>
        None = 0,

        /// <summary>Still held, by a different vanilla holder: only <c>heldBy</c> moves (no authority, status or event changes).</summary>
        Holder = 1,

        /// <summary>A transition with consequences (a return, a death, a loss, a capture or a recruitment): reconciled through a Custody episode.</summary>
        Reconcile = 2
    }

    /// <summary>A pure custody decision: plain data, built from durable facts and one observation.</summary>
    public struct CustodyDecision
    {
        public CustodyTransitionKind kind;

        /// <summary>For <see cref="CustodyTransitionKind.Reconcile"/>: the terminal outcome the Custody episode commits.</summary>
        public MemberOutcome outcome;

        /// <summary>The holder the person is (still) held by after this decision; None for a return, a death or a loss.</summary>
        public HeldKind holder;

        /// <summary>A held outcome that makes the person a captive (status Captured), as opposed to being carried, recruited or merely kept.</summary>
        public bool captive;

        public override string ToString()
        {
            return kind + (kind == CustodyTransitionKind.Reconcile ? " " + outcome : "") + (holder != HeldKind.None ? " held " + holder : "") + (captive ? " captive" : "");
        }
    }

    /// <summary>
    /// The custody rules of Phase 3.2A (PHYSICAL_LIFECYCLE § 8.2, § 9, § 15.3), PURE: no store, port, job, event or vanilla object is read or
    /// changed. The adapter gathers plain facts into a <see cref="PhysicalObservation"/>; these rules decide.
    ///
    /// <list type="bullet">
    /// <item>A held observation (player prisoner or slave, recruited, kidnapped, held by another faction, a caravan, a transport) is a TERMINAL
    /// member outcome of a mission episode: the episode commits once and continuing captivity belongs to the person's own custody record.</item>
    /// <item>Only positive evidence returns a held person: a free world pawn the Network's own registry reserves, that has left its map, owes
    /// allegiance to no permanent faction, and was never recruited. "Not spawned" is never evidence of anything.</item>
    /// <item>An observation the rules cannot classify keeps the person held (<see cref="HeldKind.Unknown"/>): fail closed, never home.</item>
    /// <item>A recruited person (<see cref="CharacterStatus.Defected"/>) permanently exits the old NPC contractor's availability. This is the
    /// Phase 3 bridge; future Player Contractor participation reads the real colony through PlayerProxy, never an ex-contractor simulation.</item>
    /// </list>
    /// </summary>
    public static class CustodyRules
    {
        /// <summary>The purpose key of an episode that reconciles a transition of a person vanilla already holds (§ 9.4 "a custody event").</summary>
        public const string Purpose = "Custody";

        public static bool IsCustodyEpisode(PhysicalEpisode e)
        {
            return e != null && e.purposeKey == Purpose;
        }

        /// <summary>Does vanilla hold the pawn as one of the custody states 3.2A understands?</summary>
        public static bool IsHeldKind(ObservedKind k)
        {
            switch (k)
            {
                case ObservedKind.HeldByPlayer:
                case ObservedKind.JoinedPlayer:
                case ObservedKind.Kidnapped:
                case ObservedKind.HeldByOther:
                case ObservedKind.InCaravan:
                case ObservedKind.InTransport:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>A captive custody: the person is someone's prisoner or slave (the player's, a kidnapper's, another faction's).</summary>
        public static bool IsCaptiveKind(ObservedKind k)
        {
            return k == ObservedKind.HeldByPlayer || k == ObservedKind.Kidnapped || k == ObservedKind.HeldByOther;
        }

        /// <summary>
        /// The holder of a held observation. The adapter reports it positively (<see cref="PhysicalObservation.holder"/>); when it did not, the
        /// observation kind gives the coarse holder, and a caravan or transport whose owner is unknown is <see cref="HeldKind.Unknown"/>.
        /// </summary>
        public static HeldKind HolderOf(PhysicalObservation o)
        {
            if (o == null) return HeldKind.Unknown;
            if (o.holder != HeldKind.None) return o.holder;
            switch (o.kind)
            {
                case ObservedKind.HeldByPlayer: return HeldKind.PlayerPrisoner;
                case ObservedKind.JoinedPlayer: return HeldKind.PlayerColonist;
                case ObservedKind.Kidnapped: return HeldKind.Kidnapped;
                case ObservedKind.HeldByOther: return HeldKind.OtherFaction;
                case ObservedKind.InTransport: return HeldKind.Transport;
                default: return HeldKind.Unknown;
            }
        }

        /// <summary>The coarse member outcome of a holder (the persisted <see cref="MemberOutcome"/> vocabulary is unchanged).</summary>
        public static MemberOutcome OutcomeFor(HeldKind h)
        {
            switch (h)
            {
                case HeldKind.PlayerPrisoner:
                case HeldKind.PlayerSlave:
                case HeldKind.PlayerCaravan:
                    return MemberOutcome.HeldByPlayer;
                case HeldKind.PlayerColonist:
                    return MemberOutcome.JoinedPlayer;
                case HeldKind.Kidnapped:
                    return MemberOutcome.Kidnapped;
                default:
                    return MemberOutcome.HeldByOther;
            }
        }

        /// <summary>A held member outcome (as opposed to Returned, Killed, Missing, Lost, NeverPlaced or Detached)?</summary>
        public static bool IsHeldOutcome(MemberOutcome o)
        {
            return o == MemberOutcome.HeldByPlayer || o == MemberOutcome.JoinedPlayer || o == MemberOutcome.Kidnapped || o == MemberOutcome.HeldByOther;
        }

        /// <summary>
        /// A free world pawn that owes allegiance to a permanent faction other than the player's: a captor recruited them, or another system made
        /// them a member. Never a free return; the Network keeps them held by <see cref="HeldKind.OtherFaction"/>.
        /// </summary>
        public static bool JoinedAnotherFaction(PhysicalObservation o)
        {
            return o != null && o.kind == ObservedKind.WorldFree && o.otherAllegiance;
        }

        /// <summary>
        /// A HELD terminal outcome of a mission episode's member (§ 15.3): which outcome, held by whom, and whether it is a capture. Only called
        /// for a held observation, or a world pawn that joined another faction.
        /// </summary>
        public static MemberOutcome MissionHeld(PhysicalObservation o, out HeldKind heldBy, out bool captive)
        {
            if (JoinedAnotherFaction(o))
            {
                heldBy = HeldKind.OtherFaction;
                captive = false;
                return MemberOutcome.HeldByOther;
            }
            heldBy = HolderOf(o);
            captive = IsCaptiveKind(o.kind);
            switch (o.kind)
            {
                case ObservedKind.HeldByPlayer: return MemberOutcome.HeldByPlayer;
                case ObservedKind.JoinedPlayer: return MemberOutcome.JoinedPlayer;
                case ObservedKind.Kidnapped: return MemberOutcome.Kidnapped;
                case ObservedKind.HeldByOther: return MemberOutcome.HeldByOther;
                default: return OutcomeFor(heldBy); // a caravan or a transport: held, not captive
            }
        }

        /// <summary>
        /// THE custody-watch decision for one held person (custody OutOfCustody, no episode link): what the observation means, from positive
        /// evidence only. <paramref name="status"/> and <paramref name="current"/> are the person's durable story status and recorded holder.
        /// </summary>
        public static CustodyDecision Transition(CharacterStatus status, HeldKind current, PhysicalObservation o)
        {
            if (o == null || status == CharacterStatus.Dead || status == CharacterStatus.Lost) return None();
            switch (o.kind)
            {
                case ObservedKind.Dead:
                    return Reconcile(MemberOutcome.Killed, HeldKind.None, false);
                case ObservedKind.Gone:
                    return Reconcile(MemberOutcome.Lost, HeldKind.None, false);
                case ObservedKind.WorldFree:
                    if (!o.exitEvidence) return None();
                    if (o.otherAllegiance) return Holder(current, HeldKind.OtherFaction);
                    // A former player recruit remains outside old NPC availability even after banishment, release or departure (O-20).
                    if (status == CharacterStatus.Defected) return Holder(current, HeldKind.Unaffiliated);
                    return Reconcile(MemberOutcome.Returned, HeldKind.None, false);
                case ObservedKind.HeldByPlayer:
                case ObservedKind.Kidnapped:
                case ObservedKind.HeldByOther:
                    {
                        HeldKind h = HolderOf(o);
                        // Someone who was held without being anybody's captive (carried in a caravan, unaffiliated, unknown) becomes one.
                        if (status != CharacterStatus.Captured && status != CharacterStatus.Defected) return Reconcile(OutcomeFor(h), h, true);
                        return Holder(current, h);
                    }
                case ObservedKind.JoinedPlayer:
                    if (status != CharacterStatus.Defected) return Reconcile(MemberOutcome.JoinedPlayer, HeldKind.PlayerColonist, false);
                    return Holder(current, HeldKind.PlayerColonist);
                case ObservedKind.InCaravan:
                case ObservedKind.InTransport:
                    return Holder(current, HolderOf(o));
                case ObservedKind.Spawned:
                    // On some map, nobody's prisoner, colonist or passenger (a released prisoner walking out, a guest set down): still not free.
                    return Holder(current, HeldKind.Unaffiliated);
                case ObservedKind.WorldOther:
                case ObservedKind.ReservationBroken:
                    // Vanilla owns the pawn in a way the Network does not reconcile (another quest, a faction leader, a broken reservation): unchanged.
                    return None();
                default:
                    // Unknown or no observation: fail closed. The person stays held; the holder is recorded as unknown.
                    return Holder(current, HeldKind.Unknown);
            }
        }

        /// <summary>
        /// The decision a CUSTODY episode commits (it always closes at its first successful commit): the transition when it still has
        /// consequences, otherwise the person's current holding (nothing changes but the record of who holds them). A broken reservation is
        /// handled before this by the ordinary reconcile (the episode is quarantined).
        /// </summary>
        public static CustodyDecision EpisodeDecision(CharacterStatus status, HeldKind current, PhysicalObservation o)
        {
            CustodyDecision t = Transition(status, current, o);
            if (t.kind == CustodyTransitionKind.Reconcile) return t;
            HeldKind h = t.kind == CustodyTransitionKind.Holder ? t.holder : (current == HeldKind.None ? HeldKind.Unknown : current);
            return Reconcile(OutcomeFor(h), h, false);
        }

        private static CustodyDecision None()
        {
            return new CustodyDecision { kind = CustodyTransitionKind.None };
        }

        private static CustodyDecision Holder(HeldKind current, HeldKind next)
        {
            if (next == current) return None();
            return new CustodyDecision { kind = CustodyTransitionKind.Holder, holder = next };
        }

        private static CustodyDecision Reconcile(MemberOutcome outcome, HeldKind heldBy, bool captive)
        {
            return new CustodyDecision { kind = CustodyTransitionKind.Reconcile, outcome = outcome, holder = heldBy, captive = captive };
        }
    }
}
