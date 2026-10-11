# Reframe dev relaunch: kill -> build -> start, all in one elevated session (single UAC).
# ASCII only: elevated runs under Windows PowerShell 5.1 which reads BOM-less files as ANSI.
$ErrorActionPreference = 'Continue'
# Locate project root (this script lives in <root>\tools\); no hardcoded path.
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$proj = Split-Path -Parent $ScriptDir
$log  = Join-Path $proj 'tools\relaunch_log.txt'
# Output lives outside the repo (Directory.Build.props); ask MSBuild where instead of guessing.
$targetDir = (& dotnet msbuild (Join-Path $proj 'Reframe.csproj') -nologo -p:Platform=x64 -getProperty:TargetDir | Out-String).Trim()
$exe  = Join-Path $targetDir 'Reframe.exe'

[System.IO.File]::WriteAllText($log, "START $(Get-Date -Format HH:mm:ss)`r`n")
function W($m){ Add-Content -LiteralPath $log -Value $m }
W ("[exe] " + $exe)
if (-not $targetDir) { W 'ABORT: could not resolve TargetDir via dotnet msbuild'; exit 1 }

# 1) kill running instance (elevated, so this works on the admin process)
$p = Get-Process -Name Reframe -ErrorAction SilentlyContinue
if ($p) {
  $p | Stop-Process -Force
  Start-Sleep -Milliseconds 800
  W ("[kill] stopped PID " + ($p.Id -join ','))
} else { W '[kill] not running' }

# 2) build
Set-Location $proj
$out = & dotnet build -p:Platform=x64 -v:m 2>&1 | Out-String
$ok = $LASTEXITCODE -eq 0
W ("[build] exit=" + $LASTEXITCODE)
if (-not $ok) {
  W ($out.Substring([Math]::Max(0, $out.Length - 3000)))
  W 'ABORT: build failed, not launching'
  exit 1
}

# 3) launch (inherits elevation, no second UAC)
if (-not (Test-Path -LiteralPath $exe)) { W 'ABORT: built exe not found'; exit 1 }
Start-Process -FilePath $exe
Start-Sleep -Seconds 4
$alive = Get-Process -Name Reframe -ErrorAction SilentlyContinue
W ("[launch] alive=" + [bool]$alive + " pid=" + ($alive.Id -join ','))
W 'DONE'
