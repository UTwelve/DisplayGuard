# test-tray.ps1 - simulation + smoke test for DisplayGuardTray.exe (v2.1)
# Part A: opens notepad, forces it onto the phantom screen (-700,1500), runs
#         DisplayGuardTray.exe --once, then proves the window is back in the
#         primary work area, and verifies the MOVE line in today's log file.
# Part B: tray-mode graceful smoke: start with --quiet, confirm alive, exit via
#         --quit IPC, confirm START/EXIT lines in the log.
# Part C: tray-mode forced-kill smoke: start with --quiet, confirm alive,
#         taskkill /F, confirm no residue.
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$exe = Join-Path $PSScriptRoot 'DisplayGuardTray.exe'
if (-not (Test-Path $exe)) { throw "DisplayGuardTray.exe not found at $exe" }
$logDir = Join-Path $PSScriptRoot 'logs'
$logFile = Join-Path $logDir ('DisplayGuard-' + (Get-Date -Format 'yyyyMMdd') + '.log')

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W32T {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int X, int Y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rc);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
"@

function Get-RectText([IntPtr]$h) {
    $rc = New-Object W32T+RECT
    [void][W32T]::GetWindowRect($h, [ref]$rc)
    return "({0},{1})-({2},{3}) {4}x{5}" -f $rc.Left, $rc.Top, $rc.Right, $rc.Bottom, ($rc.Right-$rc.Left), ($rc.Bottom-$rc.Top)
}

$failed = $false

# =========================== Part A: move test ===========================
Write-Host "===== PART A: notepad move test (--once) ====="

# 1. Start a fresh notepad and wait for its main window
$proc = Start-Process notepad.exe -PassThru
[void]$proc.WaitForInputIdle(5000)
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 50; $i++) {
    $cands = Get-Process -Name notepad -ErrorAction SilentlyContinue |
             Where-Object { $_.MainWindowHandle.ToInt64() -ne 0 }
    if ($cands) {
        $proc = $cands | Select-Object -First 1
        $hwnd = $proc.MainWindowHandle
        break
    }
    Start-Sleep -Milliseconds 100
}
if ($hwnd.ToInt64() -eq 0) { throw "notepad main window not found" }
[void][W32T]::ShowWindow($hwnd, 9)  # SW_RESTORE, ensure not maximized
Write-Host ("[A1] notepad started, hwnd={0}, pid={1}, initial rect: {2}" -f $hwnd, $proc.Id, (Get-RectText $hwnd))

# 2. Force notepad onto the phantom screen (inside DISPLAY5 bounds: -800,1440 - 0,2040)
$SWP_NOSIZE = 0x0001; $SWP_NOZORDER = 0x0004
[void][W32T]::SetWindowPos($hwnd, [IntPtr]::Zero, -700, 1500, 0, 0, $SWP_NOSIZE -bor $SWP_NOZORDER)
Start-Sleep -Milliseconds 300
$before = Get-RectText $hwnd
Write-Host "[A2] moved to phantom screen, rect BEFORE guard: $before"

# 3. Run DisplayGuardTray --once (console subsystem: waits + captures output)
Write-Host "[A3] running DisplayGuardTray.exe --once ..."
& $exe --once | ForEach-Object { Write-Host "    $_" }
Start-Sleep -Milliseconds 300

# 4. Read back the position
$after = Get-RectText $hwnd
Write-Host "[A4] rect AFTER guard: $after"

# 5. Verdict (primary work area: X 0..5120, Y 0..1392 per --list)
$rc = New-Object W32T+RECT
[void][W32T]::GetWindowRect($hwnd, [ref]$rc)
if ($rc.Left -ge 0 -and $rc.Top -ge 0 -and $rc.Right -le 5120 -and $rc.Bottom -le 1392) {
    Write-Host "[A5] TEST PASSED: notepad was moved back into the primary work area."
} else {
    Write-Host "[A5] TEST FAILED: notepad is still outside the primary work area."
    $failed = $true
}

