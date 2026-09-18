<#
.SYNOPSIS
    Builds the solution and runs the test suite.

.DESCRIPTION
    `dotnet test` cannot be used here: .NET 10 removed the VSTest bridge, and the
    Microsoft.Testing.Platform host shipped in SDK 10.0.1xx fails to launch
    xunit.v3 4.0.1 test applications at all (it reports "Zero tests ran" without
    ever starting the process). The test app is a self-hosting executable, so this
    script runs it directly, which is what `dotnet test` would have done anyway.

    Revisit once xunit.v3 ships an SDK 10-compatible MTP integration.

.PARAMETER Configuration
    Build configuration. Defaults to Debug.

.EXAMPLE
    ./test.ps1
    ./test.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$root = $PSScriptRoot

Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
dotnet build "$root/BXLogger.Serilog.Sink.slnx" --configuration $Configuration --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Build failed.' -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $root "tests/BXLogger.Serilog.Sink.Tests/bin/$Configuration/net10.0/BXLogger.Serilog.Sink.Tests.exe"

if (-not (Test-Path $exe)) {
    Write-Host "Test executable not found at $exe" -ForegroundColor Red
    exit 1
}

Write-Host ''
& $exe
exit $LASTEXITCODE
