param([Parameter(Mandatory = $true)][string]$Executable)

$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path $Executable).Path
$testDirectory = Join-Path $env:RUNNER_TEMP ('ecloud update smoke ' + [guid]::NewGuid().ToString('N'))
$installDirectory = Join-Path $testDirectory 'installed app'
$stagingRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'EcloudKeepAlive/updates'
$stagingDirectory = Join-Path $stagingRoot ('update-' + [guid]::NewGuid().ToString('N'))
$trackedProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Start-TestProcess([string]$Path, [string[]]$Arguments) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Path
    $info.WorkingDirectory = Split-Path $Path
    $info.UseShellExecute = $false
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($info)
    $trackedProcesses.Add($process)
    return $process
}

function Wait-TestFile([string]$Path, [System.Diagnostics.Process]$Process, [int]$Seconds) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path $Path)) {
        if ($Process.HasExited) { throw "Test process exited before acknowledgement: $($Process.ExitCode)" }
        if ($clock.Elapsed.TotalSeconds -gt $Seconds) { throw "Acknowledgement timed out: $Path" }
        Start-Sleep -Milliseconds 100
    }
}

try {
    New-Item -ItemType Directory -Path $installDirectory, $stagingDirectory -Force | Out-Null
    $installed = Join-Path $installDirectory 'EcloudKeepAlive.exe'
    $staged = Join-Path $stagingDirectory 'EcloudKeepAlive.exe'
    Copy-Item $Executable $installed
    Copy-Item $Executable $staged
    $readyPath = Join-Path $stagingDirectory 'started.ready'
    $original = Start-TestProcess $installed @('--update-ready', $readyPath)
    Wait-TestFile $readyPath $original 45
    Remove-Item $readyPath

    $hash = (Get-FileHash $staged -Algorithm SHA256).Hash.ToLowerInvariant()
    $requestPath = Join-Path $stagingDirectory 'install-request.json'
    @{
        ParentProcessId = $original.Id
        ParentStartTimeUtcTicks = $original.StartTime.ToUniversalTime().Ticks
        OriginalUserSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        TargetPath = $installed
        ExpectedHash = $hash
        LogPath = (Join-Path $testDirectory 'update.log')
        ResumeKeepAlive = $true
        RefreshStartupTask = $false
    } | ConvertTo-Json | Set-Content $requestPath -Encoding utf8

    $helper = Start-TestProcess $staged @('--apply-update', $requestPath)
    Wait-TestFile ($requestPath + '.ready') $helper 30
    # Only terminate the original process created by this test, never a process selected by name.
    $original.Kill($true)
    if (-not $original.WaitForExit(10000)) { throw 'Original test process did not exit.' }
    if (-not $helper.WaitForExit(90000)) { throw 'Update helper did not finish.' }
    if ($helper.ExitCode -ne 0) { throw "Update helper failed: $($helper.ExitCode)" }

    if (-not (Test-Path ($installed + '.previous'))) { throw 'Previous executable backup is missing.' }
    if ((Get-FileHash $installed -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Installed executable hash does not match.' }
    $restartedId = [int](Get-Content $readyPath -Raw).Trim()
    $restarted = [System.Diagnostics.Process]::GetProcessById($restartedId)
    $trackedProcesses.Add($restarted)
    if ($restarted.HasExited -or $restarted.MainModule.FileName -ne $installed) { throw 'Restarted application is not running from the installed path.' }
    $log = Get-Content (Join-Path $testDirectory 'update.log') -Raw
    if ($log -notmatch '在线更新完成') { throw 'Successful completion was not recorded.' }
    Write-Host 'PASS: real Windows EXE startup, helper handoff, locked-file replacement, backup and restart acknowledgement.'
}
finally {
    foreach ($process in $trackedProcesses) {
        try {
            if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(10000) }
        } catch { }
        $process.Dispose()
    }
    Remove-Item $testDirectory, $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
