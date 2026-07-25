param(
	[ValidateSet("all","netframework","net","net-x86","net-x64","net-linux")]
	[string]$buildtfm = 'all',
	[switch]$NoMsbuild
	)
$ErrorActionPreference = 'Stop'

$netframework_tfm = 'net48'
$net_tfm = 'net10.0-windows'
$configuration = 'Release'
$net_baseoutput = "dnSpy\dnSpy\bin\$configuration"
$apphostpatcher_dir = "Build\AppHostPatcher"

#
# The reason we don't use dotnet build is that dotnet build doesn't support COM references yet https://github.com/dnSpy/dnSpy/issues/1053
#

function Build-NetFramework {
	Write-Host 'Building .NET Framework x86 and x64 binaries'

	$outdir = "$net_baseoutput\$netframework_tfm"

	if ($NoMsbuild) {
		dotnet build -v:m -c $configuration
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}
	else {
		msbuild -v:m -m -restore -t:Build -p:Configuration=$configuration
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}

	# move all files to a bin sub dir but keep the exe files
	Rename-Item $outdir bin
	New-Item -ItemType Directory $outdir > $null
	Move-Item $net_baseoutput\bin $outdir
	foreach ($filename in 'dnSpy-x86.exe', 'dnSpy-x86.exe.config', 'dnSpy-x86.pdb',
			 'dnSpy.exe', 'dnSpy.exe.config', 'dnSpy.pdb',
			 'dnSpy.Console.exe', 'dnSpy.Console.exe.config', 'dnSpy.Console.pdb') {
		Move-Item $outdir\bin\$filename $outdir
	}
}

function Build-Net {
    Write-Host 'Building .NET x86 and x64 binaries'

    $outdir = "$net_baseoutput\$net_tfm"

    if ($NoMsbuild) {
        dotnet build -v:m -c $configuration -f $net_tfm
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
    else {
        msbuild -v:m -m -restore -t:Build -p:Configuration=$configuration -p:TargetFramework=$net_tfm
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }

    Write-Host "Patching .NET apphosts"

    # move all files to a bin sub dir but keep the exe apphosts
    Rename-Item $outdir bin
    New-Item -ItemType Directory $outdir > $null
    Move-Item $net_baseoutput\bin $outdir
    foreach ($exe in 'dnSpy.exe', 'dnSpy-x86.exe', 'dnSpy.Console.exe') {
        Move-Item $outdir\bin\$exe $outdir
        & $apphostpatcher_dir\bin\$configuration\$netframework_tfm\AppHostPatcher.exe $outdir\$exe -d bin
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
}

function Build-SelfContainedNet {
	param([string]$arch)

	Write-Host "Building self contained .NET $arch binaries"

	$rid = "win-$arch"
	$outdir = "$net_baseoutput\$net_tfm\$rid"
	$publishDir = "$outdir\publish"

	if ($NoMsbuild) {
		dotnet publish -v:m -c $configuration -f $net_tfm -r $rid --self-contained
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}
	else {
		msbuild -v:m -m -restore -t:Publish -p:Configuration=$configuration -p:TargetFramework=$net_tfm -p:RuntimeIdentifier=$rid -p:SelfContained=True
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}

    Write-Host "Patching self contained .NET $arch apphosts"

	# move all files to a bin sub dir but keep the exe apphosts
	$tmpbin = 'tmpbin'
	Rename-Item $publishDir $tmpbin
	New-Item -ItemType Directory $publishDir > $null
	Move-Item $outdir\$tmpbin $publishDir
	Rename-Item $publishDir\$tmpbin bin
	foreach ($exe in 'dnSpy.exe', 'dnSpy.Console.exe') {
		Move-Item $publishDir\bin\$exe $publishDir
		& $apphostpatcher_dir\bin\$configuration\$netframework_tfm\AppHostPatcher.exe $publishDir\$exe -d bin
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}
}

function Build-NetLinux {
	Write-Host "Building .NET Linux x64 binaries"

	$rid = "linux-x64"
	$outdir = "$net_baseoutput\net10.0-linux"
	$publishDir = "$outdir\publish"
	$absolutePublishDir = $publishDir
	if ($PSScriptRoot) {
		$absolutePublishDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $publishDir))
	} else {
		$absolutePublishDir = [System.IO.Path]::GetFullPath($publishDir)
	}

	if ($NoMsbuild) {
		dotnet publish -v:m -c $configuration -f net10.0 -r $rid -p:SelfContained=false -p:PublishDir="$absolutePublishDir/" dnSpy\dnSpy.Console\dnSpy.Console.csproj
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}
	else {
		msbuild -v:m -m -restore -t:Publish -p:Configuration=$configuration -p:TargetFramework=net10.0 -p:RuntimeIdentifier=$rid -p:SelfContained=false -p:PublishDir="$absolutePublishDir/" dnSpy\dnSpy.Console\dnSpy.Console.csproj
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}

	dotnet build -v:m -c $configuration -f net10.0 Extensions\ILSpy.Decompiler\dnSpy.Decompiler.ILSpy.Core\dnSpy.Decompiler.ILSpy.Core.csproj
	if ($LASTEXITCODE) { exit $LASTEXITCODE }

	Copy-Item -Path Extensions\ILSpy.Decompiler\dnSpy.Decompiler.ILSpy.Core\bin\$configuration\net10.0\*.dll -Destination $absolutePublishDir -Force
}

$buildNetFramework  = $buildtfm -eq 'all' -or $buildtfm -eq 'netframework'
$buildNet           = $buildtfm -eq 'all' -or $buildtfm -eq 'net'
$buildNetX86        = $buildtfm -eq 'all' -or $buildtfm -eq 'net-x86'
$buildNetX64        = $buildtfm -eq 'all' -or $buildtfm -eq 'net-x64'
$buildNetLinux      = $buildtfm -eq 'all' -or $buildtfm -eq 'net-linux'

if ($buildNetX86 -or $buildNetX64 -or $buildNet) {
    Write-Host 'Building AppHostPatcher tool'
	if ($NoMsbuild) {
		dotnet build -v:m -c $configuration -f $netframework_tfm $apphostpatcher_dir\AppHostPatcher.csproj
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}
	else {
		msbuild -v:m -m -restore -t:Build -p:Configuration=$configuration -p:TargetFramework=$netframework_tfm $apphostpatcher_dir\AppHostPatcher.csproj
		if ($LASTEXITCODE) { exit $LASTEXITCODE }
	}
}

if ($buildNetFramework) {
	Build-NetFramework
}

if ($buildNet) {
    Build-Net
}

if ($buildNetX86) {
	Build-SelfContainedNet x86
}

if ($buildNetX64) {
	Build-SelfContainedNet x64
}

if ($buildNetLinux) {
	Build-NetLinux
}
