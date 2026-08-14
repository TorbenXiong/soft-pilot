[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('windows-x64', 'macos-arm64', 'macos-x64', 'linux-x64')]
    [string] $PlatformId,

    [Parameter(Mandatory = $true)]
    [string] $ReleaseDirectory,

    [Parameter(Mandatory = $true)]
    [string] $ValidationDirectory,

    [Parameter(Mandatory = $true)]
    [string] $ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$ReleaseDirectory = [System.IO.Path]::GetFullPath($ReleaseDirectory)
$ValidationDirectory = [System.IO.Path]::GetFullPath($ValidationDirectory)
$ReportPath = [System.IO.Path]::GetFullPath($ReportPath)
$metadataPath = Join-Path $ReleaseDirectory 'release-metadata.json'
$checksumPath = Join-Path $ReleaseDirectory 'SHA256SUMS.txt'
$componentPath = Join-Path $ValidationDirectory 'softpilot_lifecycle_fixture.wasm'

foreach ($requiredPath in @($metadataPath, $checksumPath, $componentPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release verification input is missing: $requiredPath"
    }
}

$metadata = Get-Content -Raw -Encoding utf8 -LiteralPath $metadataPath | ConvertFrom-Json
if ($metadata.platform -ne $PlatformId) {
    throw "Release metadata platform mismatch: expected $PlatformId, found $($metadata.platform)."
}
$payload = Join-Path $ReleaseDirectory $metadata.payloadFile
if (-not (Test-Path -LiteralPath $payload -PathType Leaf)) {
    throw "Release payload is missing: $payload"
}
if (Get-ChildItem -LiteralPath $ReleaseDirectory -Filter '*.wasm' -File) {
    throw 'The main release artifact must not contain Component fixtures.'
}

$checksumLine = (Get-Content -Encoding utf8 -LiteralPath $checksumPath | Select-Object -First 1).Trim()
$checksumParts = $checksumLine -split '\s+', 2
if ($checksumParts.Count -ne 2 -or $checksumParts[1] -ne $metadata.payloadFile) {
    throw "Invalid SHA256SUMS.txt entry: $checksumLine"
}
$actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $payload).Hash.ToLowerInvariant()
if ($actualHash -ne $metadata.sha256 -or $actualHash -ne $checksumParts[0].ToLowerInvariant()) {
    throw "Release SHA-256 mismatch for $($metadata.payloadFile)."
}
$payloadSize = (Get-Item -LiteralPath $payload).Length
if ($payloadSize -ne $metadata.sizeBytes) {
    throw "Release size mismatch: metadata=$($metadata.sizeBytes), actual=$payloadSize."
}
$forbiddenRuntimeDependency = @($metadata.dynamicDependencies) |
    Where-Object { $_ -match '(?i)(python|node\.exe|libnode|jvm|java\.exe|dotnet|mono|libruby|libperl|libphp)' }
if ($forbiddenRuntimeDependency) {
    throw "Release has an unexpected language runtime dependency: $forbiddenRuntimeDependency"
}
if ($PlatformId -eq 'linux-x64') {
    $expectedBundledLibraries = @('libxkbcommon.so.0', 'libxkbcommon-x11.so.0')
    $missingBundledLibraries = @($expectedBundledLibraries | Where-Object {
            $_ -notin @($metadata.bundledRuntimeLibraries)
        })
    if ($missingBundledLibraries.Count -gt 0) {
        throw "Linux release metadata is missing bundled runtime libraries: $missingBundledLibraries"
    }
}

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) (
    "softpilot-release-verify-$([System.Diagnostics.Process]::GetCurrentProcess().Id)-$([guid]::NewGuid().ToString('N'))"
)
$installDirectory = Join-Path $scratch 'install'
$workspaceDirectory = Join-Path $scratch 'workspace'
$runtimeHome = Join-Path $scratch 'no-rust-runtime'
New-Item -ItemType Directory -Path $installDirectory, $workspaceDirectory, $runtimeHome | Out-Null
$workspaceMarker = Join-Path $workspaceDirectory 'workspace-marker.txt'
$markerValue = [guid]::NewGuid().ToString('N')

$previousCargoHome = $env:CARGO_HOME
$previousRustupHome = $env:RUSTUP_HOME
$previousPath = $env:PATH
$previousAppImageExtract = $env:APPIMAGE_EXTRACT_AND_RUN

function Install-ReleasePayload {
    param([switch] $Replace)

    switch ($PlatformId) {
        'windows-x64' {
            $destination = Join-Path $installDirectory 'SoftPilot.exe'
            Copy-Item -LiteralPath $payload -Destination $destination -Force
            return $destination
        }
        { $_ -in @('macos-arm64', 'macos-x64') } {
            $bundle = Join-Path $installDirectory 'SoftPilot.app'
            if ($Replace -and (Test-Path -LiteralPath $bundle)) {
                $resolvedBundle = [System.IO.Path]::GetFullPath($bundle)
                if ((Split-Path -Parent $resolvedBundle) -ne $installDirectory -or
                    (Split-Path -Leaf $resolvedBundle) -ne 'SoftPilot.app') {
                    throw "Refusing to replace unexpected app bundle path: $bundle"
                }
                Remove-Item -LiteralPath $bundle -Recurse -Force
            }
            & '/usr/bin/ditto' '-x' '-k' $payload $installDirectory
            if ($LASTEXITCODE -ne 0) {
                throw "macOS app bundle extraction failed with exit code $LASTEXITCODE."
            }
            return Join-Path (Join-Path (Join-Path $bundle 'Contents') 'MacOS') 'SoftPilot'
        }
        'linux-x64' {
            $destination = Join-Path $installDirectory 'SoftPilot.AppImage'
            Copy-Item -LiteralPath $payload -Destination $destination -Force
            & '/bin/chmod' '+x' $destination
            if ($LASTEXITCODE -ne 0) {
                throw "chmod failed with exit code $LASTEXITCODE."
            }
            return $destination
        }
    }
}

