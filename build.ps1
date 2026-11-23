[CmdletBinding()]
param([string]$ExecutableName = 'DiskDelta.exe')

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'src'
$output = Join-Path $PSScriptRoot 'dist'
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler was not found.'
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $output $ExecutableName
$sources = @(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | ForEach-Object FullName)
$options = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', '/warnaserror+',
    "/out:$exe", "/win32manifest:$source\app.manifest", "/win32icon:$source\app.ico",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.Xml.dll', '/r:System.Xml.Linq.dll', '/r:System.Runtime.Serialization.dll', '/r:Microsoft.CSharp.dll'
)
& $compiler @options @sources
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }
Get-Item -LiteralPath $exe
