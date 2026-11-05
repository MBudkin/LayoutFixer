using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LayoutFixer.Services;
using Xunit;

namespace LayoutFixer.Tests;

public sealed class UpdateInstallerTests
{
    [Fact]
    public void ReleaseAssetsSelectMatchingVersionAndRuntimeOnly()
    {
        UpdateInfo release = Release(
            Asset("LayoutFixer-2.0.0-source.zip"), Asset("LayoutFixer-2.0.0-framework.zip"),
            Asset("LayoutFixer-2.0.0-win-x64.zip"), Asset("LayoutFixer-1.9.0-win-x64.zip"));
        Assert.Equal("LayoutFixer-2.0.0-win-x64.zip", UpdateInstaller.SelectAsset(release, "win-x64")!.Name);
        Assert.Equal("LayoutFixer-2.0.0-framework.zip", UpdateInstaller.SelectAsset(release, "framework")!.Name);
        Assert.Null(UpdateInstaller.SelectAsset(release, "win-arm64"));
        Assert.Null(UpdateInstaller.SelectAsset(Release(Asset("LayoutFixer-2.0.0-source.zip")), "win-x64"));
    }

    [Fact]
    public void DuplicatePackagesAreRejectedInsteadOfGuessing()
    {
        UpdateAsset asset = Asset("LayoutFixer-2.0.0-win-x64.zip");
        Assert.Null(UpdateInstaller.SelectAsset(Release(asset, asset), "win-x64"));
    }

    [Theory]
    [InlineData("http://github.com/MBudkin/LayoutFixer/releases/download/v2.0.0/update.zip")]
    [InlineData("https://github.com.evil.test/MBudkin/LayoutFixer/releases/download/v2.0.0/update.zip")]
    [InlineData("https://github.com/Other/LayoutFixer/releases/download/v2.0.0/update.zip")]
    [InlineData("https://user@github.com/MBudkin/LayoutFixer/releases/download/v2.0.0/update.zip")]
    [InlineData("https://github.com:8443/MBudkin/LayoutFixer/releases/download/v2.0.0/update.zip")]
    [InlineData("https://github.com/MBudkin/LayoutFixer/releases/download/v2.0.0/other.zip")]
    [InlineData("https://github.com/MBudkin/LayoutFixer/releases/download/v2.0.0/update.zip?download=1")]
    public void UntrustedAssetLinksAreRejected(string url)
    {
        Assert.False(UpdateInstaller.IsProjectAssetUrl(url, "update.zip"));
    }

    [Fact]
    public void ReleaseParserIgnoresInvalidAssetMetadata()
    {
        UpdateInfo release = UpdateChecker.ParseRelease("""
            { "tag_name": "v2.0.0", "assets": [
              { "name": "update.zip", "state": "uploaded", "size": 123,
                "browser_download_url": "https://github.com/MBudkin/LayoutFixer/releases/download/v2.0.0/update.zip" },
              { "name": "pending.zip", "state": "new", "size": 123,
                "browser_download_url": "https://github.com/MBudkin/LayoutFixer/releases/download/v2.0.0/pending.zip" },
              { "name": "bad.zip", "state": "uploaded", "size": "garbage",
                "browser_download_url": "https://github.com/MBudkin/LayoutFixer/releases/download/v2.0.0/bad.zip" },
              null ] }
            """)!;
        Assert.Single(release.Assets);
        Assert.Equal("update.zip", release.Assets[0].Name);
    }

