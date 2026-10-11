using System.Diagnostics;

namespace LayoutFixer.Services;

internal static class ProjectLinks
{
    public const string GitHubUrl = "https://github.com/MBudkin/LayoutFixer";
    public const string IssuesUrl = GitHubUrl + "/issues";
    public const string ReleasesUrl = GitHubUrl + "/releases";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/MBudkin/LayoutFixer/releases/latest";

    public static void OpenGitHub()
    {
        OpenUrl(GitHubUrl);
    }

    public static void OpenIssues()
    {
        OpenUrl(IssuesUrl);
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Best-effort: never let opening the browser crash the app.
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Best-effort.
        }
    }
}
