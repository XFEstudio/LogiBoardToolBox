param([switch] $Execute)
$ErrorActionPreference = 'Stop'
$taskTarget = 'USB\VID_046D&PID_C547&MI_01\7&104BDF0D&0&0001'
$taskPointer = 'USB\VID_046D&PID_C547&MI_00\7&104BDF0D&0&0000'
$taskParent = 'USB\VID_046D&PID_C547\6&23491980&0&1'
$taskExpectedKeyboard = 'HID\VID_046D&PID_C547&MI_01&COL01\8&DD69A16&0&0000'
$taskFolder = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../work/ghub-binding-fix/keyboard-interface-recovery'))
$taskDevice = Get-PnpDevice -InstanceId $taskTarget
$taskPointerDevice = Get-PnpDevice -InstanceId $taskPointer
$taskChildren = @((Get-PnpDeviceProperty -InstanceId $taskTarget -KeyName DEVPKEY_Device_Children).Data)
$taskPointerArrival = (Get-PnpDeviceProperty -InstanceId $taskPointer -KeyName DEVPKEY_Device_LastArrivalDate).Data
foreach ($taskInterface in @($taskTarget,$taskPointer)) {
 if ((Get-PnpDeviceProperty -InstanceId $taskInterface -KeyName DEVPKEY_Device_Parent).Data -ne $taskParent -or
     (Get-PnpDeviceProperty -InstanceId $taskInterface -KeyName DEVPKEY_Device_Service).Data -ne 'HidUsb') { throw 'USB interface identity mismatch.' }
}
if ($taskDevice.Class -ne 'HIDClass' -or $taskPointerDevice.Status -ne 'OK' -or $taskChildren.Count -ne 3 -or
    $taskChildren -notcontains $taskExpectedKeyboard -or
    @($taskChildren | Where-Object {$_ -notlike 'HID\VID_046D&PID_C547&MI_01&COL0*\8&DD69A16&0&000*'}).Count -ne 0) { throw 'Unexpected interface children. No restart performed.' }
$taskRecord = [ordered]@{ At=[DateTimeOffset]::Now; Target=$taskTarget; Children=$taskChildren; PointerExcluded=$taskPointer; CompositeParentExcluded=$taskParent; PointerBeforeStatus=$taskPointerDevice.Status; PointerBeforeArrival=$taskPointerArrival; FlashWrites=$false; SystemRestartRequested=$false; InputGeneration=$false; Execute=[bool]$Execute }
[IO.Directory]::CreateDirectory($taskFolder) | Out-Null
if (-not $Execute) {
 $taskRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskFolder 'preflight.json') -Encoding utf8
 $taskRecord | ConvertTo-Json -Depth 5
 return
}
$taskPrincipal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Use the existing authorized repair host.' }
try {
 $taskRecord.PnpUtilOutput=(& "$env:SystemRoot/System32/pnputil.exe" /restart-device $taskTarget 2>&1 | Out-String)
 $taskRecord.ExitCode=$LASTEXITCODE
 $taskRecord.NeedsSystemRestart=$LASTEXITCODE -eq 3010
 $taskRecord.RestartCompleted=$LASTEXITCODE -eq 0
 $taskRecord.AfterStatus=(Get-PnpDevice -InstanceId $taskTarget).Status
 $taskRecord.PointerAfterStatus=(Get-PnpDevice -InstanceId $taskPointer).Status
 $taskRecord.PointerAfterArrival=(Get-PnpDeviceProperty -InstanceId $taskPointer -KeyName DEVPKEY_Device_LastArrivalDate).Data
 $taskRecord.PointerInterfaceUnchanged=$taskRecord.PointerBeforeStatus -eq $taskRecord.PointerAfterStatus -and $taskRecord.PointerBeforeArrival -eq $taskRecord.PointerAfterArrival
} finally {
 $taskRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskFolder 'result.json') -Encoding utf8
}
$taskRecord | ConvertTo-Json -Depth 5
