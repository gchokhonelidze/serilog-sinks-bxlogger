<#
.SYNOPSIS
    Builds, verifies and packs BXLogger.Serilog.Sink, and optionally pushes it.

.DESCRIPTION
    The sink is the only packable project, so `dotnet pack` alone would be correct.
    This script adds the parts that make a package safe to publish:

      * runs the sink's tests first, because a published version is permanent
      * packs Release into artifacts/, one known place instead of three bin/ folders
      * inspects the finished .nupkg and fails on what nuget.org would reject or
        silently accept and then display badly

    Push is deliberately a separate switch. Producing the artifact is repeatable and
    harmless; pushing it to nuget.org cannot be undone, so it never happens by default.

.PARAMETER Configuration
    Build configuration. Defaults to Release -- Debug is accepted for inspecting a
    package locally, and cannot be pushed.

.PARAMETER VersionSuffix
    Appended to the version in the csproj, for prereleases: -VersionSuffix rc.1
    produces 0.1.0-rc.1.

.PARAMETER SkipTests
    Packs without running the tests. For iterating on package metadata only.

.PARAMETER Push
    Pushes the packed .nupkg (and its .snupkg) to -Source.

.PARAMETER Source
    Feed to push to. Defaults to nuget.org.

.PARAMETER ApiKey
    Key for -Source. Falls back to the NUGET_API_KEY environment variable, which is
    the better place for it -- a key typed as an argument lands in shell history.

.EXAMPLE
    ./pack.ps1
    ./pack.ps1 -VersionSuffix rc.1
    ./pack.ps1 -Push                       # uses $env:NUGET_API_KEY
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$VersionSuffix = '',
    [switch]$SkipTests,
    [switch]$Push,
    [string]$Source = 'https://api.nuget.org/v3/index.json',
    [string]$ApiKey = $env:NUGET_API_KEY
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'

$root = $PSScriptRoot
$project = Join-Path $root 'src/BXLogger.Serilog.Sink/BXLogger.Serilog.Sink.csproj'
$artifacts = Join-Path $root 'artifacts'

function Fail($message) {
    Write-Host "  $message" -ForegroundColor Red
    exit 1
}

# --- Test ---------------------------------------------------------------------
# A version number on nuget.org is permanent: it can be unlisted, never replaced.
#
# Only the sink's own test project, built directly rather than through test.ps1: that
# script builds the whole solution, and a locally running BXLogger.Api holds its own
# output open, so packing the sink would fail for a reason that has nothing to do with
# the sink. The sink's tests reach Application at most, never the API.
if (-not $SkipTests) {
    Write-Host 'Testing...' -ForegroundColor Cyan

    $testProject = Join-Path $root 'tests/BXLogger.Serilog.Sink.Tests/BXLogger.Serilog.Sink.Tests.csproj'
    dotnet build $testProject --configuration Debug --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Fail 'Test project failed to build; nothing packed.' }

    # xunit.v3 test projects are self-hosting executables. See test.ps1 for why they
    # are run directly instead of through `dotnet test`.
    $testExe = Join-Path $root 'tests/BXLogger.Serilog.Sink.Tests/bin/Debug/net10.0/BXLogger.Serilog.Sink.Tests.exe'
    if (-not (Test-Path $testExe)) { Fail "Test executable not found at $testExe." }

    & $testExe
    if ($LASTEXITCODE -ne 0) { Fail 'Tests failed; nothing packed.' }
}

# --- Pack ---------------------------------------------------------------------
Write-Host ''
Write-Host "Packing ($Configuration)..." -ForegroundColor Cyan

if (Test-Path $artifacts) {
    # Otherwise a stale .nupkg from an earlier version sits alongside the new one and
    # the push step has no way to tell which was meant.
    Remove-Item "$artifacts/*.nupkg", "$artifacts/*.snupkg" -Force -ErrorAction SilentlyContinue
}

