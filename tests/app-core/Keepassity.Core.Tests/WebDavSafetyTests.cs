// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// Regressions for WebDAV transport safety: an upload redirect must never look
/// like a successful PUT, and Basic authentication must not travel over plain
/// HTTP to a non-loopback host.
/// </summary>
internal static class WebDavSafetyTests
{
    public static void Run(TestCheck check)
    {
        using var server = new WebDavMockServer();
        byte[] payload = Encoding.UTF8.GetBytes("database-bytes");
        DateTimeOffset modified = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        server.PutResource("/dav/vault.kdbx", payload, "\"etag-1\"", modified);
        // A redirect target that answers 200 is exactly the shape that made a
        // followed 303 report success even though the PUT never landed.
        server.PutResource("/redirected-target", Encoding.UTF8.GetBytes("not-a-database"),
            "\"etag-2\"", modified);
        server.RedirectUploads = true;

        string url = $"http://127.0.0.1:{server.Port}/dav/vault.kdbx";
        using (var client = new WebDavClient(url, "dav-user", "dav-password"))
        {
            bool rejected = false;
            int? status = null;
            try
            {
                _ = client.UploadAsync(payload, "\"etag-1\"").GetAwaiter().GetResult();
            }
            catch (WebDavException ex)
            {
                rejected = true;
                status = ex.StatusCode;
            }
            check("webdav upload redirect is rejected",
                rejected && status == 303, status?.ToString() ?? "no exception");
            check("webdav upload redirect is not followed",
                server.PendingRequests == 1,
                server.PendingRequests.ToString());
        }

        // A redirected GET must not carry Basic credentials to another resource
        // or replace the working copy with whatever the redirect target serves.
        int requestsBeforeDownload = server.PendingRequests;
        server.RedirectDownloads = true;
        using (var downloadClient = new WebDavClient(url, "dav-user", "dav-password"))
        {
            bool downloadRejected = false;
            int? downloadStatus = null;
            try
            {
                _ = downloadClient.DownloadAsync().GetAwaiter().GetResult();
            }
            catch (WebDavException ex)
            {
                downloadRejected = true;
                downloadStatus = ex.StatusCode;
            }
            check("webdav download redirect is rejected",
                downloadRejected && downloadStatus == 302,
                downloadStatus?.ToString() ?? "no exception");
            check("webdav download redirect is not followed",
                server.PendingRequests == requestsBeforeDownload + 1,
                (server.PendingRequests - requestsBeforeDownload).ToString());
        }

        bool nonLoopbackHttpRejected = false;
        try
        {
            using var _ = new WebDavClient("http://example.test/vault.kdbx", "user", "password");
        }
        catch (WebDavException)
        {
            nonLoopbackHttpRejected = true;
        }
        check("webdav rejects non-loopback http", nonLoopbackHttpRejected, string.Empty);

        // Loopback HTTP stays available for the in-process test harness and
        // local development servers; it never carries a real remote password.
        bool loopbackAccepted = false;
        try
        {
            using var _ = new WebDavClient(url, "user", "password");
            loopbackAccepted = true;
        }
        catch (WebDavException)
        {
            loopbackAccepted = false;
        }
        check("webdav allows loopback http for tests", loopbackAccepted, string.Empty);
    }
}
