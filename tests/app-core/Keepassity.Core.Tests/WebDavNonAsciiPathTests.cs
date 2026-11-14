// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// M4-01: exercise <see cref="WebDavClient"/> against a non-ASCII (CJK) WebDAV
/// path with an in-process HTTP/1.1 mock built on TcpListener (no URL ACL and no
/// new packages). The mock answers PROPFIND depth 0, HEAD, GET and conditional
/// PUT (If-Match / If-None-Match with ETag comparison), and records the raw
/// request targets so the test can prove the client sends the percent-encoded
/// path rather than raw UTF-8 or a mangled path.
/// </summary>
internal static class WebDavNonAsciiPathTests
{
    // /dav/密码库/测试.kdbx percent-encoded as the client must send it.
    private const string Folder = "/dav/%E5%AF%86%E7%A0%81%E5%BA%93";
    private const string DatabasePath = Folder + "/%E6%B5%8B%E8%AF%95.kdbx";
    private const string NewDatabasePath = Folder + "/%E6%96%B0%E5%BB%BA.kdbx";
    private const string DatabaseRawPath = "/dav/密码库/测试.kdbx";
    private const string NewDatabaseRawPath = "/dav/密码库/新建.kdbx";
    private const string UserName = "dav-user";
    private const string Password = "dav-test-password";

