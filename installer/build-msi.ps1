[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "0.13.0",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$InstallerDirectory = $PSScriptRoot
$ProjectRoot = Split-Path -Parent $InstallerDirectory
$PayloadRoot = Join-Path $ProjectRoot "artifacts\msi-payload"
$OutputRoot = Join-Path $ProjectRoot "artifacts\installer"
$WixSource = Join-Path $OutputRoot "CallBridge.Generated.wxs"
$MsiPath = Join-Path $OutputRoot "CallBridge-v$Version.msi"

$DesktopProject = Join-Path $ProjectRoot "src\CallBridge.Desktop\CallBridge.Desktop.csproj"
$ServiceProject = Join-Path $ProjectRoot "src\CallBridge.Service\CallBridge.Service.csproj"
$NuGetConfig = Join-Path $ProjectRoot "NuGet.Config"

function Assert-CommandExists {
    param([Parameter(Mandatory)][string]$CommandName)
    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command '$CommandName' was not found in PATH."
    }
}

function Get-WixCommand {
    $command = Get-Command "wix.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidatePaths = @(
        "$env:USERPROFILE\.dotnet\tools\wix.exe",
        "${env:ProgramFiles}\WiX Toolset v4\bin\wix.exe",
        "${env:ProgramFiles(x86)}\WiX Toolset v4\bin\wix.exe"
    )

    foreach ($path in $candidatePaths) {
        if ($path -and (Test-Path -LiteralPath $path)) { return $path }
    }

    throw @"
WiX Toolset was not found.

Install WiX Toolset v4, then rerun this script:

  dotnet tool install --global wix

If this machine cannot use global dotnet tools, install WiX Toolset v4 from the official installer and make wix.exe available in PATH.
"@
}

function ConvertTo-WixId {
    param([Parameter(Mandatory)][string]$Value)
    $id = ($Value -replace "[^A-Za-z0-9_\.]", "_")
    if ($id -notmatch "^[A-Za-z_]") { $id = "Id_$id" }
    if ($id.Length -gt 70) { $id = $id.Substring(0, 70) }
    return $id
}

function ConvertTo-XmlText {
    param([Parameter(Mandatory)][string]$Value)
    return [Security.SecurityElement]::Escape($Value)
}

function Add-FileComponentXml {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$DirectoryPath,
        [Parameter(Mandatory)][string]$DirectoryId,
        [Parameter(Mandatory)]$DirectoryXml,
        [Parameter(Mandatory)]$ComponentRefs
    )

    $files = Get-ChildItem -LiteralPath $DirectoryPath -File | Sort-Object Name
    $index = 0
    foreach ($file in $files) {
        if ($file.Name -ieq "settings.json" -or $file.Name -ieq "portal-secrets.json" -or $file.Name -like "*.pfx" -or $file.Name -like "*.pem" -or $file.Name -like "*.key") {
            throw "Refusing to package secret-like file: $($file.FullName)"
        }

        $relative = $file.FullName.Substring($SourceRoot.Length).TrimStart("\")
        $componentId = ConvertTo-WixId ("cmp_" + ($relative -replace "\\", "_") + "_$index")
        $fileId = ConvertTo-WixId ("fil_" + ($relative -replace "\\", "_") + "_$index")
        $source = ConvertTo-XmlText $file.FullName
        $name = ConvertTo-XmlText $file.Name

        $DirectoryXml.Add("        <Component Id=""$componentId"" Guid=""*"">")
        $DirectoryXml.Add("          <File Id=""$fileId"" Source=""$source"" Name=""$name"" KeyPath=""yes"" />")
        $DirectoryXml.Add("        </Component>")
        $ComponentRefs.Add("      <ComponentRef Id=""$componentId"" />")
        $index++
    }
}

function Add-DirectoryXml {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$DirectoryPath,
        [Parameter(Mandatory)][string]$DirectoryId,
        [Parameter(Mandatory)]$DirectoryXml,
        [Parameter(Mandatory)]$ComponentRefs
    )

    Add-FileComponentXml -SourceRoot $SourceRoot -DirectoryPath $DirectoryPath -DirectoryId $DirectoryId -DirectoryXml $DirectoryXml -ComponentRefs $ComponentRefs

    $directories = Get-ChildItem -LiteralPath $DirectoryPath -Directory | Sort-Object Name
    foreach ($directory in $directories) {
        $relative = $directory.FullName.Substring($SourceRoot.Length).TrimStart("\")
        $childId = ConvertTo-WixId ("dir_" + ($relative -replace "\\", "_"))
        $name = ConvertTo-XmlText $directory.Name
        $DirectoryXml.Add("        <Directory Id=""$childId"" Name=""$name"">")
        Add-DirectoryXml -SourceRoot $SourceRoot -DirectoryPath $directory.FullName -DirectoryId $childId -DirectoryXml $DirectoryXml -ComponentRefs $ComponentRefs
        $DirectoryXml.Add("        </Directory>")
    }
}

