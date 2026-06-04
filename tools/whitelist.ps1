# Pre-authorize an Openness client exe in TIA's whitelist, to avoid the per-launch
# "an application wants to access TIA Portal" confirmation dialog.
# TIA matches by FileHash == base64(SHA256(exe)). Writing the whitelist needs admin (HKLM).
# Run as administrator after each build (or once after install).
# ASCII-only on purpose (Windows PowerShell 5.1 + Chinese locale would garble non-ASCII).
param(
  [string]$Exe = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Definition) "..\bin\Release\net48\TiaMcp.exe")
)
$ErrorActionPreference = "Stop"

if (-not (Test-Path $Exe)) { Write-Host "exe not found: $Exe (build first)" -ForegroundColor Red; exit 1 }
$Exe  = (Resolve-Path $Exe).Path
$name = Split-Path -Leaf $Exe

$id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object System.Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Write-Host "Need administrator (writes HKLM whitelist). Re-run as admin." -ForegroundColor Red
  exit 1
}

$bytes = [System.IO.File]::ReadAllBytes($Exe)
$b64   = [Convert]::ToBase64String([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes))
$mtime = (Get-Item $Exe).LastWriteTimeUtc.ToString("yyyy/MM/dd HH:mm:ss.fff")

$base = "HKLM:\SOFTWARE\Siemens\Automation\Openness\18.0\Whitelist\$name"
if (-not (Test-Path $base)) { New-Item -Path $base -Force | Out-Null }

$exists = $false
Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
  $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
  if ($p.FileHash -eq $b64) { $exists = $true }
}
if ($exists) { Write-Host "Already whitelisted (hash=$b64)." -ForegroundColor Green; exit 0 }

$entry = Join-Path $base "Entry"
$i = 1
while (Test-Path $entry) { $entry = Join-Path $base ("Entry (" + $i + ")"); $i++ }
New-Item -Path $entry -Force | Out-Null
New-ItemProperty -Path $entry -Name "Path"         -Value $Exe   -PropertyType String -Force | Out-Null
New-ItemProperty -Path $entry -Name "FileHash"     -Value $b64   -PropertyType String -Force | Out-Null
New-ItemProperty -Path $entry -Name "DateModified" -Value $mtime -PropertyType String -Force | Out-Null

Write-Host "Whitelisted: $entry" -ForegroundColor Green
Write-Host "  FileHash = $b64"
