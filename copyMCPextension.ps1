# Quick redeploy shortcut — copies already-built dnspy-mcp-extension binaries
# into the dnSpyEx-dnSpy runtime directory without a full rebuild.
# For the full pipeline (refresh lib → build → deploy → build dnSpyEx) use build-all.ps1.

$src = Join-Path $PSScriptRoot "..\dnspy-mcp-extension\bin\Release\net10.0-windows"
$dst = Join-Path $PSScriptRoot "dnSpy\dnSpy\bin\Release\net10.0-windows\Extensions\MalwareMCP"

New-Item -ItemType Directory -Force $dst | Out-Null
robocopy $src $dst /MIR /NFL /NDL /NJH /NJS | Out-Null
Write-Host "Extension DLLs → $dst"

# The extension hosts an ASP.NET Core MCP endpoint inside dnSpy's process.
# dnSpy does not carry Microsoft.AspNetCore.App, so copy the shared framework DLLs.
$aspSharedRoot = "C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App"
if (Test-Path $aspSharedRoot) {
	$aspVersionDir = Get-ChildItem $aspSharedRoot -Directory |
		Where-Object { $_.Name -like "10.0.*" } |
		Sort-Object { [version]$_.Name } -Descending |
		Select-Object -First 1

	if ($aspVersionDir) {
		Copy-Item (Join-Path $aspVersionDir.FullName "*.dll") $dst -Force
		Write-Host "ASP.NET Core $($aspVersionDir.Name) DLLs → $dst"
	}
}