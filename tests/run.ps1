$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$build=Join-Path $root 'build-temp\tests'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$env:TEMP=$build;$env:TMP=$build
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
function Compile-Run([string]$Name,[string[]]$Sources,[string[]]$Options=@()) {
 $output=Join-Path $build ($Name+'.exe')
 $inputs=@($Sources | ForEach-Object {Join-Path $root $_})
 & $csc /nologo /target:exe /platform:x64 "/out:$output" @Options @inputs
 if($LASTEXITCODE -ne 0){throw "$Name build failed"}
 & $output
 if($LASTEXITCODE -ne 0){throw "$Name failed"}
}
Compile-Run 'GateTests' @('src\WorkGate.cs','tests\Tests.cs')
Compile-Run 'GatewayTests' @('src\Gateway.cs','tests\GatewayTests.cs') @('/define:GATEWAY_TEST_EXE','/reference:System.Net.Http.dll')
Compile-Run 'GpuTests' @('src\Gpu.cs','tests\GpuTests.cs')
Compile-Run 'ConfigTests' @('src\Model.cs','src\LaunchArguments.cs','tests\ConfigTests.cs') @('/reference:System.Web.Extensions.dll','/reference:System.Security.dll')
Compile-Run 'LifecycleTests' @('src\Native.cs','src\Model.cs','src\Gpu.cs','src\WorkGate.cs','src\Gateway.cs','src\LaunchArguments.cs','src\Core.cs','tests\LifecycleTests.cs') @('/define:CORE_WORKER_TEST','/reference:System.Web.Extensions.dll','/reference:System.Security.dll','/reference:System.Net.Http.dll')
