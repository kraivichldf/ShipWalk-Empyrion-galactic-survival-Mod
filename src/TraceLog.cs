using System;
using System.Globalization;
using System.IO;
using System.Text;
using Eleon.Modding;
using UnityEngine;

namespace ShipWalk
{
    internal sealed class TraceLog : IDisposable
    {
        private readonly Action<string> reply;
        private StreamWriter writer;
        private readonly TraceBudget budget = new TraceBudget();
        private bool routineLimitReported, totalLimitReported;
        public string Path { get; }
        public string LastNotice { get; private set; }
        public string LastError { get; private set; }

        internal TraceLog(Action<string> reply) { this.reply = reply; }
        public TraceLog(IModApi api, string folder) : this(message => api?.Log("[ShipWalk] " + message))
        {
            Directory.CreateDirectory(folder);
            Path = System.IO.Path.Combine(folder, "shipwalk-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")
                + "-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".csv");
            writer = new StreamWriter(Path, false, new UTF8Encoding(false)) { AutoFlush = false };
            Write("utc,fixed_time,event,mode,ship_id,grounded,collider_layer,collider_enabled,player_position,ship_local_position,before_velocity,after_velocity,support_velocity,phase,frame_velocity,relative_drag_active,detail", true);
        }

        public void Info(string message) => reply(message);
        public void Notice(string message) { LastNotice = message; Info(message); }
        public void Error(string message) { LastError = message; Info("Error: " + message); }
        public void Reply(string message) => reply(message);

        public void Event(string kind, Settings settings, Passenger state, Vector3 before, Vector3 after, string detail = "")
        {
            if (!settings.Trace || writer == null) return;
            bool critical = kind.StartsWith("exit-", StringComparison.Ordinal) || kind == "detach"
                || kind == "landing" || kind == "seat-exit-inertia";
            if (!critical && !budget.Sample(kind, Time.fixedTime)) return;
            string layer = state?.Floor == null ? "" : LayerMask.LayerToName(state.Floor.gameObject.layer);
            string enabled = state?.Floor == null ? "" : (state.Floor.enabled && state.Floor.gameObject.activeInHierarchy).ToString();
            Vector3 position = state?.Body == null ? Vector3.zero : state.Body.position;
            Vector3 local = state?.SupportTransform == null ? Vector3.zero : state.SupportTransform.InverseTransformPoint(position);
            if (state?.InteriorBody != null)
                local = Quaternion.Inverse(state.InteriorBody.rotation) * (position - state.InteriorBody.position);
            Write(string.Join(",", Quote(DateTime.UtcNow.ToString("O")), Time.fixedTime.ToString("F4", CultureInfo.InvariantCulture),
                Quote(kind), settings.Mode.ToString(), (state?.ShipId ?? -1).ToString(CultureInfo.InvariantCulture),
                (state?.Grounded ?? false).ToString(), Quote(layer), enabled, Quote(V(position)), Quote(V(local)),
                Quote(V(before)), Quote(V(after)), Quote(V(state?.SupportVelocity ?? Vector3.zero)),
                (state?.Frame.Phase ?? PassengerPhase.Detached).ToString(),
                Quote(state == null ? "" : string.Format(CultureInfo.InvariantCulture, "{0:F5} {1:F5} {2:F5}",
                    state.Frame.Velocity.X, state.Frame.Velocity.Y, state.Frame.Velocity.Z)),
                (state?.Drag.Active ?? false).ToString(), Quote(detail)), critical);
        }

        private static string V(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "{0:F5} {1:F5} {2:F5}", v.x, v.y, v.z);
        private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        private void Write(string line, bool critical)
        {
            if (writer == null) return;
            if (!budget.Take(Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine), critical))
            {
                if (critical && !totalLimitReported)
                {
                    totalLimitReported = true;
                    Info("Trace reached its 8 MiB budget; additional rows may be omitted for this session.");
                }
                else if (!critical && !routineLimitReported)
                {
                    routineLimitReported = true;
                    Info("Routine trace budget reached; reserved exit/support diagnostics continue within the 8 MiB session cap.");
                }
                return;
            }
            writer.WriteLine(line);
        }
        public void Flush() => writer?.Flush();
        public void Dispose() { writer?.Dispose(); writer = null; }
    }
}
