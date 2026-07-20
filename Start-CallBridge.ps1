$ErrorActionPreference = 'Stop'

$AppDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ExePath = Join-Path $AppDir 'publish\CallBridge.Desktop.exe'
$ProjectPath = Join-Path $AppDir 'CallBridge.Desktop.csproj'
$BackendDir = Resolve-Path -LiteralPath (Join-Path $AppDir '..\..\Alpha Testing\CallBridge-Internal-v0.7') -ErrorAction SilentlyContinue
$BackendLogDir = Join-Path $AppDir 'logs'
$BackendLog = Join-Path $BackendLogDir 'callbridge-internal.log'
$HealthUrl = 'http://127.0.0.1:8787/health'

function Test-CallBridgePort {
    try {
        $connection = Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort 8787 -State Listen -ErrorAction Stop
        return $null -ne $connection
    } catch {
        return $false
    }
}

function Test-CallBridgeHealth {
    try {
        $response = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 2
        return $response.ok -eq $true
    } catch {
        return $false
    }
}

function Start-Backend {
    if (Test-CallBridgePort) { return }
    if (-not $BackendDir) { return }

    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $node) { return }

    New-Item -ItemType Directory -Path $BackendLogDir -Force | Out-Null
    Start-Process -FilePath $node.Source `
        -ArgumentList @('apps/service/src/server.js') `
        -WorkingDirectory $BackendDir.Path `
        -WindowStyle Hidden `
        -RedirectStandardOutput $BackendLog `
        -RedirectStandardError (Join-Path $BackendLogDir 'callbridge-internal.err.log') | Out-Null

    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline) {
        if (Test-CallBridgeHealth) { return }
        Start-Sleep -Milliseconds 500
    }
}

Start-Backend

if (Test-Path -LiteralPath $ExePath) {
    Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath)
} elseif (Test-Path -LiteralPath $ProjectPath) {
    Start-Process -FilePath 'dotnet' -ArgumentList @('run', '--project', $ProjectPath, '--configuration', 'Release') -WorkingDirectory $AppDir
} else {
    throw 'CallBridge desktop executable was not found.'
}
