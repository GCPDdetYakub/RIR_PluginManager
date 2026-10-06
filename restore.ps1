# Restores original names of plugin files disabled by RIR_PluginManager
# (use if Revit crashed and you need the plugins in standalone Rhino right now).
# Handles every Revit version where the add-in is installed, including the old name RirPluginManager.
#   powershell -ExecutionPolicy Bypass -File .\restore.ps1
$candidates = @()
foreach ($n in @("RIR_PluginManager", "RirPluginManager")) {
    $candidates += @(Get-ChildItem (Join-Path $env:APPDATA "Autodesk\Revit\Addins\*\$n\renamed.txt") -ErrorAction SilentlyContinue |
                     ForEach-Object { $_.FullName })
}
$candidates += (Join-Path $env:APPDATA "RirPluginManager\renamed.txt")   # versions 1.0-1.2
$found = $false
foreach ($state in $candidates) {
    if (-not (Test-Path $state)) { continue }
    $found = $true
    $left = @()
    Get-Content $state -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object -Unique | ForEach-Object {
        $orig = $_; $off = "$orig.off"
        if (-not (Test-Path -LiteralPath $off)) { return }
        if (Test-Path -LiteralPath $orig) { Write-Warning "Exists, skipped: $orig"; return }
        try { Rename-Item -LiteralPath $off -NewName (Split-Path $orig -Leaf); Write-Host "Restored: $orig" }
        catch { Write-Warning "Failed: $orig ($_)"; $left += $orig }
    }
    if ($left.Count -eq 0) { Remove-Item $state } else { $left | Set-Content $state -Encoding UTF8 }
}
if (-not $found) { Write-Host "Nothing to restore." }
