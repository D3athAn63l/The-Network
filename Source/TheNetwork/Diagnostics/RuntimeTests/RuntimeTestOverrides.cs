using System;
using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Kernel;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    /// <summary>
    /// Every process-wide static a runtime test can influence, captured and restored EXACTLY (never reset blindly: the owner may
    /// already have the comms override or the fee waiver on). The runner wraps every step: capture, set the neutral state a
    /// scenario expects, run, detect a leak, restore. So no override a test sets is ever visible to the real game, not even for
    /// a frame, and a test that forgets to clear one is caught and reported (RT-INFRA-002).
    /// </summary>
    public sealed class RuntimeOverrideSnapshot
    {
        // ProcurementDevOverrides
        public Domain.Operations.OutcomeBand? forceBand;
        public int? forceSecured;
        public int? forceDelayTicks;
        public int forceDeliveryFailures;
        public string forceTroubled;
        public bool forceWorseThanExpected;
        public bool forceFollowUp;
        public bool forceNewcomer;
        public bool forceNotTroubled;
        public bool? forceTroubledFound;

        // IntelDevOverrides
        public bool forceLead;
        public bool forceNoLead;
        public LeadDivergence? forceDivergence;
        public SourceKind? forceSourceKind;
        public bool commsGateOverride;
        public bool waiveFees;

        // ServiceToggles (a scenario needs the services on, whatever the owner's settings say)
        public bool intelEnabled;
        public bool procurementEnabled;

        public static RuntimeOverrideSnapshot Capture()
        {
            return new RuntimeOverrideSnapshot
            {
                forceBand = ProcurementDevOverrides.forceBand,
                forceSecured = ProcurementDevOverrides.forceSecured,
                forceDelayTicks = ProcurementDevOverrides.forceDelayTicks,
                forceDeliveryFailures = ProcurementDevOverrides.forceDeliveryFailures,
                forceTroubled = ProcurementDevOverrides.forceTroubled,
                forceWorseThanExpected = ProcurementDevOverrides.forceWorseThanExpected,
                forceFollowUp = ProcurementDevOverrides.forceFollowUp,
                forceNewcomer = ProcurementDevOverrides.forceNewcomer,
                forceNotTroubled = ProcurementDevOverrides.forceNotTroubled,
                forceTroubledFound = ProcurementDevOverrides.forceTroubledFound,
                forceLead = IntelDevOverrides.forceLead,
                forceNoLead = IntelDevOverrides.forceNoLead,
                forceDivergence = IntelDevOverrides.forceDivergence,
                forceSourceKind = IntelDevOverrides.forceSourceKind,
                commsGateOverride = IntelDevOverrides.commsGateOverride,
                waiveFees = IntelDevOverrides.waiveFees,
                intelEnabled = ServiceToggles.IntelEnabled,
                procurementEnabled = ServiceToggles.ProcurementEnabled
            };
        }

        /// <summary>The state every scenario starts from: no forced draw, no waiver, both services on.</summary>
        public static RuntimeOverrideSnapshot Neutral()
        {
            return new RuntimeOverrideSnapshot { intelEnabled = true, procurementEnabled = true };
        }

        /// <summary>Puts every captured value back, exactly.</summary>
        public void Restore()
        {
            ProcurementDevOverrides.forceBand = forceBand;
            ProcurementDevOverrides.forceSecured = forceSecured;
            ProcurementDevOverrides.forceDelayTicks = forceDelayTicks;
            ProcurementDevOverrides.forceDeliveryFailures = forceDeliveryFailures;
            ProcurementDevOverrides.forceTroubled = forceTroubled;
            ProcurementDevOverrides.forceWorseThanExpected = forceWorseThanExpected;
            ProcurementDevOverrides.forceFollowUp = forceFollowUp;
            ProcurementDevOverrides.forceNewcomer = forceNewcomer;
            ProcurementDevOverrides.forceNotTroubled = forceNotTroubled;
            ProcurementDevOverrides.forceTroubledFound = forceTroubledFound;
            IntelDevOverrides.forceLead = forceLead;
            IntelDevOverrides.forceNoLead = forceNoLead;
            IntelDevOverrides.forceDivergence = forceDivergence;
            IntelDevOverrides.forceSourceKind = forceSourceKind;
            IntelDevOverrides.commsGateOverride = commsGateOverride;
            IntelDevOverrides.waiveFees = waiveFees;
            ServiceToggles.IntelEnabled = intelEnabled;
            ServiceToggles.ProcurementEnabled = procurementEnabled;
        }

        /// <summary>The names of every value that differs, "name: this -> other" (empty when equal).</summary>
        public List<string> Diff(RuntimeOverrideSnapshot o)
        {
            List<string> d = new List<string>();
            D(d, "ProcurementDevOverrides.forceBand", forceBand, o.forceBand);
            D(d, "ProcurementDevOverrides.forceSecured", forceSecured, o.forceSecured);
            D(d, "ProcurementDevOverrides.forceDelayTicks", forceDelayTicks, o.forceDelayTicks);
            D(d, "ProcurementDevOverrides.forceDeliveryFailures", forceDeliveryFailures, o.forceDeliveryFailures);
            D(d, "ProcurementDevOverrides.forceTroubled", forceTroubled, o.forceTroubled);
            D(d, "ProcurementDevOverrides.forceWorseThanExpected", forceWorseThanExpected, o.forceWorseThanExpected);
            D(d, "ProcurementDevOverrides.forceFollowUp", forceFollowUp, o.forceFollowUp);
            D(d, "ProcurementDevOverrides.forceNewcomer", forceNewcomer, o.forceNewcomer);
            D(d, "ProcurementDevOverrides.forceNotTroubled", forceNotTroubled, o.forceNotTroubled);
            D(d, "ProcurementDevOverrides.forceTroubledFound", forceTroubledFound, o.forceTroubledFound);
            D(d, "IntelDevOverrides.forceLead", forceLead, o.forceLead);
            D(d, "IntelDevOverrides.forceNoLead", forceNoLead, o.forceNoLead);
            D(d, "IntelDevOverrides.forceDivergence", forceDivergence, o.forceDivergence);
            D(d, "IntelDevOverrides.forceSourceKind", forceSourceKind, o.forceSourceKind);
            D(d, "IntelDevOverrides.commsGateOverride", commsGateOverride, o.commsGateOverride);
            D(d, "IntelDevOverrides.waiveFees", waiveFees, o.waiveFees);
            D(d, "ServiceToggles.IntelEnabled", intelEnabled, o.intelEnabled);
            D(d, "ServiceToggles.ProcurementEnabled", procurementEnabled, o.procurementEnabled);
            return d;
        }

        private static void D<T>(List<string> list, string name, T a, T b)
        {
            if (!EqualityComparer<T>.Default.Equals(a, b)) list.Add(name + ": " + (a == null ? "null" : a.ToString()) + " -> " + (b == null ? "null" : b.ToString()));
        }
    }

    /// <summary>The scratch world's log lines, kept out of the real log (and the real once-per-session warning memory).</summary>
    public sealed class RuntimeLogCapture
    {
        public readonly List<string> Lines = new List<string>();
        public int Warnings;
        public int Errors;
        private Action<NetLogLevel, string> previousSink;
        private string[] previousOnceKeys;
        private bool active;

        public void Begin()
        {
            if (active) return;
            active = true;
            previousSink = NetLog.Sink;
            previousOnceKeys = NetLog.SnapshotOnceKeys();
            NetLog.Sink = Capture;
        }

        public void End()
        {
            if (!active) return;
            active = false;
            NetLog.Sink = previousSink;
            NetLog.RestoreOnceKeys(previousOnceKeys);
            previousSink = null;
            previousOnceKeys = null;
        }

        private void Capture(NetLogLevel level, string line)
        {
            if (level == NetLogLevel.Warning) Warnings++;
            else if (level == NetLogLevel.Error) Errors++;
            if (Lines.Count < 200) Lines.Add(level + " " + line);
        }
    }
}
