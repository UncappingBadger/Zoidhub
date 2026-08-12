using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using ZoidHub.Models;
using ZoidHub.Services;

namespace ZoidHub;

public partial class MainWindow : Window
{
    private const string WebMapHost = "zoidhub.app";
    private const string TileDataHost = "zoidmap.tiles";
    private const int MaxFloor = 8;

    private const int WM_SYSCOMMAND = 0x112;
    private const int SC_SIZE_BOTTOMRIGHT = 0xF008;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private readonly MarkerStore _markerStore = new();
    private readonly SettingsService _settingsService = new();
    private readonly UpdateCheckService _updateCheckService = new();
    private AppSettings _settings = new();
    // Hardcoded until map selection exists - see MapDataService's doc comment for the plan.
    private readonly string _activeMapId = MapDataService.VanillaMapId;
    private LivePositionService? _liveService;
    private LanShareServer? _lanShareServer;
    private MapRenderService? _activeRenderer;
    private Task? _activeRenderTask;
    private bool _isFullscreen;
    private double _preFullscreenLeft, _preFullscreenTop, _preFullscreenWidth, _preFullscreenHeight;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private CancellationTokenSource _renderCts = new();
    private TaskCompletionSource<bool>? _startRenderTcs;
    private string? _pendingMissingRootWarning;

    public MainWindow()
    {
        InitializeComponent();
        CpuAffinity.PinCurrentProcess();
        _settings = _settingsService.Load();
        ApplyMapDataRootWithFallback();
        UpdateSpeedButtons();
        _updateCheckService.Checked += result => Dispatcher.Invoke(() =>
        {
            UpdateAvailableButton.Visibility = result.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        });
        _ = _updateCheckService.CheckAsync(); // silent on-launch check; failures/no-update are invisible by design
        Loaded += async (_, _) =>
        {
            if (_pendingMissingRootWarning != null)
            {
                MessageBox.Show(
                    $"ZoidHub is configured to store map data at:\n\n{_pendingMissingRootWarning}\n\n" +
                    "That drive isn't available right now (disconnected external/removable drive?) - " +
                    "using the default location for this session instead. Reconnect the drive and " +
                    "relaunch, or click \"Change Location\" once the map loads to pick somewhere else for good.",
                    "ZoidHub", MessageBoxButton.OK, MessageBoxImage.Warning);
                _pendingMissingRootWarning = null;
            }
            await InitializeWebViewAsync();
        };
        // "Left the map screen and came back" - covers both alt-tabbing away/back and
        // minimizing/restoring, since both fire Activated on return. Re-centers on wherever the
        // player currently is rather than leaving the view wherever it was last panned to.
        Activated += (_, _) => RecenterOnLivePosition();
        Closed += (_, _) =>
        {
            _renderCts.Cancel();
            _liveService?.Dispose();
            _lanShareServer?.Stop();
            AppLogger.Log("ZoidHub closed");
        };
    }

    // If AppSettings.MapDataRoot points at a drive/path that isn't there right now (a removed
    // drive letter, not just an existing-but-empty folder - that case is already handled fine by
    // EnsureMapRenderedAsync auto-prompting a fresh render), falling all the way through into
    // RemapTileHost's Directory.CreateDirectory used to throw and get caught by
    // InitializeWebViewAsync's generic catch, surfacing the same misleading "WebView2 couldn't
    // start... Runtime isn't installed" message regardless of what actually went wrong. Checking
    // this upfront and falling back to the default location for the session - rather than hard-
    // failing - is what actually lets the app still start normally, including reaching "Change
    // Location" to fix this for good, instead of a dead end.
    private void ApplyMapDataRootWithFallback()
    {
        var root = _settings.MapDataRoot;
        if (string.IsNullOrEmpty(root))
        {
            MapDataService.RootOverride = null;
            return;
        }

        var driveRoot = Path.GetPathRoot(root);
        if (!string.IsNullOrEmpty(driveRoot) && Directory.Exists(driveRoot))
        {
            MapDataService.RootOverride = root;
            return;
        }

        AppLogger.Log($"ApplyMapDataRootWithFallback: configured location '{root}' isn't available right now - using the default location for this session.");
        MapDataService.RootOverride = null;
        _pendingMissingRootWarning = root;
    }

