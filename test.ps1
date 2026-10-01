$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test-core.ps1')
& (Join-Path $PSScriptRoot 'build.ps1') -SkipDistribution
$petExe = Join-Path $PSScriptRoot 'bin/Tamago.exe'
$petTest = Start-Process -FilePath $petExe -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
if (-not $petTest.WaitForExit(30000)) { Stop-Process -Id $petTest.Id; throw 'Engine tests timed out.' }
if ($petTest.ExitCode -ne 0) { throw 'Engine tests failed. See output/engine-tests.txt.' }
$petSmoke = Start-Process -FilePath $petExe -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
if (-not $petSmoke.WaitForExit(30000)) { Stop-Process -Id $petSmoke.Id; throw 'UI smoke test timed out.' }
if ($petSmoke.ExitCode -ne 0) { throw 'UI smoke test failed. See output/smoke-test.txt or bin/smoke-error.txt.' }
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'output/engine-tests.txt') -Tail 1
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'output/smoke-test.txt')

# Run an identical executable in an isolated directory, changing only active-pet.json.
# Never edit the user's selection or the source packages during regression tests.
$packRun = Join-Path $PSScriptRoot 'output/package-smoke'
$packBin = Join-Path $packRun 'bin'
New-Item -ItemType Directory -Force -Path $packBin | Out-Null
Copy-Item -LiteralPath $petExe -Destination (Join-Path $packBin 'Tamago.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'content') -Destination $packBin -Recurse -Force
$packExe = Join-Path $packBin 'Tamago.exe'
$packHash = (Get-FileHash -LiteralPath $packExe).Hash
foreach ($packId in @('test-orb','missing-package')) {
    [IO.File]::WriteAllText((Join-Path $packBin 'content/active-pet.json'),('{"pet":"'+$packId+'"}'))
    $packSmoke = Start-Process -FilePath $packExe -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
    if (-not $packSmoke.WaitForExit(45000)) { Stop-Process -Id $packSmoke.Id; throw "Package smoke timed out: $packId" }
    if ($packSmoke.ExitCode -ne 0) { throw "Package smoke failed: $packId. See output/package-smoke/output/smoke-test.txt" }
    $packLog = Get-Content -LiteralPath (Join-Path $packRun 'output/smoke-test.txt') -Raw
    $expected = if ($packId -eq 'test-orb') {'pack=test-orb; fallback=none'} else {'pack=builtin; fallback='}
    if (-not $packLog.Contains($expected)) { throw "Unexpected selected package: $packId" }
    Copy-Item -LiteralPath (Join-Path $packRun 'output/panel-preview.png') -Destination (Join-Path $PSScriptRoot "output/pack-$packId-panel.png") -Force
    Copy-Item -LiteralPath (Join-Path $packRun 'output/study-complete-pet.png') -Destination (Join-Path $PSScriptRoot "output/pack-$packId-study-complete-pet.png") -Force
    Copy-Item -LiteralPath (Join-Path $packRun 'output/smoke-test.txt') -Destination (Join-Path $PSScriptRoot "output/pack-$packId-smoke.txt") -Force
    Write-Host "PASS same EXE restart: $packId"
}
if ((Get-FileHash -LiteralPath $packExe).Hash -ne $packHash -or $packHash -ne (Get-FileHash -LiteralPath $petExe).Hash) { throw 'Package switching changed the executable.' }
