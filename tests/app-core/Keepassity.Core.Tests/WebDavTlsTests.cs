// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// SY-04: self-signed WebDAV certificates are only accepted after an explicit
/// fingerprint confirmation. These checks cover the certificate probe, the pinned
/// client validation and the settings round-trip against an in-process TLS mock.
/// </summary>
internal static class WebDavTlsTests
{
    public static void Run(TestCheck check)
    {
        X509Certificate2? certificate = null;
        string? skipReason = null;
        try
        {
            certificate = CreateSelfSignedCertificate();
        }
        catch (Exception ex)
        {
            // Some restricted environments (and the project's documented local
            // Schannel limitation) cannot create a usable server key container.
            // The pure client-side checks still run; the TLS integration checks
            // are exercised on a normal Windows runner in CI.
            skipReason = ex.GetType().Name + ": " + ex.Message;
            check("webdav tls integration (skipped: no local TLS server support)",
                true, skipReason);
        }
        if (certificate is not null)
        {
            try
            {
                RunIntegrationChecks(check, certificate);
            }
            finally
            {
                certificate.Dispose();
            }
        }

        var settings = new AppSettings();
        settings.TrustedCertificates["example.test:443"] = new string('a', 64);
        string json = JsonSerializer.Serialize(settings);
        AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);
        bool roundTrip = false;
        string detail = "no settings";
        if (loaded is not null &&
            loaded.TrustedCertificates.TryGetValue("example.test:443", out string? stored))
        {
            roundTrip = stored == new string('a', 64);
            detail = "stored=" + loaded.TrustedCertificates.Count;
        }
        check("trusted certificate fingerprints survive a settings round-trip",
            roundTrip, detail);
        check("pinned client accepts a 64-character hex fingerprint",
            CreateClient("https://example.test/db.kdbx", "a".PadLeft(64, 'a')) is not null,
            "constructor accepted the pin");

