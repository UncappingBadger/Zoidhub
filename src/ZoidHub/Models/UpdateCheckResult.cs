namespace ZoidHub.Models;

/// <summary>Outcome of one GitHub update check - see Services/UpdateCheckService.</summary>
public sealed class UpdateCheckResult
{
    /// <summary>False if the check itself couldn't complete (no internet, GitHub unreachable, bad
    /// response) - distinct from "checked fine, you're already up to date".</summary>
    public bool CheckSucceeded { get; init; }

    public bool UpdateAvailable { get; init; }

    /// <summary>The latest release's tag as GitHub reports it (e.g. "v0.2.0"), for display only.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>Release page to open if the user wants to grab the update.</summary>
    public string? ReleaseUrl { get; init; }
}
