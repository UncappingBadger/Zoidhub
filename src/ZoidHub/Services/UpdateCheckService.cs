using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using ZoidHub.Models;

namespace ZoidHub.Services;

/// <summary>Checks GitHub's public releases API for a newer ZoidHub version than the one
/// currently running - same pattern as TruckHub/PalHub's own update checkers, so existing users
/// find out about new releases without having to remember to check themselves.</summary>
public sealed class UpdateCheckService
{
    private const string ApiUrl = "https://api.github.com/repos/UncappingBadger/Zoidhub/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    /// <summary>Fires whenever a check completes, success or failure.</summary>
    public event Action<UpdateCheckResult>? Checked;

    /// <summary>The most recent result, if any check has run yet this session.</summary>
    public UpdateCheckResult? LastResult { get; private set; }

    public async Task<UpdateCheckResult> CheckAsync()
    {
        UpdateCheckResult result;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            // GitHub's API rejects requests with no User-Agent at all.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ZoidHub", CurrentVersionTag));

            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var releaseUrl = doc.RootElement.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;

            var remote = ParseVersion(tagName);
            var local = CurrentVersion;

            result = new UpdateCheckResult
            {
                CheckSucceeded = true,
                UpdateAvailable = remote is not null && local is not null && IsNewer(remote, local),
                LatestVersion = tagName,
                ReleaseUrl = releaseUrl,
            };
        }
        catch
        {
            // No internet, GitHub unreachable, rate-limited, unexpected response shape - none of
            // these should ever surface as an error to the silent launch check.
            result = new UpdateCheckResult { CheckSucceeded = false };
        }

        LastResult = result;
        try
        {
            Checked?.Invoke(result);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"UpdateCheckService: Checked handler threw {ex.GetType().Name}: {ex.Message}");
        }
        return result;
    }

    private static Version? CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;

    private static string CurrentVersionTag => CurrentVersion?.ToString(3) ?? "0.0.0";

    // GitHub tags look like "v0.2.0" - System.Version needs at least two numeric segments and,
    // critically, doesn't compare cleanly across different segment counts (a missing segment
    // parses as -1, not 0), so IsNewer below compares the three numbers directly rather than
    // trusting Version.CompareTo on two versions that might have a different number of segments.
    private static Version? ParseVersion(string tag)
    {
        var cleaned = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(cleaned, out var version) ? version : null;
    }

    private static bool IsNewer(Version remote, Version local)
    {
        var remoteMajor = remote.Major;
        var remoteMinor = Math.Max(0, remote.Minor);
        var remoteBuild = Math.Max(0, remote.Build);
        var localMajor = local.Major;
        var localMinor = Math.Max(0, local.Minor);
        var localBuild = Math.Max(0, local.Build);

        if (remoteMajor != localMajor) return remoteMajor > localMajor;
        if (remoteMinor != localMinor) return remoteMinor > localMinor;
        return remoteBuild > localBuild;
    }
}
