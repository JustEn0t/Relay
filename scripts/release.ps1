param([string]$InnoCompiler, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$relayRoot = Split-Path $PSScriptRoot -Parent
$relayDist = Join-Path $relayRoot 'dist'
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
if (-not $InnoCompiler) {
    $relayIscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($relayIscc) { $InnoCompiler = $relayIscc.Source }
    else {
        $relayCandidates = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
        $InnoCompiler = $relayCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Install Inno Setup 6 and pass -InnoCompiler path\to\ISCC.exe.' }
& $InnoCompiler ('/DDistDir=' + $relayDist) (Join-Path $relayRoot 'installer\Relay.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$relayPortableFiles = @((Join-Path $relayDist 'Relay.exe'), (Join-Path $relayRoot 'docs\user-guide.txt'), (Join-Path $relayRoot 'LICENSE'))
Compress-Archive -LiteralPath $relayPortableFiles -DestinationPath (Join-Path $relayDist 'RELAY-Portable-2.3.1.zip') -Force
$relayArtifacts = @('RELAY-Setup-2.3.1.exe', 'RELAY-Portable-2.3.1.zip', 'Relay.exe')
$relayHashes = foreach ($relayArtifact in $relayArtifacts) {
    $relayHash = Get-FileHash -LiteralPath (Join-Path $relayDist $relayArtifact) -Algorithm SHA256
    $relayHash.Hash.ToLowerInvariant() + '  ' + $relayArtifact
}
[IO.File]::WriteAllLines((Join-Path $relayDist 'SHA256SUMS.txt'), $relayHashes, (New-Object Text.UTF8Encoding($false)))
Get-ChildItem -LiteralPath $relayDist | Select-Object Name,Length