    public static void Run(TestCheck check)
    {
        byte[] firstPayload = Encoding.UTF8.GetBytes("keepassity-vault-version-1");
        byte[] secondPayload = Encoding.UTF8.GetBytes("keepassity-vault-version-2");
        const string firstEtag = "\"etag-1\"";
        var modified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        using var server = new WebDavMockServer();
        server.PutResource(DatabasePath, firstPayload, firstEtag, modified);
        string baseUrl = $"http://127.0.0.1:{server.Port}";

        using (var client = new WebDavClient(baseUrl + DatabaseRawPath, UserName, Password))
        {
            // ---- metadata (PROPFIND depth 0) ------------------------------
            WebDavFileInfo? metadata = client.GetMetadataAsync().GetAwaiter().GetResult();
            check("webdav metadata found", metadata is not null);
            check("webdav metadata size",
                metadata?.Size == firstPayload.LongLength,
                metadata?.Size.ToString(CultureInfo.InvariantCulture) ?? "null");
            check("webdav metadata etag",
                metadata?.ETag == firstEtag,
                metadata?.ETag ?? "null");
            check("webdav metadata last modified",
                metadata?.ModifiedUnix == modified.ToUnixTimeSeconds(),
                metadata?.ModifiedUnix?.ToString(CultureInfo.InvariantCulture) ?? "null");

            WebDavRequest request = server.TakeRequest();
            check("webdav PROPFIND method", request.Method == "PROPFIND", request.Method);
            check("webdav PROPFIND depth 0", request.Header("Depth") == "0",
                request.Header("Depth") ?? "(missing)");
            check("webdav PROPFIND uses the encoded non-ASCII path",
                request.Target == DatabasePath,
                request.Target);
            check("webdav PROPFIND target is ASCII",
                request.Target.All(character => character < 0x80),
                request.Target);
            check("webdav PROPFIND asks for etag/size/modified",
                request.BodyText.Contains("getetag", StringComparison.Ordinal) &&
                request.BodyText.Contains("getcontentlength", StringComparison.Ordinal) &&
                request.BodyText.Contains("getlastmodified", StringComparison.Ordinal));
            string? authorization = request.Header("Authorization");
            check("webdav sends preemptive basic auth",
                authorization == Basic(UserName, Password),
                authorization ?? "(missing)");

            // ---- download (GET) ------------------------------------------
            byte[] downloaded = client.DownloadAsync().GetAwaiter().GetResult();
            check("webdav download returns remote bytes",
                downloaded.SequenceEqual(firstPayload),
                $"{downloaded.Length} byte(s)");
            request = server.TakeRequest();
            check("webdav GET method", request.Method == "GET", request.Method);
            check("webdav GET uses the encoded non-ASCII path",
                request.Target == DatabasePath,
                request.Target);

            // ---- HEAD support in the mock (metadata headers only) --------
            using (var headClient = new HttpClient())
            using (var headRequest = new HttpRequestMessage(
                       HttpMethod.Head, baseUrl + DatabaseRawPath))
            {
                headRequest.Headers.TryAddWithoutValidation(
                    "Authorization", Basic(UserName, Password));
                using HttpResponseMessage headResponse =
                    headClient.Send(headRequest);
                check("webdav HEAD returns metadata without a body",
                    headResponse.StatusCode == HttpStatusCode.OK &&
                    headResponse.Headers.ETag?.Tag == firstEtag &&
                    headResponse.Content.Headers.ContentLength == firstPayload.LongLength,
                    headResponse.StatusCode.ToString());
                WebDavRequest headRecord = server.TakeRequest();
                check("webdav HEAD uses the encoded non-ASCII path",
                    headRecord.Method == "HEAD" && headRecord.Target == DatabasePath,
                    headRecord.Method + " " + headRecord.Target);
            }

            // ---- conditional upload with the current ETag (If-Match) -----
            string? updatedEtag = client.UploadAsync(secondPayload, ifMatch: firstEtag)
                .GetAwaiter().GetResult();
            check("webdav upload returns the new etag",
                updatedEtag == "\"etag-2\"", updatedEtag ?? "null");
            request = server.TakeRequest();
            check("webdav PUT method", request.Method == "PUT", request.Method);
            check("webdav PUT uses the encoded non-ASCII path",
                request.Target == DatabasePath, request.Target);
            check("webdav PUT sends If-Match",
                request.Header("If-Match") == firstEtag,
                request.Header("If-Match") ?? "(missing)");
            check("webdav PUT sends the new bytes",
                request.Body.SequenceEqual(secondPayload),
                $"{request.Body.Length} byte(s)");
            check("webdav PUT replaced the remote content",
                server.ContentOf(DatabasePath).SequenceEqual(secondPayload));

            // The upload verifies what the server stored before reporting success.
            request = server.TakeRequest();
            check("webdav upload reads back the stored size",
                request.Method == "PROPFIND" && request.Target == DatabasePath,
                request.Method + " " + request.Target);

            WebDavFileInfo? refreshed = client.GetMetadataAsync().GetAwaiter().GetResult();
            check("webdav metadata reflects the uploaded etag",
                refreshed?.ETag == "\"etag-2\"", refreshed?.ETag ?? "null");
            _ = server.TakeRequest();

            // ---- stale ETag must surface as a 412 conflict ---------------
            bool conflicted = false;
            try
            {
                client.UploadAsync(firstPayload, ifMatch: firstEtag).GetAwaiter().GetResult();
            }
            catch (WebDavException exception)
            {
                conflicted = exception.StatusCode == 412 && exception.Conflict;
                check("webdav 412 maps to a readable conflict error",
                    exception.Message.Contains("remote database changed",
                        StringComparison.OrdinalIgnoreCase),
                    exception.Message);
            }
            check("webdav stale If-Match upload throws a 412 conflict", conflicted);
            request = server.TakeRequest();
            check("webdav stale PUT still used the encoded path",
                request.Target == DatabasePath, request.Target);
            check("webdav stale PUT carried the stale If-Match",
                request.Header("If-Match") == firstEtag,
                request.Header("If-Match") ?? "(missing)");
            check("webdav stale PUT did not replace the remote content",
                server.ContentOf(DatabasePath).SequenceEqual(secondPayload));

            // ---- If-None-Match: * creates a new remote file --------------
            using var creator = new WebDavClient(
                baseUrl + NewDatabaseRawPath, UserName, Password);
            string? createdEtag = creator.UploadAsync(firstPayload, ifMatch: null)
                .GetAwaiter().GetResult();
            check("webdav create upload returns an etag",
                createdEtag == "\"etag-3\"", createdEtag ?? "null");
            request = server.TakeRequest();
            check("webdav create PUT uses the encoded non-ASCII path",
                request.Target == NewDatabasePath, request.Target);
            check("webdav create PUT sends If-None-Match: *",
                request.Header("If-None-Match") == "*",
                request.Header("If-None-Match") ?? "(missing)");
            check("webdav create PUT stored the new remote file",
                server.ContentOf(NewDatabasePath).SequenceEqual(firstPayload));
            _ = server.TakeRequest();

            // A second unconditional create must not overwrite.
            bool createConflict = false;
            try
            {
                creator.UploadAsync(secondPayload, ifMatch: null).GetAwaiter().GetResult();
            }
            catch (WebDavException exception)
            {
                createConflict = exception.StatusCode == 412 && exception.Conflict;
            }
            check("webdav If-None-Match: * conflict on existing file", createConflict);
            _ = server.TakeRequest();
            check("webdav conflicting create left the file untouched",
                server.ContentOf(NewDatabasePath).SequenceEqual(firstPayload));

            // ---- missing remote file metadata returns null ---------------
            using var missing = new WebDavClient(
                baseUrl + Folder + "/%E7%BC%BA%E5%A4%B1.kdbx", UserName, Password);
            check("webdav missing metadata returns null",
                missing.GetMetadataAsync().GetAwaiter().GetResult() is null);
            _ = server.TakeRequest();
        }

        check("webdav mock saw no unexpected extra requests", server.PendingRequests == 0,
            server.PendingRequests.ToString(CultureInfo.InvariantCulture));
    }

