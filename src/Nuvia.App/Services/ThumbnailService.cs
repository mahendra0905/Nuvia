using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nuvia.App.Services;

/// <summary>
/// Builds and caches small image previews for the large-icon file view.
/// <para>
/// Nuvia uploads every file as a plain document (<c>force_file</c>), so Telegram keeps no server-side
/// photo or video thumbnail we could fetch cheaply. To show a real preview the full file is downloaded
/// once, shrunk to a small JPEG, and written to
/// <c>%LOCALAPPDATA%\Nuvia\thumbs\&lt;account&gt;\&lt;documentId&gt;.jpg</c>. Every later view (and every
/// restart) reads that tiny cached file instead of downloading again.
/// </para>
/// <para>
/// Images are decoded directly by WPF. <b>Videos</b> are turned into a poster frame by the installed
/// Windows shell thumbnail handler (the very same thumbnail Explorer shows) via
/// <see cref="IShellItemImageFactory"/> — no extra package, no bundled decoder, and nothing is produced
/// when the OS has no handler/codec for that format (the tile then just keeps its type glyph). Because a
/// video has to be downloaded whole to be thumbnailed, videos carry a larger-but-still-bounded size cap
/// (<see cref="MaxVideoSourceBytes"/>); anything over it stays on its glyph rather than pulling a big
/// download for a preview.
/// </para>
/// <para>
/// It is deliberately best-effort: anything that cannot be built (unreadable format, missing codec,
/// expired reference that will not refresh, oversized source, a transient error, or cancellation) returns
/// <c>null</c> and the tile falls back to its type glyph. It never throws to the caller, never touches the
/// SQLite index (so it cannot race the UI-thread connection) and only reads what the user is already
/// looking at.
/// </para>
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    /// <summary>Longest-side pixel size of a cached thumbnail (and the UI decode width).</summary>
    public const int MaxThumbPixels = 256;

    /// <summary>Images larger than this are left as a glyph rather than pulling a big download for a preview.</summary>
    public const long MaxSourceBytes = 25L * 1024 * 1024;

    /// <summary>
    /// Videos larger than this keep their glyph. A video must be downloaded in full to extract a poster
    /// frame, so this is capped — generous enough for typical phone/messenger clips, small enough that a
    /// vault full of large movies does not quietly pull gigabytes just to draw tiles.
    /// </summary>
    public const long MaxVideoSourceBytes = 50L * 1024 * 1024;

    // At most a couple of previews are fetched at once: previews must never crowd out a user-initiated
    // upload/download, and a vault opened for the first time should sip bandwidth, not gulp it.
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly ITelegramStorage _storage;
    private readonly string _cacheRoot;

    public ThumbnailService(ITelegramStorage storage, string cacheRoot)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _cacheRoot = cacheRoot ?? throw new ArgumentNullException(nameof(cacheRoot));
    }

    /// <summary>True when a file can be previewed (image or video) and is small enough to be worth it. Pure.</summary>
    public static bool IsThumbnailable(IndexedFile file)
    {
        if (file is null || file.SizeBytes <= 0)
            return false;
        if (IsImageFile(file.MimeType, file.OriginalFileName))
            return file.SizeBytes <= MaxSourceBytes;
        if (IsVideoFile(file.MimeType, file.OriginalFileName))
            return file.SizeBytes <= MaxVideoSourceBytes;
        return false;
    }

    /// <summary>Whether a MIME type / file name names a raster image Nuvia can decode for a preview.</summary>
    public static bool IsImageFile(string? mimeType, string? fileName)
    {
        var mime = mimeType?.ToLowerInvariant() ?? string.Empty;
        if (mime.StartsWith("image/", StringComparison.Ordinal))
            return true;
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tiff" or ".tif";
    }

    /// <summary>Whether a MIME type / file name names a video the Windows shell may be able to thumbnail.</summary>
    public static bool IsVideoFile(string? mimeType, string? fileName)
    {
        var mime = mimeType?.ToLowerInvariant() ?? string.Empty;
        if (mime.StartsWith("video/", StringComparison.Ordinal))
            return true;
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".m4v"
            or ".wmv" or ".flv" or ".3gp" or ".mpeg" or ".mpg" or ".ts" or ".m2ts";
    }

    /// <summary>Where this file's cached preview lives. Keyed by the stable remote document id, per account.</summary>
    public string CachePathFor(IndexedFile file)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));
        var account = file.OwnerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var doc = file.Remote.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(_cacheRoot, account, doc + ".jpg");
    }

    /// <summary>The cached preview path if one already exists on disk, else null. Does not contact Telegram.</summary>
    public string? TryGetCachedPath(IndexedFile file)
    {
        if (!IsThumbnailable(file) || file.Remote.DocumentId == 0)
            return null;
        var path = CachePathFor(file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Return the path to this file's cached preview, generating it (download → shrink → cache) if needed.
    /// Null when no preview can be produced. For a channel-backed file the matching <paramref name="channelGroup"/>
    /// must be supplied so an expired reference can be refreshed; it is used transiently and never persisted.
    /// </summary>
    public async Task<string?> GetThumbnailPathAsync(
        IndexedFile file, ManagedStorageGroup? channelGroup, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsThumbnailable(file) || !file.Remote.LooksUsable())
                return null;

            var cachePath = CachePathFor(file);
            if (File.Exists(cachePath))
                return cachePath;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(cachePath))
                    return cachePath; // Built by another pass while we waited for the gate.
                cancellationToken.ThrowIfCancellationRequested();

                // Keep the real extension on the temp file: the Windows shell thumbnail handler picks its
                // decoder from the extension, so a video must look like a .mp4/.mov/... on disk.
                var ext = Path.GetExtension(file.OriginalFileName);
                if (string.IsNullOrEmpty(ext) || ext.Length > 8)
                    ext = ".bin";
                var temp = Path.Combine(Path.GetTempPath(), "nuvia-thumb-" + Guid.NewGuid().ToString("N") + ext);
                try
                {
                    try
                    {
                        await _storage.DownloadDocumentAsync(file.Remote, temp, null, cancellationToken).ConfigureAwait(false);
                    }
                    catch (FileReferenceExpiredException)
                    {
                        // Re-read the message for a fresh reference and retry once. We do not write it back to
                        // the index — persistence stays on the UI thread; here it only serves this download.
                        var fresh = await _storage.RefreshMetadataAsync(file.Remote, channelGroup, cancellationToken).ConfigureAwait(false);
                        await _storage.DownloadDocumentAsync(fresh, temp, null, cancellationToken).ConfigureAwait(false);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // Decoding / frame-grabbing is CPU-heavy and must stay off the UI thread (the caller
                    // awaits us from the dispatcher). Images decode in WPF; videos go through the shell.
                    var isImage = IsImageFile(file.MimeType, file.OriginalFileName);
                    var built = await Task.Run(
                        () => isImage ? TryGenerateImage(temp, cachePath) : TryGenerateViaShell(temp, cachePath),
                        cancellationToken).ConfigureAwait(false);
                    return built ? cachePath : null;
                }
                finally
                {
                    TryDelete(temp);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // Best-effort: a preview that cannot be built just leaves the tile on its type glyph.
            return null;
        }
    }

    /// <summary>Decode <paramref name="sourcePath"/>, shrink to <see cref="MaxThumbPixels"/>, write a JPEG to the cache.</summary>
    private static bool TryGenerateImage(string sourcePath, string cachePath)
    {
        try
        {
            BitmapFrame frame;
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                frame = BitmapFrame.Create(
                    input,
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.OnLoad);

            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                return false;

            double longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
            BitmapSource source = frame;
            if (longest > MaxThumbPixels)
            {
                var scale = MaxThumbPixels / longest;
                var scaled = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                scaled.Freeze();
                source = scaled;
            }

            return WriteJpegAtomically(source, cachePath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ask the installed Windows shell thumbnail handler for a poster frame of <paramref name="sourcePath"/>
    /// (how Explorer draws a video tile) and cache it as a JPEG. Returns false — leaving the tile on its
    /// glyph — when the OS has no thumbnail for that file (missing codec, unsupported container, …).
    /// </summary>
    private static bool TryGenerateViaShell(string sourcePath, string cachePath)
    {
        IShellItemImageFactory? factory = null;
        var hBitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(sourcePath, IntPtr.Zero, iid, out factory);
            if (factory is null)
                return false;

            var size = new SIZE { cx = MaxThumbPixels, cy = MaxThumbPixels };
            // THUMBNAILONLY: a real frame or nothing — never the generic file-type icon (we already draw one).
            factory.GetImage(size, SIIGBF.ResizeToFit | SIIGBF.ThumbnailOnly, out hBitmap);
            if (hBitmap == IntPtr.Zero)
                return false;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return WriteJpegAtomically(source, cachePath);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
            if (factory is not null)
                Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Encode <paramref name="source"/> to a JPEG, writing a sibling temp first then moving it into
    /// place so a crash never leaves a torn cache file.</summary>
    private static bool WriteJpegAtomically(BitmapSource source, string cachePath)
    {
        var tmpOut = cachePath + ".tmp";
        try
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
            encoder.Frames.Add(BitmapFrame.Create(source));

            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using (var output = new FileStream(tmpOut, FileMode.Create, FileAccess.Write, FileShare.None))
                encoder.Save(output);

            if (File.Exists(cachePath))
                File.Delete(cachePath);
            File.Move(tmpOut, cachePath);
            return true;
        }
        catch
        {
            TryDelete(tmpOut);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* a leftover temp file is harmless */ }
    }

    public void Dispose() => _gate.Dispose();

    // ----- Windows shell thumbnail interop (video poster frames; same handler Explorer uses) -----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }
}
