<#
  Publishes MarkLite as a self-contained NativeAOT build into publish/.

  This machine has no Windows SDK installed and the VS 18 C++ install is
  partial (no vcvarsall.bat, VC.Tools component not registered), so the
  ilcompiler linker autodetection fails. Instead, IlcUseEnvironmentalTools
  makes ilcompiler take link.exe from PATH and library paths from LIB:
    - MSVC linker + CRT libs come from the local VS 18 toolset (onecore lib
      variant — the desktop lib\x64 set is not installed; onecore links fine).
    - Windows SDK import libs were staged to D:\packages\WinSDK-CPP from the
      official NuGet package Microsoft.Windows.SDK.CPP.x64 10.0.28000.2526.
#>
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
<#  Newest installed toolset, not a pinned version: a Visual Studio update
    replaces the versioned folder (14.51.36231 -> 14.52.37025 broke the pin). #>
$msvcRoot = 'C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Tools\MSVC'
$msvc = Get-ChildItem -LiteralPath $msvcRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -as [version] } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $msvc) {
    throw "No MSVC toolset found under $msvcRoot"
}
$sdkLibs = 'D:\packages\WinSDK-CPP'

foreach ($required in "$msvc\bin\Hostx64\x64\link.exe",
                      "$msvc\lib\onecore\x64\libcmt.lib",
                      "$sdkLibs\um\x64\kernel32.lib",
                      "$sdkLibs\ucrt\x64\ucrt.lib") {
    if (-not (Test-Path $required)) {
        throw "Missing toolchain piece: $required"
    }
}

$env:PATH = "$msvc\bin\Hostx64\x64;$env:PATH"
$env:LIB = "$msvc\lib\onecore\x64;$sdkLibs\um\x64;$sdkLibs\ucrt\x64"

dotnet publish "$repoRoot\src\MarkLite\MarkLite.csproj" `
    -c Release -r win-x64 --self-contained `
    -p:IlcUseEnvironmentalTools=true `
    -o "$repoRoot\publish"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# The app forces Win32RenderingMode.Software, so the ANGLE GL translator
# never loads — no reason to ship it.
Remove-Item "$repoRoot\publish\av_libglesv2.dll" -ErrorAction SilentlyContinue

Write-Host "Published to $repoRoot\publish"
