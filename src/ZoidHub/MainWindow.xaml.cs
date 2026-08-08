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
    private MapRenderService? _activeRenderer;
    private Task? _activeRenderTask;
    private bool _isFullscreen;
    private double _preFullscreenLeft, _preFullscreenTop, _preFullscreenWidth, _preFullscreenHeight;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private CancellationTokenSource _renderCts = new();

    public MainWindow()
    {
        InitializeComponent();
        CpuAffinity.PinCurrentProcess();
        _settings = _settingsService.Load();
        UpdateSpeedButtons();
        _updateCheckService.Checked += result => Dispatcher.Invoke(() =>
        {
            UpdateAvailableButton.Visibility = result.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        });
        _ = _updateCheckService.CheckAsync(); // silent on-launch check; failures/no-update are invisible by design
        Loaded += async (_, _) => await InitializeWebViewAsync();
        // "Left the map screen and came back" - covers both alt-tabbing away/back and
        // minimizing/restoring, since both fire Activated on return. Re-centers on wherever the
        // player currently is rather than leaving the view wherever it was last panned to.
        Activated += (_, _) => RecenterOnLivePosition();
        Closed += (_, _) =>
        {
            _renderCts.Cancel();
            _liveService?.Dispose();
            AppLogger.Log("ZoidHub closed");
        };
    }

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

            // If nothing's rendered anywhere yet, still map the host at the path the bundled
            // renderer WILL write to. SetVirtualHostNameToFolderMapping resolves individual FILE
            // requests lazily, but - contrary to what an earlier version of this comment assumed
            // - it throws immediately if the folder itself doesn't exist yet, so create it first.
            var tileDir = MapDataService.FindMapHtmlDir(MapDataService.VanillaMapId)
                ?? Path.Combine(MapDataService.GetOutputDir(MapDataService.VanillaMapId), "html");
            Directory.CreateDirectory(tileDir);
            MapWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                TileDataHost, tileDir, CoreWebView2HostResourceAccessKind.Allow);

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
        var existingDir = MapDataService.FindMapHtmlDir(MapDataService.VanillaMapId);
        if (existingDir != null && MapRenderService.IsFloorRendered(existingDir, 0))
        {
            return; // already have real tiles (e.g. a previous run, or one still filling in floors)
        }

        var pzRoot = GameLocator.FindGameInstallDir();
        if (pzRoot == null)
        {
            AppLogger.Log("EnsureMapRenderedAsync: Project Zomboid install not found - can't auto-render.");
            ShowRenderStatus("Couldn't find your Project Zomboid install - the map can't render yet.", 0, "");
            return;
        }

        var outputPath = MapDataService.GetOutputDir(MapDataService.VanillaMapId);
        var renderer = new MapRenderService(pzRoot, outputPath, workerCount: MapRenderService.ResolveWorkerCount(_settings.RenderSpeed));
        if (!renderer.IsBundleAvailable())
        {
            AppLogger.Log("EnsureMapRenderedAsync: bundled renderer not found.");
            return;
        }

        _activeRenderer = renderer;
        _activeRenderTask = RunRenderPassAsync(renderer, _renderCts.Token);
        await _activeRenderTask;
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
        RenderProgressBar.Value = percent;
        RenderStatusDetail.Text = detail;
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
