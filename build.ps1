<#
.SYNOPSIS
    Builds Windows XP 32-bit compatible (CLR v2.0 / .NET 3.5) and 64-bit / AnyCPU WinParted binaries.
.DESCRIPTION
    Discovers all .cs files under src\ and compiles them directly with csc.exe (C# 5.0 language level),
    eliminating any dependency on Visual Studio, MSBuild, or external NuGet packages.
    WinParted-XP-x86.exe and WinParted-x86.exe are linked strictly against .NET Framework 2.0/3.5
    assemblies (/noconfig /nostdlib /platform:x86) for native execution on Windows XP 32-bit SP2/SP3,
    with src\app.config copied alongside each executable for dual CLR 2.0 / CLR 4.0 support.
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

# Locate .NET Framework 2.0/3.5 reference assemblies for Windows XP 32-bit compatibility
$fx20Dir = "C:\Windows\Microsoft.NET\Framework\v2.0.50727"
$sysCoreCandidates = @(
    "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\v3.5\System.Core.dll",
    "C:\Program Files\Reference Assemblies\Microsoft\Framework\v3.5\System.Core.dll",
    "C:\Windows\assembly\GAC_MSIL\System.Core\3.5.0.0__b77a5c561934e089\System.Core.dll"
)
$sysCore35 = $sysCoreCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

# Gather every C# source file recursively from src\
$sources = Get-ChildItem -Path (Join-Path $root "src") -Filter "*.cs" -Recurse | ForEach-Object { $_.FullName }
$appConfigPath = Join-Path (Join-Path $root "src") "app.config"

function Clear-LockedTarget($outPath, $outName) {
    Get-ChildItem -Path $binDir -Filter "$outName*.old" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $outPath) {
        Remove-Item -LiteralPath $outPath -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $outPath) {
            $oldPath = "$outPath.old"
            if (Test-Path -LiteralPath $oldPath) {
                $oldPath = "$outPath.$([DateTime]::UtcNow.Ticks).old"
            }
            Move-Item -LiteralPath $outPath -Destination $oldPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Copy-AppConfig($outPath) {
    if (Test-Path -LiteralPath $appConfigPath) {
        Copy-Item -LiteralPath $appConfigPath -Destination ($outPath + ".config") -Force
    }
}

# Compiles a legacy Windows XP 32-bit compatible CLR 2.0 (.NET 3.5) binary
function Invoke-Xp35Build($outName) {
    if (-not (Test-Path (Join-Path $fx20Dir "mscorlib.dll")) -or -not $sysCore35) {
        throw ".NET Framework 2.0/3.5 assemblies not found for Windows XP 32-bit build."
    }

    $outPath = Join-Path $binDir $outName
    Clear-LockedTarget $outPath $outName
    Write-Host "Building $outName (.NET 3.5 / CLR v2.0.50727 x86 for Windows XP 32-bit)..." -ForegroundColor Cyan

    $refMscorlib = "/reference:" + (Join-Path $fx20Dir "mscorlib.dll")
    $refSystem = "/reference:" + (Join-Path $fx20Dir "System.dll")
    $refDrawing = "/reference:" + (Join-Path $fx20Dir "System.Drawing.dll")
    $refForms = "/reference:" + (Join-Path $fx20Dir "System.Windows.Forms.dll")
    $refCore = "/reference:" + $sysCore35

    $compilerArgs = @(
        "/nologo",
        "/noconfig",
        "/nostdlib",
        "/target:winexe",
        "/optimize+",
        "/platform:x86",
        "/out:$outPath",
        $refMscorlib,
        $refSystem,
        $refDrawing,
        $refForms,
        $refCore
    ) + $sources

    & $csc32 @compilerArgs
    if ($LASTEXITCODE -ne 0) {
        throw "XP 32-bit compilation failed for $outName (exit $LASTEXITCODE)"
    }
    Copy-AppConfig $outPath
    Write-Host "  -> Built $outPath" -ForegroundColor Green
}

# Compiles a standard .NET 4.x target platform binary (x64 or anycpu) to bin\<outName>
function Invoke-TargetBuild($compiler, $platform, $outName) {
    $outPath = Join-Path $binDir $outName
    Clear-LockedTarget $outPath $outName
    Write-Host "Building $outName (/platform:$platform)..." -ForegroundColor Cyan
    $compilerArgs = @(
        "/nologo",
        "/target:winexe",
        "/optimize+",
        "/platform:$platform",
        "/out:$outPath",
        "/reference:System.dll",
        "/reference:System.Core.dll",
        "/reference:System.Drawing.dll",
        "/reference:System.Windows.Forms.dll"
    ) + $sources

    & $compiler @compilerArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation failed for $outName (exit $LASTEXITCODE)"
    }
    Copy-AppConfig $outPath
    Write-Host "  -> Built $outPath" -ForegroundColor Green
}

Invoke-Xp35Build "WinParted-XP-x86.exe"
Invoke-Xp35Build "WinParted-x86.exe"
Invoke-TargetBuild $csc64 "x64" "WinParted-x64.exe"
Invoke-TargetBuild $csc64 "anycpu" "WinParted.exe"

Write-Host "`nAll Windows XP 32-bit (CLR 2.0 / x86), 64-bit (x64), and Universal (AnyCPU) builds succeeded!" -ForegroundColor Green
