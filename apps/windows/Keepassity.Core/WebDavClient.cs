// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Keepassity.Mock;

public sealed class WebDavException : Exception
{
    public WebDavException(string message, int? statusCode = null, bool conflict = false)
        : base(message)
    {
        StatusCode = statusCode;
        Conflict = conflict;
    }

    public int? StatusCode { get; }
    public bool Conflict { get; }
}

public sealed class WebDavFileInfo
{
    public string? ETag { get; init; }
    public long Size { get; init; }
    public long? ModifiedUnix { get; init; }
}

/// <summary>Minimal WebDAV file client: PROPFIND depth 0, GET and conditional PUT.</summary>
public sealed class WebDavClient : IDisposable
{
    private const string PropfindBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:getetag/><d:getcontentlength/>" +
        "<d:getlastmodified/></d:prop></d:propfind>";

    private readonly HttpClient _client;
    private readonly Uri _fileUri;

    public WebDavClient(string fileUrl, string username, string password)
    {
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new WebDavException("WebDAV URL must be an absolute http(s) file URL.");
        }
        _fileUri = uri;
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true,
            AllowAutoRedirect = true,
        };
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("keepassity/0.1");
        if (!string.IsNullOrEmpty(username))
        {
            string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", token);
        }
    }

    public async Task<WebDavFileInfo?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), _fileUri);
        request.Headers.TryAddWithoutValidation("Depth", "0");
        request.Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml");
        using HttpResponseMessage response = await _client.SendAsync(request,
            HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, cancellationToken);
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        XNamespace dav = "DAV:";
        XElement? prop = XDocument.Parse(text)
            .Descendants(dav + "response")
            .SelectMany(responseElement => responseElement.Elements(dav + "propstat"))
            .Select(propstat => propstat.Element(dav + "prop"))
            .FirstOrDefault(element => element is not null);
        if (prop is null)
        {
            throw new WebDavException("WebDAV PROPFIND response has no property block.");
        }
        string? etag = prop.Element(dav + "getetag")?.Value;
        _ = long.TryParse(prop.Element(dav + "getcontentlength")?.Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out long size);
        long? modified = null;
        if (DateTimeOffset.TryParse(prop.Element(dav + "getlastmodified")?.Value,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset when))
        {
            modified = when.ToUnixTimeSeconds();
        }
        return new WebDavFileInfo
        {
            ETag = string.IsNullOrWhiteSpace(etag) ? null : etag.Trim(),
            Size = size,
            ModifiedUnix = modified,
        };
    }

    public async Task<byte[]> DownloadAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _client.GetAsync(_fileUri,
            HttpCompletionOption.ResponseContentRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<string?> UploadAsync(byte[] bytes, string? ifMatch,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, _fileUri);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content = content;
        if (ifMatch is null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        using HttpResponseMessage response = await _client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new WebDavException("The remote database changed during sync.", 412,
                conflict: true);
        }
        await EnsureSuccessAsync(response, cancellationToken);
        return response.Headers.ETag?.Tag
               ?? (response.Headers.TryGetValues("ETag", out IEnumerable<string>? values)
                   ? values.FirstOrDefault()?.Trim()
                   : null);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            // Error body is best-effort; status mapping below is enough.
        }
        string message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "WebDAV authentication failed.",
            HttpStatusCode.Forbidden => "WebDAV access was denied.",
            HttpStatusCode.NotFound => "The remote database file was not found.",
            HttpStatusCode.MethodNotAllowed => "The WebDAV server rejected the required method.",
            HttpStatusCode.InsufficientStorage => "The WebDAV server is out of storage.",
            HttpStatusCode.Conflict => "The remote path conflicts with a collection.",
            _ => $"WebDAV request failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
        };
        if (body.Length is > 0 and < 500)
        {
            message += " " + body.Trim();
        }
        throw new WebDavException(message, (int)response.StatusCode);
    }

    public void Dispose() => _client.Dispose();
}
