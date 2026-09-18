param([string]$GameRoot)
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GameRoot 'Client/Empyrion_Data/Managed'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $managed 'Trivial.Mono.Cecil.dll'))
$path = Join-Path $managed 'Assembly-CSharp.dll'
if ((Get-FileHash -LiteralPath $path).Hash -ne 'F7B5C81EB3C4D502E42017AEF0CEE102B6829C04137485241C7F2568DC81B86F') { throw 'Unsupported block-contact fixture source.' }
$a = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
try {
    $type = $a.MainModule.GetType('Assembly-CSharp.OptionsScope')
    $tokens = @(0x06004E54,0x06004EA7)
    $methods = @($type.Methods | Where-Object { $_.MetadataToken.ToInt32() -in $tokens })
    $before = @($methods | ForEach-Object { $_.Body.Instructions | ForEach-Object ToString }) -join "`n"
    foreach ($method in @($type.Methods)) { if ($method -notin $methods) { $null = $type.Methods.Remove($method) } }
    foreach ($field in @($type.Fields)) {
        if ($field.MetadataToken.ToInt32() -notin @(0x04004A5C,0x04004A5F,0x04004A64,0x04004A65,0x04004A66)) { $null = $type.Fields.Remove($field) }
    }
    $type.BaseType = $a.MainModule.TypeSystem.Object
    $type.Interfaces.Clear(); $type.NestedTypes.Clear(); $type.Properties.Clear(); $type.Events.Clear(); $type.CustomAttributes.Clear()
    $a.CustomAttributes.Clear(); $a.SecurityDeclarations.Clear(); $a.MainModule.CustomAttributes.Clear()
    $a.MainModule.GetType('<Module>').Methods.Clear(); $a.MainModule.GetType('<Module>').Fields.Clear()
    foreach ($other in @($a.MainModule.Types)) { if ($other -ne $type -and $other.Name -ne '<Module>') { $null = $a.MainModule.Types.Remove($other) } }
    $a.Name.Name = 'ShipWalk.NativeBlockContactFixture'; $a.MainModule.Name = 'ShipWalk.NativeBlockContactFixture.dll'
    $a.MainModule.Mvid = [guid]::NewGuid()
    $output = Join-Path $PSScriptRoot 'bin/Release/net472/ShipWalk.NativeBlockContactFixture.dll'
    $a.Write($output)
    $check = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($output)
    try {
        $after = @($check.MainModule.GetType('Assembly-CSharp.OptionsScope').Methods | ForEach-Object { $_.Body.Instructions | ForEach-Object ToString }) -join "`n"
        if ($before -cne $after) { throw 'Native spatial conversion IL changed during extraction.' }
    } finally { $check.Dispose() }
    Write-Output 'PASS: native grid point conversion and cluster accessor preserve exact IL in test-only fixture; no engine startup.'
} finally { $a.Dispose() }
