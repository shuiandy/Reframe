# Reframe installer build script (pure ASCII; runs under Windows PowerShell 5.1 and pwsh 7+).
#
# Pipeline:
#   1. Locate the project root (this script lives in <root>\tools\).
#   2. Read <Version> from Reframe.csproj.
#   3. dotnet publish a framework-dependent (.NET) + WinAppSDK self-contained Release
#      build into publish_out\ - same publish parameters as tools\publish.ps1, so the
#      installer payload is the small framework-dependent build (~50 MB), NOT a bloated
#      self-contained .NET one (~150 MB).
#   4. Compile tools\installer\Reframe.iss with ISCC.exe, passing the version and the
#      absolute publish_out path as defines.
#   5. Result: dist\Reframe-Setup-v<version>-win-x64.exe
#
# Usage (from anywhere):
#   pwsh -File tools\build-installer.ps1
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
#
# Requires: .NET 9 SDK (+ WinAppSDK workload) and Inno Setup 6.3+ (ISCC.exe).

$ErrorActionPreference = 'Stop'

# --- Locate project root (this script lives in <root>\tools\) ---
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectRoot = Split-Path -Parent $ScriptDir
$Csproj      = Join-Path $ProjectRoot 'Reframe.csproj'
$PublishDir  = Join-Path $ProjectRoot 'publish_out'
$DistDir     = Join-Path $ProjectRoot 'dist'
$IssFile     = Join-Path $ScriptDir 'installer\Reframe.iss'

if (-not (Test-Path $Csproj)) {
    throw "Cannot find Reframe.csproj at $Csproj"
}
if (-not (Test-Path $IssFile)) {
    throw "Cannot find installer script at $IssFile"
}

# --- Locate ISCC.exe (PATH first, then known install locations) ---
# winget installs Inno Setup either machine-wide (Program Files (x86)\Inno Setup 6)
# or per-user (%LOCALAPPDATA%\Programs\Inno Setup 6), depending on version/elevation.
$Iscc = (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $Iscc) {
    $IsccCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $IsccCandidates) {
        if (Test-Path $c) { $Iscc = $c; break }
    }
}
if (-not $Iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6 (winget install --id JRSoftware.InnoSetup) or add ISCC.exe to PATH."
}
Write-Host "Using ISCC: $Iscc"

# --- Read <Version> from the csproj ---
[xml]$xml = Get-Content -Path $Csproj
$Version = $null
foreach ($pg in $xml.Project.PropertyGroup) {
    if ($pg.Version) { $Version = [string]$pg.Version; break }
}
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = '0.0.0' }
Write-Host "Reframe version: $Version"

# --- Clean previous publish output (leave dist\ alone except the target setup exe) ---
if (Test-Path $PublishDir) {
    Write-Host "Removing previous publish output: $PublishDir"
    Remove-Item -Path $PublishDir -Recurse -Force
}

# --- Publish: Release, win-x64, x64 platform, WinAppSDK self-contained, .NET framework-dependent ---
#     (Mirrors tools\publish.ps1; --self-contained false keeps the .NET runtime out of the payload.)
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

# Sanity check: the main exe must be in the payload.
$PublishedExe = Join-Path $PublishDir 'Reframe.exe'
if (-not (Test-Path $PublishedExe)) {
    throw "Publish output missing Reframe.exe at $PublishedExe"
}

# --- Ensure dist\ exists ---
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir | Out-Null
}

# --- Compile the installer ---
Write-Host 'Running ISCC ...'
& $Iscc "/DMyAppVersion=$Version" "/DPublishDir=$PublishDir" $IssFile
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE"
}

# --- Report the artifact ---
$SetupName = "Reframe-Setup-v$Version-win-x64.exe"
$SetupPath = Join-Path $DistDir $SetupName
if (-not (Test-Path $SetupPath)) {
    throw "Expected installer not found at $SetupPath"
}
$SetupItem = Get-Item $SetupPath
$SetupMB = [math]::Round($SetupItem.Length / 1MB, 2)
Write-Host ''
Write-Host "Done. Installer: $SetupPath ($SetupMB MB)"
