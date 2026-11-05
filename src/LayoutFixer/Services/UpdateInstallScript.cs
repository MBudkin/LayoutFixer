namespace LayoutFixer.Services;

/// <summary>
/// The installer runs outside LayoutFixer so Windows can release its executable.
/// All paths are data in JSON, never interpolated into PowerShell source.
/// </summary>
internal static class UpdateInstallScript
{
    internal const string Text = """
        param([Parameter(Mandatory = $true)][string]$ConfigPath)
        $ErrorActionPreference = 'Stop'
        $config = $null
        $ready = $false
        $replaced = $false
        $preserveBackup = $false
        $parentExited = $false
        try {
            $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
            $target = [System.IO.Path]::GetFullPath($config.Executable)
            $staged = [System.IO.Path]::GetFullPath($config.Staged)
            $backup = [System.IO.Path]::GetFullPath($config.Backup)
            $folder = [System.IO.Path]::GetDirectoryName($target)
            if (-not $folder.Equals([System.IO.Path]::GetDirectoryName($staged), [StringComparison]::OrdinalIgnoreCase) -or
                -not $folder.Equals([System.IO.Path]::GetDirectoryName($backup), [StringComparison]::OrdinalIgnoreCase) -or
                $target.Equals($staged, [StringComparison]::OrdinalIgnoreCase) -or
                $target.Equals($backup, [StringComparison]::OrdinalIgnoreCase) -or
                $staged.Equals($backup, [StringComparison]::OrdinalIgnoreCase) -or
                [System.IO.File]::Exists($backup)) {
                throw 'Invalid update file paths.'
            }
            if ([System.IO.File]::GetAttributes($target) -band [System.IO.FileAttributes]::ReparsePoint) {
                throw 'Updating an executable link is not supported.'
            }
            $hash = [System.Security.Cryptography.SHA256]::Create()
            $stream = [System.IO.File]::OpenRead($staged)
            try { $actualHash = [System.BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $hash.Dispose() }
            if ($actualHash -ne $config.Sha256) {
                throw 'The prepared update checksum does not match.'
            }
            $parent = [System.Diagnostics.Process]::GetProcessById([int]$config.ProcessId)
            if ($parent.StartTime.ToUniversalTime().Ticks.ToString() -ne $config.ProcessStartTicks) {
                throw 'The running application changed before installation.'
            }
            [System.IO.File]::WriteAllText($config.Ready, 'ready')
            $ready = $true
            if (-not $parent.WaitForExit(45000)) {
                throw 'LayoutFixer did not close. No files were replaced.'
            }
            $parentExited = $true
            $parent.Dispose()

            # File.Replace is atomic and the source is on the same volume.
            # Brief retries allow antivirus scanners to release the old image.
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                try {
                    [System.IO.File]::Replace($staged, $target, $backup, $true)
                    $replaced = $true
                    break
                } catch [System.IO.IOException] {
                    if ($attempt -eq 19) { throw }
                    Start-Sleep -Milliseconds 250
                }
            }
            $start = New-Object System.Diagnostics.ProcessStartInfo
            $start.FileName = $target
            $start.WorkingDirectory = $folder
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $child = [System.Diagnostics.Process]::Start($start)
            if ($null -eq $child -or $child.WaitForExit(3000)) {
                throw 'The updated application exited during startup.'
            }
            $child.Dispose()
            # Cleanup cannot turn an already running update into a rollback.
            try { [System.IO.File]::Delete($backup) } catch { }
        } catch {
            $failure = $_.Exception.Message
            if ($replaced) {
                try {
                    # PowerShell coerces $null to an empty string for this
                    # overload. Reuse the consumed staging path for the failed
                    # image instead; finally removes it after the rollback.
                    [System.IO.File]::Replace($backup, $target, $staged, $true)
                    $replaced = $false
                } catch {
                    $preserveBackup = $true
                    $failure += [Environment]::NewLine + 'Backup: ' + $backup + [Environment]::NewLine + $_.Exception.Message
                }
            }
            if ($null -ne $config) {
                try { [System.IO.File]::WriteAllText($config.Error, $failure) } catch { }
                if ($parentExited -and -not $preserveBackup) {
                    try {
                        $restart = New-Object System.Diagnostics.ProcessStartInfo
                        $restart.FileName = $target
                        $restart.WorkingDirectory = $folder
                        $restart.UseShellExecute = $false
                        $restart.CreateNoWindow = $true
                        $oldProcess = [System.Diagnostics.Process]::Start($restart)
                        if ($null -ne $oldProcess) { $oldProcess.Dispose() }
                    } catch { }
                }
                if ($ready -and -not [string]::IsNullOrEmpty($config.ErrorMessage)) {
                    try {
                        Add-Type -AssemblyName System.Windows.Forms
                        [System.Windows.Forms.MessageBox]::Show(
                            [string]::Format($config.ErrorMessage, $failure), 'LayoutFixer', 'OK', 'Error') | Out-Null
                    } catch { }
                }
            }
        } finally {
            if ($null -ne $config) {
                try { [System.IO.File]::Delete($config.Staged) } catch { }
                # On failure retain the log and any recovery backup. On success,
                # remove only the unique, explicitly supplied temporary folder.
                if (-not [System.IO.File]::Exists($config.Error)) {
                    try { [System.IO.Directory]::Delete($config.Work, $true) } catch { }
                }
            }
        }
        """;
}
