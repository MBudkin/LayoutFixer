using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LayoutFixer.Services;

/// <summary>A published release that is newer than the running build.</summary>
internal sealed record UpdateInfo(Version Version, string Title, string Notes, string PageUrl, DateTimeOffset? PublishedAt);

internal enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed
}

internal sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update = null, string? Error = null);

/// <summary>
/// Asks the GitHub API for the latest published release of the project and
/// compares its tag with the running version. Read-only: nothing is
/// downloaded or installed, and the request carries no user data (only the
/// program name and version in the User-Agent header, which GitHub requires).
/// Drafts and pre-releases are never returned by the "latest" endpoint.
/// </summary>
internal static class UpdateChecker
{
    private static readonly Regex VersionPattern = new(@"\d+(?:\.\d+){1,3}", RegexOptions.CultureInvariant);

    public static Version CurrentVersion { get; } = Normalize(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Get, ProjectLinks.LatestReleaseApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LayoutFixer", CurrentVersion.ToString(3)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // No published release yet: nothing newer than this build.
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: $"HTTP {(int)response.StatusCode}");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            UpdateInfo? latest = ParseRelease(json);
            if (latest is null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "unrecognized release tag");
            }

            return IsNewer(latest.Version, CurrentVersion)
                ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, latest)
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>
    /// Reads a GitHub "release" object. Returns null when the release is a
    /// draft/pre-release or its tag holds no version number.
    /// </summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            GetBool(root, "draft") ||
            GetBool(root, "prerelease"))
        {
            return null;
        }

        string tag = GetString(root, "tag_name");
        string title = GetString(root, "name");
        Version? version = ParseVersion(tag) ?? ParseVersion(title);
        if (version is null)
        {
            return null;
        }

        string page = GetString(root, "html_url");
        if (!page.StartsWith(ProjectLinks.GitHubUrl + "/", StringComparison.OrdinalIgnoreCase))
        {
            // Never open a link that does not belong to the project.
            page = ProjectLinks.ReleasesUrl;
        }

        DateTimeOffset? published = DateTimeOffset.TryParse(GetString(root, "published_at"), out DateTimeOffset value)
            ? value
            : null;

        return new UpdateInfo(
            version,
            string.IsNullOrWhiteSpace(title) ? tag : title.Trim(),
            GetString(root, "body").Trim(),
            page,
            published);
    }

    /// <summary>"v1.10.0", "1.10", "LayoutFixer 1.10.0-beta" → 1.10.0; null without a number.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match match = VersionPattern.Match(text);
        return match.Success && Version.TryParse(match.Value, out Version? version)
            ? Normalize(version)
            : null;
    }

    public static bool IsNewer(Version candidate, Version current)
    {
        return Normalize(candidate).CompareTo(Normalize(current)) > 0;
    }

    // 1.10 and 1.10.0 must compare equal; System.Version treats a missing
    // component as -1, so pad to three components (the 4th is ignored).
    private static Version Normalize(Version version)
    {
        return new Version(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build));
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool GetBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }
}
