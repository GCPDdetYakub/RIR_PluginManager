# Removes RIR_PluginManager for one Revit version (run with that Revit closed).
# First restores plugin files that are still disabled (except files of other Revit versions that
# are running right now), then deletes the add-in together with its settings, profiles and logs.
# If some files could not be restored, nothing is deleted, so the list of disabled files is kept.
# The shared folder Addins\RIR_PluginManager is removed when the add-in is no longer installed
# for any Revit version and no files are disabled.
#   powershell -ExecutionPolicy Bypass -File .\uninstall.ps1                      # Revit 2027
#   powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RevitVersion 2026
#   add -Force to skip the check that this Revit is closed (and restore files of running Revit too)
param(
    [string]$RevitVersion = "2027",
    [switch]$Force
)
$ErrorActionPreference = "Stop"

if (-not $Force) {
    $running = @(Get-Process -Name Revit -ErrorAction SilentlyContinue | Where-Object {
        -not $_.Path -or $_.Path -match "Revit $RevitVersion"
    })
    if ($running.Count -gt 0) {
        Write-Host "Revit $RevitVersion is running (process id: $($running.Id -join ', ')). Close it and run again, or add -Force." -ForegroundColor Red
        exit 2
    }
}

& (Join-Path $PSScriptRoot "restore.ps1") -Force:$Force
$code = $LASTEXITCODE
if ($code -ne 0) {
    Write-Host "Uninstall stopped: restore.ps1 finished with code $code (see messages above). Nothing was removed." -ForegroundColor Red
    exit $code
}

$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins"
$year   = Join-Path $addins $RevitVersion
Remove-Item (Join-Path $year "RIR_PluginManager.addin") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $year "RIR_PluginManager") -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "RIR_PluginManager removed from Revit $RevitVersion."

# Shared folder: only when no Revit version has the add-in and nothing is disabled
$shared = Join-Path $addins "RIR_PluginManager"
$installed = @(Get-ChildItem $addins -Directory -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -match '^\d{4}$' -and (Test-Path (Join-Path $_.FullName "RIR_PluginManager")) })
if ((Test-Path $shared) -and $installed.Count -eq 0 -and -not (Test-Path (Join-Path $shared "disabled-plugins.txt"))) {
    Remove-Item $shared -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Shared folder removed: $shared"
}
