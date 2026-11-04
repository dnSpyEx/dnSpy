<#
.SYNOPSIS
	The decompiler regression check: decompiles a fixed corpus with dnSpy's C# decompiler and compares the results
	and the output with another commit's (the base).

.DESCRIPTION
	The corpus:
	- Pinned NuGet packages (downloaded from nuget.org, not stored in the repo): old and new C#, async, Span and
	  unsafe code, IL emit, F#, array-heavy crypto code. Every type of each assembly is decompiled and its output saved.
	- Synthetic samples with obfuscator tricks, made from one of them by the corpus runner (long, Unicode and
	  keyword names, invalid dead code, deep nesting, a huge method, missing references, metadata tricks). Only
	  crashes, exceptions and timeouts are compared for them, not the output.

	With -BaseRepo (CI: the pull request's target branch), that checkout's corpus runner decompiles the same corpus
	and the results are compared. It fails (exit code 1) if the head is worse: a crash, an assembly that can't be
	loaded, fewer types decompiled, more exceptions, timeouts or debug info errors. Output that changed is listed in
	summary.md and output.diff, for review: a decompiler update is expected to change some output.

	Without a base (or if the base has no corpus runner) only crashes fail.

	Output (in -Out): summary.md, output.diff (base → head, the types whose output changed), head/ and base/ (each
	run's results.json, report.md and dump/ with each type's output), corpus/ (the assemblies).

.PARAMETER Out
	The output directory (it's emptied, except the downloaded packages)

.PARAMETER BaseRepo
	A checkout of the commit to compare with, e.g. 'git worktree add ../dnspy-base origin/master'

.PARAMETER Jobs
	Assemblies decompiled at the same time

.EXAMPLE
	git worktree add ../dnspy-base origin/master
	pwsh Tests/RegressionCorpus/Run-Corpus.ps1 -BaseRepo ../dnspy-base
#>
param(
	[string]$Out = (Join-Path $PSScriptRoot '../../artifacts/regression'),
	[string]$BaseRepo,
	[int]$Jobs = [Environment]::ProcessorCount
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

# Pinned: a new version would change the output. Add packages here (and keep their licenses permissive).
$Packages = @(
	# C# 3-7, .NET Framework (mscorlib), LINQ, dynamic
	@{ Id = 'Newtonsoft.Json'; Version = '13.0.4'; File = 'lib/net45/Newtonsoft.Json.dll' }
	# Newest C#, .NET 10
	@{ Id = 'Humanizer.Core'; Version = '3.0.1'; File = 'lib/net10.0/Humanizer.dll' }
	@{ Id = 'Spectre.Console'; Version = '0.57.0'; File = 'lib/net10.0/Spectre.Console.dll' }
	# async, ValueTask, generics
	@{ Id = 'Polly.Core'; Version = '8.8.0'; File = 'lib/net8.0/Polly.Core.dll' }
	# IL emit, async ADO.NET
	@{ Id = 'Dapper'; Version = '2.1.79'; File = 'lib/net10.0/Dapper.dll' }
	# Structs, operators, interfaces
	@{ Id = 'NodaTime'; Version = '3.3.5'; File = 'lib/net8.0/NodaTime.dll' }
	# Span, ref structs, unsafe code
	@{ Id = 'CommunityToolkit.HighPerformance'; Version = '8.4.0'; File = 'lib/net8.0/CommunityToolkit.HighPerformance.dll' }
	# Another compiler's IL (F#)
	@{ Id = 'FSharp.Core'; Version = '10.1.402'; File = 'lib/netstandard2.1/FSharp.Core.dll' }
	# Big and array-heavy (crypto, ASN.1): its buffer code once made the collection expression transform overflow the stack
	@{ Id = 'BouncyCastle.Cryptography'; Version = '2.4.0'; File = 'lib/net6.0/BouncyCastle.Cryptography.dll' }
)
# The synthetic samples are made from this one
$SyntheticBase = 'Newtonsoft.Json.dll'

function Write-Step([string]$text) { Write-Host "== $text" -ForegroundColor Cyan }

function Invoke-Checked([string]$exe, [string[]]$arguments) {
	& $exe @arguments
	if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed (exit code $LASTEXITCODE)" }
}

# Downloads the packages (kept in $Out/packages) and copies the assemblies to $dir
function Get-Corpus([string]$dir) {
	Add-Type -AssemblyName System.IO.Compression.FileSystem
	$packagesDir = Join-Path $Out 'packages'
	New-Item -ItemType Directory -Force $packagesDir, $dir | Out-Null
	foreach ($p in $Packages) {
		$id = $p.Id.ToLowerInvariant()
		$nupkg = Join-Path $packagesDir "$id.$($p.Version).nupkg"
		if (-not (Test-Path $nupkg)) {
			Write-Host "Downloading $($p.Id) $($p.Version)"
			Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/$id/$($p.Version)/$id.$($p.Version).nupkg" -OutFile "$nupkg.tmp"
			Move-Item "$nupkg.tmp" $nupkg
		}
		$zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
		try {
			$entry = $zip.Entries | Where-Object { $_.FullName -eq $p.File } | Select-Object -First 1
			if (-not $entry) { throw "$($p.Id) $($p.Version) has no $($p.File)" }
			[System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dir $entry.Name), $true)
		}
		finally { $zip.Dispose() }
	}
}

# Builds a checkout's corpus runner and returns its path, or $null if it doesn't have one
function Build-Runner([string]$repo, [string]$name) {
	$project = Join-Path $repo 'Tests/dnSpy.Decompiler.ILSpy.CorpusRunner/dnSpy.Decompiler.ILSpy.CorpusRunner.csproj'
	if (-not (Test-Path $project)) { return $null }
	Write-Step "Building the $name corpus runner ($repo)"
	$log = Join-Path $Out "build-$name.log"
	& dotnet build $project -c Release -nologo -v:q *> $log
	if ($LASTEXITCODE -ne 0) {
		Get-Content $log -Tail 30 | Write-Host
		throw "The $name corpus runner didn't build (exit code $LASTEXITCODE, $log)"
	}
	$dll = Get-ChildItem (Join-Path $repo 'Tests/dnSpy.Decompiler.ILSpy.CorpusRunner/bin/Release') -Recurse -Filter 'dnSpy.Decompiler.ILSpy.CorpusRunner.dll' |
		Sort-Object LastWriteTime -Descending | Select-Object -First 1
	if (-not $dll) { throw "No corpus runner in $repo after the build" }
	# Older runners need the WPF runtime, which only Windows has (the decompiler doesn't use it)
	$config = [System.IO.Path]::ChangeExtension($dll.FullName, '.runtimeconfig.json')
	if (-not $IsWindows -and (Test-Path $config) -and (Get-Content $config -Raw) -match 'Microsoft\.WindowsDesktop\.App') {
		$json = Get-Content $config -Raw | ConvertFrom-Json
		$netCore = $json.runtimeOptions.frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' } | Select-Object -First 1
		$json.runtimeOptions.PSObject.Properties.Remove('frameworks')
		$json.runtimeOptions | Add-Member -NotePropertyName framework -NotePropertyValue $netCore
		$json | ConvertTo-Json -Depth 10 | Set-Content $config
	}
	return $dll.FullName
}

function Invoke-Corpus([string]$runner, [string]$name, [string]$corpus) {
	Write-Step "Decompiling the corpus ($name)"
	$dir = Join-Path $Out $name
	Invoke-Checked 'dotnet' @($runner, 'run', '--engines', 'dnspy', '--max-types', '100000', '--jobs', $Jobs,
		'--dump', (Join-Path $dir 'corpus/dump'), '--out', (Join-Path $dir 'corpus'), (Join-Path $corpus 'assemblies'))
	Write-Step "Decompiling the synthetic samples ($name)"
	Invoke-Checked 'dotnet' @($runner, 'run', '--engines', 'dnspy', '--max-types', '300', '--jobs', $Jobs,
		'--out', (Join-Path $dir 'synthetic'), (Join-Path $corpus 'synthetic'))
}

# (Relative to the current location, which .NET's current directory might not be)
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
New-Item -ItemType Directory -Force $Out | Out-Null
Get-ChildItem $Out -Exclude 'packages' | Remove-Item -Recurse -Force
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$corpus = Join-Path $Out 'corpus'

$headRunner = Build-Runner $repo 'head'
if (-not $headRunner) { throw "No corpus runner in $repo" }
$baseRunner = $null
if ($BaseRepo) {
	$baseRunner = Build-Runner ($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BaseRepo)) 'base'
	if (-not $baseRunner) { Write-Warning "$BaseRepo has no corpus runner: nothing to compare with" }
}

Write-Step 'Getting the corpus'
Get-Corpus (Join-Path $corpus 'assemblies')
Invoke-Checked 'dotnet' @($headRunner, 'generate', '--out', (Join-Path $corpus 'synthetic'), (Join-Path $corpus "assemblies/$SyntheticBase")) | Out-Null

Invoke-Corpus $headRunner 'head' $corpus
if ($baseRunner) { Invoke-Corpus $baseRunner 'base' $corpus }

Write-Step 'Comparing'
$failed = $false
$summaries = @()
foreach ($set in @(@{ Name = 'corpus'; Title = 'Decompiler regression: corpus' }, @{ Name = 'synthetic'; Title = 'Decompiler regression: synthetic samples (obfuscator tricks)' })) {
	$summary = Join-Path $Out "summary-$($set.Name).md"
	$arguments = @($headRunner, 'compare', '--title', $set.Title, '--head', (Join-Path $Out "head/$($set.Name)"), '--out', $summary)
	if ($baseRunner) { $arguments += @('--base', (Join-Path $Out "base/$($set.Name)")) }
	& dotnet @arguments | Out-Null
	if ($LASTEXITCODE -eq 1) { $failed = $true }
	elseif ($LASTEXITCODE -ne 0) { throw "compare failed (exit code $LASTEXITCODE)" }
	$summaries += Get-Content $summary -Raw
}

if ($baseRunner -and (Test-Path (Join-Path $Out 'base/corpus/dump'))) {
	# Paths relative to $Out: base/corpus/dump/ASSEMBLY/TYPE.cs → head/corpus/dump/ASSEMBLY/TYPE.cs
	Push-Location $Out
	try {
		& git -c core.autocrlf=false diff --no-index --no-color --stat=200 -- base/corpus/dump head/corpus/dump > output-diffstat.txt
		& git -c core.autocrlf=false diff --no-index --no-color -- base/corpus/dump head/corpus/dump > output.diff
		$global:LASTEXITCODE = 0
	}
	finally { Pop-Location }
}

$text = ($summaries -join "`n") + "`n"
Set-Content (Join-Path $Out 'summary.md') $text -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) { Add-Content $env:GITHUB_STEP_SUMMARY $text -Encoding utf8 }
Write-Host $text
if ($failed) {
	Write-Host 'The head is worse than the base: see summary.md' -ForegroundColor Red
	exit 1
}
exit 0
