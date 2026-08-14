[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryRoot 'Cargo.toml'
$targetRoot = if ($env:CARGO_TARGET_DIR) {
    [System.IO.Path]::GetFullPath($env:CARGO_TARGET_DIR)
}
else {
    Join-Path $repositoryRoot 'target'
}
$componentPath = Join-Path $targetRoot 'wasm32-wasip2'
$componentPath = Join-Path $componentPath 'release'
$componentPath = Join-Path $componentPath 'softpilot_lifecycle_fixture.wasm'

& cargo build `
    --manifest-path $manifestPath `
    --package softpilot-lifecycle-fixture `
    --target wasm32-wasip2 `
    --release `
    --locked
if ($LASTEXITCODE -ne 0) {
    throw "Lifecycle Component fixture build failed with exit code $LASTEXITCODE."
}

& cargo run `
    --manifest-path $manifestPath `
    --package softpilot-gui `
    --locked `
    -- `
    --platform-spike $componentPath
if ($LASTEXITCODE -ne 0) {
    throw "Platform spike failed with exit code $LASTEXITCODE."
}

& cargo run `
    --manifest-path $manifestPath `
    --package softpilot-gui `
    --locked `
    -- `
    --window-smoke
if ($LASTEXITCODE -ne 0) {
    throw "Slint window smoke test failed with exit code $LASTEXITCODE."
}
