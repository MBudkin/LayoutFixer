using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LayoutFixer.Localization;

namespace LayoutFixer.Services;

/// <summary>Downloads only after an explicit user action. Published builds contain one executable.</summary>
internal static class UpdateInstaller
{
    internal const long MaxDownloadBytes = 200L * 1024 * 1024;
    private const long MaxExecutableBytes = 300L * 1024 * 1024;

    // In a self-contained bundle CoreLib is bundled too; in the framework
    // variant it is loaded from the installed shared runtime.
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty CoreLib location intentionally identifies the self-contained bundle.")]
    internal static string CurrentVariant => string.IsNullOrEmpty(typeof(object).Assembly.Location) ? "win-x64" : "framework";

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "In-place installation is explicitly limited to published single-file bundles.")]
    public static bool CanInstall(UpdateInfo update) =>
        string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location) &&
        Environment.ProcessPath is { } path && File.Exists(path) && SelectAsset(update) is not null;

    public static UpdateAsset? SelectAsset(UpdateInfo update) => SelectAsset(update, CurrentVariant);

    internal static UpdateAsset? SelectAsset(UpdateInfo update, string variant)
    {
        if (variant is not ("win-x64" or "framework")) return null;
        string prefix = $"LayoutFixer-{update.Version.ToString(3)}-{variant}";
        // A zip is preferred when a release contains both formats. Never
        // accidentally select source code or another runtime variant.
        foreach (string extension in new[] { ".zip", ".exe" })
        {
            UpdateAsset[] matches = update.Assets.Where(asset =>
                asset.Name.Equals(prefix + extension, StringComparison.OrdinalIgnoreCase) &&
                asset.Size > 0 && asset.Size <= MaxDownloadBytes &&
                IsProjectAssetUrl(asset.DownloadUrl, asset.Name)).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1) return null;
        }
        return null;
    }

    internal static bool IsProjectAssetUrl(string url, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0 ||
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !IsHttps(uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;

        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 6 &&
               parts[0].Equals("MBudkin", StringComparison.OrdinalIgnoreCase) &&
               parts[1].Equals("LayoutFixer", StringComparison.OrdinalIgnoreCase) &&
               parts[2] == "releases" && parts[3] == "download" &&
               Uri.UnescapeDataString(parts[5]).Equals(name, StringComparison.Ordinal);
    }

    internal static bool IsAllowedDownloadUri(Uri uri) => IsHttps(uri) &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
             uri.AbsolutePath.StartsWith("/MBudkin/LayoutFixer/releases/download/", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsHttps(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    public static async Task<PreparedUpdate> PrepareAsync(UpdateInfo update, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanInstall(update) || !UpdateChecker.IsNewer(update.Version, UpdateChecker.CurrentVersion))
            throw new InvalidOperationException("This release cannot update the current installation.");

        UpdateAsset asset = SelectAsset(update)!;
        string executable = Path.GetFullPath(Environment.ProcessPath!);
        if ((File.GetAttributes(executable) & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) != 0)
            throw new UnauthorizedAccessException("The current executable is read-only or is a file link.");
        string work = Path.Combine(Path.GetTempPath(), "LayoutFixer.Update." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string staged = Path.Combine(Path.GetDirectoryName(executable)!, ".LayoutFixer.update-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            // Check write access before a large download, without touching the
            // running executable. Staging on the same volume enables File.Replace.
            using (new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            string package = Path.Combine(work, "download" + Path.GetExtension(asset.Name));
            await DownloadAsync(asset, package, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                string candidate = package;
                if (Path.GetExtension(asset.Name).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = Path.Combine(work, "LayoutFixer.exe");
                    ExtractExecutable(package, candidate);
                }
                ValidateExecutable(candidate, update.Version);
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(candidate, staged, overwrite: true);
            }, cancellationToken).ConfigureAwait(false);

            return new PreparedUpdate(work, staged, executable);
        }
        catch
        {
            DeleteFile(staged);
            DeleteWorkDirectory(work);
            throw;
        }
    }

    private static async Task DownloadAsync(UpdateAsset asset, string destination, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using HttpResponseMessage response = await OpenDownloadAsync(http, new Uri(asset.DownloadUrl), timeout.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            throw new InvalidDataException("The download size differs from the release metadata.");
        await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        byte[] buffer = new byte[81920];
        long received = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        progress?.Report(0);
        while (true)
        {
            int read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            received += read;
            if (received > asset.Size || received > MaxDownloadBytes)
                throw new InvalidDataException("The update download is too large.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            progress?.Report((int)(received * 100 / asset.Size));
        }
        if (received != asset.Size) throw new InvalidDataException("The update download is incomplete.");
        VerifyDigest(asset.Digest, hash.GetHashAndReset());
    }

    internal static async Task<HttpResponseMessage> OpenDownloadAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            if (!IsAllowedDownloadUri(uri)) throw new InvalidDataException("The update redirected to an untrusted address.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LayoutFixer", UpdateChecker.CurrentVersion.ToString(3)));
            HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("The update redirect has no destination.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (response.IsSuccessStatusCode) return response;
            response.Dispose();
            throw new HttpRequestException($"Update download failed: HTTP {(int)response.StatusCode}.");
        }
        throw new InvalidDataException("Too many update redirects.");
    }

    internal static void VerifyDigest(string? digest, byte[] actual)
    {
        // Older GitHub releases may not provide a digest. HTTPS and the strict
        // repository allowlist still apply. A supplied digest must match.
        if (string.IsNullOrEmpty(digest)) return;
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71)
            throw new InvalidDataException("The release checksum is not supported.");
        byte[] expected;
        try { expected = Convert.FromHexString(digest[7..]); }
        catch (FormatException ex) { throw new InvalidDataException("The release checksum is invalid.", ex); }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidDataException("The update checksum does not match.");
    }

    internal static void ExtractExecutable(string archivePath, string destination)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 100) throw new InvalidDataException("The update archive has too many files.");
        ZipArchiveEntry? executable = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            string[] segments = name.Split('/');
            if (name.StartsWith('/') || segments.Any(segment => segment is "." or ".." ||
                    segment.Contains(':') || segment.EndsWith(' ') || segment.EndsWith('.')) ||
                (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("The update archive contains an unsafe path.");

            if (!segments[^1].Equals("LayoutFixer.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (executable is not null || segments.Length > 2 || entry.Length <= 0 || entry.Length > MaxExecutableBytes)
                throw new InvalidDataException("The update archive must contain exactly one application executable.");
            executable = entry;
        }
        if (executable is null) throw new InvalidDataException("LayoutFixer.exe is missing from the update archive.");
        // Enforce the limit on decompressed bytes too; the archive's advertised
        // length alone is not sufficient for a corrupt ZIP.
        using Stream input = executable.Open();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[81920];
        long extracted = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            extracted += read;
            if (extracted > executable.Length || extracted > MaxExecutableBytes)
                throw new InvalidDataException("The extracted application exceeds its expected size.");
            output.Write(buffer, 0, read);
        }
        if (extracted != executable.Length) throw new InvalidDataException("The application archive is incomplete.");
    }

    internal static void ValidateExecutable(string path, Version expectedVersion)
    {
        if (new FileInfo(path).Length > MaxExecutableBytes) throw new InvalidDataException("The application executable is too large.");
        using (var stream = File.OpenRead(path))
        using (var reader = new PEReader(stream))
        {
            if (reader.PEHeaders.CoffHeader.Machine != Machine.Amd64 ||
                reader.PEHeaders.PEHeader?.Subsystem != Subsystem.WindowsGui ||
                (reader.PEHeaders.CoffHeader.Characteristics & Characteristics.Dll) != 0)
                throw new InvalidDataException("The update is not a Windows x64 application.");
        }
        FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
        if (version.ProductName != "LayoutFixer" ||
            new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart) != expectedVersion)
            throw new InvalidDataException("The application name or version differs from the selected release.");
    }

    internal static void DeleteFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static void DeleteWorkDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Owns temporary files until the hidden installer has acknowledged its startup.</summary>
internal sealed class PreparedUpdate : IDisposable
{
    private readonly string _work;
    private readonly string _staged;
    private readonly string _executable;
    private bool _started;
    private bool _disposed;

    internal PreparedUpdate(string work, string staged, string executable)
    {
        _work = work;
        _staged = staged;
        _executable = executable;
    }

    public async Task StartInstallAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("The update installer has already started.");
        using Process current = Process.GetCurrentProcess();
        string ready = Path.Combine(_work, "ready");
        string error = Path.Combine(_work, "error.txt");
        string script = Path.Combine(_work, "install.ps1");
        string config = Path.Combine(_work, "install.json");
        string digest;
        await using (var stream = new FileStream(_staged, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        File.WriteAllText(script, UpdateInstallScript.Text);
        File.WriteAllText(config, JsonSerializer.Serialize(new
        {
            ProcessId = current.Id,
            ProcessStartTicks = current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Executable = _executable,
            Staged = _staged,
            Sha256 = digest,
            Backup = Path.Combine(Path.GetDirectoryName(_executable)!, ".LayoutFixer.backup-" + Guid.NewGuid().ToString("N") + ".exe"),
            Ready = ready,
            Error = error,
            ErrorMessage = Localizer.Get("UpdateInstallFailedAfterExit"),
            Work = _work
        }));
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = _work
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-ConfigPath", config })
            start.ArgumentList.Add(argument);
        using Process helper = Process.Start(start) ?? throw new IOException("The update installer could not start.");
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (helper.HasExited || timeout.Elapsed > TimeSpan.FromSeconds(15))
                    throw new IOException(File.Exists(error) ? File.ReadAllText(error) : "The update installer did not start.");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _started = true;
        }
        catch
        {
            try
            {
                if (!helper.HasExited) helper.Kill();
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException) { }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_started)
        {
            UpdateInstaller.DeleteFile(_staged);
            UpdateInstaller.DeleteWorkDirectory(_work);
        }
    }
}
