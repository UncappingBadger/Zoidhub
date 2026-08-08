using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ZoidHub.Models;

namespace ZoidHub.Services;

/// <summary>Runs the bundled, invisible-to-the-user copy of pzmap2dzi to render real game-sprite
/// map tiles directly from the player's own Project Zomboid install - no manual Python/YAML
/// steps. See tools\pzmap2dzi in the repo for the original CLI tool this is bundled from (same
/// MIT-licensed source, same conf.yaml shape).
///
/// Renders every floor in one combined pass rather than looping floor-by-floor. An earlier
/// version rendered floor 0 first on the theory that scoping to one floor would make it ready
/// faster - measured against a real timing run, that's false: pzmap2dzi's job count for "just
/// floor 0" came back almost identical to "all 9 floors" (~1.4M either way), meaning the cost is
/// dominated by a pass over the whole map's raw data, not by how many floors are requested. So
/// floor-scoping bought nothing but the overhead of repeating that pass 9 times - one combined
/// call is both simpler and genuinely faster.</summary>
public class MapRenderService
{
    private static readonly Regex JobLineRegex = new(@"job:\s*(\d+)/(\d+)", RegexOptions.Compiled);

    private readonly string _pythonExe;
    private readonly string _pzmap2dziDir;
    private readonly string _bundledConfDir;
    private readonly string _pzRoot;
    private readonly string _outputPath;
    private readonly string? _customMapName;
    private int _workerCount;

    /// <param name="pzRoot">Project Zomboid game install dir.</param>
    /// <param name="outputPath">Per-map output root - see MapDataService.GetOutputDir.</param>
    /// <param name="customMapName">Null for the vanilla county map (the only thing wired up and
    /// tested today). For a custom map, this is the entry key from the bundled conf\mod\*.txt
    /// map catalog (e.g. "RavenCreek") - renders as a pzmap2dzi "mod map" instead of the base
    /// map, landing under html/mod_maps/&lt;name&gt;/ per MapDataService's own path convention.
    /// Not yet exercised against a real custom map - see [[project_zoidhub_build]].</param>
    /// <param name="workerCount">How many parallel render workers (real OS subprocesses, not
    /// threads) to use - the main CPU/speed tradeoff knob. See RenderSpeedMode.</param>
    public MapRenderService(string pzRoot, string outputPath, string? customMapName = null, int workerCount = 4)
    {
        var rendererDir = PayloadExtractor.RendererDir;
        _pythonExe = Path.Combine(rendererDir, "python", "python.exe");
        _pzmap2dziDir = Path.Combine(rendererDir, "pzmap2dzi");
        _bundledConfDir = Path.Combine(rendererDir, "conf");
        _pzRoot = pzRoot;
        _outputPath = outputPath;
        _customMapName = customMapName;
        _workerCount = Math.Max(1, workerCount);
    }

    /// <summary>Lets the speed mode change take effect on the next RenderFloorsAsync call
    /// without recreating the whole service (and losing track of already-unpacked textures).</summary>
    public void SetWorkerCount(int workerCount) => _workerCount = Math.Max(1, workerCount);

    public bool IsBundleAvailable() => File.Exists(_pythonExe) && File.Exists(Path.Combine(_pzmap2dziDir, "main.py"));

    /// <summary>Maps the user-facing Light/Fast choice to an actual worker count. The "leave
    /// room for the game" guarantee is now handled by CpuAffinity (a hard core-affinity fence,
    /// not just a worker-count hint), so both modes are scaled off CpuAffinity.AvailableCores
    /// (already excludes the reserved cores) rather than raw core count: Light uses a quarter of
    /// what's available (min 1) for a light footprint even within the allowed range; Fast uses
    /// everything ZoidHub is allowed to touch.</summary>
    public static int ResolveWorkerCount(RenderSpeedMode mode)
    {
        var available = CpuAffinity.AvailableCores;
        return mode == RenderSpeedMode.Fast
            ? available
            : Math.Max(1, available / 4);
    }

