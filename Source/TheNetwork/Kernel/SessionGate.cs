using System;

namespace TheNetwork.Kernel
{
    public enum NetworkSession : byte
    {
        NotStarted = 0,
        Starting = 1,
        Running = 2,
        Failed = 3
    }

    /// <summary>
    /// The start-up state of one play session (runtime only, never saved). The Network starts on first
    /// use: the first tick, site callback, signal or command. A start-up that throws leaves it
    /// <see cref="NetworkSession.Failed"/> for the rest of the session: the scheduler does not run, site
    /// callbacks and signals are ignored, commands are refused. Saved Network data is left exactly as
    /// loaded, and start-up is tried again on the next load. The error is logged once.
    /// </summary>
    public sealed class SessionGate
    {
        private string stage = "start";

        public NetworkSession State { get; private set; }
        public string FailedStage { get; private set; }
        public string FailureMessage { get; private set; }

        /// <summary>How many times start-up has run this session (at most once).</summary>
        public int Attempts { get; private set; }

        public bool IsRunning => State == NetworkSession.Running;
        public bool IsFailed => State == NetworkSession.Failed;

        /// <summary>Names the start-up step now running, for the failure report.</summary>
        public void Stage(string name)
        {
            stage = name;
        }

        /// <summary>
        /// Runs <paramref name="startup"/> the first time it is called and returns whether the Network is
        /// running. A call made while start-up is still running (re-entrant) or after it failed returns
        /// false and runs nothing.
        /// </summary>
        public bool Ensure(Action<SessionGate> startup)
        {
            switch (State)
            {
                case NetworkSession.Running:
                    return true;
                case NetworkSession.NotStarted:
                    break;
                default:
                    return false;
            }
            State = NetworkSession.Starting;
            Attempts++;
            stage = "start";
            try
            {
                startup(this);
                State = NetworkSession.Running;
                StateVersion.Bump();
                return true;
            }
            catch (Exception ex)
            {
                State = NetworkSession.Failed;
                FailedStage = stage;
                FailureMessage = ex.GetType().Name + ": " + NetScribe.Truncate(ex.Message, 200);
                NetLog.Error(LogCategory.Kernel, "Start-up failed during " + stage + ". The Network is inactive for the rest of this session: "
                    + "no simulation, no site callbacks, no commands. Saved Network data is kept as loaded; start-up is tried again on the next load. " + ex);
                StateVersion.Bump();
                return false;
            }
        }
    }
}
