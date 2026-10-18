$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OneClick-Install.ps1')
$script:checks = 0
function Assert($Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_ }
    Assert ($null -ne $failure -and $failure.ToString() -like "*$Pattern*") "Expected $Pattern, got $failure"
}
$repo = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repo ('artifacts/install-tests/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$state = Join-Path $fixture 'state.json'
$script:actions = 0
$script:valid = $false
$validate = { $script:valid }
$action = { $script:actions++; $script:valid = $true }
Invoke-InstallStage $state 'v1' $validate $action
Assert ($script:actions -eq 1 -and (Test-Path $state)) 'First install must run and record success.'
Invoke-InstallStage $state 'v1' $validate $action
Assert ($script:actions -eq 1) 'Unchanged, validated outputs must skip expensive extraction.'
Invoke-InstallStage $state 'v2' $validate $action
Assert ($script:actions -eq 2) 'A changed game must rebuild.'
$script:valid = $false
Invoke-InstallStage $state 'v2' $validate $action
Assert ($script:actions -eq 3) 'Deleted output must rebuild despite a matching marker.'
Set-Content -LiteralPath $state -Value 'broken JSON'
Invoke-InstallStage $state 'v2' $validate $action
Assert ($script:actions -eq 4) 'Corrupt state must be recoverable.'
Expect-Failure { Invoke-InstallStage $state 'v3' $validate { throw 'interrupted extraction' } } 'interrupted extraction'
Assert (!(Test-Path $state)) 'Failed extraction must invalidate the previous success marker.'
$script:valid = $false
Expect-Failure { Invoke-InstallStage $state 'v3' $validate {} } 'required output'
Assert (!(Test-Path $state)) 'Incomplete output must not be marked successful.'
Invoke-InstallStage $state 'v3' $validate $action
Assert ((Get-Content $state -Raw | ConvertFrom-Json).Fingerprint -eq 'v3') 'Retry after failure must succeed.'

$sqpack = Join-Path $fixture 'game/sqpack/ffxiv'
New-Item -ItemType Directory -Path $sqpack -Force | Out-Null
Set-Content (Join-Path $sqpack '000000.win32.index') 'index'
Set-Content (Join-Path $sqpack '000000.win32.dat0') 'data'
$first = Get-ResourceFingerprint $sqpack
Assert ($first -eq (Get-ResourceFingerprint $sqpack)) 'Unchanged game fingerprint must be stable.'
Add-Content (Join-Path $sqpack '000000.win32.dat0') 'patch'
Assert ($first -ne (Get-ResourceFingerprint $sqpack)) 'Data-only patches must invalidate resources.'
Expect-Failure { Get-ResourceFingerprint (Join-Path $fixture 'empty') } 'does not exist'

$resources = Join-Path $fixture 'resources'
New-Item -ItemType Directory -Path $resources -Force | Out-Null
Set-Content (Join-Path $resources 'catalog.json') '{"Version":1,"Voices":[{"References":[{"RelativePath":"voice.wav"}]}],"Errors":[]}'
Set-Content (Join-Path $resources 'native-voiced-lines.json') '{"Version":1,"Lines":[{"Text":"hello"}]}'
Set-Content (Join-Path $resources 'npc-voice-index.json') '[{"Key":"npc:1"}]'
Set-Content (Join-Path $resources 'npc-voices.csv') "Key,VoiceDescription`nnpc:1,voice"
$wav = [byte[]]::new(100)
[Text.Encoding]::ASCII.GetBytes('RIFF').CopyTo($wav, 0)
[Text.Encoding]::ASCII.GetBytes('WAVE').CopyTo($wav, 8)
[IO.File]::WriteAllBytes((Join-Path $resources 'voice.wav'), $wav)
Assert (Test-InstalledResources $resources) 'Complete resource fixture should be reusable.'
[IO.File]::WriteAllBytes((Join-Path $resources 'voice.wav'), [byte[]]::new(20))
Assert (!(Test-InstalledResources $resources)) 'Truncated reference must invalidate resource stage.'
Remove-Item -LiteralPath (Join-Path $resources 'voice.wav')
Assert (!(Test-InstalledResources $resources)) 'Missing reference must invalidate resource stage.'
[IO.File]::WriteAllBytes((Join-Path $resources 'voice.wav'), [byte[]]::new(100))
Assert (!(Test-InstalledResources $resources)) 'Wrong reference file format must invalidate resource stage.'
[IO.File]::WriteAllBytes((Join-Path $resources 'voice.wav'), $wav)
Set-Content (Join-Path $resources 'native-voiced-lines.json') '{"Version":1,"Lines":[]}'
Assert (!(Test-InstalledResources $resources)) 'Empty native index must not permit a ready install.'
Set-Content (Join-Path $resources 'native-voiced-lines.json') 'broken'
Assert (!(Test-InstalledResources $resources)) 'Corrupt native index must not permit a ready install.'
Set-Content (Join-Path $resources 'native-voiced-lines.json') '{"Version":1,"Lines":[{"Text":"hello"}]}'
Set-Content (Join-Path $resources 'catalog.json') '{"Version":1,"Voices":[{"References":[{"RelativePath":"../outside.wav"}]}],"Errors":[]}'
[IO.File]::WriteAllBytes((Join-Path $fixture 'outside.wav'), [byte[]]::new(100))
Assert (!(Test-InstalledResources $resources)) 'References outside the library must be rejected.'
Set-Content (Join-Path $resources 'catalog.json') '{"Voices":null,"Errors":[]}'
Assert (!(Test-InstalledResources $resources)) 'A null voice list must not count as a populated catalog.'

$settings = Join-Path $fixture 'profile/settings.json'
Initialize-FirstRunSettings $settings
Assert ((Get-Content $settings -Raw | ConvertFrom-Json).TranslationEngine -eq 'google') 'Fresh install must not require an uninstalled local translation model.'
Set-Content $settings '{"TranslationEngine":"ollama","OllamaModel":"my-model","other":"preserve"}'
$before = [IO.File]::ReadAllBytes($settings)
Initialize-FirstRunSettings $settings
Assert ([Convert]::ToBase64String($before) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($settings))) 'Existing settings must remain byte-identical.'
Assert-GgufHardware ([pscustomobject]@{CudaVersion=[version]'12.9'; Capabilities=@(7.5)})
$script:checks++
Expect-Failure { Assert-GgufHardware ([pscustomobject]@{CudaVersion=[version]'12.8'; Capabilities=@(7.5)}) } 'driver first'
Expect-Failure { Assert-GgufHardware ([pscustomobject]@{CudaVersion=[version]'13.2'; Capabilities=@(8.6, 6.1)}) } 'Turing'
Expect-Failure { Assert-GgufHardware ([pscustomobject]@{CudaVersion=$null; Capabilities=@()}) } 'driver first'

