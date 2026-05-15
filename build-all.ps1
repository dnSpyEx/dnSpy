#Requires -Version 5.1
<#
.SYNOPSIS
    Full build pipeline for dnSpyEx-dnSpy + dnspy-mcp-extension.

.DESCRIPTION
    1. Refreshes dnspy-mcp-extension\lib\ with the latest contracts from dnSpyEx-dnSpy output.
    2. Builds the standalone dnspy-mcp-extension project.
    3. Deploys the extension binaries + ASP.NET Core 10 DLLs into the dnSpyEx-dnSpy runtime.
    4. Runs a full dnSpyEx-dnSpy build.

.NOTES
    First-time / clean-build: the lib refresh (step 1) requires dnSpy.Contracts.DnSpy.dll,
    dnSpy.Contracts.Logic.dll and dnlib.dll to already exist in the dnSpyEx-dnSpy output
    directory.  Bootstrap by running once manually:
        cd B:\github\dnSpyEx-dnSpy
        dotnet build -c Release
    After that, run this script for all subsequent builds.

.PARAMETER Configuration
    Build configuration. Default: Release.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$dnspyExRoot = $PSScriptRoot                                              # …\dnSpyEx-dnSpy
$mcpExtRoot  = Join-Path (Split-Path $dnspyExRoot) 'dnspy-mcp-extension' # …\dnspy-mcp-extension
$tfm         = 'net10.0-windows'
$dnspyOut    = Join-Path $dnspyExRoot "dnSpy\dnSpy\bin\$Configuration\$tfm"
$libDir      = Join-Path $mcpExtRoot 'lib'
$mcpExtCsproj = Join-Path $mcpExtRoot 'dnSpy.Extension.MalwareMCP.csproj'
$mcpSrc      = Join-Path $mcpExtRoot "bin\$Configuration\$tfm"
$mcpDst      = Join-Path $dnspyOut 'Extensions\MalwareMCP'

function Step([string]$msg) {
    Write-Host ""
    Write-Host "=== $msg ===" -ForegroundColor Cyan
}

function Assert-File([string]$path) {
    if (-not (Test-Path $path)) {
        throw @"
Required file not found: $path

Bootstrap: run the following once to seed the build output, then re-run build-all.ps1:
    cd "$dnspyExRoot"
    dotnet build -c $Configuration
"@
    }
}

# ---------------------------------------------------------------------------
# Step 1 — Refresh dnspy-mcp-extension\lib
# ---------------------------------------------------------------------------
Step "Step 1/4 — Refresh dnspy-mcp-extension\lib"

if (-not (Test-Path $libDir)) {
    New-Item -ItemType Directory -Force $libDir | Out-Null
}

foreach ($dll in 'dnlib.dll', 'dnSpy.Contracts.DnSpy.dll', 'dnSpy.Contracts.Logic.dll') {
    $src = Join-Path $dnspyOut $dll
    Assert-File $src
    Copy-Item $src (Join-Path $libDir $dll) -Force
    Write-Host "  Copied  $dll"
}

# ---------------------------------------------------------------------------
# Step 2 — Build dnspy-mcp-extension
# ---------------------------------------------------------------------------
Step "Step 2/4 — Build dnspy-mcp-extension"

Push-Location $mcpExtRoot
try {
    dotnet build $mcpExtCsproj -c $Configuration --nologo
    if ($LASTEXITCODE) { throw "dnspy-mcp-extension build failed (exit $LASTEXITCODE)" }
}
finally { Pop-Location }

# ---------------------------------------------------------------------------
# Step 3 — Deploy extension to dnSpyEx-dnSpy runtime
# ---------------------------------------------------------------------------
Step "Step 3/4 — Deploy MCP extension"

New-Item -ItemType Directory -Force $mcpDst | Out-Null
robocopy $mcpSrc $mcpDst /MIR /NFL /NDL /NJH /NJS | Out-Null
Write-Host "  Extension DLLs → $mcpDst"

# ASP.NET Core 10 shared framework (not bundled with dnSpy)
$aspRoot = 'C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App'
if (Test-Path $aspRoot) {
    $aspDir = Get-ChildItem $aspRoot -Directory |
        Where-Object { $_.Name -like '10.0.*' } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1
    if ($aspDir) {
        Copy-Item (Join-Path $aspDir.FullName '*.dll') $mcpDst -Force
        Write-Host "  ASP.NET Core $($aspDir.Name) DLLs → $mcpDst"
    }
    else {
        Write-Warning "No 10.0.* ASP.NET Core runtime found under $aspRoot — skipping"
    }
}
else {
    Write-Warning "Microsoft.AspNetCore.App not found at $aspRoot — skipping"
}

# ---------------------------------------------------------------------------
# Step 4 — Full dnSpyEx-dnSpy build
# ---------------------------------------------------------------------------
Step "Step 4/4 — Build dnSpyEx-dnSpy"

Push-Location $dnspyExRoot
try {
    dotnet build -c $Configuration --nologo
    if ($LASTEXITCODE) { throw "dnSpyEx-dnSpy build failed (exit $LASTEXITCODE)" }
}
finally { Pop-Location }

Write-Host ""
Write-Host "Build pipeline complete." -ForegroundColor Green
