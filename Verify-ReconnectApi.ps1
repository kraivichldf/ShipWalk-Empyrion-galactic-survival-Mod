#Requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$GameRoot, [Parameter(Mandatory=$true)][string]$StandaloneAssembly)
$ErrorActionPreference = 'Stop'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $GameRoot 'Client/Empyrion_Data/Managed/Trivial.Mono.Cecil.dll'))
function Assert-Implemented($assembly, [string]$typeName, [string]$methodName, [int]$parameters) {
    $type = $assembly.MainModule.GetType($typeName)
    if ($null -eq $type) { throw "Missing reconnect API type: $typeName" }
    $method = @($type.Methods | Where-Object { $_.Name -eq $methodName -and $_.Parameters.Count -eq $parameters })
    if ($method.Count -ne 1 -or -not $method[0].HasBody -or $method[0].Body.Instructions.Count -le 2) {
        throw "Missing or stub reconnect API: ${typeName}::$methodName"
    }
    $calls = @($method[0].Body.Instructions | Where-Object { $_.Operand -is [Trivial.Mono.Cecil.MethodReference] })
    if ($calls.Count -eq 0) { throw "Reconnect API does not reach its native implementation: $methodName" }
}
function Assert-ManagerIdentity($assembly) {
    $method = $assembly.MainModule.GetType('Eleon.ModBridge.ApplicationBridgeCommon').Methods | Where-Object Name -eq 'GetPlayerDataFor'
    $il = @($method.Body.Instructions)
    $recordType = $null
    for ($i = 2; $i -lt $il.Count; $i++) {
        $target = $il[$i].Operand
        if ($target -is [Trivial.Mono.Cecil.MethodReference] -and $target.Parameters.Count -eq 2 -and
            $target.Parameters[0].ParameterType.FullName -eq 'System.Int32' -and $target.Parameters[1].ParameterType.FullName -eq 'System.Boolean' -and
            $il[$i - 2].OpCode.Name -eq 'ldarg.1' -and $il[$i - 1].OpCode.Name -eq 'ldc.i4.0') {
            $recordType = $target.ReturnType.FullName
        }
    }
    $accountFromRecord = $false
    for ($i = 1; $i -lt $il.Count; $i++) {
        $field = $il[$i].Operand; $source = $il[$i - 1].Operand
        if ($il[$i].OpCode.Name -eq 'stfld' -and $field -is [Trivial.Mono.Cecil.FieldReference] -and
            $field.DeclaringType.FullName -eq 'Eleon.Modding.PlayerData' -and $field.Name -eq 'SteamId' -and
            $il[$i - 1].OpCode.Name -eq 'ldfld' -and $source -is [Trivial.Mono.Cecil.FieldReference] -and
            $source.FieldType.FullName -eq 'System.String' -and $source.DeclaringType.FullName -eq $recordType) {
            $accountFromRecord = $true
        }
    }
    if (-not $recordType -or -not $accountFromRecord) { throw 'Manager account identity is not sourced from the requested actor record.' }
}
$managerPath = Join-Path $GameRoot 'DedicatedServer/EmpyrionDedicated_Data/Managed/Assembly-CSharp.dll'
$manager = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($managerPath)
try {
    Assert-Implemented $manager 'Eleon.ModBridge.ApplicationBridgeCommon' 'GetPathFor' 1
    Assert-Implemented $manager 'Eleon.ModBridge.ApplicationBridgeCommon' 'GetStructure' 2
    Assert-Implemented $manager 'Eleon.ModBridge.ApplicationBridgeCommon' 'GetPlayerDataFor' 1
    Assert-ManagerIdentity $manager
    Assert-Implemented $manager 'Eleon.ModBridge.NetworkBridge' 'SendToPlayfieldServer' 3
} finally { $manager.Dispose() }
foreach ($path in @((Join-Path $GameRoot 'Client/Empyrion_Data/Managed/Assembly-CSharp.dll'), $StandaloneAssembly)) {
    $assembly = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
    try {
        # PlayerBridge.SteamId is a process-settings getter in these builds, not
        # a remote actor identity API. Reconnect must use the manager above.
        Assert-Implemented $assembly 'Eleon.ModBridge.NetworkBridge' 'SendToDedicatedServer' 3
        if ($path -ne $StandaloneAssembly) {
            Assert-Implemented $assembly 'Eleon.ModBridge.PlayerBridge' 'Teleport' 3
            Assert-Implemented $assembly 'Eleon.ModBridge.PlayerBridge' 'Teleport' 1
        }
    } finally { $assembly.Dispose() }
}
Write-Output 'PASS: manager resolves account identity from the requested actor record; global ship lookup, save path, inter-server delivery and native player transfer have implementations; gameplay remains unexecuted.'
