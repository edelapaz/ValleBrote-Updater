$ErrorActionPreference = 'Stop'
$version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain a three-part version.' }

$src = Join-Path $PSScriptRoot 'src'
$dist = Join-Path $PSScriptRoot 'dist'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$metadata = Join-Path $dist 'AssemblyInfo.cs'
@"
using System.Reflection;
[assembly: AssemblyTitle("Valle Brote Updater")]
[assembly: AssemblyProduct("Valle Brote Updater")]
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
[assembly: AssemblyInformationalVersion("$version")]
"@ | Set-Content -LiteralPath $metadata -Encoding ASCII

$output = Join-Path $dist 'ValleBroteUpdater.exe'
$compilerArgs = @(
    '/nologo', '/target:winexe', '/optimize+', "/out:$output",
    "/resource:$(Join-Path $src 'updates.json'),ValleBrote.UpdateSettings",
    "/r:$framework/System.IO.Compression.dll",
    "/r:$framework/System.IO.Compression.FileSystem.dll",
    '/r:System.Web.Extensions.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    (Join-Path $src 'Program.cs'), (Join-Path $src 'UpdateRelease.cs'), $metadata
)
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Updater compilation failed.' }

$info = (Get-Item -LiteralPath $output).VersionInfo
if ($info.ProductName -cne 'Valle Brote Updater' -or $info.ProductVersion -cne $version -or
    $info.FileVersion -cne "$version.0" -or $info.OriginalFilename -cne 'ValleBroteUpdater.exe') {
    throw 'Unexpected updater version metadata.'
}
$test = Start-Process -FilePath $output -ArgumentList '--self-test' -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -Wait
if ($test.ExitCode -ne 0) { throw 'Updater self-test failed. See Logs/updater-test-results.txt.' }
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Logs/updater-test-results.txt') -TotalCount 1
Get-FileHash -LiteralPath $output -Algorithm SHA256