    // GameLocator.FindGameInstallDir is 100% Steam-registry-based, no fallback at all - a GOG/Epic
    // copy or a manually-relocated install Steam doesn't know about gets nothing. Checked first
    // (not as a fallback to GameLocator) since a manual override the user deliberately set via
    // BrowseGameButton_Click should always win, even if a Steam install also happens to exist.
    private string? ResolvePzInstallDir()
    {
        var manual = _settings.PzInstallDir;
        if (!string.IsNullOrEmpty(manual) && IsValidPzInstallDir(manual)) return manual;
        return GameLocator.FindGameInstallDir();
    }

    private static bool IsValidPzInstallDir(string dir) =>
        Directory.Exists(dir) && File.Exists(Path.Combine(dir, "ProjectZomboid64.exe"));

    private void RecenterOnLivePosition()
    {
        if (LivePositionCheckBox.IsChecked != true) return;
        PostMessage(new { type = "recenterOnLive" });
    }

    private async System.Threading.Tasks.Task InitializeWebViewAsync()
    {
        try
        {
            PayloadExtractor.EnsureExtracted();

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZoidHub", "WebView2");
            ClearWebViewCacheOnVersionChange(userDataFolder);
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await MapWebView.EnsureCoreWebView2Async(env);

            MapWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                WebMapHost, PayloadExtractor.WebMapDir, CoreWebView2HostResourceAccessKind.Allow);

            RemapTileHost();

            MapWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            MapWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            MapWebView.CoreWebView2.Navigate($"https://{WebMapHost}/index.html");

            RefreshBridgeModDetection();

            _ = EnsureMapRenderedAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"WebView2 init failed: {ex.Message}");
            MessageBox.Show(
                "ZoidHub couldn't start its map view. This usually means the WebView2 Runtime " +
                "isn't installed. It ships with Windows 11 by default - if you're on an older " +
                "Windows install, get it from Microsoft's WebView2 download page.",
                "ZoidHub", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Maps TileDataHost at whatever folder MapDataService currently resolves to for the
    /// active map. Re-callable, not just a one-time init step - SetVirtualHostNameToFolderMapping
    /// replaces an existing mapping for the same host name rather than erroring, which is what
    /// lets ChooseLocationButton_Click point the map at a freshly-picked drive without restarting
    /// the app. If nothing's rendered anywhere yet, still map the host at the path the bundled
    /// renderer WILL write to - SetVirtualHostNameToFolderMapping resolves individual file
    /// requests lazily, but throws immediately if the folder itself doesn't exist yet, so create
    /// it first.</summary>
    private void RemapTileHost()
    {
        var tileDir = MapDataService.FindMapHtmlDir(MapDataService.VanillaMapId)
            ?? Path.Combine(MapDataService.GetOutputDir(MapDataService.VanillaMapId), "html");
        Directory.CreateDirectory(tileDir);
        MapWebView.CoreWebView2?.SetVirtualHostNameToFolderMapping(
            TileDataHost, tileDir, CoreWebView2HostResourceAccessKind.Allow);
    }

    /// <summary>WebView2 caches the WebMap content it's served (HTML/JS/CSS via
    /// SetVirtualHostNameToFolderMapping) inside its own profile folder, independent of
    /// PayloadExtractor's own version-gated re-extraction of the underlying files - confirmed
    /// live while testing (an edited zoidmap.js kept running the old version until this profile
    /// folder was cleared by hand). Without this, a future ZoidHub update that changes the web
    /// map code could get silently served stale from an existing user's cache. Mirrors
    /// PayloadExtractor's own version-marker pattern.</summary>
    private static void ClearWebViewCacheOnVersionChange(string userDataFolder)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        var marker = Path.Combine(userDataFolder, $".webview-version-{version}");
        if (File.Exists(marker)) return;

        if (Directory.Exists(userDataFolder))
        {
            try { Directory.Delete(userDataFolder, recursive: true); }
            catch (Exception ex) { AppLogger.Log($"ClearWebViewCacheOnVersionChange: couldn't clear old profile: {ex.Message}"); }
        }
        Directory.CreateDirectory(userDataFolder);
        File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
    }

    private async Task EnsureMapRenderedAsync()
    {
        try
        {
            await EnsureMapRenderedInnerAsync();
        }
        catch (OperationCanceledException)
        {
            // The user picked a new map data location while this was sitting at the speed-choice
            // prompt (see ChooseLocationButton_Click, which cancels _startRenderTcs) - a fresh
            // call is already on its way in to restart against the new location, so just unwind
            // quietly here instead of continuing toward the stale path captured below.
        }
    }

