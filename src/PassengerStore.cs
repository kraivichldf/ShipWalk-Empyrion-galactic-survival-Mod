using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ShipWalk
{
    // Functional save data, confined to the selected saved world. No diagnostic output.
    internal sealed class PassengerStore
    {
        private const int Magic = 0x31505753, Limit = 10000, MaxBytes = 8 * 1024 * 1024;
        private readonly string path;
        private Dictionary<string, PassengerRecord> records = new Dictionary<string, PassengerRecord>(StringComparer.Ordinal);
        private bool dirty;
        public int Count => records.Count;
        public string Error { get; private set; }
        public PassengerStore(string saveFolder)
        {
            if (string.IsNullOrWhiteSpace(saveFolder) || !Directory.Exists(saveFolder)) throw new IOException("The server save folder is not ready.");
            path = Path.Combine(Path.GetFullPath(saveFolder), "ShipWalk", "passengers-v1.bin");
            if (!File.Exists(path) && !File.Exists(path + ".bak")) return;
            if (Load(path, out var loaded) || Load(path + ".bak", out loaded)) { records = loaded; return; }
            // Never overwrite evidence of a corrupt save with an empty registry.
            Error = "Passenger save and backup are unreadable; reconnect persistence is disabled.";
        }
        public PassengerRecord Find(string identity, int actor)
            => Error == null && records.TryGetValue(identity, out var p) && p.Actor == actor ? p.Copy() : null;
        public void Put(PassengerRecord p)
        {
            if (Error != null || p == null || !p.Valid) return;
            if (records.Count >= Limit && !records.ContainsKey(p.Identity)) throw new IOException("Passenger save capacity reached.");
            records[p.Identity] = p.Copy(); dirty = true;
        }
        public void Remove(string identity) { if (Error == null) dirty |= records.Remove(identity); }
        public void Flush()
        {
            if (!dirty || Error != null) return;
            byte[] payload;
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                w.Write(Magic); w.Write(records.Count);
                foreach (var item in records.OrderBy(p => p.Key, StringComparer.Ordinal)) ReconnectProtocol.WriteRecord(w, item.Value);
                w.Flush(); payload = stream.ToArray();
            }
            if (payload.Length + 32 > MaxBytes) throw new IOException("Passenger save exceeds its supported size.");
            byte[] digest; using (var sha = SHA256.Create()) digest = sha.ComputeHash(payload);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(payload, 0, payload.Length); file.Write(digest, 0, digest.Length); file.Flush(true); }
            if (File.Exists(path)) File.Replace(path + ".tmp", path, path + ".bak"); else File.Move(path + ".tmp", path);
            dirty = false;
        }
        private static bool Load(string file, out Dictionary<string, PassengerRecord> loaded)
        {
            loaded = null;
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > MaxBytes) return false;
                byte[] bytes = File.ReadAllBytes(file); if (bytes.Length < 40) return false;
                using (var sha = SHA256.Create())
                    if (!sha.ComputeHash(bytes, 0, bytes.Length - 32).SequenceEqual(bytes.Skip(bytes.Length - 32))) return false;
                var result = new Dictionary<string, PassengerRecord>(StringComparer.Ordinal);
                using (var r = new BinaryReader(new MemoryStream(bytes, 0, bytes.Length - 32, false), Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) return false;
                    int count = r.ReadInt32(); if (count < 0 || count > Limit) return false;
                    for (int i = 0; i < count; i++)
                    {
                        PassengerRecord p = ReconnectProtocol.ReadRecord(r);
                        if (!p.Valid || result.ContainsKey(p.Identity)) return false;
                        result.Add(p.Identity, p);
                    }
                    if (r.BaseStream.Position != r.BaseStream.Length) return false;
                }
                loaded = result; return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { return false; }
        }
    }
}
