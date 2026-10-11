using System;
using System.IO;
using System.Text;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Services;

/// <summary>
/// Crash-safe storage for the small JSON files in %AppData%\LayoutFixer.
/// </summary>
/// <remarks>
/// A plain File.WriteAllText that is interrupted (power loss, crash, full
/// disk) leaves a truncated file. Loading it then fails, the in-memory data
/// starts empty, and the next save silently replaces the user's dictionary
/// with that empty state. Instead, the new content is written to a temporary
/// file and swapped in with File.Replace, which keeps the previous version as
/// "&lt;name&gt;.bak". A file that cannot be parsed is moved aside, never
/// overwritten, and the backup is offered to the loader.
/// </remarks>
internal static class AtomicFile
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string BackupPath(string path) => path + ".bak";

    public static void WriteAllText(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, Utf8NoBom))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(temporary, path, BackupPath(path), ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporary, path);
        }
    }

    /// <summary>
    /// Reads the file and parses it; when that fails, moves the broken file
    /// aside and tries the last good backup. Returns default when neither exists
    /// or both are unreadable.
    /// </summary>
    public static T? Load<T>(string path, Func<string, T?> parse, out bool recoveredFromBackup)
    {
        recoveredFromBackup = false;
        if (File.Exists(path))
        {
            try
            {
                return parse(File.ReadAllText(path, Utf8NoBom));
            }
            catch (Exception ex)
            {
                Logger.Log($"AtomicFile: '{Path.GetFileName(path)}' is unreadable: {ex.Message}");
                Quarantine(path);
            }
        }

        string backup = BackupPath(path);
        if (!File.Exists(backup))
        {
            return default;
        }

        try
        {
            T? value = parse(File.ReadAllText(backup, Utf8NoBom));
            recoveredFromBackup = true;
            Logger.Log($"AtomicFile: restored '{Path.GetFileName(path)}' from its backup");
            return value;
        }
        catch (Exception ex)
        {
            Logger.Log($"AtomicFile: backup of '{Path.GetFileName(path)}' is unreadable too: {ex.Message}");
            return default;
        }
    }

    // Keeps the damaged file for manual recovery instead of letting the next
    // save overwrite it.
    private static void Quarantine(string path)
    {
        try
        {
            string target = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, target, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Log($"AtomicFile: could not move the damaged file aside: {ex.Message}");
        }
    }
}
