# Restores original names of plugin files disabled by RIR_PluginManager
# (use if Revit crashed and you need the plugins in standalone Rhino right now).
#   powershell -ExecutionPolicy Bypass -File .\restore.ps1
#   powershell -ExecutionPolicy Bypass -File .\restore.ps1 -Force   # also files of running Revit
#
# Since 1.18 all Revit versions share one list of disabled files:
#   %APPDATA%\Autodesk\Revit\Addins\RIR_PluginManager\disabled-plugins.txt
# Every line knows the Revit process that disabled the file. Files of Revit processes that are
# still running are left alone (that Revit returns them itself), so the script can be run while
# Revit is open. Old per-version lists (Addins\<year>\RIR_PluginManager\renamed.txt, before 1.18)
# are processed too, except for Revit versions that are running right now.
# A line is removed only when its file was really restored; failed files and conflicts (the
# original name is already taken) stay in the list.
# Exit code: 0 - OK, 1 - errors or conflicts, 3 - the list is locked by another process.
param([switch]$Force)
$ErrorActionPreference = "Stop"

$addins  = Join-Path $env:APPDATA "Autodesk\Revit\Addins"
$journal = Join-Path $addins "RIR_PluginManager\disabled-plugins.txt"
$machine = [Environment]::MachineName
$inv     = [System.Globalization.CultureInfo]::InvariantCulture
$restored = 0; $failed = 0; $conflicts = 0; $skipped = 0; $found = $false

# Returns $null if the file is back (or there is nothing to do), otherwise "failed" / "conflict".
function Restore-One([string]$orig) {
    $off = "$orig.off"
    if (-not (Test-Path -LiteralPath $off)) { return $null }
    if (Test-Path -LiteralPath $orig) {
        Write-Warning "Conflict, both files exist, nothing changed: $orig"
        return "conflict"
    }
    try {
        Rename-Item -LiteralPath $off -NewName (Split-Path $orig -Leaf) -ErrorAction Stop
        Write-Host "Restored: $orig"
        $script:restored++
        return $null
    }
    catch {
        Write-Warning "Failed: $orig ($($_.Exception.Message))"
        return "failed"
    }
}

# Years of running Revit processes; "?" if the year cannot be read.
function Get-RunningRevitYears {
    $years = @()
    foreach ($p in @(Get-Process -Name Revit -ErrorAction SilentlyContinue)) {
        if ($p.Path -and $p.Path -match 'Revit (\d{4})') { $years += $Matches[1] } else { $years += "?" }
    }
    return $years
}

