# Registers an on-demand interactive task so Project Hub can open Cutroom in the signed-in desktop session.
param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Cutroom'))
$ErrorActionPreference = 'Stop'
$exe = Join-Path $Destination 'Cutroom.exe'
if (!(Test-Path -LiteralPath $exe)) { throw "Cutroom is not installed at $exe" }

$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $Destination
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'Cutroom Hub Operator' -Action $action -Principal $principal -Settings $settings -Description 'On-demand Project Hub launch for Cutroom in the signed-in desktop session.' -Force | Out-Null
Write-Output 'Registered the on-demand Cutroom Hub Operator task for the current Windows user.'
