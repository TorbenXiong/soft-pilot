[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows) {
    throw 'The Windows platform spike must run on Windows.'
}

& (Join-Path $PSScriptRoot 'test-platform-spike.ps1')