    private static string Basic(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
}

/// <summary>One raw HTTP request observed by the mock server.</summary>
internal sealed record WebDavRequest(
    string Method,
    string Target,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    public string? Header(string name) =>
        Headers.TryGetValue(name, out string? value) ? value : null;

    public string BodyText => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Minimum HTTP/1.1 server that understands the WebDAV subset used by
/// <see cref="WebDavClient"/>. One request per connection, then close.
/// </summary>
internal sealed class WebDavMockServer : IDisposable
{
    private sealed class Resource
    {
        public Resource(byte[] content, string etag, DateTimeOffset modified)
        {
            Content = content;
            ETag = etag;
            Modified = modified;
        }

        public byte[] Content { get; }
        public string ETag { get; }
        public DateTimeOffset Modified { get; }
    }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;
    private readonly object _sync = new();
    private readonly Dictionary<string, Resource> _resources = new(StringComparer.Ordinal);
    private readonly Queue<WebDavRequest> _requests = new();
    private int _etagCounter;

    public WebDavMockServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>Answer the next PUT with a 303 to a readable resource.</summary>
    public bool RedirectUploads { get; set; }

    /// <summary>Answer GET with a 302 to a readable resource.</summary>
    public bool RedirectDownloads { get; set; }

    /// <summary>Delay every response by this much; used by the timeout checks.</summary>
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    /// <summary>When set, PUT stores only this many bytes but still answers 2xx.</summary>
    public int? StoreTruncatedTo { get; set; }

    public int PendingRequests
    {
        get
        {
            lock (_sync)
            {
                return _requests.Count;
            }
        }
    }

    public void PutResource(string encodedTarget, byte[] content, string etag,
        DateTimeOffset modified)
    {
        lock (_sync)
        {
            _resources[encodedTarget] = new Resource(content, etag, modified);
            // Keep generated ETags unique even when a resource is seeded by the test.
            _etagCounter = Math.Max(_etagCounter, EtagSuffix(etag));
        }
    }

    private static int EtagSuffix(string etag) =>
        int.TryParse(etag.Trim('"').Split('-')[^1], NumberStyles.None,
            CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    public byte[] ContentOf(string encodedTarget)
    {
        lock (_sync)
        {
            return _resources.TryGetValue(encodedTarget, out Resource? resource)
                ? resource.Content
                : Array.Empty<byte>();
        }
    }

    public WebDavRequest TakeRequest()
    {
        lock (_sync)
        {
            if (_requests.Count == 0)
            {
                throw new InvalidOperationException("the client made no expected request");
            }
            return _requests.Dequeue();
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token)
                    .ConfigureAwait(false);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal.
        }
        catch (ObjectDisposedException)
        {
            // Listener stopped during disposal.
        }
        catch (SocketException)
        {
            // Listener stopped during disposal.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using NetworkStream stream = client.GetStream();
                WebDavRequest? request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }
                lock (_sync)
                {
                    _requests.Enqueue(request);
                }
                if (ResponseDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ResponseDelay).ConfigureAwait(false);
                }
                Response response = Handle(request);
                await WriteResponseAsync(stream, response).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A malformed or aborted request must not take the server down;
                // the test fails through the missing recorded request instead.
            }
        }
    }

    private static async Task<WebDavRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var header = new StringBuilder();
        var one = new byte[1];
        while (header.Length < 64 * 1024)
        {
            int read = await stream.ReadAsync(one.AsMemory(0, 1)).ConfigureAwait(false);
            if (read == 0)
            {
                return header.Length == 0 ? null : throw new IOException("EOF inside headers");
            }
            header.Append((char)one[0]);
            if (header.Length >= 4 &&
                header[^4] == '\r' && header[^3] == '\n' &&
                header[^2] == '\r' && header[^1] == '\n')
            {
                break;
            }
        }
        string[] lines = header.ToString()
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            throw new IOException("malformed request line");
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < lines.Length; index++)
        {
            int separator = lines[index].IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }
            headers[lines[index][..separator].Trim()] = lines[index][(separator + 1)..].Trim();
        }
        byte[] body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out string? lengthText) &&
            long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture,
                out long length) && length > 0)
        {
            body = new byte[length];
            int offset = 0;
            while (offset < body.Length)
            {
                int read = await stream.ReadAsync(body.AsMemory(offset)).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("EOF inside request body");
                }
                offset += read;
            }
        }
        return new WebDavRequest(requestLine[0], requestLine[1], headers, body);
    }

    private sealed record Response(int Status, string Reason, byte[] Body,
        string? ETag, DateTimeOffset? Modified, long? ContentLength = null,
        string? Location = null);

    private Response Handle(WebDavRequest request)
    {
        lock (_sync)
        {
            switch (request.Method)
            {
                case "PROPFIND":
                    return _resources.TryGetValue(request.Target, out Resource? resource)
                        ? Propfind(resource)
                        : NotFound();
                case "HEAD":
                    return _resources.TryGetValue(request.Target, out Resource? headResource)
                        ? new Response(200, "OK", Array.Empty<byte>(), headResource.ETag,
                            headResource.Modified, headResource.Content.LongLength)
                        : NotFound();
                case "GET":
                    if (RedirectDownloads)
                    {
                        return new Response(302, "Found", Array.Empty<byte>(), null, null,
                            Location: "/redirected-target");
                    }
                    return _resources.TryGetValue(request.Target, out Resource? getResource)
                        ? new Response(200, "OK", getResource.Content, getResource.ETag,
                            getResource.Modified)
                        : NotFound();
                case "PUT":
                    if (RedirectUploads)
                    {
                        // A body-less GET of this target returns 200; a client
                        // that follows 303 would report a fake upload success.
                        return new Response(303, "See Other", Array.Empty<byte>(),
                            null, null, Location: "/redirected-target");
                    }
                    return Put(request);
                default:
                    return new Response(405, "Method Not Allowed", Array.Empty<byte>(),
                        null, null);
            }
        }
    }

    private Response Put(WebDavRequest request)
    {
        bool exists = _resources.TryGetValue(request.Target, out Resource? existing);
        string? ifMatch = request.Header("If-Match");
        string? ifNoneMatch = request.Header("If-None-Match");
        if (ifMatch is not null && (!exists || existing!.ETag != ifMatch))
        {
            return new Response(412, "Precondition Failed", Array.Empty<byte>(), null, null);
        }
        if (ifNoneMatch is not null && exists)
        {
            return new Response(412, "Precondition Failed", Array.Empty<byte>(), null, null);
        }
        string etag = $"\"etag-{++_etagCounter}\"";
        byte[] storedBody = request.Body;
        if (StoreTruncatedTo is int limit && limit >= 0 && limit < storedBody.Length)
        {
            // Simulate a server/proxy that reports success but keeps fewer bytes.
            storedBody = storedBody[..limit];
        }
        _resources[request.Target] = new Resource(storedBody, etag, DateTimeOffset.UtcNow);
        return new Response(exists ? 200 : 201, exists ? "OK" : "Created",
            Array.Empty<byte>(), etag, null);
    }

    private static Response Propfind(Resource resource)
    {
        XNamespace dav = "DAV:";
        var document = new XDocument(
            new XElement(dav + "multistatus",
                new XElement(dav + "response",
                    new XElement(dav + "href", ""),
                    new XElement(dav + "propstat",
                        new XElement(dav + "prop",
                            new XElement(dav + "getetag", resource.ETag),
                            new XElement(dav + "getcontentlength",
                                resource.Content.LongLength.ToString(CultureInfo.InvariantCulture)),
                            new XElement(dav + "getlastmodified",
                                resource.Modified.ToUniversalTime()
                                    .ToString("R", CultureInfo.InvariantCulture))),
                        new XElement(dav + "status", "HTTP/1.1 200 OK")))));
        return new Response(207, "Multi-Status", Encoding.UTF8.GetBytes(document.ToString()),
            null, null);
    }

    private static Response NotFound() =>
        new(404, "Not Found", Array.Empty<byte>(), null, null);

    private static async Task WriteResponseAsync(NetworkStream stream, Response response)
    {
        var headers = new StringBuilder();
        headers.Append("HTTP/1.1 ").Append(response.Status).Append(' ')
            .Append(response.Reason).Append("\r\n");
        long contentLength = response.ContentLength ?? response.Body.LongLength;
        headers.Append("Content-Length: ").Append(contentLength).Append("\r\n");
        if (response.ETag is not null)
        {
            headers.Append("ETag: ").Append(response.ETag).Append("\r\n");
        }
        if (response.Modified is DateTimeOffset modified)
        {
            headers.Append("Last-Modified: ")
                .Append(modified.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture))
                .Append("\r\n");
        }
        if (response.Location is not null)
        {
            headers.Append("Location: ").Append(response.Location).Append("\r\n");
        }
        headers.Append("Content-Type: application/octet-stream\r\n");
        headers.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()))
            .ConfigureAwait(false);
        if (response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body).ConfigureAwait(false);
        }
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Shutdown race is not a test failure.
        }
        _stopping.Dispose();
    }
}