    [Fact]
    public async Task DownloadRejectsUntrustedRedirectBeforeSendingSecondRequest()
    {
        int requests = 0;
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://evil.test/package.zip") }
            };
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.OpenDownloadAsync(client,
            new Uri(Asset("update.zip").DownloadUrl), CancellationToken.None));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task DownloadAcceptsGitHubAssetCdnRedirect()
    {
        int requests = 0;
        using var client = new HttpClient(new FakeHandler(request =>
        {
            requests++;
            Assert.NotEmpty(request.Headers.UserAgent);
            return requests == 1 ? new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1?token=x") }
            } : new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using HttpResponseMessage response = await UpdateInstaller.OpenDownloadAsync(client,
            new Uri(Asset("update.zip").DownloadUrl), CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void SuppliedChecksumMustMatch()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("download"));
        UpdateInstaller.VerifyDigest("sha256:" + Convert.ToHexString(hash), hash);
        UpdateInstaller.VerifyDigest(null, hash);
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.VerifyDigest("sha256:" + new string('0', 64), hash));
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.VerifyDigest("md5:123", hash));
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.VerifyDigest("sha256:" + new string('z', 64), hash));
    }

    [Theory]
    [InlineData("LayoutFixer.exe")]
    [InlineData("LayoutFixer/LayoutFixer.exe")]
    [InlineData("LayoutFixer-2.0.0-framework/LayoutFixer.exe")]
    public void PackageExtractionWritesOnlyTheApplication(string name)
    {
        using var folder = new TestFolder();
        string zip = Path.Combine(folder.Path, "release.zip");
        WriteZip(zip, name, "LICENSE", "user-settings.json");
        string output = Path.Combine(folder.Path, "new.exe");
        UpdateInstaller.ExtractExecutable(zip, output);
        Assert.Equal(name, File.ReadAllText(output));
        Assert.False(File.Exists(Path.Combine(folder.Path, "LICENSE")));
        Assert.False(File.Exists(Path.Combine(folder.Path, "user-settings.json")));
    }

    [Theory]
    [InlineData("../LayoutFixer.exe")]
    [InlineData("..\\LayoutFixer.exe")]
    [InlineData("/LayoutFixer.exe")]
    [InlineData("C:/LayoutFixer.exe")]
    [InlineData("folder./LayoutFixer.exe")]
    [InlineData("folder /LayoutFixer.exe")]
    [InlineData("folder/LayoutFixer.exe:stream")]
    public void UnsafeArchivesAreRejectedWithoutWritingFiles(string name)
    {
        using var folder = new TestFolder();
        string zip = Path.Combine(folder.Path, "release.zip");
        WriteZip(zip, name);
        string output = Path.Combine(folder.Path, "new.exe");
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.ExtractExecutable(zip, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void MultipleExecutablesAreRejected()
    {
        using var folder = new TestFolder();
        string zip = Path.Combine(folder.Path, "release.zip");
        WriteZip(zip, "LayoutFixer.exe", "folder/LayoutFixer.exe");
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.ExtractExecutable(zip, Path.Combine(folder.Path, "new.exe")));
    }

    [Fact]
    public void DisposingUnstartedUpdatePreservesExistingApplicationAndUserFiles()
    {
        using var folder = new TestFolder();
        string work = Path.Combine(folder.Path, "work");
        Directory.CreateDirectory(work);
        string target = Path.Combine(folder.Path, "renamed.exe");
        string staged = Path.Combine(folder.Path, ".new.exe");
        string settings = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(target, "original");
        File.WriteAllText(staged, "new");
        File.WriteAllText(settings, "settings");
        using (var prepared = new PreparedUpdate(work, staged, target)) { }
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal("settings", File.ReadAllText(settings));
        Assert.False(File.Exists(staged));
        Assert.False(Directory.Exists(work));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHelperWaitsForExitThenInstallsOrRollsBack(bool failStartup)
    {
        // A harmless fixture replaces the application: no hooks, no profile,
        // no network. Its path deliberately exercises quoting and wildcards.
        using var folder = new TestFolder();
        string work = Path.Combine(folder.Path, "work");
        Directory.CreateDirectory(work);
        string target = Path.Combine(folder.Path, "renamed.exe");
        string staged = Path.Combine(folder.Path, ".new.exe");
        string backup = Path.Combine(folder.Path, ".previous.exe");
        string ready = Path.Combine(work, "ready");
        string error = Path.Combine(work, "error.txt");
        string signal = Path.Combine(folder.Path, "started.txt");
        string pidFile = Path.Combine(folder.Path, "started.pid");
        string untouched = Path.Combine(folder.Path, "settings.json");
        File.WriteAllText(untouched, "user data");
        await CompileFixtureAsync(target, "old", exitImmediately: false);
        await CompileFixtureAsync(staged, "new", failStartup);
        byte[] oldBytes = File.ReadAllBytes(target);
        byte[] newBytes = File.ReadAllBytes(staged);
        using Process parent = StartPowerShell("-Command", "Start-Sleep -Seconds 60");
        string script = Path.Combine(work, "install.ps1");
        string config = Path.Combine(work, "install.json");
        File.WriteAllText(script, UpdateInstallScript.Text);
        File.WriteAllText(config, JsonSerializer.Serialize(new
        {
            ProcessId = parent.Id,
            ProcessStartTicks = parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Executable = target, Staged = staged, Backup = backup, Ready = ready, Error = error, Work = work,
            Sha256 = Convert.ToHexString(SHA256.HashData(newBytes)),
            // Null prevents a modal error dialog in the failure-path fixture.
            ErrorMessage = (string?)null
        }));
        using Process helper = StartPowerShell("-File", script, "-ConfigPath", config);
        try
        {
            await WaitUntilAsync(() => File.Exists(ready) || helper.HasExited);
            Assert.False(helper.HasExited, File.Exists(error) ? File.ReadAllText(error) : "Installer exited before readiness.");
            Assert.Equal(oldBytes, File.ReadAllBytes(target));
            Assert.False(File.Exists(signal));
            parent.Kill();
            await parent.WaitForExitAsync();
            await WaitUntilAsync(() => helper.HasExited);
            await WaitUntilAsync(() => File.Exists(signal), () => File.Exists(error) ? File.ReadAllText(error) : "No installer error log.");
            Assert.Equal(failStartup ? "old" : "new", File.ReadAllText(signal));
            Assert.Equal(failStartup ? oldBytes : newBytes, File.ReadAllBytes(target));
            Assert.Equal("user data", File.ReadAllText(untouched));
            Assert.False(File.Exists(staged));
            Assert.False(File.Exists(backup));
            Assert.Equal(failStartup, File.Exists(error));
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(); parent.WaitForExit(); }
            if (!helper.HasExited) { helper.Kill(); helper.WaitForExit(); }
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out int id))
            {
                try
                {
                    using Process fixture = Process.GetProcessById(id);
                    if (fixture.MainModule?.FileName == target) { fixture.Kill(); fixture.WaitForExit(); }
                }
                catch (ArgumentException) { }
            }
        }
    }

    private static async Task CompileFixtureAsync(string destination, string label, bool exitImmediately)
    {
        // Windows PowerShell's Add-Type resolves OutputAssembly as a wildcard
        // path. Compile in the plain parent, then move using a literal .NET path.
        string compiled = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(destination))!,
            "fixture-" + Guid.NewGuid().ToString("N") + ".exe");
        string body = exitImmediately ? "return;" :
            "System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, \"started.txt\"), \"" + label + "\"); " +
            "System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, \"started.pid\"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString()); System.Threading.Thread.Sleep(30000);";
        string command = "Add-Type -TypeDefinition 'public static class Fixture { public static void Main() { " + body +
            " } }' -OutputAssembly $env:LAYOUTFIXER_FIXTURE_PATH -OutputType WindowsApplication";
        ProcessStartInfo start = PowerShellStart("-Command", command);
        start.Environment["LAYOUTFIXER_FIXTURE_PATH"] = compiled;
        start.RedirectStandardError = true;
        using Process compiler = Process.Start(start)!;
        string error = await compiler.StandardError.ReadToEndAsync();
        await compiler.WaitForExitAsync();
        Assert.True(compiler.ExitCode == 0, error);
        Assert.True(File.Exists(compiled), error);
        File.Move(compiled, destination);
    }

    private static Process StartPowerShell(params string[] args) => Process.Start(PowerShellStart(args))!;

    private static ProcessStartInfo PowerShellStart(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" }) start.ArgumentList.Add(arg);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string>? details = null)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), "Timed out waiting for the isolated installer fixture. " + details?.Invoke());
            await Task.Delay(50);
        }
    }

    private static UpdateInfo Release(params UpdateAsset[] assets) =>
        new(new Version(2, 0, 0), "release", "", ProjectLinks.ReleasesUrl, null) { Assets = assets };
    private static UpdateAsset Asset(string name) =>
        new(name, $"https://github.com/MBudkin/LayoutFixer/releases/download/v2.0.0/{name}", 123);

    private static void WriteZip(string path, params string[] names)
    {
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string name in names)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(name);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class TestFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LayoutFixer.Update.Tests." + Guid.NewGuid().ToString("N"), "папка '[$]& test");
        public TestFolder() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            string root = System.IO.Path.GetDirectoryName(Path)!;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