# 6. Verify the MOVE line landed in today's log
$npPid = $proc.Id
Start-Sleep -Milliseconds 300
if (Test-Path $logFile) {
    $moveLines = Select-String -Path $logFile -Pattern ("MOVE .* \(pid=" + $npPid + "\)") |
                 ForEach-Object { $_.Line }
    if ($moveLines) {
        Write-Host "[A6] TEST PASSED: MOVE line(s) found in $logFile :"
        $moveLines | ForEach-Object { Write-Host "    $_" }
    } else {
        Write-Host "[A6] TEST FAILED: no MOVE line for pid=$npPid in $logFile"
        $failed = $true
    }
} else {
    Write-Host "[A6] TEST FAILED: log file not found: $logFile"
    $failed = $true
}

# 7. Cleanup notepad
$proc.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 500
if (-not $proc.HasExited) { $proc.Kill() }
Write-Host "[A7] notepad closed."

# ======================= Part B: tray graceful smoke =======================
Write-Host "===== PART B: tray mode smoke test (--quiet, graceful exit) ====="

# 1. Start tray app in background with --quiet (console subsystem -> hidden window)
$tray = Start-Process -FilePath $exe -ArgumentList '--quiet' -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 3

# 2. Confirm the process is alive
$alive = Get-Process -Id $tray.Id -ErrorAction SilentlyContinue
if ($alive) {
    Write-Host ("[B1] tray process alive: pid={0}, name={1}" -f $alive.Id, $alive.ProcessName)
} else {
    Write-Host "[B1] TEST FAILED: tray process is not running."
    $failed = $true
}

# 3. Ask it to exit gracefully via --quit IPC
& $exe --quit | ForEach-Object { Write-Host "    $_" }
Start-Sleep -Milliseconds 500
$gone = Get-Process -Id $tray.Id -ErrorAction SilentlyContinue
if ($gone) {
    Write-Host "[B2] TEST FAILED: tray process still running after --quit."
    $failed = $true
    & "$env:SystemRoot\System32\taskkill.exe" /F /PID $tray.Id | Out-Null
} else {
    Write-Host "[B2] tray process exited gracefully, no residue."
}

# 4. Confirm START/EXIT lines in the log
if (Test-Path $logFile) {
    $startLine = Select-String -Path $logFile -Pattern 'START mode=Tray quiet=True' |
                 Select-Object -Last 1
    $exitLine  = Select-String -Path $logFile -Pattern 'EXIT mode=Tray' |
                 Select-Object -Last 1
    if ($startLine -and $exitLine) {
        Write-Host "[B3] TEST PASSED: tray START/EXIT log lines present:"
        Write-Host ("    " + $startLine.Line)
        Write-Host ("    " + $exitLine.Line)
    } else {
        Write-Host "[B3] TEST FAILED: tray START/EXIT log lines missing."
        $failed = $true
    }
} else {
    Write-Host "[B3] TEST FAILED: log file not found: $logFile"
    $failed = $true
}

# ======================= Part C: tray taskkill smoke =======================
Write-Host "===== PART C: tray mode smoke test (taskkill /F) ====="

$tray2 = Start-Process -FilePath $exe -ArgumentList '--quiet' -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 3
$alive2 = Get-Process -Id $tray2.Id -ErrorAction SilentlyContinue
if ($alive2) {
    Write-Host ("[C1] tray process alive: pid={0}" -f $alive2.Id)
} else {
    Write-Host "[C1] TEST FAILED: tray process is not running."
    $failed = $true
}

$taskkillOut = & "$env:SystemRoot\System32\taskkill.exe" /F /PID $tray2.Id 2>&1
$taskkillCode = $LASTEXITCODE
Write-Host ("[C2] taskkill exit code: {0} ({1})" -f $taskkillCode, ($taskkillOut -join ' '))
if ($taskkillCode -ne 0) {
    Write-Host "[C2] TEST FAILED: taskkill returned non-zero."
    $failed = $true
}

Start-Sleep -Milliseconds 500
$gone2 = Get-Process -Id $tray2.Id -ErrorAction SilentlyContinue
if ($gone2) {
    Write-Host "[C3] TEST FAILED: tray process still running after taskkill."
    $failed = $true
} else {
    Write-Host "[C3] tray process terminated, no residue."
}

if ($failed) {
    Write-Host "===== OVERALL: TEST FAILED ====="
    exit 1
}
Write-Host "===== OVERALL: TEST PASSED ====="
exit 0
