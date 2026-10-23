# Removes the scheduled task and makes the headset audio endpoint visible again.
# Requires administrator rights.
#
# usage: right-click -> Run with PowerShell (as administrator)
#    or: powershell -ExecutionPolicy Bypass -File uninstall.ps1

$ErrorActionPreference = 'Continue'

$dir = $PSScriptRoot
$exe = Join-Path $dir 'G5DockSwitch.exe'

foreach ($t in @('HECATEDockAudioSwitch', 'G5DockAudioSwitch')) {
    Stop-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue
}

Get-Process -Name G5DockSwitch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# let the tool itself make the endpoint visible again (it knows how to find it)
if (Test-Path $exe) {
    & $exe headset | Out-Null
    Start-Sleep -Seconds 1
    Remove-Item (Join-Path $dir 'override.txt') -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "task removed : $(-not (Get-ScheduledTask -TaskName 'HECATEDockAudioSwitch' -ErrorAction SilentlyContinue))"
Write-Host "headset audio: set visible again"
Write-Host ""
Write-Host "You can delete this folder now if you also want the logs and the exe gone."