$mutex = New-Object System.Threading.Mutex($false, 'Local\RIR_PluginManager.DisabledPlugins')
$owned = $false
try { $owned = $mutex.WaitOne(15000) } catch [System.Threading.AbandonedMutexException] { $owned = $true }
if (-not $owned) {
    Write-Host "The list of disabled files is locked by another process (Revit is busy with it). Try again in a moment." -ForegroundColor Red
    $mutex.Dispose()
    exit 3
}
try {
    # ---------- Shared list (1.18+) ----------
    if (Test-Path -LiteralPath $journal) {
        $found = $true
        Write-Host "List: $journal"
        $entries = @()
        foreach ($line in [System.IO.File]::ReadAllLines($journal)) {
            if (-not $line.Trim() -or $line.TrimStart().StartsWith("#")) { continue }
            $f = $line.Split("`t")
            $e = [pscustomobject]@{ Path = $f[0].Trim(); Revit = "?"; ProcId = 0; Start = [datetime]::MinValue; Machine = ""; Line = $line; Alive = $false }
            if ($f.Count -gt 1 -and $f[1].Trim()) { $e.Revit = $f[1].Trim() }
            $id = 0
            if ($f.Count -gt 2 -and [int]::TryParse($f[2], [ref]$id)) { $e.ProcId = $id }   # [ref] does not work with object properties
            if ($f.Count -gt 3) { try { $e.Start = [datetime]::Parse($f[3], $inv, [System.Globalization.DateTimeStyles]::RoundtripKind) } catch { } }
            if ($f.Count -gt 4) { $e.Machine = $f[4].Trim() }
            if ($e.Path) { $entries += $e }
        }

        # A line is alive while its Revit process is running (same id, same start time, this computer)
        foreach ($e in $entries) {
            $live = $false
            if (-not $Force -and $e.ProcId -gt 0 -and $e.Machine -eq $machine) {
                $p = Get-Process -Id $e.ProcId -ErrorAction SilentlyContinue
                if ($p) {
                    try { $live = [Math]::Abs(($p.StartTime.ToUniversalTime() - $e.Start.ToUniversalTime()).TotalSeconds) -le 2 }
                    catch { $live = $true }   # no access to the process: leave its files alone
                }
            }
            $e.Alive = $live
        }

        $keep = New-Object System.Collections.Generic.List[string]
        foreach ($e in $entries) { if ($e.Alive) { $keep.Add($e.Line); $skipped++ } }
        $livePaths = @($entries | Where-Object { $_.Alive } | ForEach-Object { $_.Path.ToUpperInvariant() })

        $done = @{}
        foreach ($e in ($entries | Where-Object { -not $_.Alive })) {
            $key = $e.Path.ToUpperInvariant()
            if ($done.ContainsKey($key)) { continue }
            $done[$key] = $true
            if ($livePaths -contains $key) { continue }      # still needed by a running Revit
            $result = Restore-One $e.Path
            if ($result) {
                if ($result -eq "conflict") { $conflicts++ } else { $failed++ }
                $min = [datetime]::MinValue.ToString("o", $inv)
                $keep.Add(("{0}`t{1}`t0`t{2}`t{3}`t{4}" -f $e.Path, $e.Revit, $min, $e.Machine, [datetime]::UtcNow.ToString("o", $inv)))
            }
        }

        if ($keep.Count -eq 0) { Remove-Item -LiteralPath $journal -ErrorAction Stop }
        else {
            $lines = @("# RIR_PluginManager: plugin files disabled (renamed to *.off) by Revit processes.",
                       "# path<TAB>Revit<TAB>pid<TAB>process start (UTC)<TAB>computer<TAB>written (UTC). Do not edit while Revit is running.") + $keep
            $tmp = "$journal.tmp"
            [System.IO.File]::WriteAllLines($tmp, [string[]]$lines)
            Move-Item -LiteralPath $tmp -Destination $journal -Force -ErrorAction Stop
        }
    }

    # ---------- Old per-version lists (before 1.18) ----------
    $running = @(Get-RunningRevitYears)
    $old = @()
    foreach ($n in @("RIR_PluginManager", "RirPluginManager")) {
        $old += @(Get-ChildItem (Join-Path $addins "*\$n\renamed.txt") -ErrorAction SilentlyContinue |
                  ForEach-Object { [pscustomobject]@{ File = $_.FullName; Year = $_.Directory.Parent.Name } })
    }
    $old += [pscustomobject]@{ File = (Join-Path $env:APPDATA "RirPluginManager\renamed.txt"); Year = "?" }   # versions 1.0-1.2
    foreach ($o in $old) {
        if (-not (Test-Path -LiteralPath $o.File)) { continue }
        $found = $true
        $busy = ($running -contains $o.Year) -or ($running -contains "?") -or ($o.Year -eq "?" -and $running.Count -gt 0)
        if ($busy -and -not $Force) {
            Write-Host "Old list skipped, Revit $($o.Year) is running: $($o.File)" -ForegroundColor Yellow
            $skipped++
            continue
        }
        Write-Host "Old list: $($o.File)"
        try {
            $left = New-Object System.Collections.Generic.List[string]
            $lines = @(Get-Content -LiteralPath $o.File -Encoding UTF8 -ErrorAction Stop |
                       ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object -Unique)
            foreach ($orig in $lines) {
                $result = Restore-One $orig
                if ($result) { if ($result -eq "conflict") { $conflicts++ } else { $failed++ }; $left.Add($orig) }
            }
            if ($left.Count -eq 0) { Remove-Item -LiteralPath $o.File -ErrorAction Stop }
            else { Set-Content -LiteralPath $o.File -Value $left -Encoding UTF8 -ErrorAction Stop }
        }
        catch {
            Write-Warning "List not processed: $($o.File) ($($_.Exception.Message))"
            $failed++
        }
    }
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}

if (-not $found) { Write-Host "Nothing to restore."; exit 0 }
Write-Host ""
$color = if ($failed -or $conflicts) { "Yellow" } else { "Green" }
Write-Host "Restored: $restored, failed: $failed, conflicts: $conflicts, left to running Revit: $skipped" -ForegroundColor $color
if ($failed -or $conflicts) {
    Write-Host "Files that were not restored stay in the list; fix the cause and run restore.ps1 again." -ForegroundColor Yellow
    exit 1
}
exit 0
