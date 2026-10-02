using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Tests for the pure, offline parts of <see cref="ThumbnailService"/> — the image-detection rules, the
/// thumbnailable gate (image + size cap), and the on-disk cache-path shape. The download/decode path needs
/// a real account and the WPF imaging stack, so it is a Windows/real-account check, not covered here. No
/// network, no credentials, no display.
/// </summary>
public sealed class ThumbnailServiceTests : IDisposable
{
    private readonly string _cacheRoot;
    private readonly ThumbnailService _service;

    public ThumbnailServiceTests()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(), "nuvia-thumbs-test-" + Guid.NewGuid().ToString("N"));
        _service = new ThumbnailService(new StubStorage(), _cacheRoot);
    }

    [Theory]
    [InlineData("image/png", "whatever.bin", true)]
    [InlineData("image/jpeg", "a.jpg", true)]
    [InlineData("IMAGE/WEBP", "a", true)]            // mime match is case-insensitive
    [InlineData("application/pdf", "a.pdf", false)]
    [InlineData("video/mp4", "clip.mp4", false)]
    [InlineData(null, "photo.PNG", true)]            // extension wins when mime is absent
    [InlineData(null, "notes.txt", false)]
    [InlineData("application/octet-stream", "a.webp", true)] // generic mime, image extension
    [InlineData(null, null, false)]
    public void IsImageFile_FollowsMimeThenExtension(string? mime, string? name, bool expected)
        => Assert.Equal(expected, ThumbnailService.IsImageFile(mime, name));

    [Fact]
    public void IsThumbnailable_TrueForASmallImage()
        => Assert.True(ThumbnailService.IsThumbnailable(Img(1, 7, 2048, "image/png", "a.png")));

    [Fact]
    public void IsThumbnailable_FalseForANonImage()
        => Assert.False(ThumbnailService.IsThumbnailable(Img(1, 7, 2048, "application/pdf", "a.pdf")));

    [Fact]
    public void IsThumbnailable_FalseWhenOverTheSourceSizeCap()
    {
        var tooBig = ThumbnailService.MaxSourceBytes + 1;
        Assert.False(ThumbnailService.IsThumbnailable(Img(1, 7, tooBig, "image/png", "a.png")));
    }

    [Fact]
    public void IsThumbnailable_FalseForZeroSizeOrNull()
    {
        Assert.False(ThumbnailService.IsThumbnailable(Img(1, 7, 0, "image/png", "a.png")));
        Assert.False(ThumbnailService.IsThumbnailable(null!));
    }

    [Theory]
    [InlineData("video/mp4", "clip.mp4", true)]
    [InlineData("VIDEO/QUICKTIME", "a", true)]       // mime match is case-insensitive
    [InlineData(null, "VID-20261001.MP4", true)]     // extension wins when mime is absent
    [InlineData(null, "movie.mkv", true)]
    [InlineData("image/png", "a.png", false)]        // images are not "video"
    [InlineData("application/pdf", "a.pdf", false)]
    [InlineData(null, null, false)]
    public void IsVideoFile_FollowsMimeThenExtension(string? mime, string? name, bool expected)
        => Assert.Equal(expected, ThumbnailService.IsVideoFile(mime, name));

    [Fact]
    public void IsThumbnailable_TrueForASmallVideo()
        => Assert.True(ThumbnailService.IsThumbnailable(Img(1, 7, 4_600_000, "video/mp4", "clip.mp4")));

    [Fact]
    public void IsThumbnailable_FalseForAVideoOverItsLargerCap()
    {
        var tooBig = ThumbnailService.MaxVideoSourceBytes + 1;
        Assert.False(ThumbnailService.IsThumbnailable(Img(1, 7, tooBig, "video/mp4", "clip.mp4")));
    }

    [Fact]
    public void IsThumbnailable_VideoCapIsHigherThanTheImageCap()
        => Assert.True(ThumbnailService.MaxVideoSourceBytes > ThumbnailService.MaxSourceBytes);

    [Fact]
    public void CachePathFor_IsKeyedByAccountThenDocumentId()
    {
        var file = Img(docId: 1234, ownerId: 7, sizeBytes: 2048, mime: "image/png", name: "a.png");
        var expected = Path.Combine(_cacheRoot, "7", "1234.jpg");
        Assert.Equal(expected, _service.CachePathFor(file));
    }

    [Fact]
    public void TryGetCachedPath_NullForNonImageOrWhenNoFileExists()
    {
        Assert.Null(_service.TryGetCachedPath(Img(1, 7, 2048, "application/pdf", "a.pdf")));
        Assert.Null(_service.TryGetCachedPath(Img(2, 7, 2048, "image/png", "a.png"))); // image, but nothing cached
    }

    [Fact]
    public void TryGetCachedPath_ReturnsThePathOnceTheCacheFileExists()
    {
        var file = Img(docId: 99, ownerId: 7, sizeBytes: 2048, mime: "image/png", name: "a.png");
        var path = _service.CachePathFor(file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF }); // stand-in for a cached JPEG

        Assert.Equal(path, _service.TryGetCachedPath(file));
    }

    /// <summary>Minimal <see cref="ITelegramStorage"/> so the service can be constructed. The pure methods
    /// under test never call it; anything that would hit the network throws, keeping the tests honest.</summary>
    private sealed class StubStorage : ITelegramStorage
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public event EventHandler<RemoteDeletionEventArgs>? RemoteFilesDeleted { add { } remove { } }
        public void StartWatching() => throw new NotSupportedException();
        public Task<HistoryImportResult> ImportSavedMessagesAsync(int afterMessageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HistoryImportResult> ImportGroupHistoryAsync(ManagedStorageGroup group, int afterMessageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiscoveredStorageGroup>> DiscoverManagedStorageGroupsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteMessageId>> FindMissingRemoteMessagesAsync(IReadOnlyList<SavedMessageRef> references, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedStorageGroup> CreatePrivateStorageGroupAsync(string title, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ManagedStorageGroup> RenameStorageGroupAsync(ManagedStorageGroup group, string newTitle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteStorageGroupAsync(ManagedStorageGroup group, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SavedMessageRef> UploadDocumentAsync(string localPath, StorageDestination destination, IProgress<double>? progress, CancellationToken cancellationToken, TransferGate? pauseGate = null, string? folderPath = null) => throw new NotSupportedException();
        public Task DownloadDocumentAsync(SavedMessageRef reference, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken, TransferGate? pauseGate = null) => throw new NotSupportedException();
        public Task<SavedMessageRef> RefreshMetadataAsync(SavedMessageRef reference, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteDocumentAsync(SavedMessageRef reference, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateFolderMarkerAsync(SavedMessageRef reference, ManagedStorageGroup? channelGroup, string? folderPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() { }
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_cacheRoot, recursive: true); } catch { /* best-effort */ }
    }

    private static IndexedFile Img(long docId, long ownerId, long sizeBytes, string? mime, string name) => new()
    {
        OwnerId = ownerId,
        OriginalFileName = name,
        DisplayName = name,
        SizeBytes = sizeBytes,
        MimeType = mime,
        Remote = new SavedMessageRef(
            PeerKind: SavedMessageRef.PeerKindSelf,
            PeerId: ownerId,
            MessageId: 1,
            DocumentId: docId,
            AccessHash: 7,
            FileReference: new byte[] { 1, 2, 3 },
            DcId: 2,
            SizeBytes: sizeBytes,
            MimeType: mime,
            UploadedUtc: DateTime.UtcNow),
    };
}
