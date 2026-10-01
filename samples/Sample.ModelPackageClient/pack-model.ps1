# Packs Sample.Model into ./feed, for Sample.ModelPackageClient to restore as a consumer of a published
# model package would.
#
# The version is derived from what the package is made of: the model DLL, the project and central
# versions its dependencies come from, and this script. The global packages folder never replaces a
# version it already holds, so a fixed version would go on serving the first package ever packed, while
# a version per content adds an entry only when the package actually changes.
param(
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$model = Join-Path $root 'samples/Sample.Model/Sample.Model.csproj'
$feed = Join-Path $PSScriptRoot 'feed'

# GeneratePackageOnBuild=false, as CI passes to every build outside src: a Release build would otherwise
# pack the src projects beneath the model again.
dotnet build $model -c $Configuration -p:GeneratePackageOnBuild=false
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$dll = Join-Path $root "samples/Sample.Model/bin/$Configuration/net10.0/Sample.Model.dll"
$central = Join-Path $root 'samples/Directory.Packages.props'
$hashes = ($dll, $model, $central, $PSCommandPath | ForEach-Object { (Get-FileHash $_ -Algorithm SHA256).Hash }) -join ''
$stream = [System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes($hashes))
$hash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.Substring(0, 12).ToLowerInvariant()

# A prerelease label of letters and digits: one of digits alone may not start with a zero.
$version = "1.0.0-sha$hash"

# Built again rather than packed from the build above: the property also shapes the package's
# dependencies, which pack reads from a restore made with it. The DLL is the same one, builds being
# deterministic, so the version still describes it.
dotnet pack $model -c $Configuration -p:GeneratePackageOnBuild=false -p:SampleModelPackageVersion=$version -o $feed
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$props = @"
<Project>
  <!-- Written by pack-model.ps1: the version of the Sample.Model package beside this file. -->
  <PropertyGroup>
    <SampleModelPackageVersion>$version</SampleModelPackageVersion>
  </PropertyGroup>
</Project>
"@
Set-Content -Path (Join-Path $feed 'Sample.Model.version.props') -Value $props
Write-Host "Packed Sample.Model $version into $feed"
