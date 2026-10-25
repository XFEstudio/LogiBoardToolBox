param([switch] $SelfCheck)
$ErrorActionPreference = 'Stop'
$taskKeyboardId = 'HID\VID_046D&PID_C547&MI_01&COL01\8&DD69A16&0&0000'
$taskExpectedParent = 'USB\VID_046D&PID_C547&MI_01\7&104bdf0d&0&0001'
$taskOutputDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../work/ghub-binding-fix/keyboard-child-restart-authorized'))
$taskKeyboard = Get-PnpDevice -InstanceId $taskKeyboardId
$taskParent = (Get-PnpDeviceProperty -InstanceId $taskKeyboardId -KeyName DEVPKEY_Device_Parent).Data
if ($taskKeyboard.Class -ne 'Keyboard' -or $taskKeyboard.InstanceId -ne $taskKeyboardId -or $taskParent -ne $taskExpectedParent) {
    throw 'Target keyboard child identity does not match. No restart was performed.'
}
if ($SelfCheck) {
    [pscustomobject]@{ Target = $taskKeyboardId; Parent = $taskParent; Class = $taskKeyboard.Class; State = $taskKeyboard.Status; RequiresAdministrator = $true; RestartExecuted = $false } | ConvertTo-Json
    exit 0
}
$taskPrincipal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator authorization is required. This script never elevates itself.'
}
[IO.Directory]::CreateDirectory($taskOutputDirectory) | Out-Null
$taskRecord = [ordered]@{ At = [DateTimeOffset]::Now; Target = $taskKeyboardId; Parent = $taskParent; BeforeStatus = $taskKeyboard.Status; RestartParent = $false; RestartMousePointer = $false; FlashWrites = $false; InputGenerated = $false }
try {
    $taskRecord.PnpUtilOutput = (& "$env:SystemRoot/System32/pnputil.exe" /restart-device $taskKeyboardId 2>&1 | Out-String)
    $taskRecord.PnpUtilExitCode = $LASTEXITCODE
    if ($LASTEXITCODE -ne 0) { throw "Keyboard child restart failed with exit code $LASTEXITCODE." }
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $taskAfter = Get-PnpDevice -InstanceId $taskKeyboardId
        if ($taskAfter.Status -eq 'OK') { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    $taskRecord.AfterStatus = $taskAfter.Status
    $taskRecord.Success = $taskAfter.Status -eq 'OK'
} catch {
    $taskRecord.Success = $false
    $taskRecord.Error = $_.Exception.Message
    throw
} finally {
    $taskRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutputDirectory 'result.json') -Encoding utf8
}
