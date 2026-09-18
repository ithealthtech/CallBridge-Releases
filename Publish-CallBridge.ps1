[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidatePattern('^win-(x64|arm64)$')]
    [string]$Runtime = "win-x64",
    [string]$Version = "0.19.0"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath($PSScriptRoot)
$publishRoot = [IO.Path]::GetFullPath((Join-Path $root "publish"))
if (-not $publishRoot.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish output must remain inside the CallBridge project."
}

$dotnet = & (Join-Path $root "scripts\Get-DotNetPath.ps1") -RequiredMajor 10
$serviceProject = Join-Path $root "src\CallBridge.Service\CallBridge.Service.csproj"
$desktopProject = Join-Path $root "src\CallBridge.Desktop\CallBridge.Desktop.csproj"

foreach ($project in @($serviceProject, $desktopProject)) {
    & $dotnet restore $project -r $Runtime --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Runtime restore failed for $project." }
}

foreach ($directory in @((Join-Path $publishRoot "service"), (Join-Path $publishRoot "desktop"))) {
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

& $dotnet publish $serviceProject -c $Configuration -r $Runtime --self-contained true --no-restore -o (Join-Path $publishRoot "service") -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "CallBridge service publish failed." }
& $dotnet publish $desktopProject -c $Configuration -r $Runtime --self-contained true --no-restore -o (Join-Path $publishRoot "desktop") -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "CallBridge desktop publish failed." }

Write-Host "Self-contained CallBridge $Runtime publish completed: $publishRoot"
