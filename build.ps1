# Rebuild TinySticky.exe. Usage:
#   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src"
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$jsonPath = Join-Path $root "services.json"
$json = [System.IO.File]::ReadAllText($jsonPath)
$bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
$b64 = [Convert]::ToBase64String($bytes)
$embedded = @"
namespace TinySticky {
    internal static class EmbeddedJson {
        internal const string B64 = "$b64";
        internal static string Text {
            get { return System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(B64)); }
        }
    }
}
"@
$utf8Bom = New-Object System.Text.UTF8Encoding $true
[System.IO.File]::WriteAllText((Join-Path $src "EmbeddedJson.cs"), $embedded, $utf8Bom)

function Find-Csc {
    $fixed = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
    if (Test-Path -LiteralPath $fixed) { return $fixed }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\Roslyn\csc.exe"
        $hit = @($found) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
        if ($hit) { return [string]$hit }
    }
    throw "csc.exe not found. Install Visual Studio 2022 with MSBuild."
}
$csc = Find-Csc
$fx = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
$out = Join-Path $dist "TinySticky.exe"
$manifest = Join-Path $src "app.manifest"
$sources = @(Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName })
$argList = @(
    "/nologo",
    "/target:winexe",
    "/optimize+",
    "/langversion:10",
    "/platform:x64",
    "/codepage:65001",
    "/utf8output",
    "/lib:$fx",
    "/win32manifest:$manifest",
    "/out:$out"
) + $sources

& $csc @argList
if ($LASTEXITCODE -ne 0) { throw "csc exited $LASTEXITCODE" }
Copy-Item $jsonPath (Join-Path $dist "services.json") -Force
$item = Get-Item $out
Write-Output ("built {0} ({1} KB)" -f $item.FullName, [Math]::Round($item.Length / 1KB, 1))
