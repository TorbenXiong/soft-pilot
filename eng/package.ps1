[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.0.3',
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
$payload = Join-Path $work 'payload'
$gui = Join-Path $work 'gui'
$cli = Join-Path $work 'cli'
$shim = Join-Path $work 'shim'
$uninstall = Join-Path $work 'uninstall'
$release = Join-Path $artifacts "release\$Version"

if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
if (Test-Path -LiteralPath $release) {
    Get-ChildItem -LiteralPath $release -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $release | Out-Null
}
New-Item -ItemType Directory -Path $payload, $gui, $cli, $shim, $uninstall | Out-Null

& $dotnet publish (Join-Path $repositoryRoot 'src\SoftPilot.Gui\SoftPilot.Gui.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -o $gui
if ($LASTEXITCODE -ne 0) { throw 'SoftPilot.Gui publish failed.' }
& $dotnet publish (Join-Path $repositoryRoot 'src\SoftPilot.Cli\SoftPilot.Cli.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $cli
if ($LASTEXITCODE -ne 0) { throw 'SoftPilot.Cli publish failed.' }
& $dotnet publish (Join-Path $repositoryRoot 'src\SoftPilot.Shim\SoftPilot.Shim.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $shim
if ($LASTEXITCODE -ne 0) { throw 'SoftPilot.Shim publish failed.' }
& $dotnet publish (Join-Path $repositoryRoot 'src\SoftPilot.Uninstall\SoftPilot.Uninstall.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -o $uninstall
if ($LASTEXITCODE -ne 0) { throw 'SoftPilot.Uninstall publish failed.' }

Copy-Item -Path (Join-Path $gui '*') -Destination $payload -Recurse
Copy-Item -LiteralPath (Join-Path $cli 'spt.exe') -Destination (Join-Path $payload 'spt.exe')
Copy-Item -LiteralPath (Join-Path $uninstall 'SoftPilot.Uninstall.exe') -Destination (Join-Path $payload 'SoftPilot.Uninstall.exe')
$shimDirectory = Join-Path $payload 'shims'
New-Item -ItemType Directory -Path $shimDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $shim 'SoftPilot.Shim.exe') `
    -Destination (Join-Path $shimDirectory 'SoftPilot.Shim.exe')

if ($CertificateThumbprint) {
    Get-ChildItem -LiteralPath $payload -Recurse -Filter *.exe -File | ForEach-Object {
        Invoke-CodeSign $_.FullName
    }
}

$manifest = Join-Path $payload 'payload.sha256'
$manifestLines = Get-ChildItem -LiteralPath $payload -File -Recurse |
    Where-Object { $_.FullName -ne $manifest } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($payload.Length).TrimStart('\').Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$hash  $relative"
    }
[IO.File]::WriteAllLines($manifest, $manifestLines, [Text.UTF8Encoding]::new($false))

$archive = Join-Path $work 'SoftPilot-Payload.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive -CompressionLevel Optimal
& $dotnet publish (Join-Path $repositoryRoot 'src\SoftPilot.Setup\SoftPilot.Setup.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -p:PayloadArchive=$archive -p:Version=$Version -o $release
if ($LASTEXITCODE -ne 0) { throw 'SoftPilot.Setup publish failed.' }

$setupExecutable = Join-Path $release 'SoftPilot-Setup.exe'
Invoke-CodeSign $setupExecutable

Get-ChildItem -LiteralPath $release -File |
    Where-Object Name -ne 'SoftPilot-Setup.exe' |
    Remove-Item -Force
if (-not $CertificateThumbprint) {
    Write-Warning 'Created an unsigned development build. Supply -CertificateThumbprint for a signed release.'
}
Write-Host "Created: $setupExecutable"
