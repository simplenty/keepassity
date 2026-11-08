// SPDX-License-Identifier: GPL-3.0-or-later
// Minimal, self-contained WebDAV server for the M4-07 drills.
//
// It speaks just enough HTTP/1.1 for the application's WebDavClient:
//   PROPFIND (Depth: 0) -> ETag / content length / last modified
//   GET / HEAD
//   PUT with ETag, If-Match and If-None-Match preconditions
//   DELETE (drill cleanup)
//   OPTIONS
//
// Paths are percent-decoded (UTF-8), so non-ASCII file names such as
// "密码库（个人）.kdbx" work. The ETag is the SHA-256 of the file content, which
// makes conditional requests meaningful without keeping a second metadata
// store.
//
// This is a test tool, not a production server: it binds to loopback, handles
// one connection at a time, always answers "Connection: close", and performs no
// authentication (the drill checks that the client sends Basic credentials but
// the server deliberately accepts anything).
//
// Usage:
//   MockWebDav --root <dir> [--port 0] [--port-file <path>] [--log <path>]
//              [--host 127.0.0.1] [--stop-file <path>]
//
// --port 0 lets the OS pick a free port; the actual port is printed as
// `MOCKWEBDAV READY port=<n>` and written to --port-file so scripts can
// discover it without capturing stdout. Creating the --stop-file stops the
// server gracefully.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Keepassity.Drills.MockWebDav;

internal static class Program
{
    private const string Usage =
        "usage: MockWebDav --root <dir> [--port 0] [--port-file <path>] [--log <path>]\n" +
        "                  [--host 127.0.0.1] [--stop-file <path>]";

    private static string _root = string.Empty;
    private static string? _logPath;
    private static readonly object LogLock = new();
    private static volatile bool _stopping;

