// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// A vault with large attachments can take minutes to download or upload on a
/// slow link; the per-request timeouts must therefore distinguish the small
/// metadata request from the whole-file transfer, and must still end a stalled
/// transfer. These checks use the loopback WebDAV server with an injected delay.
/// </summary>
internal static class WebDavTimeoutTests
{
    public static void Run(TestCheck check)
    {
        DefaultTimeoutsAreSizedForLargeVaults(check);
        MetadataTimeoutFailsFast(check);
        UploadTimeoutEndsAStalledTransfer(check);
        DownloadTimeoutEndsAStalledTransfer(check);
        CallerCancellationIsNotReportedAsTimeout(check);
    }

    private static void DefaultTimeoutsAreSizedForLargeVaults(TestCheck check)
    {
        check("webdav: the metadata timeout stays short",
            WebDavClient.DefaultMetadataTimeout <= TimeSpan.FromSeconds(60) &&
            WebDavClient.DefaultMetadataTimeout >= TimeSpan.FromSeconds(10),
            WebDavClient.DefaultMetadataTimeout.ToString());
        check("webdav: the transfer timeout allows a large vault on a slow link",
            WebDavClient.DefaultTransferTimeout >= TimeSpan.FromMinutes(10),
            WebDavClient.DefaultTransferTimeout.ToString());
    }

    private static void MetadataTimeoutFailsFast(TestCheck check)
    {
        using var server = new WebDavMockServer();
        server.ResponseDelay = TimeSpan.FromMilliseconds(500);
        server.PutResource("/dav/vault.kdbx", Encoding.UTF8.GetBytes("database"),
            "\"etag-1\"", DateTimeOffset.UtcNow);
        using var client = new WebDavClient(
            $"http://127.0.0.1:{server.Port}/dav/vault.kdbx", "user", "password",
            metadataTimeout: TimeSpan.FromMilliseconds(100));

        WebDavException? failure = null;
        try
        {
            _ = client.GetMetadataAsync().GetAwaiter().GetResult();
        }
        catch (WebDavException ex)
        {
            failure = ex;
        }
        check("webdav: a slow metadata request times out with a clear error",
            failure is not null &&
            failure.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase),
            failure?.Message ?? "no exception");
    }

    private static void UploadTimeoutEndsAStalledTransfer(TestCheck check)
    {
        using var server = new WebDavMockServer();
        server.ResponseDelay = TimeSpan.FromMilliseconds(500);
        using var client = new WebDavClient(
            $"http://127.0.0.1:{server.Port}/dav/vault.kdbx", "user", "password",
            metadataTimeout: WebDavClient.DefaultMetadataTimeout,
            transferTimeout: TimeSpan.FromMilliseconds(100));

        WebDavException? failure = null;
        try
        {
            _ = client.UploadAsync(Encoding.UTF8.GetBytes("payload"), ifMatch: null)
                .GetAwaiter().GetResult();
        }
        catch (WebDavException ex)
        {
            failure = ex;
        }
        check("webdav: a stalled upload is aborted with a clear error",
            failure is not null &&
            failure.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase),
            failure?.Message ?? "no exception");
    }

    private static void DownloadTimeoutEndsAStalledTransfer(TestCheck check)
    {
        using var server = new WebDavMockServer();
        server.ResponseDelay = TimeSpan.FromMilliseconds(500);
        server.PutResource("/dav/vault.kdbx", Encoding.UTF8.GetBytes("database"),
            "\"etag-1\"", DateTimeOffset.UtcNow);
        using var client = new WebDavClient(
            $"http://127.0.0.1:{server.Port}/dav/vault.kdbx", "user", "password",
            transferTimeout: TimeSpan.FromMilliseconds(100));

        WebDavException? failure = null;
        try
        {
            _ = client.DownloadAsync().GetAwaiter().GetResult();
        }
        catch (WebDavException ex)
        {
            failure = ex;
        }
        check("webdav: a stalled download is aborted with a clear error",
            failure is not null &&
            failure.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase),
            failure?.Message ?? "no exception");
    }

    private static void CallerCancellationIsNotReportedAsTimeout(TestCheck check)
    {
        using var server = new WebDavMockServer();
        server.ResponseDelay = TimeSpan.FromMilliseconds(500);
        server.PutResource("/dav/vault.kdbx", Encoding.UTF8.GetBytes("database"),
            "\"etag-1\"", DateTimeOffset.UtcNow);
        using var client = new WebDavClient(
            $"http://127.0.0.1:{server.Port}/dav/vault.kdbx", "user", "password");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        bool canceledAsExpected = false;
        try
        {
            _ = client.GetMetadataAsync(canceled.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            canceledAsExpected = true;
        }
        catch (WebDavException)
        {
            canceledAsExpected = false;
        }
        check("webdav: caller cancellation is not rewritten as a timeout",
            canceledAsExpected);
    }
}
