using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualBasic.FileIO;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace InstantReplay;

public sealed record CategoryFilter(string Name, int Count)
{
    public string Label => Count > 0 ? $"{ReplayItem.FormatCategory(Name)}  {Count}" : ReplayItem.FormatCategory(Name);
}

public sealed class ReplayItem : INotifyPropertyChanged
{
    private long _sizeBytes;
    private TimeSpan _duration;
    private int _videoWidth;
    private int _videoHeight;
    private double _fps;
    private ImageSource? _thumbnail;

    public ReplayItem(string folderPath, string videoPath, string category, DateTime created, long sizeBytes)
    {
        FolderPath = folderPath;
        VideoPath = videoPath;
        Category = string.IsNullOrWhiteSpace(category) ? "desktop" : category.ToLowerInvariant();
        Created = created;
        _sizeBytes = sizeBytes;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FolderPath { get; }
    public string VideoPath { get; }
    public string Category { get; }
    public string BadgeCategory => FormatBadgeCategory(Category);
    public bool IsDesktop => string.Equals(Category, "desktop", StringComparison.OrdinalIgnoreCase);
    public string CategoryGlyph => IsDesktop ? "\uE7F4" : "\uE7FC";
    public Microsoft.UI.Xaml.Visibility DesktopIconVisibility => IsDesktop ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility GameIconVisibility => IsDesktop ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public DateTime Created { get; }
    public string ThumbPath => ReplayLibrary.ThumbnailPathFor(VideoPath);

    internal bool Busy;
    internal Task? EnsureTask;
    public bool MetadataLoaded { get; internal set; }

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (_sizeBytes == value) return;
            _sizeBytes = value;
            Raise(nameof(SizeBytes), nameof(SizeText), nameof(TopRightBadgeText), nameof(MetaLineText));
        }
    }

    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            if (_duration == value) return;
            _duration = value;
            Raise(nameof(Duration), nameof(DurationText));
        }
    }

    public int VideoWidth
    {
        get => _videoWidth;
        set
        {
            if (_videoWidth == value) return;
            _videoWidth = value;
            Raise(nameof(VideoWidth), nameof(QualityText), nameof(MetaLineText), nameof(ResolutionText));
        }
    }

    public int VideoHeight
    {
        get => _videoHeight;
        set
        {
            if (_videoHeight == value) return;
            _videoHeight = value;
            Raise(nameof(VideoHeight), nameof(QualityText), nameof(MetaLineText), nameof(ResolutionText));
        }
    }

    public double Fps
    {
        get => _fps;
        set
        {
            if (Math.Abs(_fps - value) < 0.01) return;
            _fps = value;
            Raise(nameof(Fps), nameof(FpsText), nameof(QualityText), nameof(TopRightBadgeText), nameof(MetaLineText));
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            Raise(nameof(Thumbnail));
        }
    }

    public string DateText => Created.ToString("MMM d, yyyy · HH:mm", CultureInfo.InvariantCulture).ToLowerInvariant();

    public string DurationText => Duration > TimeSpan.Zero
        ? (Duration.TotalHours >= 1
            ? $"{(int)Duration.TotalHours}:{Duration.Minutes:00}:{Duration.Seconds:00}"
            : $"{(int)Duration.TotalMinutes}:{Duration.Seconds:00}")
        : "--:--";

    public string SizeText => FormatSize(SizeBytes);

    public string FpsText => Fps > 0 ? $"{Fps:0} fps" : "";

    public string TopRightBadgeText => Fps > 0 ? $"{Fps:0} fps · {SizeText}" : SizeText;

    public string QualityText
    {
        get
        {
            if (VideoHeight <= 0) return "—";
            return Fps > 0 ? $"{VideoHeight}p · {Fps:0} fps" : $"{VideoHeight}p";
        }
    }

    public bool IsEdited => Path.GetFileNameWithoutExtension(VideoPath).Contains("_cut", StringComparison.OrdinalIgnoreCase);

    public string CardTitleText => IsEdited ? $"{DateText} · edited" : DateText;

    public string ResolutionText => VideoWidth > 0 && VideoHeight > 0
        ? $"{VideoWidth}×{VideoHeight}"
        : (Fps > 0 ? $"{Fps:0} fps" : "video");

    public string MetaLineText => VideoHeight > 0
        ? $"{DateText} · {VideoHeight}p"
        : DateText;

    public static string FormatCategory(string category)
    {
        string display = category.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(display) ? "desktop" : display;
    }

    private static string FormatBadgeCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "Desktop";
        string c = category.Trim().ToLowerInvariant();
        return c switch
        {
            "counter-strike global offensive" or "counter-strike: global offensive" => "cs:go",
            "counter-strike 2" => "cs2",
            "counter-strike source" => "cs:s",
            "grand theft auto v" or "grand theft auto 5" => "gta v",
            "playerunknown's battlegrounds" or "pubg battlegrounds" => "pubg",
            "tom clancy's rainbow six siege" or "rainbow six siege" => "r6 siege",
            "league of legends" => "lol",
            "escape from tarkov" => "tarkov",
            "red dead redemption 2" => "rdr2",
            "world of warcraft" => "wow",
            "call of duty modern warfare" => "cod mw",
            "dead by daylight" => "dbd",
            _ => FormatCategory(category)
        };
    }

    private static string FormatSize(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024 ? $"{mb / 1024.0:0.0} gb" : $"{mb:0.0} mb";
    }

    private void Raise(params string[] names)
    {
        foreach (string n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public sealed class ReplayLibrary
{
    private readonly record struct ScanEntry(string Folder, string Video, string Category, DateTime Created, long Size);

    private readonly DispatcherQueueTimer _debounce;
    private FileSystemWatcher? _watcher;
    private string _watchedDir = "";
    private string _activeFilter = "all";
    private readonly List<ReplayItem> _allItems = [];

    public ReplayLibrary(DispatcherQueue dq)
    {
        _debounce = dq.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(500);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => FolderChanged?.Invoke();
        DispatcherQueue = dq;
    }

    public DispatcherQueue DispatcherQueue { get; }
    public ObservableCollection<ReplayItem> Items { get; } = new();
    public IReadOnlyList<ReplayItem> AllItems => _allItems;
    public string ActiveFilter => _activeFilter;
    public event Action? FolderChanged;

    public static void EnsureBaseFolders(string rootDir)
    {
        try
        {
            Directory.CreateDirectory(rootDir);
            Directory.CreateDirectory(Path.Combine(rootDir, "desktop"));
        }
        catch (Exception ex)
        {
            Logger.Warn($"could not initialize replay folders at '{rootDir}': {ex.Message}");
        }
    }

    public static string ThumbnailPathFor(string videoPath)
    {
        string stem = Path.GetFileNameWithoutExtension(videoPath);
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(videoPath.ToLowerInvariant()));
        string suffix = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "replay_thumbnails", $"{stem}_{suffix}.jpg");
    }

    public List<CategoryFilter> GetCategoryFilters()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allItems)
        {
            counts.TryGetValue(item.Category, out int c);
            counts[item.Category] = c + 1;
        }

        var list = new List<CategoryFilter> { new("all", _allItems.Count) };
        if (counts.TryGetValue("desktop", out int desktopCount))
            list.Add(new("desktop", desktopCount));

        foreach (var kv in counts.Where(k => !string.Equals(k.Key, "desktop", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(new(kv.Key.ToLowerInvariant(), kv.Value));
        }
        return list;
    }

    public void SetFilter(string category)
    {
        string normalized = string.IsNullOrWhiteSpace(category) ? "all" : category.ToLowerInvariant();
        if (string.Equals(_activeFilter, normalized, StringComparison.OrdinalIgnoreCase))
        {
            SyncFilteredItems();
            return;
        }
        _activeFilter = normalized;
        SyncFilteredItems();
    }

    private void SyncFilteredItems()
    {
        List<ReplayItem> desired = string.Equals(_activeFilter, "all", StringComparison.OrdinalIgnoreCase)
            ? _allItems.ToList()
            : _allItems.Where(x => string.Equals(x.Category, _activeFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var desiredSet = new HashSet<ReplayItem>(desired);
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(Items[i])) Items.RemoveAt(i);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            int current = Items.IndexOf(item);
            if (current < 0) Items.Insert(Math.Min(i, Items.Count), item);
            else if (current != i) Items.Move(current, i);
        }
    }

    public async Task RefreshAsync(string dir)
    {
        EnsureBaseFolders(dir);
        Watch(dir);

        List<ScanEntry> found = await Task.Run(() => Scan(dir));
        var foundKeys = new HashSet<string>(found.Select(f => f.Video), StringComparer.OrdinalIgnoreCase);

        for (int i = _allItems.Count - 1; i >= 0; i--)
        {
            if (!foundKeys.Contains(_allItems[i].VideoPath))
                _allItems.RemoveAt(i);
        }

        var existing = _allItems.ToDictionary(x => x.VideoPath, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < found.Count; i++)
        {
            ScanEntry f = found[i];
            if (!existing.TryGetValue(f.Video, out ReplayItem? item))
            {
                item = new ReplayItem(f.Folder, f.Video, f.Category, f.Created, f.Size);
                existing[f.Video] = item;
                _allItems.Insert(Math.Min(i, _allItems.Count), item);
            }
            else
            {
                item.SizeBytes = f.Size;
                int cur = _allItems.IndexOf(item);
                if (cur >= 0 && cur != i)
                {
                    _allItems.RemoveAt(cur);
                    _allItems.Insert(Math.Min(i, _allItems.Count), item);
                }
            }
        }

        // If active filter category no longer exists, revert to "all"
        if (!string.Equals(_activeFilter, "all", StringComparison.OrdinalIgnoreCase) &&
            !_allItems.Any(x => string.Equals(x.Category, _activeFilter, StringComparison.OrdinalIgnoreCase)))
        {
            _activeFilter = "all";
        }

        SyncFilteredItems();
        _ = EnsureAllAsync(_allItems.ToList());
    }

    private static List<ScanEntry> Scan(string dir)
    {
        var list = new List<ScanEntry>();
        try
        {
            if (!Directory.Exists(dir)) return list;

            // 1. Direct .mp4 files in root (treated as desktop)
            foreach (string mp4 in Directory.EnumerateFiles(dir, "*.mp4"))
                TryAddFile(list, dir, mp4, "desktop");

            // 2. Category subfolders: <root>\<category>\*.mp4 or <root>\<category>\replay_*\replay.mp4
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string subName = Path.GetFileName(sub);
                if (subName.StartsWith("replay_", StringComparison.OrdinalIgnoreCase))
                {
                    string legacyVideo = Path.Combine(sub, "replay.mp4");
                    if (File.Exists(legacyVideo))
                        TryAddFile(list, sub, legacyVideo, "desktop");
                    continue;
                }

                string category = subName.ToLowerInvariant();
                foreach (string mp4 in Directory.EnumerateFiles(sub, "*.mp4"))
                    TryAddFile(list, sub, mp4, category);

                foreach (string nested in Directory.EnumerateDirectories(sub, "replay_*"))
                {
                    string legacyVideo = Path.Combine(nested, "replay.mp4");
                    if (File.Exists(legacyVideo))
                        TryAddFile(list, nested, legacyVideo, category);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"gallery scan failed for '{dir}': {ex.Message}");
        }

        list.Sort((a, b) => b.Created.CompareTo(a.Created));
        return list;
    }

    private static void TryAddFile(List<ScanEntry> list, string folder, string videoPath, string category)
    {
        try
        {
            var info = new FileInfo(videoPath);
            if (!info.Exists || info.Length == 0) return;
            DateTime created = TryParseDate(Path.GetFileNameWithoutExtension(videoPath))
                ?? TryParseDate(Path.GetFileName(folder))
                ?? info.LastWriteTime;
            list.Add(new ScanEntry(folder, videoPath, category, created, info.Length));
        }
        catch { }
    }

    private static DateTime? TryParseDate(string name)
    {
        const string prefix = "replay_";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = name[prefix.Length..];
        if (rest.Length < 15) return null;
        return DateTime.TryParseExact(rest[..15], "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt) ? dt : null;
    }

    private async Task EnsureAllAsync(List<ReplayItem> items)
    {
        foreach (ReplayItem item in items) await EnsureAsync(item);
    }

    public Task EnsureAsync(ReplayItem item)
    {
        bool thumbOk = item.Thumbnail != null && File.Exists(item.ThumbPath);
        if (item.MetadataLoaded && thumbOk) return Task.CompletedTask;
        if (item.EnsureTask is { IsCompleted: false } existing) return existing;

        Task task = EnsureCoreAsync(item, thumbOk);
        item.EnsureTask = task;
        return task;
    }

    private static async Task EnsureCoreAsync(ReplayItem item, bool thumbOk)
    {
        item.Busy = true;
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(item.VideoPath);
            if (!item.MetadataLoaded) await LoadMetadataAsync(item, file);
            if (!thumbOk) await EnsureThumbnailAsync(item, file);
        }
        catch (Exception ex)
        {
            Logger.Warn($"gallery prepare failed for '{item.VideoPath}': {ex.Message}");
        }
        finally
        {
            item.Busy = false;
            item.EnsureTask = null;
        }
    }

    private static async Task LoadMetadataAsync(ReplayItem item, StorageFile file)
    {
        try
        {
            VideoProperties vp = await file.Properties.GetVideoPropertiesAsync();
            item.Duration = vp.Duration;
            item.VideoWidth = (int)vp.Width;
            item.VideoHeight = (int)vp.Height;

            IDictionary<string, object> props =
                await file.Properties.RetrievePropertiesAsync(new[] { "System.Video.FrameRate" });
            if (props.TryGetValue("System.Video.FrameRate", out object? v) && v is uint fr && fr > 0)
                item.Fps = fr / 1000.0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"reading video properties failed: {ex.Message}");
        }
        item.MetadataLoaded = true;
    }

    private static async Task EnsureThumbnailAsync(ReplayItem item, StorageFile video)
    {
        string thumbPath = item.ThumbPath;
        if (File.Exists(thumbPath))
        {
            try
            {
                await LoadThumbnailAsync(item, thumbPath);
                return;
            }
            catch { }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
        byte[]? bytes = null;
        try
        {
            using StorageItemThumbnail thumb =
                await video.GetThumbnailAsync(ThumbnailMode.VideosView, 520, ThumbnailOptions.UseCurrentScale);
            if (thumb != null && thumb.Type == ThumbnailType.Image && thumb.Size > 0)
                bytes = await ReadThumbnailBytesAsync(thumb);
        }
        catch { }

        if (bytes == null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var clip = await MediaClip.CreateFromFileAsync(video).AsTask(timeout.Token);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            TimeSpan position = TimeSpan.FromSeconds(Math.Min(1, clip.OriginalDuration.TotalSeconds / 2));
            using var image = await composition.GetThumbnailAsync(position, 520, 0,
                VideoFramePrecision.NearestKeyFrame).AsTask(timeout.Token);
            bytes = await ReadThumbnailBytesAsync(image);
        }

        string temporary = thumbPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, thumbPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
        await LoadThumbnailAsync(item, thumbPath);
    }

    private static async Task<byte[]> ReadThumbnailBytesAsync(IRandomAccessStream stream)
    {
        if (stream.Size == 0 || stream.Size > 16 * 1024 * 1024)
            throw new InvalidDataException("thumbnail is empty or too large.");
        var bytes = new byte[(int)stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        uint loaded = await reader.LoadAsync((uint)bytes.Length);
        if (loaded != bytes.Length) throw new InvalidDataException("thumbnail is incomplete.");
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task LoadThumbnailAsync(ReplayItem item, string thumbPath)
    {
        StorageFile thumbFile = await StorageFile.GetFileFromPathAsync(thumbPath);
        using IRandomAccessStream stream = await thumbFile.OpenReadAsync();
        var bmp = new BitmapImage { DecodePixelWidth = 520 };
        await bmp.SetSourceAsync(stream);
        item.Thumbnail = bmp;
    }

    public async Task<bool> DeleteAsync(ReplayItem item)
    {
        try
        {
            bool isLegacyFolder = Path.GetFileName(item.FolderPath).StartsWith("replay_", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(item.VideoPath), "replay.mp4", StringComparison.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                if (isLegacyFolder)
                    FileSystem.DeleteDirectory(item.FolderPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else
                    FileSystem.DeleteFile(item.VideoPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            });
            try { File.Delete(item.ThumbPath); } catch { }
            _allItems.Remove(item);
            Items.Remove(item);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"gallery delete failed '{item.VideoPath}': {ex.Message}");
            return false;
        }
    }

    private void Watch(string dir)
    {
        if (string.Equals(_watchedDir, dir, StringComparison.OrdinalIgnoreCase) && _watcher != null) return;

        _watcher?.Dispose();
        _watcher = null;
        _watchedDir = dir;

        try
        {
            if (!Directory.Exists(dir)) return;

            var watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            };
            FileSystemEventHandler onChange = (_, _) => DispatcherQueue.TryEnqueue(() => { _debounce.Stop(); _debounce.Start(); });
            watcher.Created += onChange;
            watcher.Deleted += onChange;
            watcher.Renamed += (_, _) => DispatcherQueue.TryEnqueue(() => { _debounce.Stop(); _debounce.Start(); });
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            Logger.Warn($"gallery could not watch '{dir}': {ex.Message}");
        }
    }
}
