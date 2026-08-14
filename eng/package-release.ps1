[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('windows-x64', 'macos-arm64', 'macos-x64', 'linux-x64')]
    [string] $PlatformId,

    [string] $OutputDirectory,

    [string] $CargoCommand = 'cargo',

    [string] $LinuxDeployPath,

    [string] $LinuxDeploySha256 = 'c20cd71e3a4e3b80c3483cef793cda3f4e990aca14014d23c544ca3ce1270b4d'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Join-Path $repositoryRoot 'target') 'release-spike'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$platformRoot = Join-Path $OutputDirectory $PlatformId
$releaseDirectory = Join-Path $platformRoot 'release'
$validationDirectory = Join-Path $platformRoot 'validation'
$stagingDirectory = Join-Path $platformRoot 'staging'
$manifestPath = Join-Path $repositoryRoot 'Cargo.toml'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string[]] $ArgumentList,

        [string] $Description = $FilePath
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Assert-NativePlatform {
    $os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()

    switch ($PlatformId) {
        'windows-x64' {
            if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
                    [System.Runtime.InteropServices.OSPlatform]::Windows
                ) -or $architecture -ne 'X64') {
                throw "windows-x64 must be packaged on native Windows x64; found $os $architecture."
            }
        }
        'macos-arm64' {
            if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
                    [System.Runtime.InteropServices.OSPlatform]::OSX
                ) -or $architecture -ne 'Arm64') {
                throw "macos-arm64 must be packaged on native macOS ARM64; found $os $architecture."
            }
        }
        'macos-x64' {
            if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
                    [System.Runtime.InteropServices.OSPlatform]::OSX
                ) -or $architecture -ne 'X64') {
                throw "macos-x64 must be packaged on native macOS x64; found $os $architecture."
            }
        }
        'linux-x64' {
            if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
                    [System.Runtime.InteropServices.OSPlatform]::Linux
                ) -or $architecture -ne 'X64') {
                throw "linux-x64 must be packaged on native Linux x64; found $os $architecture."
            }
        }
    }
}

function Reset-PlatformOutput {
    if (Test-Path -LiteralPath $platformRoot) {
        $resolvedParent = [System.IO.Path]::GetFullPath((Split-Path -Parent $platformRoot))
        $resolvedLeaf = Split-Path -Leaf $platformRoot
        if ($resolvedParent -ne $OutputDirectory -or $resolvedLeaf -ne $PlatformId) {
            throw "Refusing to remove unexpected release output path: $platformRoot"
        }
        Remove-Item -LiteralPath $platformRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $releaseDirectory, $validationDirectory, $stagingDirectory |
        Out-Null
}

function Get-TargetRoot {
    if ($env:CARGO_TARGET_DIR) {
        if ([System.IO.Path]::IsPathRooted($env:CARGO_TARGET_DIR)) {
            return [System.IO.Path]::GetFullPath($env:CARGO_TARGET_DIR)
        }
        return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $env:CARGO_TARGET_DIR))
    }
    return Join-Path $repositoryRoot 'target'
}

