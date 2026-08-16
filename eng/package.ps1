[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.0.4',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$CertificateThumbprint,
    [ValidatePattern('^https?://')]
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw ".NET SDK not found at $dotnet"
}

function Get-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidate = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') `
        -Recurse -Filter signtool.exe -File -ErrorAction SilentlyContinue |
        Where-Object FullName -Match '\\x64\\signtool\.exe$' |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $candidate) {
        throw 'signtool.exe was not found. Restore Microsoft.Windows.SDK.BuildTools first.'
    }

    return $candidate
}

function Invoke-CodeSign([string]$Path) {
    if (-not $CertificateThumbprint) { return }
    & $script:signTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "Code signing failed: $Path" }
    & $script:signTool verify /pa /all $Path
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $Path" }
}

$signTool = if ($CertificateThumbprint) { Get-SignTool } else { $null }
$artifacts = Join-Path $repositoryRoot 'artifacts'
$work = Join-Path $artifacts 'package-work'
$cli = Join-Path $work 'cli'
$shim = Join-Path $work 'shim'
$toolsPayload = Join-Path $work 'tools-payload'
$gui = Join-Path $work 'gui'
$release = Join-Path $artifacts "release\$Version"

if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
if (Test-Path -LiteralPath $release) {
    Get-ChildItem -LiteralPath $release -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $release | Out-Null
}
New-Item -ItemType Directory -Path $cli, $shim, $toolsPayload, $gui | Out-Null

& $dotnet publish (Join-Path $repositoryRoot 'src\SoftPilot.Cli\SoftPilot.Cli.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $cli
if ($LASTEXITCODE -ne 0) { throw 'SoftPilot.Cli publish failed.' }
& $dotnet publish (Join-Path $repositoryRoot 'src\SoftPilot.Shim\SoftPilot.Shim.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $shim
if ($LASTEXITCODE -ne 0) { throw 'SoftPilot.Shim publish failed.' }

Copy-Item -LiteralPath (Join-Path $cli 'spt.exe') -Destination (Join-Path $toolsPayload 'spt.exe')
$shimDirectory = Join-Path $toolsPayload 'shims'
New-Item -ItemType Directory -Path $shimDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $shim 'SoftPilot.Shim.exe') -Destination (Join-Path $shimDirectory 'SoftPilot.Shim.exe')

if ($CertificateThumbprint) {
    Invoke-CodeSign (Join-Path $toolsPayload 'spt.exe')
    Invoke-CodeSign (Join-Path $shimDirectory 'SoftPilot.Shim.exe')
}

$toolsManifest = Join-Path $toolsPayload 'tools.sha256'
$manifestLines = Get-ChildItem -LiteralPath $toolsPayload -File -Recurse |
    Where-Object { $_.FullName -ne $toolsManifest } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($toolsPayload.Length).TrimStart('\').Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$hash  $relative"
    }
[IO.File]::WriteAllLines($toolsManifest, $manifestLines, [Text.UTF8Encoding]::new($false))

$toolsArchive = Join-Path $work 'SoftPilot-Tools.zip'
Compress-Archive -Path (Join-Path $toolsPayload '*') -DestinationPath $toolsArchive -CompressionLevel Optimal

& $dotnet publish (Join-Path $repositoryRoot 'src\SoftPilot.Gui\SoftPilot.Gui.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:EnableMsixTooling=true -p:DebugType=None -p:PortableToolsArchive=$toolsArchive -o $gui
if ($LASTEXITCODE -ne 0) { throw 'SoftPilot.Gui single-file publish failed.' }

$releaseExecutable = Join-Path $release 'SoftPilot.exe'
Copy-Item -LiteralPath (Join-Path $gui 'SoftPilot.exe') -Destination $releaseExecutable
Invoke-CodeSign $releaseExecutable

$executableHash = (Get-FileHash -LiteralPath $releaseExecutable -Algorithm SHA256).Hash
[IO.File]::WriteAllText(
    "$releaseExecutable.sha256",
    "$executableHash  SoftPilot.exe`n",
    [Text.UTF8Encoding]::new($false))

if (-not $CertificateThumbprint) {
    Write-Warning 'Created an unsigned development build. Supply -CertificateThumbprint for a signed release.'
}
Write-Host "Created: $releaseExecutable"
