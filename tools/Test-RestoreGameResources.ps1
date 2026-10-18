# Run with powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-RestoreGameResources.ps1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$restore = Join-Path $PSScriptRoot 'Restore-GameResources.ps1'
$testRoot = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/resource-tool-tests/' + [Guid]::NewGuid().ToString('N'))
$client = Join-Path $testRoot 'client with spaces/game/sqpack'
$tcClient = Join-Path $testRoot 'tc client/game/sqpack'
foreach ($path in @($client, $tcClient)) {
    New-Item -ItemType Directory -Path (Join-Path $path 'ffxiv') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $path 'ffxiv/test.index') -Value 'fixture'
}
$global:resourceTestCalls = [Collections.Generic.List[string[]]]::new()
$global:resourceTestMode = 'ok'
$global:resourceTestSdk = '10.0.100 [mock]'
function Write-Json([string]$Path, $Value) {
    New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force | Out-Null
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}
# These functions shadow executables only inside this test process.
function dotnet {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq '--list-sdks') { return $global:resourceTestSdk }
    $global:resourceTestCalls.Add([string[]]$args)
    if ($global:resourceTestMode -eq 'exit-failure') { $global:LASTEXITCODE = 7; return }
    if ($args[2] -like '*VoiceScan') {
        $scan = $args[5]
        $errors = @()
        if ($global:resourceTestMode -eq 'scan-errors') { $errors = @('unreadable SCD fixture') }
        $count = 1
        if ($global:resourceTestMode -eq 'empty') { $count = 0 }
        Write-Json (Join-Path $scan 'scan-summary.json') @{Errors=$errors; VerifiedAudioVariants=$count}
        Write-Json (Join-Path $scan 'voice-clips.json') @()
    } elseif ($args[4] -eq '--dialogue-index' -or $args[4] -eq '--traditional-dialogue') {
        $destination = $args[6 + [int]($args[4] -eq '--traditional-dialogue')]
        Write-Json ($destination + '.report.json') @{Errors=@()}
    } elseif ($args[4] -notlike '--*') {
        $folder = $args[6]
        $errors = @()
        if ($global:resourceTestMode -eq 'catalog-errors') { $errors = @('missing reference') }
        Write-Json (Join-Path $folder 'catalog.json') @{Errors=$errors; Voices=@(@{Token='TEST'})}
        foreach ($name in @('native-voiced-lines.json', 'npc-voice-index.json', 'npc-voices.csv', 'npcs-without-matched-voice.csv', 'voice-name-candidates.json')) {
            Set-Content -LiteralPath (Join-Path $folder $name) -Value '{}'
        }
    }
}
function ffmpeg { $global:LASTEXITCODE = 0; 'ffmpeg mock' }
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_ }
    Assert ($null -ne $caught) "Expected failure: $Pattern"
    Assert ($caught.ToString() -like "*$Pattern*") "Unexpected error: $caught"
}
$out = Join-Path $testRoot 'output with spaces'
& $restore -SqPack $client -OutputRoot $out -CheckOnly
Assert ($global:resourceTestCalls.Count -eq 0 -and !(Test-Path -LiteralPath $out)) 'CheckOnly must not invoke builders or create output.'

& $restore -SqPack $client -OutputRoot $out -TraditionalSqPack $tcClient -DialogueLanguages ja,en
Assert ($global:resourceTestCalls.Count -eq 5) 'Expected scan, library, ja, en and TC pairing; ja must not duplicate.'
Assert ($global:resourceTestCalls[0][4] -eq $client) 'Client path with spaces must remain one argument.'
Assert ($global:resourceTestCalls[2][4] -eq '--dialogue-index' -and $global:resourceTestCalls[2][7] -eq 'ja') 'Japanese must be built before TC pairing.'
Assert ($global:resourceTestCalls[4][4] -eq '--traditional-dialogue' -and $global:resourceTestCalls[4][6] -eq $tcClient) 'TC pairing arguments incorrect.'

$global:resourceTestCalls.Clear()
& $restore -SqPack $client -OutputRoot $out
Assert ($global:resourceTestCalls.Count -eq 2 -and $global:resourceTestCalls[0][2] -like '*VoiceScan') 'An existing manifest must still be rescanned.'

$global:resourceTestCalls.Clear()
$global:resourceTestMode = 'scan-errors'
& $restore -SqPack $client -OutputRoot $out
Assert ($global:resourceTestCalls.Count -eq 2) 'Default mode must retain usable verified clips after a warning.'
$global:resourceTestCalls.Clear()
Expect-Failure { & $restore -SqPack $client -OutputRoot $out -StrictScan } 'reported errors'
Assert ($global:resourceTestCalls.Count -eq 1) 'Strict scan errors must stop before library building.'

$global:resourceTestMode = 'empty'
Expect-Failure { & $restore -SqPack $client -OutputRoot $out } 'No voice resources'
$global:resourceTestMode = 'catalog-errors'
Expect-Failure { & $restore -SqPack $client -OutputRoot $out } 'reported errors'
$global:resourceTestMode = 'exit-failure'
$global:resourceTestCalls.Clear()
Expect-Failure { & $restore -SqPack $client -OutputRoot $out } 'exit 7'
Assert ($global:resourceTestCalls.Count -eq 1) 'A failing executable must stop the pipeline.'
$global:resourceTestMode = 'ok'

Expect-Failure { & $restore -SqPack (Join-Path $testRoot 'missing') -OutputRoot $out -CheckOnly } 'not found'
Expect-Failure { & $restore -SqPack $client -OutputRoot (Join-Path $client 'output') -CheckOnly } 'outside the game'
Expect-Failure { & $restore -SqPack $client -TraditionalSqPack $tcClient -OutputRoot (Join-Path $tcClient 'output') -CheckOnly } 'outside the game'
Expect-Failure { & $restore -SqPack $client -Task NpcTable -OutputRoot (Join-Path $testRoot 'no-catalog') } 'Build the voice library first'
$global:resourceTestSdk = '9.0.100 [mock]'
Expect-Failure { & $restore -SqPack $client -OutputRoot $out -CheckOnly } '.NET 10 SDK'
$global:resourceTestSdk = '10.0.100 [mock]'

$global:resourceTestCalls.Clear()
& $restore -SqPack $client -OutputRoot $out -Task NpcTable
& $restore -SqPack $client -OutputRoot $out -Task MatchNames
Assert ($global:resourceTestCalls.Count -eq 2 -and $global:resourceTestCalls[0][4] -eq '--npc-table' -and $global:resourceTestCalls[1][4] -eq '--match-names') 'Partial rebuild dispatch failed.'
Write-Host 'PASS: resource tool preflight, paths, rescan, optional indexes, partial rebuilds and failure propagation.'