function Find-DumpBin {
    $command = Get-Command 'dumpbin.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not $programFilesX86) {
        return $null
    }
    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        return $null
    }

    $matches = & $vswhere `
        -latest `
        -products '*' `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe'
    if ($LASTEXITCODE -ne 0) {
        return $null
    }
    return $matches | Select-Object -First 1
}

function Get-DynamicDependencies {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Executable
    )

    switch -Wildcard ($PlatformId) {
        'windows-*' {
            $dumpbin = Find-DumpBin
            if (-not $dumpbin) {
                throw 'dumpbin.exe was not found; Windows dynamic dependencies cannot be recorded.'
            }
            $output = & $dumpbin '/DEPENDENTS' $Executable 2>&1
            $output = @($output | ForEach-Object { $_.ToString().Trim() } |
                Where-Object { $_ -match '(?i)^[a-z0-9_.-]+\.dll$' })
        }
        'macos-*' {
            $output = & '/usr/bin/otool' '-L' $Executable 2>&1
        }
        'linux-*' {
            $output = & '/usr/bin/ldd' $Executable 2>&1
        }
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Dynamic dependency inspection failed with exit code $LASTEXITCODE."
    }

    return @($output | ForEach-Object { $_.ToString().TrimEnd() } | Where-Object { $_ })
}

function Find-LinuxSharedLibrary {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Soname
    )

    $ldconfig = if (Test-Path -LiteralPath '/sbin/ldconfig' -PathType Leaf) {
        '/sbin/ldconfig'
    }
    else {
        $command = Get-Command 'ldconfig' -ErrorAction SilentlyContinue
        if (-not $command) {
            throw "ldconfig was not found while resolving $Soname."
        }
        $command.Source
    }

    $output = & $ldconfig '-p' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "ldconfig failed while resolving $Soname with exit code $LASTEXITCODE."
    }

    $escapedSoname = [regex]::Escape($Soname)
    $candidates = @()
    foreach ($line in $output) {
        if ($line.ToString() -match "^\s*$escapedSoname\s+\([^)]*x86-64[^)]*\)\s+=>\s+(.+?)\s*$") {
            $candidate = $Matches[1]
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $candidates += [System.IO.Path]::GetFullPath($candidate)
            }
        }
    }

    $candidates = @($candidates | Sort-Object -Unique)
    if ($candidates.Count -ne 1) {
        throw "Expected one x86-64 path for $Soname, found $($candidates.Count): $candidates"
    }
    return $candidates[0]
}

function Write-ReleaseMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Payload,

        [Parameter(Mandatory = $true)]
        [string] $Format,

        [Parameter(Mandatory = $true)]
        [string] $DependencyExecutable,

        [string[]] $BundledRuntimeLibraries = @()
    )

    $payloadItem = Get-Item -LiteralPath $Payload
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Payload).Hash.ToLowerInvariant()
    $dependencies = Get-DynamicDependencies -Executable $DependencyExecutable
    $runtimeLinkage = if ($PlatformId -eq 'windows-x64') {
        'Windows CRT statically linked; Windows system DLLs dynamically linked'
    }
    else {
        'Native executable with recorded platform system-library dependencies'
    }
    $metadata = [ordered]@{
        platform = $PlatformId
        format = $Format
        payloadFile = $payloadItem.Name
        sizeBytes = $payloadItem.Length
        sha256 = $hash
        dynamicDependencies = @($dependencies)
        bundledRuntimeLibraries = @($BundledRuntimeLibraries)
        runtimeLinkage = $runtimeLinkage
        rustToolchain = '1.97.1'
        signed = $false
        productionRelease = $false
    }

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText(
        (Join-Path $releaseDirectory 'SHA256SUMS.txt'),
        "$hash  $($payloadItem.Name)`n",
        $utf8NoBom
    )
    [System.IO.File]::WriteAllText(
        (Join-Path $releaseDirectory 'release-metadata.json'),
        (($metadata | ConvertTo-Json -Depth 5) + "`n"),
        $utf8NoBom
    )
}

Assert-NativePlatform
Reset-PlatformOutput

$targetRoot = Get-TargetRoot
Invoke-CheckedCommand -FilePath $CargoCommand -Description 'Lifecycle Component fixture build' -ArgumentList @(
    'build',
    '--manifest-path', $manifestPath,
    '--package', 'softpilot-lifecycle-fixture',
    '--target', 'wasm32-wasip2',
    '--release',
    '--locked'
)
$previousEncodedRustFlags = $env:CARGO_ENCODED_RUSTFLAGS
try {
    if ($PlatformId -eq 'windows-x64') {
        $rustFlagSeparator = [char]0x1f
        $env:CARGO_ENCODED_RUSTFLAGS = @(
            '-Clink-arg=/STACK:8000000',
            '-Ctarget-feature=+crt-static'
        ) -join $rustFlagSeparator
    }
    Invoke-CheckedCommand -FilePath $CargoCommand -Description 'SoftPilot release build' -ArgumentList @(
        'build',
        '--manifest-path', $manifestPath,
        '--package', 'softpilot-gui',
        '--release',
        '--locked'
    )
}
finally {
    $env:CARGO_ENCODED_RUSTFLAGS = $previousEncodedRustFlags
}

$componentSource = Join-Path (Join-Path (Join-Path $targetRoot 'wasm32-wasip2') 'release') `
    'softpilot_lifecycle_fixture.wasm'
if (-not (Test-Path -LiteralPath $componentSource -PathType Leaf)) {
    throw "Built Component fixture was not found at $componentSource."
}
Copy-Item -LiteralPath $componentSource -Destination (
    Join-Path $validationDirectory 'softpilot_lifecycle_fixture.wasm'
)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-release-artifact.ps1') `
    -Destination $validationDirectory

$hostFileName = if ($PlatformId -eq 'windows-x64') { 'softpilot-gui.exe' } else { 'softpilot-gui' }
$hostSource = Join-Path (Join-Path $targetRoot 'release') $hostFileName
if (-not (Test-Path -LiteralPath $hostSource -PathType Leaf)) {
    throw "Built SoftPilot executable was not found at $hostSource."
}

