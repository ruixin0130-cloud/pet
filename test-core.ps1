$ErrorActionPreference = 'Stop'
$coreProject = $PSScriptRoot
$coreFramework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
if (-not (Test-Path (Join-Path $coreFramework 'csc.exe'))) {
    $coreFramework = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319'
}
$coreCompiler = Join-Path $coreFramework 'csc.exe'
if (-not (Test-Path $coreCompiler)) { throw '.NET Framework 4.x compiler is required.' }
$coreOutput = Join-Path $coreProject 'output/core'
New-Item -ItemType Directory -Force -Path $coreOutput | Out-Null
$coreDll = Join-Path $coreOutput 'Tamago.Core.dll'
$coreExe = Join-Path $coreOutput 'Tamago.Core.Tests.exe'
$coreSources = @(
    'AgentCoreContracts.cs', 'AgentCoreRuntime.cs', 'AgentTools.cs', 'AgentTasks.cs', 'AgentExtensions.cs',
    'AgentDurableContracts.cs', 'AgentDurableService.cs', 'AgentFileWriteTool.cs',
    'AgentMemoryContracts.cs', 'AgentMemoryTools.cs', 'AgentMemoryService.cs', 'AgentScheduleContracts.cs','AgentSchedulerService.cs','AgentAuditArchiveContracts.cs','AgentDataBackupContracts.cs'
) | ForEach-Object { Join-Path $coreProject "src/Core/$_" }
$coreSources += @('JsonAgentTaskStore.cs','JsonAgentMemoryStore.cs','JsonAgentScheduleStore.cs','JsonAgentAuditArchiveStore.cs','AgentStorageLayout.cs','LocalAgentDataBackupStore.cs','LocalAgentFileWriter.cs') | ForEach-Object { Join-Path $coreProject "src/Persistence/$_" }
# Intentionally no WPF, PetEngine, PetPort, legacy adapter, HTTP or asset references.
& $coreCompiler /nologo /target:library /optimize+ /utf8output /codepage:65001 "/out:$coreDll" /reference:System.Web.Extensions.dll @coreSources
if ($LASTEXITCODE -ne 0) { throw 'Core library build failed.' }
& $coreCompiler /nologo /target:exe /optimize+ /utf8output /codepage:65001 "/out:$coreExe" "/reference:$coreDll" "/reference:System.Web.Extensions.dll" (Join-Path $coreProject 'tests/Core/AgentCoreTests.cs') (Join-Path $coreProject 'tests/Core/AgentDurableTests.cs') (Join-Path $coreProject 'tests/Core/AgentMemoryTests.cs') (Join-Path $coreProject 'tests/Core/AgentSchedulerTests.cs') (Join-Path $coreProject 'tests/Core/AgentAuditArchiveTests.cs') (Join-Path $coreProject 'tests/Core/AgentDataBackupTests.cs') (Join-Path $coreProject 'tests/Core/Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'Core tests build failed.' }
$coreLog = Join-Path $coreProject 'output/core-tests.txt'
# Keep filesystem integration data in a fresh OS temporary directory, away from the source checkout.
$coreData = Join-Path ([IO.Path]::GetTempPath()) ('TamagoCoreTests-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $coreData | Out-Null
$coreRun = Start-Process -FilePath $coreExe -ArgumentList ('"'+$coreLog+'" "'+$coreData+'"') -WindowStyle Hidden -PassThru
if (-not $coreRun.WaitForExit(30000)) { Stop-Process -Id $coreRun.Id; throw 'Core tests timed out.' }
if ($coreRun.ExitCode -ne 0) { throw "Core tests failed ($($coreRun.ExitCode)). See output/core-tests.txt." }
Get-Content -LiteralPath $coreLog -Tail 1
