# Reframe publish script (pure ASCII; runs under Windows PowerShell 5.1 as well as pwsh 7+).
#
# Produces a framework-dependent (.NET) but WinAppSDK self-contained Release build,
# then zips the output to <build root>\dist\Reframe-v<version>-win-x64.zip.
# <build root> is ReframeBuildRoot from Directory.Build.props (outside the repo, by default
# %USERPROFILE%\ClaudeOutputs-local\build\<checkout folder name>\).
#
#   .NET runtime          : framework-dependent (target machine needs .NET 9 Desktop runtime)
#   Windows App SDK        : self-contained     (target machine does NOT need the WinAppSDK runtime)
#
# Usage (from anywhere):
#   pwsh -File tools\publish.ps1
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1
#
# Notes:
#   - Publishes into a dedicated folder (<build root>\publish\) so it never touches bin\Debug
#     and does not disturb a running Reframe instance.
#   - Reads <Version> from Reframe.csproj for the zip name.

$ErrorActionPreference = 'Stop'

# --- Locate project root (this script lives in <root>\tools\) ---
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectRoot = Split-Path -Parent $ScriptDir
$Csproj      = Join-Path $ProjectRoot 'Reframe.csproj'

if (-not (Test-Path $Csproj)) {
    throw "Cannot find Reframe.csproj at $Csproj"
}

# --- Output folders live outside the repo, under ReframeBuildRoot (set in Directory.Build.props) ---
$BuildRoot = (& dotnet msbuild $Csproj -nologo -getProperty:ReframeBuildRoot | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($BuildRoot)) {
    throw "Could not resolve ReframeBuildRoot via dotnet msbuild"
}
$PublishDir  = Join-Path $BuildRoot 'publish'
$DistDir     = Join-Path $BuildRoot 'dist'

# --- Read <Version> from the csproj ---
[xml]$xml = Get-Content -Path $Csproj
$Version = $null
foreach ($pg in $xml.Project.PropertyGroup) {
    if ($pg.Version) { $Version = [string]$pg.Version; break }
}
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = '0.0.0' }
Write-Host "Reframe version: $Version"

# --- Clean previous publish output (leave dist\ alone except the target zip) ---
if (Test-Path $PublishDir) {
    Write-Host "Removing previous publish output: $PublishDir"
    Remove-Item -Path $PublishDir -Recurse -Force
}

# --- Publish: Release, win-x64, x64 platform, WinAppSDK self-contained, .NET framework-dependent ---
Write-Host 'Running dotnet publish ...'
& dotnet publish $Csproj `
    -c Release `
    -r win-x64 `
    -p:Platform=x64 `
    -p:WindowsAppSDKSelfContained=true `
    --self-contained false `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# --- Zip the published output ---
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir | Out-Null
}
$ZipName = "Reframe-v$Version-win-x64.zip"
$ZipPath = Join-Path $DistDir $ZipName
if (Test-Path $ZipPath) {
    Remove-Item -Path $ZipPath -Force
}

Write-Host "Compressing to $ZipPath ..."
Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal

$ZipItem = Get-Item $ZipPath
$ZipMB = [math]::Round($ZipItem.Length / 1MB, 2)
Write-Host ''
Write-Host "Done. Artifact: $ZipPath ($ZipMB MB)"