function Invoke-SoftPilot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Executable,

        [Parameter(Mandatory = $true)]
        [string[]] $ArgumentList,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $runningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows
    )
    if ($runningOnWindows) {
        $quotedArguments = @($ArgumentList | ForEach-Object {
                '"' + $_.Replace('"', '\"') + '"'
            })
        $process = Start-Process `
            -FilePath $Executable `
            -ArgumentList $quotedArguments `
            -Wait `
            -PassThru
        $exitCode = $process.ExitCode
    }
    else {
        & $Executable @ArgumentList
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }
}

try {
    $env:CARGO_HOME = $runtimeHome
    $env:RUSTUP_HOME = $runtimeHome
    $pathSeparator = [System.IO.Path]::PathSeparator
    $env:PATH = (($previousPath -split [regex]::Escape([string]$pathSeparator)) |
        Where-Object {
            $_ -and $_ -notmatch '(?i)([\\/]\.cargo[\\/]|[\\/]rustup[\\/]|hostedtoolcache.*rust)'
        }) -join $pathSeparator

    if (Get-Command cargo -ErrorAction SilentlyContinue) {
        throw 'cargo is still available in the clean verification environment.'
    }
    if (Get-Command rustc -ErrorAction SilentlyContinue) {
        throw 'rustc is still available in the clean verification environment.'
    }
    if ($PlatformId -eq 'linux-x64') {
        $env:APPIMAGE_EXTRACT_AND_RUN = '1'
    }

    $executable = Install-ReleasePayload
    Invoke-SoftPilot -Executable $executable -Description 'Release startup probe' -ArgumentList @(
        '--child-probe'
    )
    Invoke-SoftPilot -Executable $executable -Description 'Workspace selection smoke test' -ArgumentList @(
        '--workspace-smoke', $workspaceDirectory
    )
    [System.IO.File]::WriteAllText($workspaceMarker, $markerValue)
    Invoke-SoftPilot -Executable $executable -Description 'Component and platform probe' -ArgumentList @(
        '--platform-spike', $componentPath
    )
    Invoke-SoftPilot -Executable $executable -Description 'Slint window smoke test' -ArgumentList @(
        '--window-smoke'
    )

    $executable = Install-ReleasePayload -Replace
    Invoke-SoftPilot -Executable $executable -Description 'Post-replacement workspace selection smoke test' -ArgumentList @(
        '--workspace-smoke', $workspaceDirectory
    )
    $actualMarker = Get-Content -Raw -Encoding utf8 -LiteralPath $workspaceMarker
    if ($actualMarker -ne $markerValue) {
        throw 'Replacing the main release payload changed the independent workspace marker.'
    }

    $report = [ordered]@{
        platform = $PlatformId
        format = $metadata.format
        payloadFile = $metadata.payloadFile
        sizeBytes = $payloadSize
        sha256 = $actualHash
        dynamicDependencies = @($metadata.dynamicDependencies)
        bundledRuntimeLibraries = @($metadata.bundledRuntimeLibraries)
        runtimeLinkage = $metadata.runtimeLinkage
        cleanRunner = [ordered]@{
            installsRust = $false
            cargoAvailable = $false
            rustcAvailable = $false
            externalLanguageRuntimeDependency = $false
        }
        verification = [ordered]@{
            startup = 'passed'
            slintWindow = 'passed'
            workspaceSelection = 'passed'
            componentDescriptor = 'dev.softpilot.lifecycle-fixture 0.1.0 api 0.1.0'
            childProcess = 'passed'
            crossProcessLock = 'passed'
            directoryLink = 'passed'
            replacementWorkspaceReuse = 'passed'
        }
    }

    $reportDirectory = Split-Path -Parent $ReportPath
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText(
        $ReportPath,
        (($report | ConvertTo-Json -Depth 6) + "`n"),
        $utf8NoBom
    )
    Write-Output "Release verification passed: $PlatformId"
    Write-Output "Report: $ReportPath"
}
finally {
    $env:CARGO_HOME = $previousCargoHome
    $env:RUSTUP_HOME = $previousRustupHome
    $env:PATH = $previousPath
    $env:APPIMAGE_EXTRACT_AND_RUN = $previousAppImageExtract

    $ownedScratch = (Split-Path -Leaf $scratch).StartsWith('softpilot-release-verify-')
    if ($ownedScratch -and (Test-Path -LiteralPath $scratch)) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