    private async Task EnsureMapRenderedInnerAsync()
    {
        var outputPath = MapDataService.GetOutputDir(MapDataService.VanillaMapId);
        var existingDir = MapDataService.FindMapHtmlDir(MapDataService.VanillaMapId);

        // The completion marker alone can't tell a healthy render from a broken one - a texture-
        // unpack that silently produced nothing (see MapDataService.HasHealthyUnpackedTextures)
        // still lets the render step exit 0 and get marked complete. Re-checking this on every
        // launch, not just before a fresh render, is what lets an install that got bitten by that
        // bug on an earlier version self-heal after updating, instead of staying broken forever
        // because "a marker file exists" was good enough to skip re-rendering.
        if (existingDir != null && MapRenderService.IsFloorRendered(existingDir, 0)
            && MapDataService.HasHealthyUnpackedTextures(outputPath))
        {
            return; // already have real tiles (e.g. a previous run, or one still filling in floors)
        }

        var pzRoot = ResolvePzInstallDir();
        if (pzRoot == null)
        {
            AppLogger.Log("EnsureMapRenderedAsync: Project Zomboid install not found - can't auto-render.");
            ShowRenderStatus("Couldn't find your Project Zomboid install - the map can't render yet.", 0, "");
            BrowseGameButton.Visibility = Visibility.Visible;
            return;
        }

        // A real run measured live during this project's own testing: ~1.4M individual tiles at
        // roughly 185KB average (sampled), landing north of 150GB total for a full 9-floor render
        // even with omit_levels trimming the two deepest zoom levels - a genuinely large, easy to
        // underestimate requirement for what looks like a small companion app. Worse, running out
        // mid-render doesn't cleanly fail: pzmap2dzi's worker pool can hit "OSError: No space left
        // on device" on individual tiles without the overall process exit code reflecting it (see
        // RenderFloorsAsync's own fatal-error detection, added after hitting this for real) -
        // catching it here, before starting, avoids burning hours of CPU on a render that can't
        // actually finish correctly regardless of that detection existing.
        const long MinRequiredFreeBytes = 150L * 1024 * 1024 * 1024;
        var driveRoot = Path.GetPathRoot(outputPath);
        if (driveRoot != null)
        {
            try
            {
                var freeBytes = new DriveInfo(driveRoot).AvailableFreeSpace;
                if (freeBytes < MinRequiredFreeBytes)
                {
                    var freeGb = freeBytes / 1024.0 / 1024 / 1024;
                    AppLogger.Log($"EnsureMapRenderedAsync: only {freeGb:0.#} GB free on {driveRoot} - refusing to start render (need ~150GB+).");
                    ShowRenderStatus($"Not enough free disk space to render the map ({freeGb:0.#} GB free, ~150 GB+ needed).", 0, "");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"EnsureMapRenderedAsync: disk space check failed, proceeding anyway: {ex.Message}");
            }
        }

        var renderer = new MapRenderService(pzRoot, outputPath, workerCount: MapRenderService.ResolveWorkerCount(_settings.RenderSpeed));
        if (!renderer.IsBundleAvailable())
        {
            AppLogger.Log("EnsureMapRenderedAsync: bundled renderer not found.");
            return;
        }

        // A first-ever render can run for hours of real CPU load, so it waits for an explicit
        // Start click rather than beginning the moment the game install is found - the user picks
        // Light/Fast first (see the render status bar's speed buttons), then clicks Start.
        _startRenderTcs = new TaskCompletionSource<bool>();
        ShowSpeedChoicePrompt();
        await _startRenderTcs.Task;

        _activeRenderer = renderer;
        _activeRenderTask = RunRenderPassAsync(renderer, _renderCts.Token);
        await _activeRenderTask;
    }

