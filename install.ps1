# Registers the logon scheduled task that runs G5DockSwitch.exe.
# Requires administrator rights (scheduled task registration).
#
# usage: right-click -> Run with PowerShell (as administrator)
#    or: powershell -ExecutionPolicy Bypass -File install.ps1

$ErrorActionPreference = 'Stop'

$task = 'HECATEDockAudioSwitch'
$dir  = $PSScriptRoot
$exe  = Join-Path $dir 'G5DockSwitch.exe'

if (-not (Test-Path $exe)) {
    throw "G5DockSwitch.exe not found next to this script. Build it with build.ps1 or extract the release zip."
}

# stop and remove any previous instance (also the old task name used by early builds)
foreach ($t in @($task, 'G5DockAudioSwitch')) {
    Stop-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 500

$action    = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $dir
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Start-Sleep -Milliseconds 500
Start-ScheduledTask -TaskName $task
Start-Sleep -Seconds 2

$info = Get-ScheduledTask -TaskName $task
Write-Host ""
Write-Host "task     : $($info.TaskName)"
Write-Host "state    : $($info.State)"
Write-Host "action   : $($info.Actions[0].Execute)"
Write-Host "log file : $(Join-Path $dir 'g5-switch.log')"
Write-Host ""
Write-Host "Now pick the headset up and put it back once, then check the log for 'state:' lines."