# All external commands below are mocks. Nothing is installed or downloaded.
function fail-command { $global:LASTEXITCODE = 3010 }
Expect-Failure { Invoke-InstallCommand fail-command @() } 'restart Windows'
$script:commands = @()
function Invoke-InstallCommand([string]$Command, [string[]]$Arguments) {
    $script:commands += [pscustomobject]@{Command=$Command; Arguments=$Arguments}
}
function Update-InstallPath {}
Install-WingetPackage 'Nvidia.CUDA' '12.9' '-y -gm2 -s -n nvcc_12.9'
$call = $script:commands[-1]
Assert ($call.Command -eq 'winget' -and $call.Arguments -contains '12.9' -and $call.Arguments[-1] -eq '-y -gm2 -s -n nvcc_12.9') 'Version and multiword override must stay separate arguments.'

$source = Join-Path $fixture 'VoxCPM'
New-Item -ItemType Directory -Path $source -Force | Out-Null
Set-Content (Join-Path $source 'uv.lock') 'existing user source'
$beforeCount = $script:commands.Count
Initialize-InstallSource $fixture VoxCPM 'unused' 'unused' 'uv.lock'
Assert ($script:commands.Count -eq $beforeCount) 'Existing usable source must not be overwritten.'
Remove-Item -LiteralPath (Join-Path $source 'uv.lock')
Set-Content (Join-Path $source 'personal-file.txt') 'keep'
Expect-Failure { Initialize-InstallSource $fixture VoxCPM 'unused' 'unused' 'uv.lock' } 'will not be deleted'
Assert ((Get-Content (Join-Path $source 'personal-file.txt')) -eq 'keep') 'Unknown partial folder must be preserved.'

# CheckOnly must not invoke installation, build, extraction or settings initialization.
function winget { throw 'Unexpected package installation' }
function Initialize-FirstRunSettings { throw 'Unexpected settings write' }
function Start-Transcript { throw 'CheckOnly wrote logs' }
& (Join-Path $repo 'Install-FF14Voice.ps1') -CheckOnly
$script:checks++
Write-Host "PASS: $script:checks one-click installer checks. Fixtures: $fixture"
