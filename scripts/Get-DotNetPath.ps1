[CmdletBinding()]
param(
    [ValidateRange(1, 99)]
    [int]$RequiredMajor = 10
)

$ErrorActionPreference = "Stop"

$candidates = New-Object System.Collections.Generic.List[string]
$userLocal = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet$RequiredMajor\dotnet.exe"
if (Test-Path -LiteralPath $userLocal) { $candidates.Add($userLocal) }
$command = Get-Command "dotnet.exe" -ErrorAction SilentlyContinue
if ($command -and -not $candidates.Contains($command.Source)) { $candidates.Add($command.Source) }

foreach ($candidate in $candidates)
{
    try
    {
        $version = (& $candidate --version 2>$null | Select-Object -First 1).Trim()
        if ($version -match "^$RequiredMajor\.")
        {
            Write-Output $candidate
            exit 0
        }
    }
    catch { }
}

throw ".NET $RequiredMajor SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/$RequiredMajor.0."
