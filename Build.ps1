#Requires -Version 7.0
<#
.SYNOPSIS
Build, verify and package ShipWalk for Empyrion 1.19.2 build 5150.
.DESCRIPTION
Requires PowerShell 7, the .NET SDK, and your own supported client and dedicated
playfield game assemblies. Game binaries and generated native test fixtures are
not distributed in this repository. Run Get-Help ./Build.ps1 -Examples for usage.
.EXAMPLE
./Build.ps1 -GameRoot 'C:\Games\Empyrion' -StandaloneAssembly 'C:\Server\EmpyrionPlayfieldServer_Data\Managed\Assembly-CSharp.dll'
#>
param([Parameter(Mandatory=$true)][string]$GameRoot, [Parameter(Mandatory=$true)][string]$StandaloneAssembly)
$ErrorActionPreference = 'Stop'
if (-not $StandaloneAssembly -or -not (Test-Path -LiteralPath $StandaloneAssembly)) { throw 'Pass -StandaloneAssembly with the reviewed standalone playfield Assembly-CSharp.dll; both native variants must be verified before packaging.' }
$resolvedGame = (Resolve-Path -LiteralPath $GameRoot).Path.TrimEnd('\') + '\'
$expected = 'F7B5C81EB3C4D502E42017AEF0CEE102B6829C04137485241C7F2568DC81B86F'
$gameAssembly = Join-Path $resolvedGame 'Client\Empyrion_Data\Managed\Assembly-CSharp.dll'
$before = (Get-FileHash -LiteralPath $gameAssembly -Algorithm SHA256).Hash
if ($before -ne $expected) { throw 'Unsupported game build; inspect it before updating the compatibility map.' }

& dotnet build (Join-Path $PSScriptRoot 'tests\ShipWalk.Tests.csproj') -c Release --nologo "-p:GameRoot=$resolvedGame" -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& (Join-Path $PSScriptRoot 'tests\Prepare-NativeEnvelopeFixture.ps1') -GameRoot $resolvedGame
& (Join-Path $PSScriptRoot 'tests\Prepare-NativeBlockContactFixture.ps1') -GameRoot $resolvedGame
& (Join-Path $PSScriptRoot 'tests\Prepare-NativeTravelFixture.ps1') -GameRoot $resolvedGame
& (Join-Path $PSScriptRoot 'tests\bin\Release\net472\ShipWalk.Tests.exe') $resolvedGame
if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
& (Join-Path $PSScriptRoot 'Verify-Metadata.ps1') -GameRoot $resolvedGame
& (Join-Path $PSScriptRoot 'Verify-NetworkMetadata.ps1') -GameRoot $resolvedGame
& (Join-Path $PSScriptRoot 'Verify-TravelMetadata.ps1') -GameRoot $resolvedGame
& (Join-Path $PSScriptRoot 'Verify-StandaloneMetadata.ps1') -GameRoot $resolvedGame -StandaloneAssembly $StandaloneAssembly
$hostExecutable = Join-Path $PSHOME 'pwsh.exe'
foreach ($role in @('Client','Coop','Standalone')) {
    $nativeAssembly = if ($role -eq 'Standalone') { $StandaloneAssembly } else { $gameAssembly }
    & $hostExecutable -NoProfile -File (Join-Path $PSScriptRoot 'Verify-NativeInitialization.ps1') -GameRoot $resolvedGame -GameAssembly $nativeAssembly -Role $role
    if ($LASTEXITCODE -ne 0) { throw "Native binding initialization failed for $role." }
}

$package = Join-Path $PSScriptRoot 'dist\ShipWalk'
$projectXml = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\ShipWalk.csproj') -Raw)
$version = $projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$null = New-Item -ItemType Directory -Path $package -Force
foreach ($name in @('ShipWalk.dll', '0Harmony.dll')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "src\bin\Release\net472\$name") -Destination (Join-Path $package $name)
}
foreach ($name in @('ShipWalk_Info.yaml', 'ShipWalk.cfg')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "package\$name") -Destination (Join-Path $package $name)
}
$packagesPath = (& dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the Harmony license.' }
Copy-Item -LiteralPath (Join-Path $packagesPath 'lib.harmony\2.3.6\LICENSE') -Destination (Join-Path $package 'Harmony-LICENSE.txt')
Set-Content -LiteralPath (Join-Path $package '.shipwalk-owned') -Value "ShipWalk $version package" -Encoding ascii
$manifestNames = @('ShipWalk.dll', '0Harmony.dll', 'ShipWalk_Info.yaml', 'ShipWalk.cfg', 'Harmony-LICENSE.txt', '.shipwalk-owned')
$manifest = foreach ($name in $manifestNames) {
    [pscustomobject]@{ File = $name; SHA256 = (Get-FileHash -LiteralPath (Join-Path $package $name) -Algorithm SHA256).Hash }
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding utf8
$archive = Join-Path $PSScriptRoot "dist\ShipWalk-Multiplayer-$version-build5150.zip"
# Use an explicit file list: never package referenced official assemblies or stale build outputs.
$archiveFiles = @($manifestNames | ForEach-Object { Join-Path $package $_ }) + @((Join-Path $package 'manifest.json'))
Compress-Archive -LiteralPath $archiveFiles -DestinationPath $archive -Force
if ((Get-FileHash -LiteralPath $gameAssembly -Algorithm SHA256).Hash -ne $before) { throw 'Official assembly hash changed during the build.' }
Write-Output "Verified package: $package"
Write-Output "Archive: $archive"