    // Shown even when the drive passing EnsureMapRenderedInnerAsync's disk-space check has plenty
    // of room - "enough free space right now" isn't the same as "a drive the user actually wants
    // ~150-250GB permanently used on" (e.g. a laptop's only SSD). Surfacing this before Start,
    // not just when the check fails, is what actually lets someone redirect proactively instead
    // of only finding out via ChooseLocationButton after committing an undesired drive.
    private void ShowSpeedChoicePrompt()
    {
        var driveRoot = Path.GetPathRoot(MapDataService.GetOutputDir(MapDataService.VanillaMapId)) ?? "C:\\";
        RenderStatusBar.Visibility = Visibility.Visible;
        RenderStatusTitle.Text = $"This one-time render needs ~150GB+ free space and will be written to {driveRoot} - " +
            "if you don't want that, click \"Change Location\" first. Then choose a speed and click Start.";
        RenderProgressBar.Visibility = Visibility.Collapsed;
        RenderStatusDetail.Visibility = Visibility.Collapsed;
        StartRenderButton.Visibility = Visibility.Visible;
        BrowseGameButton.Visibility = Visibility.Collapsed;
    }

    private void StartRenderButton_Click(object sender, RoutedEventArgs e)
    {
        StartRenderButton.Visibility = Visibility.Collapsed;
        RenderProgressBar.Visibility = Visibility.Visible;
        RenderStatusDetail.Visibility = Visibility.Visible;
        _startRenderTcs?.TrySetResult(true);
    }

