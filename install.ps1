# Builds RIR_PluginManager and installs it for the current user.
# Run with the target Revit closed:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1                      # Revit 2027
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -RevitVersion 2026   # Revit 2026.5+
# If Revit is installed elsewhere, add: -RevitDir "D:\Autodesk\Revit 2027"
# The first build needs access to nuget.org (package Lib.Harmony).

param(
    [string]$RevitVersion = "2027",
    [string]$RevitDir = ""
)
$ErrorActionPreference = "Stop"

if (-not $RevitDir) { $RevitDir = "C:\Program Files\Autodesk\Revit $RevitVersion" }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK not found. Install .NET 10 SDK (x64) and open a new PowerShell window."
}
if (-not (Test-Path (Join-Path $RevitDir "RevitAPI.dll"))) {
    throw "RevitAPI.dll not found in '$RevitDir'. Pass the correct folder with -RevitDir."
}

$proj = Join-Path $PSScriptRoot "RIR_PluginManager.csproj"
$out  = Join-Path $PSScriptRoot "bin\Release$RevitVersion"
dotnet build $proj -c Release "-p:RevitDir=$RevitDir" -o $out
if ($LASTEXITCODE -ne 0) { throw "Build failed, see messages above." }

$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$target = Join-Path $addins "RIR_PluginManager"
New-Item -ItemType Directory -Force -Path $target | Out-Null

# Previous name of the add-in (RirPluginManager): move the user data over and remove the old install,
# otherwise Revit would load both and complain about a duplicate AddInId.
$old = Join-Path $addins "RirPluginManager"
if (Test-Path $old) {
    Get-ChildItem $old -File | Where-Object { $_.Name -like "profile-revit*.txt" -or $_.Name -in @("renamed.txt", "folders.txt") } |
        ForEach-Object {
            $dst = Join-Path $target $_.Name
            if ($_.Name -eq "renamed.txt" -and (Test-Path $dst)) {
                Get-Content $_.FullName -Encoding UTF8 | Add-Content $dst -Encoding UTF8
            } elseif (-not (Test-Path $dst)) {
                Copy-Item $_.FullName $dst
            }
        }
    Remove-Item $old -Recurse -Force
    Write-Host "Old install 'RirPluginManager' migrated and removed."
}
Remove-Item (Join-Path $addins "RirPluginManager.addin") -Force -ErrorAction SilentlyContinue

# The add-in DLL and its dependencies (0Harmony.dll, ...)
Get-ChildItem $out -Filter *.dll |
    Where-Object { $_.Name -notin @("RevitAPI.dll", "RevitAPIUI.dll") } |
    Copy-Item -Destination $target -Force
Copy-Item (Join-Path $PSScriptRoot "RIR_PluginManager.addin") $addins -Force

Write-Host ""
Write-Host "Installed to: $addins" -ForegroundColor Green
Get-ChildItem $target -Filter *.dll | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host "Start Revit $RevitVersion and choose 'Always Load' if Revit asks about an unsigned add-in."
