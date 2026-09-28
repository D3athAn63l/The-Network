using System.Collections.Generic;
using TheNetwork.Kernel;

namespace TheNetwork.Domain.Contracts
{
    /// <summary>Field Log line keys. The text is "TheNetwork_FieldLog_" + key in the language files.</summary>
    public static class FieldLogKeys
    {
        public const string Accepted = "Accepted";
        public const string TookOver = "TookOver";
        public const string SetOut = "SetOut";
        public const string WorkingNearby = "WorkingNearby";
        public const string Arrived = "Arrived";
        public const string WorseThanExpected = "WorseThanExpected";
        public const string PaidMore = "PaidMore";
        public const string ReducedScope = "ReducedScope";
        public const string CarryOn = "CarryOn";
        public const string Delayed = "Delayed";
        public const string Missing = "Missing";
        public const string Stranded = "Stranded";
        public const string Captured = "Captured";
        public const string CapturedSolo = "CapturedSolo";
        public const string Recovered = "Recovered";
        public const string SecuredAll = "SecuredAll";
        public const string SecuredPart = "SecuredPart";
        public const string PartialAccepted = "PartialAccepted";
        public const string PartialContinued = "PartialContinued";
        public const string DeliveryHeld = "DeliveryHeld";
        public const string DeliveryRetry = "DeliveryRetry";
        public const string PaymentDue = "PaymentDue";
        public const string Handover = "Handover";
        public const string TransportArranged = "TransportArranged";
    }

    /// <summary>
    /// The Field Log (SPATIAL § 8): a temporary activity journal for work a contractor is doing FOR THE
    /// PLAYER, owned by the player-issued contract (never by the contractor). It starts when the player
    /// accepts a quote, records only meaningful beats in reporting language (never a tile, a route or a
    /// hidden number, never a daily "nothing happened"), and is cleared when the contract closes;
    /// History and letters keep the durable record. Written in the same step as the state change it
    /// reports, so a reload never duplicates or rewords an entry.
    /// </summary>
    public sealed class FieldLogService
    {
        public const int MaxEntries = 24;

        private static readonly List<FieldLogEntry> None = new List<FieldLogEntry>();

        private readonly DomainContext ctx;

        public FieldLogService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        /// <summary>Only contracts the player issued keep a Field Log.</summary>
        public bool Records(Contract c)
        {
            return c != null && ctx.actors?.PlayerProxy != null && c.parties.issuer == ctx.actors.PlayerProxyId;
        }

        /// <summary>
        /// Appends a line to the live log of a player's accepted, still-running contract. A line equal to
        /// the last one is not repeated (a retry, a second report of the same thing).
        /// </summary>
        public void Note(Contract c, string key, params string[] args)
        {
            if (!Records(c) || c.IsTerminal || c.awardedTick < 0) return;
            FieldLogEntry e = new FieldLogEntry { tick = ctx.Now, key = key, args = new List<string>(args ?? new string[0]) };
            if (c.fieldLog.Count > 0 && c.fieldLog[c.fieldLog.Count - 1].SameAs(e)) return;
            c.fieldLog.Add(e);
            if (c.fieldLog.Count > MaxEntries) c.fieldLog.RemoveRange(0, c.fieldLog.Count - MaxEntries);
            StateVersion.Bump();
        }

        /// <summary>A beat that is told once per contract, however often its cause recurs (a replanned charter, a reload).</summary>
        public void NoteOnce(Contract c, string key, params string[] args)
        {
            if (c == null) return;
            for (int i = 0; i < c.fieldLog.Count; i++) if (c.fieldLog[i].key == key) return;
            Note(c, key, args);
        }

        /// <summary>What the player may read: the live log of their own running, accepted contract; otherwise nothing.</summary>
        public List<FieldLogEntry> Visible(Contract c)
        {
            if (!Records(c) || c.IsTerminal || c.awardedTick < 0) return None;
            return c.fieldLog;
        }

        /// <summary>The contract closed: the live log ends (History keeps the lasting record).</summary>
        public void Close(Contract c)
        {
            if (c != null && c.fieldLog.Count > 0) c.fieldLog.Clear();
        }
    }
}
