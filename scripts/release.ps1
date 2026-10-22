param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$')]
    [string]$Version = '0.1.0-preview.1',
    [switch]$SkipBuild,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $SkipBuild) { & (Join-Path $root 'build.ps1') }
if (-not $SkipTests) { & (Join-Path $root 'tests\run.ps1') }
& (Join-Path $root 'tests\public-scan.ps1') -Root $root

$executable = Join-Path $root 'bin\FlareIdle.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'build output is missing' }
$stage = Join-Path $root ('build\release-' + [Guid]::NewGuid().ToString('N'))
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $stage,$dist -Force | Out-Null
$documents = @('README.md','LICENSE','THIRD_PARTY_NOTICES.md',
    'docs/compatibility.md','docs/security.md','docs/troubleshooting.md','docs/development.md')
Copy-Item -LiteralPath $executable -Destination (Join-Path $stage 'FlareIdle.exe')
foreach ($name in $documents) {
    $target = Join-Path $stage $name
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $target
}
$archive = Join-Path $dist "flare-idle-$Version-windows-x64.zip"
$contents = @(Get-ChildItem -LiteralPath $stage | ForEach-Object FullName)
Compress-Archive -LiteralPath $contents -DestinationPath $archive -CompressionLevel Optimal -Force
& (Join-Path $root 'tests\public-scan.ps1') -Archive $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($archive + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($archive) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "release ready: $([IO.Path]::GetFileName($archive))"