Assert-CommandExists "dotnet"
$Wix = Get-WixCommand

if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $PayloadRoot) { Remove-Item -LiteralPath $PayloadRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $PayloadRoot "publish\desktop"), (Join-Path $PayloadRoot "publish\service") | Out-Null

    dotnet publish $ServiceProject -c $Configuration -o (Join-Path $PayloadRoot "publish\service") --self-contained false --no-restore -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "Service publish failed." }

    dotnet publish $DesktopProject -c $Configuration -o (Join-Path $PayloadRoot "publish\desktop") --self-contained false --no-restore -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed." }

    Copy-Item -LiteralPath (Join-Path $ProjectRoot "README.md") -Destination $PayloadRoot -Force
}

if (-not (Test-Path -LiteralPath (Join-Path $PayloadRoot "publish\desktop\CallBridge.Desktop.exe"))) {
    throw "MSI payload is missing CallBridge.Desktop.exe."
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$directoryXml = New-Object System.Collections.Generic.List[string]
$componentRefs = New-Object System.Collections.Generic.List[string]
Add-DirectoryXml -SourceRoot $PayloadRoot -DirectoryPath $PayloadRoot -DirectoryId "INSTALLFOLDER" -DirectoryXml $directoryXml -ComponentRefs $componentRefs

$wxs = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package
    Name="CallBridge"
    Manufacturer="IT Health Technologies"
    Version="$Version"
    UpgradeCode="{65C7B03D-D3C3-4D98-A80B-0900980898F5}"
    Scope="perMachine"
    Compressed="yes">

    <MajorUpgrade DowngradeErrorMessage="A newer version of CallBridge is already installed." />
    <MediaTemplate EmbedCab="yes" />

    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="CallBridge">
$($directoryXml -join [Environment]::NewLine)
      </Directory>
    </StandardDirectory>

    <StandardDirectory Id="ProgramMenuFolder">
      <Directory Id="ApplicationProgramsFolder" Name="CallBridge" />
    </StandardDirectory>

    <StandardDirectory Id="DesktopFolder" />

    <DirectoryRef Id="ApplicationProgramsFolder">
      <Component Id="StartMenuShortcutComponent" Guid="*">
        <Shortcut Id="StartMenuShortcut" Name="CallBridge" Target="[INSTALLFOLDER]publish\desktop\CallBridge.Desktop.exe" WorkingDirectory="INSTALLFOLDER" />
        <RemoveFolder Id="ApplicationProgramsFolder" On="uninstall" />
        <RegistryValue Root="HKLM" Key="Software\IT Health Technologies\CallBridge" Name="StartMenuShortcut" Type="integer" Value="1" KeyPath="yes" />
      </Component>
    </DirectoryRef>

    <DirectoryRef Id="DesktopFolder">
      <Component Id="DesktopShortcutComponent" Guid="*">
        <Shortcut Id="DesktopShortcut" Name="CallBridge" Target="[INSTALLFOLDER]publish\desktop\CallBridge.Desktop.exe" WorkingDirectory="INSTALLFOLDER" />
        <RegistryValue Root="HKLM" Key="Software\IT Health Technologies\CallBridge" Name="DesktopShortcut" Type="integer" Value="1" KeyPath="yes" />
      </Component>
    </DirectoryRef>

    <Feature Id="MainFeature" Title="CallBridge" Level="1">
$($componentRefs -join [Environment]::NewLine)
      <ComponentRef Id="StartMenuShortcutComponent" />
      <ComponentRef Id="DesktopShortcutComponent" />
    </Feature>
  </Package>
</Wix>
"@

Set-Content -LiteralPath $WixSource -Value $wxs -Encoding UTF8

& $Wix build $WixSource -arch x64 -out $MsiPath
if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed." }

$hash = Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256
Write-Host "MSI: $MsiPath"
Write-Host "SHA256: $($hash.Hash)"
