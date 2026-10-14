param([string]$Distro = 'Ubuntu')
$ErrorActionPreference = 'Stop'
$linuxScript = ((& wsl -d $Distro --exec wslpath -a (Join-Path $PSScriptRoot 'tools/wsl/setup-omni.sh')) -join '').Trim()
if ($LASTEXITCODE -ne 0 -or !$linuxScript) { throw 'Cannot access Ubuntu WSL2.' }
& wsl -d $Distro --exec bash $linuxScript
if ($LASTEXITCODE -ne 0) { throw 'Omni setup failed.' }
