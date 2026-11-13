$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$binDir = Join-Path $root "bin"
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

$csc64 = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$csc32 = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if (-not (Test-Path $csc64)) { $csc64 = $csc32 }
if (-not (Test-Path $csc32)) { throw "csc.exe not found under C:\Windows\Microsoft.NET\Framework*\v4.0.30319" }

$sources = Get-ChildItem -Path (Join-Path $root "src") -Filter "*.cs" -Recurse | ForEach-Object { $_.FullName }

$refs = @(
    "System.dll",
    "System.Core.dll",
    "System.Drawing.dll",
    "System.Windows.Forms.dll"
)

function Build-Target($compiler, $platform, $outName) {
    $outPath = Join-Path $binDir $outName
    Write-Host "Building $outName (/platform:$platform)..." -ForegroundColor Cyan
    $args = @(
        "/nologo",
        "/target:winexe",
        "/optimize+",
        "/platform:$platform",
        "/out:$outPath"
    )
    foreach ($r in $refs) { $args += "/reference:$r" }
    $args += $sources

    & $compiler @args
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed for $outName (exit $LASTEXITCODE)"
    }
    Write-Host "  -> Built $outPath" -ForegroundColor Green
}

Build-Target $csc32 "x86" "WinParted-x86.exe"
Build-Target $csc64 "x64" "WinParted-x64.exe"
Build-Target $csc64 "anycpu" "WinParted.exe"

Write-Host "`nAll 32-bit (x86), 64-bit (x64), and Universal (AnyCPU) builds succeeded!" -ForegroundColor Green
