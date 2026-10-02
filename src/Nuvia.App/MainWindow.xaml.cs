using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Nuvia.App.Controls;
using Nuvia.App.QuickUpload;
using Nuvia.App.Services;

namespace Nuvia.App;

public partial class MainWindow : Window
{
    private readonly IndexStore _index;
    private readonly ITelegramStorage _storage;
    private readonly SettingsStore _settings;
    private readonly FileTransferCoordinator _transfers;

    /// <summary>
    /// Rebuilds the local index from Telegram — the post-reinstall recovery pass and the "show external
    /// files" re-scan. UI-free and read-only against Telegram (it only ever writes to the local index).
    /// Run in the background after the window opens and when the external-files toggle is switched on.
    /// </summary>
    private readonly LibraryRebuildService _rebuild;

    private readonly long _accountId;
    private readonly ObservableCollection<FileRow> _rows = new();

    /// <summary>
    /// Raised when the user confirms "Log out and delete local session" in the settings dialog and no
    /// transfer is in progress. The App layer owns the auth service and login windows, so it — not this
    /// window — performs the actual logout and returns to the sign-in screen in-process.
    /// </summary>
    public event Action? LogoutRequested;

    /// <summary>
    /// The full, unfiltered set of rows for the signed-in account, newest first. The visible
    /// <see cref="_rows"/> collection is this list passed through the local name filter. Rebuilt only
    /// from SQLite in <see cref="ReloadList"/> — never from Telegram.
    /// </summary>
    private readonly List<FileRow> _allRows = new();

    /// <summary>The current local search text. Empty means "show everything". Never sent anywhere.</summary>
    private string _searchText = string.Empty;

    private bool _busy;

    /// <summary>How the file area is drawn: large-icon tiles (Explorer default) or a details column list.</summary>
    private enum FileViewMode { Grid, Details }

    /// <summary>Which stored destination the left Locations pane is filtering to. View-only — no new scope.</summary>
    private enum LocationFilter { All, SavedMessages, Group }

    /// <summary>Active file view. Starts on large icons to match the Explorer look the user asked for.</summary>
    private FileViewMode _viewMode = FileViewMode.Grid;

    /// <summary>Current Locations filter. Purely local: it narrows <see cref="_allRows"/>, nothing more.</summary>
    private LocationFilter _locationFilter = LocationFilter.All;

    /// <summary>Optional file-type sub-filter from the left "Storage" overview. null ⇒ all types. View-only.</summary>
    private FileCategory? _categoryFilter;

    /// <summary>Guards programmatic category (de)selection so it does not re-enter the filter pipeline.</summary>
    private bool _syncingCategory;

    /// <summary>The left "Storage" overview rows, updated in place from the local index. Never sample data.</summary>
    private readonly ObservableCollection<CategoryRow> _categories = new();

    /// <summary>
    /// Explorer-style Back/Forward history of visited views. Each entry is a Locations-pane item plus the
    /// folder open within it (null = that location's root), so Back/Forward walk folder depth as well as
    /// Locations. Purely local view history — it changes no data and contacts nothing.
    /// </summary>
    private readonly record struct NavEntry(System.Windows.Controls.ListBoxItem Location, long? FolderId);
    private readonly System.Collections.Generic.List<NavEntry> _navHistory = new();
    private int _navIndex = -1;
    private bool _navigatingHistory;

    /// <summary>Local sort key ("Name" / "Size" / "Uploaded" / "Destination"). Default keeps newest first.</summary>
    private string _sortKey = "Uploaded";

    /// <summary>Sort direction. false = descending, so the default is newest-uploaded first (as loaded).</summary>
    private bool _sortAsc;

    /// <summary>
    /// Every managed private storage group for this account, oldest first (empty if none were created).
    /// Loaded once at start and kept in step after a confirmed create/rename/delete — never fabricated,
    /// never recreated automatically.
    /// </summary>
    private System.Collections.Generic.List<ManagedStorageGroup> _groups = new();

    /// <summary>
    /// The group whose contents are currently open in the file area, or null when the open location is not
    /// a group (Home, All files, or Saved Messages). Drives per-group filtering and the current-location
    /// upload target.
    /// </summary>
    private ManagedStorageGroup? _activeGroup;

    /// <summary>
    /// The left-pane Locations rows created for each managed group (one per usable group), rebuilt whenever
    /// the group set changes. Tracked so they can be removed and re-inserted without disturbing the fixed
    /// Home / All files / Saved Messages nodes.
    /// </summary>
    private readonly List<System.Windows.Controls.ListBoxItem> _dynamicGroupNodes = new();

    /// <summary>Guards <see cref="LocationsList_SelectionChanged"/> while group nodes are being rebuilt.</summary>
    private bool _rebuildingGroupNodes;

    /// <summary>
    /// Where the next upload goes when the open location is Home or All files (the saved fallback default).
    /// An open group or Saved Messages overrides this at upload time — see <see cref="ResolveCurrentDestination"/>.
    /// Starts at Saved Messages and only becomes a group when the user explicitly picks it in the selector
    /// (or had previously chosen it and a usable group still exists).
    /// </summary>
    private StorageDestination _destination = StorageDestination.SavedMessages;

    /// <summary>Guards the destination selector against reacting to changes it makes itself.</summary>
    private bool _updatingDestinationSelector;

    /// <summary>
    /// Cached "also show external Saved Messages files" preference for this account. When false (the
    /// default) the list hides files added to Saved Messages outside Nuvia; Nuvia's own uploads always
    /// show, and the managed group always shows every file regardless of this flag. Kept in step with
    /// <see cref="SettingsStore.GetShowExternalSavedMessages"/>.
    /// </summary>
    private bool _showExternalSaved;

    /// <summary>True while a library rebuild is in flight, so overlapping triggers do not stack import scans.</summary>
    private bool _rebuilding;

    // --------------------------------------------------------------- folders (Nuvia-local layer)

    /// <summary>
    /// The folder currently open inside the active location, or null for the location's root. Folders are a
    /// Nuvia-local layer (never a Telegram concept); this only narrows the local view and the upload target.
    /// Reset to null whenever the open Location changes.
    /// </summary>
    private long? _activeFolderId;

    /// <summary>
    /// Every folder of the open location (all levels), reloaded from the index on each list refresh. Used to
    /// build the folder strip, the breadcrumb and the folder paths — never fabricated.
    /// </summary>
    private List<LocalFolder> _foldersInLocation = new();

    /// <summary>The child-folder tiles shown above the file area for the current level. Bound to FolderStrip.</summary>
    private readonly ObservableCollection<FolderRow> _folderRows = new();

    /// <summary>
    /// Folders apply only inside a single location (Saved Messages or one managed group). Home / All files
    /// span every location, so they stay flat — exactly as before this feature.
    /// </summary>
    private bool FoldersApply => _locationFilter != LocationFilter.All;


    /// <summary>Cancels the transfer that is running right now, if any. Replaced for each transfer.</summary>
    private CancellationTokenSource? _transferCts;

    /// <summary>
    /// Builds and caches the small image previews shown on image tiles. Previews are best-effort and
    /// lazy: <see cref="_thumbCache"/> keeps decoded previews in memory keyed by remote document id so a
    /// re-filter or sort re-uses them without touching disk, while the service writes tiny JPEGs under
    /// <c>%LOCALAPPDATA%\Nuvia\thumbs</c> so they also survive a restart. <see cref="_thumbCts"/> cancels
    /// the in-flight preview pass whenever the visible list changes, so navigating away stops the work.
    /// </summary>
    private readonly ThumbnailService _thumbnails;
    private readonly Dictionary<long, System.Windows.Media.ImageSource> _thumbCache = new();
    private CancellationTokenSource? _thumbCts;

    /// <summary>
    /// Background update/announcement checker and the notification-center model it feeds. The check is a
    /// single outbound HTTPS GET (no local server), best-effort and silent on failure; results are
    /// marshalled back to the UI thread into <see cref="_notifications"/>. <see cref="_latestUpdate"/>
    /// holds the newest offered release (null = none) so the bell popup and the Settings → Updates "Open
    /// download page" link share one source of truth, and <see cref="_updateTimer"/> re-checks every few
    /// hours so a long-running session still notices a release the dev publishes after launch.
    /// </summary>
    private readonly UpdateService _updateService;
    private readonly ObservableCollection<NotificationItem> _notifications = new();
    private CancellationTokenSource? _updateCts;
    private DispatcherTimer? _updateTimer;
    private UpdateInfo? _latestUpdate;

    // Notification sources are kept apart so a refresh of one never drops the other, then merged into
    // _notifications by RebuildNotifications: the update first, then announcements (Telegram channel + any
    // website manifest) newest-first, deduped by Id.
    private readonly AnnouncementService? _announcements;
    private readonly List<NotificationItem> _channelAnnouncements = new();
    private readonly List<NotificationItem> _webAnnouncements = new();
    private DispatcherTimer? _announcementTimer;
    private readonly CancellationTokenSource _announcementCts = new();

    /// <summary>True once the user has confirmed closing during a transfer.</summary>
    private bool _closing;

    /// <summary>True when the confirmed close is waiting for the transfer to finish its cleanup.</summary>
    private bool _closeRequested;

    /// <summary>The separate Windows-style progress window shown while a transfer runs (null when idle).</summary>
    private TransferProgressWindow? _transferWindow;

    /// <summary>
    /// While a batch (multi-file) transfer runs, the "x of N" window header to keep showing. Each file's
    /// snapshots would otherwise overwrite the header with a generic "Uploading to Nuvia" line, losing the
    /// batch context; <see cref="OnTransferSnapshot"/> prefers this when it is set. Null outside a batch.
    /// </summary>
    private string? _batchHeader;

    /// <summary>
    /// True while a drag-OUT (Nuvia → Explorer) is running. The download it drives reports snapshots on the
    /// shared <see cref="Progress{T}"/> channel just like any transfer, but there is no transfer window and
    /// the UI thread is blocked inside the OLE drag loop, so <see cref="OnTransferSnapshot"/> skips its work
    /// to avoid churning the status bar with per-file states. Reset (and the status restored) once the drag
    /// ends and the queued snapshots have drained.
    /// </summary>
    private bool _dragOutActive;

    /// <summary>
    /// When the last remote-deletion reconcile ran. A throttle (see <see cref="ReconcileWithRemoteAsync"/>)
    /// keeps the automatic window-activate catch-up from hammering Telegram; a manual Refresh/F5 bypasses it.
    /// </summary>
    private DateTime _lastReconcileUtc = DateTime.MinValue;

    /// <summary>True while a reconcile is in flight, so overlapping triggers do not stack getMessages calls.</summary>
    private bool _reconciling;

    /// <summary>How long the automatic (non-manual) reconcile stays quiet after a run.</summary>
    private static readonly TimeSpan ReconcileThrottle = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One child-folder tile in the folder strip. A tiny view record — id, name and a short count caption.
    /// Backed by a real <see cref="LocalFolder"/> row; there is no sample data.
    /// </summary>
    public sealed class FolderRow
    {
        public required long Id { get; init; }
        public required string Name { get; init; }
        public required string ItemCountDisplay { get; init; }
    }

    /// <summary>
    /// One row of the local index, shaped for display. Backed by a real index record — there is no
    /// sample or placeholder data in authenticated operation.
    /// </summary>
    public sealed class FileRow : INotifyPropertyChanged
    {
        public required IndexedFile File { get; init; }
        public required string Name { get; init; }
        public required string SizeDisplay { get; init; }
        public required string UploadedDisplay { get; init; }
        public required string Destination { get; init; }

        /// <summary>Segoe Fluent/MDL2 glyph chosen from the file's MIME type or extension, for the tile view.</summary>
        public required string IconGlyph { get; init; }

        /// <summary>
        /// Short upper-case type label (e.g. "TXT", "PDF", "MP4", "ZIP") shown under the glyph so one
        /// document tile is told apart from another at a glance. Empty for images, which show a real preview.
        /// </summary>
        public required string TypeLabel { get; init; }

        /// <summary>True when this file is an image (so its tile shows no type label — a preview replaces it).</summary>
        public required bool IsImage { get; init; }

        /// <summary>
        /// True when a real preview is worth attempting for this file — an image, or a video small enough to
        /// download for a poster frame. Gates the lazy thumbnail pass; a video keeps its "MP4"-style label
        /// until (and unless) the preview actually loads.
        /// </summary>
        public required bool IsPreviewable { get; init; }

        /// <summary>
        /// True when this file is a video. Drives the small corner "play" badge drawn over the poster-frame
        /// preview so a video reads as a video at a glance, instead of looking like a still photo.
        /// </summary>
        public required bool IsVideo { get; init; }

        private System.Windows.Media.ImageSource? _thumbnail;

