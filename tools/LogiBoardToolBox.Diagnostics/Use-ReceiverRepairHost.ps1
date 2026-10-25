param(
    [ValidateSet('ping','self-test','restart-keyboard','diagnostic','stop')] [string] $Operation = 'ping',
    [switch] $DryRun,
    [string] $ScriptPath,
    [int] $WaitSeconds = 20
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../work/ghub-binding-fix'))
$taskMetadata = Join-Path $taskRoot $(if ($DryRun) { 'repair-host-dry-run.json' } else { 'repair-host-active.json' })
$taskHost = Get-Content -LiteralPath $taskMetadata -Raw | ConvertFrom-Json
if (-not (Get-Process -Id $taskHost.ProcessId -ErrorAction SilentlyContinue)) { throw 'The repair host is not running.' }
$taskSession = Join-Path (Join-Path $taskRoot 'repair-host') $taskHost.SessionId
$taskReady = Get-Content -LiteralPath (Join-Path $taskSession 'ready.json') -Raw | ConvertFrom-Json
if ($taskReady.SessionId -ne $taskHost.SessionId -or $taskReady.ProcessId -ne $taskHost.ProcessId) { throw 'Repair host identity mismatch.' }
$taskId = [guid]::NewGuid().ToString('N')
$taskRequest = [ordered]@{ SessionId=$taskHost.SessionId; RequestId=$taskId; Operation=$Operation }
if ($Operation -eq 'diagnostic') {
    $taskSource = Get-Item -LiteralPath $ScriptPath
    if ($taskSource.Extension -ne '.ps1') { throw 'An explicit PowerShell diagnostic script is required.' }
    $taskRequest.ScriptName = $taskId + '.ps1'
    $taskDestination = Join-Path (Join-Path $taskSession 'scripts') $taskRequest.ScriptName
    Copy-Item -LiteralPath $taskSource.FullName -Destination $taskDestination
    $taskRequest.ScriptSHA256 = (Get-FileHash -LiteralPath $taskDestination -Algorithm SHA256).Hash
}
$taskPath = Join-Path (Join-Path $taskSession 'requests') ($taskId + '.json')
$taskResult = Join-Path (Join-Path $taskSession 'results') ($taskId + '.json')
$taskRequest | ConvertTo-Json | Set-Content -LiteralPath ($taskPath + '.writing') -Encoding utf8
Move-Item -LiteralPath ($taskPath + '.writing') -Destination $taskPath
$taskDeadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
while (-not (Test-Path -LiteralPath $taskResult) -and [DateTime]::UtcNow -lt $taskDeadline) { Start-Sleep -Milliseconds 100 }
if (Test-Path -LiteralPath $taskResult) { Get-Content -LiteralPath $taskResult -Raw }
else { @{ Pending=$true; RequestId=$taskId; ResultPath=$taskResult } | ConvertTo-Json }
