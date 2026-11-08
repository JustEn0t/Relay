param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$relayAllowedRoots = @('src','scripts','installer','tests','docs','assets','.github')
$relayAllowedTop = @('.gitignore','.gitattributes','README.md','LICENSE','PRIVACY.md','SECURITY.md','CHANGELOG.md')
$relayForbidden = @('(?i)\b[0-9]{6,12}:[A-Za-z0-9_-]{35,}\b','(?i)\b(?:ghp|gho|ghu|ghs|github_pat)_[A-Za-z0-9_]{20,}\b','-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----','(?i)C:[\\/]Users[\\/]')
$relayCount = 0
foreach ($relayFile in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
    $relayRelative = $relayFile.FullName.Substring($Root.TrimEnd('\').Length + 1)
    $relayFirst = ($relayRelative -split '[\\/]')[0]
    if ($relayFirst -in @('.git','dist','work','tools')) { continue }
    if ($relayFirst -notin $relayAllowedRoots -and $relayRelative -notin $relayAllowedTop) { throw ('Unreviewed public file: ' + $relayRelative) }
    if ($relayFile.Name -match '(?i)(\.protected|\.bak$|^\.env|\.pdb$|\.exe$|\.dll$|\.zip$|\.log$|last-preview|^preview-)') { throw ('Private/generated file in public tree: ' + $relayRelative) }
    $relayBytes = [IO.File]::ReadAllBytes($relayFile.FullName)
    foreach ($relayEncoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        $relayText = $relayEncoding.GetString($relayBytes)
        foreach ($relayPattern in $relayForbidden) {
            if ([regex]::IsMatch($relayText, $relayPattern)) { throw ('Potential secret or private identifier in: ' + $relayRelative) }
        }
    }
    $relayCount++
}
Write-Output ('PASS: ' + $relayCount + ' public files checked; no forbidden files, token patterns or private identifiers.')
