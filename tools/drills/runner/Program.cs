// SPDX-License-Identifier: GPL-3.0-or-later
// Drill driver for the M4-07 partial drill automation.
//
// Sub-commands (all headless; no WinUI):
//   restore            --fixture <kdbx> --workdir <dir>
//   protocol           --url <mock-webdav-file-url>
//   offline-prepare    --url <file-url> --fixture <kdbx> --workdir <dir>
//   offline-unreachable --workdir <dir>
//   offline-replay     --workdir <dir>
//   conflict           --url <file-url> --fixture <kdbx> --workdir <dir>
//
// The driver uses the real application services (Keepassity.Core) and the real
// native core through the FFI v1 C ABI, so edits/saves/sync decisions/merges are
// the product code paths. Only the WinUI conflict dialog and its conflict-copy
// file naming remain UI/manual (documented in tools/drills/README.md and
// the project process documentation).

using System.Security.Cryptography;
using System.Text.Json;
using Keepassity.Mock;

namespace Keepassity.Drills.Runner;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0)
        {
            Console.WriteLine(Usage);
            return 2;
        }

        try
        {
            string? workdir = Drill.ArgOrNull(args, "--workdir");
            if (workdir is not null)
            {
                workdir = Path.GetFullPath(workdir);
                Directory.CreateDirectory(workdir);
                // Keep every drill artefact (remote working copies, sync-state
                // sidecars) inside the temp workdir, never in the real profile.
                Environment.SetEnvironmentVariable("KEEPASSITY_DATA_DIR",
                    Path.Combine(workdir, "appdata"));
            }

            CoreApi.CheckAbi();
            return args[0] switch
            {
                "restore" => Drills.Restore(args),
                "protocol" => Drills.Protocol(args),
                "offline-prepare" => Drills.OfflinePrepare(args),
                "offline-unreachable" => Drills.OfflineUnreachable(args),
                "offline-replay" => Drills.OfflineReplay(args),
                "conflict" => Drills.Conflict(args),
                "--help" or "-h" => Help(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception exception)
        {
            Console.WriteLine("FAIL  unexpected exception: " + exception);
            return 1;
        }
    }

    private const string Usage =
        "usage: DrillRunner <command> [--fixture <kdbx>] [--url <mock-webdav-url>] [--workdir <dir>]";

    private static int Help()
    {
        Console.WriteLine(Usage);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.WriteLine($"unknown command: {command}");
        Console.WriteLine(Usage);
        return 2;
    }
}

internal sealed class OfflineState
{
    public string RemoteUrl { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string Etag { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}

internal static class Drill
{
    public const string MasterPassword = "demopass";

    public static string Arg(string[] args, string name)
        => ArgOrNull(args, name)
           ?? throw new ArgumentException($"missing required argument: {name}");

    public static string? ArgOrNull(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    public static void Pass(string name, string? detail = null)
        => Console.WriteLine("PASS  " + name +
                             (string.IsNullOrEmpty(detail) ? string.Empty : "  (" + detail + ")"));

    public static void Info(string message) => Console.WriteLine("INFO  " + message);

    public static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string RemoteMetaJson(WebDavFileInfo metadata)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["etag"] = metadata.ETag,
            ["size"] = metadata.Size,
            ["modified_unix"] = metadata.ModifiedUnix,
            // SyncRemoteMeta (crates/core/src/dto) renames the enum to lowercase.
            ["reliability"] = "full",
        });

    public static string ReadAction(string decisionJson)
    {
        using JsonDocument document = JsonDocument.Parse(decisionJson);
        return document.RootElement.GetProperty("action").GetString() ?? string.Empty;
    }

    public static T ReadJson<T>(string path) where T : new()
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("drill state is missing", path);
        }
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
    }

    public static void WriteJson(string path, object value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value,
            new JsonSerializerOptions { WriteIndented = true }));

    public static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // A locked file leaves the previous run's data; do not fail here.
            }
        }
        Directory.CreateDirectory(path);
    }

    public static string StatePath(string workdir) => Path.Combine(workdir, "offline-state.json");
}