    private static int Main(string[] args)
    {
        int port = 0;
        string? portFile = null;
        string? stopFile = null;
        string host = "127.0.0.1";

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root":
                    _root = RequireValue(args, ref index, "--root");
                    break;
                case "--port":
                    port = int.Parse(RequireValue(args, ref index, "--port"),
                        NumberStyles.Integer, CultureInfo.InvariantCulture);
                    break;
                case "--port-file":
                    portFile = RequireValue(args, ref index, "--port-file");
                    break;
                case "--log":
                    _logPath = RequireValue(args, ref index, "--log");
                    break;
                case "--host":
                    host = RequireValue(args, ref index, "--host");
                    break;
                case "--stop-file":
                    stopFile = RequireValue(args, ref index, "--stop-file");
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[index]}");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(_root))
        {
            Console.Error.WriteLine("--root is required");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        _root = Path.GetFullPath(_root);
        Directory.CreateDirectory(_root);

        var listener = new TcpListener(IPAddress.Parse(host), port);
        listener.Start();
        int actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Log($"READY port={actualPort} root={_root}");
        Console.WriteLine($"MOCKWEBDAV READY port={actualPort} root={_root}");
        Console.Out.Flush();

        if (portFile is not null)
        {
            string fullPortFile = Path.GetFullPath(portFile);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPortFile)!);
            File.WriteAllText(fullPortFile,
                actualPort.ToString(CultureInfo.InvariantCulture));
        }

        if (stopFile is not null)
        {
            _ = Task.Run(async () =>
            {
                string fullStopFile;
                try
                {
                    fullStopFile = Path.GetFullPath(stopFile);
                }
                catch
                {
                    return;
                }
                while (!_stopping)
                {
                    if (File.Exists(fullStopFile))
                    {
                        _stopping = true;
                        try
                        {
                            listener.Stop();
                        }
                        catch
                        {
                            // Listener already closed.
                        }
                        return;
                    }
                    await Task.Delay(100).ConfigureAwait(false);
                }
            });
        }

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            _stopping = true;
            try
            {
                listener.Stop();
            }
            catch
            {
                // Listener already closed.
            }
        };

        try
        {
            while (!_stopping)
            {
                using TcpClient client = listener.AcceptTcpClient();
                try
                {
                    HandleConnection(client);
                }
                catch (Exception exception)
                {
                    Log($"ERROR {exception.GetType().Name}: {exception.Message}");
                    try
                    {
                        WriteResponse(client.GetStream(), 500, "Internal Server Error",
                            null, Array.Empty<byte>());
                    }
                    catch
                    {
                        // Client already gone.
                    }
                }
            }
        }
        catch (SocketException) when (_stopping)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Normal shutdown.
        }
        finally
        {
            listener.Stop();
            Log("STOP");
        }
        return 0;
    }

    private static void HandleConnection(TcpClient client)
    {
        using NetworkStream stream = client.GetStream();
        string? requestLine = ReadLine(stream);
        if (string.IsNullOrEmpty(requestLine))
        {
            return;
        }

        string[] parts = requestLine.Split(' ');
        if (parts.Length < 3)
        {
            WriteResponse(stream, 400, "Bad Request", null, Array.Empty<byte>());
            return;
        }
        string method = parts[0];
        string target = parts[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while (!string.IsNullOrEmpty(line = ReadLine(stream)))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (headers.TryGetValue(name, out string? existing))
            {
                headers[name] = existing + ", " + value;
            }
            else
            {
                headers[name] = value;
            }
        }

        if (headers.TryGetValue("Expect", out string? expect) &&
            expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            WriteRaw(stream, "HTTP/1.1 100 Continue\r\n\r\n");
            stream.Flush();
        }

        byte[] body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out string? lengthText) &&
            long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture,
                out long contentLength) && contentLength > 0)
        {
            body = ReadExactly(stream, checked((int)contentLength));
        }

        bool hasAuthorization = headers.ContainsKey("Authorization");
        int status;
        try
        {
            status = Dispatch(stream, method, target, headers, body, hasAuthorization);
        }
        catch (Exception exception)
        {
            Log($"ERROR {method} {target} {exception.GetType().Name}: {exception.Message}");
            WriteResponse(stream, 500, "Internal Server Error", null, Array.Empty<byte>());
            status = 500;
        }
        Log($"{method} {target} -> {status} auth={hasAuthorization}");
    }

    private static int Dispatch(Stream stream, string method, string target,
        IReadOnlyDictionary<string, string> headers, byte[] body, bool hasAuthorization)
    {
        _ = hasAuthorization;
        string path = ResolvePath(target);
        switch (method.ToUpperInvariant())
        {
            case "OPTIONS":
                WriteResponse(stream, 200, "OK",
                    new Dictionary<string, string>
                    {
                        ["Allow"] = "OPTIONS, PROPFIND, GET, HEAD, PUT, DELETE",
                        ["DAV"] = "1",
                    }, Array.Empty<byte>());
                return 200;

            case "PROPFIND":
            {
                if (!File.Exists(path))
                {
                    WriteResponse(stream, 404, "Not Found", null, Array.Empty<byte>());
                    return 404;
                }
                FileInfo info = new(path);
                string etag = EtagOfFile(path);
                byte[] xml = Encoding.UTF8.GetBytes(PropfindXml(
                    RequestTarget(Path.GetFileName(path)), etag, info.Length,
                    info.LastWriteTimeUtc));
                WriteResponse(stream, 207, "Multi-Status",
                    new Dictionary<string, string> { ["ETag"] = etag },
                    xml, "application/xml; charset=utf-8");
                return 207;
            }

            case "GET":
            case "HEAD":
            {
                if (!File.Exists(path))
                {
                    WriteResponse(stream, 404, "Not Found", null, Array.Empty<byte>());
                    return 404;
                }
                FileInfo info = new(path);
                byte[] content = method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                    ? Array.Empty<byte>()
                    : File.ReadAllBytes(path);
                WriteResponse(stream, 200, "OK",
                    new Dictionary<string, string>
                    {
                        ["ETag"] = EtagOfFile(path),
                        ["Last-Modified"] = info.LastWriteTimeUtc.ToString("R",
                            CultureInfo.InvariantCulture),
                    },
                    content, "application/octet-stream", includeContentLength: info.Length);
                return 200;
            }

            case "PUT":
            {
                bool exists = File.Exists(path);
                string? currentEtag = exists ? EtagOfFile(path) : null;
                if (FailsPrecondition(headers, exists, currentEtag))
                {
                    WriteResponse(stream, 412, "Precondition Failed",
                        currentEtag is null
                            ? null
                            : new Dictionary<string, string> { ["ETag"] = currentEtag },
                        Array.Empty<byte>());
                    return 412;
                }
                string? parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                string temp = path + ".upload-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temp, body);
                File.Move(temp, path, overwrite: true);
                string etag = EtagOfBytes(body);
                WriteResponse(stream, exists ? 204 : 201, exists ? "No Content" : "Created",
                    new Dictionary<string, string> { ["ETag"] = etag }, Array.Empty<byte>());
                return exists ? 204 : 201;
            }

            case "DELETE":
            {
                if (!File.Exists(path))
                {
                    WriteResponse(stream, 404, "Not Found", null, Array.Empty<byte>());
                    return 404;
                }
                File.Delete(path);
                WriteResponse(stream, 204, "No Content", null, Array.Empty<byte>());
                return 204;
            }

            default:
                WriteResponse(stream, 405, "Method Not Allowed",
                    new Dictionary<string, string>
                    {
                        ["Allow"] = "OPTIONS, PROPFIND, GET, HEAD, PUT, DELETE",
                    }, Array.Empty<byte>());
                return 405;
        }
    }

    private static bool FailsPrecondition(IReadOnlyDictionary<string, string> headers,
        bool exists, string? currentEtag)
    {
        if (headers.TryGetValue("If-None-Match", out string? ifNoneMatch))
        {
            foreach (string tag in ifNoneMatch.Split(','))
            {
                string trimmed = tag.Trim();
                if (trimmed == "*" && exists)
                {
                    return true;
                }
                if (exists && trimmed == currentEtag)
                {
                    return true;
                }
            }
        }
        if (headers.TryGetValue("If-Match", out string? ifMatch))
        {
            bool wildcard = false;
            bool matches = false;
            foreach (string tag in ifMatch.Split(','))
            {
                string trimmed = tag.Trim();
                if (trimmed == "*")
                {
                    wildcard = true;
                }
                else if (exists && trimmed == currentEtag)
                {
                    matches = true;
                }
            }
            if (!exists)
            {
                return true;
            }
            if (!wildcard && !matches)
            {
                return true;
            }
        }
        return false;
    }

    private static string PropfindXml(string href, string etag, long size,
        DateTime lastModifiedUtc)
    {
        static string Escape(string value) => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
               "<d:multistatus xmlns:d=\"DAV:\"><d:response>" +
               $"<d:href>{Escape(href)}</d:href><d:propstat><d:prop>" +
               $"<d:getetag>{Escape(etag)}</d:getetag>" +
               $"<d:getcontentlength>{size}</d:getcontentlength>" +
               $"<d:getlastmodified>{lastModifiedUtc.ToString("R", CultureInfo.InvariantCulture)}</d:getlastmodified>" +
               "</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>" +
               "</d:response></d:multistatus>";
    }

    private static string RequestTarget(string fileName)
    {
        // Percent-encode each segment so non-ASCII names are legal in the href.
        return "/" + Uri.EscapeDataString(fileName);
    }

    private static string ResolvePath(string target)
    {
        string raw = target;
        int query = raw.IndexOf('?');
        if (query >= 0)
        {
            raw = raw[..query];
        }
        raw = Uri.UnescapeDataString(raw);
        if (!raw.StartsWith('/'))
        {
            raw = "/" + raw;
        }
        string relative = raw.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
        {
            if (segment == ".." || segment == ".")
            {
                throw new InvalidOperationException("path traversal is not allowed");
            }
        }
        string full = Path.GetFullPath(Path.Combine(_root, relative));
        string rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, _root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("path escapes the mock root");
        }
        return full;
    }

    private static string EtagOfFile(string path) => EtagOfBytes(File.ReadAllBytes(path));

    private static string EtagOfBytes(byte[] content) =>
        "\"" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() + "\"";

    private static string? ReadLine(Stream stream)
    {
        var buffer = new List<byte>(128);
        while (true)
        {
            int value = stream.ReadByte();
            if (value < 0)
            {
                return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            }
            if (value == '\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r')
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            buffer.Add((byte)value);
            if (buffer.Count > 64 * 1024)
            {
                throw new InvalidOperationException("request header line too long");
            }
        }
    }

    private static byte[] ReadExactly(Stream stream, int length)
    {
        byte[] data = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = stream.Read(data, offset, length - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException("request body ended early");
            }
            offset += read;
        }
        return data;
    }

    private static void WriteResponse(Stream stream, int status, string reason,
        IReadOnlyDictionary<string, string>? headers, byte[] body,
        string? contentType = null, long? includeContentLength = null)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        builder.Append("Connection: close\r\n");
        builder.Append("Server: keepassity-mock-webdav\r\n");
        if (contentType is not null)
        {
            builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
        }
        builder.Append("Content-Length: ")
            .Append(includeContentLength ?? body.Length)
            .Append("\r\n");
        if (headers is not null)
        {
            foreach (KeyValuePair<string, string> header in headers)
            {
                builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
        }
        builder.Append("\r\n");
        WriteRaw(stream, builder.ToString());
        if (body.Length > 0)
        {
            stream.Write(body, 0, body.Length);
        }
        stream.Flush();
    }

    private static void WriteRaw(Stream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void Log(string message)
    {
        string line = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture) + " " + message;
        if (_logPath is null)
        {
            return;
        }
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never break a drill request.
        }
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"missing value for {name}");
        }
        index++;
        return args[index];
    }
}
