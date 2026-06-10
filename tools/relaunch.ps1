# Reframe dev relaunch: kill -> build -> start, all in one elevated session (single UAC).
# ASCII only: elevated runs under Windows PowerShell 5.1 which reads BOM-less files as ANSI.
$ErrorActionPreference = 'Continue'
# Locate project root (this script lives in <root>\tools\); no hardcoded path.
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$proj = Split-Path -Parent $ScriptDir
$exe  = Join-Path $proj 'bin\x64\Debug\net9.0-windows10.0.19041.0\Reframe.exe'
$log  = Join-Path $proj 'tools\relaunch_log.txt'

[System.IO.File]::WriteAllText($log, "START $(Get-Date -Format HH:mm:ss)`r`n")
function W($m){ Add-Content -LiteralPath $log -Value $m }

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
Start-Process -FilePath $exe
Start-Sleep -Seconds 4
$alive = Get-Process -Name Reframe -ErrorAction SilentlyContinue
W ("[launch] alive=" + [bool]$alive + " pid=" + ($alive.Id -join ','))
W 'DONE'
