$ErrorActionPreference = 'Stop'
$petProject = $PSScriptRoot
$petBin = Join-Path $petProject 'bin'
New-Item -ItemType Directory -Force -Path $petBin | Out-Null
$petFramework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
if (-not (Test-Path (Join-Path $petFramework 'csc.exe'))) {
    $petFramework = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319'
}
$petCompiler = Join-Path $petFramework 'csc.exe'
if (-not (Test-Path $petCompiler)) { throw '.NET Framework 4.x is required. Enable it in Windows Features.' }
$petWpf = Join-Path $petFramework 'WPF'
$petOutput = Join-Path $petBin 'Tamago.exe'
$petArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001',
    "/out:$petOutput",
    "/win32manifest:$petProject\src\app.manifest",
    "/resource:$petProject\src\Panel.xaml,Panel.xaml",
    "/resource:$petProject\assets\tamago-sprites.png,tamago-sprites.png",
    "/resource:$petProject\assets\tamago-interactions.png,tamago-interactions.png",
    "/reference:$petWpf\PresentationCore.dll",
    "/reference:$petWpf\PresentationFramework.dll",
    "/reference:$petWpf\WindowsBase.dll",
    '/reference:System.Xaml.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Xml.Linq.dll',
    "$petProject\src\PetEngine.cs",
    "$petProject\src\Interaction.cs",
    "$petProject\src\App.cs",
    "$petProject\src\Tests.cs",
    "$petProject\src\Dialogue.cs"
)
& $petCompiler @petArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
Copy-Item -LiteralPath $petOutput -Destination (Join-Path $petProject ([string][char]0x7389+[char]0x5B50+[char]0x684C+[char]0x5BA0+'.exe')) -Force
Write-Host "Built: $petOutput"
