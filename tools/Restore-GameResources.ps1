<#
.SYNOPSIS
Rebuild ignored FF14 resources from your installed client (read-only).
.EXAMPLE
.\tools\Restore-GameResources.ps1 -SqPack 'D:\Games\FFXIV\game\sqpack'
.EXAMPLE
.\tools\Restore-GameResources.ps1 -SqPack 'D:\Games\FFXIV\game\sqpack' -TraditionalSqPack 'E:\FFXIV TC\game\sqpack'
#>
[CmdletBinding()]
param(
    [string]$SqPack = $env:FF14_SQPACK,
    [string]$TraditionalSqPack,
    [ValidateSet('ja', 'en', 'de', 'fr')][string[]]$DialogueLanguages = @(),
    [ValidateSet('Voices', 'NpcTable', 'MatchNames')][string]$Task = 'Voices',
    [string]$OutputRoot,
    [switch]$CheckOnly,
    [switch]$StrictScan
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = $repoRoot }

function Resolve-SqPack([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Container)) { throw "SqPack directory not found: $Path" }
    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath.TrimEnd('\', '/')
    if (!(Test-Path -LiteralPath (Join-Path $resolved 'ffxiv') -PathType Container) -or
        !(Get-ChildItem -LiteralPath (Join-Path $resolved 'ffxiv') -Filter '*.index' -File | Select-Object -First 1)) {
        throw "Choose the game\sqpack directory containing ffxiv\*.index: $resolved"
    }
    return $resolved
}

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed (exit $LASTEXITCODE). Resources are incomplete; fix the error and rerun." }
}

function Assert-Report([string]$Path, [switch]$AllowErrors) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing build report: $Path" }
    $report = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    if (@($report.Errors).Count -gt 0) {
        if (!$AllowErrors) { throw "Resource build reported errors. Inspect $Path before retrying." }
        Write-Warning "$(@($report.Errors).Count) candidate resources could not be read; only verified clips will be used. See $Path"
    }
    return $report
}

if ([string]::IsNullOrWhiteSpace($SqPack)) {
    if ($CheckOnly) { throw 'Pass -SqPack or set FF14_SQPACK.' }
    $SqPack = Read-Host 'Path to your international FF14 game\sqpack directory'
}
$SqPack = Resolve-SqPack $SqPack.Trim('"')
if ($TraditionalSqPack) {
    if ($Task -ne 'Voices') { throw '-TraditionalSqPack requires -Task Voices.' }
    $TraditionalSqPack = Resolve-SqPack $TraditionalSqPack.Trim('"')
    $DialogueLanguages = @($DialogueLanguages) + @('ja')
}
if ($Task -ne 'Voices' -and $DialogueLanguages.Count -gt 0) { throw '-DialogueLanguages requires -Task Voices.' }
$OutputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot).TrimEnd('\', '/')
foreach ($gamePath in @($SqPack, $TraditionalSqPack) | Where-Object { $_ }) {
    # Refuse output anywhere under the client, including game/sqpack itself.
    $gameRoot = Split-Path -Parent $gamePath
    if ($OutputRoot.Equals($gameRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $OutputRoot.StartsWith($gameRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputRoot must be outside the game directory.'
    }
}
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK and reopen your terminal.' }
$sdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or !($sdks -match '^10\.')) { throw 'The .NET 10 SDK is required (runtime alone is insufficient).' }
if ($Task -eq 'Voices') {
    if (!(Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw 'Install ffmpeg and add its bin directory to PATH.' }
    & ffmpeg -version | Select-Object -First 1 | Write-Host
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg could not start.' }
}
$library = Join-Path $OutputRoot 'data/game-voices'
$scan = Join-Path $OutputRoot 'artifacts/game-voices'
$voiceProject = Join-Path $repoRoot 'tools/FF14Sound.VoiceLibrary'
if ($Task -ne 'Voices' -and !(Test-Path -LiteralPath (Join-Path $library 'catalog.json'))) {
    throw 'Build the voice library first with Restore-GameResources.cmd.'
}
Write-Host "Client: $SqPack"
Write-Host "Output: $OutputRoot"
Write-Host "Task: $Task; dialogue languages: $(($DialogueLanguages | Select-Object -Unique) -join ', ')"
if ($CheckOnly) { Write-Host 'Prerequisites OK. No resources written.'; return }

if ($Task -eq 'NpcTable') {
    Invoke-Dotnet @('run', '--project', $voiceProject, '--', '--npc-table', $SqPack, $library)
} elseif ($Task -eq 'MatchNames') {
    Invoke-Dotnet @('run', '--project', $voiceProject, '--', '--match-names', $SqPack, $library)
} else {
    # Always rescan: a manifest from another installation/version must not be reused.
    Write-Host 'Scanning installed voice resources (this can take a while)...'
    Invoke-Dotnet @('run', '--project', (Join-Path $repoRoot 'tools/FF14Sound.VoiceScan'), '--', $SqPack, $scan)
    $summary = Assert-Report (Join-Path $scan 'scan-summary.json') -AllowErrors:(!$StrictScan)
    if ($summary.VerifiedAudioVariants -eq 0) { throw 'No voice resources found. Check the international client installation.' }
    Invoke-Dotnet @('run', '--project', $voiceProject, '--', $SqPack, (Join-Path $scan 'voice-clips.json'), $library)
    $catalog = Assert-Report (Join-Path $library 'catalog.json')
    if (@($catalog.Voices).Count -eq 0) { throw 'No voice references were built.' }
    foreach ($file in @('native-voiced-lines.json', 'npc-voice-index.json', 'npc-voices.csv', 'npcs-without-matched-voice.csv', 'voice-name-candidates.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $library $file))) { throw "Missing generated resource: $file" }
    }
    foreach ($language in $DialogueLanguages | Select-Object -Unique) {
        $index = Join-Path $OutputRoot "data/dialogue-$language.jsonl"
        Invoke-Dotnet @('run', '--project', $voiceProject, '--', '--dialogue-index', $SqPack, $index, $language)
        $null = Assert-Report ($index + '.report.json')
    }
    if ($TraditionalSqPack) {
        $paired = Join-Path $OutputRoot 'data/dialogue-ja-tc.jsonl'
        Invoke-Dotnet @('run', '--project', $voiceProject, '--', '--traditional-dialogue', (Join-Path $OutputRoot 'data/dialogue-ja.jsonl'), $TraditionalSqPack, $paired)
        $null = Assert-Report ($paired + '.report.json')
    }
}
Write-Host "Resources ready: $library"
Write-Host 'Restart FF14Sound to reload rebuilt resources. For a custom OutputRoot, select its catalog.json in the GUI.'
