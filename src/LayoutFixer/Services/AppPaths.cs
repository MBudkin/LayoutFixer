using System;
using System.IO;

namespace LayoutFixer.Services;

/// <summary>
/// Where LayoutFixer keeps its settings, dictionaries and debug log:
/// %AppData%\LayoutFixer by default. The LAYOUTFIXER_DATA_DIR environment
/// variable overrides it (isolated test runs, portable setups).
/// </summary>
internal static class AppPaths
{
    public const string DataDirectoryVariable = "LAYOUTFIXER_DATA_DIR";

    private static readonly Lazy<string> Directory = new(ResolveDataDirectory);

    public static string DataDirectory => Directory.Value;

    public static string File(string name) => Path.Combine(DataDirectory, name);

    private static string ResolveDataDirectory()
    {
        try
        {
            string? overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridden.Trim()));
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                return Path.Combine(appData, "LayoutFixer");
            }
        }
        catch
        {
            // Fall back to the working directory below.
        }

        return Path.GetFullPath("LayoutFixer");
    }
}
