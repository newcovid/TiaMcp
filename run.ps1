# Run TiaMcp (Phase 1)
# Prereq: TIA Portal V18 is open with a project loaded.
# First connection: TIA shows a security dialog -> click "Yes / Yes to all" in the TIA window.
# NOTE: ASCII-only on purpose. Windows PowerShell 5.1 on a Chinese locale reads BOM-less
#       UTF-8 scripts as GBK and garbles non-ASCII, which can break parsing / $PSScriptRoot.

$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$exe  = Join-Path $here "bin\Release\net48\TiaMcp.exe"

if (-not (Test-Path $exe)) {
    Write-Host "Not built yet. Run .\build.ps1 first." -ForegroundColor Yellow
    exit 1
}

& $exe
