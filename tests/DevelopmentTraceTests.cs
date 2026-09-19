using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ShipWalk;

internal static class DevelopmentTraceTests
{
    public static void AutomaticOutput()
    {
        var output = new List<string>();
        using (var log = new TraceLog(output.Add))
        {
            log.Info("sample"); log.Notice("Ready"); log.Error("failure"); log.Reply("status");
            if (output.Count != 4 || output[0] != "sample" || output[3] != "status"
                || log.LastNotice != "Ready" || log.LastError != "failure")
                throw new Exception("Development output or retained status was lost.");
        }
    }
    public static void TraceConfig()
    {
        string path = Path.GetTempFileName();
        try
        {
            foreach (bool enabled in new[] { true, false })
            {
                File.WriteAllText(path, "Trace=" + enabled + "\nAllowMovingSeatExit=true\nPreserveExitMomentum=true");
                Settings settings = Settings.Load(path);
                if (settings.Trace != enabled || !settings.AllowMovingSeatExit || !settings.PreserveExitMomentum)
                    throw new Exception("Trace option changed movement configuration.");
            }
        }
        finally { File.Delete(path); }
    }
    public static void BoundedCsv()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ShipWalk-development-" + Guid.NewGuid().ToString("N"));
        string path = null;
        try
        {
            using (var log = (TraceLog)Activator.CreateInstance(typeof(TraceLog), new object[] { null, folder }))
            {
                path = log.Path;
                var write = typeof(TraceLog).GetMethod("Write", BindingFlags.Instance | BindingFlags.NonPublic);
                string row = new string('x', 1024 * 1024);
                for (int i = 0; i < 12; i++) write.Invoke(log, new object[] { row, true });
                log.Flush();
                long bytes = new FileInfo(path).Length;
                if (bytes < 1024 * 1024 || bytes > TraceBudget.MaxBytes)
                    throw new Exception("Trace file was empty or exceeded its session budget.");
            }
        }
        finally { if (path != null) File.Delete(path); if (Directory.Exists(folder)) Directory.Delete(folder); }
    }
}
