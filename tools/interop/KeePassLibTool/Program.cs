// SPDX-License-Identifier: GPL-3.0-or-later
// Test-only interop probe: opens a KDBX with the official KeePassLib and prints
// a canonical structural hash + counts. Never prints field values or secrets.
// KeePassLib is GPL-2.0-or-later and this tool is not part of the app.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KeePassLib;
using KeePassLib.Interfaces;
using KeePassLib.Keys;
using KeePassLib.Security;
using KeePassLib.Serialization;

namespace KeePassLibTool
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("{\"status\":\"error\",\"kind\":\"" + JsonEscape(ex.GetType().Name) +
                                  "\",\"message\":\"" + JsonEscape(ex.Message) + "\"}");
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            if (args.Length < 1 || args[0] == "--help" || args[0] == "-h")
            {
                Console.WriteLine("usage: KeePassLibTool <db.kdbx> --password-env VAR");
                return args.Length < 1 ? 2 : 0;
            }

            string path = args[0];
            string password = null;
            bool allowEmptyStandardFields = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--password-env" && i + 1 < args.Length)
                {
                    password = Environment.GetEnvironmentVariable(args[++i]);
                }
                else if (args[i] == "--password" && i + 1 < args.Length)
                {
                    password = args[++i];
                }
                else if (args[i] == "--allow-empty-standard-fields")
                {
                    allowEmptyStandardFields = true;
                }
                else
                {
                    Console.WriteLine("{\"status\":\"error\",\"kind\":\"args\",\"message\":\"unknown argument\"}");
                    return 2;
                }
            }
            if (string.IsNullOrEmpty(password))
            {
                Console.WriteLine("{\"status\":\"error\",\"kind\":\"args\",\"message\":\"missing password\"}");
                return 2;
            }

            var db = new PwDatabase();
            var key = new CompositeKey();
            key.AddUserKey(new KcpPassword(password));
            db.Open(IOConnectionInfo.FromPath(path), key, new NullStatusLogger());

            var hash = new CanonicalHash();
            var stats = new Stats { KdbxVersion = DetectVersion(path) };

            hash.Add("META"); hash.Add("name"); hash.Add(db.Name);
            hash.Add("desc"); hash.Add(db.Description);
            hash.Add("recycle_enabled"); hash.Add(db.RecycleBinEnabled ? "1" : "0");
            hash.Add("recycle_uuid"); hash.Add(db.RecycleBinUuid != null ? db.RecycleBinUuid.ToHexString() : "");

            WalkGroup(db.RootGroup, "", hash, stats, true, allowEmptyStandardFields);

            foreach (PwDeletedObject deleted in db.DeletedObjects)
            {
                hash.Add("DEL");
                hash.Add(deleted.Uuid.ToHexString());
                hash.Add(deleted.DeletionTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
                stats.Deleted++;
            }

            stats.Sha256 = hash.Finish();
            Console.WriteLine(stats.ToJson());
            return 0;
        }

        private static void WalkGroup(PwGroup group, string parentUuid, CanonicalHash hash, Stats stats,
                                      bool count, bool allowEmptyStandardFields)
        {
            if (count) stats.Groups++;
            hash.Add("G");
            hash.Add(group.Uuid.ToHexString());
            hash.Add(parentUuid);
            hash.Add(group.Name);
            hash.Add(group.Notes);
            foreach (string tag in group.Tags.OrderBy(x => x, StringComparer.Ordinal))
            {
                hash.Add("GT");
                hash.Add(tag);
            }
            foreach (PwEntry entry in group.Entries)
            {
                WalkEntry(entry, hash, stats, count, allowEmptyStandardFields);
            }
            foreach (PwGroup subgroup in group.Groups)
            {
                WalkGroup(subgroup, group.Uuid.ToHexString(), hash, stats, count, allowEmptyStandardFields);
            }
        }

        private static void WalkEntry(PwEntry entry, CanonicalHash hash, Stats stats, bool count,
                                      bool allowEmptyStandardFields)
        {
            if (count) stats.Entries++;
            hash.Add("E");
            hash.Add(entry.Uuid.ToHexString());
            hash.Add(entry.ParentGroup != null ? entry.ParentGroup.Uuid.ToHexString() : "");

            bool hasTotp = false;
            foreach (KeyValuePair<string, ProtectedString> field in
                     entry.Strings.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                string value = field.Value.ReadString();
                // Accepted deviation: some backends materialise empty standard
                // fields (Notes/URL/UserName) for legacy entries. They are
                // semantically absent; optionally ignore them in the hash.
                if (allowEmptyStandardFields && IsEmptyStandardField(field.Key, value)) continue;
                hash.Add("S");
                hash.Add(field.Key);
                hash.Add(field.Value.IsProtected ? "1" : "0");
                hash.Add(value);
                if (IsTotpField(field.Key)) hasTotp = true;
            }
            if (count && hasTotp) stats.TotpEntries++;

            foreach (KeyValuePair<string, ProtectedBinary> binary in
                     entry.Binaries.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                byte[] data = binary.Value.ReadData();
                hash.Add("B");
                hash.Add(binary.Key);
                hash.AddBytes(data ?? new byte[0]);
                if (count) stats.Attachments++;
            }

            foreach (string tag in entry.Tags.OrderBy(x => x, StringComparer.Ordinal))
            {
                hash.Add("T");
                hash.Add(tag);
            }

            if (count) stats.HistoryEntries += (int)entry.History.UCount;
            foreach (PwEntry historical in entry.History)
            {
                WalkEntry(historical, hash, stats, false, allowEmptyStandardFields);
            }
        }

        private static bool IsEmptyStandardField(string name, string value)
        {
            return value.Length == 0 && (name == "Notes" || name == "URL" || name == "UserName");
        }

        private static bool IsTotpField(string name)
        {
            return name == "otp"
                   || name.StartsWith("TimeOtp-", StringComparison.OrdinalIgnoreCase)
                   || name == "TOTP Seed"
                   || name == "TOTP Settings";
        }

        private static string DetectVersion(string path)
        {
            byte[] header = new byte[12];
            using (FileStream stream = File.OpenRead(path))
            {
                if (stream.Read(header, 0, header.Length) < header.Length) return "unknown";
            }
            uint sig1 = BitConverter.ToUInt32(header, 0);
            uint sig2 = BitConverter.ToUInt32(header, 4);
            uint version = BitConverter.ToUInt32(header, 8);
            if (sig1 != 0x9AA2D903 || sig2 != 0xB54BFB67) return "unknown";
            switch (version)
            {
                case 0x00030001: return "3.1";
                case 0x00040000: return "4.0";
                case 0x00040001: return "4.1";
                default: return "0x" + version.ToString("X8");
            }
        }

        private static string JsonEscape(string value)
        {
            if (value == null) return "";
            var builder = new StringBuilder();
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') builder.Append('\\').Append(c);
                else if (c == '\n') builder.Append("\\n");
                else if (c == '\r') builder.Append("\\r");
                else if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("x4"));
                else builder.Append(c);
            }
            return builder.ToString();
        }

        private sealed class Stats
        {
            public int Groups;
            public int Entries;
            public int Deleted;
            public int Attachments;
            public int HistoryEntries;
            public int TotpEntries;
            public string KdbxVersion;
            public string Sha256;

            public string ToJson()
            {
                return "{\"status\":\"ok\""
                       + ",\"kdbx\":\"" + KdbxVersion + "\""
                       + ",\"groups\":" + Groups
                       + ",\"entries\":" + Entries
                       + ",\"deleted_objects\":" + Deleted
                       + ",\"attachments\":" + Attachments
                       + ",\"history_entries\":" + HistoryEntries
                       + ",\"totp_entries\":" + TotpEntries
                       + ",\"canonical_sha256\":\"" + Sha256 + "\"}";
            }
        }

        private sealed class CanonicalHash
        {
            private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            public void Add(string value)
            {
                if (value == null)
                {
                    AddInt(-1);
                    return;
                }
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                AddInt(bytes.Length);
                _hash.AppendData(bytes);
            }

            public void AddBytes(byte[] bytes)
            {
                if (bytes == null) bytes = new byte[0];
                AddInt(bytes.Length);
                _hash.AppendData(bytes);
            }

            private void AddInt(int value)
            {
                _hash.AppendData(new[]
                {
                    (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
                });
            }

            public string Finish()
            {
                byte[] digest = _hash.GetHashAndReset();
                var builder = new StringBuilder(digest.Length * 2);
                foreach (byte value in digest) builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
