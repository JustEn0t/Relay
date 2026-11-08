param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$relayRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $relayRoot 'dist' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& (Join-Path $relayRoot 'src\build.ps1') -OutputFile (Join-Path (Resolve-Path $OutputDirectory) 'Relay.exe')
if ($LASTEXITCODE -ne 0) { throw 'RELAY compilation failed.' }
