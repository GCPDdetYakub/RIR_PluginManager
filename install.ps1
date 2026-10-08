# Builds RIR_PluginManager and installs it for the current user.
# Run with the target Revit closed:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1                      # Revit 2027
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -RevitVersion 2026   # Revit 2026 (any update)
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -RevitVersion 2025   # Revit 2025 (any update)
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -RevitVersion 2024   # Revit 2021-2024 (.NET Framework 4.8)
# If Revit is installed elsewhere, add: -RevitDir "D:\Autodesk\Revit 2027"
# If the update of Revit 2025/2026 cannot be read from RevitAPI.dll, add: -DotNet 8 (x.0-x.4) or -DotNet 10 (x.5+)
# The target Revit must be closed; add -Force to skip this check.
# The first build needs access to nuget.org (Lib.Harmony and System.Formats.Nrbf for .NET 8/10,
# System.Reflection.Metadata for .NET Framework 4.8).

param(
    [string]$RevitVersion = "2027",
    [string]$RevitDir = "",
    [string]$DotNet = "",         # override: 48, 8 or 10
    [switch]$Force                # do not check whether this Revit is running
)
$ErrorActionPreference = "Stop"

if (-not $RevitDir) { $RevitDir = "C:\Program Files\Autodesk\Revit $RevitVersion" }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK not found. Install .NET 10 SDK (x64) and open a new PowerShell window."
}
if (-not (Test-Path (Join-Path $RevitDir "RevitAPI.dll"))) {
    throw "RevitAPI.dll not found in '$RevitDir'. Pass the correct folder with -RevitDir."
}

# The add-in DLLs of a running Revit are locked: stop if Revit of this version is running.
if (-not $Force) {
    $exe = [System.IO.Path]::GetFullPath((Join-Path $RevitDir "Revit.exe"))
    $running = @(Get-Process -Name Revit -ErrorAction SilentlyContinue | Where-Object {
        # Path is empty if it cannot be read: treat such a process as this Revit, to be safe
        -not $_.Path -or [string]::Equals($_.Path, $exe, [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($running.Count -gt 0) {
        throw "Revit $RevitVersion is running (process id: $($running.Id -join ', ')). Close it and run again, or add -Force."
    }
}

# Runtime of this Revit: 2021-2024 -> .NET Framework 4.8; 2025/2026 up to x.4 -> .NET 8; 2025.5+/2026.5+/2027 -> .NET 10.
# Determined from the file version of RevitAPI.dll (for example 25.4.x.x or 26.5.x.x).
$apiVersion = $null
try { $apiVersion = [version](Get-Item (Join-Path $RevitDir "RevitAPI.dll")).VersionInfo.FileVersion.Split(' ')[0] } catch { }
$year = [int]$RevitVersion
if ($DotNet) { $dotnet = $DotNet }
elseif ($year -lt 2025) { $dotnet = "48" }
elseif ($year -ge 2027) { $dotnet = "10" }
elseif (-not $apiVersion) {
    throw "Cannot read the version of RevitAPI.dll in '$RevitDir', so the runtime of Revit $RevitVersion is unknown. " +
          "Add -DotNet 8 for Revit $RevitVersion.0-$RevitVersion.4 or -DotNet 10 for $RevitVersion.5 and later."
}
elseif ($apiVersion.Minor -lt 5) { $dotnet = "8" }
else { $dotnet = "10" }
Write-Host "Revit $RevitVersion (RevitAPI $apiVersion) -> target runtime: $(if ($dotnet -eq '48') {'.NET Framework 4.8'} else {".NET $dotnet"})"

$proj = Join-Path $PSScriptRoot "RIR_PluginManager.csproj"
$out  = Join-Path $PSScriptRoot "bin\Release$RevitVersion"
dotnet build $proj -c Release "-p:RevitVersion=$RevitVersion" "-p:RevitDotNet=$dotnet" "-p:RevitDir=$RevitDir" -o $out
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
