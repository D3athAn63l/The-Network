using System;
using System.Collections.Generic;
using Verse;

namespace TheNetwork.Kernel
{
    public enum LogCategory
    {
        Kernel,
        Scheduler,
        Events,
        History,
        Actors,
        Cast,
        Settings,
        Intel,
        Opportunities,
        Catalog,
        Sites,
        Payment,
        Comms,
        Compat,
        Save,
        UI,
        Dev,
        Contracts,
        Operations,
        Delivery,
        Spatial
    }

    public enum NetLogLevel { Verbose, Info, Warning, Error }

    /// <summary>
    /// The only logging entry point of The Network (DEBUGGING § 2).
    ///
    /// Every line is prefixed "[TheNetwork][Category]". Errors and warnings can be issued once per key
    /// per session so a recurring problem never spams the log. Verbose tracing costs one bool check when
    /// disabled: callers guard message formatting with <see cref="Verbose"/>.
    /// </summary>
    public static class NetLog
    {
        public const string Prefix = "[TheNetwork]";

        /// <summary>Toggled by Mod Settings ("Detailed Network logging").</summary>
        public static bool VerboseEnabled;

        /// <summary>
        /// Headless tests replace the sink so nothing reaches Verse.Log (which needs the Unity player).
        /// </summary>
        public static Action<NetLogLevel, string> Sink;

        private static readonly HashSet<string> onceKeys = new HashSet<string>();

        public static bool Verbose(LogCategory category)
        {
            return VerboseEnabled;
        }

        public static string Format(LogCategory category, string message)
        {
            return Prefix + "[" + category + "] " + message;
        }

        public static void Trace(LogCategory category, string message)
        {
            if (!VerboseEnabled) return;
            Write(NetLogLevel.Verbose, Format(category, message));
        }

        public static void Info(LogCategory category, string message)
        {
            Write(NetLogLevel.Info, Format(category, message));
        }

        public static void Warn(LogCategory category, string message)
        {
            Write(NetLogLevel.Warning, Format(category, message));
        }

        public static void Error(LogCategory category, string message)
        {
            Write(NetLogLevel.Error, Format(category, message));
        }

        /// <summary>Logs an error at most once per (category, key) for this session.</summary>
        public static void ErrorOnce(LogCategory category, string key, string message)
        {
            if (onceKeys.Add("E|" + category + "|" + key)) Error(category, message);
        }

        /// <summary>Logs a warning at most once per (category, key) for this session.</summary>
        public static void WarnOnce(LogCategory category, string key, string message)
        {
            if (onceKeys.Add("W|" + category + "|" + key)) Warn(category, message);
        }

        /// <summary>Clears the once-per-session memory. Tests only.</summary>
        public static void ResetOnceKeys()
        {
            onceKeys.Clear();
        }

        private static void Write(NetLogLevel level, string line)
        {
            Action<NetLogLevel, string> sink = Sink;
            if (sink != null)
            {
                sink(level, line);
                return;
            }
            switch (level)
            {
                case NetLogLevel.Error:
                    Log.Error(line);
                    break;
                case NetLogLevel.Warning:
                    Log.Warning(line);
                    break;
                default:
                    Log.Message(line);
                    break;
            }
        }
    }
}
