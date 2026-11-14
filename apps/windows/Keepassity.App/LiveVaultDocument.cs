// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.App;

/// <summary>
/// Adapts the open window's vault file to <see cref="IVaultDocument"/>: durable
/// gated writes, core reopen and merging through a second handle stay in the
/// shell, while the sync protocol itself is platform-free in Core.
/// </summary>
internal sealed class LiveVaultDocument : IVaultDocument
{
    private readonly MainWindow _window;

    public LiveVaultDocument(MainWindow window, string path)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        Path = path;
    }

    public string Path { get; }

    public Task<byte[]> ReadBytesAsync() => _window.ReadLocalBytesAsync(Path);

    public Task ReplaceAsync(byte[] bytes) => _window.WriteLocalBytesAsync(Path, bytes);

    public Task ReloadAsync() => _window.ReopenFromLocalAsync();

    public Task<SyncMergeResult> MergeRemoteAsync(byte[] remote)
        => _window.ProbeMergeAsync(remote);
}
