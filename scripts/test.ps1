$ErrorActionPreference = 'Stop'
$relayRoot = Split-Path $PSScriptRoot -Parent
$relayWork = Join-Path $relayRoot 'work'
New-Item -ItemType Directory -Path $relayWork -Force | Out-Null
$relayCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$relaySuites = @(
    @{ File = 'RelayPublicCoreChecks.cs'; Main = 'StreamBotChecks' },
    @{ File = 'RelayDeletionChecks.cs'; Main = 'RelayDeletionChecks' },
    @{ File = 'RelaySecurityChecks.cs'; Main = 'RelaySecurityChecks' }
)
Push-Location $relayRoot
try {
    foreach ($relaySuite in $relaySuites) {
        $relayExe = Join-Path $relayWork ($relaySuite.Main + '.exe')
        & $relayCompiler /nologo /codepage:65001 ('/main:' + $relaySuite.Main) ('/out:' + $relayExe) /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll (Join-Path $relayRoot 'src\Core.cs') (Join-Path $relayRoot 'src\Cards.cs') (Join-Path $relayRoot ('tests\' + $relaySuite.File))
        if ($LASTEXITCODE -ne 0) { throw ('Test compilation failed: ' + $relaySuite.Main) }
        & $relayExe
        if ($LASTEXITCODE -ne 0) { throw ('Test failed: ' + $relaySuite.Main) }
    }
} finally { Pop-Location }
