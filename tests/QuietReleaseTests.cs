using System;
using System.Collections.Generic;
using System.IO;
using ShipWalk;

internal static class QuietReleaseTests
{
    public static void CommandOnlyOutput()
    {
        var output = new List<string>();
        using (var log = new TraceLog(output.Add))
        {
            int evaluated = 0;
            Func<string> expensive = () => { evaluated++; return "automatic diagnostic"; };
            log.Info(expensive());
            typeof(TraceLog).GetMethod("Info").Invoke(log, new object[] { "automatic message" });
            log.Notice("Ready"); log.Error("Saved failure"); log.Flush();
            if (output.Count != 0 || evaluated != 0 || log.LastNotice != "Ready" || log.LastError != "Saved failure")
                throw new Exception("Automatic output escaped or diagnostics were evaluated.");
            log.Reply("explicit status");
            if (output.Count != 1 || output[0] != "explicit status") throw new Exception("Manual reply was lost.");
        }
    }
    public static void LegacyTraceConfig()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "Trace=true\nAllowMovingSeatExit=true\nPreserveExitMomentum=true");
            Settings settings = Settings.Load(path);
            if (settings.Trace || !settings.AllowMovingSeatExit || !settings.PreserveExitMomentum)
                throw new Exception("Legacy tracing reenabled or movement configuration changed.");
        }
        finally { File.Delete(path); }
    }
    public static void NoTraceFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ShipWalk-quiet-" + Guid.NewGuid());
        using (var log = (TraceLog)Activator.CreateInstance(typeof(TraceLog), new object[] { null, folder }))
        {
            log.Flush();
            if (log.Path != null || Directory.Exists(folder)) throw new Exception("Release logger created a trace path.");
        }
    }
}
