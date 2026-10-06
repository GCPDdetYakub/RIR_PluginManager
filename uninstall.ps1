# Removes RIR_PluginManager for one Revit version (run with that Revit closed).
# First restores any plugin files that are still disabled, then deletes the add-in
# together with its profile and log.
#   powershell -ExecutionPolicy Bypass -File .\uninstall.ps1                      # Revit 2027
#   powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RevitVersion 2026
param([string]$RevitVersion = "2027")
& (Join-Path $PSScriptRoot "restore.ps1")
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
Remove-Item (Join-Path $addins "RIR_PluginManager.addin") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $addins "RIR_PluginManager") -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "RIR_PluginManager removed from Revit $RevitVersion."
