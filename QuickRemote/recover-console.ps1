# QuickRemote Emergency Console Recovery
# Logs off all stuck remote RDP sessions (rdp-tcp#N) so Windows restores
# the local console session. No reboot required. Run as Administrator.
#
# Usage:  right-click -> "Run with PowerShell" (or run as admin)
#         powershell -ExecutionPolicy Bypass -File recover-console.ps1

$ErrorActionPreference = 'SilentlyContinue'

Write-Host "=== QuickRemote Emergency Recovery ===" -ForegroundColor Cyan
Write-Host "Searching for stuck remote sessions..." -ForegroundColor Yellow

$rdpLines = query session 2>$null | Select-String 'rdp-tcp'

if (-not $rdpLines) {
    Write-Host ""
    Write-Host "No remote RDP session found." -ForegroundColor Green
    Write-Host "If your screen is still black, try:" -ForegroundColor Yellow
    Write-Host "  1. Press Ctrl+Alt+Del to open the security screen" -ForegroundColor White
    Write-Host "  2. Press Win+Ctrl+Shift+B to reset the graphics driver" -ForegroundColor White
    Write-Host ""
    Read-Host "Press Enter to exit"
    exit 0
}

$count = 0
foreach ($line in $rdpLines) {
    $text = $line.ToString().Trim()
    # Parse session ID: "rdp-tcp#1    username    2  Active" -> ID = 2
    if ($text -match 'rdp-tcp#?\d*\s+\S*\s+(\d+)') {
        $sid = $Matches[1]
        Write-Host "  Logging off remote session (ID $sid)..." -ForegroundColor Yellow
        logoff $sid 2>$null
        if ($LASTEXITCODE -eq 0 -or $?) {
            $count++
        }
    }
}

Write-Host ""
Write-Host "Recovery complete: logged off $count remote session(s)." -ForegroundColor Green
Write-Host "The local console should be restored now." -ForegroundColor Green
Write-Host ""
Write-Host "If the screen is still black, press Ctrl+Alt+Del, then log back in." -ForegroundColor Yellow
Write-Host ""
Read-Host "Press Enter to exit"
