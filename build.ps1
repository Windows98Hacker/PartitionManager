<#
.SYNOPSIS
    Builds 32-bit (x86), 64-bit (x64), and AnyCPU WinParted binaries using the built-in .NET Framework 4.x C# compiler.
.DESCRIPTION
    Discovers all .cs files under src\ and compiles them directly with csc.exe (C# 5.0 language level),
    eliminating any dependency on Visual Studio, MSBuild, or external NuGet packages.
#>
$ErrorActionPreference = "Stop"

# Resolve project root and ensure the output bin\ directory exists
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$binDir = Join-Path $root "bin"
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

# Locate the built-in .NET Framework 4.x C# compilers (64-bit and 32-bit)
$csc64 = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$csc32 = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if (-not (Test-Path $csc64)) { $csc64 = $csc32 }
if (-not (Test-Path $csc32)) { throw "csc.exe not found under C:\Windows\Microsoft.NET\Framework*\v4.0.30319" }

# Gather every C# source file recursively from src\
$sources = Get-ChildItem -Path (Join-Path $root "src") -Filter "*.cs" -Recurse | ForEach-Object { $_.FullName }

# Core WinForms + LINQ framework assemblies (no external dependencies)
$refs = @(
    "System.dll",
    "System.Core.dll",
    "System.Drawing.dll",
    "System.Windows.Forms.dll"
)

# Compiles a single target platform binary (x86, x64, or anycpu) to bin\<outName>
function Invoke-TargetBuild($compiler, $platform, $outName) {
    $outPath = Join-Path $binDir $outName
    Write-Host "Building $outName (/platform:$platform)..." -ForegroundColor Cyan
    $compilerArgs = @(
        "/nologo",
        "/target:winexe",
        "/optimize+",
        "/platform:$platform",
        "/out:$outPath"
    )
    foreach ($r in $refs) { $compilerArgs += "/reference:$r" }
    $compilerArgs += $sources

    & $compiler @compilerArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed for $outName (exit $LASTEXITCODE)"
    }
    Write-Host "  -> Built $outPath" -ForegroundColor Green
}

Invoke-TargetBuild $csc32 "x86" "WinParted-x86.exe"
Invoke-TargetBuild $csc64 "x64" "WinParted-x64.exe"
Invoke-TargetBuild $csc64 "anycpu" "WinParted.exe"

Write-Host "`nAll 32-bit (x86), 64-bit (x64), and Universal (AnyCPU) builds succeeded!" -ForegroundColor Green
