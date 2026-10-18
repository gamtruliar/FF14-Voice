function Invoke-InstallCommand([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed (exit $LASTEXITCODE). If the installer requests a restart, restart Windows and double-click Install-FF14Voice.cmd again. Completed downloads/builds will be reused."
    }
}

function Update-InstallPath {
    $paths = @([Environment]::GetEnvironmentVariable('Path', 'Machine'),
        [Environment]::GetEnvironmentVariable('Path', 'User'), $env:PATH,
        (Join-Path $env:LOCALAPPDATA 'Microsoft/WinGet/Links'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft/WindowsApps'))
    $env:PATH = ($paths | Where-Object { $_ }) -join ';'
}

function Test-InstallCommand([string]$Name) {
    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Get-CppBuildTools {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { return $null }
    & $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}

function Test-Dotnet10 {
    if (!(Test-InstallCommand dotnet)) { return $false }
    $sdks = & dotnet --list-sdks
    return $LASTEXITCODE -eq 0 -and [bool]($sdks -match '^10\.')
}

function Install-WingetPackage([string]$Id, [string]$Version = '', [string]$Override = '') {
    $arguments = @('install', '--id', $Id, '--exact', '--source', 'winget', '--silent',
        '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
    if ($Version) { $arguments += @('--version', $Version) }
    if ($Override) { $arguments += @('--override', $Override) }
    Write-Host "Installing $Id. Windows may request administrator approval."
    Invoke-InstallCommand winget $arguments
    Update-InstallPath
}

function Initialize-InstallSource([string]$RepoRoot, [string]$Name, [string]$Url, [string]$Revision, [string]$RequiredFile) {
    $folder = Join-Path $RepoRoot $Name
    if (Test-Path -LiteralPath (Join-Path $folder $RequiredFile)) { return }
    if ((Test-Path -LiteralPath $folder) -and !(Test-Path -LiteralPath (Join-Path $folder '.git')) -and
        @(Get-ChildItem -LiteralPath $folder -Force).Count -gt 0) {
        throw "$Name contains incomplete/unrecognized files. Move it aside and rerun; existing files will not be deleted."
    }
    if (!(Test-Path -LiteralPath (Join-Path $folder '.git'))) { Invoke-InstallCommand git @('init', $folder) }
    $changes = & git -C $folder status --porcelain
    if ($LASTEXITCODE -ne 0 -or $changes) { throw "Cannot restore $Name because it has local changes." }
    Invoke-InstallCommand git @('-C', $folder, 'fetch', '--depth', '1', $Url, $Revision)
    Invoke-InstallCommand git @('-C', $folder, 'checkout', '--detach', $Revision)
    if (!(Test-Path -LiteralPath (Join-Path $folder $RequiredFile))) { throw "Missing $Name/$RequiredFile after download." }
}

function Get-ResourceFingerprint([string]$SqPack) {
    $path = (Resolve-Path -LiteralPath $SqPack).ProviderPath
    if (!(Get-ChildItem -LiteralPath $path -Filter '*.index' -Recurse -File | Select-Object -First 1)) {
        throw 'No game index files found.'
    }
    # Include data archives too: patches can replace audio without changing index length.
    $rows = @(Get-ChildItem -LiteralPath $path -Recurse -File | Where-Object {
        $_.Name -match '\.(index2?|dat[0-9]+)$'
    } | Sort-Object FullName | ForEach-Object {
        '{0}|{1}|{2}' -f $_.FullName, $_.Length, $_.LastWriteTimeUtc.Ticks
    })
    if (!$rows.Count) { throw 'No game index files found.' }
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($path + "`n" + ($rows -join "`n")))).Replace('-', '') }
    finally { $hash.Dispose() }
}

function Test-InstalledResources([string]$Folder) {
    try {
        foreach ($name in @('catalog.json', 'native-voiced-lines.json', 'npc-voice-index.json', 'npc-voices.csv')) {
            $file = Get-Item -LiteralPath (Join-Path $Folder $name) -ErrorAction Stop
            if ($file.PSIsContainer -or $file.Length -eq 0) { return $false }
        }
        $catalog = Get-Content -LiteralPath (Join-Path $Folder 'catalog.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if (!$catalog.Voices -or @($catalog.Errors).Count -gt 0) { return $false }
        $root = [IO.Path]::GetFullPath($Folder).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        foreach ($voice in $catalog.Voices) {
            if (!$voice.References) { return $false }
            foreach ($reference in $voice.References) {
                if (!$reference.RelativePath -or [IO.Path]::IsPathRooted($reference.RelativePath)) { return $false }
                $path = [IO.Path]::GetFullPath((Join-Path $root $reference.RelativePath))
                if (!$path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { return $false }
                if ((Get-Item -LiteralPath $path -ErrorAction Stop).Length -le 44) { return $false }
                $stream = [IO.File]::OpenRead($path)
                try {
                    $header = [byte[]]::new(12)
                    if ($stream.Read($header, 0, 12) -ne 12 -or [Text.Encoding]::ASCII.GetString($header, 0, 4) -ne 'RIFF' -or
                        [Text.Encoding]::ASCII.GetString($header, 8, 4) -ne 'WAVE') { return $false }
                } finally { $stream.Dispose() }
            }
        }
        $native = Get-Content -LiteralPath (Join-Path $Folder 'native-voiced-lines.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($native.Version -ne 1 -or !$native.Lines) { return $false }
        $npcs = @(Get-Content -LiteralPath (Join-Path $Folder 'npc-voice-index.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
        return $npcs.Count -gt 0 -and $null -ne $npcs[0]
    } catch { return $false }
}

function Assert-GgufHardware($Hardware) {
    if (!$Hardware.CudaVersion -or $Hardware.CudaVersion -lt [version]'12.9') {
        throw 'Update the NVIDIA display driver first (nvidia-smi must report CUDA 12.9 or newer), restart Windows, then rerun. The installer does not replace display drivers.'
    }
    if (!$Hardware.Capabilities.Count -or @($Hardware.Capabilities | Where-Object { $_ -lt 7.5 }).Count) {
        throw 'This GGUF build requires NVIDIA Turing (GTX 16 / RTX 20 series) or newer on every visible GPU.'
    }
}

function Initialize-FirstRunSettings([string]$Path) {
    if (Test-Path -LiteralPath $Path) { return }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    # CreateNew prevents overwriting settings if the app starts during installation.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes('{"TranslationEngine":"google"}')
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
}

function Invoke-InstallStage([string]$StatePath, [string]$Fingerprint, [scriptblock]$Validate, [scriptblock]$Action) {
    if (Test-Path -LiteralPath $StatePath) {
        try { $state = Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $state = $null }
        if ($state -and $state.Fingerprint -eq $Fingerprint -and (& $Validate)) {
            Write-Host 'Verified completed resource stage; reusing its output.'
            return
        }
    }
    # Invalidate the success marker before a stage that can partially overwrite output.
    if (Test-Path -LiteralPath $StatePath) { Remove-Item -LiteralPath $StatePath }
    & $Action
    if (!(& $Validate)) { throw 'The installation stage did not produce all required output.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $StatePath) -Force | Out-Null
    @{Fingerprint=$Fingerprint} | ConvertTo-Json | Set-Content -LiteralPath ($StatePath + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($StatePath + '.tmp') -Destination $StatePath -Force
}