        /// <summary>
        /// The cached image preview once it has loaded, or null while it is still a glyph. Set on the UI
        /// thread by the lazy preview pass; the tile swaps from glyph to picture when it changes.
        /// </summary>
        public System.Windows.Media.ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value)) return;
                _thumbnail = value;
                OnChanged(nameof(Thumbnail));
                OnChanged(nameof(ThumbnailVisibility));
                OnChanged(nameof(GlyphVisibility));
                OnChanged(nameof(VideoBadgeVisibility));
            }
        }

        public Visibility ThumbnailVisibility => _thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GlyphVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The video badge shows only on a video tile that is actually displaying its poster frame
        /// (while it is still a glyph the "MP4" label already marks it, so the badge would be redundant).</summary>
        public Visibility VideoBadgeVisibility => IsVideo && _thumbnail is not null ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public static FileRow From(IndexedFile file, ManagedStorageGroup? group)
        {
            // A file stored in the managed group shows that group's title, but only when the row we hold
            // is actually that same group (matched by channel id). Anything channel-backed that we cannot
            // match is labelled generically rather than mislabelled with the current group's name.
            var destination = file.Remote.PeerKind == SavedMessageRef.PeerKindChannel
                ? (group is not null && group.ChannelId == file.Remote.PeerId
                    && !string.IsNullOrWhiteSpace(group.Title)
                        ? group.Title
                        : "Private group")
                : "Saved Messages";

            var isImage = ThumbnailService.IsImageFile(file.MimeType, file.OriginalFileName);
            var isVideo = ThumbnailService.IsVideoFile(file.MimeType, file.OriginalFileName);

            return new()
            {
                File = file,
                Name = file.DisplayName,
                SizeDisplay = FormatSize(file.SizeBytes),
                UploadedDisplay = file.RemoteUploadUtc.ToUniversalTime()
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Destination = destination,
                IconGlyph = GlyphForFile(file),
                IsImage = isImage,
                IsPreviewable = ThumbnailService.IsThumbnailable(file),
                IsVideo = isVideo,
                TypeLabel = isImage ? string.Empty : TypeLabelFor(file),
            };
        }

        /// <summary>
        /// A short, upper-case type label from the file extension (or a MIME fallback) — "TXT", "PDF",
        /// "DOCX", "MP4", "ZIP". Purely presentational; capped so an odd extension cannot blow out the tile.
        /// </summary>
        private static string TypeLabelFor(IndexedFile file)
        {
            var ext = Path.GetExtension(file.OriginalFileName)?.TrimStart('.') ?? string.Empty;
            if (ext.Length is > 0 and <= 5)
                return ext.ToUpperInvariant();

            // No usable extension: fall back to the MIME subtype's tail (e.g. "application/pdf" -> "PDF").
            var mime = file.MimeType ?? string.Empty;
            var slash = mime.LastIndexOf('/');
            if (slash >= 0 && slash < mime.Length - 1)
            {
                var sub = mime[(slash + 1)..];
                var plus = sub.IndexOf('+');
                if (plus > 0) sub = sub[..plus];
                if (sub.Length is > 0 and <= 5)
                    return sub.ToUpperInvariant();
            }
            return "FILE";
        }

        /// <summary>
        /// Picks a type glyph for the large-icon view from the record's MIME type, falling back to the
        /// original file extension. Purely presentational — it reads only fields already held locally.
        /// </summary>
        private static string GlyphForFile(IndexedFile file)
        {
            var mime = file.MimeType?.ToLowerInvariant() ?? string.Empty;
            var ext = Path.GetExtension(file.OriginalFileName)?.ToLowerInvariant() ?? string.Empty;

            if (mime.StartsWith("image/")
                || ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".heic" or ".tiff")
                return ""; // Photo

            if (mime.StartsWith("video/")
                || ext is ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".wmv" or ".m4v")
                return ""; // Video

            if (mime.StartsWith("audio/")
                || ext is ".mp3" or ".flac" or ".wav" or ".m4a" or ".ogg" or ".aac" or ".opus")
                return ""; // MusicInfo

            if (mime.Contains("zip") || mime.Contains("compressed") || mime.Contains("x-tar")
                || ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2")
                return ""; // Folder-like, for archives

            // Everything else (pdf, office docs, text, unknown) shows a generic document glyph.
            return ""; // Document
        }
    }

    /// <summary>File-type buckets for the left "Storage" overview. Every file maps to exactly one.</summary>
    public enum FileCategory { Images, Documents, Archive, Media, Unknown, Others }

    /// <summary>
    /// One file-type bucket shown in the left "Storage" pane. Its count and size are filled from the real
    /// local index and updated in place, so the pane's selection survives a refresh. Purely presentational.
    /// </summary>
    public sealed class CategoryRow : INotifyPropertyChanged
    {
        public FileCategory Category { get; }
        public string Name { get; }
        public string Glyph { get; }

        private int _count;
        private long _bytes;

        public CategoryRow(FileCategory category, string name, string glyph)
        {
            Category = category;
            Name = name;
            Glyph = glyph;
        }

        public string CountDisplay => _count == 1 ? "1 File" : $"{_count} Files";
        public string SizeDisplay => FormatSize(_bytes);
        public Visibility RowVisibility => _count > 0 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Updates the bucket's totals in place, raising change notifications only when they move.</summary>
        public void Set(int count, long bytes)
        {
            if (count == _count && bytes == _bytes) return;
            _count = count;
            _bytes = bytes;
            OnChanged(nameof(CountDisplay));
            OnChanged(nameof(SizeDisplay));
            OnChanged(nameof(RowVisibility));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Classifies a file into exactly one <see cref="FileCategory"/> from its MIME type and extension.
    /// Reads only fields already held locally — it contacts nothing and mutates nothing.
    /// </summary>
    private static FileCategory CategoryFor(IndexedFile file)
    {
        var mime = file.MimeType?.ToLowerInvariant() ?? string.Empty;
        var ext = Path.GetExtension(file.OriginalFileName)?.ToLowerInvariant() ?? string.Empty;

        if (mime.StartsWith("image/")
            || ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".heic" or ".heif" or ".tiff" or ".svg" or ".ico")
            return FileCategory.Images;

        if (mime.StartsWith("video/") || mime.StartsWith("audio/")
            || ext is ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".wmv" or ".m4v"
                   or ".mp3" or ".flac" or ".wav" or ".m4a" or ".ogg" or ".aac" or ".opus")
            return FileCategory.Media;

        if (mime.Contains("zip") || mime.Contains("compressed") || mime.Contains("x-tar")
            || mime.Contains("x-7z") || mime.Contains("x-rar")
            || ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".cab")
            return FileCategory.Archive;

        if (mime == "application/pdf" || mime.StartsWith("text/")
            || mime.Contains("word") || mime.Contains("excel") || mime.Contains("spreadsheet")
            || mime.Contains("powerpoint") || mime.Contains("presentation")
            || mime.Contains("officedocument") || mime.Contains("opendocument")
            || ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx"
                   or ".txt" or ".rtf" or ".csv" or ".md" or ".odt" or ".ods" or ".odp" or ".epub" or ".pages")
            return FileCategory.Documents;

        if (ext.Length == 0 && (mime.Length == 0 || mime == "application/octet-stream"))
            return FileCategory.Unknown;

        return FileCategory.Others;
    }

    /// <summary>Creates the storage-overview rows once, in the order shown in the pane.</summary>
    private void BuildCategoryRows()
    {
        // Glyphs are Segoe MDL2; icon colour stays on the Nuvia accent (theme rule), not the mock's mix.
        _categories.Add(new CategoryRow(FileCategory.Images, "Images", ""));
        _categories.Add(new CategoryRow(FileCategory.Documents, "Documents", ""));
        _categories.Add(new CategoryRow(FileCategory.Archive, "Archive", ""));
        _categories.Add(new CategoryRow(FileCategory.Media, "Media", ""));
        _categories.Add(new CategoryRow(FileCategory.Unknown, "Unknown files", ""));
        _categories.Add(new CategoryRow(FileCategory.Others, "Others", ""));
    }

    public MainWindow(IndexStore index, ITelegramStorage storage, SettingsStore settings, long accountId, string? accountName = null, AnnouncementService? announcements = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _accountId = accountId;
        // Dev-pushed announcements channel reader. Null in demo/unavailable mode (no signed-in client), in
        // which case the bell shows updates only. Never joins, posts to, or browses anything but the one
        // app-owned public broadcast channel.
        _announcements = announcements;
        // The coordinator reports through this one channel. Progress<T> is built here on the UI
        // thread, so every Report lands on the dispatcher; the transfer itself runs on a worker
        // thread and never touches a control directly.
        _transfers = new FileTransferCoordinator(_index, _storage, new Progress<TransferSnapshot>(OnTransferSnapshot));

        // The rebuild coordinator only reads Telegram history and writes the local index. Like the list,
        // all of its index access happens on the UI thread (see RunLibraryRebuildAsync), so it never races
        // the reconcile or a transfer over the SQLite connection.
        _rebuild = new LibraryRebuildService(_index, _storage, _settings);

        // Image previews are cached under %LOCALAPPDATA%\Nuvia\thumbs so they survive a restart.
        _thumbnails = new ThumbnailService(_storage, Path.Combine(NuviaConfig.LocalDataDirectory, "thumbs"));

        // Update/announcement checker. Outbound-only, best-effort; no server, no account data on the wire.
        _updateService = new UpdateService(_settings);

        InitializeComponent();

        FileListView.ItemsSource = _rows;
        FileGridView.ItemsSource = _rows;
        FolderStrip.ItemsSource = _folderRows;
        NotificationsList.ItemsSource = _notifications;

        // "Contact support" (Settings → About) opens the support chat in Telegram via the default handler.
        // Hidden until a support handle is configured in AppInfo, so it never shows a dead button.
        if (AppInfo.SupportConfigured)
        {
            ContactSupportButton.Tag = AppInfo.SupportUrl;
            ContactSupportButton.Visibility = Visibility.Visible;
        }

        // The location name in the breadcrumb doubles as a crumb back to the location's root while a folder
        // is open (RebuildBreadcrumb toggles the cursor so it only looks clickable when it is).
        LocationHeaderText.MouseLeftButtonDown += (_, _) =>
        {
            if (FoldersApply && _activeFolderId is not null)
                EnterFolder(null);
        };

        // Build the left "Storage" overview once; its per-type counts are filled from the local index on
        // every list refresh (RefreshCategoryCounts), so the numbers are always real, never sample data.
        BuildCategoryRows();
        CategoriesList.ItemsSource = _categories;

        IndexPathText.Text = "Index: " + _index.DatabasePath;

        // Account identity shown in the top-right chip and its menu header. This is the account's own
        // display name — never a phone number, code or session material.
        var chipLabel = string.IsNullOrWhiteSpace(accountName)
            ? (_accountId > 0 ? "Account" : "Not signed in")
            : accountName;
        AccountChipText.Text = chipLabel;
        AccountLabelItem.Header = _accountId > 0 ? $"Signed in — {chipLabel}" : "Not signed in";

        // Load the account's group + preferred destination once. This only reads local state; it never
        // creates or contacts a group.
        LoadDestinationState();
        UpdateUploadTargetText();

        Loaded += (_, _) =>
        {
            ReloadList();

            // Seed the Back/Forward history with the landing location (Home, at its root) so the first
            // navigation away from it can be walked back, exactly like Explorer opening on a starting folder.
            if (_navHistory.Count == 0 && LocationsList.SelectedItem is System.Windows.Controls.ListBoxItem seed)
            {
                _navHistory.Add(new NavEntry(seed, null));
                _navIndex = 0;
            }
            UpdateNavButtons();
            UpdateSearchPlaceholder();

            // Explorer-style mouse behaviour on both views: rubber-band selection from empty space, and a
            // files drag-OUT when a selected item is dragged. Wire once, after the template is realised.
            MarqueeSelector.Attach(FileListView, BeginItemDragOut);
            MarqueeSelector.Attach(FileGridView, BeginItemDragOut);

            // Live remote-delete sync. A file removed from Telegram anywhere else (phone, another device,
            // the Telegram app) disappears from Nuvia. StartWatching subscribes to the account's update
            // stream for instant pushes; the reconcile is a throttled catch-up (also on window-activate and
            // Refresh/F5) for deletions that happened while Nuvia was closed or the push was missed. This
            // only ever re-checks message ids Nuvia already stored — never a history scan.
            _storage.RemoteFilesDeleted += OnRemoteFilesDeleted;
            _storage.StartWatching();
            Activated += OnWindowActivated;
            _ = ReconcileWithRemoteAsync(force: true);

            // Rebuild the local index from Telegram once on open. This is what brings files back after a
            // reinstall wiped the local database (they still live on the account). It runs in the
            // background, never blocks the window, and only refreshes the list if it actually found
            // something. Read-only against Telegram — it imports history but sends/deletes nothing.
            _ = RunLibraryRebuildAsync();

            // Background update check (opt-out). Silent no-op when no endpoint is configured; never blocks
            // the window and never fakes a result. A periodic timer keeps a long session up to date.
            if (_settings.GetAutoCheckUpdates())
            {
                _ = RunUpdateCheckAsync();
                EnsureUpdateTimer();
            }

            // Dev-pushed announcements: a no-join background read of the one app-owned public channel over the
            // signed-in account. Silent no-op until a channel handle is configured. A short timer keeps it
            // near-real-time without ever holding the UI thread.
            if (_announcements is not null && _announcements.IsConfigured)
            {
                _ = RunAnnouncementsFetchAsync();
                EnsureAnnouncementTimer();
            }

            UpdateNotificationBadge();
        };
    }

    // ------------------------------------------------------------------ list

    /// <summary>
    /// Reloads rows from SQLite into <see cref="_allRows"/>, then applies the current local filter.
    /// Every query is restricted to the signed-in account id, so a different account can never surface
    /// these records. This only re-reads the local index — it never imports anything from Telegram.
    /// </summary>
    private void ReloadList(long? selectId = null)
    {
        _allRows.Clear();

        // Owner id 0 means "no real account" (demo mode); nothing can be stored under it, so the
        // list is simply empty rather than a risk of showing another account's files.
        if (_accountId > 0)
        {
            foreach (var file in _index.ListForAccount(_accountId))
            {
                // Saved Messages hides files added outside Nuvia unless the user opted in. The managed
                // group always shows every file, so channel rows are never filtered here. This is a
                // view-only guard — the rows still exist locally until the toggle-off handler purges them.
                if (!_showExternalSaved
                    && file.Remote.PeerKind == SavedMessageRef.PeerKindSelf
                    && file.Source == IndexStore.SourceExternal)
                    continue;

                _allRows.Add(FileRow.From(file, GroupForChannel(file.Remote.PeerId)));
            }
        }

        // Refresh the folders of the open location from the index before filtering, so the strip, the
        // breadcrumb and FolderMatches all see the current tree.
        RefreshFoldersInLocation();
        ApplyFilter(selectId);
    }

    /// <summary>
    /// Rebuilds the visible <see cref="_rows"/> from <see cref="_allRows"/>, keeping only rows whose
    /// display name contains the search text (case-insensitive). Purely local: it filters rows already
    /// read from SQLite and never touches Telegram.
    /// </summary>
    private void ApplyFilter(long? selectId = null)
    {
        _rows.Clear();

        // Refresh the left storage overview first: it may clear a now-empty type filter, which must be
        // settled before we decide which rows match below.
        RefreshCategoryCounts();

        var query = _searchText.Trim();

        // Collect the rows that pass both the local name search and the Locations-pane filter, then
        // sort them in memory. Both filters and the sort are purely local view state — _allRows keeps
        // its original order and nothing here contacts Telegram.
        var matched = new List<FileRow>();
        foreach (var row in _allRows)
        {
            // OrdinalIgnoreCase gives predictable, culture-independent matching that behaves the way a
            // user expects for ordinary filenames (ASCII case-folding without locale surprises).
            var nameMatch = query.Length == 0
                || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (nameMatch && LocationMatches(row) && CategoryMatches(row) && FolderMatches(row))
                matched.Add(row);
        }

        foreach (var row in SortRows(matched))
        {
            // Re-use an already-decoded preview so a filter/sort does not flash the glyph then the picture.
            if (row.IsPreviewable && row.File.Remote.DocumentId != 0
                && _thumbCache.TryGetValue(row.File.Remote.DocumentId, out var cached))
                row.Thumbnail = cached;
            _rows.Add(row);
        }

        // The folder strip and breadcrumb are view state derived from the open location + active folder.
        RebuildFolderStrip();
        RebuildBreadcrumb();

        UpdateState();

        // Lazily fill in real image previews for whatever is now on screen (cancels any earlier pass).
        QueueThumbnailLoads();

        if (selectId is { } id)
        {
            foreach (var row in _rows)
            {
                if (row.File.Id == id)
                {
                    // Reselect in whichever view is currently active so the selection survives a reload
                    // regardless of Grid/Details mode.
                    if (_viewMode == FileViewMode.Grid)
                        FileGridView.SelectedItem = row;
                    else
                        FileListView.SelectedItem = row;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Starts a lazy pass that fills in real image previews for the rows now on screen, cancelling any
    /// earlier pass first. Cached previews are already attached in <see cref="ApplyFilter"/>, so this only
    /// touches image rows that still show a glyph. A no-op when storage is offline (nothing to download).
    /// </summary>
    private void QueueThumbnailLoads()
    {
        _thumbCts?.Cancel();
        if (!_storage.IsAvailable)
            return;

        var pending = _rows
            .Where(r => r.IsPreviewable && r.Thumbnail is null && r.File.Remote.LooksUsable())
            .ToList();
        if (pending.Count == 0)
            return;

        _thumbCts = new CancellationTokenSource();
        _ = LoadThumbnailsAsync(pending, _thumbCts.Token);
    }

    /// <summary>
    /// Builds previews for <paramref name="rows"/> one at a time (the service also caps concurrency), then
    /// shows each on its tile. Runs on the UI thread between awaits — the download and decode happen inside
    /// the service on worker threads — so assigning <see cref="FileRow.Thumbnail"/> and touching
    /// <see cref="_thumbCache"/> here is single-threaded and safe. Best-effort: a row with no preview is
    /// simply left on its glyph.
    /// </summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<FileRow> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var channelGroup = GroupForChannel(row.File.Remote.PeerId);

            string? path;
            try
            {
                path = await _thumbnails.GetThumbnailPathAsync(row.File, channelGroup, cancellationToken);
            }
            catch
            {
                continue; // The service is best-effort, but guard the await regardless.
            }

            if (path is null || cancellationToken.IsCancellationRequested)
                continue;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;         // Read the file now so it is not left locked.
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.DecodePixelWidth = ThumbnailService.MaxThumbPixels;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();

                _thumbCache[row.File.Remote.DocumentId] = bitmap;
                row.Thumbnail = bitmap;
            }
            catch
            {
                // A corrupt cache file just leaves the tile on its glyph; drop it so a later pass can rebuild.
                try { File.Delete(path); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>True when a row belongs in the currently selected Locations filter. View-only.</summary>
    private bool LocationMatches(FileRow row) => _locationFilter switch
    {
        LocationFilter.SavedMessages => row.File.Remote.PeerKind == SavedMessageRef.PeerKindSelf,
        // A group location shows only the files stored in that specific group's channel.
        LocationFilter.Group => _activeGroup is not null
            && row.File.Remote.PeerKind == SavedMessageRef.PeerKindChannel
            && row.File.Remote.PeerId == _activeGroup.ChannelId,
        _ => true,
    };

    /// <summary>True when a row belongs in the selected storage category. No category selected ⇒ all pass.</summary>
    private bool CategoryMatches(FileRow row) =>
        _categoryFilter is not { } category || CategoryFor(row.File) == category;

    /// <summary>
    /// Recomputes the left "Storage" overview from the real index: per-type file counts and total sizes,
    /// scoped to the current Location (not the search box). If the selected type just emptied — after a
    /// delete, or a Location switch — the type filter is dropped so we never rest on a hidden bucket.
    /// Purely local: reads <see cref="_allRows"/> and contacts nothing.
    /// </summary>
    private void RefreshCategoryCounts()
    {
        var n = Enum.GetValues<FileCategory>().Length;
        var counts = new int[n];
        var bytes = new long[n];

        foreach (var row in _allRows)
        {
            if (!LocationMatches(row)) continue;
            var i = (int)CategoryFor(row.File);
            counts[i]++;
            if (row.File.SizeBytes > 0) bytes[i] += row.File.SizeBytes;
        }

        foreach (var category in _categories)
            category.Set(counts[(int)category.Category], bytes[(int)category.Category]);

        if (_categoryFilter is { } selected && counts[(int)selected] == 0)
        {
            _categoryFilter = null;
            if (CategoriesList.SelectedIndex != -1)
            {
                _syncingCategory = true;
                CategoriesList.SelectedIndex = -1;
                _syncingCategory = false;
            }
        }
    }

    /// <summary>Applies the current local sort key and direction. Never reorders <see cref="_allRows"/>.</summary>
    private IEnumerable<FileRow> SortRows(List<FileRow> rows) => _sortKey switch
    {
        "Size" => _sortAsc
            ? rows.OrderBy(r => r.File.SizeBytes)
            : rows.OrderByDescending(r => r.File.SizeBytes),
        "Uploaded" => _sortAsc
            ? rows.OrderBy(r => r.File.RemoteUploadUtc)
            : rows.OrderByDescending(r => r.File.RemoteUploadUtc),
        "Destination" => _sortAsc
            ? rows.OrderBy(r => r.Destination, StringComparer.OrdinalIgnoreCase)
            : rows.OrderByDescending(r => r.Destination, StringComparer.OrdinalIgnoreCase),
        _ => _sortAsc
            ? rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            : rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase),
    };

    private void UpdateState()
    {
        var selectedCount = SelectedCount();
        var hasSelection = selectedCount > 0;
        // Rename acts on exactly one file (batch rename stays out of scope — no safe single-message
        // equivalent). Delete works on the whole selection: one file opens the per-file dialog, several
        // open a single batch confirmation.
        var single = selectedCount == 1;

        // Show the active view (Grid or Details) when there are rows, and the empty-state panel when not.
        // A level that holds only subfolders is NOT empty — the folder strip is content too, so the empty
        // panel appears only when there are neither files nor child folders here.
        ApplyViewMode();
        var showEmpty = _rows.Count == 0 && _folderRows.Count == 0;
        EmptyStatePanel.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;

        UploadButton.IsEnabled = _storage.IsAvailable && !_busy;
        // Download works on the whole selection: one file uses the Save-as dialog, several ask for a
        // destination folder. Any non-empty selection is enough.
        DownloadButton.IsEnabled = _storage.IsAvailable && hasSelection && !_busy;
        // Rename is a purely local edit, so it does not need Saved Messages to be connected — only a
        // single selected row and no transfer in flight.
        RenameButton.IsEnabled = single && !_busy;
        // Delete is offered on any selection: the local-only option works offline; the "delete everywhere"
        // option (inside the dialog) is what needs Saved Messages, and that is guarded when the deletion
        // actually runs. A single explicit confirmation still gates every delete, batch included.
        DeleteButton.IsEnabled = hasSelection && !_busy;
        RefreshButton.IsEnabled = !_busy;

        // The clear-search affordance only matters while a filter is actually in effect.
        var searching = _searchText.Trim().Length > 0;
        ClearSearchButton.IsEnabled = searching && !_busy;

        // Create is offered whenever signed in — an account may keep as many groups as Telegram allows,
        // so an existing group no longer disables it.
        CreateGroupButton.IsEnabled = _storage.IsAvailable && _accountId > 0 && !_busy;

        // Destination radios: Saved Messages is always usable; the group only once at least one exists.
        var groupUsable = _groups.Any(g => g.LooksUsable());
        SavedMessagesRadio.IsEnabled = !_busy;
        GroupRadio.IsEnabled = groupUsable && !_busy;

        if (showEmpty)
        {
            if (searching && _allRows.Count > 0)
            {
                // Files exist for this account, but none match the current search. This is a distinct
                // state from "nothing indexed" so the user knows the filter — not an empty vault — is why
                // the list looks empty.
                EmptyStateTitle.Text = "No matching files";
                EmptyStateBody.Text =
                    $"No indexed file's name matches “{_searchText.Trim()}”. "
                    + $"Clear the search to see all {_allRows.Count} file(s).";
            }
            else if (!_storage.IsAvailable)
            {
                EmptyStateTitle.Text = "Saved Messages is not connected";
                EmptyStateBody.Text = _storage.UnavailableReason ?? "Sign in to use Saved Messages storage.";
            }
            else if (FoldersApply && _activeFolderId is not null)
            {
                // Inside an empty folder: distinct from an empty location so the user knows the folder (not
                // the whole location) is what is empty, and how to fill it.
                EmptyStateTitle.Text = "This folder is empty";
                EmptyStateBody.Text =
                    "No files or subfolders here yet. Use Upload to store a file in this folder, or "
                    + "right-click to create a subfolder.";
            }
            else
            {
                EmptyStateTitle.Text = "No indexed files yet";
                EmptyStateBody.Text =
                    "Use Upload to store a file in your Telegram Saved Messages. "
                    + "This list is a local index of files Nuvia uploaded — it is not a copy of your "
                    + "Telegram history, so files sent from other devices will not appear here.";
            }
        }

        if (!_busy)
        {
            if (searching)
            {
                // Always show the filtered count against the true total, so a small result set is never
                // mistaken for the whole vault.
                StatusText.Text =
                    $"{_rows.Count} of {_allRows.Count} file(s) match “{_searchText.Trim()}”.";
            }
            else
            {
                StatusText.Text = _allRows.Count switch
                {
                    0 => "Ready.",
                    1 => "1 indexed file.",
                    _ => $"{_allRows.Count} indexed files.",
                };
            }
        }
    }

    private FileRow? SelectedRow() =>
        (_viewMode == FileViewMode.Grid ? FileGridView.SelectedItem : FileListView.SelectedItem) as FileRow;

    /// <summary>The list control the user is currently looking at (Grid or Details).</summary>
    private ListView ActiveList() => _viewMode == FileViewMode.Grid ? FileGridView : FileListView;

    /// <summary>How many rows are selected in the active view. Cheap — reads only the selection count.</summary>
    private int SelectedCount() => ActiveList().SelectedItems.Count;

    /// <summary>
    /// Every selected row in the active view, in the order the list holds them. Used by the batch
    /// download and drag-out paths; single-file actions still use <see cref="SelectedRow"/>.
    /// </summary>
    private IReadOnlyList<FileRow> SelectedRows() =>
        ActiveList().SelectedItems.Cast<FileRow>().ToList();

    private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    // ------------------------------------------------------------- view / sort / locations

    /// <summary>Shows the active view and hides the other; both stay collapsed when the list is empty.</summary>
    private void ApplyViewMode()
    {
        var hasRows = _rows.Count > 0;
        FileGridView.Visibility = _viewMode == FileViewMode.Grid && hasRows
            ? Visibility.Visible : Visibility.Collapsed;
        FileListView.Visibility = _viewMode == FileViewMode.Details && hasRows
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Switches between large-icon and details views, carrying the current selection across so the same
    /// file stays selected. Both views are bound to the same collection, so this is pure presentation.
    /// </summary>
    private void SetViewMode(FileViewMode mode)
    {
        var current = SelectedRow();
        _viewMode = mode;

        ViewLargeIconsItem.IsChecked = mode == FileViewMode.Grid;
        ViewDetailsItem.IsChecked = mode == FileViewMode.Details;

        ApplyViewMode();

        var active = mode == FileViewMode.Grid ? (ListView)FileGridView : FileListView;
        active.SelectedItem = current;
        if (current is not null)
            active.ScrollIntoView(current);

        UpdateState();
    }

    private void ViewMenuButton_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);

    private void SortMenuButton_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);

    /// <summary>Opens a toolbar button's own ContextMenu on click, so it reads as a dropdown.</summary>
    private static void OpenButtonMenu(object sender)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void ViewModeItem_Click(object sender, RoutedEventArgs e)
    {
        var mode = (sender as MenuItem)?.Tag as string == "Details" ? FileViewMode.Details : FileViewMode.Grid;
        SetViewMode(mode);
    }

    private void GridViewToggle_Click(object sender, RoutedEventArgs e) => SetViewMode(FileViewMode.Grid);

    private void DetailsViewToggle_Click(object sender, RoutedEventArgs e) => SetViewMode(FileViewMode.Details);

    private void SortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag as string is { } key)
            SetSort(key, _sortAsc);
    }

    private void SortDirItem_Click(object sender, RoutedEventArgs e)
    {
        var asc = (sender as MenuItem)?.Tag as string == "Asc";
        SetSort(_sortKey, asc);
    }

    /// <summary>
    /// Sort by a details column header click. Clicking the active column flips direction; a new column
    /// starts ascending. Reads only the row data already loaded — no remote calls.
    /// </summary>
    private void FileDetailsHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header)
            return;
        if (header.Column?.Header as string is not { } key)
            return; // padding header or the resize gripper

        var asc = key == _sortKey ? !_sortAsc : true;
        SetSort(key, asc);
    }

    /// <summary>Applies a new sort key/direction, syncs the Sort menu check marks, and re-filters.</summary>
    private void SetSort(string key, bool ascending)
    {
        _sortKey = key;
        _sortAsc = ascending;

        SortByNameItem.IsChecked = key == "Name";
        SortBySizeItem.IsChecked = key == "Size";
        SortByDateItem.IsChecked = key == "Uploaded";
        SortAscItem.IsChecked = ascending;
        SortDescItem.IsChecked = !ascending;

        ApplyFilter(SelectedRow()?.File.Id);
    }

    private void LocationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires once during InitializeComponent as the default item is marked selected; ignore until the
        // window is initialised so we do not filter before the list has loaded. Also ignore the churn while
        // the dynamic group nodes are being rebuilt — the caller restores the right selection afterwards.
        if (!IsInitialized || _rebuildingGroupNodes) return;

        if (LocationsList.SelectedItem is System.Windows.Controls.ListBoxItem { Tag: ManagedStorageGroup group })
        {
            // A specific managed group is now the open location.
            _activeGroup = group;
            _locationFilter = LocationFilter.Group;
            LocationHeaderText.Text = string.IsNullOrWhiteSpace(group.Title) ? "Private group" : group.Title;
        }
        else if (LocationSavedItem.IsSelected)
        {
            _activeGroup = null;
            _locationFilter = LocationFilter.SavedMessages;
            LocationHeaderText.Text = "Saved Messages";
        }
        else if (LocationHomeItem.IsSelected)
        {
            // Home is the Explorer-style landing view: same "no filter" as All files.
            _activeGroup = null;
            _locationFilter = LocationFilter.All;
            LocationHeaderText.Text = "Home";
        }
        else
        {
            _activeGroup = null;
            _locationFilter = LocationFilter.All;
            LocationHeaderText.Text = "All files";
        }

        // A new Location is a fresh context: drop any active file-type sub-filter so the counts and the
        // file list both reflect the Location the user just picked.
        _categoryFilter = null;
        if (CategoriesList.SelectedIndex != -1)
        {
            _syncingCategory = true;
            CategoriesList.SelectedIndex = -1;
            _syncingCategory = false;
        }

        // The upload target follows the open location (Explorer-style), so keep the status line in step.
        UpdateUploadTargetText();

        // A new Location is a fresh folder context: drop back to its root and reload that location's folders
        // before filtering, so the strip/breadcrumb reflect the location just picked.
        _activeFolderId = null;
        RefreshFoldersInLocation();

        ApplyFilter(SelectedRow()?.File.Id);
        RecordNavigation();
    }

    /// <summary>
    /// A left "Storage" category was chosen: narrow the file list to that file type, on top of the current
    /// Location and search. Selecting nothing clears the type filter. Purely local — no remote calls.
    /// </summary>
    private void CategoriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _syncingCategory) return;
        _categoryFilter = CategoriesList.SelectedItem is CategoryRow c ? c.Category : (FileCategory?)null;
        ApplyFilter(SelectedRow()?.File.Id);
    }

    /// <summary>Clicking the already-selected category clears the type filter (toggle off), like Explorer.</summary>
    private void CategoriesList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(CategoriesList, e.OriginalSource as DependencyObject)
                is ListBoxItem { IsSelected: true })
        {
            CategoriesList.SelectedIndex = -1; // fires SelectionChanged → clears _categoryFilter
            e.Handled = true;
        }
    }

    // ---------------------------------------------------- Explorer-style navigation

    /// <summary>
    /// Records the current view — the selected Locations item plus the open folder — in the Back/Forward
    /// history, unless we are currently walking that history (Back/Forward), and refreshes the nav-button
    /// enabled states. Called on both a Location change and a folder change. View-only.
    /// </summary>
    private void RecordNavigation()
    {
        if (LocationsList.SelectedItem is not System.Windows.Controls.ListBoxItem current)
        {
            UpdateNavButtons();
            return;
        }

        if (!_navigatingHistory)
        {
            var entry = new NavEntry(current, _activeFolderId);

            // A fresh navigation drops any forward entries, just like a browser or Explorer.
            if (_navIndex < _navHistory.Count - 1)
                _navHistory.RemoveRange(_navIndex + 1, _navHistory.Count - _navIndex - 1);

            // Only push when the view actually changed (location OR folder), so re-selecting the same
            // place does not stack duplicate entries.
            if (_navIndex < 0 || !_navHistory[_navIndex].Equals(entry))
            {
                _navHistory.Add(entry);
                _navIndex = _navHistory.Count - 1;
            }
        }

        UpdateNavButtons();
    }

    private void NavigateHistory(int index)
    {
        if (index < 0 || index >= _navHistory.Count) return;

        var entry = _navHistory[index];
        _navIndex = index;
        _navigatingHistory = true;
        try
        {
            // Restore the location first. This fires LocationsList_SelectionChanged only when the location
            // actually changes — which resets the folder and reloads that location's folders — and records
            // nothing while _navigatingHistory is set. Then restore the folder within that location and
            // refresh the view once more so folder-only steps (same location) also take effect.
            if (!entry.Location.IsSelected)
                entry.Location.IsSelected = true;

            _activeFolderId = entry.FolderId;
            RefreshFoldersInLocation(); // validates the folder still exists for this location
            ApplyFilter(SelectedRow()?.File.Id);
        }
        finally { _navigatingHistory = false; }
        UpdateNavButtons();
    }

    private void NavBack_Click(object sender, RoutedEventArgs e) => NavigateHistory(_navIndex - 1);

    private void NavForward_Click(object sender, RoutedEventArgs e) => NavigateHistory(_navIndex + 1);

    private void NavUp_Click(object sender, RoutedEventArgs e)
    {
        // Inside a folder, "Up" climbs one folder level; at a location's root it climbs out to the Home
        // landing view. Home itself has no parent (the button is disabled there).
        if (FoldersApply && _activeFolderId is { } current)
        {
            EnterFolder(FolderById(current)?.ParentFolderId);
            return;
        }

        if (!LocationHomeItem.IsSelected) LocationHomeItem.IsSelected = true;
    }

    private void BreadcrumbRoot_Click(object sender, RoutedEventArgs e)
    {
        if (!LocationHomeItem.IsSelected) LocationHomeItem.IsSelected = true;
    }

    private void UpdateNavButtons()
    {
        NavBackButton.IsEnabled = _navIndex > 0;
        NavForwardButton.IsEnabled = _navIndex >= 0 && _navIndex < _navHistory.Count - 1;
        // "Up" leaves Home only out of a location, but inside a folder it climbs the folder tree too.
        NavUpButton.IsEnabled = (FoldersApply && _activeFolderId is not null) || !LocationHomeItem.IsSelected;
    }

    private void UpdateSearchPlaceholder()
    {
        if (SearchPlaceholder != null)
            SearchPlaceholder.Visibility =
                string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----------------------------------------------------------------- folders (local layer)

    /// <summary>
    /// Reloads <see cref="_foldersInLocation"/> from the index for the open location (Saved Messages or one
    /// group). Home / All files have no single location, so the set is empty there. Local read only.
    /// </summary>
    private void RefreshFoldersInLocation()
    {
        _foldersInLocation = new List<LocalFolder>();
        if (_accountId > 0 && TryGetOpenLocation(out var kind, out var peer))
        {
            foreach (var f in _index.GetFolders(_accountId, kind, peer))
                _foldersInLocation.Add(f);
        }

        // If the open folder has vanished (deleted, or we switched to a location that does not contain it),
        // fall back to the location root rather than leaving a dangling breadcrumb pointing nowhere.
        if (_activeFolderId is { } id && FolderById(id) is null)
            _activeFolderId = null;
    }

    /// <summary>
    /// The folder scope of the open location: Saved Messages ⇒ ("self", accountId); a group ⇒
    /// ("channel", channelId). False for Home / All files, which are flat.
    /// </summary>
    private bool TryGetOpenLocation(out string locationKind, out long locationPeer)
    {
        if (_locationFilter == LocationFilter.SavedMessages)
        {
            locationKind = SavedMessageRef.PeerKindSelf;
            locationPeer = _accountId;
            return true;
        }
        if (_locationFilter == LocationFilter.Group && _activeGroup is not null)
        {
            locationKind = SavedMessageRef.PeerKindChannel;
            locationPeer = _activeGroup.ChannelId;
            return true;
        }
        locationKind = string.Empty;
        locationPeer = 0;
        return false;
    }

    private LocalFolder? FolderById(long id)
    {
        foreach (var f in _foldersInLocation)
            if (f.Id == id) return f;
        return null;
    }

    /// <summary>The open-location folders from root down to <paramref name="id"/>. Empty if not found; cycle-safe.</summary>
    private List<LocalFolder> BuildFolderChain(long id)
    {
        var chain = new List<LocalFolder>();
        var seen = new HashSet<long>();
        var cursor = FolderById(id);
        while (cursor is not null && seen.Add(cursor.Id))
        {
            chain.Insert(0, cursor);
            cursor = cursor.ParentFolderId is { } p ? FolderById(p) : null;
        }
        return chain;
    }

    /// <summary>The '/'-joined path of a folder in the open location, or null if it cannot be resolved.</summary>
    private string? BuildFolderPath(long id)
    {
        var chain = BuildFolderChain(id);
        if (chain.Count == 0) return null;
        var names = new List<string>(chain.Count);
        foreach (var f in chain) names.Add(f.Name);
        return FolderNames.Join(names);
    }

    /// <summary>Opens a folder (null = location root): re-filters, rebuilds the strip/breadcrumb and nav state.</summary>
    private void EnterFolder(long? folderId)
    {
        _activeFolderId = folderId;
        ApplyFilter(SelectedRow()?.File.Id);
        // A folder change is a navigation in its own right, so Back/Forward can walk folder depth — not
        // just Locations. RecordNavigation no-ops while we are replaying history.
        RecordNavigation();
    }

    /// <summary>
    /// True when a row belongs at the current folder level. Flat locations (Home / All files) match
    /// everything; inside a location only the files directly in the open folder (or its root) match —
    /// files in subfolders are shown by entering those folders, Explorer-style.
    /// </summary>
    private bool FolderMatches(FileRow row)
        => !FoldersApply || row.File.FolderId == _activeFolderId;

    /// <summary>
    /// The folder a new in-window upload should land in: the open folder's path + id, or (null, null) at a
    /// location root or on Home / All files. The path becomes the hidden caption marker; the id tags the
    /// local row.
    /// </summary>
    private (string? Path, long? Id) ResolveCurrentUploadFolder()
    {
        if (!FoldersApply || _activeFolderId is not { } id)
            return (null, null);

        var path = BuildFolderPath(id);
        return path is null ? (null, null) : (path, id);
    }

    /// <summary>Rebuilds the folder-tile strip with the child folders of the current level. View-only.</summary>
    private void RebuildFolderStrip()
    {
        _folderRows.Clear();

        if (FoldersApply)
        {
            // _foldersInLocation is already ordered by name (COLLATE NOCASE), so siblings stay alphabetical.
            foreach (var f in _foldersInLocation)
            {
                if (f.ParentFolderId != _activeFolderId) continue;
                _folderRows.Add(new FolderRow
                {
                    Id = f.Id,
                    Name = f.Name,
                    ItemCountDisplay = DescribeFolderContents(f.Id),
                });
            }
        }

        FolderStripBorder.Visibility = _folderRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A short "N folders, M files" caption for a folder tile, counting its direct children only.</summary>
    private string DescribeFolderContents(long folderId)
    {
        var files = 0;
        foreach (var row in _allRows)
            if (row.File.FolderId == folderId) files++;

        var subfolders = 0;
        foreach (var f in _foldersInLocation)
            if (f.ParentFolderId == folderId) subfolders++;

        var parts = new List<string>(2);
        if (subfolders > 0) parts.Add(subfolders == 1 ? "1 folder" : $"{subfolders} folders");
        parts.Add(files == 1 ? "1 file" : $"{files} files");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Rebuilds the dynamic folder breadcrumb between the location name and the end of the bar. The location
    /// name (LocationHeaderText) is the first crumb; each ancestor folder is a clickable link, and the open
    /// folder is the bold tail. Cleared (and the location name made non-clickable) at a location root.
    /// </summary>
    private void RebuildBreadcrumb()
    {
        BreadcrumbFolders.Children.Clear();

        var folderOpen = FoldersApply && _activeFolderId is not null;
        LocationHeaderText.Cursor = folderOpen ? Cursors.Hand : Cursors.Arrow;
        if (!folderOpen) return;

        var chain = BuildFolderChain(_activeFolderId!.Value);
        for (var i = 0; i < chain.Count; i++)
        {
            var folder = chain[i];
            BreadcrumbFolders.Children.Add(MakeBreadcrumbChevron());

            if (i == chain.Count - 1)
            {
                var tail = new TextBlock
                {
                    Text = folder.Name,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.SemiBold,
                };
                tail.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                BreadcrumbFolders.Children.Add(tail);
            }
            else
            {
                var link = new Button
                {
                    Content = folder.Name,
                    Tag = folder.Id,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = $"Go to {folder.Name}",
                };
                link.SetResourceReference(StyleProperty, "LinkButton");
                link.Click += BreadcrumbFolder_Click;
                BreadcrumbFolders.Children.Add(link);
            }
        }
    }

    private TextBlock MakeBreadcrumbChevron()
    {
        var chevron = new TextBlock
        {
            Text = "",
            FontSize = 10,
            Margin = new Thickness(7, 1, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.SetResourceReference(StyleProperty, "IconFont");
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return chevron;
    }

    private void BreadcrumbFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long id })
            EnterFolder(id);
    }

    // ----------------------------------------------------------------- folder handlers

    /// <summary>Double-clicking a folder tile opens that folder.</summary>
    private void FolderTile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: FolderRow row })
            EnterFolder(row.Id);
    }

    private static MenuItem? FindMenuItemByTag(ContextMenu menu, string tag)
    {
        foreach (var item in menu.Items)
            if (item is MenuItem mi && (mi.Tag as string) == tag)
                return mi;
        return null;
    }

    /// <summary>Greys "New folder…" out (with a reason) when the open location is flat (Home / All files).</summary>
    private void FileAreaContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var newFolder = FindMenuItemByTag(menu, "NewFolder");
        if (newFolder is null) return;

        if (FoldersApply)
        {
            newFolder.IsEnabled = true;
            newFolder.ToolTip = null;
        }
        else
        {
            newFolder.IsEnabled = false;
            ToolTipService.SetShowOnDisabled(newFolder, true);
            newFolder.ToolTip =
                "Open Saved Messages or a group first. Folders live inside one location, so there is no "
                + "single place to create one on Home or All files.";
        }
    }

    /// <summary>Creates a folder inside the currently open folder (or the location root). Local only.</summary>
    private void NewFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!FoldersApply || !TryGetOpenLocation(out var kind, out var peer))
        {
            ShowError("Open a location first",
                "Folders live inside Saved Messages or a group. Open one in the left pane, then create a "
                + "folder there.");
            return;
        }

        var dialog = new CreateFolderWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var name = dialog.FolderName;

        // Refuse before creating if the resulting path could not be hidden in a Telegram caption (too deep or
        // too long) — a folder we could never restore after a reinstall would be a quiet trap.
        var parentPath = _activeFolderId is { } pid ? BuildFolderPath(pid) : null;
        var newPath = string.IsNullOrEmpty(parentPath) ? name : parentPath + FolderNames.Separator + name;
        if (!FolderNames.IsEncodablePath(newPath))
        {
            ShowError("Folder path too long",
                "This folder would nest too deeply (or its names are too long) for Nuvia to remember it in "
                + "Telegram, so it could not be restored after a reinstall. Use a shorter name or a shallower "
                + "folder.");
            return;
        }

        try
        {
            _index.CreateFolder(_accountId, kind, peer, _activeFolderId, name);
            RefreshFoldersInLocation();
            ApplyFilter(SelectedRow()?.File.Id);
            StatusText.Text = $"Created folder “{name}”.";
        }
        catch (DuplicateFolderNameException ex)
        {
            ShowError("Folder already exists", ex.Message);
        }
        catch (Exception ex)
        {
            ShowError("Could not create the folder", Describe(ex));
        }
    }

    private LocalFolder? FolderFromMenu(ContextMenu menu)
    {
        if (menu.PlacementTarget is FrameworkElement { DataContext: FolderRow row })
            return FolderById(row.Id);
        return null;
    }

    private LocalFolder? FolderFromSenderMenu(object sender)
        => sender is MenuItem { Parent: ContextMenu menu } ? FolderFromMenu(menu) : null;

    /// <summary>Greys "Delete folder…" out (with a reason) while the folder's tree still holds files.</summary>
    private void FolderTileContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var del = FindMenuItemByTag(menu, "DeleteFolder");
        if (del is null) return;

        var folder = FolderFromMenu(menu);
        var count = folder is not null && _accountId > 0
            ? _index.CountFilesInFolderTree(_accountId, folder.Id)
            : 0;

        if (count > 0)
        {
            del.IsEnabled = false;
            ToolTipService.SetShowOnDisabled(del, true);
            del.ToolTip =
                $"This folder (with any subfolders) still holds {count} file(s). Move or delete them first.";
        }
        else
        {
            del.IsEnabled = true;
            del.ToolTip = null;
        }
    }

    private void RenameFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (FolderFromSenderMenu(sender) is { } folder)
            _ = RenameFolderAsync(folder);
    }

    private void DeleteFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (FolderFromSenderMenu(sender) is { } folder)
            DeleteFolder(folder);
    }

    /// <summary>True when every selected row sits in one location; returns that location's (kind, peer).</summary>
    private bool TryGetRowsLocation(IReadOnlyList<FileRow> rows, out string locationKind, out long locationPeer)
    {
        locationKind = string.Empty;
        locationPeer = 0;
        if (rows.Count == 0) return false;

        var first = rows[0].File.Remote;
        locationKind = first.PeerKind;
        locationPeer = first.PeerId;

        foreach (var row in rows)
            if (row.File.Remote.PeerKind != locationKind || row.File.Remote.PeerId != locationPeer)
                return false;
        return true;
    }

    /// <summary>Appends depth-indented folder entries (one subtree) to the "Move to folder" submenu.</summary>
    private void AppendFolderMenuItems(ItemCollection target, IReadOnlyList<LocalFolder> all, long? parentFolderId, int depth)
    {
        foreach (var f in all)
        {
            if (f.ParentFolderId != parentFolderId) continue;

            var indent = depth > 0 ? new string(' ', depth * 4) : string.Empty;
            var item = new MenuItem { Header = indent + f.Name, Tag = f.Id };
            item.Click += MoveToFolderTarget_Click;
            target.Add(item);

            AppendFolderMenuItems(target, all, f.Id, depth + 1);
        }
    }

    /// <summary>
    /// Builds the "Move to folder ▸" submenu for the current selection: the files' OWN location root plus
    /// every folder of that location. Disabled with a reason when the selection spans more than one
    /// location. Works from Home / All files too, since the targets come from the file's location.
    /// </summary>
    private void FileItemContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var move = FindMenuItemByTag(menu, "MoveToFolder");
        if (move is null) return;

        move.Items.Clear();

        var rows = SelectedRows();
        if (rows.Count == 0)
        {
            move.IsEnabled = false;
            ToolTipService.SetShowOnDisabled(move, true);
            move.ToolTip = "Select one or more files first.";
            return;
        }

        if (!TryGetRowsLocation(rows, out var kind, out var peer))
        {
            move.IsEnabled = false;
            ToolTipService.SetShowOnDisabled(move, true);
            move.ToolTip =
                "The selected files are in more than one location. Select files from a single location "
                + "(one group, or Saved Messages) to move them into a folder.";
            return;
        }

        move.IsEnabled = true;
        move.ToolTip = null;

        var root = new MenuItem { Header = "Location root" }; // null Tag = root
        root.Click += MoveToFolderTarget_Click;
        move.Items.Add(root);
        move.Items.Add(new Separator());

        var folders = _accountId > 0
            ? _index.GetFolders(_accountId, kind, peer)
            : (IReadOnlyList<LocalFolder>)Array.Empty<LocalFolder>();
        AppendFolderMenuItems(move.Items, folders, parentFolderId: null, depth: 0);

        if (folders.Count == 0)
            move.Items.Add(new MenuItem { Header = "(no folders in this location yet)", IsEnabled = false });
    }

    private void MoveToFolderTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        long? target = item.Tag is long id ? id : (long?)null;
        _ = MoveSelectedToFolderAsync(target);
    }

    // ----------------------------------------------------------------- folder operations

    /// <summary>The '/'-joined path of <paramref name="id"/> within an arbitrary folder set (any location).</summary>
    private static string? BuildFolderPathIn(IReadOnlyList<LocalFolder> all, long id)
    {
        var byId = new Dictionary<long, LocalFolder>();
        foreach (var f in all) byId[f.Id] = f;

        var names = new List<string>();
        var seen = new HashSet<long>();
        long? cursor = id;
        while (cursor is { } c && byId.TryGetValue(c, out var folder) && seen.Add(c))
        {
            names.Insert(0, folder.Name);
            cursor = folder.ParentFolderId;
        }
        return names.Count == 0 ? null : FolderNames.Join(names);
    }

    /// <summary>Ids of a folder plus all its descendants within the open location. Cycle-safe.</summary>
    private List<long> CollectSubtree(long rootId)
    {
        var result = new List<long> { rootId };
        var queue = new Queue<long>();
        queue.Enqueue(rootId);
        var seen = new HashSet<long> { rootId };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var f in _foldersInLocation)
                if (f.ParentFolderId == current && seen.Add(f.Id))
                {
                    result.Add(f.Id);
                    queue.Enqueue(f.Id);
                }
        }
        return result;
    }

    /// <summary>A folder's path using overridden names where present (for a pre-rename budget check).</summary>
    private string? BuildRenamedPath(long id, IReadOnlyDictionary<long, string> renamed)
    {
        var names = new List<string>();
        var seen = new HashSet<long>();
        var cursor = FolderById(id);
        while (cursor is not null && seen.Add(cursor.Id))
        {
            names.Insert(0, renamed.TryGetValue(cursor.Id, out var nm) ? nm : cursor.Name);
            cursor = cursor.ParentFolderId is { } p ? FolderById(p) : null;
        }
        return names.Count == 0 ? null : FolderNames.Join(names);
    }

    private bool IsDescendantOf(long candidateId, long ancestorId)
    {
        var seen = new HashSet<long>();
        var cursor = FolderById(candidateId);
        while (cursor?.ParentFolderId is { } parent && seen.Add(cursor.Id))
        {
            if (parent == ancestorId) return true;
            cursor = FolderById(parent);
        }
        return false;
    }

    /// <summary>
    /// Moves the selected files into <paramref name="targetFolderId"/> (null = location root): a local index
    /// move first, then — for Nuvia's OWN uploads only — a hidden caption-marker edit so the new placement
    /// survives a reinstall. External files are moved locally but never tagged (writing Nuvia's marker onto a
    /// file added outside Nuvia would make a reinstall wrongly claim it). The status line reports exactly
    /// what happened, including partial or skipped marker updates.
    /// </summary>
    private async Task MoveSelectedToFolderAsync(long? targetFolderId)
    {
        if (_busy) return;

        var rows = SelectedRows();
        if (rows.Count == 0)
        {
            ShowError("Nothing selected", "Select one or more files first, then move them into a folder.");
            return;
        }

        if (!TryGetRowsLocation(rows, out var kind, out var peer))
        {
            ShowError("Files span more than one location",
                "Move works within a single location. Select files from one group (or Saved Messages) at a "
                + "time.");
            return;
        }

        // Resolve the target folder's path (null = location root) from the files' OWN location.
        string? targetPath = null;
        if (targetFolderId is { } tid)
        {
            var locationFolders = _index.GetFolders(_accountId, kind, peer);
            targetPath = BuildFolderPathIn(locationFolders, tid);
            if (targetPath is null)
            {
                ShowError("Folder not found",
                    "That folder is no longer in the local index. Refresh and try again.");
                return;
            }
            if (!FolderNames.IsEncodablePath(targetPath))
            {
                ShowError("Folder path too long",
                    "This folder path is too long for Nuvia to remember in Telegram, so files moved here "
                    + "could not be restored after a reinstall. Use a shallower folder.");
                return;
            }
        }

        // 1) Local move first — fast, offline, and reversible. This never touches Telegram.
        int moved;
        var ids = new List<long>(rows.Count);
        foreach (var r in rows) ids.Add(r.File.Id);
        try
        {
            moved = _index.SetFilesFolder(_accountId, ids, kind, peer, targetFolderId);
        }
        catch (Exception ex)
        {
            ShowError("Could not move the files", Describe(ex));
            return;
        }

        // 2) Update the hidden marker on Nuvia's own uploads only.
        var nuviaRows = new List<FileRow>();
        foreach (var r in rows)
            if (string.Equals(r.File.Source, IndexStore.SourceNuvia, StringComparison.Ordinal))
                nuviaRows.Add(r);
        var externalCount = rows.Count - nuviaRows.Count;

        var updated = 0;
        var remoteSkipped = false;
        if (nuviaRows.Count > 0)
        {
            // The move stays within one location, so the channel group (if any) resolves once up front.
            var isChannel = string.Equals(kind, SavedMessageRef.PeerKindChannel, StringComparison.Ordinal);
            var channelGroup = isChannel ? GroupForChannel(peer) : null;
            var reachable = _storage.IsAvailable && (!isChannel || channelGroup is not null);

            if (!reachable)
            {
                // The local move already happened; the marker simply cannot be refreshed right now. Say so —
                // a reinstall before it catches up would restore these files to their previous folder.
                remoteSkipped = true;
            }
            else
            {
                _transferCts = new CancellationTokenSource();
                try
                {
                    for (var i = 0; i < nuviaRows.Count; i++)
                    {
                        if (_transferCts.IsCancellationRequested) break;

                        var row = nuviaRows[i];
                        SetBusy($"Updating folder marker {i + 1} of {nuviaRows.Count}: {row.File.DisplayName}",
                            cancellable: true, indeterminate: true);
                        try
                        {
                            await _storage.UpdateFolderMarkerAsync(
                                row.File.Remote, channelGroup, targetPath, _transferCts.Token);
                            updated++;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch
                        {
                            // Best-effort: a marker that will not update is reported in the count, never thrown.
                            // The file is already in the right folder locally.
                        }
                    }
                }
                finally
                {
                    ClearBusy();
                }
            }
        }

        ReloadList();

        // Status is built last: ReloadList (via UpdateState) rewrites StatusText, so set ours afterwards.
        var status = new System.Text.StringBuilder();
        var where = targetPath is null ? "the location root" : $"“{targetPath}”";
        status.Append($"Moved {moved} file{(moved == 1 ? "" : "s")} to {where}.");
        if (nuviaRows.Count > 0)
        {
            if (remoteSkipped)
                status.Append(" Could not update the Telegram folder markers right now (not connected), so a "
                    + "reinstall may restore these to their previous folder until the markers catch up.");
            else
                status.Append($" Updated {updated} of {nuviaRows.Count} folder marker"
                    + $"{(nuviaRows.Count == 1 ? "" : "s")} on Telegram.");
        }
        if (externalCount > 0)
            status.Append($" {externalCount} external file{(externalCount == 1 ? " was" : "s were")} moved in "
                + "Nuvia only — files added outside Nuvia are not tagged on Telegram.");

        StatusText.Text = status.ToString();
    }

    /// <summary>
    /// Renames a folder, then updates the hidden caption marker on every Nuvia upload at or under it, so a
    /// reinstall restores the new path. Best-effort on the markers: the local rename always sticks; each
    /// marker that will not update is counted and reported, never silently dropped.
    /// </summary>
    private async Task RenameFolderAsync(LocalFolder folder)
    {
        if (_busy) return;

        var dialog = new CreateFolderWindow(folder.Name) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var newName = dialog.FolderName;
        if (string.Equals(newName, folder.Name, StringComparison.Ordinal))
            return; // Nothing actually changed.

        // Pre-check the whole subtree's new paths against the marker budget before touching anything, so a
        // too-deep rename is refused up front rather than leaving some markers updated and others not.
        var subtree = CollectSubtree(folder.Id);
        var renamed = new Dictionary<long, string> { [folder.Id] = newName };
        foreach (var id in subtree)
        {
            var preview = BuildRenamedPath(id, renamed);
            if (preview is not null && !FolderNames.IsEncodablePath(preview))
            {
                ShowError("Folder name too long",
                    "Renaming to this would make a folder path too long for Nuvia to remember in Telegram. "
                    + "Choose a shorter name.");
                return;
            }
        }

        // 1) Local rename first.
        try
        {
            if (!_index.RenameFolder(_accountId, folder.Id, newName))
            {
                ShowError("Folder not found",
                    "That folder is no longer in the local index. Refresh and try again.");
                return;
            }
        }
        catch (DuplicateFolderNameException ex)
        {
            ShowError("Name already used", ex.Message);
            return;
        }
        catch (Exception ex)
        {
            ShowError("Could not rename the folder", Describe(ex));
            return;
        }

        RefreshFoldersInLocation();

        // 2) Update the marker on every Nuvia upload at or under the renamed folder; paths now reflect the
        // new name. Folder ids are location-scoped, so filtering _allRows by the subtree ids is enough.
        var subtreeSet = new HashSet<long>(subtree);
        var isChannel = string.Equals(
            folder.LocationKind, SavedMessageRef.PeerKindChannel, StringComparison.Ordinal);
        var channelGroup = isChannel ? GroupForChannel(folder.LocationPeerId) : null;
        var reachable = _storage.IsAvailable && (!isChannel || channelGroup is not null);

        var nuviaRows = _allRows.FindAll(r =>
            r.File.FolderId is { } fid && subtreeSet.Contains(fid)
            && string.Equals(r.File.Source, IndexStore.SourceNuvia, StringComparison.Ordinal));

        var updated = 0;
        var remoteSkipped = false;
        if (nuviaRows.Count > 0 && !reachable)
        {
            remoteSkipped = true;
        }
        else if (nuviaRows.Count > 0)
        {
            _transferCts = new CancellationTokenSource();
            try
            {
                for (var i = 0; i < nuviaRows.Count; i++)
                {
                    if (_transferCts.IsCancellationRequested) break;

                    var row = nuviaRows[i];
                    var path = row.File.FolderId is { } fid ? BuildFolderPath(fid) : null;
                    SetBusy($"Updating folder marker {i + 1} of {nuviaRows.Count}: {row.File.DisplayName}",
                        cancellable: true, indeterminate: true);
                    try
                    {
                        await _storage.UpdateFolderMarkerAsync(
                            row.File.Remote, channelGroup, path, _transferCts.Token);
                        updated++;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Best-effort: a marker that will not update is reported in the count, never thrown.
                    }
                }
            }
            finally
            {
                ClearBusy();
            }
        }

        ReloadList();

        // Status last: ReloadList (via UpdateState) rewrites StatusText, so set ours afterwards.
        var status = new System.Text.StringBuilder();
        status.Append($"Renamed the folder to “{newName}”.");
        if (nuviaRows.Count > 0)
        {
            if (remoteSkipped)
                status.Append(" Could not update the Telegram folder markers right now (not connected), so a "
                    + "reinstall may restore these files under the old name until the markers catch up.");
            else
                status.Append($" Updated {updated} of {nuviaRows.Count} folder marker"
                    + $"{(nuviaRows.Count == 1 ? "" : "s")} on Telegram.");
        }
        StatusText.Text = status.ToString();
    }

    /// <summary>
    /// Deletes an empty folder subtree — local only, as folders never existed on Telegram. Refuses while any
    /// file remains anywhere under it, so a delete can never orphan a file row.
    /// </summary>
    private void DeleteFolder(LocalFolder folder)
    {
        if (_busy) return;

        int fileCount;
        try
        {
            fileCount = _index.CountFilesInFolderTree(_accountId, folder.Id);
        }
        catch (Exception ex)
        {
            ShowError("Could not check the folder", Describe(ex));
            return;
        }

        if (fileCount > 0)
        {
            ShowError("Folder is not empty",
                $"“{folder.Name}” still holds {fileCount} file{(fileCount == 1 ? "" : "s")} "
                + "(counting any subfolders). Move or delete those files first, then delete the folder.");
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Delete the folder “{folder.Name}”? This removes it from Nuvia only — nothing on Telegram "
            + "changes, and the folder is empty, so no files are affected.",
            "Delete folder", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK)
            return;

        int removed;
        try
        {
            removed = _index.DeleteFolderTree(_accountId, folder.Id);
        }
        catch (Exception ex)
        {
            ShowError("Could not delete the folder", Describe(ex));
            return;
        }

        // If the open folder was the one deleted (or sat under it), step out to its parent before refreshing
        // so the view never points at a folder that no longer exists.
        if (_activeFolderId is { } active && (active == folder.Id || IsDescendantOf(active, folder.Id)))
            _activeFolderId = folder.ParentFolderId;

        RefreshFoldersInLocation();
        ApplyFilter(SelectedRow()?.File.Id);
        StatusText.Text = removed <= 1
            ? $"Deleted the folder “{folder.Name}”."
            : $"Deleted “{folder.Name}” and {removed - 1} subfolder{(removed - 1 == 1 ? "" : "s")}.";
    }

    // ----------------------------------------------------------------- upload

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!_storage.IsAvailable)
        {
            ShowError("Saved Messages is not connected", _storage.UnavailableReason ?? "Sign in first.");
            return;
        }

        // Native picker, now allowing several files, and only ever reached from an explicit Upload click.
        var dialog = new OpenFileDialog
        {
            Title = "Choose files to store in Saved Messages",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = true,
            DereferenceLinks = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        await UploadFilesAsync(dialog.FileNames);
    }

    /// <summary>
    /// Uploads one or more local files to the current destination, one at a time, reusing the single
    /// transfer coordinator. Folders and vanished paths are skipped (and reported) — scope never widens
    /// to a recursive folder upload. A lone real file keeps the original single-file experience verbatim;
    /// several files run as a batch with one progress window and a single end-of-run summary.
    /// </summary>
    private async Task UploadFilesAsync(IReadOnlyList<string> paths)
    {
        if (_busy) return;

        if (!_storage.IsAvailable)
        {
            ShowError("Saved Messages is not connected", _storage.UnavailableReason ?? "Sign in first.");
            return;
        }

        // Only real files are uploaded; folders and missing paths are counted so the summary can say so.
        var files = new List<string>();
        var skippedFolders = 0;
        var skippedMissing = 0;
        foreach (var p in paths)
        {
            if (File.Exists(p)) files.Add(p);
            else if (Directory.Exists(p)) skippedFolders++;
            else skippedMissing++;
        }

        if (files.Count == 0)
        {
            ShowError("Nothing to upload",
                skippedFolders > 0
                    ? "Folders can’t be uploaded. Pick or drop individual files instead."
                    : "None of the selected items are files that still exist.");
            return;
        }

        // A single clean file behaves exactly as before (rich not-indexed dialog and all).
        if (files.Count == 1 && skippedFolders == 0 && skippedMissing == 0)
        {
            await UploadOneAsync(files[0]);
            return;
        }

        var destination = ResolveCurrentDestination();
        // The whole batch lands in whatever folder is open inside the current location (null = root). Home /
        // All files have no single location, so folder resolution returns root there.
        var (folderPath, folderId) = ResolveCurrentUploadFolder();
        var results = new List<BatchItemResult>(files.Count);
        long? lastIndexedId = null;
        var cancelled = false;

        // One cancellation source for the whole batch: Cancel stops the current file and the rest.
        _transferCts = new CancellationTokenSource();
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var path = files[i];
                var name = Path.GetFileName(path);
                // Preserve the "x of N" context across the per-file snapshots (see OnTransferSnapshot).
                _batchHeader = $"Uploading {i + 1} of {files.Count} to {destination.DisplayName}: {name}";
                SetBusy(_batchHeader);

                try
                {
                    var result = await _transfers.UploadAsync(path, _accountId, destination, null, _transferCts.Token, folderPath, folderId);
                    if (result.Outcome == UploadOutcome.Indexed)
                    {
                        lastIndexedId = result.Row?.Id ?? lastIndexedId;
                        results.Add(new BatchItemResult(name, ItemOutcome.Done, null));
                    }
                    else
                    {
                        results.Add(new BatchItemResult(name, ItemOutcome.DoneNotIndexed, result.IndexError));
                    }
                }
                catch (OperationCanceledException)
                {
                    results.Add(new BatchItemResult(name, ItemOutcome.Cancelled, null));
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    results.Add(new BatchItemResult(name, ItemOutcome.Failed, Describe(ex)));
                }
            }
        }
        finally
        {
            _batchHeader = null;
            ClearBusy();
        }

        ReloadList(lastIndexedId);
        ReportBatch("Upload", results, files.Count, cancelled, skippedFolders, skippedMissing);
    }

    /// <summary>The original single-file upload flow, kept intact for the one-file case.</summary>
    private async Task UploadOneAsync(string path, StorageDestination? destinationOverride = null)
    {
        var name = Path.GetFileName(path);

        // Resolve the destination at click time. If the chosen group has gone missing since it was
        // selected, fall back to Saved Messages rather than failing — and never recreate the group.
        // A shell-launched quick upload passes an explicit destination override, so it uploads exactly
        // where the user picked in the right-click menu regardless of the window's current selector.
        var destination = destinationOverride ?? ResolveCurrentDestination();

        // A shell-launched quick upload (explicit override) always lands at the location root — the shell
        // path never has a folder context. An in-window upload follows the open folder.
        var (folderPath, folderId) = destinationOverride is null ? ResolveCurrentUploadFolder() : (null, null);

        UploadResult result;
        try
        {
            SetBusy($"Uploading {name} to {destination.DisplayName}…");

            // The byte-level progress is not forwarded to the storage layer: the coordinator owns the
            // single reporting channel, so every reading the UI shows comes from one place. Passing null
            // for the per-call progress keeps that single channel authoritative.
            _transferCts = new CancellationTokenSource();
            result = await _transfers.UploadAsync(path, _accountId, destination, null, _transferCts.Token, folderPath, folderId);
        }
        catch (OperationCanceledException)
        {
            // The user cancelled — that is a normal outcome, not a failure. No error dialog: just note it.
            StatusText.Text = "Upload cancelled.";
            return;
        }
        catch (Exception ex)
        {
            ShowError("Upload failed",
                $"{name} was not uploaded to {destination.DisplayName}.\n\n{Describe(ex)}\n\n"
                + "Nothing was added to the local index.");
            return;
        }
        finally
        {
            ClearBusy();
        }

        if (result.Outcome == UploadOutcome.Indexed)
        {
            ReloadList(result.Row?.Id);
            StatusText.Text = $"Uploaded {name} to {destination.DisplayName}.";
            return;
        }

        // Remote succeeded, local indexing did not. Be explicit, and never re-upload.
        ReloadList();
        MessageBox.Show(this,
            $"{name} was uploaded to your Telegram {destination.DisplayName}, but Nuvia could not add it "
            + "to the local index.\n\n"
            + "• The file may already exist remotely. Do not upload it again — that would store a "
            + "second copy.\n"
            + "• It just will not appear in this list until the index can be written.\n\n"
            + $"Index error: {result.IndexError}\n\n"
            + $"Index file: {_index.DatabasePath}",
            "Uploaded, but not indexed",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        StatusText.Text = $"{name} is in {destination.DisplayName} but is not indexed locally.";
    }

    // --------------------------------------------------------------- quick upload (shell menu, warm path)

    /// <summary>
    /// Handles a shell-launched quick-upload request that was routed to the already-open main window
    /// (the process that owns the Telegram session). It reuses this window's single transfer coordinator,
    /// so exactly one transfer runs at a time; a request arriving while another transfer is in flight is
    /// politely refused rather than starting a concurrent one. An activate-only request just brings the
    /// window to the front. This performs no fake success and never widens scope to a folder.
    /// </summary>
    public async void QuickUpload(QuickUploadRequest request)
    {
        if (request is null)
            return;

        SurfaceSelf();

        if (request.IsActivateOnly)
            return;

        if (_busy)
        {
            MessageBox.Show(this,
                "Nuvia is already uploading a file. Please wait for it to finish, then try again.",
                "Nuvia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = request.Path;

        // Only ever a single existing file. A folder must never widen into a recursive upload.
        if (Directory.Exists(path))
        {
            ShowError("Folders can’t be uploaded",
                "Nuvia uploads single files. Uploading a whole folder isn’t supported.");
            return;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ShowError("File not found",
                "Nuvia couldn’t find the file to upload. It may have been moved, renamed, or deleted.");
            return;
        }

        if (!_storage.IsAvailable)
        {
            ShowError("Not connected", _storage.UnavailableReason ?? "Sign in first, then try the upload again.");
            return;
        }

        // Resolve the destination from the coarse hint, showing the chooser and/or the create-group
        // confirmation exactly like the standalone path (same planner, same one-group-per-account rule).
        var plan = QuickUploadDestinationPlanner.PlanDestination(
            this, path, request.To, _accountId, _index, _storage);

        switch (plan)
        {
            case UseSavedMessages:
                await UploadOneAsync(path, StorageDestination.SavedMessages);
                break;

            case UseExistingGroup existing:
                await UploadOneAsync(path, StorageDestination.ForGroup(existing.Group));
                break;

            case CreateNewGroup create:
                var created = await CreateGroupForQuickUploadAsync(create.Title);
                if (created is null)
                    return; // creation failed — already reported; nothing uploaded.
                await UploadOneAsync(path, StorageDestination.ForGroup(created));
                break;

            case CancelQuickUpload:
            default:
                return; // user backed out.
        }
    }

    /// <summary>
    /// Creates a new managed private group for a quick upload whose destination the user already
    /// confirmed in the planner's dialog. Mirrors <see cref="CreateGroupButton_Click"/> minus the
    /// confirmation prompt: it persists identifiers only after the remote create succeeds, updates the
    /// selector, and returns the group (or null, having reported the failure).
    /// </summary>
    private async Task<ManagedStorageGroup?> CreateGroupForQuickUploadAsync(string title)
    {
        try
        {
            // A single short RPC, not a cancellable byte transfer: no Cancel button, indeterminate bar.
            SetBusy($"Creating private storage group \"{title}\"…", cancellable: false, indeterminate: true);

            var group = await _storage.CreatePrivateStorageGroupAsync(title, CancellationToken.None);

            _index.SaveStorageGroup(group);
            _groups.RemoveAll(g => g.ChannelId == group.ChannelId);
            _groups.Add(group);
            RebuildDestinationSelector();
            return group;
        }
        catch (Exception ex)
        {
            ShowError("Could not create the group",
                $"The private storage group was not created, so nothing was uploaded.\n\n{Describe(ex)}\n\n"
                + "Nothing was changed on your account.");
            return null;
        }
        finally
        {
            ClearBusy();
        }
    }

    /// <summary>Brings this window to the front for a shell-launched request arriving from a second process.</summary>
    private void SurfaceSelf()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        if (!IsVisible)
            Show();
        Activate();
    }

    // --------------------------------------------------------------- download

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var rows = SelectedRows();
        if (rows.Count == 0)
        {
            ShowError("Nothing selected", "Select one or more files in the list first.");
            return;
        }

        if (!_storage.IsAvailable)
        {
            ShowError("Saved Messages is not connected", _storage.UnavailableReason ?? "Sign in first.");
            return;
        }

        // One file keeps the familiar Save-as dialog (choose exact name + overwrite prompt); several files
        // ask once for a destination folder and save each with a safe, de-duplicated name.
        if (rows.Count == 1)
            await DownloadOneAsync(rows[0]);
        else
            await DownloadManyAsync(rows);
    }

    /// <summary>The original single-file download flow: Save-as dialog, one overwrite prompt, kept intact.</summary>
    private async Task DownloadOneAsync(FileRow row)
    {
        // The suggested save name comes from the row's display name (which the user may have renamed)
        // but is sanitised separately here: the rename dialog only validates empty/length, never
        // filesystem safety, so an arbitrary label is never trusted as a path. If nothing usable
        // survives, fall back to the original filename, then to a generic name.
        var suggested = FileNameSafety.ToSafeFileName(
            row.File.DisplayName,
            FileNameSafety.ToSafeFileName(row.File.OriginalFileName, "nuvia-download"));

        var dialog = new SaveFileDialog
        {
            Title = "Save file from Saved Messages",
            FileName = suggested,
            DefaultExt = Path.GetExtension(suggested),
            Filter = "All files (*.*)|*.*",
            AddExtension = false,
            OverwritePrompt = false, // Nuvia asks once itself, right before the file is moved into place
            InitialDirectory = SafeDownloadDirectory(),
        };

        if (dialog.ShowDialog(this) != true)
            return;

        var destination = dialog.FileName;

        try
        {
            SetBusy($"Downloading {row.File.DisplayName}…");

            // As with upload: one reporting channel only — the coordinator's snapshots.
            _transferCts = new CancellationTokenSource();
            var result = await _transfers.DownloadAsync(
                row.File,
                destination,
                null,
                ConfirmOverwrite,
                _transferCts.Token);

            if (!result.Saved)
            {
                StatusText.Text = "Download cancelled.";
                return;
            }

            if (result.RefreshedReference)
                ReloadList(row.File.Id);

            StatusText.Text = result.RefreshedReference
                ? $"Saved to {destination} (remote file reference refreshed)."
                : $"Saved to {destination}";
        }
        catch (OperationCanceledException)
        {
            // The user cancelled — a normal outcome, not a failure. No error dialog.
            StatusText.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            ShowError("Download failed",
                $"{row.File.DisplayName} was not saved.\n\n{Describe(ex)}\n\n"
                + "Any partial file was removed; the existing file (if any) was left untouched.");
        }
        finally
        {
            ClearBusy();
        }
    }

    /// <summary>Asks once for a destination folder, then saves every selected file into it.</summary>
    private async Task DownloadManyAsync(IReadOnlyList<FileRow> rows)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Choose a folder to save the selected files",
            InitialDirectory = SafeDownloadDirectory(),
            Multiselect = false,
        };

        if (picker.ShowDialog(this) != true)
            return;

        await DownloadRowsAsync(rows, picker.FolderName);
    }

    /// <summary>
    /// Saves several files into one folder, one at a time, reusing the single transfer coordinator. Each
    /// file gets a safe, unique name (de-duplicated against both the folder's existing files and the
    /// other files in this batch), so nothing is silently overwritten and no per-file prompt is needed.
    /// </summary>
    private async Task DownloadRowsAsync(IReadOnlyList<FileRow> rows, string folder)
    {
        var results = new List<BatchItemResult>(rows.Count);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cancelled = false;
        var refreshedAny = false;

        _transferCts = new CancellationTokenSource();
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var display = row.File.DisplayName;
                _batchHeader = $"Downloading {i + 1} of {rows.Count}: {display}";
                SetBusy(_batchHeader);

                var safeName = FileNameSafety.ToSafeFileName(
                    row.File.DisplayName,
                    FileNameSafety.ToSafeFileName(row.File.OriginalFileName, "nuvia-download"));
                var target = UniqueInFolder(folder, safeName, usedNames);

                try
                {
                    // A unique name means the target does not exist, so confirmOverwrite is never invoked;
                    // "keep both" (return false) is a safe default if a same-named file appears mid-save.
                    var result = await _transfers.DownloadAsync(row.File, target, null, _ => false, _transferCts.Token);
                    if (result.Saved)
                    {
                        refreshedAny |= result.RefreshedReference;
                        results.Add(new BatchItemResult(display, ItemOutcome.Done, null));
                    }
                    else
                    {
                        results.Add(new BatchItemResult(display, ItemOutcome.Failed,
                            "A file with that name already existed and was left untouched."));
                    }
                }
                catch (OperationCanceledException)
                {
                    results.Add(new BatchItemResult(display, ItemOutcome.Cancelled, null));
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    results.Add(new BatchItemResult(display, ItemOutcome.Failed, Describe(ex)));
                }
            }
        }
        finally
        {
            _batchHeader = null;
            ClearBusy();
        }

        if (refreshedAny)
            ReloadList(SelectedRow()?.File.Id);

        ReportBatch("Download", results, rows.Count, cancelled, 0, 0);
    }

    /// <summary>
    /// Builds a full path inside <paramref name="folder"/> that collides with neither an existing file
    /// nor another name already handed out in this batch, appending " (2)", " (3)", … before the
    /// extension until it is free. The chosen name is recorded in <paramref name="used"/>.
    /// </summary>
    private static string UniqueInFolder(string folder, string fileName, HashSet<string> used)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = fileName;
        var n = 2;
        while (used.Contains(candidate) || File.Exists(Path.Combine(folder, candidate)))
        {
            candidate = $"{baseName} ({n}){ext}";
            n++;
        }

        used.Add(candidate);
        return Path.Combine(folder, candidate);
    }

    /// <summary>
    /// Asked only if the destination already exists at the moment the finished download is moved
    /// into place.
    /// </summary>
    private bool ConfirmOverwrite(string destination)
    {
        var answer = MessageBox.Show(this,
            $"{Path.GetFileName(destination)} already exists in that folder.\n\n"
            + "Replace it with the file from Saved Messages?",
            "Replace existing file?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }

    // ------------------------------------------------------------- batch results

    /// <summary>How one file in a batch ended up.</summary>
    private enum ItemOutcome { Done, DoneNotIndexed, Failed, Cancelled }

    /// <summary>The outcome of one file in a batch transfer, for the end-of-run summary.</summary>
    private sealed record BatchItemResult(string Name, ItemOutcome Outcome, string? Error);

    /// <summary>
    /// Reports the outcome of a batch once, rather than a modal per file: a one-line status-bar summary
    /// always, plus a single themed dialog only when something actually went wrong (failures or
    /// stored-but-not-indexed). A clean run stays quiet in the status bar.
    /// </summary>
    private void ReportBatch(string verb, IReadOnlyList<BatchItemResult> results, int total,
        bool cancelled, int skippedFolders, int skippedMissing)
    {
        var done = results.Count(r => r.Outcome == ItemOutcome.Done);
        var notIndexed = results.Count(r => r.Outcome == ItemOutcome.DoneNotIndexed);
        var failed = results.Count(r => r.Outcome == ItemOutcome.Failed);

        var pastVerb = verb switch
        {
            "Upload" => "uploaded",
            "Deletion" => "deleted",
            "Removal" => "removed",
            _ => "saved",
        };
        var parts = new List<string>();
        if (done > 0) parts.Add($"{done} {pastVerb}");
        if (notIndexed > 0) parts.Add($"{notIndexed} stored but not indexed");
        if (failed > 0) parts.Add($"{failed} failed");
        if (skippedFolders > 0) parts.Add($"{skippedFolders} folder(s) skipped");
        if (skippedMissing > 0) parts.Add($"{skippedMissing} missing");
        if (cancelled)
        {
            var notStarted = Math.Max(0, total - results.Count);
            parts.Add(notStarted > 0 ? $"cancelled ({notStarted} not started)" : "cancelled");
        }

        StatusText.Text = parts.Count > 0
            ? $"{verb}: {string.Join(", ", parts)}."
            : $"{verb}: nothing to do.";

        // Only real problems get a dialog; a clean run is left to the status bar.
        var problems = results
            .Where(r => r.Outcome is ItemOutcome.Failed or ItemOutcome.DoneNotIndexed)
            .Select(r => (r.Name, Reason: r.Error
                ?? (r.Outcome == ItemOutcome.DoneNotIndexed
                    ? "Stored in Telegram but could not be added to the local index."
                    : "Failed.")))
            .ToList();

        if (problems.Count > 0)
        {
            new TransferSummaryWindow(this, $"{verb} finished with issues", StatusText.Text, problems)
                .ShowDialog();
        }
    }

    // ------------------------------------------------------------- drag-in upload

    /// <summary>True when the dragged payload is droppable right now: real files, storage up, not busy.</summary>
    private bool CanAcceptFileDrop(DragEventArgs e) =>
        !_busy && _storage.IsAvailable && e.Data.GetDataPresent(DataFormats.FileDrop);

    private void FileArea_DragOver(object sender, DragEventArgs e)
    {
        if (CanAcceptFileDrop(e))
        {
            e.Effects = DragDropEffects.Copy;
            DropHintText.Text = $"Drop to upload to {ResolveCurrentDestination().DisplayName}";
            DropHintOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            DropHintOverlay.Visibility = Visibility.Collapsed;
        }

        e.Handled = true;
    }

    private void FileArea_DragLeave(object sender, DragEventArgs e)
    {
        DropHintOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private async void FileArea_Drop(object sender, DragEventArgs e)
    {
        DropHintOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (!CanAcceptFileDrop(e))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            return;

        // UploadFilesAsync filters folders/missing itself and reports them — the same path the picker uses.
        await UploadFilesAsync(paths);
    }

    // ------------------------------------------------------------- drag-out download

    /// <summary>
    /// Called by <see cref="MarqueeSelector"/> when the user drags a selected row out of the list. Offers the
    /// selected files to Explorer (or any drop target) as virtual files: nothing is downloaded until the user
    /// actually drops, and then each file's bytes are fetched on demand by <see cref="DownloadForDragOut"/>.
    /// This blocks (runs the OLE drag loop) until the drop or cancel, matching how <c>DoDragDrop</c> works.
    /// </summary>
    private void BeginItemDragOut(ListView list)
    {
        // Guard the same conditions as a normal download: real files selected, storage up, nothing else busy.
        if (_busy || !_storage.IsAvailable)
            return;

        var rows = list.SelectedItems.Cast<FileRow>().ToList();
        if (rows.Count == 0)
            return;

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = rows.Select(row =>
        {
            var safe = FileNameSafety.ToSafeFileName(
                row.File.DisplayName,
                FileNameSafety.ToSafeFileName(row.File.OriginalFileName, "nuvia-download"));
            safe = UniqueName(safe, usedNames);

            return new VirtualFileDataObject.FileItem
            {
                Name = safe,
                Length = row.File.SizeBytes > 0 ? row.File.SizeBytes : null,
                WriteContents = target => DownloadForDragOut(row, target),
            };
        }).ToList();

        // Suppress the shared snapshot channel's UI churn while the drag loop owns the thread; restore a clean
        // status afterwards at Background priority, so any snapshots queued during the drag drain out first.
        _dragOutActive = true;
        try
        {
            VirtualFileDataObject.StartDrag(items, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            // Drag-out is the most fragile gesture (COM virtual files); never let it take the app down.
            Debug.WriteLine($"Drag-out failed: {ex}");
        }
        finally
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _dragOutActive = false;
                UpdateState();
            }), DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Fetches one file's bytes for a drag-OUT and writes them into the target-provided stream. Runs on the
    /// UI thread inside the OLE drop, so the actual download is pushed to a worker thread and awaited there —
    /// this never touches the UI and never runs concurrently with another transfer (the coordinator gates).
    /// </summary>
    private void DownloadForDragOut(FileRow row, Stream target)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Nuvia", "dragout-src");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));

        try
        {
            // The download is path-based (temp .nuvia-part → move); confirmOverwrite can't fire (unique name).
            var result = Task.Run(() =>
                _transfers.DownloadAsync(row.File, tempPath, null, _ => true, CancellationToken.None))
                .GetAwaiter().GetResult();

            if (result.Saved && File.Exists(tempPath))
            {
                using var source = File.OpenRead(tempPath);
                source.CopyTo(target);
            }
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Picks a name unused within this drag batch, appending " (2)", " (3)", … before the extension.</summary>
    private static string UniqueName(string fileName, HashSet<string> used)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = fileName;
        var n = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{baseName} ({n}){ext}";
            n++;
        }

        return candidate;
    }

    // -------------------------------------------------------------- utilities

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        // Local reload is instant; the remote reconcile runs in the background and prunes anything that
        // has since been deleted on Telegram. A manual refresh bypasses the activate-throttle.
        ReloadList(SelectedRow()?.File.Id);
        _ = ReconcileWithRemoteAsync(force: true);
    }

    /// <summary>
    /// Background catch-up for remote deletions: asks the storage layer which of this account's known
    /// message ids the server no longer has, then prunes the matching local rows. Runs after window open,
    /// on window-activate (throttled) and on manual Refresh/F5 (<paramref name="force"/>). It complements
    /// the live push from <see cref="OnRemoteFilesDeleted"/> and never blocks the UI or scans history.
    /// </summary>
    private async Task ReconcileWithRemoteAsync(bool force = false)
    {
        if (!_storage.IsAvailable || _accountId <= 0)
            return;
        // Don't fight an active transfer or a drag-out (the UI thread is inside the OLE loop then), and
        // don't overlap reconciles.
        if (_busy || _dragOutActive || _closing || _reconciling)
            return;
        if (!force && DateTime.UtcNow - _lastReconcileUtc < ReconcileThrottle)
            return;

        _reconciling = true;
        _lastReconcileUtc = DateTime.UtcNow;
        try
        {
            var refs = _index.ListForAccount(_accountId).Select(f => f.Remote).ToList();
            if (refs.Count == 0)
                return;

            // Probe every reachable peer for deletions, but each peer only once. Saved Messages (self) is
            // resolved on the first call; the first usable group's channel rides along with it, and any
            // further groups are probed with their own channel refs only — never re-probing self, and never
            // touching refs for a channel we cannot reach (a lost group must not read as a mass deletion).
            var usableGroups = _groups.Where(g => g.LooksUsable()).ToList();
            var missing = new List<RemoteMessageId>();

            if (usableGroups.Count == 0)
            {
                missing.AddRange(await _storage.FindMissingRemoteMessagesAsync(refs, null, CancellationToken.None));
            }
            else
            {
                missing.AddRange(
                    await _storage.FindMissingRemoteMessagesAsync(refs, usableGroups[0], CancellationToken.None));

                for (var i = 1; i < usableGroups.Count; i++)
                {
                    var g = usableGroups[i];
                    var channelRefs = refs
                        .Where(r => r.PeerKind == SavedMessageRef.PeerKindChannel && r.PeerId == g.ChannelId)
                        .ToList();
                    if (channelRefs.Count == 0)
                        continue;
                    missing.AddRange(
                        await _storage.FindMissingRemoteMessagesAsync(channelRefs, g, CancellationToken.None));
                }
            }

            if (missing.Count > 0 && !_closing)
                PruneRemoteDeletions(missing);
        }
        catch
        {
            // Best-effort background sync: a failed check must never disrupt the app or the file list.
        }
        finally
        {
            _reconciling = false;
        }
    }

    /// <summary>
    /// Remove local rows whose remote message has been deleted on Telegram, then refresh the list and
    /// report it. Self-chat rows are matched by message id alone (ids are unique across the account's own
    /// dialogs); managed-group rows are matched by (channel id, message id). Ids Nuvia does not track match
    /// nothing. Idempotent — a second pass over already-pruned rows changes nothing.
    /// </summary>
    private void PruneRemoteDeletions(IReadOnlyList<RemoteMessageId> gone)
    {
        if (gone.Count == 0 || _accountId <= 0)
            return;

        var selfIds = new HashSet<int>();
        var channelIds = new HashSet<(long PeerId, int MessageId)>();
        foreach (var d in gone)
        {
            if (d.PeerKind == SavedMessageRef.PeerKindChannel)
                channelIds.Add((d.PeerId, d.MessageId));
            else
                selfIds.Add(d.MessageId);
        }

        var pruned = 0;
        string? lastName = null;
        foreach (var file in _index.ListForAccount(_accountId))
        {
            var r = file.Remote;
            var match = r.PeerKind == SavedMessageRef.PeerKindChannel
                ? channelIds.Contains((r.PeerId, r.MessageId))
                : selfIds.Contains(r.MessageId);
            if (!match)
                continue;

            try
            {
                if (_index.DeleteFile(_accountId, file.Id))
                {
                    pruned++;
                    lastName = file.DisplayName;
                }
            }
            catch
            {
                // Leave a row we could not remove; the next reconcile will retry it.
            }
        }

        if (pruned > 0)
        {
            ReloadList(SelectedRow()?.File.Id);
            StatusText.Text = pruned == 1
                ? $"“{lastName}” was deleted on Telegram — removed from Nuvia."
                : $"{pruned} files were deleted on Telegram — removed from Nuvia.";
        }
    }

    /// <summary>
    /// Live push from the storage layer, raised on the client's callback thread. Marshal to the UI thread
    /// before touching the index or list, and skip if the window is closing.
    /// </summary>
    private void OnRemoteFilesDeleted(object? sender, RemoteDeletionEventArgs e)
    {
        var deletions = e.Deletions;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing)
                PruneRemoteDeletions(deletions);
        });
    }

    /// <summary>Returning to Nuvia catches up on anything deleted while it was in the background (throttled).</summary>
    private void OnWindowActivated(object? sender, EventArgs e) => _ = ReconcileWithRemoteAsync();

    // ------------------------------------------------------------- local search

    /// <summary>
    /// Filters the visible list as the user types. This searches the <b>local index only</b> by display
    /// name — it never queries Telegram or scans message history. The current selection is preserved
    /// when the matching row survives the filter.
    /// </summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text ?? string.Empty;
        UpdateSearchPlaceholder();

        // WPF raises TextChanged once while the control tree is still being built, before the rest of
        // the window exists. Skip that early call so UpdateState never touches a not-yet-created control;
        // the initial list is drawn by the Loaded handler.
        if (!IsInitialized)
            return;

        ApplyFilter(SelectedRow()?.File.Id);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Escape clears the search while the box has focus — a small convenience tied to the search
        // action itself, not a global shortcut.
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();      // TextChanged then re-applies an empty filter
        SearchBox.Focus();
    }

    // ------------------------------------------------------------- local rename

    private void RenameButton_Click(object sender, RoutedEventArgs e) => RenameSelected();

    /// <summary>
    /// Renames the selected file's <b>local display name</b> only. The original filename and every
    /// remote identifier are preserved, and Telegram is never contacted: the message, its caption and
    /// the stored document are untouched. Validation (empty, length) happens in the dialog; the store
    /// enforces the same bounds again as a backstop.
    /// </summary>
    private void RenameSelected()
    {
        if (_busy) return;

        if (SelectedCount() > 1)
        {
            ShowError("Select one file",
                "Rename works on a single file at a time. Select just one file, then rename it.");
            return;
        }

        var row = SelectedRow();
        if (row is null)
        {
            ShowError("Nothing selected", "Select a file in the list first, then rename it.");
            return;
        }

        var dialog = new RenameWindow(row.File.DisplayName) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var newName = dialog.NewName;
        if (string.Equals(newName, row.File.DisplayName, StringComparison.Ordinal))
            return; // nothing changed

        try
        {
            var changed = _index.UpdateDisplayName(_accountId, row.File.Id, newName);
            if (!changed)
            {
                ShowError("Rename failed",
                    "That file is no longer in the local index for this account, so nothing was changed.");
                ReloadList();
                return;
            }
        }
        catch (Exception ex)
        {
            ShowError("Rename failed",
                $"The local display name was not changed.\n\n{Describe(ex)}\n\n"
                + "Your file in Telegram is untouched — only Nuvia's local label would have changed.");
            return;
        }

        // Re-read so the row reflects the new name; reselect it by id. If a search filter is active and
        // the new name no longer matches, the row correctly drops out of the filtered view.
        ReloadList(row.File.Id);
        StatusText.Text = $"Renamed to “{newName}” locally. Your file in Telegram is unchanged.";
    }

    // ------------------------------------------------------------- delete

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => _ = DeleteSelectedAsync();

    /// <summary>
    /// Deletes the selected file after an explicit confirmation. The dialog offers two clearly separated
    /// choices: remove it from Nuvia's local index only (the file stays in Telegram), or delete it
    /// everywhere (the remote message is deleted first, then the local row). Nothing is deleted without
    /// the user confirming a choice, and the remote delete only happens on the explicit "everywhere" path.
    /// </summary>
    private async Task DeleteSelectedAsync()
    {
        if (_busy) return;

        var rows = SelectedRows();
        if (rows.Count == 0)
        {
            ShowError("Nothing selected", "Select one or more files in the list first, then delete them.");
            return;
        }

        // Several files share one confirmation and one applied choice; a single file keeps the original flow.
        if (rows.Count > 1)
        {
            await DeleteManyAsync(rows);
            return;
        }

        var row = rows[0];

        // "Delete everywhere" needs Saved Messages connected; a file stored in the managed group also
        // needs that group to be reachable. When either is missing we still allow a local-only removal,
        // but disable the remote option and say why.
        string? remoteBlockedReason = null;
        if (!_storage.IsAvailable)
        {
            remoteBlockedReason =
                "Not connected to Telegram, so the remote message cannot be deleted right now. "
                + "You can still remove it from Nuvia only.";
        }
        else if (row.File.Remote.PeerKind == SavedMessageRef.PeerKindChannel
                 && GroupForChannel(row.File.Remote.PeerId) is null)
        {
            remoteBlockedReason =
                "This file is stored in a private group, which is not available, so its remote message "
                + "cannot be deleted. You can still remove it from Nuvia only.";
        }

        var dialog = new DeleteFileWindow(row.File.DisplayName, remoteBlockedReason) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        if (dialog.Choice == DeleteScope.LocalOnly)
        {
            DeleteLocalRow(row, remoteKept: true);
            return;
        }

        // Delete everywhere: remove the remote message first, then the local row. If the remote delete
        // fails the local row is left in place so the file is never orphaned (present remotely, gone
        // locally) without the user knowing.
        var channelGroup = row.File.Remote.PeerKind == SavedMessageRef.PeerKindChannel
            ? GroupForChannel(row.File.Remote.PeerId)
            : null;

        try
        {
            // A single short RPC, not a cancellable byte transfer: no Cancel button, indeterminate bar.
            SetBusy($"Deleting {row.File.DisplayName} from Telegram…", cancellable: false, indeterminate: true);

            await _storage.DeleteDocumentAsync(row.File.Remote, channelGroup, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError("Delete failed",
                $"{row.File.DisplayName} was not deleted.\n\n{Describe(ex)}\n\n"
                + "Nothing was removed — the file is still in Telegram and still listed in Nuvia.");
            return;
        }
        finally
        {
            ClearBusy();
        }

        // Remote delete succeeded — now drop the local index row.
        DeleteLocalRow(row, remoteKept: false);
    }

    /// <summary>
    /// Removes the row from the local index and refreshes the list. <paramref name="remoteKept"/> controls
    /// only the honest wording of the status line — it does not change what is deleted here.
    /// </summary>
    private void DeleteLocalRow(FileRow row, bool remoteKept)
    {
        try
        {
            var removed = _index.DeleteFile(_accountId, row.File.Id);
            if (!removed)
            {
                ShowError("Delete failed",
                    "That file is no longer in the local index for this account, so nothing was changed.");
                ReloadList();
                return;
            }
        }
        catch (Exception ex)
        {
            ShowError("Delete failed",
                $"The local index entry was not removed.\n\n{Describe(ex)}");
            return;
        }

        ReloadList();
        StatusText.Text = remoteKept
            ? $"Removed “{row.File.DisplayName}” from Nuvia. Your file in Telegram is unchanged."
            : $"Deleted “{row.File.DisplayName}” from Telegram and removed it from Nuvia.";
    }

    /// <summary>
    /// Batch delete for a multi-selection: one confirmation covers every selected file, and the scope the
    /// user picks (local-only or everywhere) is applied to all of them. Kept behind that single explicit
    /// confirmation — defaulting to the reversible local-only removal — so a multi-select can never
    /// bulk-delete remote messages by accident. The remote/local ordering and orphan-avoidance of the
    /// single-file path are preserved per file.
    /// </summary>
    private async Task DeleteManyAsync(IReadOnlyList<FileRow> rows)
    {
        // "Delete everywhere" needs Saved Messages connected; when it is not, only local removal is offered
        // for the whole batch. Per-file group reachability is checked again as each remote delete runs.
        var remoteBlockedReason = _storage.IsAvailable
            ? null
            : "Not connected to Telegram, so the remote messages cannot be deleted right now. "
              + "You can still remove the selected files from Nuvia only.";

        var dialog = new DeleteFileWindow(rows.Count, remoteBlockedReason) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        if (dialog.Choice == DeleteScope.LocalOnly)
            DeleteLocalRows(rows);
        else
            await DeleteRowsEverywhereAsync(rows);
    }

    /// <summary>
    /// Removes several rows from the local index only; the Telegram messages are left untouched. Purely
    /// local and fast, so it runs without the progress window and reports once at the end.
    /// </summary>
    private void DeleteLocalRows(IReadOnlyList<FileRow> rows)
    {
        var results = new List<BatchItemResult>(rows.Count);
        foreach (var row in rows)
        {
            try
            {
                // A false result (already gone) still leaves the file out of the list — the user's goal —
                // so it counts as done; only a thrown error is a real failure.
                _index.DeleteFile(_accountId, row.File.Id);
                results.Add(new BatchItemResult(row.File.DisplayName, ItemOutcome.Done, null));
            }
            catch (Exception ex)
            {
                results.Add(new BatchItemResult(row.File.DisplayName, ItemOutcome.Failed, Describe(ex)));
            }
        }

        ReloadList();
        ReportBatch("Removal", results, rows.Count, cancelled: false, 0, 0);
    }

    /// <summary>
    /// Deletes several files everywhere: for each, the remote Telegram message first, then the local row.
    /// A remote failure (or an unreachable group) leaves that file fully intact so it is never orphaned
    /// (gone locally, present in Telegram). Cancellable like the other batches; files already deleted from
    /// Telegram stay deleted — that cannot be undone — and the summary reports how many completed.
    /// </summary>
    private async Task DeleteRowsEverywhereAsync(IReadOnlyList<FileRow> rows)
    {
        var results = new List<BatchItemResult>(rows.Count);
        var cancelled = false;

        _transferCts = new CancellationTokenSource();
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (_transferCts.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var row = rows[i];
                var display = row.File.DisplayName;

                // A file stored in a group that is not reachable cannot have its Telegram message deleted;
                // leave it fully intact rather than removing only the local row, and say why.
                var isChannel = row.File.Remote.PeerKind == SavedMessageRef.PeerKindChannel;
                var channelGroup = isChannel ? GroupForChannel(row.File.Remote.PeerId) : null;
                var reachable = !isChannel || channelGroup is not null;
                if (!reachable)
                {
                    results.Add(new BatchItemResult(display, ItemOutcome.Failed,
                        "Stored in a private group, which is not available, so its Telegram message could not be deleted."));
                    continue;
                }

                _batchHeader = $"Deleting {i + 1} of {rows.Count} from Telegram: {display}";
                SetBusy(_batchHeader, cancellable: true, indeterminate: true);

                try
                {
                    await _storage.DeleteDocumentAsync(row.File.Remote, channelGroup, _transferCts.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    // Remote delete failed: keep the local row too, so the file is never gone locally but
                    // still present in Telegram.
                    results.Add(new BatchItemResult(display, ItemOutcome.Failed, Describe(ex)));
                    continue;
                }

                // Remote gone — now drop the local row.
                try
                {
                    _index.DeleteFile(_accountId, row.File.Id);
                    results.Add(new BatchItemResult(display, ItemOutcome.Done, null));
                }
                catch (Exception ex)
                {
                    results.Add(new BatchItemResult(display, ItemOutcome.Failed,
                        $"Deleted from Telegram, but its Nuvia entry could not be removed: {Describe(ex)}"));
                }
            }
        }
        finally
        {
            _batchHeader = null;
            ClearBusy();
        }

        ReloadList();
        ReportBatch("Deletion", results, rows.Count, cancelled, 0, 0);
    }

    // -------------------------------------------------------- keyboard shortcuts

    /// <summary>
    /// Shortcuts for the actions that actually exist in this build: F2 rename, F5 refresh, Ctrl+F focus
    /// search. Nothing is bound to an action Nuvia does not implement, and none fire during a transfer.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || _busy)
            return;

        switch (e.Key)
        {
            case Key.F2 when SelectedRow() is not null:
                RenameSelected();
                e.Handled = true;
                break;

            case Key.Delete when SelectedCount() > 0:
                _ = DeleteSelectedAsync();
                e.Handled = true;
                break;

            case Key.F5:
                ReloadList(SelectedRow()?.File.Id);
                _ = ReconcileWithRemoteAsync(force: true);
                e.Handled = true;
                break;

            case Key.F when (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
        }
    }

    // -------------------------------------------------------- account menu / panels

    /// <summary>Opens the top-right account menu (Settings / About / Log out).</summary>
    private void AccountChip_Click(object sender, RoutedEventArgs e)
    {
        if (AccountChip.ContextMenu is { } menu)
        {
            menu.PlacementTarget = AccountChip;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// Log out from the account menu. Refused while a transfer is running so it can never abandon an
    /// in-flight upload or download; otherwise <see cref="BeginLogout"/> confirms and performs it.
    /// </summary>
    private void LogoutMenuItem_Click(object sender, RoutedEventArgs e) => BeginLogout();

    /// <summary>
    /// Shared logout path for both entry points (account menu and Settings → Account). Refuses while a
    /// transfer is running, shows the confirm dialog, then hands off to the App layer (which owns the auth
    /// service and login windows) via <see cref="LogoutRequested"/>. Returns true once logout was requested.
    /// </summary>
    private bool BeginLogout()
    {
        if (_busy || _transfers.IsBusy)
        {
            ShowError("A transfer is in progress",
                "Wait for the current upload or download to finish (or cancel it) before logging out.");
            return false;
        }

        if (new LogoutWindow(this).ShowDialog() != true)
            return false;

        LogoutRequested?.Invoke();
        return true;
    }

    // -------------------------------------------------------------- settings

    /// <summary>
    /// Opens the full-window Settings page (gear button + account-menu "Settings" both land here). The
    /// destination radios and Create-group button live on the "Upload destination" category; logout lives
    /// only in the account menu, so it is not repeated here.
    /// </summary>
    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => OpenSettings(0);

    /// <summary>Shows the Settings page on the given rail category and refreshes the destination selector.</summary>
    private void OpenSettings(int categoryIndex)
    {
        // Reflect the current group + default before showing the page.
        RebuildDestinationSelector();
        if (SettingsRail is not null)
            SettingsRail.SelectedIndex = categoryIndex;
        SettingsPage.Visibility = Visibility.Visible;
    }

    /// <summary>Back / Close (and Esc) all return to the file list.</summary>
    private void CloseSettingsPanel_Click(object sender, RoutedEventArgs e) =>
        SettingsPage.Visibility = Visibility.Collapsed;

    /// <summary>Clicking the dimmed scrim outside the Settings card closes it; clicks on the card
    /// itself (any child element) are ignored, so only a hit on the scrim Grid passes through.</summary>
    private void SettingsScrim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SettingsPage))
            SettingsPage.Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows the section panel matching the selected rail category (one visible at a time).</summary>
    private void SettingsRail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent before the section panels exist; ignore until they do.
        if (SecDestination is null) return;

        int index = SettingsRail.SelectedIndex;
        SecDestination.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        SecSaved.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SecData.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        SecAccount.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        SecAbout.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        SecUpdates.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;

        switch (index)
        {
            case 2: _ = RefreshStorageSectionAsync(); break;
            case 3: RefreshAccountSection(); break;
            case 5: RefreshUpdatesSection(); break;
        }
    }

    /// <summary>Esc closes the Settings page when it is open.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsPage.Visibility == Visibility.Visible)
        {
            SettingsPage.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Reacts to a destination radio choice in the Settings panel and persists it. Ignored while the
    /// radios are being set in code (guarded by <see cref="_updatingDestinationSelector"/>).
    /// </summary>
    private void DestinationRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingDestinationSelector) return;

        var chooseGroup = GroupRadio.IsChecked == true && _groups.Any(g => g.LooksUsable());
        ApplyDestination(chooseGroup ? StorageDestinationKind.ManagedGroup : StorageDestinationKind.SavedMessages);
    }

    /// <summary>
    /// Applies and persists a default-destination choice — the fallback used when the open location is Home
    /// or All files. A group choice targets the first usable group; it is only honoured when at least one
    /// group still exists, otherwise it quietly falls back to Saved Messages and never recreates a group.
    /// </summary>
    private void ApplyDestination(StorageDestinationKind kind)
    {
        var defaultGroup = _groups.FirstOrDefault(g => g.LooksUsable());
        if (kind == StorageDestinationKind.ManagedGroup && defaultGroup is not null)
        {
            _destination = StorageDestination.ForGroup(defaultGroup);
            if (_accountId > 0)
                _settings.SetPreferredDestinationKind(_accountId, StorageDestinationKind.ManagedGroup);
        }
        else
        {
            _destination = StorageDestination.SavedMessages;
            if (_accountId > 0)
                _settings.SetPreferredDestinationKind(_accountId, StorageDestinationKind.SavedMessages);
        }

        // Reflect the new default in the radios without re-triggering their change handler.
        RebuildDestinationSelector();
        UpdateUploadTargetText();
        StatusText.Text = $"New uploads will go to {_destination.DisplayName}.";
    }

    /// <summary>Keeps the status-bar "Uploads → …" line in step with the current upload target (the open location).</summary>
    private void UpdateUploadTargetText() =>
        UploadTargetText.Text = "Uploads → " + ResolveCurrentDestination().DisplayName;

    // ------------------------------------------------- settings · storage

    /// <summary>
    /// Fills the Storage section's on-disk size readouts: the SQLite index (plus its -wal/-shm sidecars)
    /// and the preview-thumbnail cache. Both are measured off the UI thread because walking the cache can
    /// touch many files; the results are marshalled back here. Best-effort — a missing path reads as 0 B,
    /// nothing throws.
    /// </summary>
    private async Task RefreshStorageSectionAsync()
    {
        if (StorageIndexSizeText is null) return;
        StorageIndexSizeText.Text = "…";
        StorageCacheSizeText.Text = "…";

        var indexPath = NuviaConfig.GetIndexDatabasePath();
        var thumbsPath = Path.Combine(NuviaConfig.LocalDataDirectory, "thumbs");

        var (indexBytes, cacheBytes) = await Task.Run(() =>
        {
            long idx = FileLength(indexPath) + FileLength(indexPath + "-wal") + FileLength(indexPath + "-shm");
            long cache = DirectorySize(thumbsPath);
            return (idx, cache);
        });

        if (StorageIndexSizeText is null) return; // window torn down while measuring
        StorageIndexSizeText.Text = FormatSize(indexBytes);
        StorageCacheSizeText.Text = FormatSize(cacheBytes);
    }

    /// <summary>Opens the Nuvia data folder in File Explorer. Creates it first so the open never dead-ends.</summary>
    private void StorageOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = NuviaConfig.LocalDataDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("Could not open the data folder", ex.Message);
        }
    }

    /// <summary>
    /// Deletes the cached preview thumbnails to free disk space. Cancels any in-flight preview pass first,
    /// drops the in-memory cache, then removes the on-disk folder — best-effort, a locked file is simply
    /// overwritten on the next pass. Previews are rebuilt automatically the next time the list is read;
    /// nothing in Telegram is touched. The size readout refreshes to show the space reclaimed.
    /// </summary>
    private void StorageClearCache_Click(object sender, RoutedEventArgs e)
    {
        _thumbCts?.Cancel();
        try
        {
            var thumbsPath = Path.Combine(NuviaConfig.LocalDataDirectory, "thumbs");
            if (Directory.Exists(thumbsPath))
                Directory.Delete(thumbsPath, recursive: true);
        }
        catch { /* best-effort: a locked thumbnail is overwritten on the next pass */ }

        _thumbCache.Clear();
        _ = RefreshStorageSectionAsync();
    }

    /// <summary>
    /// Forgets this account's local file list and rebuilds it from Telegram. Confirmed first, and refused
    /// while a transfer or rebuild is running. Clears the index rows + Nuvia-local folders + scan watermarks
    /// (<see cref="IndexStore.DeleteAllForAccount"/>) and the now-orphaned preview cache, resets the view to
    /// an empty root, then runs <see cref="RunLibraryRebuildAsync"/>. Honest about what returns: files
    /// uploaded through Nuvia come back; local-only details (custom names, folders of external files) do not;
    /// nothing is ever deleted from Telegram.
    /// </summary>
    private async void StorageResetIndex_Click(object sender, RoutedEventArgs e)
    {
        if (_accountId <= 0) return;

        if (_busy || _transfers.IsBusy || _rebuilding)
        {
            ShowError("Nuvia is busy",
                "Wait for the current transfer or rebuild to finish before resetting the local list.");
            return;
        }

        var confirm = MessageBox.Show(this,
            "Forget Nuvia's local file list on this PC and rebuild it from your Telegram account?\n\n" +
            "Files you uploaded through Nuvia come back automatically. Local-only details are lost: custom " +
            "names you gave files, and folders you placed files added outside Nuvia into. Nothing is deleted " +
            "from Telegram.",
            "Reset local file list", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        // 1) Forget the local index for this account (files + folders + scan watermarks), one transaction.
        var removed = _index.DeleteAllForAccount(_accountId);

        // 2) Drop the preview cache — those thumbnails key off rows that no longer exist.
        _thumbCts?.Cancel();
        try
        {
            var thumbsPath = Path.Combine(NuviaConfig.LocalDataDirectory, "thumbs");
            if (Directory.Exists(thumbsPath))
                Directory.Delete(thumbsPath, recursive: true);
        }
        catch { /* best-effort */ }
        _thumbCache.Clear();

        // 3) Empty root view, then rebuild from Telegram.
        _activeFolderId = null;
        ReloadList();

        var cleared = $"Cleared {removed} local entr{(removed == 1 ? "y" : "ies")}.";
        StorageResetStatusText.Visibility = Visibility.Visible;

        if (!_storage.IsAvailable)
        {
            StorageResetStatusText.Text =
                $"{cleared} Nuvia will rebuild the list from Telegram once the connection is back.";
            _ = RefreshStorageSectionAsync();
            return;
        }

        StorageResetStatusText.Text = $"{cleared} Rebuilding from Telegram…";
        await RunLibraryRebuildAsync();
        StorageResetStatusText.Text =
            "Rebuild complete. Files you uploaded through Nuvia have been restored from your account.";
        _ = RefreshStorageSectionAsync();
    }

    /// <summary>Total size of one file, or 0 if it is missing/unreadable.</summary>
    private static long FileLength(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.Length : 0; }
        catch { return 0; }
    }

    /// <summary>Total size of every file under a directory tree, or 0 if it is missing/unreadable.</summary>
    private static long DirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* skip a vanishing file */ }
            }
            return total;
        }
        catch { return 0; }
    }

    // ------------------------------------------------- settings · account

    /// <summary>Fills the Account section: signed-in state and the current default upload destination.</summary>
    private void RefreshAccountSection()
    {
        if (AccountStateText is null) return;
        AccountStateText.Text = "Signed in to your Telegram account";
        AccountDestinationText.Text = $"Default upload destination: {_destination.DisplayName}.";
    }

    /// <summary>
    /// Settings → Account "Log out". Shares the account-menu path (transfer guard + confirm dialog) via
    /// <see cref="BeginLogout"/>, and closes the Settings page once logout is actually requested.
    /// </summary>
    private void SettingsLogout_Click(object sender, RoutedEventArgs e)
    {
        if (BeginLogout())
            SettingsPage.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------- library rebuild / import

    /// <summary>
    /// Rebuild the local index from Telegram once, in the background. This is what restores files after a
    /// reinstall wiped the local database (they still live on the account), and what re-scans Saved Messages
    /// when the "show external files" toggle is switched on. It reads history but sends, edits and deletes
    /// nothing. All index access happens here on the UI thread — exactly like <see cref="ReloadList"/> and
    /// <see cref="ReconcileWithRemoteAsync"/> — so a rebuild never races them over the SQLite connection;
    /// the awaited network reads keep the UI responsive between pages. A discovered group is reflected in
    /// the selector, and the list is only re-read when something actually changed. Best-effort: any failure
    /// is swallowed so a rebuild never interrupts the user or disturbs the existing local list.
    /// </summary>
    private async Task RunLibraryRebuildAsync()
    {
        if (!_storage.IsAvailable || _accountId <= 0)
            return;
        // Don't fight an active transfer or a drag-out (the UI thread is inside the OLE loop then), and
        // don't overlap rebuilds.
        if (_busy || _dragOutActive || _closing || _rebuilding)
            return;

        _rebuilding = true;
        try
        {
            var summary = await _rebuild.RebuildAsync(_accountId, CancellationToken.None);
            if (_closing)
                return;

            // A rediscovered group (post-reinstall) must be reflected in the selector and Locations pane
            // before the list re-reads, so its rows resolve to the group location.
            if (summary.GroupDiscovered)
                LoadDestinationState();

            if (summary.AnyChange)
                ReloadList(SelectedRow()?.File.Id);
        }
        catch
        {
            // Best-effort background rebuild: a failed import must never disrupt the app or the file list.
        }
        finally
        {
            _rebuilding = false;
        }
    }

    /// <summary>
    /// The user toggled "Also show files added outside Nuvia" for Saved Messages. On: persist, reset the
    /// Saved Messages scan watermark so the next rebuild re-reads the whole history — external files were
    /// skipped while the toggle was off and the watermark had advanced past them — then rebuild. Off:
    /// persist and purge the external Saved Messages rows from the local index. Either way the managed group
    /// is untouched (it always shows everything), and nothing is ever changed on Telegram — turning the
    /// toggle off only forgets rows locally, it never deletes a file from the account.
    /// </summary>
    private async void ShowExternalFilesToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_accountId <= 0)
            return;

        var showExternal = ShowExternalFilesCheck.IsChecked == true;
        _settings.SetShowExternalSavedMessages(_accountId, showExternal);
        _showExternalSaved = showExternal;

        if (showExternal)
        {
            // Re-scan Saved Messages from the start so files added outside Nuvia are picked up. Only the
            // Saved Messages watermark is reset; the managed group's own watermark is left alone.
            _index.ResetImportWatermark(_accountId, SavedMessageRef.PeerKindSelf, _accountId);
            StatusText.Text = "Showing files added outside Nuvia. Checking your Saved Messages…";
            await RunLibraryRebuildAsync();
            // Re-read even when the rebuild found nothing new, so the relaxed filter takes effect at once.
            ReloadList(SelectedRow()?.File.Id);
        }
        else
        {
            // Hide (and forget locally) the external Saved Messages files. This removes only local index
            // rows — nothing is deleted from Telegram, and Nuvia's own uploads are kept.
            var removed = _index.DeleteExternalSelfFiles(_accountId);
            ReloadList(SelectedRow()?.File.Id);
            StatusText.Text = removed > 0
                ? $"Stopped showing {removed} file(s) added outside Nuvia. Your own files are unaffected."
                : "Saved Messages now shows only files you added through Nuvia.";
        }
    }

    // ------------------------------------------------------- notifications / updates

    /// <summary>
    /// Runs one update/announcement check in the background and applies the result to the UI. Cancels any
    /// in-flight check first (manual "Check now" supersedes a background pass). Never throws: the service is
    /// best-effort and returns an honest result, and this wrapper swallows anything stray so a check can
    /// never crash the window. Returns the result so the Settings "Check now" button can show inline status.
    /// </summary>
    private async Task<NotificationCheckResult> RunUpdateCheckAsync()
    {
        try
        {
            _updateCts?.Cancel();
            _updateCts = new CancellationTokenSource();
            var result = await _updateService.CheckAsync(_updateCts.Token);
            ApplyCheckResult(result);
            return result;
        }
        catch
        {
            return NotificationCheckResult.Failed;
        }
    }

    /// <summary>
    /// One no-join background read of the app-owned public announcements channel over the signed-in account,
    /// merged into the notification center. Awaited on the UI thread so the continuation marshals back to the
    /// dispatcher (the service's own awaits use <c>ConfigureAwait(false)</c>). Best-effort: a null or
    /// unconfigured service, or any failure, leaves the current list untouched — the feed never joins the
    /// channel, never crashes the window, and never fabricates a notice.
    /// </summary>
    private async Task RunAnnouncementsFetchAsync()
    {
        if (_announcements is null || !_announcements.IsConfigured)
            return;

        try
        {
            var items = await _announcements.FetchAsync(_announcementCts.Token);
            _channelAnnouncements.Clear();
            _channelAnnouncements.AddRange(items);
            RebuildNotifications();
        }
        catch
        {
            // Best-effort: keep whatever is already shown rather than clearing on a flaky read.
        }
    }

    /// <summary>
    /// Creates (once) and starts the announcement poll timer. A short interval keeps dev notices near
    /// real-time without a channel join or a push connection; each tick is a single read-only getHistory and
    /// never blocks the UI. No-op when the feed is unconfigured.
    /// </summary>
    private void EnsureAnnouncementTimer()
    {
        if (_announcements is null || !_announcements.IsConfigured)
            return;

        if (_announcementTimer is null)
        {
            _announcementTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            _announcementTimer.Tick += (_, _) => _ = RunAnnouncementsFetchAsync();
        }
        _announcementTimer.Start();
    }

    /// <summary>
    /// Marshals a finished check onto the notification center. A silent no-op when no source is configured;
    /// a configured-but-offline result leaves the existing list untouched (no fake "couldn't check" rows —
    /// the Settings status line reports that honestly). On a successful reach it stores the offered update and
    /// the website announcements, then rebuilds the merged list (the channel feed is kept separately).
    /// </summary>
    private void ApplyCheckResult(NotificationCheckResult result)
    {
        if (!result.Configured || !result.CheckedOk)
        {
            UpdateNotificationBadge();
            return;
        }

        _latestUpdate = result.Update;

        _webAnnouncements.Clear();
        _webAnnouncements.AddRange(result.Announcements);

        RebuildNotifications();
        if (SecUpdates is not null && SecUpdates.Visibility == Visibility.Visible)
            RefreshUpdatesSection();
    }

    /// <summary>
    /// Rebuilds the single <see cref="_notifications"/> list the bell popup binds to from the separately held
    /// sources: the offered update (if any) pinned on top, then every announcement — Telegram channel and
    /// website manifest alike — newest first, deduped by Id. Keeping the sources apart means a refresh of one
    /// (a background channel poll, say) never drops the other's rows. Ends by refreshing the unread badge.
    /// </summary>
    private void RebuildNotifications()
    {
        _notifications.Clear();

        if (_latestUpdate is not null)
        {
            _notifications.Add(new NotificationItem
            {
                Id = "update:" + _latestUpdate.VersionDisplay,
                Title = $"Nuvia {_latestUpdate.VersionDisplay} is available",
                Message = string.IsNullOrWhiteSpace(_latestUpdate.Notes)
                    ? "A newer version is ready to download."
                    : _latestUpdate.Notes!,
                Timestamp = DateTimeOffset.Now,
                Kind = NotificationKind.Update,
                ActionUrl = _latestUpdate.DownloadUrl,
                ActionLabel = string.IsNullOrWhiteSpace(_latestUpdate.DownloadUrl) ? null : "Download",
            });
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ann in _channelAnnouncements.Concat(_webAnnouncements)
                     .OrderByDescending(a => a.Timestamp))
        {
            if (seen.Add(ann.Id))
                _notifications.Add(ann);
        }

        UpdateNotificationBadge();
    }

    /// <summary>
    /// Shows the red dot when something is unread and toggles the popup's empty state. "Unread" means a
    /// newer version the user has not yet opened the center for, or an announcement whose id is not in the
    /// seen set. Reading the "seen" markers is cheap and keeps the badge honest across restarts.
    /// </summary>
    private void UpdateNotificationBadge()
    {
        if (NotificationBadge is null)
            return;

        var unread = false;
        if (_latestUpdate is not null)
        {
            var seen = _settings.GetLastSeenUpdateVersion();
            if (!string.Equals(seen, _latestUpdate.VersionDisplay, StringComparison.Ordinal))
                unread = true;
        }

        var seenIds = new HashSet<string>(_settings.GetSeenAnnouncementIds(), StringComparer.Ordinal);
        foreach (var n in _notifications)
        {
            if (n.Kind == NotificationKind.Announcement && !seenIds.Contains(n.Id))
            {
                unread = true;
                break;
            }
        }

        NotificationBadge.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;

        var any = _notifications.Count > 0;
        NotificationsScroller.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        NotificationsEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Marks everything in the center as seen (clears the badge) and persists the markers.</summary>
    private void MarkNotificationsRead()
    {
        if (_latestUpdate is not null)
            _settings.SetLastSeenUpdateVersion(_latestUpdate.VersionDisplay);

        var annIds = _notifications
            .Where(n => n.Kind == NotificationKind.Announcement)
            .Select(n => n.Id)
            .ToList();
        if (annIds.Count > 0)
            _settings.AddSeenAnnouncementIds(annIds);

        UpdateNotificationBadge();
    }

    /// <summary>Bell click toggles the center; opening it marks the current contents as read.</summary>
    private void NotificationsButton_Click(object sender, RoutedEventArgs e)
    {
        NotificationsPopup.IsOpen = !NotificationsPopup.IsOpen;
        if (NotificationsPopup.IsOpen)
            MarkNotificationsRead();
    }

    /// <summary>"Mark all read" link in the center header.</summary>
    private void MarkAllNotificationsRead_Click(object sender, RoutedEventArgs e) => MarkNotificationsRead();

    /// <summary>
    /// Shared handler for every "Download"/"Open" action (center rows and the Settings link). The URL rides
    /// on the control's Tag; <see cref="OpenUrl"/> re-validates it and opens the default browser. Opening a
    /// browser to a download page is an explicit user click — Nuvia is never itself a browser or installer.
    /// </summary>
    private void NotificationAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string url)
            OpenUrl(url);
    }

    /// <summary>Opens an http/https URL in the default browser, guarded. Non-web schemes are ignored.</summary>
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("Could not open the link",
                $"The page could not be opened in your browser.\n\n{Describe(ex)}");
        }
    }

    /// <summary>Settings → Updates: the "Automatically check for updates" toggle (opt-out, on by default).</summary>
    private void AutoCheckUpdatesToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = AutoCheckUpdatesCheck.IsChecked == true;
        _settings.SetAutoCheckUpdates(enabled);

        if (enabled)
        {
            EnsureUpdateTimer();
            _ = RunUpdateCheckAsync();
        }
        else
        {
            _updateTimer?.Stop();
        }
    }

    /// <summary>
    /// Settings → Updates: the explicit "Check for updates now" button. A manual, user-initiated action, so
    /// it runs regardless of the auto-check toggle. Shows an honest inline status and never fakes a result.
    /// </summary>
    private async void CheckUpdatesNow_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesNowButton.IsEnabled = false;
        UpdateDownloadLink.Visibility = Visibility.Collapsed;
        UpdateStatusText.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "Checking for updates…";

        try
        {
            var result = await RunUpdateCheckAsync();
            if (!result.Configured)
                UpdateStatusText.Text = "Update checking isn't available in this build yet.";
            else if (!result.CheckedOk)
                UpdateStatusText.Text = "Couldn't check for updates. Are you online?";
            else if (result.Update is not null)
            {
                UpdateStatusText.Text = $"Update available — Nuvia {result.Update.VersionDisplay}.";
                if (!string.IsNullOrWhiteSpace(result.Update.DownloadUrl))
                {
                    UpdateDownloadLink.Tag = result.Update.DownloadUrl;
                    UpdateDownloadLink.Visibility = Visibility.Visible;
                }
            }
            else
                UpdateStatusText.Text = "You're up to date.";
        }
        finally
        {
            CheckUpdatesNowButton.IsEnabled = true;
            RefreshUpdatesSection();
        }
    }

    /// <summary>
    /// Syncs the Settings → Updates controls to current state: the toggle, the "last checked" line, and —
    /// when an update is already known this session — the "update available" status + download link. It
    /// never asserts "up to date" on its own (that only follows a real check), so it won't clobber the
    /// honest transient status the manual button sets.
    /// </summary>
    private void RefreshUpdatesSection()
    {
        if (AutoCheckUpdatesCheck is null)
            return;

        AutoCheckUpdatesCheck.IsChecked = _settings.GetAutoCheckUpdates();

        var last = _settings.GetLastUpdateCheckUtc();
        LastCheckedText.Text = last is null
            ? "Last checked: never"
            : "Last checked: " + NotificationItem.FormatRelative(last.Value, DateTimeOffset.Now);

        if (_latestUpdate is not null)
        {
            UpdateStatusText.Text = $"Update available — Nuvia {_latestUpdate.VersionDisplay}.";
            UpdateStatusText.Visibility = Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(_latestUpdate.DownloadUrl))
            {
                UpdateDownloadLink.Tag = _latestUpdate.DownloadUrl;
                UpdateDownloadLink.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateDownloadLink.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Creates (once) and starts the periodic re-check timer so a long session notices new releases.</summary>
    private void EnsureUpdateTimer()
    {
        if (_updateTimer is null)
        {
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            _updateTimer.Tick += (_, _) =>
            {
                if (_settings.GetAutoCheckUpdates())
                    _ = RunUpdateCheckAsync();
            };
        }
        _updateTimer.Start();
    }


    // ----------------------------------------------------- storage destination

    /// <summary>
    /// Reads the account's groups and preferred fallback destination from the local index. Purely local: it
    /// never creates or contacts a group. A stored "group" preference is only honoured when at least one
    /// group row is still present — otherwise it falls back to Saved Messages and is never recreated.
    /// </summary>
    private void LoadDestinationState()
    {
        _groups = _accountId > 0 ? _index.GetStorageGroups(_accountId).ToList() : new();

        // The Saved Messages external-files preference is per-account and defaults off (Nuvia files only).
        _showExternalSaved = _accountId > 0 && _settings.GetShowExternalSavedMessages(_accountId);

        var preferred = _accountId > 0
            ? _settings.GetPreferredDestinationKind(_accountId)
            : StorageDestinationKind.SavedMessages;

        var defaultGroup = _groups.FirstOrDefault(g => g.LooksUsable());
        _destination = preferred == StorageDestinationKind.ManagedGroup && defaultGroup is not null
            ? StorageDestination.ForGroup(defaultGroup)
            : StorageDestination.SavedMessages;

        RebuildDestinationSelector();
    }

    /// <summary>
    /// Reflects the current groups + fallback default in the Settings panel radios (without firing the
    /// change handler) and rebuilds the left-pane group list. The group radio is only offered when a usable
    /// group exists; with several groups it simply means "the default group". Enable/disable is left to
    /// <see cref="UpdateState"/>.
    /// </summary>
    private void RebuildDestinationSelector()
    {
        // Keep the left Locations pane in step: one selectable node per usable group, plus the fixed nodes.
        RebuildGroupNodes();

        _updatingDestinationSelector = true;
        try
        {
            var defaultGroup = _groups.FirstOrDefault(g => g.LooksUsable());
            var groupUsable = defaultGroup is not null;

            GroupRadio.Content = groupUsable ? defaultGroup!.Title : "Private storage group";
            NoGroupNote.Visibility = groupUsable ? Visibility.Collapsed : Visibility.Visible;

            if (_destination.Kind == StorageDestinationKind.ManagedGroup && groupUsable)
                GroupRadio.IsChecked = true;
            else
                SavedMessagesRadio.IsChecked = true;
        }
        finally
        {
            _updatingDestinationSelector = false;
        }

        // Reflect the Saved Messages external-files toggle. Setting IsChecked in code raises Checked/
        // Unchecked, not Click, so this never re-enters ShowExternalFilesToggle_Click. The toggle is only
        // meaningful for a real signed-in account.
        ShowExternalFilesCheck.IsChecked = _showExternalSaved;
        ShowExternalFilesCheck.IsEnabled = _accountId > 0;

        UpdateState();
    }

    /// <summary>
    /// The destination to use for an upload right now, Explorer-style: whatever location is open. An open
    /// group or Saved Messages is the target directly; at Home / All files it is the saved fallback default
    /// (a group only while one still exists, otherwise Saved Messages — never recreating a group).
    /// </summary>
    private StorageDestination ResolveCurrentDestination()
    {
        if (_locationFilter == LocationFilter.Group && _activeGroup is not null && _activeGroup.LooksUsable())
            return StorageDestination.ForGroup(_activeGroup);

        if (_locationFilter == LocationFilter.SavedMessages)
            return StorageDestination.SavedMessages;

        // Home / All files: fall back to the saved default.
        if (_destination.Kind == StorageDestinationKind.ManagedGroup)
        {
            var g = _groups.FirstOrDefault(x => x.LooksUsable());
            if (g is not null)
                return StorageDestination.ForGroup(g);
        }

        return StorageDestination.SavedMessages;
    }

    /// <summary>
    /// Creates an optional private storage group — only after the user confirms in the dialog. An account
    /// may keep as many groups as Telegram allows; each create simply adds another. Shared by the Settings
    /// button, the left-pane "New group…" entry and the right-click "New group…" menu item.
    /// </summary>
    private async void CreateGroupButton_Click(object sender, RoutedEventArgs e)
        => await StartCreateGroupAsync();

    private async Task StartCreateGroupAsync()
    {
        if (_busy) return;

        if (!_storage.IsAvailable)
        {
            ShowError("Not signed in", _storage.UnavailableReason ?? "Sign in first.");
            return;
        }

        // Explicit confirmation with an editable name. Nothing is created unless this returns true.
        var dialog = new CreateGroupWindow { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var title = dialog.GroupName;

        try
        {
            // A single short RPC, not a cancellable byte transfer: no Cancel button, indeterminate bar.
            SetBusy($"Creating private storage group \"{title}\"…", cancellable: false, indeterminate: true);

            var group = await _storage.CreatePrivateStorageGroupAsync(title, CancellationToken.None);

            // Persist identifiers only after the remote create actually succeeded, then add it to the set.
            _index.SaveStorageGroup(group);
            _groups.RemoveAll(g => g.ChannelId == group.ChannelId);
            _groups.Add(group);

            // Creating a group does not change the fallback default or the open location — it just appears
            // in the left pane. New uploads still go wherever the current location says.
            RebuildDestinationSelector();

            StatusText.Text = $"Created private storage group \"{group.Title}\". "
                + "Open it in the left pane to store new files there.";
        }
        catch (Exception ex)
        {
            ShowError("Could not create the group",
                $"The private storage group was not created.\n\n{Describe(ex)}\n\n"
                + "Nothing was changed on your account.");
        }
        finally
        {
            ClearBusy();
        }
    }

    // ---------------------------------------------------- group management (context menus)

    /// <summary>
    /// Right-click "New group…" / "Create group…" from the file area or the left-pane group node.
    /// Same explicit, confirmed create as the Settings button.
    /// </summary>
    private async void NewGroupMenuItem_Click(object sender, RoutedEventArgs e)
        => await StartCreateGroupAsync();

    /// <summary>Right-click "Open" on a group node: select that group's location (opens/filters to it).</summary>
    private void OpenGroupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var group = GroupFromSender(sender);
        if (group is null || !group.LooksUsable())
        {
            ShowError("Group unavailable", "This private storage group is no longer available.");
            return;
        }

        // Selecting the node drives LocationsList_SelectionChanged, which filters the list to the group.
        var node = NodeForGroup(group);
        if (node is not null)
        {
            node.IsSelected = true;
            node.Focus();
        }
    }

    /// <summary>
    /// Right-click "Rename group…": edit ONLY the group's Telegram title (channels.editTitle). This never
    /// renames or edits any stored message — Nuvia's per-file rename is a separate, local-only label.
    /// </summary>
    private async void RenameGroupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var group = GroupFromSender(sender);
        if (group is null || !group.LooksUsable())
        {
            ShowError("Group unavailable", "This private storage group is no longer available to rename.");
            return;
        }

        if (!_storage.IsAvailable)
        {
            ShowError("Not signed in", _storage.UnavailableReason ?? "Sign in first.");
            return;
        }

        var dialog = new CreateGroupWindow(group.Title, renameMode: true) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var newTitle = dialog.GroupName;
        if (string.Equals(newTitle, group.Title, StringComparison.Ordinal))
            return; // no change

        try
        {
            SetBusy($"Renaming the group to \"{newTitle}\"…", cancellable: false, indeterminate: true);

            var updated = await _storage.RenameStorageGroupAsync(group, newTitle, CancellationToken.None);

            _index.SaveStorageGroup(updated);
            _groups.RemoveAll(g => g.ChannelId == updated.ChannelId);
            _groups.Add(updated);
            if (_activeGroup is not null && _activeGroup.ChannelId == updated.ChannelId)
                _activeGroup = updated;

            // Reflect the new title everywhere: destination selector, the left-pane node, the header (when
            // this group is the active location) and the file rows' "Destination" column.
            RebuildDestinationSelector();
            if (_locationFilter == LocationFilter.Group
                && _activeGroup is not null && _activeGroup.ChannelId == updated.ChannelId)
                LocationHeaderText.Text = updated.Title;
            ReloadList(SelectedRow()?.File.Id);

            StatusText.Text = $"Renamed the private group to \"{updated.Title}\".";
        }
        catch (Exception ex)
        {
            ShowError("Could not rename the group",
                $"The group name was not changed.\n\n{Describe(ex)}");
        }
        finally
        {
            ClearBusy();
        }
    }

    /// <summary>
    /// Right-click "Delete group…": permanently delete the supergroup and all its files from Telegram
    /// (channels.deleteChannel), after a high-friction confirmation. Then purge the local group row and its
    /// orphaned file rows. Other groups are left untouched.
    /// </summary>
    private async void DeleteGroupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var group = GroupFromSender(sender);
        if (group is null || !group.LooksUsable())
        {
            ShowError("Group unavailable", "This private storage group is no longer available to delete.");
            return;
        }

        if (!_storage.IsAvailable)
        {
            ShowError("Not signed in", _storage.UnavailableReason ?? "Sign in first.");
            return;
        }

        // How many local rows live in this group — shown in the confirmation so the cost is explicit.
        var channelId = group.ChannelId;
        var fileCount = _allRows.Count(r =>
            r.File.Remote.PeerKind == SavedMessageRef.PeerKindChannel && r.File.Remote.PeerId == channelId);

        var dialog = new DeleteGroupWindow(group.Title, fileCount) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var doomed = group;
        try
        {
            SetBusy($"Deleting the group \"{doomed.Title}\"…", cancellable: false, indeterminate: true);

            await _storage.DeleteStorageGroupAsync(doomed, CancellationToken.None);

            FinishGroupRemoval(doomed);
            StatusText.Text = $"Deleted the private group \"{doomed.Title}\" and its files from Telegram.";
        }
        catch (StorageGroupUnavailableException)
        {
            // The remote group is already gone or unreachable. There is nothing left to delete on Telegram,
            // so clear the local pointers too rather than leaving Nuvia aimed at a group that no longer exists.
            FinishGroupRemoval(doomed);
            StatusText.Text = $"The group \"{doomed.Title}\" was already gone on Telegram; removed it from Nuvia.";
        }
        catch (Exception ex)
        {
            ShowError("Could not delete the group",
                $"The group was not deleted.\n\n{Describe(ex)}\n\nNothing was changed on your account.");
        }
        finally
        {
            ClearBusy();
        }
    }

    /// <summary>
    /// Purge one group's local row and its orphaned file rows and drop it from state. If it was the open
    /// location, move off it; if it was the fallback default and no usable group remains, fall back to
    /// Saved Messages. Other groups are untouched. Local only — the remote delete (if any) happened already.
    /// </summary>
    private void FinishGroupRemoval(ManagedStorageGroup group)
    {
        if (_accountId > 0)
            _index.DeleteStorageGroup(_accountId, group.ChannelId);

        _groups.RemoveAll(g => g.ChannelId == group.ChannelId);

        // If the deleted group was the open location, step off it back to "All files".
        if (_activeGroup is not null && _activeGroup.ChannelId == group.ChannelId)
        {
            _activeGroup = null;
            if (_locationFilter == LocationFilter.Group)
                LocationAllItem.IsSelected = true;
        }

        // Keep the fallback default coherent: if it pointed at a group, re-resolve it — another usable group
        // keeps a group default, otherwise fall back to Saved Messages and persist it so nothing lingers
        // pointing at a deleted group. Either path rebuilds the selector (and the left-pane node list).
        if (_destination.Kind == StorageDestinationKind.ManagedGroup)
            ApplyDestination(_groups.Any(g => g.LooksUsable())
                ? StorageDestinationKind.ManagedGroup
                : StorageDestinationKind.SavedMessages);
        else
            RebuildDestinationSelector();

        // Reload so the deleted group's files disappear from the list.
        ReloadList();
    }

    // -------------------------------------------------- multi-group helpers (view state)

    /// <summary>Reads the target group off a right-click menu item's <see cref="FrameworkElement.Tag"/>.</summary>
    private static ManagedStorageGroup? GroupFromSender(object sender)
        => (sender as MenuItem)?.Tag as ManagedStorageGroup;

    /// <summary>The usable managed group that owns <paramref name="peerId"/>'s channel, or null if none.</summary>
    private ManagedStorageGroup? GroupForChannel(long peerId)
    {
        if (peerId == 0)
            return null;
        foreach (var g in _groups)
            if (g.ChannelId == peerId && g.LooksUsable())
                return g;
        return null;
    }

    /// <summary>The left-pane Locations node currently representing <paramref name="group"/>, or null.</summary>
    private System.Windows.Controls.ListBoxItem? NodeForGroup(ManagedStorageGroup group)
        => _dynamicGroupNodes.FirstOrDefault(n => (n.Tag as ManagedStorageGroup)?.ChannelId == group.ChannelId);

    /// <summary>
    /// Rebuilds the per-group Locations nodes from <see cref="_groups"/> — one selectable node per usable
    /// group, inserted just after "Saved Messages". View-only: it never contacts Telegram or the index.
    /// Selection churn is suppressed via <see cref="_rebuildingGroupNodes"/>; if a group is the open
    /// location its (new) node is re-selected, and if that group has vanished the caller decides the next
    /// location.
    /// </summary>
    private void RebuildGroupNodes()
    {
        _rebuildingGroupNodes = true;
        try
        {
            // Drop the previous dynamic nodes (and prune them from nav history so Back never lands on a
            // node that is no longer in the list).
            foreach (var node in _dynamicGroupNodes)
            {
                PruneNavHistory(node);
                LocationsList.Items.Remove(node);
            }
            _dynamicGroupNodes.Clear();

            var insertAt = LocationsList.Items.IndexOf(LocationSavedItem) + 1;
            foreach (var group in _groups)
            {
                if (!group.LooksUsable())
                    continue;
                var node = CreateGroupNode(group);
                LocationsList.Items.Insert(insertAt++, node);
                _dynamicGroupNodes.Add(node);
            }

            // Keep the open group selected across the rebuild (its node is a fresh object each time).
            if (_activeGroup is not null)
            {
                var match = NodeForGroup(_activeGroup);
                if (match is not null)
                    match.IsSelected = true;
                else
                    _activeGroup = null; // its group vanished — the caller sets the next location
            }
        }
        finally
        {
            _rebuildingGroupNodes = false;
        }
    }

    /// <summary>Builds one left-pane Locations node for a managed group: icon + title, with an Open/Rename/Delete menu.</summary>
    private System.Windows.Controls.ListBoxItem CreateGroupNode(ManagedStorageGroup group)
    {
        var icon = new TextBlock { Text = "", Margin = new Thickness(0, 0, 12, 0) };
        icon.SetResourceReference(FrameworkElement.StyleProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(group.Title) ? "Private group" : group.Title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(icon);
        panel.Children.Add(label);

        var open = new MenuItem { Header = "Open", Tag = group };
        open.Click += OpenGroupMenuItem_Click;
        var rename = new MenuItem { Header = "Rename group…", Tag = group };
        rename.Click += RenameGroupMenuItem_Click;
        var delete = new MenuItem { Header = "Delete group…", Tag = group };
        delete.SetResourceReference(MenuItem.ForegroundProperty, "DangerTextBrush");
        delete.Click += DeleteGroupMenuItem_Click;

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(rename);
        menu.Items.Add(delete);

        return new System.Windows.Controls.ListBoxItem { Tag = group, Content = panel, ContextMenu = menu };
    }

    /// <summary>Removes every reference to a discarded nav item from the Back/Forward history, fixing the index.</summary>
    private void PruneNavHistory(System.Windows.Controls.ListBoxItem removed)
    {
        for (var i = _navHistory.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_navHistory[i].Location, removed))
                continue;
            _navHistory.RemoveAt(i);
            if (i <= _navIndex)
                _navIndex--;
        }
        if (_navIndex < -1)
            _navIndex = -1;
    }

    /// <summary>
    /// Explorer-style right-click selection: right-clicking a file that is not already part of the current
    /// selection selects just that file, so the per-file Rename/Delete menu acts on what was clicked.
    /// Right-clicking empty space leaves the selection alone and lets the file-area menu show instead.
    /// </summary>
    private void FileView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView list)
            return;

        var item = FindAncestorListViewItem(e.OriginalSource as DependencyObject);
        if (item is null)
            return; // empty space → keep selection, background menu opens

        if (!item.IsSelected)
        {
            list.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    /// <summary>Walks the visual tree up to the containing <see cref="ListViewItem"/>, or null if none.</summary>
    private static ListViewItem? FindAncestorListViewItem(DependencyObject? source)
    {
        while (source is not null && source is not ListViewItem)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        return source as ListViewItem;
    }

    /// <summary>
    /// Opens the folder that holds the local index, selecting the database file in Explorer. Purely local
    /// and read-only — it never touches the account.
    /// </summary>
    private void OpenIndexFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _index.DatabasePath;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowError("Could not open the folder",
                $"The index folder could not be opened.\n\n{Describe(ex)}");
        }
    }

    private void SetBusy(string label, bool cancellable = true, bool indeterminate = false)
    {
        _busy = true;
        StatusText.Text = label;

        // Every transfer starts from a clean slate: no leftover flood-wait line from an earlier run.
        FloodWaitItem.Visibility = Visibility.Collapsed;
        FloodWaitText.Text = string.Empty;

        ShowTransferWindow(label, cancellable, indeterminate);
        UpdateState();
    }

    /// <summary>Opens (or reuses) the separate progress window and starts it on a fresh operation.</summary>
    private void ShowTransferWindow(string header, bool cancellable, bool indeterminate)
    {
        if (_transferWindow is null)
        {
            _transferWindow = new TransferProgressWindow { Owner = this };
            _transferWindow.CancelRequested += TransferWindow_CancelRequested;
            _transferWindow.PauseResumeRequested += TransferWindow_PauseResumeRequested;
        }

        _transferWindow.Begin(header, cancellable, indeterminate);
        if (!_transferWindow.IsVisible)
            _transferWindow.Show();
    }

    private void ClearBusy()
    {
        _busy = false;
        CloseTransferWindow();
        UpdateState();
    }

    /// <summary>Closes the separate progress window (if open) and detaches its cancel handler.</summary>
    private void CloseTransferWindow()
    {
        if (_transferWindow is null)
            return;

        var window = _transferWindow;
        _transferWindow = null;
        window.CancelRequested -= TransferWindow_CancelRequested;
        window.PauseResumeRequested -= TransferWindow_PauseResumeRequested;
        window.ForceClose();
    }

    /// <summary>A folder that certainly exists — never derived from the remote filename.</summary>
    private static string SafeDownloadDirectory()
    {
        foreach (var candidate in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                     Path.GetTempPath(),
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return candidate;
        }

        return Path.GetTempPath();
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    /// <summary>
    /// The single place a transfer is allowed to touch the UI. Runs on the dispatcher because the
    /// <see cref="Progress{T}"/> was created on the UI thread.
    /// </summary>
    private void OnTransferSnapshot(TransferSnapshot snapshot)
    {
        // A drag-OUT download runs on this same channel but owns no window and blocks the UI thread while
        // Explorer copies; suppress the per-file status churn (see _dragOutActive) and let the drag finish.
        if (_dragOutActive)
            return;

        var line = DescribeSnapshot(snapshot);
        StatusText.Text = line;

        var running = snapshot.State is TransferState.Preparing or TransferState.Uploading
            or TransferState.Downloading or TransferState.WaitingForFloodLimit
            or TransferState.Paused or TransferState.Cancelling;

        var indeterminate =
            snapshot.State is TransferState.Uploading or TransferState.Downloading
            && snapshot.TotalBytes is null or <= 0;

        // Pause/Resume only makes sense while bytes are (or would be) moving with a known state.
        var pausable = snapshot.State is TransferState.Uploading or TransferState.Downloading
            or TransferState.Paused;

        if (_transferWindow is not null)
        {
            // During a batch the header stays "x of N: name"; a single transfer uses the per-state header.
            var header = _batchHeader ?? HeaderForSnapshot(snapshot);
            _transferWindow.Report(header, line, snapshot.Fraction ?? 0d, indeterminate);
            _transferWindow.SetCancellable(running, snapshot.State != TransferState.Cancelling);
            _transferWindow.SetPause(pausable, snapshot.State == TransferState.Paused);
        }

        if (snapshot.FloodWaitSecondsRemaining is int seconds)
        {
            var flood = $"FLOOD_WAIT {seconds}s — waiting as the server instructed";
            FloodWaitItem.Visibility = Visibility.Visible;
            FloodWaitText.Text = flood;
            _transferWindow?.SetFloodWait(flood);
        }
        else
        {
            FloodWaitItem.Visibility = Visibility.Collapsed;
            FloodWaitText.Text = string.Empty;
            _transferWindow?.SetFloodWait(null);
        }

        // A confirmed close waits for the transfer to report its terminal state — by then the
        // coordinator has already done its cleanup, so closing here cannot orphan a partial file.
        if (_closeRequested && snapshot.State is TransferState.Cancelled or TransferState.Completed or TransferState.Failed)
        {
            _closeRequested = false;
            Dispatcher.BeginInvoke(Close);
        }
    }

    /// <summary>Short window header for the current state; the detail line carries the byte/percent text.</summary>
    private static string HeaderForSnapshot(TransferSnapshot snapshot) => snapshot.State switch
    {
        TransferState.Preparing => $"{snapshot.Operation}: preparing…",
        TransferState.Uploading => "Uploading to Nuvia",
        TransferState.Downloading => "Downloading from Nuvia",
        TransferState.Paused => "Paused",
        TransferState.WaitingForFloodLimit => "Waiting for the server’s rate limit",
        TransferState.Cancelling => "Cancelling…",
        TransferState.Completed => $"{snapshot.Operation} complete",
        TransferState.Failed => $"{snapshot.Operation} failed",
        TransferState.Cancelled => $"{snapshot.Operation} cancelled",
        _ => snapshot.Operation,
    };

    /// <summary>
    /// Asks the coordinator to stop the running transfer when the user cancels from the progress window.
    /// Deliberately does not unblock the UI or move state itself: the coordinator owns the transitions and
    /// reports Cancelling -> Cancelled once cleanup has actually happened. During a flood-wait the same
    /// token interrupts the delay.
    /// </summary>
    private void TransferWindow_CancelRequested(object? sender, EventArgs e)
    {
        if (_transferCts is null || _transferCts.IsCancellationRequested)
            return;

        StatusText.Text = "Cancelling transfer…";
        _transferCts.Cancel();
    }

    /// <summary>
    /// Toggles pause/resume when the user clicks the button on the progress window. The coordinator owns
    /// the actual gating of the byte flow; here we only flip it and let the resulting snapshot update the UI.
    /// </summary>
    private void TransferWindow_PauseResumeRequested(object? sender, EventArgs e)
    {
        if (!_transfers.IsBusy)
            return;

        if (_transfers.IsPaused)
            _transfers.Resume();
        else
            _transfers.Pause();
    }

    /// <summary>
    /// Closing during a transfer must offer a clear choice rather than silently killing it. Yes stops
    /// the transfer and closes once its cleanup is done; No leaves everything running untouched.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closing || !_busy)
        {
            CloseTransferWindow();
            base.OnClosing(e);
            return;
        }

        var answer = MessageBox.Show(
            this,
            "A transfer is still running.\n\nClose Nuvia and stop it? Whatever has not been stored yet is discarded.",
            "Transfer in progress",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _closing = true;
        _transferWindow?.ShowCancelling();
        _transferCts?.Cancel();

        if (_transfers.IsBusy)
        {
            // Hold the window open until the coordinator reports the transfer has stopped.
            e.Cancel = true;
            _closeRequested = true;
            return;
        }

        CloseTransferWindow();
        base.OnClosing(e);
    }

    /// <summary>
    /// Detach the live remote-delete watch once the window is truly gone. The storage layer itself is
    /// owned and disposed by the App layer; here we only unsubscribe so no event fires into a dead window.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _storage.RemoteFilesDeleted -= OnRemoteFilesDeleted;
        Activated -= OnWindowActivated;
        _thumbCts?.Cancel();
        _thumbnails.Dispose();
        _announcementTimer?.Stop();
        _announcementCts.Cancel();
        base.OnClosed(e);
    }

    private static string DescribeSnapshot(TransferSnapshot snapshot)
    {
        var bytes = snapshot.TotalBytes is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{FormatSize(snapshot.BytesTransferred)} of {FormatSize(snapshot.TotalBytes.Value)}")
            : FormatSize(snapshot.BytesTransferred);

        var percent = snapshot.Fraction is double fraction
            ? string.Create(CultureInfo.InvariantCulture, $" ({fraction * 100:0}%)")
            : string.Empty;

        return snapshot.State switch
        {
            TransferState.Idle => "Ready",
            TransferState.Preparing => $"{snapshot.Operation}: preparing…",
            TransferState.Uploading => $"Uploading — {bytes}{percent}",
            TransferState.Downloading => $"Downloading — {bytes}{percent}",
            TransferState.Paused => $"Paused — {bytes}{percent}",
            TransferState.WaitingForFloodLimit => $"FLOOD_WAIT — {snapshot.FloodWaitSecondsRemaining ?? 0}s remaining",
            TransferState.Cancelling => $"{snapshot.Operation}: cancelling…",
            TransferState.Completed => $"{snapshot.Operation} complete — {bytes}",
            TransferState.Failed => $"{snapshot.Operation} failed",
            TransferState.Cancelled => $"{snapshot.Operation} cancelled",
            _ => snapshot.Operation,
        };
    }

    private static string Describe(Exception ex)
    {
        if (ex is OperationCanceledException)
            return "Transfer cancelled.";

        // Cancellation and every failure class Phase 5 names come from one catalog, so a raw RPC
        // string is never shown. The local switch is the fallback for anything unclassified.
        var classified = TransferErrors.Classify(ex);
        return classified.Class == TransferErrorClass.Unknown
            ? ex switch
            {
                FileReferenceExpiredException => "The remote file reference could not be refreshed.",
                UnauthorizedAccessException => "Access to the local file was denied.",
                IOException io => io.Message,
                _ => ex.Message,
            }
            : classified.UserMessage;
    }

    private void ShowError(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