    /// <summary>Checks a specific floor's tiles exist. <paramref name="mapHtmlDir"/> is the
    /// resolved directory from MapDataService.FindMapHtmlDir (the one that directly contains
    /// "base/") - vanilla and custom maps resolve to different real paths, so this takes the
    /// already-resolved directory rather than assuming the vanilla shape itself.</summary>
    public static bool IsFloorRendered(string mapHtmlDir, int floor) =>
        File.Exists(Path.Combine(mapHtmlDir, "base", $"layer{floor}.dzi")) &&
        File.Exists(Path.Combine(mapHtmlDir, "base", "map_info.json"));

    private bool IsUnpacked() =>
        Directory.Exists(Path.Combine(_outputPath, "texture")) &&
        Directory.GetDirectories(Path.Combine(_outputPath, "texture")).Length > 0;

    public async Task EnsureUnpackedAsync(IProgress<RenderProgress> progress, CancellationToken ct)
    {
        if (IsUnpacked()) return;

        progress.Report(new RenderProgress { Stage = RenderStage.Unpacking, Message = "Reading game textures..." });
        var confPath = WriteTempConf(minFloor: 0, maxFloorExclusive: 1, omitLevels: 2);
        await RunPythonAsync(new[] { "main.py", "-c", confPath, "unpack" }, line => { }, ct);
    }

    /// <summary>Renders floors [minFloor, maxFloorExclusive) in one pzmap2dzi invocation.</summary>
    public async Task RenderFloorsAsync(int minFloor, int maxFloorExclusive, int omitLevels, IProgress<RenderProgress> progress, CancellationToken ct)
    {
        var confPath = WriteTempConf(minFloor, maxFloorExclusive, omitLevels);

        void OnLine(string line)
        {
            var m = JobLineRegex.Match(line);
            if (!m.Success) return;
            var done = double.Parse(m.Groups[1].Value);
            var total = double.Parse(m.Groups[2].Value);
            progress.Report(new RenderProgress
            {
                Stage = RenderStage.RenderingFloor,
                FloorPercent = total > 0 ? Math.Min(100.0, done / total * 100.0) : 0,
                Message = "Rendering World - This may take some time"
            });
        }

        var exitCode = await RunPythonAsync(new[] { "main.py", "-c", confPath, "render", "base" }, OnLine, ct);
        if (exitCode != 0)
        {
            AppLogger.Log($"MapRenderService: render floors [{minFloor},{maxFloorExclusive}) exited with code {exitCode}");
            return;
        }

        // Marks the WHOLE map complete, so only call this after rendering the full intended
        // floor range in one pass (as MainWindow's RunRenderPassAsync always does) - a partial
        // range succeeding here would wrongly mark an incomplete map as done.
        // Mirrors MapDataService.FindMapHtmlDir's own path shape (vanilla -> html/base, custom
        // map -> html/mod_maps/<name>/base) - the two have to stay in sync since this is what
        // makes IsValid's completeness check actually pass afterward.
        var htmlBaseDir = _customMapName == null
            ? Path.Combine(_outputPath, "html", "base")
            : Path.Combine(_outputPath, "html", "mod_maps", _customMapName, "base");
        MapDataService.MarkRenderComplete(htmlBaseDir);
    }

