// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Keepassity.Mock;

/// <summary>
/// Facts about the TLS certificate a WebDAV server presents, shown to the user
/// before an explicit trust decision (SY-04: self-signed certificates are only
/// accepted after the user confirms the fingerprint, and the app never disables
/// certificate validation globally).
/// </summary>
public sealed class TlsCertificateInfo
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Subject { get; init; }
    public required string Issuer { get; init; }
    /// <summary>Lowercase hex SHA-256 of the DER certificate.</summary>
    public required string Sha256FingerprintHex { get; init; }
    public required DateTimeOffset NotAfter { get; init; }
    /// <summary>Validation errors Windows reported (None for a publicly trusted certificate).</summary>
    public required SslPolicyErrors Errors { get; init; }

    public bool IsPubliclyTrusted => Errors == SslPolicyErrors.None;

    public string Endpoint => $"{Host}:{Port}";
}

/// <summary>A TLS handshake failed (untrusted certificate, wrong host name, ...).</summary>
public sealed class WebDavTlsException : WebDavException
{
    public WebDavTlsException(Uri uri, Exception inner)
        : base($"The TLS connection to {uri.Host}:{uri.Port} failed: {inner.Message}")
    {
        Host = uri.Host;
        Port = uri.Port;
        Inner = inner;
    }

    public string Host { get; }
    public int Port { get; }
    public Exception Inner { get; }
}

/// <summary>
/// Connects to a WebDAV endpoint once and reports the certificate it presents,
/// without sending any request or credentials.
/// </summary>
public static class WebDavTls
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Lowercase hex SHA-256 of the certificate's DER encoding.</summary>
    public static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();

    /// <summary>
    /// Opens a TLS connection to <paramref name="host"/>:<paramref name="port"/>,
    /// captures the presented certificate and returns its facts. The connection
    /// is closed again; no HTTP data is exchanged.
    /// </summary>
    public static async Task<TlsCertificateInfo> ProbeAsync(string host, int port,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        // The certificate object handed to the callback is only valid during the
        // handshake, so copy the facts we need inside the callback.
        string? subject = null;
        string? issuer = null;
        string? fingerprint = null;
        DateTime notAfter = default;
        SslPolicyErrors errors = SslPolicyErrors.None;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            await using var stream = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                (_, certificate, _, policyErrors) =>
                {
                    if (certificate is not null)
                    {
                        subject = certificate.Subject;
                        issuer = certificate.Issuer;
                        fingerprint = Fingerprint(certificate);
                        if (certificate is X509Certificate2 x509)
                        {
                            notAfter = x509.NotAfter;
                        }
                        else
                        {
                            using var copy = new X509Certificate2(certificate);
                            notAfter = copy.NotAfter;
                        }
                    }
                    errors = policyErrors;
                    // Always accept here: the probe only reads the certificate so
                    // the UI can ask the user; it never carries data or credentials.
                    return true;
                });
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out while checking the TLS certificate of {host}:{port}.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
        {
            throw new WebDavTlsException(new Uri($"https://{host}:{port}/"), ex);
        }
        if (fingerprint is null)
        {
            throw new WebDavTlsException(new Uri($"https://{host}:{port}/"),
                new AuthenticationException("the server did not present a certificate"));
        }
        return new TlsCertificateInfo
        {
            Host = host,
            Port = port,
            Subject = subject ?? string.Empty,
            Issuer = issuer ?? string.Empty,
            Sha256FingerprintHex = fingerprint,
            NotAfter = notAfter.ToUniversalTime(),
            Errors = errors,
        };
    }
}
