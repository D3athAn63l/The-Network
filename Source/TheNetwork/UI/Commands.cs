using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Kernel;

namespace TheNetwork.UI
{
    /// <summary>
    /// The single entry point for player actions (ARCHITECTURE § 9). Each command validates, mutates
    /// through its service, publishes events and returns a reason code. Buttons are enabled from the
    /// matching CanX, which returns the same codes. No Intel command takes a quantity; procurement
    /// asks for an exact one.
    /// </summary>
    public sealed class NetworkCommands
    {
        private readonly NetworkRuntime runtime;

        public NetworkCommands(NetworkRuntime runtime)
        {
            this.runtime = runtime;
        }

        /// <summary>
        /// Every command (and its CanX) first starts the Network if needed. After a failed start-up
        /// nothing may change Network state this session: NetworkStartupFailed.
        /// </summary>
        private bool Blocked(out CommandResult result)
        {
            if (!runtime.EnsureStarted())
            {
                result = CommandResult.Fail(runtime.Session.IsFailed ? "NetworkStartupFailed" : "NetworkStarting");
                return true;
            }
            if (runtime.Inert)
            {
                result = CommandResult.Fail("NetworkPreparedForRemoval");
                return true;
            }
            result = CommandResult.Ok;
            return false;
        }

        public CommandResult CanSubmitIntel(SourceKey source, string defName)
        {
            CommandResult blocked;
            if (Blocked(out blocked)) return blocked;
            return runtime.Ctx.Intel.CanSubmit(source, defName);
        }

        public CommandResult SubmitIntel(SourceKey source, string defName)
        {
            CommandResult blocked;
            if (Blocked(out blocked)) return blocked;
            return runtime.Ctx.Intel.Submit(source, defName);
        }

        public CommandResult CanContinueIntel(IntelRequestId id)
        {
            CommandResult blocked;
            if (Blocked(out blocked)) return blocked;
            return runtime.Ctx.Intel.CanContinue(id);
        }

        public CommandResult ContinueIntel(IntelRequestId id)
        {
            CommandResult blocked;
            if (Blocked(out blocked)) return blocked;
            return runtime.Ctx.Intel.Continue(id);
        }

        public CommandResult CanEndIntel(IntelRequestId id)
        {
            CommandResult blocked;
            if (Blocked(out blocked)) return blocked;
            return runtime.Ctx.Intel.CanEnd(id);
        }

        public CommandResult EndIntel(IntelRequestId id)
        {
            CommandResult blocked;
            if (Blocked(out blocked)) return blocked;
            return runtime.Ctx.Intel.End(id);
        }

        public CommandResult CanCancelIntel(IntelRequestId id)
        {
            CommandResult blocked;
            if (Blocked(out blocked)) return blocked;
            return runtime.Ctx.Intel.CanCancel(id);
        }

        public CommandResult CancelIntel(IntelRequestId id)
        {
            CommandResult blocked;
            if (Blocked(out blocked)) return blocked;
            return runtime.Ctx.Intel.Cancel(id);
        }

        // ------------------------------------------------------------------ procurement (Phase 2)

        private delegate CommandResult Act();

        private CommandResult Run(Act act)
        {
            CommandResult blocked;
            if (Blocked(out blocked)) return blocked;
            return act();
        }

        private Domain.Contracts.ProcurementService P => runtime.Ctx.Procurement;

        public CommandResult CanPostProcurement(ProcurementRequest req) => Run(() => P.CanPost(req));
        public CommandResult PostProcurement(ProcurementRequest req) => Run(() => P.Post(req));
        public CommandResult CanAcceptOffer(OfferId id, bool insure) => Run(() => P.CanAccept(id, insure));
        public CommandResult AcceptOffer(OfferId id, bool insure) => Run(() => P.Accept(id, insure));
        public CommandResult DeclineOffer(OfferId id) => Run(() => P.Decline(id));
        public CommandResult CanCancelContract(ContractId id) => Run(() => P.CanCancel(id));
        public CommandResult CancelContract(ContractId id) => Run(() => P.Cancel(id));
        public CommandResult CanRepost(ContractId id) => Run(() => P.CanRepost(id));
        public CommandResult Repost(ContractId id, bool openToAll) => Run(() => P.Repost(id, openToAll));
        public CommandResult CanRespondWorse(ContractId id, WorseChoice choice) => Run(() => P.CanRespondWorse(id, choice));
        public CommandResult RespondWorse(ContractId id, WorseChoice choice) => Run(() => P.RespondWorse(id, choice));
        public CommandResult CanRespondPartial(ContractId id, PartialChoice choice) => Run(() => P.CanRespondPartial(id, choice));
        public CommandResult RespondPartial(ContractId id, PartialChoice choice) => Run(() => P.RespondPartial(id, choice));
        public CommandResult CanPayBalance(ContractId id) => Run(() => P.CanPayBalance(id));
        public CommandResult PayBalance(ContractId id) => Run(() => P.PayBalance(id));

        /// <summary>A catalog preference (ModSettings), not world data; applies to every save.</summary>
        public void SetItemOverride(string defName, ItemOverride value)
        {
            NetworkSettings s = NetworkMod.Settings;
            if (s == null) return;
            s.SetOverride(defName, value);
            NetworkMod.Instance?.WriteSettings();
        }

        public void SetShowUnusual(bool show)
        {
            NetworkSettings s = NetworkMod.Settings;
            if (s == null || s.showUnusualItems == show) return;
            s.showUnusualItems = show;
            NetworkMod.Instance?.WriteSettings();
            StateVersion.Bump();
        }
    }
}