$packArgs = @(
    'pack', $project,
    '--configuration', $Configuration,
    '--output', $artifacts,
    '--nologo', '--verbosity', 'quiet'
)
if ($VersionSuffix) { $packArgs += @("-p:VersionSuffix=$VersionSuffix") }

dotnet @packArgs
if ($LASTEXITCODE -ne 0) { Fail 'Pack failed.' }

$nupkg = Get-ChildItem "$artifacts/*.nupkg" | Select-Object -First 1
if (-not $nupkg) { Fail 'Pack reported success but produced no .nupkg.' }

# --- Verify -------------------------------------------------------------------
Write-Host ''
Write-Host 'Verifying...' -ForegroundColor Cyan

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
try {
    $entries = $zip.Entries.FullName
    $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
    $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
    $nuspec = $reader.ReadToEnd()
    $reader.Dispose()
}
finally { $zip.Dispose() }

$problems = @()

# Every target framework in the csproj has to have produced a lib/ folder. Package
# validation catches asset mismatches between them; this catches one missing outright.
foreach ($tfm in 'net8.0', 'net9.0', 'net10.0') {
    if ($entries -notcontains "lib/$tfm/BXLogger.Serilog.Sink.dll") { $problems += "lib/$tfm is missing." }
    if ($entries -notcontains "lib/$tfm/BXLogger.Serilog.Sink.xml") { $problems += "lib/$tfm has no XML docs." }
}

# nuget.org renders these; a package missing them looks abandoned on the listing page.
if ($entries -notcontains 'README.md') { $problems += 'README.md is not in the package.' }
if ($entries -notcontains 'icon.png')  { $problems += 'icon.png is not in the package.' }
if ($nuspec -notmatch '<license type="expression">') { $problems += 'No license expression.' }

if (-not (Test-Path ($nupkg.FullName -replace '\.nupkg$', '.snupkg'))) {
    $problems += 'No .snupkg was produced.'
}

# NuGet drops <projectUrl> entirely when the value is not a valid URI rather than
# failing the pack, so the absence of the element is the same problem as the
# placeholder still being there: nuget.org would show no project link at all.
$isPlaceholder = ($nuspec -match 'PLACEHOLDER') -or ($nuspec -notmatch '<projectUrl>')
if ($isPlaceholder) {
    Write-Host '  No usable projectUrl. Set BXLoggerProjectUrl in Directory.Build.props.' -ForegroundColor Yellow
}

if ($problems) {
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

$version = ([regex]'<version>(.+?)</version>').Match($nuspec).Groups[1].Value
Write-Host "  $($nupkg.Name)  ($([math]::Round($nupkg.Length / 1KB)) KB)" -ForegroundColor Green
Write-Host "  artifacts/ -> $artifacts"

# --- Push ---------------------------------------------------------------------
if (-not $Push) {
    Write-Host ''
    Write-Host "Not pushed. To publish ${version}:" -ForegroundColor Yellow
    Write-Host '  ./pack.ps1 -Push' -ForegroundColor Yellow
    exit 0
}

if ($Configuration -ne 'Release') { Fail 'Refusing to push a Debug build.' }
if ($isPlaceholder) {
    Fail 'Refusing to push: projectUrl is PLACEHOLDER, so nuget.org would link nowhere.'
}
if (-not $ApiKey) { Fail 'No API key. Pass -ApiKey or set NUGET_API_KEY.' }

Write-Host ''
Write-Host "About to push $version to $Source. This cannot be undone." -ForegroundColor Yellow
$answer = Read-Host "Type the version to confirm"
if ($answer -ne $version) { Fail 'Not confirmed.' }

# --skip-duplicate so a re-run after a partial failure is not itself an error.
# The .snupkg rides along with the .nupkg; pushing it separately double-publishes.
dotnet nuget push $nupkg.FullName --source $Source --api-key $ApiKey --skip-duplicate
if ($LASTEXITCODE -ne 0) { Fail 'Push failed.' }

Write-Host ''
Write-Host "Pushed $version. Indexing on nuget.org takes a few minutes." -ForegroundColor Green
exit 0
