[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MsiPath,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "The MSI lifecycle smoke test must run from an elevated PowerShell session."
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$testRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts\msi-lifecycle"))
if (-not $testRoot.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "MSI lifecycle output must remain inside the CallBridge project."
}
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$failureResult = Join-Path $testRoot "failure.txt"
Remove-Item -LiteralPath $failureResult -Force -ErrorAction SilentlyContinue
trap {
    Set-Content -LiteralPath $failureResult -Value ($_ | Out-String) -Encoding UTF8
    exit 1
}
$resolvedMsi = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $MsiPath).Path)

$installRoot = Join-Path $env:ProgramFiles "CallBridge"
$desktopExe = Join-Path $installRoot "publish\desktop\CallBridge.Desktop.exe"
$startMenuShortcut = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\CallBridge.lnk"
$desktopShortcut = Join-Path $env:PUBLIC "Desktop\CallBridge.lnk"
$msiexec = Join-Path $env:SystemRoot "System32\msiexec.exe"
$installedByTest = $false

function Get-MsiProperty {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$PropertyName
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $installer, @($PackagePath, 0))
    $view = $database.GetType().InvokeMember(
        "OpenView",
        "InvokeMethod",
        $null,
        $database,
        @("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$PropertyName'")
    )
    $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null
    $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
    if ($null -eq $record) { throw "MSI property '$PropertyName' was not found." }
    $value = $record.GetType().InvokeMember("StringData", "GetProperty", $null, $record, @(1))
    $view.GetType().InvokeMember("Close", "InvokeMethod", $null, $view, $null) | Out-Null
    return $value
}

function Invoke-MsiExec {
    param(
        [Parameter(Mandatory)][string]$Arguments,
        [Parameter(Mandatory)][string]$Operation
    )

    $process = Start-Process -FilePath $msiexec -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -notin @(0, 1641, 3010)) {
        throw "$Operation failed with Windows Installer exit code $($process.ExitCode)."
    }
}

$existing = @(
    Get-ItemProperty -Path @(
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    ) -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties["DisplayName"] -and $_.DisplayName -eq "CallBridge" }
)
if ($existing.Count -gt 0 -or (Test-Path -LiteralPath $installRoot)) {
    throw "CallBridge is already installed. The lifecycle test will not replace an existing installation."
}

$running = @(Get-Process -Name "CallBridge.Desktop", "CallBridge.Service" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw "Close CallBridge before running the MSI lifecycle smoke test."
}

$productCode = Get-MsiProperty -PackagePath $resolvedMsi -PropertyName "ProductCode"
$installLog = Join-Path $testRoot "install.log"
$repairLog = Join-Path $testRoot "repair.log"
$uninstallLog = Join-Path $testRoot "uninstall.log"
$smokeResult = Join-Path ([IO.Path]::GetTempPath()) ("CallBridge\msi-startup-smoke-{0}.log" -f [Guid]::NewGuid().ToString("N"))
$smokeEvidence = Join-Path $testRoot "startup-smoke.log"
$successResult = Join-Path $testRoot "result.txt"
$settingsRoot = Join-Path $testRoot "profile"
Remove-Item -LiteralPath $successResult, $smokeEvidence -Force -ErrorAction SilentlyContinue

try {
    Invoke-MsiExec -Arguments "/i `"$resolvedMsi`" /qn /norestart /l*v `"$installLog`"" -Operation "Install"
    $installedByTest = $true

    foreach ($requiredPath in @($desktopExe, $startMenuShortcut, $desktopShortcut)) {
        if (-not (Test-Path -LiteralPath $requiredPath)) {
            throw "The installed package is missing '$requiredPath'."
        }
    }

    $previousSmokeResult = $env:CALLBRIDGE_STARTUP_SMOKE_RESULT
    $previousSettingsRoot = $env:CALLBRIDGE_SETTINGS_ROOT
    try {
        $env:CALLBRIDGE_STARTUP_SMOKE_RESULT = $smokeResult
        $env:CALLBRIDGE_SETTINGS_ROOT = $settingsRoot
        $desktop = Start-Process -FilePath $desktopExe -ArgumentList "--startup-smoke" -WorkingDirectory (Split-Path $desktopExe) -PassThru
        if (-not $desktop.WaitForExit($TimeoutSeconds * 1000)) {
            try { $desktop.Kill($true) } catch { }
            throw "The installed CallBridge executable did not finish its startup smoke test in time."
        }
        $desktop.Refresh()
        if ($desktop.ExitCode -ne 0) {
            throw "The installed CallBridge executable exited with code $($desktop.ExitCode)."
        }
    }
    finally {
        $env:CALLBRIDGE_STARTUP_SMOKE_RESULT = $previousSmokeResult
        $env:CALLBRIDGE_SETTINGS_ROOT = $previousSettingsRoot
    }

    foreach ($requiredMessage in @(
        "Startup smoke test rendered successfully",
        "Desktop shutdown complete",
        "Main window closed",
        "Desktop application exited"
    )) {
        if (-not (Select-String -LiteralPath $smokeResult -SimpleMatch $requiredMessage -Quiet)) {
            throw "Installed startup smoke log did not contain '$requiredMessage'."
        }
    }
    Copy-Item -LiteralPath $smokeResult -Destination $smokeEvidence -Force

    Invoke-MsiExec -Arguments "/fa $productCode /qn /norestart /l*v `"$repairLog`"" -Operation "Repair"
    if (-not (Test-Path -LiteralPath $desktopExe)) {
        throw "MSI repair did not preserve the CallBridge executable."
    }

    Invoke-MsiExec -Arguments "/x $productCode /qn /norestart /l*v `"$uninstallLog`"" -Operation "Uninstall"
    $installedByTest = $false
    foreach ($removedPath in @($desktopExe, $startMenuShortcut, $desktopShortcut)) {
        if (Test-Path -LiteralPath $removedPath) {
            throw "MSI uninstall left '$removedPath' behind."
        }
    }

    $successMessage = "CallBridge MSI install, shortcut, installed-startup, repair, and uninstall smoke test passed."
    Set-Content -LiteralPath $successResult -Value $successMessage -Encoding UTF8
    Write-Host $successMessage
}
finally {
    if ($installedByTest) {
        try { Invoke-MsiExec -Arguments "/x $productCode /qn /norestart /l*v `"$uninstallLog`"" -Operation "Cleanup uninstall" } catch { }
    }
    Remove-Item -LiteralPath $smokeResult -Force -ErrorAction SilentlyContinue
}
