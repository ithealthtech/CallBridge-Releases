[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0",
    [switch]$SelfContained,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$InstallerDirectory = $PSScriptRoot
$ProjectRoot = Split-Path -Parent $InstallerDirectory

# Change this if your .csproj has a different location or name.
$ProjectFile = Join-Path $ProjectRoot "src\CallBridge\CallBridge.csproj"

$PublishDirectory = Join-Path $ProjectRoot "publish"
$DistributionDirectory = Join-Path $ProjectRoot "dist"
$InstallerScript = Join-Path $InstallerDirectory "CallBridge.iss"

function Find-InnoSetupCompiler {
    $possiblePaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($path in $possiblePaths) {
        if ($path -and (Test-Path $path)) {
            return $path
        }
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue

    if ($command) {
        return $command.Source
    }

    throw @"
Inno Setup Compiler was not found.

Install Inno Setup and then run this script again.
The compiler executable should be named ISCC.exe.
"@
}

function Assert-CommandExists {
    param(
        [Parameter(Mandatory)]
        [string]$CommandName
    )

    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command '$CommandName' was not found in PATH."
    }
}

Write-Host "Building CallBridge installer..." -ForegroundColor Cyan
Write-Host "Version:       $Version"
Write-Host "Configuration: $Configuration"
Write-Host "Runtime:       $Runtime"
Write-Host "Self-contained: $($SelfContained.IsPresent)"

if (-not $SkipPublish) {
    Assert-CommandExists -CommandName "dotnet"

    if (-not (Test-Path $ProjectFile)) {
        throw "CallBridge project file was not found: $ProjectFile"
    }

    if (Test-Path $PublishDirectory) {
        Remove-Item $PublishDirectory -Recurse -Force
    }

    New-Item $PublishDirectory -ItemType Directory -Force | Out-Null

    $publishArguments = @(
        "publish"
        $ProjectFile
        "--configuration", $Configuration
        "--runtime", $Runtime
        "--output", $PublishDirectory
        "-p:Version=$Version"
        "-p:DebugType=None"
        "-p:DebugSymbols=false"
    )

    if ($SelfContained) {
        $publishArguments += @(
            "--self-contained", "true"
            "-p:PublishSingleFile=true"
            "-p:IncludeNativeLibrariesForSelfExtract=true"
            "-p:EnableCompressionInSingleFile=true"
        )
    }
    else {
        $publishArguments += @(
            "--self-contained", "false"
        )
    }

    Write-Host ""
    Write-Host "Publishing CallBridge..." -ForegroundColor Yellow

    & dotnet @publishArguments

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

$ExecutablePath = Join-Path $PublishDirectory "CallBridge.exe"

if (-not (Test-Path $ExecutablePath)) {
    throw @"
CallBridge.exe was not found in the publish directory:

$ExecutablePath

Confirm that:
1. The project builds a Windows executable.
2. The assembly name is CallBridge.
3. The publish step completed successfully.
"@
}

New-Item $DistributionDirectory -ItemType Directory -Force | Out-Null

$InnoCompiler = Find-InnoSetupCompiler

Write-Host ""
Write-Host "Compiling installer..." -ForegroundColor Yellow
Write-Host "Compiler: $InnoCompiler"

$innoArguments = @(
    "/DAppVersion=$Version"
    "/DSourceDirectory=$PublishDirectory"
    "/DOutputDirectory=$DistributionDirectory"
    $InstallerScript
)

& $InnoCompiler @innoArguments

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$InstallerPath = Join-Path $DistributionDirectory "CallBridge-Setup-$Version.exe"

if (-not (Test-Path $InstallerPath)) {
    throw "The installer compiler completed, but the expected installer was not found: $InstallerPath"
}

$InstallerHash = Get-FileHash $InstallerPath -Algorithm SHA256

Write-Host ""
Write-Host "CallBridge installer built successfully." -ForegroundColor Green
Write-Host "Installer: $InstallerPath"
Write-Host "SHA-256:   $($InstallerHash.Hash)"