switch ($PlatformId) {
    'windows-x64' {
        $payload = Join-Path $releaseDirectory 'SoftPilot.exe'
        Copy-Item -LiteralPath $hostSource -Destination $payload
        Write-ReleaseMetadata `
            -Payload $payload `
            -Format 'Windows PE x64 single executable' `
            -DependencyExecutable $payload
    }
    { $_ -in @('macos-arm64', 'macos-x64') } {
        $bundle = Join-Path $stagingDirectory 'SoftPilot.app'
        $contents = Join-Path $bundle 'Contents'
        $macOSDirectory = Join-Path $contents 'MacOS'
        New-Item -ItemType Directory -Path $macOSDirectory | Out-Null
        $bundleExecutable = Join-Path $macOSDirectory 'SoftPilot'
        Copy-Item -LiteralPath $hostSource -Destination $bundleExecutable
        Invoke-CheckedCommand -FilePath '/bin/chmod' -ArgumentList @('+x', $bundleExecutable)
        $macOSResources = Join-Path (Join-Path $PSScriptRoot 'release') 'macos'
        Copy-Item -LiteralPath (Join-Path $macOSResources 'Info.plist') `
            -Destination (Join-Path $contents 'Info.plist')

        $archiveName = if ($PlatformId -eq 'macos-arm64') {
            'SoftPilot-macos-arm64.zip'
        }
        else {
            'SoftPilot-macos-x64.zip'
        }
        $payload = Join-Path $releaseDirectory $archiveName
        Invoke-CheckedCommand -FilePath '/usr/bin/ditto' -Description 'macOS app bundle archive' -ArgumentList @(
            '-c', '-k', '--sequesterRsrc', '--keepParent', $bundle, $payload
        )
        Write-ReleaseMetadata `
            -Payload $payload `
            -Format 'macOS SoftPilot.app bundle in ZIP transport' `
            -DependencyExecutable $bundleExecutable
    }
    'linux-x64' {
        if (-not $LinuxDeployPath) {
            throw 'linux-x64 packaging requires -LinuxDeployPath.'
        }
        $LinuxDeployPath = [System.IO.Path]::GetFullPath($LinuxDeployPath)
        if (-not (Test-Path -LiteralPath $LinuxDeployPath -PathType Leaf)) {
            throw "linuxdeploy was not found at $LinuxDeployPath."
        }
        $actualToolHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $LinuxDeployPath).Hash
        if ($actualToolHash -ne $LinuxDeploySha256) {
            throw "linuxdeploy SHA-256 mismatch: expected $LinuxDeploySha256, found $actualToolHash."
        }
        Invoke-CheckedCommand -FilePath '/bin/chmod' -ArgumentList @('+x', $LinuxDeployPath)

        $linuxHost = Join-Path $stagingDirectory 'SoftPilot'
        Copy-Item -LiteralPath $hostSource -Destination $linuxHost
        Invoke-CheckedCommand -FilePath '/bin/chmod' -ArgumentList @('+x', $linuxHost)
        $appDir = Join-Path $stagingDirectory 'SoftPilot.AppDir'
        $linuxResources = Join-Path (Join-Path $PSScriptRoot 'release') 'linux'
        $payload = Join-Path $releaseDirectory 'SoftPilot-x86_64.AppImage'
        $bundledRuntimeLibraries = @(
            'libxkbcommon.so.0',
            'libxkbcommon-x11.so.0'
        )
        $bundledRuntimeLibraryPaths = @($bundledRuntimeLibraries | ForEach-Object {
                Find-LinuxSharedLibrary -Soname $_
            })
        $previousOutput = $env:OUTPUT
        $env:OUTPUT = Split-Path -Leaf $payload

        $linuxDeployArguments = @(
            '--appimage-extract-and-run',
            '--appdir', $appDir,
            '--executable', $linuxHost,
            '--desktop-file', (Join-Path $linuxResources 'softpilot.desktop'),
            '--icon-file', (Join-Path $linuxResources 'softpilot.svg')
        )
        foreach ($libraryPath in $bundledRuntimeLibraryPaths) {
            $linuxDeployArguments += @('--library', $libraryPath)
        }
        $linuxDeployArguments += @('--output', 'appimage')

        Push-Location $releaseDirectory
        try {
            Invoke-CheckedCommand `
                -FilePath $LinuxDeployPath `
                -Description 'linuxdeploy AppImage packaging' `
                -ArgumentList $linuxDeployArguments
        }
        finally {
            Pop-Location
            $env:OUTPUT = $previousOutput
        }

        if (-not (Test-Path -LiteralPath $payload -PathType Leaf)) {
            throw "linuxdeploy did not generate the expected AppImage at $payload."
        }
        Invoke-CheckedCommand -FilePath '/bin/chmod' -ArgumentList @('+x', $payload)

        $dependencyExecutable = Join-Path (Join-Path (Join-Path $appDir 'usr') 'bin') 'SoftPilot'
        if (-not (Test-Path -LiteralPath $dependencyExecutable -PathType Leaf)) {
            throw "linuxdeploy did not install the executable at $dependencyExecutable."
        }
        foreach ($soname in $bundledRuntimeLibraries) {
            $bundled = @(Get-ChildItem -LiteralPath $appDir -Recurse -File -Filter "$soname*")
            if ($bundled.Count -eq 0) {
                throw "linuxdeploy did not bundle the required runtime library $soname."
            }
        }
        Write-ReleaseMetadata `
            -Payload $payload `
            -Format 'Linux x86-64 AppImage' `
            -DependencyExecutable $dependencyExecutable `
            -BundledRuntimeLibraries $bundledRuntimeLibraries
    }
}

Write-Output "Release payload: $payload"
Write-Output "Validation payload: $validationDirectory"
