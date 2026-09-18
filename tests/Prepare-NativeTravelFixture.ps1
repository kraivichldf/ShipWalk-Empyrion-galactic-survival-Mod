param([string]$GameRoot)
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GameRoot 'Client/Empyrion_Data/Managed'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $managed 'Trivial.Mono.Cecil.dll'))
$path = Join-Path $managed 'Assembly-CSharp.dll'
if ((Get-FileHash -LiteralPath $path).Hash -ne 'F7B5C81EB3C4D502E42017AEF0CEE102B6829C04137485241C7F2568DC81B86F') { throw 'Unsupported travel fixture source.' }
$a = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
try {
    $module = $a.MainModule
    $point = $module.LookupToken([int]0x0600539E)
    $getPoint = $module.LookupToken([int]0x06001BE8)
    $seat = $module.LookupToken([int]0x04001B7E)
    $position = $module.LookupToken([int]0x04001B34)
    $recovery = $module.LookupToken([int]0x06002270)
    $recoveryBefore = @($recovery.Body.Instructions | ForEach-Object ToString) -join "`n"
    $baseRecovery = $module.LookupToken([int]0x06001BDE)
    $boundaryFailure = $module.LookupToken([int]0x06001BDC)
    $cancel = $module.LookupToken([int]0x06006B9D)
    $world = $cancel.DeclaringType
    $worldInstance = ($recovery.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldsfld' }).Operand.Resolve()
    $snapshot = $module.GetType('Assembly-CSharp.AspectContext')
    $codecBefore = @($snapshot.Methods | ForEach-Object { $_.Body.Instructions | ForEach-Object ToString }) -join "`n"
    $prefixBefore = @($point.Body.Instructions | Where-Object Offset -LE 0x1B | ForEach-Object ToString) -join "`n"
    if ($point.Body.Instructions[9].OpCode.Code -ne 'Stloc_0' -or $point.Body.Instructions[9].Offset -ne 0x1B) { throw 'Native boundary selection layout changed.' }
    # Preserve the original selection instructions. End immediately after the
    # chosen point; omit Unity world discovery/loading from this offline probe.
    while ($point.Body.Instructions.Count -gt 10) { $point.Body.Instructions.RemoveAt($point.Body.Instructions.Count - 1) }
    while ($point.Body.Variables.Count -gt 1) { $point.Body.Variables.RemoveAt($point.Body.Variables.Count - 1) }
    $point.Body.ExceptionHandlers.Clear(); $point.ReturnType = $position.FieldType
    $point.Name = 'SelectedBoundaryPoint'; $point.IsPrivate = $false; $point.IsPublic = $true
    $il = $point.Body.GetILProcessor(); $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ldloc_0); $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ret)
    $keep = @('<Module>','Assembly-CSharp.MenuOptions','Assembly-CSharp.ViewDictionary','Assembly-CSharp.StreamToken',
        'Assembly-CSharp.AspectContext','Assembly-CSharp.PartitionConverterAssemblyInvoker',
        'Assembly-CSharp.FunctionAttributeNodeCollection','EnumOutOfPlayfieldTypes')
    foreach ($type in @($module.Types | Where-Object FullName -in $keep)) {
        foreach ($method in $type.Methods) { if ($method.HasBody) { $null = $method.Body.Instructions.Count } }
    }
    $a.CustomAttributes.Clear(); $a.SecurityDeclarations.Clear(); $module.CustomAttributes.Clear()
    $module.GetType('<Module>').Methods.Clear(); $module.GetType('<Module>').Fields.Clear()
    foreach ($type in @($module.Types)) { if ($type.FullName -notin $keep) { $null = $module.Types.Remove($type) } }
    foreach ($name in @('MenuOptions','ViewDictionary','StreamToken','FunctionAttributeNodeCollection')) {
        $type = $module.GetType('Assembly-CSharp.' + $name)
        $type.BaseType = if ($name -eq 'ViewDictionary') { $module.GetType('Assembly-CSharp.MenuOptions') } else { $module.TypeSystem.Object }
        foreach ($method in @($type.Methods)) { if ($method -notin @($getPoint,$point,$recovery,$baseRecovery,$boundaryFailure,$cancel)) { $null = $type.Methods.Remove($method) } }
        foreach ($field in @($type.Fields)) { if ($field -notin @($seat,$position,$worldInstance)) { $null = $type.Fields.Remove($field) } }
        $type.Interfaces.Clear(); $type.Properties.Clear(); $type.Events.Clear(); $type.NestedTypes.Clear(); $type.CustomAttributes.Clear()
    }
    # Keep the original player recovery override, including its exact enum
    # branch into ConnectIcon. Replace only engine/transport boundaries with
    # observable counters; this fixture never enters Unity physics/networking.
    $publicStatic = [Trivial.Mono.Cecil.FieldAttributes]::Public -bor [Trivial.Mono.Cecil.FieldAttributes]::Static
    $pending = [Trivial.Mono.Cecil.FieldDefinition]::new('PendingTransfer',$publicStatic,$module.TypeSystem.Boolean)
    $repositions = [Trivial.Mono.Cecil.FieldDefinition]::new('Repositions',$publicStatic,$module.TypeSystem.Int32)
    $world.Fields.Add($pending); $world.Fields.Add($repositions)
    $failure = [Trivial.Mono.Cecil.FieldDefinition]::new('BoundaryFailure',[Trivial.Mono.Cecil.FieldAttributes]::Public,$boundaryFailure.ReturnType)
    $boundaryFailure.DeclaringType.Fields.Add($failure)
    foreach ($method in @($baseRecovery,$boundaryFailure,$cancel)) {
        $method.Body = [Trivial.Mono.Cecil.Cil.MethodBody]::new($method)
        $method.IsPrivate=$false; $method.IsFamily=$false; $method.IsPublic=$true
    }
    $recovery.IsFamily=$false; $recovery.IsPublic=$true
    $il=$cancel.Body.GetILProcessor()
    $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ldc_I4_0); $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Stsfld,$pending); $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ret)
    $il=$baseRecovery.Body.GetILProcessor()
    $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ldsfld,$repositions); $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ldc_I4_1)
    $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Add); $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Stsfld,$repositions)
    $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ldarg_0); $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ldfld,$position); $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ret)
    $il=$boundaryFailure.Body.GetILProcessor()
    $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ldarg_0); $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ldfld,$failure); $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ret)
    $a.Name.Name = 'ShipWalk.NativeTravelFixture'; $module.Name = 'ShipWalk.NativeTravelFixture.dll'; $module.Mvid = [guid]::NewGuid()
    $output = Join-Path $PSScriptRoot 'bin/Release/net472/ShipWalk.NativeTravelFixture.dll'; $a.Write($output)
    $check = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($output)
    try {
        $newPoint = $check.MainModule.GetType('Assembly-CSharp.StreamToken').Methods[0]
        $prefixAfter = @($newPoint.Body.Instructions | Select-Object -First 10 | ForEach-Object ToString) -join "`n"
        $codecAfter = @($check.MainModule.GetType('Assembly-CSharp.AspectContext').Methods | ForEach-Object { $_.Body.Instructions | ForEach-Object ToString }) -join "`n"
        $recoveryAfter = @($check.MainModule.GetType('Assembly-CSharp.ViewDictionary').Methods | Where-Object Name -EQ InsertBuilder | ForEach-Object { $_.Body.Instructions | ForEach-Object ToString }) -join "`n"
        if ($prefixBefore -cne $prefixAfter -or $codecBefore -cne $codecAfter -or $recoveryBefore -cne $recoveryAfter) { throw 'Native point selection, recovery cancellation or snapshot codec instructions changed.' }
    } finally { $check.Dispose() }
    Write-Output 'PASS: original native player/seat boundary selection, player recovery cancellation branch and vessel/passenger/seat snapshot codec retained in offline travel fixture.'
} finally { $a.Dispose() }
