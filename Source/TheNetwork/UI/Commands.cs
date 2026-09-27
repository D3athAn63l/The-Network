using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Intel;
using TheNetwork.Kernel;

namespace TheNetwork.UI
{
    /// <summary>
    /// The single entry point for player actions (ARCHITECTURE § 9). Each command validates, mutates
    /// through its service, publishes events and returns a reason code. Buttons are enabled from the
    /// matching CanX, which returns the same codes. No command takes a quantity.
    /// </summary>
    public sealed class NetworkCommands
    {
        private readonly NetworkRuntime runtime;

        public NetworkCommands(NetworkRuntime runtime)
        {
            this.runtime = runtime;
        }

        private CommandResult Inert()
        {
            return CommandResult.Fail("NetworkPreparedForRemoval");
        }

        public CommandResult CanSubmitIntel(SourceKey source, string defName)
        {
            if (runtime.Inert) return Inert();
            return runtime.Ctx.Intel.CanSubmit(source, defName);
        }

        public CommandResult SubmitIntel(SourceKey source, string defName)
        {
            if (runtime.Inert) return Inert();
            return runtime.Ctx.Intel.Submit(source, defName);
        }

        public CommandResult CanContinueIntel(IntelRequestId id)
        {
            if (runtime.Inert) return Inert();
            return runtime.Ctx.Intel.CanContinue(id);
        }

        public CommandResult ContinueIntel(IntelRequestId id)
        {
            if (runtime.Inert) return Inert();
            return runtime.Ctx.Intel.Continue(id);
        }

        public CommandResult CanEndIntel(IntelRequestId id)
        {
            if (runtime.Inert) return Inert();
            return runtime.Ctx.Intel.CanEnd(id);
        }

        public CommandResult EndIntel(IntelRequestId id)
        {
            if (runtime.Inert) return Inert();
            return runtime.Ctx.Intel.End(id);
        }

        public CommandResult CanCancelIntel(IntelRequestId id)
        {
            if (runtime.Inert) return Inert();
            return runtime.Ctx.Intel.CanCancel(id);
        }

        public CommandResult CancelIntel(IntelRequestId id)
        {
            if (runtime.Inert) return Inert();
            return runtime.Ctx.Intel.Cancel(id);
        }

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
