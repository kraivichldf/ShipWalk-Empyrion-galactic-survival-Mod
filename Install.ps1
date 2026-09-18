param([Parameter(Mandatory=$true)][string]$GameRoot, [switch]$ReplaceInstalledWithMultiplayer)
$ErrorActionPreference = 'Stop'
if (-not $ReplaceInstalledWithMultiplayer) { throw 'Back up installed ShipWalk, then explicitly pass -ReplaceInstalledWithMultiplayer to replace it. Never load both versions.' }
$resolvedGame = (Resolve-Path -LiteralPath $GameRoot).Path
$package = Join-Path $PSScriptRoot 'dist\ShipWalk'
$target = [IO.Path]::GetFullPath((Join-Path $resolvedGame 'Content\Mods\ShipWalk'))
$expectedTarget = [IO.Path]::GetFullPath((Join-Path $resolvedGame 'Content\Mods')) + [IO.Path]::DirectorySeparatorChar
if (-not $target.StartsWith($expectedTarget, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid target directory.' }
if ((Test-Path -LiteralPath $target) -and -not (Test-Path -LiteralPath (Join-Path $target '.shipwalk-owned'))) {
    throw 'Existing ShipWalk folder has no ownership marker. Preserve it and choose a different installation.'
}
$assembly = Join-Path $resolvedGame 'Client\Empyrion_Data\Managed\Assembly-CSharp.dll'
$before = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
if ($before -ne 'F7B5C81EB3C4D502E42017AEF0CEE102B6829C04137485241C7F2568DC81B86F') { throw 'Unsupported game build.' }
$manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
$allowed = @('ShipWalk.dll','0Harmony.dll','ShipWalk_Info.yaml','ShipWalk.cfg','Harmony-LICENSE.txt','.shipwalk-owned')
if ($manifest.Count -ne $allowed.Count -or @($manifest.File | Sort-Object -Unique).Count -ne $allowed.Count) { throw 'Invalid package manifest.' }
foreach ($entry in $manifest) {
    if ($entry.File -notin $allowed -or (Get-FileHash -LiteralPath (Join-Path $package $entry.File) -Algorithm SHA256).Hash -ne $entry.SHA256) {
        throw 'Invalid package file or checksum.'
    }
}
$gamePrefix = $resolvedGame.TrimEnd('\') + '\'
$running = Get-Process Empyrion,EmpyrionDedicated,EmpyrionPlayfieldServer -ErrorAction SilentlyContinue | Where-Object {
    -not $_.Path -or $_.Path.StartsWith($gamePrefix, [StringComparison]::OrdinalIgnoreCase)
}
if ($running) { throw 'Exit the game and its playfield/dedicated processes before installing ShipWalk.' }
$null = New-Item -ItemType Directory -Path $target -Force
foreach ($entry in $manifest) {
    $destination = Join-Path $target $entry.File
    if ($entry.File -eq 'ShipWalk.cfg' -and (Test-Path -LiteralPath $destination)) { continue }
    Copy-Item -LiteralPath (Join-Path $package $entry.File) -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entry.SHA256) { throw 'Installed file verification failed.' }
}
if ((Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash -ne $before) { throw 'Official game hash changed.' }
Write-Output "Installed only into $target"
Write-Output 'Clients, the dedicated manager and gameplay playfield workers load this mod at startup. Start them normally; no process was restarted by this script.'