    private async Task<int> RunPythonAsync(string[] args, Action<string> onOutputLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            WorkingDirectory = _pzmap2dziDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        CpuAffinity.PinProcessTree(process.Id);
        _ = PinProcessTreeRepeatedlyAsync(process.Id, ct);

        var stdoutTask = ReadStreamSplitOnCrOrLfAsync(process.StandardOutput, onOutputLine, ct);
        var stderrTask = ReadStreamSplitOnCrOrLfAsync(process.StandardError, line => AppLogger.Log($"[pzmap2dzi] {line}"), ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelling the token only stops US awaiting - the multiprocessing worker tree
            // (the tool spawns real OS subprocesses, not threads) would otherwise keep running
            // and burning CPU in the background with no visible sign to the user. Kill the whole
            // tree explicitly, then let the cancellation propagate as normal.
            AppLogger.Log("MapRenderService: cancelling, killing renderer process tree.");
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return process.ExitCode;
    }

    // pzmap2dzi's worker pool spawns progressively as it ramps up, not all in one instant, so a
    // single pin right after Start() can miss workers that appear a moment later. A few passes
    // over the first several seconds catches the whole pool without needing an open-ended
    // poller for the entire (potentially hours-long) render.
    private static async Task PinProcessTreeRepeatedlyAsync(int rootPid, CancellationToken ct)
    {
        int[] delaysMs = { 1000, 3000, 6000, 10000 };
        foreach (var delay in delaysMs)
        {
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
            CpuAffinity.PinProcessTree(rootPid);
        }
    }

    // pzmap2dzi prints progress as carriage-return-only updates ("job: N/Total\r"), which
    // StreamReader.ReadLine() won't split on - it waits for an actual '\n'. Read raw and split
    // on either character so progress isn't stuck buffering until the process exits.
    private static async Task ReadStreamSplitOnCrOrLfAsync(StreamReader reader, Action<string> onLine, CancellationToken ct)
    {
        var buffer = new char[4096];
        var sb = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            if (ct.IsCancellationRequested) return;
            for (int i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\r' || c == '\n')
                {
                    if (sb.Length > 0) { onLine(sb.ToString()); sb.Clear(); }
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        if (sb.Length > 0) onLine(sb.ToString());
    }

    private string WriteTempConf(int minFloor, int maxFloorExclusive, int omitLevels)
    {
        var pzRootYaml = _pzRoot.Replace("\\", "/");
        var outputYaml = _outputPath.Replace("\\", "/");

        // steamapps/common/ProjectZomboid -> steamapps/workshop/content/108600 (108600 is PZ's
        // Steam App ID) - only actually needed when rendering a custom map, but harmless to
        // compute unconditionally since it's just a path string, not a filesystem check.
        var modRootYaml = Path.GetFullPath(Path.Combine(_pzRoot, "..", "..", "workshop", "content", "108600"))
            .Replace("\\", "/");

        var baseMapLine = _customMapName == null ? "base_map: default" : "base_map: ''";
        var modMapsLine = _customMapName == null ? "mod_maps:" : $"mod_maps:\n    - {_customMapName}";

        var yaml = $@"
pz_root: |-
    {pzRootYaml}
output_path: |-
    {outputYaml}
mod_root: |-
    {modRootYaml}
map_conf_default: default.txt
map_conf:
    - vanilla.txt
    - mod/
use_depend_texture_only: false
{baseMapLine}
{modMapsLine}
render_conf:
    verbose: true
    profile: false
    worker_count: {_workerCount}
    break_key: ''
    tile_size: 1024
    layer_range: [{minFloor}, {maxFloorExclusive}]
    omit_levels: {omitLevels}
    image_fmt: webp
    image_fmt_base_layer0: jpg
    image_save_options: {{}}
    enable_cache: true
    cache_limit_mb: 0
    top_view_square_size: 1
    plants_conf:
        snow: false
        large_bush: false
        flower: false
        season: summer2
        tree_size: 2
        jumbo_tree_size: 4
        jumbo_tree_type: 0
        no_ground_cover: false
        unify_tree_type: -1
    top_view_color_mode: avg
    default_font: arial.ttf
    default_font_size: 20
";
        // Only ""unpack"" and ""render base"" are ever invoked (see EnsureUnpackedAsync /
        // RenderFloorsAsync) - the plain sprite/terrain visual render. pzmap2dzi's other render
        // commands (zombie heatmap, vehicle/story/special-zombie overlays, room-name overlays)
        // are never called, so their config keys are deliberately left out entirely rather than
        // set to false - this map shows what the world looks like, not zombie density, vehicle
        // spawns, or anything loot-related. Loot itself isn't even in these static map files -
        // container contents are randomized by the game engine at runtime, not stored on disk.
        // parse_map() resolves map_conf/map_conf_default relative to the conf FILE's own
        // directory, not a configurable "custom_root" - the generated conf has to live next to
        // the bundled default.txt/vanilla.txt/mod/, not in the output folder.
        // Keyed by map, not just floor range - two MapRenderService instances for different maps
        // (e.g. vanilla + a custom map) could otherwise collide on the same conf file if they
        // ever ran concurrently.
        var mapSlug = _customMapName ?? "vanilla";
        var confPath = Path.Combine(_bundledConfDir, $"_zoidhub_conf_{mapSlug}_{minFloor}_{maxFloorExclusive}.yaml");
        File.WriteAllText(confPath, yaml);
        return confPath;
    }
}
