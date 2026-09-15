$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$petExe = Join-Path $PSScriptRoot 'bin/Tamago.exe'
$petTest = Start-Process -FilePath $petExe -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
if (-not $petTest.WaitForExit(30000)) { Stop-Process -Id $petTest.Id; throw 'Engine tests timed out.' }
if ($petTest.ExitCode -ne 0) { throw 'Engine tests failed. See output/engine-tests.txt.' }
$petSmoke = Start-Process -FilePath $petExe -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
if (-not $petSmoke.WaitForExit(30000)) { Stop-Process -Id $petSmoke.Id; throw 'UI smoke test timed out.' }
if ($petSmoke.ExitCode -ne 0) { throw 'UI smoke test failed. See output/smoke-test.txt or bin/smoke-error.txt.' }
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'output/engine-tests.txt') -Tail 1
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'output/smoke-test.txt')
