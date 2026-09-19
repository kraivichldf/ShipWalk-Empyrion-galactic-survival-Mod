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
$managerPath = Join-Path $GameRoot 'DedicatedServer/EmpyrionDedicated_Data/Managed/Assembly-CSharp.dll'
$manager = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($managerPath)
try {
    Assert-Implemented $manager 'Eleon.ModBridge.ApplicationBridgeCommon' 'GetPathFor' 1
    Assert-Implemented $manager 'Eleon.ModBridge.ApplicationBridgeCommon' 'GetStructure' 2
    Assert-Implemented $manager 'Eleon.ModBridge.ApplicationBridgeCommon' 'GetPlayerDataFor' 1
    Assert-Implemented $manager 'Eleon.ModBridge.NetworkBridge' 'SendToPlayfieldServer' 3
} finally { $manager.Dispose() }
foreach ($path in @((Join-Path $GameRoot 'Client/Empyrion_Data/Managed/Assembly-CSharp.dll'), $StandaloneAssembly)) {
    $assembly = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
    try {
        Assert-Implemented $assembly 'Eleon.ModBridge.PlayerBridge' 'get_SteamId' 0
        Assert-Implemented $assembly 'Eleon.ModBridge.NetworkBridge' 'SendToDedicatedServer' 3
        if ($path -ne $StandaloneAssembly) {
            Assert-Implemented $assembly 'Eleon.ModBridge.PlayerBridge' 'Teleport' 3
            Assert-Implemented $assembly 'Eleon.ModBridge.PlayerBridge' 'Teleport' 1
        }
    } finally { $assembly.Dispose() }
}
Write-Output 'PASS: manager global ship/player lookup, save path, inter-server delivery and native client player transfer have implementations; gameplay remains unexecuted.'
