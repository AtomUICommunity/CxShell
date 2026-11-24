$ErrorActionPreference = "Stop"
$env:VCPKG_ROOT = "D:\vcpkg"

& (Join-Path $PSScriptRoot "tools\build-rdp-bridge.ps1") `
  -VcpkgRoot $env:VCPKG_ROOT `
  -Triplet x64-windows `
  -OutputDir (Join-Path $PSScriptRoot "runtimes\win-x64\native")

dotnet publish (Join-Path $PSScriptRoot "CxShell.csproj") `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o (Join-Path $PSScriptRoot "artifacts\publish\win-x64") `
  /p:DebugType=none `
  /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
  throw "dotnet publish failed with exit code $LASTEXITCODE."
}