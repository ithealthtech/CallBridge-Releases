param(
    [ValidateRange(1024, 65535)]
    [int]$Port = 8787,

    [ValidateRange(1, 3650)]
    [int]$CallRetentionDays = 90
)

$ErrorActionPreference = 'Stop'

# Some launch environments expose both `Path` and `PATH`; Windows PowerShell's
# Start-Process cannot copy that malformed environment to a child process.
$pathKeys = @([Environment]::GetEnvironmentVariables().Keys | Where-Object { $_ -cmatch '^(Path|PATH)$' })
if ($pathKeys -ccontains 'Path' -and $pathKeys -ccontains 'PATH') {
    Remove-Item Env:PATH -ErrorAction SilentlyContinue
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$DesktopDir = Join-Path $Root 'src\CallBridge.Desktop'
$ServiceDir = Join-Path $Root 'src\CallBridge.Service'
$DesktopExe = Join-Path $Root 'publish\desktop\CallBridge.Desktop.exe'
$DesktopProject = Join-Path $DesktopDir 'CallBridge.Desktop.csproj'
$ServiceExe = Join-Path $Root 'publish\service\CallBridge.Service.exe'
$ServiceDll = Join-Path $Root 'publish\service\CallBridge.Service.dll'
$ServiceProject = Join-Path $ServiceDir 'CallBridge.Service.csproj'
$RuntimeRoot = Join-Path $env:LOCALAPPDATA 'IT Health Technologies\CallBridge'
$DataDir = Join-Path $RuntimeRoot 'data'
$LogDir = Join-Path $RuntimeRoot 'logs'
$DesktopLog = Join-Path $LogDir 'callbridge-desktop.log'
$DesktopErrorLog = Join-Path $LogDir 'callbridge-desktop.err.log'
$HealthUrl = "http://127.0.0.1:$Port/health"

function New-SessionToken {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

function Get-PortOwners {
    Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique
}

function Get-OwnedServiceProcess {
    foreach ($processId in @(Get-PortOwners)) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId = $processId" -ErrorAction SilentlyContinue
        if (-not $process) { continue }

        $isPublishedService = $process.Name -ieq 'CallBridge.Service.exe' -and
            $process.ExecutablePath -and
            [IO.Path]::GetFullPath($process.ExecutablePath) -ieq [IO.Path]::GetFullPath($ServiceExe)
        $isDevelopmentService = $process.Name -ieq 'dotnet.exe' -and $process.CommandLine -and
            ($process.CommandLine.IndexOf($ServiceProject, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
             $process.CommandLine.IndexOf($ServiceDll, [StringComparison]::OrdinalIgnoreCase) -ge 0)

        if ($isPublishedService -or $isDevelopmentService) { $process }
    }
}

function Stop-OwnedOrphan {
    foreach ($process in @(Get-OwnedServiceProcess)) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
        Wait-Process -Id $process.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
    }
}

function Test-Health {
    try {
        $response = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 2
        return $response.ok -eq $true
    } catch { return $false }
}

function Get-StartupFailure {
    if (-not (Test-Path -LiteralPath $DesktopErrorLog)) { return 'No additional startup details were recorded.' }
    $details = @(Get-Content -LiteralPath $DesktopErrorLog -Tail 12 -ErrorAction SilentlyContinue)
    if ($details.Count -eq 0) { return 'No additional startup details were recorded.' }
    return ($details -join [Environment]::NewLine)
}

if (-not (Test-Path -LiteralPath $ServiceProject)) { throw "CallBridge Service was not found at $ServiceProject" }
if (-not (Test-Path -LiteralPath $DesktopProject)) { throw "CallBridge Desktop was not found at $DesktopProject" }

New-Item -ItemType Directory -Path $DataDir, $LogDir -Force | Out-Null
Stop-OwnedOrphan
if (@(Get-PortOwners).Count -gt 0) {
    throw "Port $Port is being used by another application. CallBridge will not terminate an unrelated process."
}

$dotnetPath = $null
if (-not (Test-Path -LiteralPath $ServiceExe) -or -not (Test-Path -LiteralPath $DesktopExe)) {
    $dotnetPath = & (Join-Path $Root 'scripts\Get-DotNetPath.ps1') -RequiredMajor 10
}

$env:CALLBRIDGE_LOCAL_TOKEN = New-SessionToken
$env:CALLBRIDGE_CALL_RETENTION_DAYS = [string]$CallRetentionDays
$env:PORT = [string]$Port
$env:DATABASE_PATH = Join-Path $DataDir 'callbridge.db'

$serviceProcess = $null
try {
    if (Test-Path -LiteralPath $ServiceExe) {
        $serviceProcess = Start-Process -FilePath $ServiceExe `
            -WorkingDirectory (Split-Path -Parent $ServiceExe) `
            -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $LogDir 'callbridge-service.log') `
            -RedirectStandardError (Join-Path $LogDir 'callbridge-service.err.log') `
            -PassThru
    } else {
        $serviceProcess = Start-Process -FilePath $dotnetPath `
            -ArgumentList @('run', '--project', ('"{0}"' -f $ServiceProject), '--configuration', 'Release', '--no-launch-profile') `
            -WorkingDirectory $ServiceDir `
            -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $LogDir 'callbridge-service.log') `
            -RedirectStandardError (Join-Path $LogDir 'callbridge-service.err.log') `
            -PassThru
    }

    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and -not (Test-Health)) {
        if ($serviceProcess.HasExited) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-Health)) { throw "CallBridge Service did not start. Review $LogDir" }

    Remove-Item -LiteralPath $DesktopLog, $DesktopErrorLog -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $DesktopExe) {
        $desktopProcess = Start-Process -FilePath $DesktopExe `
            -WorkingDirectory (Split-Path -Parent $DesktopExe) `
            -RedirectStandardOutput $DesktopLog `
            -RedirectStandardError $DesktopErrorLog `
            -PassThru
    } else {
        $desktopProcess = Start-Process -FilePath $dotnetPath `
            -ArgumentList @('run', '--project', ('"{0}"' -f $DesktopProject), '--configuration', 'Release', '--no-launch-profile') `
            -WorkingDirectory $DesktopDir `
            -RedirectStandardOutput $DesktopLog `
            -RedirectStandardError $DesktopErrorLog `
            -PassThru
    }

    $desktopProcess.WaitForExit()
    $desktopProcess.Refresh()
    $desktopExitCode = $desktopProcess.ExitCode
    if ($null -ne $desktopExitCode -and $desktopExitCode -ne 0) {
        $details = Get-StartupFailure
        throw "CallBridge Desktop exited with code $desktopExitCode.`n$details`nLogs: $LogDir"
    }
} finally {
    if ($serviceProcess -and -not $serviceProcess.HasExited) {
        Stop-Process -Id $serviceProcess.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $serviceProcess.Id -Timeout 5 -ErrorAction SilentlyContinue
    }
    Remove-Item Env:CALLBRIDGE_LOCAL_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:CALLBRIDGE_CALL_RETENTION_DAYS -ErrorAction SilentlyContinue
    Remove-Item Env:PORT -ErrorAction SilentlyContinue
    Remove-Item Env:DATABASE_PATH -ErrorAction SilentlyContinue
}
