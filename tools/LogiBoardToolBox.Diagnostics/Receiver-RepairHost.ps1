param([switch] $SelfCheck, [switch] $DryRun, [string] $SessionId)
$ErrorActionPreference = 'Stop'
# A child started by PowerShell 7 can inherit its incompatible module path.
# Use the Windows PowerShell module roots for this Windows PowerShell 5.1 host.
$env:PSModulePath = (Join-Path $PSHOME 'Modules') + ';' + (Join-Path $env:ProgramFiles 'WindowsPowerShell/Modules')
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../work/ghub-binding-fix/repair-host'))
$taskRestartScript = Join-Path $PSScriptRoot 'Restart-ReceiverKeyboard.ps1'
if ($SelfCheck) {
    & $taskRestartScript -SelfCheck
    [pscustomobject]@{ Interpreter = $PSVersionTable.PSVersion.ToString(); Persistent = $true; Operations = @('ping','self-test','restart-keyboard','diagnostic','stop'); DeviceActionsExecuted = $false } | ConvertTo-Json
    exit 0
}
if ($SessionId -notmatch '^[a-f0-9]{32}$') { throw 'A fresh session ID is required.' }
$taskPrincipal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
$taskAdministrator = $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $DryRun -and -not $taskAdministrator) { throw 'Administrator authorization is required.' }
$taskDirectory = Join-Path $taskRoot $SessionId
$taskRequests = Join-Path $taskDirectory 'requests'
$taskScripts = Join-Path $taskDirectory 'scripts'
$taskResults = Join-Path $taskDirectory 'results'
foreach ($taskFolder in @($taskDirectory,$taskRequests,$taskScripts,$taskResults)) {
    [IO.Directory]::CreateDirectory($taskFolder) | Out-Null
    if ((Get-Item -LiteralPath $taskFolder).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not allowed.' }
}
function Save-HostJson([string] $Name, $Value) {
    $taskDestination = Join-Path $taskDirectory $Name
    $taskTemporary = $taskDestination + '.writing'
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $taskTemporary -Encoding utf8
    Move-Item -LiteralPath $taskTemporary -Destination $taskDestination -Force
}
function Invoke-RepairChild([string] $ScriptPath, [string] $RequestId, [string] $Command) {
    $taskChildOut = Join-Path $taskResults ($RequestId + '.stdout.txt')
    $taskChildErr = Join-Path $taskResults ($RequestId + '.stderr.txt')
    $taskChildArgs = @('-NoProfile','-ExecutionPolicy','Bypass')
    if ($ScriptPath) { $taskChildArgs += @('-File',('"' + $ScriptPath + '"')) }
    else { $taskChildArgs += @('-Command',('"' + $Command + '"')) }
    $taskChildStart = New-Object Diagnostics.ProcessStartInfo
    $taskChildStart.FileName = Join-Path $PSHOME 'powershell.exe'
    $taskChildStart.Arguments = $taskChildArgs -join ' '
    $taskChildStart.UseShellExecute = $false
    $taskChildStart.CreateNoWindow = $true
    $taskChildStart.RedirectStandardOutput = $true
    $taskChildStart.RedirectStandardError = $true
    $taskChild = New-Object Diagnostics.Process
    $taskChild.StartInfo = $taskChildStart
    if (-not $taskChild.Start()) { throw 'Could not start diagnostic child.' }
    $taskChildOutput = $taskChild.StandardOutput.ReadToEndAsync()
    $taskChildError = $taskChild.StandardError.ReadToEndAsync()
    $taskChildDeadline = [DateTime]::UtcNow.AddMinutes(10)
    while (-not $taskChild.WaitForExit(500)) {
        Save-HostJson 'heartbeat.json' @{ At=[DateTimeOffset]::Now; ProcessId=$PID; Ready=$true; SessionId=$SessionId; ActiveRequest=$RequestId; ChildProcessId=$taskChild.Id }
        if ([DateTime]::UtcNow -gt $taskChildDeadline) { $taskChild.Kill(); throw 'Diagnostic child exceeded ten minutes. The repair host remains available.' }
    }
    $taskChild.WaitForExit()
    $taskChildOutput.Result | Set-Content -LiteralPath $taskChildOut -Encoding utf8
    $taskChildError.Result | Set-Content -LiteralPath $taskChildErr -Encoding utf8
    $taskChildResult = [ordered]@{ ProcessId=$taskChild.Id; ExitCode=$taskChild.ExitCode; StandardOutput=$taskChildOutput.Result; StandardError=$taskChildError.Result }
    return $taskChildResult
}
$taskStop = $false
$taskStarted = [DateTimeOffset]::Now
Save-HostJson 'ready.json' ([ordered]@{ SessionId=$SessionId; ProcessId=$PID; Administrator=$taskAdministrator; DryRun=[bool]$DryRun; StartedAt=$taskStarted; Persistent=$true; UserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value; RestartScriptHash=(Get-FileHash -LiteralPath $taskRestartScript -Algorithm SHA256).Hash })
$taskHeartbeat = [DateTime]::MinValue
try {
    while (-not $taskStop) {
        try {
            if ([DateTime]::UtcNow -ge $taskHeartbeat) {
                Save-HostJson 'heartbeat.json' @{ At=[DateTimeOffset]::Now; ProcessId=$PID; Ready=$true; SessionId=$SessionId }
                $taskHeartbeat = [DateTime]::UtcNow.AddSeconds(2)
            }
            $taskFiles = @(Get-ChildItem -LiteralPath $taskRequests -Filter '*.json' -File | Sort-Object Name)
            foreach ($taskFile in $taskFiles) {
                $taskId = [IO.Path]::GetFileNameWithoutExtension($taskFile.Name)
                if ($taskId -notmatch '^[a-f0-9]{32}$' -or $taskFile.Length -gt 65536 -or ($taskFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
                $taskResponse = [ordered]@{ RequestId=$taskId; ProcessId=$PID; StartedAt=[DateTimeOffset]::Now; Success=$false; DryRun=[bool]$DryRun }
                try {
                    $taskRequest = Get-Content -LiteralPath $taskFile.FullName -Raw | ConvertFrom-Json
                    if ($taskRequest.SessionId -ne $SessionId -or $taskRequest.RequestId -ne $taskId) { throw 'Request identity mismatch.' }
                    switch ($taskRequest.Operation) {
                        'ping' { $taskResponse.Output = @{ Alive=$true; Administrator=$taskAdministrator; Persistent=$true } }
                        'self-test' {
                            $taskResponse.Output = Invoke-RepairChild '' $taskId 'exit 7'
                            if ($taskResponse.Output.ExitCode -ne 7) { throw 'Child process isolation self-test failed.' }
                        }
                        'restart-keyboard' {
                            if ($DryRun) { throw 'Device mutation is disabled in the dry-run host.' }
                            $taskResponse.Output = Invoke-RepairChild $taskRestartScript $taskId ''
                            if ($taskResponse.Output.ExitCode -ne 0) { throw 'Keyboard child restart process failed. The repair host remains available.' }
                            $taskRecovery = Get-Content -LiteralPath (Join-Path $taskRoot '../keyboard-child-restart-authorized/result.json') -Raw | ConvertFrom-Json
                            if (-not $taskRecovery.Success) { throw 'Keyboard child restart did not succeed.' }
                            $taskResponse.Recovery = $taskRecovery
                        }
                        'diagnostic' {
                            # Only explicit, hash-checked jobs staged inside this session.
                            # There is no network listener or automatic loading of repo files.
                            if ($taskRequest.ScriptName -notmatch '^[a-f0-9]{32}\.ps1$') { throw 'Invalid diagnostic script name.' }
                            $taskScript = Join-Path $taskScripts $taskRequest.ScriptName
                            $taskScriptInfo = Get-Item -LiteralPath $taskScript
                            if ($taskScriptInfo.Length -gt 262144 -or ($taskScriptInfo.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid diagnostic script file.' }
                            if ((Get-FileHash -LiteralPath $taskScript -Algorithm SHA256).Hash -ne $taskRequest.ScriptSHA256) { throw 'Diagnostic script hash mismatch.' }
                            if ($DryRun) { throw 'Diagnostic execution is disabled in the dry-run host.' }
                            $taskResponse.Output = Invoke-RepairChild $taskScript $taskId ''
                            if ($taskResponse.Output.ExitCode -ne 0) { throw 'Diagnostic process failed. The repair host remains available.' }
                        }
                        'stop' { $taskStop = $true; $taskResponse.Output = 'Stopping after this request.' }
                        default { throw 'Unsupported repair operation.' }
                    }
                    $taskResponse.Success = $true
                } catch {
                    $taskResponse.Error = $_.Exception.Message
                } finally {
                    $taskResponse.CompletedAt = [DateTimeOffset]::Now
                    Save-HostJson (Join-Path 'results' ($taskId + '.json')) $taskResponse
                    # Non-recursive cleanup, exact file inside the validated queue.
                    Remove-Item -LiteralPath $taskFile.FullName
                }
            }
        } catch {
            Save-HostJson 'last-host-error.json' @{ At=[DateTimeOffset]::Now; Error=$_.Exception.Message; ProcessId=$PID; ContinuesRunning=$true }
        }
        Start-Sleep -Milliseconds 200
    }
} finally {
    Save-HostJson 'stopped.json' @{ At=[DateTimeOffset]::Now; ProcessId=$PID; SessionId=$SessionId }
}
