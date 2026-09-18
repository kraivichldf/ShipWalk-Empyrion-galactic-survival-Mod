using System;
using System.IO;

namespace ShipWalk
{
    internal enum RunMode { Off, Diagnostics, Experimental }

    internal sealed class Settings
    {
        public RunMode Mode = RunMode.Diagnostics;
        public bool Trace => false;
        public bool PreserveInterior;
        public bool AllowMovingSeatExit = true;
        public bool PreserveExitMomentum = true;

        public static Settings Load(string path)
        {
            var settings = new Settings();
            if (!File.Exists(path)) return settings;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] pair = line.Split(new[] { '=' }, 2);
                if (pair.Length != 2) throw new FormatException("Expected key=value in ShipWalk.cfg.");
                string key = pair[0].Trim();
                string value = pair[1].Trim();
                switch (key)
                {
                    case "Mode":
                        if (!Enum.TryParse(value, true, out RunMode mode) || !Enum.IsDefined(typeof(RunMode), mode))
                            throw new FormatException("Mode must be Off, Diagnostics or Experimental.");
                        settings.Mode = mode;
                        break;
                    // Accept old configs, but do not restore automatic output.
                    case "Trace": bool.Parse(value); break;
                    case "PreserveInterior": settings.PreserveInterior = bool.Parse(value); break;
                    case "AllowMovingSeatExit": settings.AllowMovingSeatExit = bool.Parse(value); break;
                    case "PreserveExitMomentum": settings.PreserveExitMomentum = bool.Parse(value); break;
                    default: throw new FormatException("Unknown ShipWalk setting: " + key);
                }
            }
            return settings;
        }
    }
}