    /// <summary>Lets someone whose OS drive doesn't (and won't ever) have the ~150-250GB a full
    /// render needs point map data at a different drive/folder instead - added after a real user
    /// hit exactly that wall on a C: drive too small to ever clear the pre-render disk check.
    /// Only meaningful before a render is actually writing tiles (see the _activeRenderer guard
    /// below) - already-rendered data doesn't move itself, and switching mid-render would just
    /// mean two locations with partial data.</summary>
    private void ChooseLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRenderer != null)
        {
            MessageBox.Show(
                "Can't change the map data location while a render is in progress - wait for it " +
                "to finish, or close and relaunch ZoidHub first.",
                "ZoidHub", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder for ZoidHub's map data (needs ~150-250 GB free)",
        };
        if (dialog.ShowDialog() != true) return;

        _settings.MapDataRoot = dialog.FolderName;
        _settingsService.Save(_settings);
        MapDataService.RootOverride = dialog.FolderName;
        AppLogger.Log($"ChooseLocationButton_Click: map data root set to {dialog.FolderName}");

        RemapTileHost();

        // If EnsureMapRenderedInnerAsync is currently sitting at the speed-choice prompt awaiting
        // _startRenderTcs, it already captured the OLD output path in a local variable before this
        // ran - cancelling here (caught by EnsureMapRenderedAsync's wrapper) lets this fresh call
        // re-evaluate everything, including the disk-space check, against the new location instead
        // of silently continuing to render to the old one.
        _startRenderTcs?.TrySetCanceled();
        _ = EnsureMapRenderedAsync();
    }

    /// <summary>Only shown once auto-detect (GameLocator.FindGameInstallDir, Steam-registry-only,
    /// no fallback) has already failed - lets someone on a GOG/Epic copy or a manually-relocated
    /// install point ZoidHub at it directly instead of being stuck with no way forward.</summary>
    private void BrowseGameButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select your Project Zomboid install folder (contains ProjectZomboid64.exe)",
        };
        if (dialog.ShowDialog() != true) return;

        if (!IsValidPzInstallDir(dialog.FolderName))
        {
            MessageBox.Show(
                "That folder doesn't look like a Project Zomboid install - ProjectZomboid64.exe " +
                "wasn't found there. Select the folder that directly contains it.",
                "ZoidHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.PzInstallDir = dialog.FolderName;
        _settingsService.Save(_settings);
        AppLogger.Log($"BrowseGameButton_Click: manual PZ install dir set to {dialog.FolderName}");

        BrowseGameButton.Visibility = Visibility.Collapsed;
        _ = EnsureMapRenderedAsync();
    }

    /// <summary>No way exists (or realistically could exist, short of hashing the whole game
    /// install on every launch) to detect "the game map actually changed" - this is the honest
    /// alternative: wipes the current render entirely (both texture/ and html/, not just the
    /// completion marker - a Project Zomboid update could change texture packs too, not just map
    /// layout, so a stale partial texture set left over from a lighter-weight "just re-render on
    /// top of what's there" approach could reintroduce the exact black/missing-object bug this
    /// project already root-caused once) and re-triggers EnsureMapRenderedAsync fresh, which
    /// naturally re-runs the disk-space check and speed-choice prompt exactly like a first-ever
    /// render.</summary>
    private void RerenderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRenderer != null)
        {
            MessageBox.Show(
                "A render is already in progress - wait for it to finish before starting another.",
                "ZoidHub", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            "This will delete the current map render and generate a fresh one from your current " +
            "Project Zomboid install - useful after a game update changes the map. It can take " +
            "hours and use significant CPU while running. Continue?",
            "ZoidHub", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        var outputDir = MapDataService.GetOutputDir(_activeMapId);
        AppLogger.Log($"RerenderButton_Click: deleting existing render at {outputDir} to force a fresh one.");
        try
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"RerenderButton_Click: failed to delete existing render: {ex.Message}");
            MessageBox.Show(
                "Couldn't delete the existing map render - see the app log for details.",
                "ZoidHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Directory.Delete above just removed the folder RemapTileHost's virtual host mapping
        // still points at (the mapping itself doesn't need re-creating, only what's underneath
        // it) - reload so the WebMap re-fetches map_info.json, gets a 404, and shows its own
        // "tiles not found yet" status instead of a stale view of the now-deleted map.
        Directory.CreateDirectory(Path.Combine(outputDir, "html"));
        MapWebView.CoreWebView2?.Reload();

        _ = EnsureMapRenderedAsync();
    }

    /// <summary>Runs unpack + the combined render, reporting progress to the status bar. Also
    /// the resume path when the user flips the speed toggle mid-render (see SpeedMode_Click) -
    /// pzmap2dzi skips tiles it already finished, so restarting with a different worker count
    /// picks up where it left off rather than starting over.</summary>
    private async Task RunRenderPassAsync(MapRenderService renderer, CancellationToken ct)
    {
        var progress = new Progress<RenderProgress>(p =>
        {
            switch (p.Stage)
            {
                case RenderStage.Unpacking:
                    ShowRenderStatus("Rendering World - This may take some time", 0, "Reading game textures...");
                    break;
                case RenderStage.RenderingFloor:
                    ShowRenderStatus("Rendering World - This may take some time", p.FloorPercent, $"{p.FloorPercent:0}%");
                    break;
            }
        });

        try
        {
            await renderer.EnsureUnpackedAsync(progress, ct);
            if (ct.IsCancellationRequested) return;

            // All floors in one combined pass - a measured timing run showed scoping to a single
            // floor doesn't actually save time (pzmap2dzi's cost is dominated by a pass over the
            // whole map, not by how many floors are requested), so looping floor-by-floor only
            // added repeated per-invocation overhead for no benefit.
            await renderer.RenderFloorsAsync(0, MaxFloor + 1, omitLevels: 2, progress, ct);
            if (ct.IsCancellationRequested) return;

            AppLogger.Log("RunRenderPassAsync: render complete, reloading map view.");
            _activeRenderer = null;
            MapWebView.CoreWebView2?.Reload();
            HideRenderStatus();
        }
        catch (OperationCanceledException)
        {
            // Either the window closed (nothing left to do - MapRenderService already kills the
            // subprocess tree) or the user changed the speed mode, which restarts this same
            // method with a fresh token right after cancelling - see SpeedMode_Click.
        }
        catch (Exception ex)
        {
            AppLogger.Log($"RunRenderPassAsync failed: {ex.Message}");
            _activeRenderer = null;
            // Surfaced, not just logged - a texture-unpack failure used to fail silently here
            // while the render step ran anyway and "succeeded" with black/missing tiles, which
            // is exactly what a real user hit and reported with no indication anything was wrong.
            // Full detail goes to the log above, not this single-line Auto-width status title -
            // the actual diagnostic message can run to a full paragraph.
            ShowRenderStatus("Map render failed - see the app log for details", 0, "");
        }
    }

    private async void SpeedMode_Click(object sender, RoutedEventArgs e)
    {
        var mode = ReferenceEquals(sender, SpeedFastButton) ? RenderSpeedMode.Fast : RenderSpeedMode.Light;
        if (_settings.RenderSpeed == mode) return;

        _settings.RenderSpeed = mode;
        _settingsService.Save(_settings);
        UpdateSpeedButtons();

        if (_activeRenderer == null) return; // nothing running right now - takes effect next render

        AppLogger.Log($"SpeedMode_Click: switching to {mode}, restarting render with new worker count.");
        var renderer = _activeRenderer;
        renderer.SetWorkerCount(MapRenderService.ResolveWorkerCount(mode));

        // Wait for the old pass to actually stop (its process killed) before starting a new one -
        // Cancel() alone only signals; without awaiting, a moment could exist where the old and
        // new renders are both writing to the same output folder at once.
        var oldCts = _renderCts;
        var oldTask = _activeRenderTask;
        oldCts.Cancel();
        if (oldTask != null) await oldTask;
        oldCts.Dispose();

        _renderCts = new CancellationTokenSource();
        _activeRenderTask = RunRenderPassAsync(renderer, _renderCts.Token);
    }

    private static readonly System.Windows.Media.Brush ActiveBg = new System.Windows.Media.SolidColorBrush(
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3D2F12"));
    private static readonly System.Windows.Media.Brush ActiveFg = new System.Windows.Media.SolidColorBrush(
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E0A030"));
    private static readonly System.Windows.Media.Brush InactiveBg = new System.Windows.Media.SolidColorBrush(
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2A2A2A"));
    private static readonly System.Windows.Media.Brush InactiveFg = new System.Windows.Media.SolidColorBrush(
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#AAAAAA"));

    private void UpdateSpeedButtons()
    {
        var fast = _settings.RenderSpeed == RenderSpeedMode.Fast;
        SpeedFastButton.Background = fast ? ActiveBg : InactiveBg;
        SpeedFastButton.Foreground = fast ? ActiveFg : InactiveFg;
        SpeedLightButton.Background = !fast ? ActiveBg : InactiveBg;
        SpeedLightButton.Foreground = !fast ? ActiveFg : InactiveFg;
    }

    // No Dispatcher.Invoke needed here - both callers construct their Progress<T> on the UI
    // thread, which makes Progress<T> itself marshal Report() callbacks back to it automatically.
    private void ShowRenderStatus(string title, double percent, string detail)
    {
        RenderStatusBar.Visibility = Visibility.Visible;
        RenderStatusTitle.Text = title;
        // Always ensures the "actively rendering" sub-layout, not the pre-start speed-choice one -
        // every caller of this represents real progress/an error, never the initial choice gate
        // (that's ShowSpeedChoicePrompt). Redundant with StartRenderButton_Click's own toggle on
        // the very first call after Start is clicked, but harmless/idempotent to repeat here.
        RenderProgressBar.Visibility = Visibility.Visible;
        RenderProgressBar.Value = percent;
        RenderStatusDetail.Visibility = Visibility.Visible;
        RenderStatusDetail.Text = detail;
        StartRenderButton.Visibility = Visibility.Collapsed;
        // Only the "couldn't find your Project Zomboid install" caller re-shows this - every
        // other caller (progress updates, disk-space failure, render failure) should never carry
        // it over from a previous call.
        BrowseGameButton.Visibility = Visibility.Collapsed;
    }

    private void HideRenderStatus()
    {
        RenderStatusBar.Visibility = Visibility.Collapsed;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        var markers = _markerStore.LoadForMap(_activeMapId);
        PostMessage(new { type = "initMarkers", markers });
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            if (type == "markersChanged")
            {
                var markers = JsonSerializer.Deserialize<System.Collections.Generic.List<Marker>>(
                    root.GetProperty("markers").GetRawText(), JsonOptions) ?? new();
                _markerStore.SaveForMap(_activeMapId, markers);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"OnWebMessageReceived failed: {ex.Message}");
        }
    }

    private void PostMessage(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            MapWebView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"PostMessage failed: {ex.Message}");
        }
    }

    // Single source of truth for the install-button/checkbox pair, re-run on every launch (not
    // just cached from whenever the app happened to last check) and right after an install
    // attempt. Fixes a real bug: the checkbox used to silently install the mod the instant it
    // was ticked, which then permanently hid the Install button (even across relaunches) whether
    // or not the mod was ever actually enabled in-game - the checkbox now stays disabled and
    // un-tickable until a real detection pass confirms the mod files are present, mirroring the
    // same UE4SS-detection-gates-checkbox pattern PalHub Live already uses.
    private void RefreshBridgeModDetection()
    {
        var installed = LuaBridgeInstaller.IsInstalled();
        InstallBridgeButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        LivePositionCheckBox.IsEnabled = installed;
    }

    private void LivePosition_Changed(object sender, RoutedEventArgs e)
    {
        if (LivePositionCheckBox.IsChecked == true)
        {
            _liveService?.Dispose();
            _liveService = new LivePositionService(pos =>
            {
                Dispatcher.Invoke(() => PostMessage(new { type = "livePosition", position = pos }));
            });
            _liveService.Start();
        }
        else
        {
            _liveService?.Dispose();
            _liveService = null;
            PostMessage(new { type = "livePosition", position = (PlayerPosition?)null });
        }
    }

    /// <summary>Starts/stops the LAN-facing web server (LanShareServer) so another device on the
    /// same WiFi/network can view the map in a plain browser. Forces Live Position off and
    /// disables its checkbox while active - a deliberate simplicity choice (see the XAML comment
    /// on ShareOnlineCheckBox), not because live position is actually exposed to the LAN view
    /// (it isn't - LanShareServer has no position endpoint at all).</summary>
    private void ShareOnline_Changed(object sender, RoutedEventArgs e)
    {
        if (ShareOnlineCheckBox.IsChecked == true)
        {
            if (LivePositionCheckBox.IsChecked == true) LivePositionCheckBox.IsChecked = false; // triggers LivePosition_Changed, tears down _liveService
            LivePositionCheckBox.IsEnabled = false;

            _lanShareServer = new LanShareServer(
                PayloadExtractor.WebMapDir,
                () => MapDataService.FindMapHtmlDir(_activeMapId),
                () => _markerStore.LoadForMap(_activeMapId));
            _lanShareServer.Start();

            ShowIpButton.Content = "Show IP";
            ShowIpButton.Visibility = Visibility.Visible;
            AppLogger.Log($"ShareOnline_Changed: LAN sharing enabled on port {_lanShareServer.Port}.");
        }
        else
        {
            _lanShareServer?.Stop();
            _lanShareServer = null;
            ShowIpButton.Visibility = Visibility.Collapsed;
            LivePositionCheckBox.IsEnabled = LuaBridgeInstaller.IsInstalled();
            AppLogger.Log("ShareOnline_Changed: LAN sharing disabled.");
        }
    }

    private void ShowIpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lanShareServer == null) return;

        // Toggling back to the plain label re-hides the address rather than leaving it
        // permanently on screen - the whole point of gating it behind a button (see the user's
        // own framing: "for security") is that it isn't sitting visible by default.
        if (ShowIpButton.Content as string != "Show IP")
        {
            ShowIpButton.Content = "Show IP";
            return;
        }

        var ip = LanShareServer.FindLanIPv4Address();
        ShowIpButton.Content = ip != null
            ? $"http://{ip}:{_lanShareServer.Port}"
            : "Couldn't find your network IP";
    }

    private void UpdateAvailableButton_Click(object sender, RoutedEventArgs e)
    {
        var url = _updateCheckService.LastResult?.ReleaseUrl;
        if (string.IsNullOrEmpty(url)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void InstallBridgeButton_Click(object sender, RoutedEventArgs e)
    {
        if (LuaBridgeInstaller.Install())
        {
            RefreshBridgeModDetection();
            MessageBox.Show(
                "The ZoidHub Bridge mod was installed. One more step: launch Project Zomboid, " +
                "go to Mods, enable \"ZoidHub Bridge\", and load your save - then tick Live Position here.",
                "ZoidHub", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(
                "Couldn't install the bridge mod - check zoidhub.log for details.",
                "ZoidHub", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        DragMove();
    }

    private static bool IsWithinButton(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is ButtonBase) return true;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen)
        {
            Left = _preFullscreenLeft;
            Top = _preFullscreenTop;
            Width = _preFullscreenWidth;
            Height = _preFullscreenHeight;
            _isFullscreen = false;
            return;
        }

        _preFullscreenLeft = Left;
        _preFullscreenTop = Top;
        _preFullscreenWidth = Width;
        _preFullscreenHeight = Height;

        var handle = new WindowInteropHelper(this).Handle;
        var screenBounds = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;

        Left = screenBounds.Left * transform.M11;
        Top = screenBounds.Top * transform.M22;
        Width = screenBounds.Width * transform.M11;
        Height = screenBounds.Height * transform.M22;
        _isFullscreen = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_SIZE_BOTTOMRIGHT, IntPtr.Zero);
    }
}
