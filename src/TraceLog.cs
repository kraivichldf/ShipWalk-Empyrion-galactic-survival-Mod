using System;
using System.Diagnostics;
using Eleon.Modding;
using UnityEngine;

namespace ShipWalk
{
    // Release output is command-driven. No automatic game log, popup or file IO.
    internal sealed class TraceLog : IDisposable
    {
        private readonly Action<string> reply;
        public string Path => null;
        public string LastNotice { get; private set; }
        public string LastError { get; private set; }

        public TraceLog(IModApi api, string folder) : this(message => api.Log("[ShipWalk] " + message)) { }
        internal TraceLog(Action<string> reply) { this.reply = reply; }

        // Retain the diagnostic signatures while removing their argument
        // evaluation from normal builds. These methods have no output sink.
        [Conditional("SHIPWALK_DIAGNOSTICS")]
        public void Info(string message) { }
        [Conditional("SHIPWALK_DIAGNOSTICS")]
        public void Event(string kind, Settings settings, Passenger state, Vector3 before, Vector3 after, string detail = "") { }

        public void Notice(string message) => LastNotice = message;
        public void Error(string message) => LastError = message;
        public void Reply(string message) => reply(message);
        public void Flush() { }
        public void Dispose() { }
    }
}
