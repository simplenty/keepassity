// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Xml.Linq;

namespace Keepassity.Core;

public class WebDavException : Exception
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
public sealed class WebDavClient : IDisposable, ISyncRemote
{
    private const string PropfindBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:getetag/><d:getcontentlength/>" +
        "<d:getlastmodified/></d:prop></d:propfind>";

    private readonly HttpClient _client;
    /// <summary>
    /// Uploads never follow redirects: a 301/302/303/307/308 would otherwise be
    /// replayed as a body-less GET (303) and a 200 response from the redirect
    /// target would look like a successful database upload.
    /// </summary>
    private readonly HttpClient _uploadClient;
    private readonly Uri _fileUri;
    private readonly TimeSpan _metadataTimeout;
    private readonly TimeSpan _transferTimeout;

    /// <summary>PROPFIND is a small request; a short timeout keeps sync responsive.</summary>
    public static readonly TimeSpan DefaultMetadataTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Download/upload carry the whole database, which can hold large attachments;
    /// the old fixed 45-second client timeout made big vaults on slow links fail
    /// every save. A stalled transfer still ends after half an hour.
    /// </summary>
    public static readonly TimeSpan DefaultTransferTimeout = TimeSpan.FromMinutes(30);

    /// <param name="trustedCertificateSha256">
    /// Lowercase hex SHA-256 of a certificate the user explicitly trusted (SY-04).
    /// Only that exact certificate is accepted in addition to the platform-trusted
    /// chain; certificate validation is never disabled globally.
    /// </param>
    /// <param name="metadataTimeout">Override for tests; defaults to
    /// <see cref="DefaultMetadataTimeout"/>.</param>
    /// <param name="transferTimeout">Override for tests; defaults to
    /// <see cref="DefaultTransferTimeout"/>.</param>
    public WebDavClient(string fileUrl, string username, string password,
        string? trustedCertificateSha256 = null, TimeSpan? metadataTimeout = null,
        TimeSpan? transferTimeout = null)
    {
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new WebDavException("WebDAV URL must be an absolute http(s) file URL.");
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            // Basic authentication sends the WebDAV password with every
            // request, so plain HTTP is only acceptable for loopback tests.
            throw new WebDavException(
                "WebDAV URLs must use https:// (the password travels with Basic authentication).");
        }
        _fileUri = uri;
        _metadataTimeout = metadataTimeout ?? DefaultMetadataTimeout;
        _transferTimeout = transferTimeout ?? DefaultTransferTimeout;
        _client = CreateHttpClient(username, password, trustedCertificateSha256,
            // Download and metadata must not follow redirects either: a redirected
            // host could receive Basic credentials, and a redirected GET could
            // replace the working copy with a different resource. The upload
            // client already rejects redirects; both paths fail closed.
            allowAutoRedirect: false);
        _uploadClient = CreateHttpClient(username, password, trustedCertificateSha256,
            allowAutoRedirect: false);
    }

    /// <summary>Timeout for one request; caller cancellation still wins.</summary>
    private static CancellationTokenSource StartTimeout(CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        CancellationTokenSource cts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static HttpClient CreateHttpClient(string username, string password,
        string? trustedCertificateSha256, bool allowAutoRedirect)
    {
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true,
            AllowAutoRedirect = allowAutoRedirect,
        };
        if (!string.IsNullOrWhiteSpace(trustedCertificateSha256))
        {
            string pinned = trustedCertificateSha256.Trim().ToLowerInvariant();
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                errors == SslPolicyErrors.None ||
                (certificate is not null &&
                 string.Equals(WebDavTls.Fingerprint(certificate), pinned,
                     StringComparison.Ordinal));
        }
        var client = new HttpClient(handler)
        {
            // Per-operation timeouts are applied by the caller so a large vault
            // upload is not capped at the metadata request's short timeout.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        // Update together with Directory.Build.props on a release bump.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("keepassity/1.0.0");
        if (!string.IsNullOrEmpty(username))
        {
            string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", token);
        }
        return client;
    }

    public async Task<WebDavFileInfo?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        try
        {
        using var timeout = StartTimeout(cancellationToken, _metadataTimeout);
        CancellationToken token = timeout.Token;
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), _fileUri);
        request.Headers.TryAddWithoutValidation("Depth", "0");
        request.Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml");
        using HttpResponseMessage response = await _client.SendAsync(request,
            HttpCompletionOption.ResponseContentRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, token);
        string text = await response.Content.ReadAsStringAsync(token);
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
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException tls)
        {
            throw new WebDavTlsException(_fileUri, tls);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebDavException("The WebDAV metadata request timed out.");
        }
    }

    public async Task<byte[]> DownloadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = StartTimeout(cancellationToken, _transferTimeout);
            CancellationToken token = timeout.Token;
            using HttpResponseMessage response = await _client.GetAsync(_fileUri,
                HttpCompletionOption.ResponseHeadersRead, token);
            await EnsureSuccessAsync(response, token);
            return await response.Content.ReadAsByteArrayAsync(token);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException tls)
        {
            throw new WebDavTlsException(_fileUri, tls);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebDavException("The WebDAV download timed out.");
        }
    }

    public async Task<string?> UploadAsync(byte[] bytes, string? ifMatch,
        CancellationToken cancellationToken = default)
    {
        using var timeout = StartTimeout(cancellationToken, _transferTimeout);
        CancellationToken token = timeout.Token;
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
        HttpResponseMessage response;
        try
        {
            response = await _uploadClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, token);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException tls)
        {
            throw new WebDavTlsException(_fileUri, tls);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebDavException("The WebDAV upload timed out.");
        }
        using (response)
        {
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new WebDavException("The remote database changed during sync.", 412,
                conflict: true);
        }
        await EnsureSuccessAsync(response, token);
        return response.Headers.ETag?.Tag
               ?? (response.Headers.TryGetValues("ETag", out IEnumerable<string>? values)
                   ? values.FirstOrDefault()?.Trim()
                   : null);
        }
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
            HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect =>
                "The WebDAV server redirected the request; refusing to follow it.",
            _ => $"WebDAV request failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
        };
        if (body.Length is > 0 and < 500)
        {
            message += " " + body.Trim();
        }
        throw new WebDavException(message, (int)response.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _uploadClient.Dispose();
    }
}
