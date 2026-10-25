param([switch] $DryRun)
$ErrorActionPreference = 'Stop'
$taskScript = Join-Path $PSScriptRoot 'Receiver-RepairHost.ps1'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../work/ghub-binding-fix'))
$taskMetadata = Join-Path $taskRoot $(if ($DryRun) { 'repair-host-dry-run.json' } else { 'repair-host-active.json' })
if (Test-Path -LiteralPath $taskMetadata) {
    $taskPrevious = Get-Content -LiteralPath $taskMetadata -Raw | ConvertFrom-Json
    if (Get-Process -Id $taskPrevious.ProcessId -ErrorAction SilentlyContinue) { throw 'An existing repair host is still running. Reuse it.' }
}
$taskSession = [guid]::NewGuid().ToString('N')
$taskArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $taskScript + '"'),'-SessionId',$taskSession)
if ($DryRun) { $taskArgs += '-DryRun' }
$taskStart = @{ FilePath=(Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'); ArgumentList=$taskArgs; WindowStyle='Hidden'; PassThru=$true }
if (-not $DryRun) { $taskStart.Verb = 'RunAs' }
else { $taskStart.RedirectStandardOutput = Join-Path $taskRoot 'repair-host-dry-run.stdout.txt'; $taskStart.RedirectStandardError = Join-Path $taskRoot 'repair-host-dry-run.stderr.txt' }
$taskProcess = Start-Process @taskStart
@{ SessionId=$taskSession; ProcessId=$taskProcess.Id; DryRun=[bool]$DryRun; LauncherProcessId=$PID; StartedAt=[DateTimeOffset]::Now } | ConvertTo-Json | Set-Content -LiteralPath $taskMetadata -Encoding utf8
Write-Output ('Repair host launched: ' + $taskSession + ' PID ' + $taskProcess.Id)
# Keep the launcher alive too, so the tool session does not tear down the child.
while (-not $taskProcess.WaitForExit(1000)) { }
Write-Output ('Repair host exited: ' + $taskProcess.ExitCode)
