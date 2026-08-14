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
$componentPath = Join-Path $targetRoot 'wasm32-wasip2\release\softpilot_lifecycle_fixture.wasm'

$variants = @(
    @{
        Feature = $null
        Test = 'component::tests::loads_fixture_descriptor'
    },
    @{
        Feature = 'wasi-imports'
        Test = 'component::tests::rejects_fixture_with_wasi_imports'
    },
    @{
        Feature = 'trap'
        Test = 'component::tests::isolates_fixture_trap'
    },
    @{
        Feature = 'non-terminating'
        Test = 'component::tests::interrupts_non_terminating_fixture'
    },
    @{
        Feature = 'excessive-memory'
        Test = 'component::tests::rejects_fixture_exceeding_memory_limit'
    }
)

$previousComponent = $env:SOFTPILOT_TEST_COMPONENT
try {
    $env:SOFTPILOT_TEST_COMPONENT = $componentPath
    foreach ($variant in $variants) {
        $buildArguments = @(
            'build',
            '--manifest-path', $manifestPath,
            '--package', 'softpilot-lifecycle-fixture',
            '--target', 'wasm32-wasip2',
            '--release',
            '--locked'
        )
        if ($variant.Feature) {
            $buildArguments += @('--features', $variant.Feature)
        }

        & cargo @buildArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Lifecycle Component fixture build failed with exit code $LASTEXITCODE."
        }

        & cargo test `
            --manifest-path $manifestPath `
            --package softpilot-plugin-api `
            --locked `
            $variant.Test `
            -- `
            --ignored `
            --exact
        if ($LASTEXITCODE -ne 0) {
            throw "Lifecycle Component host test failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    $env:SOFTPILOT_TEST_COMPONENT = $previousComponent
}
