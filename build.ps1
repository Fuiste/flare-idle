$ErrorActionPreference='Stop'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not(Test-Path -LiteralPath $compiler)){throw '.net framework 4.8 with its c# compiler is required'}
$framework=Split-Path $compiler
$wpf=Join-Path $framework 'WPF'
$bin=Join-Path $PSScriptRoot 'bin'
$temporary=Join-Path $PSScriptRoot 'build-temp'
New-Item -ItemType Directory -Force -Path $bin,$temporary | Out-Null
$env:TEMP=$temporary;$env:TMP=$temporary
$sources=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
& $compiler /nologo /optimize+ /platform:x64 /target:winexe "/out:$bin\FlareIdle.exe" "/win32manifest:$PSScriptRoot\app.manifest" /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.Security.dll "/reference:$wpf\PresentationFramework.dll" "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\WindowsBase.dll" /reference:System.Xaml.dll @sources
if($LASTEXITCODE -ne 0){throw 'build failed'}
Write-Output 'built bin/FlareIdle.exe'