        // Manual check against a real TLS server (for example `openssl s_server`):
        // set KEEPASSITY_TLS_TEST_URL=https://host:port/file.kdbx.
        string? external = Environment.GetEnvironmentVariable("KEEPASSITY_TLS_TEST_URL");
        if (!string.IsNullOrWhiteSpace(external) &&
            Uri.TryCreate(external, UriKind.Absolute, out Uri? externalUri))
        {
            RunExternalServerChecks(check, externalUri);
        }
    }

    private static void RunExternalServerChecks(TestCheck check, Uri uri)
    {
        TlsCertificateInfo? info = null;
        try
        {
            info = WebDavTls.ProbeAsync(uri.Host, uri.Port).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            check("external TLS probe (manual)", false,
                ex.GetType().Name + ": " + ex.Message);
        }
        if (info is not null)
        {
            check("external TLS probe reports a fingerprint (manual)",
                info.Sha256FingerprintHex.Length == 64, info.Sha256FingerprintHex);
            check("external TLS server certificate is not publicly trusted (manual)",
                !info.IsPubliclyTrusted, info.Errors.ToString());
        }
        try
        {
            using var client = new WebDavClient(uri.ToString(), "user", "pass");
            _ = client.GetMetadataAsync().GetAwaiter().GetResult();
            check("external TLS server rejects an untrusted certificate (manual)", false,
                "the request unexpectedly succeeded");
        }
        catch (WebDavTlsException)
        {
            check("external TLS server rejects an untrusted certificate (manual)", true);
        }
        catch (Exception ex)
        {
            check("external TLS server rejects an untrusted certificate (manual)", false,
                ex.GetType().Name + ": " + ex.Message);
        }
        if (info is null)
        {
            return;
        }
        try
        {
            using var client = new WebDavClient(uri.ToString(), "user", "pass",
                info.Sha256FingerprintHex);
            // A plain GET works against any HTTPS server (for example
            // `openssl s_server -www`), so it proves the handshake with the
            // pinned certificate succeeded.
            byte[] body = client.DownloadAsync().GetAwaiter().GetResult();
            check("pinned client completes TLS with the external server (manual)", true,
                body.Length + " byte(s) received");
        }
        catch (WebDavTlsException ex)
        {
            check("pinned client completes TLS with the external server (manual)", false,
                "TLS still rejected: " + ex.Message);
        }
        catch (WebDavException ex)
        {
            // A non-TLS WebDAV error means the handshake and HTTP exchange worked.
            check("pinned client completes TLS with the external server (manual)", true,
                "TLS accepted, HTTP/WebDAV error: " + ex.Message);
        }
        catch (Exception ex)
        {
            check("pinned client completes TLS with the external server (manual)", false,
                ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static WebDavClient? CreateClient(string url, string fingerprint)
    {
        using var client = new WebDavClient(url, "user", "pass", fingerprint);
        return new WebDavClient(url, "user", "pass", fingerprint);
    }

    private static void RunIntegrationChecks(TestCheck check, X509Certificate2 certificate)
    {
        string fingerprint = WebDavTls.Fingerprint(certificate);
        check("certificate fingerprint is lowercase hex sha-256",
            fingerprint.Length == 64 && fingerprint.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            fingerprint);
        using var server = new TlsWebDavMockServer(certificate);
        string url = $"https://localhost:{server.Port}/db.kdbx";

        TlsCertificateInfo? probed = null;
        try
        {
            probed = WebDavTls.ProbeAsync("localhost", server.Port).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            check("webdav tls probe succeeds", false,
                ex.GetType().Name + ": " + ex.Message +
                " | server: " + (server.ServerError ?? "(none)"));
        }
        if (probed is not null)
        {
            check("webdav tls probe reports the presented fingerprint",
                string.Equals(probed.Sha256FingerprintHex, fingerprint, StringComparison.Ordinal),
                probed.Sha256FingerprintHex);
            check("webdav tls probe reports untrusted certificate",
                probed.Errors != SslPolicyErrors.None, probed.Errors.ToString());
            check("webdav tls probe reports the host",
                probed.Endpoint == $"localhost:{server.Port}", probed.Endpoint);
        }

        try
        {
            using var client = new WebDavClient(url, "user", "pass");
            _ = client.GetMetadataAsync().GetAwaiter().GetResult();
            check("untrusted server certificate is rejected", false,
                "the request unexpectedly succeeded");
        }
        catch (WebDavTlsException)
        {
            check("untrusted server certificate is rejected", true);
        }
        catch (Exception ex)
        {
            check("untrusted server certificate is rejected", false,
                ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            using var client = new WebDavClient(url, "user", "pass", fingerprint);
            WebDavFileInfo? metadata = client.GetMetadataAsync().GetAwaiter().GetResult();
            check("explicitly trusted certificate is accepted",
                metadata is not null && metadata.Size == 4,
                metadata is null ? "no metadata" : $"size={metadata.Size}");
        }
        catch (Exception ex)
        {
            check("explicitly trusted certificate is accepted", false,
                ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            using var client = new WebDavClient(url, "user", "pass", new string('0', 64));
            _ = client.GetMetadataAsync().GetAwaiter().GetResult();
            check("certificate with a different fingerprint is rejected", false,
                "the request unexpectedly succeeded");
        }
        catch (WebDavException)
        {
            check("certificate with a different fingerprint is rejected", true);
        }
        catch (Exception ex)
        {
            check("certificate with a different fingerprint is rejected", false,
                ex.GetType().Name + ": " + ex.Message);
        }
    }
    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=keepassity-tls-test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        byte[] pfx = ephemeral.Export(X509ContentType.Pfx, "keepassity");
        // Windows Schannel (the TLS server side) cannot use ephemeral keys, so the
        // private key has to live in a real key container. Try the locations in
        // order; restricted environments may block the machine store.
        X509KeyStorageFlags[] attempts =
        {
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable |
            X509KeyStorageFlags.PersistKeySet,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable |
            X509KeyStorageFlags.PersistKeySet,
        };
        foreach (X509KeyStorageFlags flags in attempts)
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12(pfx, "keepassity", flags);
            }
            catch (CryptographicException)
            {
                // Try the next key storage location.
            }
        }
        throw new InvalidOperationException(
            "no key storage for the test TLS certificate is available");
    }
}

/// <summary>
/// Minimal TLS WebDAV server: answers every connection with a PROPFIND 207 for a
/// 4-byte resource. Used only by the certificate trust tests.
/// </summary>
internal sealed class TlsWebDavMockServer : IDisposable
{
    private static readonly byte[] PropfindResponse = Encoding.UTF8.GetBytes(
        "HTTP/1.1 207 Multi-Status\r\n" +
        "Content-Type: application/xml; charset=utf-8\r\n" +
        "Connection: close\r\n" +
        "Content-Length: " + PropfindBody().Length + "\r\n\r\n" + PropfindBody());

    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;

    public TlsWebDavMockServer(X509Certificate2 certificate)
    {
        _certificate = certificate;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string? ServerError { get; private set; }

    private static string PropfindBody() =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/db.kdbx</d:href>" +
        "<d:propstat><d:prop><d:getetag>\"etag-1\"</d:getetag>" +
        "<d:getcontentlength>4</d:getcontentlength>" +
        "<d:getlastmodified>Mon, 09 Nov 2026 00:00:00 GMT</d:getlastmodified>" +
        "</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>";

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
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = new SslStream(client.GetStream(), false);
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, _stopping.Token).ConfigureAwait(false);
                var header = new StringBuilder();
                var one = new byte[1];
                while (header.Length < 64 * 1024)
                {
                    int read = await stream.ReadAsync(one.AsMemory(0, 1), _stopping.Token)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }
                    header.Append((char)one[0]);
                    if (header.Length >= 4 && header[^4] == '\r' && header[^3] == '\n' &&
                        header[^2] == '\r' && header[^1] == '\n')
                    {
                        break;
                    }
                }
                await stream.WriteAsync(PropfindResponse, _stopping.Token).ConfigureAwait(false);
                await stream.FlushAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Record the server-side reason so a broken handshake is reportable.
                ServerError = ex.GetType().Name + ": " + ex.Message;
            }
        }
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
        }
        _stopping.Dispose();
    }
}
