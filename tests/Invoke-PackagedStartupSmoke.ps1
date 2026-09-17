[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$smokeRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts\startup-smoke"))
if (-not $smokeRoot.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Startup smoke output must remain inside the CallBridge project."
}

$existing = @(Get-Process -Name "CallBridge.Desktop", "CallBridge.Service" -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) {
    throw "Close CallBridge before running the packaged startup smoke test."
}

if (Test-Path -LiteralPath $smokeRoot) {
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force
}
$desktopRoot = Join-Path $smokeRoot "desktop"
$serviceRoot = Join-Path $smokeRoot "service"
New-Item -ItemType Directory -Path $desktopRoot, $serviceRoot -Force | Out-Null
$dotnet = & (Join-Path $projectRoot "scripts\Get-DotNetPath.ps1") -RequiredMajor 10

$serviceProject = Join-Path $projectRoot "src\CallBridge.Service\CallBridge.Service.csproj"
$desktopProject = Join-Path $projectRoot "src\CallBridge.Desktop\CallBridge.Desktop.csproj"
foreach ($project in @($serviceProject, $desktopProject)) {
    & $dotnet restore $project -r win-x64 --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Runtime restore failed for $project." }
}
& $dotnet publish $serviceProject -c $Configuration -r win-x64 --self-contained true -o $serviceRoot --no-restore
if ($LASTEXITCODE -ne 0) { throw "CallBridge service publish failed." }
& $dotnet publish $desktopProject -c $Configuration -r win-x64 --self-contained true -o $desktopRoot --no-restore
if ($LASTEXITCODE -ne 0) { throw "CallBridge desktop publish failed." }

$desktopExe = Join-Path $desktopRoot "CallBridge.Desktop.exe"
$smokeResult = Join-Path ([IO.Path]::GetTempPath()) ("CallBridge\startup-smoke-{0}.log" -f [Guid]::NewGuid().ToString("N"))
$previousSmokeResult = $env:CALLBRIDGE_STARTUP_SMOKE_RESULT
$previousSettingsRoot = $env:CALLBRIDGE_SETTINGS_ROOT
try {
    $env:CALLBRIDGE_STARTUP_SMOKE_RESULT = $smokeResult
    $env:CALLBRIDGE_SETTINGS_ROOT = Join-Path $smokeRoot "profile"
    $process = Start-Process -FilePath $desktopExe -ArgumentList "--startup-smoke --service-recovery-smoke --single-instance-smoke" -WorkingDirectory $desktopRoot -PassThru
    $readyDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $readyDeadline) {
        if ((Test-Path -LiteralPath $smokeResult) -and (Select-String -LiteralPath $smokeResult -SimpleMatch "Single-instance smoke primary ready" -Quiet)) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($process.HasExited -or -not (Test-Path -LiteralPath $smokeResult) -or -not (Select-String -LiteralPath $smokeResult -SimpleMatch "Single-instance smoke primary ready" -Quiet)) {
        try { if (-not $process.HasExited) { $process.Kill($true) } } catch { }
        throw "CallBridge did not become ready for the single-instance activation test."
    }

    $activationProcess = Start-Process -FilePath $desktopExe -ArgumentList "--activation-probe" -WorkingDirectory $desktopRoot -Wait -PassThru
    if ($activationProcess.ExitCode -ne 0) {
        throw "The second CallBridge launch exited with code $($activationProcess.ExitCode)."
    }

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill($true) } catch { }
        throw "CallBridge did not finish the startup smoke test within $TimeoutSeconds seconds."
    }
}
finally {
    $env:CALLBRIDGE_STARTUP_SMOKE_RESULT = $previousSmokeResult
    $env:CALLBRIDGE_SETTINGS_ROOT = $previousSettingsRoot
}
$process.Refresh()
if ($process.ExitCode -ne 0) {
    throw "CallBridge startup smoke test exited with code $($process.ExitCode)."
}

Start-Sleep -Milliseconds 500
$orphans = @(Get-Process -Name "CallBridge.Desktop", "CallBridge.Service" -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($smokeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }
    catch { $false }
})
if ($orphans.Count -gt 0) {
    foreach ($orphan in $orphans) { try { $orphan.Kill($true) } catch { } }
    throw "CallBridge left a packaged startup-smoke process running after shutdown."
}

$recentLog = if (Test-Path -LiteralPath $smokeResult) { @(Get-Content -LiteralPath $smokeResult) } else { @() }

foreach ($requiredMessage in @(
    "Local service recovery smoke test passed",
    "Single-instance smoke primary ready",
    "Existing CallBridge window activation requested",
    "Single-instance activation smoke test passed",
    "Startup smoke test rendered successfully",
    "Desktop shutdown complete",
    "Main window closed",
    "Desktop application exited"
)) {
    if (-not ($recentLog -match [Regex]::Escape($requiredMessage))) {
        throw "Startup smoke log did not contain '$requiredMessage'."
    }
}

Write-Host "Packaged CallBridge startup, render, and clean shutdown smoke test passed."
