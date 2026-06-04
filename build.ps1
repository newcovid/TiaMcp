# Build TiaMcp (Phase 1)
# This machine's PATH points to an x86 dotnet WITHOUT an SDK -> "No SDKs were found".
# So we call the x64 .NET SDK by full path (C:\Program Files\dotnet).
# NOTE: ASCII-only on purpose (see run.ps1 for why).
$ErrorActionPreference = "Stop"

$here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$proj   = Join-Path $here "TiaMcp.csproj"

if (-not (Test-Path $dotnet)) {
    Write-Host "x64 dotnet not found: $dotnet" -ForegroundColor Red
    exit 1
}

Write-Host "Building with x64 .NET SDK: $dotnet" -ForegroundColor Cyan
& $dotnet build $proj -c Release
