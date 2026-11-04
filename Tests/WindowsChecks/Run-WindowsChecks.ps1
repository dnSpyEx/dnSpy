<#
.SYNOPSIS
	Runs the decompiler checks that need Windows (WPF, MSBuild for WPF/WinForms projects, the GAC, .NET Framework)
	and packs the results into one zip file. Worth a run after a bigger ICSharpCode.Decompiler update.

.DESCRIPTION
	Run it from a "Developer PowerShell for VS" (or any PowerShell with the .NET 10 SDK; MSBuild from Visual Studio is
	used when it's installed) in the repository:

		powershell -ExecutionPolicy Bypass -File Tests\WindowsChecks\Run-WindowsChecks.ps1

	It takes 15-60 minutes, depending on the machine. Nothing outside the repository is changed: dnSpy is started with
	its own settings file, and all output goes to Tests\WindowsChecks\out\<date-time>. Steps (see README.md):

		Build        dnSpy.sln, Debug and Release, .NET Framework 4.8 and .NET 10 (and the test projects)
		Tests        the headless test suite (Tests\dnSpy.Decompiler.ILSpy.Tests) on Windows
		Baml         BAML -> XAML of dnSpy's own assemblies and the WPF framework (Windows resolves all references)
		Corpus       decompiles .NET Framework 4.8 assemblies (mscorlib, WinForms, WPF, ...)
		Samples      a console, WPF, WinForms and .NET Framework app: exported with dnSpy.Console (.NET 10 and
		             .NET Framework builds), the exported projects built with MSBuild and run
		Screenshots  starts dnSpy (.NET 10 and .NET Framework, Debug) on the decompiler views and takes screenshots

	The debugger and the rest of the UI need a person: see the end of summary.md.

.PARAMETER Skip
	Steps to skip, e.g. -Skip Build,Corpus (use -Skip Build only if a full Debug and Release build is already there).

.PARAMETER Only
	Only run these steps, e.g. -Only Samples,Screenshots.
#>
param(
	[string[]]$Skip = @(),
	[string[]]$Only = @()
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Out = Join-Path $PSScriptRoot "out\$Stamp"
New-Item -ItemType Directory -Force $Out | Out-Null
$Summary = New-Object System.Collections.Generic.List[string]
$Started = Get-Date

# Languages (dnSpy's UniqueGuid)
$CSharp = '4162dada-67c3-4de4-a5f3-6552c8353ece'
$ILWithCSharp = 'ca52a515-12ae-4182-bc88-81ed037c3d32'
$ILAst = 'b1f782ff-6947-458c-80cb-d7d652355558'
$TypedIL = 'a06dc5f0-bab0-411e-a735-235a1c49f0ae'
# Settings sections: the C# decompiler's, and the one of the decompiler it replaced (migrated once)
$CSharpSettingsSection = '2e5e8e5f-5b1b-4c61-9c2c-7a2b8e3c4d11'
$LegacyCSharpSettingsSection = '6745457f-254b-4b7b-90f1-f948f0721c3b'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

#region Helpers
function Add-Summary([string]$line) {
	$Summary.Add($line)
	Write-Host $line
}

function Should-Run([string]$step) {
	if ($Only.Count -gt 0) { return $Only -contains $step }
	return -not ($Skip -contains $step)
}

# Runs a step; a failure is recorded and the next step runs
function Invoke-Step([string]$name, [scriptblock]$body) {
	if (-not (Should-Run $name)) {
		Add-Summary "## $name`n`nSkipped.`n"
		return
	}
	Write-Host ''
	Write-Host "===== $name =====" -ForegroundColor Cyan
	Add-Summary "## $name`n"
	$watch = [Diagnostics.Stopwatch]::StartNew()
	try {
		& $body
	}
	catch {
		Add-Summary "**Step failed:** $($_.Exception.Message)"
		$_ | Out-String | Set-Content (Join-Path $Out "$name-exception.txt")
	}
	Add-Summary ("`n_{0}: {1:N0} min_`n" -f $name, $watch.Elapsed.TotalMinutes)
}

# Runs a program, its output goes to a log file. Returns the exit code (-1: timed out).
function Invoke-Logged([string]$exe, [string[]]$arguments, [string]$log, [int]$timeoutMinutes = 60, [string]$workingDirectory = $Repo) {
	$psi = New-Object System.Diagnostics.ProcessStartInfo
	$psi.FileName = $exe
	$psi.Arguments = ($arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
	$psi.WorkingDirectory = $workingDirectory
	$psi.UseShellExecute = $false
	$psi.RedirectStandardOutput = $true
	$psi.RedirectStandardError = $true
	$psi.StandardOutputEncoding = [Text.Encoding]::UTF8
	$psi.StandardErrorEncoding = [Text.Encoding]::UTF8
	"> $exe $($psi.Arguments)" | Set-Content -Encoding UTF8 $log
	$process = [System.Diagnostics.Process]::Start($psi)
	$stdout = $process.StandardOutput.ReadToEndAsync()
	$stderr = $process.StandardError.ReadToEndAsync()
	if (-not $process.WaitForExit($timeoutMinutes * 60 * 1000)) {
		try { $process.Kill() } catch { }
		Add-Content -Encoding UTF8 $log $stdout.Result, $stderr.Result, "TIMEOUT after $timeoutMinutes min"
		return -1
	}
	$process.WaitForExit()
	Add-Content -Encoding UTF8 $log $stdout.Result
	if ($stderr.Result) { Add-Content -Encoding UTF8 $log "--- stderr ---", $stderr.Result }
	return $process.ExitCode
}

function Find-MSBuild {
	$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
	if (Test-Path $vswhere) {
		$path = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
		if ($path -and (Test-Path $path)) { return $path }
	}
	$cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
	if ($cmd) { return $cmd.Source }
	return $null
}

# Builds a project or solution: MSBuild if there is one (dnSpy's COM references need it), else dotnet build
function Invoke-Build([string]$project, [string]$configuration, [string]$log, [string[]]$extra = @()) {
	if ($script:MSBuild) {
		$arguments = @($project, '-restore', '-m', '-nologo', '-v:m', "-p:Configuration=$configuration") + $extra
		return Invoke-Logged $script:MSBuild $arguments $log
	}
	$arguments = @('build', $project, '-c', $configuration, '-nologo', '-v:m') + $extra
	return Invoke-Logged 'dotnet' $arguments $log
}

function Get-BuildErrors([string]$log) {
	if (-not (Test-Path $log)) { return @() }
	return @(Select-String -Path $log -Pattern ': error ' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
}

function Get-RelativePath([string]$path) {
	if ($path.StartsWith($Repo, [StringComparison]::OrdinalIgnoreCase)) { return $path.Substring($Repo.Length).TrimStart('\') }
	return $path
}

# Starts a GUI or console program and returns what it printed (WPF/WinForms apps write to a redirected stdout)
function Get-ProgramOutput([string]$exe, [string[]]$arguments, [int]$timeoutSeconds = 120) {
	$psi = New-Object System.Diagnostics.ProcessStartInfo
	$psi.FileName = $exe
	$psi.Arguments = $arguments -join ' '
	$psi.UseShellExecute = $false
	$psi.RedirectStandardOutput = $true
	$psi.RedirectStandardError = $true
	$psi.WorkingDirectory = Split-Path $exe
	$process = [System.Diagnostics.Process]::Start($psi)
	$stdout = $process.StandardOutput.ReadToEndAsync()
	$stderr = $process.StandardError.ReadToEndAsync()
	if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
		try { $process.Kill() } catch { }
		return "TIMEOUT"
	}
	$text = $stdout.Result
	if ($process.ExitCode -ne 0) { $text += "`nEXIT CODE $($process.ExitCode)`n" + $stderr.Result }
	return $text
}
#endregion

#region Environment
Invoke-Step 'Environment' {
	$info = New-Object System.Collections.Generic.List[string]
	$info.Add("Date: $(Get-Date -Format o)")
	$info.Add("OS: $([Environment]::OSVersion.VersionString) ($((Get-CimInstance Win32_OperatingSystem).Caption))")
	$info.Add("PowerShell: $($PSVersionTable.PSVersion)")
	$info.Add("CPU: $((Get-CimInstance Win32_Processor | Select-Object -First 1).Name), $([Environment]::ProcessorCount) threads")
	$info.Add("RAM: {0:N1} GB" -f ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB))
	$git = Get-Command git -ErrorAction SilentlyContinue
	if ($git) {
		Push-Location $Repo
		try {
			$info.Add("Commit: $(git rev-parse HEAD) ($(git rev-parse --abbrev-ref HEAD))")
			$changes = @(git status --porcelain --untracked-files=no)
			$info.Add("Changed files: $($changes.Count)")
		}
		finally { Pop-Location }
	}
	$script:MSBuild = Find-MSBuild
	$info.Add("MSBuild: $(if ($script:MSBuild) { $script:MSBuild } else { 'not found (dotnet build is used; dnSpy''s COM references may not build)' })")
	$info | Set-Content (Join-Path $Out 'environment.txt')
	dotnet --info | Set-Content (Join-Path $Out 'dotnet-info.txt')
	$sdks = @(dotnet --list-sdks)
	$info.Add("SDKs: $($sdks -join '; ')")
	foreach ($line in $info) { Add-Summary "- $line" }
	if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
		throw 'The .NET 10 SDK is needed (https://dotnet.microsoft.com/download/dotnet/10.0)'
	}
}
if (-not (Get-Variable MSBuild -Scope Script -ErrorAction SilentlyContinue)) { $script:MSBuild = Find-MSBuild }
#endregion

#region Build
Invoke-Step 'Build' {
	$results = @()
	foreach ($configuration in 'Debug', 'Release') {
		$log = Join-Path $Out "build-dnSpy-$configuration.log"
		$code = Invoke-Build (Join-Path $Repo 'dnSpy.sln') $configuration $log
		$errors = @(Get-BuildErrors $log)
		$warnings = @(Select-String -Path $log -Pattern ': warning ' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
		$results += "| dnSpy.sln $configuration | $(if ($code -eq 0) { 'OK' } else { "**FAILED** (exit code $code)" }) | $($errors.Count) | $($warnings.Count) |"
		if ($errors.Count -gt 0) { $errors | Select-Object -First 40 | Set-Content (Join-Path $Out "build-dnSpy-$configuration-errors.txt") }
		if ($warnings.Count -gt 0) { $warnings | Set-Content (Join-Path $Out "build-dnSpy-$configuration-warnings.txt") }
	}
	# The test projects (not in dnSpy.sln)
	foreach ($item in @(
			@{ Project = 'Tests\dnSpy.Decompiler.ILSpy.Tests\dnSpy.Decompiler.ILSpy.Tests.csproj'; Configuration = 'Debug' },
			@{ Project = 'Tests\dnSpy.Decompiler.ILSpy.CorpusRunner\dnSpy.Decompiler.ILSpy.CorpusRunner.csproj'; Configuration = 'Release' },
			@{ Project = 'Tests\DebuggerPlayground\DebuggerPlayground.csproj'; Configuration = 'Debug' },
			@{ Project = 'Tests\DebuggerPlayground\DebuggerPlayground.csproj'; Configuration = 'Release' })) {
		$name = [IO.Path]::GetFileNameWithoutExtension($item.Project)
		$log = Join-Path $Out "build-$name-$($item.Configuration).log"
		$code = Invoke-Logged 'dotnet' @('build', (Join-Path $Repo $item.Project), '-c', $item.Configuration, '-nologo', '-v:m') $log
		$errors = @(Get-BuildErrors $log)
		$results += "| $name $($item.Configuration) | $(if ($code -eq 0) { 'OK' } else { "**FAILED** (exit code $code)" }) | $($errors.Count) | |"
	}
	Add-Summary '| Build | Result | Errors | Warnings |'
	Add-Summary '|---|---|---|---|'
	foreach ($line in $results) { Add-Summary $line }
}
#endregion

#region Tests
Invoke-Step 'Tests' {
	$results = Join-Path $Out 'tests'
	$log = Join-Path $Out 'tests.log'
	$project = Join-Path $Repo 'Tests\dnSpy.Decompiler.ILSpy.Tests\dnSpy.Decompiler.ILSpy.Tests.csproj'
	$code = Invoke-Logged 'dotnet' @('test', $project, '-c', 'Debug', '--no-build', '--logger', 'trx;LogFileName=tests.trx',
		'--logger', 'console;verbosity=normal', '--results-directory', $results) $log 120
	$trx = Get-ChildItem $results -Filter *.trx -Recurse | Select-Object -First 1
	if (-not $trx) {
		Add-Summary "No test results (exit code $code), see tests.log"
		return
	}
	[xml]$xml = Get-Content $trx.FullName
	$counters = $xml.TestRun.ResultSummary.Counters
	Add-Summary "Total $($counters.total), passed $($counters.passed), failed $($counters.failed), not run $($counters.notExecuted)"
	$failed = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' })
	foreach ($result in $failed) {
		$message = ''
		if ($result.Output -and $result.Output.ErrorInfo) { $message = ($result.Output.ErrorInfo.Message -split "`n" | Select-Object -First 6) -join ' / ' }
		Add-Summary "- FAILED ``$($result.testName)``: $message"
	}
}
#endregion

#region BAML
Invoke-Step 'Baml' {
	$project = Join-Path $Repo 'Tests\dnSpy.Decompiler.ILSpy.Tests\dnSpy.Decompiler.ILSpy.Tests.csproj'
	$corpora = New-Object System.Collections.Generic.List[object]
	$dnSpyBin = Join-Path $Repo 'dnSpy\dnSpy\bin\Debug\net10.0-windows'
	if (Test-Path $dnSpyBin) { $corpora.Add(@{ Name = 'dnSpy'; Path = $dnSpyBin }) }
	$desktop = Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App') -Directory -ErrorAction SilentlyContinue |
		Where-Object { $_.Name -like '10.*' } | Sort-Object { [version]($_.Name -replace '-.*', '') } | Select-Object -Last 1
	if ($desktop) { $corpora.Add(@{ Name = 'WindowsDesktop.App'; Path = $desktop.FullName }) }
	# .NET Framework WPF and its themes (in the GAC)
	$netfxWpf = Join-Path ([IO.Path]::GetTempPath()) "dnSpyWindowsChecks-baml-$Stamp"
	New-Item -ItemType Directory -Force $netfxWpf | Out-Null
	$gac = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL'
	foreach ($name in 'PresentationFramework', 'PresentationFramework.Aero', 'PresentationFramework.Aero2', 'PresentationFramework.AeroLite',
		'PresentationFramework.Classic', 'PresentationFramework.Luna', 'PresentationFramework.Royale', 'System.Windows.Controls.Ribbon') {
		$file = Get-ChildItem (Join-Path $gac $name) -Recurse -Filter "$name.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
		if ($file) { Copy-Item $file.FullName $netfxWpf }
	}
	$corpora.Add(@{ Name = 'NETFramework-WPF'; Path = $netfxWpf })

	Add-Summary '| Assemblies | Result |'
	Add-Summary '|---|---|'
	foreach ($corpus in $corpora) {
		$env:BAML_CORPUS = $corpus.Path
		$env:BAML_CORPUS_OUT = Join-Path $Out "baml-$($corpus.Name)"
		$log = Join-Path $Out "baml-$($corpus.Name).log"
		$code = Invoke-Logged 'dotnet' @('test', $project, '-c', 'Debug', '--no-build', '--filter', 'FullyQualifiedName~BamlTests',
			'--logger', 'console;verbosity=detailed') $log 60
		$line = Select-String -Path $log -Pattern 'BAML: ' | Select-Object -Last 1
		$result = if ($line) { $line.Line.Trim() } else { "no result (exit code $code)" }
		Add-Summary "| $($corpus.Name) | $result |"
		$problems = @(Select-String -Path $log -Pattern '\.dll .*\.baml:' | Select-Object -First 15)
		foreach ($problem in $problems) { Add-Summary "|  | $($problem.Line.Trim() -replace '\|', '/') |" }
	}
	Remove-Item Env:\BAML_CORPUS, Env:\BAML_CORPUS_OUT -ErrorAction SilentlyContinue
	Remove-Item -Recurse -Force $netfxWpf -ErrorAction SilentlyContinue
	Add-Summary "`nThe XAML is in baml-*/."
}
#endregion

#region Corpus
Invoke-Step 'Corpus' {
	$runner = Join-Path $Repo 'Tests\dnSpy.Decompiler.ILSpy.CorpusRunner\bin\Release\net10.0-windows\dnSpy.Decompiler.ILSpy.CorpusRunner.dll'
	if (-not (Test-Path $runner)) { throw "Not built: $runner" }
	$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
	$files = @(@('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Data.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
		'System.Runtime.Serialization.dll', 'System.Net.Http.dll', 'Microsoft.CSharp.dll', 'WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll',
		'WPF\WindowsBase.dll') | ForEach-Object { Join-Path $framework $_ } | Where-Object { Test-Path $_ })
	$corpusOut = Join-Path $Out 'corpus-netfx'
	$log = Join-Path $Out 'corpus-netfx.log'
	$arguments = @($runner, 'run', '--out', $corpusOut, '--engines', 'dnspy', '--max-types', '150') + $files
	$code = Invoke-Logged 'dotnet' $arguments $log 120
	$report = Join-Path $corpusOut 'report.md'
	if (Test-Path $report) {
		Add-Summary "$($files.Count) .NET Framework 4.8 assemblies, at most 150 types each (corpus-netfx/report.md):`n"
		Get-Content $report | ForEach-Object { Add-Summary $_ }
	}
	else {
		Add-Summary "No report (exit code $code), see corpus-netfx.log"
	}
}
#endregion

#region Samples
Invoke-Step 'Samples' {
	$samplesOut = Join-Path $Out 'samples'
	New-Item -ItemType Directory -Force $samplesOut | Out-Null
	# The exported projects are built here (outside the repository, so dnSpy's build settings don't apply)
	$work = Join-Path ([IO.Path]::GetTempPath()) "dnSpyWindowsChecks-$Stamp"
	New-Item -ItemType Directory -Force $work | Out-Null
	$consoles = @(
		@{ Name = 'net10'; Exe = Join-Path $Repo 'dnSpy\dnSpy\bin\Release\net10.0-windows\dnSpy.Console.exe' },
		@{ Name = 'net48'; Exe = Join-Path $Repo 'dnSpy\dnSpy\bin\Release\net48\dnSpy.Console.exe' })
	$engines = @(@{ Name = 'csharp'; Guid = $CSharp })
	$samples = @(
		@{ Name = 'ConsoleSample'; Output = 'bin\Release\net10.0\ConsoleSample.exe'; Args = @(); Kinds = @('sdk') },
		@{ Name = 'WpfSample'; Output = 'bin\Release\net10.0-windows\WpfSample.exe'; Args = @('--selftest'); Kinds = @('sdk') },
		@{ Name = 'WinFormsSample'; Output = 'bin\Release\net10.0-windows\WinFormsSample.exe'; Args = @('--selftest'); Kinds = @('sdk') },
		@{ Name = 'NetFxSample'; Output = 'bin\Release\net48\NetFxSample.exe'; Args = @(); Kinds = @('sdk', 'classic') })

	Add-Summary '| Sample | dnSpy.Console | Engine | Project | Export | Build (errors) | Runs the same |'
	Add-Summary '|---|---|---|---|---|---|---|'
	foreach ($sample in $samples) {
		$sampleDir = Join-Path $PSScriptRoot "Samples\$($sample.Name)"
		$log = Join-Path $samplesOut "$($sample.Name)-original-build.log"
		$code = Invoke-Logged 'dotnet' @('build', $sampleDir, '-c', 'Release', '-nologo', '-v:m') $log
		$original = Join-Path $sampleDir $sample.Output
		if ($code -ne 0 -or -not (Test-Path $original)) {
			Add-Summary "| $($sample.Name) | | | | **the sample doesn't build** ($($sample.Name)-original-build.log) | | |"
			continue
		}
		$expected = Get-ProgramOutput $original $sample.Args
		$expected | Set-Content -Encoding UTF8 (Join-Path $samplesOut "$($sample.Name)-original-output.txt")
		$assembly = [IO.Path]::ChangeExtension($original, '.dll')
		if (-not (Test-Path $assembly)) { $assembly = $original }

		foreach ($console in $consoles) {
			if (-not (Test-Path $console.Exe)) {
				Add-Summary "| $($sample.Name) | $($console.Name) | | | **not built**: $(Get-RelativePath $console.Exe) | | |"
				continue
			}
			foreach ($engine in $engines) {
				foreach ($kind in $sample.Kinds) {
					$id = "$($sample.Name)-$($console.Name)-$($engine.Name)-$kind"
					$exportDir = Join-Path $work $id
					$arguments = @('-o', $exportDir, '-l', $engine.Guid)
					if ($kind -eq 'sdk') { $arguments += '--sdk-project' }
					$arguments += $assembly
					$exportLog = Join-Path $samplesOut "$id-export.log"
					$code = Invoke-Logged $console.Exe $arguments $exportLog 15
					$csproj = Get-ChildItem $exportDir -Filter *.csproj -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
					if ($code -ne 0 -or -not $csproj) {
						Add-Summary "| $($sample.Name) | $($console.Name) | $($engine.Name) | $kind | **failed** (exit code $code, $id-export.log) | | |"
						continue
					}
					# Keep the exported code
					Compress-Archive -Path (Join-Path $exportDir '*') -DestinationPath (Join-Path $samplesOut "$id-project.zip") -Force
					$buildLog = Join-Path $samplesOut "$id-build.log"
					if ($kind -eq 'classic') {
						if (-not $script:MSBuild) {
							Add-Summary "| $($sample.Name) | $($console.Name) | $($engine.Name) | $kind | OK | not built (needs MSBuild) | |"
							continue
						}
						$code = Invoke-Logged $script:MSBuild @($csproj.FullName, '-restore', '-nologo', '-v:m', '-p:Configuration=Release') $buildLog 15 $csproj.DirectoryName
					}
					else {
						$code = Invoke-Logged 'dotnet' @('build', $csproj.FullName, '-c', 'Release', '-nologo', '-v:m') $buildLog 15 $csproj.DirectoryName
					}
					$errors = @(Get-BuildErrors $buildLog)
					$build = if ($code -eq 0) { 'OK' } else { "**FAILED** ($($errors.Count))" }
					$same = ''
					if ($code -eq 0) {
						$exe = Get-ChildItem $csproj.DirectoryName -Recurse -Filter ([IO.Path]::GetFileName($original)) |
							Where-Object { $_.FullName -match '\\bin\\' } | Select-Object -First 1
						if ($exe) {
							$actual = Get-ProgramOutput $exe.FullName $sample.Args
							$actual | Set-Content -Encoding UTF8 (Join-Path $samplesOut "$id-output.txt")
							$same = if ($actual -ceq $expected) { 'yes' } else { "**no** ($id-output.txt)" }
						}
						else { $same = 'no exe found' }
					}
					# XAML: the names and event handlers the connection ID pass adds
					if ($sample.Name -eq 'WpfSample') {
						$xaml = Get-ChildItem $exportDir -Filter MainWindow.xaml -Recurse | Select-Object -First 1
						if ($xaml) {
							$text = Get-Content -Raw $xaml.FullName
							# (Regular expressions. The attached event is ButtonBase.Click: Click is declared there.)
							$missing = @('Name="okButton"', 'Click="OkButton_Click"', '(Button|ButtonBase)\.Click="Toolbar_Click"', 'Handler="Item_MouseDoubleClick"',
								'TextChanged="NameBox_TextChanged"', 'Loaded="Window_Loaded"', '<EventSetter') | Where-Object { $text -notmatch $_ }
							if ($text -match 'Unknown connection ID|cannot be parsed') { $missing = @($missing) + 'no connection ID problems' }
							if ($missing) { $same += " XAML is missing: $($missing -join ', ')" }
						}
						else { $same += ' no MainWindow.xaml' }
					}
					Add-Summary "| $($sample.Name) | $($console.Name) | $($engine.Name) | $kind | OK | $build | $same |"
					if ($errors.Count -gt 0) {
						$errors | Select-Object -First 25 | ForEach-Object { $_ -replace [regex]::Escape($work), '' } |
							Set-Content (Join-Path $samplesOut "$id-errors.txt")
					}
				}
			}
		}
	}
	Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
	Add-Summary "`nLogs, the exported projects (*-project.zip) and the program outputs are in samples/."
}
#endregion

#region Screenshots
# Win32 only (no System.Drawing at compile time, which PowerShell 7 can't reference): the window is saved as a .bmp,
# and Convert-ToPng makes a .png of it when System.Drawing can be loaded
$WindowCaptureSource = @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class WindowCapture {
	[StructLayout(LayoutKind.Sequential)]
	struct RECT { public int Left, Top, Right, Bottom; }

	[StructLayout(LayoutKind.Sequential)]
	struct BITMAPINFOHEADER {
		public uint biSize;
		public int biWidth, biHeight;
		public ushort biPlanes, biBitCount;
		public uint biCompression, biSizeImage;
		public int biXPelsPerMeter, biYPelsPerMeter;
		public uint biClrUsed, biClrImportant;
	}

	[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
	[DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
	[DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
	[DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
	[DllImport("user32.dll")] static extern bool SetProcessDPIAware();
	[DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
	[DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
	[DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
	[DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
	[DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
	[DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
	[DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
	[DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, uint rop);
	[DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

	public static void Init() { try { SetProcessDPIAware(); } catch { } }

	public static void Maximize(IntPtr hWnd) {
		ShowWindow(hWnd, 3);
		SetForegroundWindow(hWnd);
	}

	public static bool SaveBmp(IntPtr hWnd, string path) {
		RECT r;
		if (!GetWindowRect(hWnd, out r))
			return false;
		int width = r.Right - r.Left, height = r.Bottom - r.Top;
		if (width <= 0 || height <= 0)
			return false;
		IntPtr screen = GetDC(IntPtr.Zero);
		IntPtr memory = CreateCompatibleDC(screen);
		IntPtr bitmap = CreateCompatibleBitmap(screen, width, height);
		IntPtr old = SelectObject(memory, bitmap);
		// PW_RENDERFULLCONTENT: WPF content. If it fails, copy the screen (the window is in front)
		if (!PrintWindow(hWnd, memory, 2))
			BitBlt(memory, 0, 0, width, height, screen, r.Left, r.Top, 0x00CC0020);
		SelectObject(memory, old);
		var info = new BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = height, biPlanes = 1, biBitCount = 32 };
		var bits = new byte[width * height * 4];
		int lines = GetDIBits(memory, bitmap, 0, (uint)height, bits, ref info, 0);
		DeleteObject(bitmap);
		DeleteDC(memory);
		ReleaseDC(IntPtr.Zero, screen);
		if (lines == 0)
			return false;
		using (var writer = new BinaryWriter(File.Create(path))) {
			// BITMAPFILEHEADER
			writer.Write((ushort)0x4D42);
			writer.Write(14 + 40 + bits.Length);
			writer.Write(0);
			writer.Write(14 + 40);
			// BITMAPINFOHEADER (bottom-up, 32 bpp, BI_RGB)
			writer.Write(40u);
			writer.Write(width);
			writer.Write(height);
			writer.Write((ushort)1);
			writer.Write((ushort)32);
			writer.Write(0u);
			writer.Write((uint)bits.Length);
			writer.Write(0);
			writer.Write(0);
			writer.Write(0u);
			writer.Write(0u);
			writer.Write(bits);
		}
		return true;
	}
}
'@

# Checks the settings file dnSpy saved when it was closed. Returns '' or the problems.
function Test-SavedSettings($scenario, [string]$settings) {
	$isDefault = -not $scenario.Language
	$isLegacy = $scenario.ContainsKey('LegacySettings')
	if (-not $isDefault -and -not $isLegacy) { return '' }
	if (-not (Test-Path $settings)) { return ' **settings not saved**' }
	try { [xml]$xml = Get-Content -Raw $settings }
	catch { return " **settings not read**: $($_.Exception.Message)" }
	$problems = New-Object System.Collections.Generic.List[string]
	$sections = @($xml.SelectNodes('//section'))
	if ($isDefault) {
		# The tab dnSpy saved has the language it used
		$tabLanguages = @($sections | Where-Object { $_.GetAttribute('Language') } | ForEach-Object { $_.GetAttribute('Language') })
		if ($tabLanguages.Count -eq 0) { $problems.Add('no saved tab') }
		elseif ($tabLanguages[0] -ne $CSharp) { $problems.Add("default language $($tabLanguages[0]), expected C# $CSharp") }
	}
	if ($isLegacy) {
		if (@($sections | Where-Object { $_.GetAttribute('_') -eq $LegacyCSharpSettingsSection }).Count -ne 0) { $problems.Add('the old C# settings are still there') }
		$new = @($sections | Where-Object { $_.GetAttribute('_') -eq $CSharpSettingsSection }) | Select-Object -First 1
		if (-not $new) { $problems.Add('no C# settings saved') }
		else {
			if ($new.GetAttribute('ShowXmlDocumentation') -ne 'False') { $problems.Add("ShowXmlDocumentation='$($new.GetAttribute('ShowXmlDocumentation'))', expected False") }
			if ($new.GetAttribute('ShowHiddenMembers') -ne 'True') { $problems.Add("ShowHiddenMembers='$($new.GetAttribute('ShowHiddenMembers'))', expected True") }
			if ($new.HasAttribute('AnonymousMethods')) { $problems.Add('AnonymousMethods was moved but has its default value') }
		}
	}
	if ($problems.Count -eq 0) { return ' (settings OK)' }
	return ' **settings:** ' + ($problems -join '; ')
}

# Returns the .png, or the .bmp if System.Drawing isn't available
function Convert-ToPng([string]$bmp) {
	try {
		Add-Type -AssemblyName System.Drawing
		$png = [IO.Path]::ChangeExtension($bmp, '.png')
		$image = [System.Drawing.Image]::FromFile($bmp)
		try { $image.Save($png, [System.Drawing.Imaging.ImageFormat]::Png) }
		finally { $image.Dispose() }
		Remove-Item $bmp
		return $png
	}
	catch {
		return $bmp
	}
}

Invoke-Step 'Screenshots' {
	if (-not ('WindowCapture' -as [type])) { Add-Type -TypeDefinition $WindowCaptureSource }
	[WindowCapture]::Init()
	$shots = Join-Path $Out 'screenshots'
	New-Item -ItemType Directory -Force $shots | Out-Null
	$consoleSample = Join-Path $PSScriptRoot 'Samples\ConsoleSample\bin\Release\net10.0\ConsoleSample.dll'
	$wpfSample = Join-Path $PSScriptRoot 'Samples\WpfSample\bin\Release\net10.0-windows\WpfSample.dll'
	$playground = Join-Path $Repo 'Tests\DebuggerPlayground\bin\Release\net10.0\DebuggerPlayground.dll'
	foreach ($sample in 'ConsoleSample', 'WpfSample') {
		if (-not (Test-Path (Join-Path $PSScriptRoot "Samples\$sample\bin\Release"))) {
			Invoke-Logged 'dotnet' @('build', (Join-Path $PSScriptRoot "Samples\$sample"), '-c', 'Release', '-nologo', '-v:m') (Join-Path $shots "$sample-build.log") | Out-Null
		}
	}
	$scenarios = @(
		@{ Name = 'csharp-program'; Language = $CSharp; Select = 'T:ConsoleSample.Program'; File = $consoleSample },
		# No --language: a new settings file starts with dnSpy's default language, which must be "C#" (the new engine)
		@{ Name = 'default-language-program'; Language = $null; Select = 'T:ConsoleSample.Program'; File = $consoleSample },
		# The settings the user changed in the old C# decompiler are moved to the new one, once
		@{ Name = 'csharp-migrated-settings'; Language = $CSharp; Select = 'T:ConsoleSample.Counter'; File = $consoleSample; LegacySettings = $true },
		@{ Name = 'csharp-counter'; Language = $CSharp; Select = 'T:ConsoleSample.Counter'; File = $consoleSample },
		@{ Name = 'csharp-collections'; Language = $CSharp; Select = 'M:ConsoleSample.Program.Primes'; File = $consoleSample },
		@{ Name = 'csharp-mainwindow'; Language = $CSharp; Select = 'T:WpfSample.MainWindow'; File = $wpfSample },
		@{ Name = 'csharp-playground'; Language = $CSharp; Select = 'T:DebuggerPlayground.Scenarios'; File = $playground },
		# MethodOnly: the method on its own, not its type (the ILAst stepper only follows single methods)
		@{ Name = 'il-with-csharp'; Language = $ILWithCSharp; Select = 'M:ConsoleSample.Program.Describe(System.Object)'; File = $consoleSample; MethodOnly = $true },
		@{ Name = 'ilast'; Language = $ILAst; Select = 'M:ConsoleSample.Program.Describe(System.Object)'; File = $consoleSample; MethodOnly = $true },
		@{ Name = 'typed-il'; Language = $TypedIL; Select = 'M:ConsoleSample.Program.Describe(System.Object)'; File = $consoleSample; MethodOnly = $true })
	$builds = @(
		@{ Name = 'net10'; Exe = Join-Path $Repo 'dnSpy\dnSpy\bin\Debug\net10.0-windows\dnSpy.exe' },
		@{ Name = 'net48'; Exe = Join-Path $Repo 'dnSpy\dnSpy\bin\Debug\net48\dnSpy.exe' })

	Add-Summary '| dnSpy (Debug) | View | Result |'
	Add-Summary '|---|---|---|'
	foreach ($build in $builds) {
		if (-not (Test-Path $build.Exe)) {
			Add-Summary "| $($build.Name) | | **not built**: $(Get-RelativePath $build.Exe) |"
			continue
		}
		foreach ($scenario in $scenarios) {
			if (-not (Test-Path $scenario.File)) {
				Add-Summary "| $($build.Name) | $($scenario.Name) | missing $($scenario.File) |"
				continue
			}
			$settings = Join-Path $shots "settings-$($build.Name).xml"
			Remove-Item $settings -ErrorAction SilentlyContinue
			if ($scenario.ContainsKey('MethodOnly')) {
				# Options -> General -> "Decompile full type" off (DocumentTabServiceSettings)
				@('<?xml version="1.0" encoding="utf-8"?>', '<settings>',
					'  <section _="1ace15fd-d689-40dc-b1e7-6eec25b3116f" RestoreTabs="True" DecompileFullType="False" />',
					'</settings>') | Set-Content -Encoding UTF8 $settings
			}
			if ($scenario.ContainsKey('LegacySettings')) {
				# Changed in the old C# decompiler: no XML documentation, show hidden members. AnonymousMethods has its
				# default value, so it isn't moved.
				@('<?xml version="1.0" encoding="utf-8"?>', '<settings>',
					"  <section _=`"$LegacyCSharpSettingsSection`" ShowXmlDocumentation=`"False`" ForceShowAllMembers=`"True`" AnonymousMethods=`"True`" />",
					'</settings>') | Set-Content -Encoding UTF8 $settings
			}
			$arguments = @('--multiple', '--settings-file', "`"$settings`"")
			if ($scenario.Language) { $arguments += @('--language', $scenario.Language) }
			$arguments += @('--select', "`"$($scenario.Select)`"", "`"$($scenario.File)`"")
			$process = Start-Process -FilePath $build.Exe -ArgumentList $arguments -PassThru
			$deadline = (Get-Date).AddSeconds(90)
			while ($process.MainWindowHandle -eq [IntPtr]::Zero -and -not $process.HasExited -and (Get-Date) -lt $deadline) {
				Start-Sleep -Milliseconds 500
				$process.Refresh()
			}
			if ($process.HasExited) {
				Add-Summary "| $($build.Name) | $($scenario.Name) | **dnSpy exited** (exit code $($process.ExitCode)) |"
				continue
			}
			if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
				Add-Summary "| $($build.Name) | $($scenario.Name) | **no window after 90 s** |"
				try { $process.Kill() } catch { }
				continue
			}
			[WindowCapture]::Maximize($process.MainWindowHandle)
			# Loading, decompiling, and the ILAst stepper window
			Start-Sleep -Seconds 12
			$process.Refresh()
			$file = Join-Path $shots "$($build.Name)-$($scenario.Name).bmp"
			$saved = $false
			if (-not $process.HasExited) { $saved = [WindowCapture]::SaveBmp($process.MainWindowHandle, $file) }
			if ($saved) { $file = Convert-ToPng $file }
			$status = if ($process.HasExited) { "**dnSpy exited** (exit code $($process.ExitCode))" } elseif ($saved) { "screenshots/$(Split-Path $file -Leaf)" } else { 'no screenshot' }
			if (-not $process.HasExited) {
				# dnSpy saves its settings when it's closed
				$process.CloseMainWindow() | Out-Null
				if (-not $process.WaitForExit(15000)) { try { $process.Kill() } catch { } }
			}
			$status += Test-SavedSettings $scenario $settings
			Add-Summary "| $($build.Name) | $($scenario.Name) | $status |"
			if ($scenario.ContainsKey('LegacySettings') -or -not $scenario.Language) {
				Copy-Item $settings (Join-Path $shots "settings-$($build.Name)-$($scenario.Name).xml") -ErrorAction SilentlyContinue
			}
		}
	}
}
#endregion

#region Summary
$Summary.Insert(0, "# Windows checks ($Stamp)`n")
Add-Summary "## Still manual`n"
Add-Summary '- The debugger: `Tests/DebuggerPlayground/README.md` (DebuggerPlayground is built, Debug and Release).'
Add-Summary '- The UI: the code view (tooltips, links, highlighting), the edit dialogs, the Export to Project dialog, the ILAst stepper.'
Add-Summary ("`nTotal time: {0:N0} min" -f ((Get-Date) - $Started).TotalMinutes)
$summaryFile = Join-Path $Out 'summary.md'
$Summary | Set-Content -Encoding UTF8 $summaryFile

$zip = Join-Path $PSScriptRoot "out\WindowsChecks-$Stamp.zip"
Compress-Archive -Path (Join-Path $Out '*') -DestinationPath $zip -Force
Write-Host ''
Write-Host "Summary: $summaryFile" -ForegroundColor Green
Write-Host "Results: $zip" -ForegroundColor Green
#endregion
