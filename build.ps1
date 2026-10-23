<#
    Builds G5DockSwitch.exe with the C# compiler that ships with Windows.
    No .NET SDK, no Visual Studio, no NuGet required.

    usage:  powershell -ExecutionPolicy Bypass -File build.ps1
#>
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$src  = Join-Path $root 'src\G5DockSwitch.cs'
$dist = Join-Path $root 'dist'
$out  = Join-Path $dist 'G5DockSwitch.exe'

if (-not (Test-Path $src)) { throw "source not found: $src" }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$candidates = @(
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'C# compiler (csc.exe) not found. Windows 10/11 normally ships it under Microsoft.NET\Framework64\v4.0.30319.' }

& $csc /nologo /target:winexe /optimize+ /platform:anycpu /out:"$out" /reference:System.dll "$src"
if ($LASTEXITCODE -ne 0) { throw "compile failed (exit $LASTEXITCODE)" }

$item = Get-Item $out
Write-Host ("built  : {0}" -f $item.FullName)
Write-Host ("size   : {0:N1} KB" -f ($item.Length / 1KB))
Write-Host ("sha256 : {0}" -f (Get-FileHash $out -Algorithm SHA256).Hash)
