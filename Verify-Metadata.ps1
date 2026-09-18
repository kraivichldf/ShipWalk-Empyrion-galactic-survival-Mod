param([string]$GameRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GameRoot 'Client\Empyrion_Data\Managed'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $managed 'Trivial.Mono.Cecil.dll'))
$assemblyPath = Join-Path $managed 'Assembly-CSharp.dll'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\Build5150.cs') -Raw
$expectedHash = [regex]::Match($source, 'const string Hash = "([0-9A-F]+)"').Groups[1].Value
$expectedMvid = [regex]::Match($source, 'const string Mvid = "([0-9a-f-]+)"').Groups[1].Value
if ((Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Assembly hash mismatch.' }
$assembly = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($assemblyPath)
$count = 0
function Get-TypeSignature($type) {
    if ($type.IsGenericParameter) {
        $prefix = if ($type.Type.ToString() -eq 'Type') { '!' } else { '!!' }
        return $prefix + $type.Position
    }
    if ($type.IsGenericInstance) {
        $arguments = @($type.GenericArguments | ForEach-Object { Get-TypeSignature $_ })
        return $type.ElementType.FullName + '<' + ($arguments -join ',') + '>'
    }
    if ($type.IsArray) {
        $suffix = if ($type.IsVector) { '[]' } else { '[rank=' + $type.Rank + ']' }
        return (Get-TypeSignature $type.ElementType) + $suffix
    }
    if ($type.IsByReference) { return (Get-TypeSignature $type.ElementType) + '&' }
    if ($type.IsPointer) { return (Get-TypeSignature $type.ElementType) + '*' }
    return $type.FullName
}
function Get-FieldSignature($field) {
    $signature = $field.FieldType
    $required = @()
    $optional = @()
    while ($signature.IsRequiredModifier -or $signature.IsOptionalModifier) {
        if ($signature.IsRequiredModifier) { $required += Get-TypeSignature $signature.ModifierType }
        else { $optional += Get-TypeSignature $signature.ModifierType }
        $signature = $signature.ElementType
    }
    if ($signature.FullName -match 'modreq\(|modopt\(') { throw "Unsupported nested field modifier: $field" }
    return (Get-TypeSignature $signature) + '|required=' + ($required -join ',') + '|optional=' + ($optional -join ',')
}
$fieldReferences = 0
$modifiedReferences = 0
$genericReferences = 0
$uniqueReferences = @{}
try {
    if ($assembly.MainModule.Mvid.ToString() -ne $expectedMvid) { throw 'MVID mismatch.' }
    $fields = [regex]::Matches($source, 'Field\(module, (0x[0-9A-F]+), "([^"]+)", (?:"([^"]+)"|typeof\(([^)]+)\).FullName)\)')
    foreach ($match in $fields) {
        $token = [Convert]::ToUInt32($match.Groups[1].Value.Substring(2),16)
        $field = $assembly.MainModule.LookupToken([int]$token)
        $type = if ($match.Groups[3].Success) { $match.Groups[3].Value } else { 'UnityEngine.' + $match.Groups[4].Value }
        if ($field.DeclaringType.FullName -ne $match.Groups[2].Value -or $field.FieldType.FullName -ne $type) { throw "Field mismatch: $field" }
        $count++
    }
    $methods = [regex]::Matches($source, 'Method\(module, (0x[0-9A-F]+), "([^"]+)", "([^"]+)"([^;]*)\);')
    foreach ($match in $methods) {
        $token = [Convert]::ToUInt32($match.Groups[1].Value.Substring(2),16)
        $method = $assembly.MainModule.LookupToken([int]$token)
        $parameterMatches = [regex]::Matches($match.Groups[4].Value, 'typeof\(([^)]+)\)|assembly.GetType\("([^"]+)", true\)')
        $parameters = @($parameterMatches | ForEach-Object {
            if ($_.Groups[2].Success) { $_.Groups[2].Value }
            else { switch ($_.Groups[1].Value) { 'float' {'System.Single'} 'bool' {'System.Boolean'} 'byte' {'System.Byte'} 'Vector3' {'UnityEngine.Vector3'} 'Quaternion' {'UnityEngine.Quaternion'} 'Bounds' {'UnityEngine.Bounds'} 'Collision' {'UnityEngine.Collision'} 'Collider' {'UnityEngine.Collider'} default {throw 'Unknown parameter type.'} } }
        })
        if ($method.DeclaringType.FullName -ne $match.Groups[2].Value -or $method.Name -ne $match.Groups[3].Value -or $method.IsStatic -or $method.ReturnType.FullName -ne 'System.Void') { throw "Method mismatch: $method" }
        if ($method.Parameters.Count -ne $parameters.Count) { throw "Parameter count mismatch: $method" }
        for ($i=0; $i -lt $parameters.Count; $i++) { if ($method.Parameters[$i].ParameterType.FullName -ne $parameters[$i]) { throw "Parameter mismatch: $method" } }
        # Audit every local field operand copied by Harmony, not just our direct bindings.
        foreach ($instruction in $method.Body.Instructions) {
            $reference = $instruction.Operand
            if ($reference -isnot [Trivial.Mono.Cecil.FieldReference] -or $reference.DeclaringType.Scope.Name -ne $assembly.MainModule.Name) { continue }
            $owner = $reference.DeclaringType.Resolve()
            $signature = Get-FieldSignature $reference
            $candidates = @($owner.Fields | Where-Object { $_.Name -ceq $reference.Name -and (Get-FieldSignature $_) -ceq $signature })
            if ($candidates.Count -ne 1) { throw "Field signature is not unique in hook $($method.FullName): $reference" }
            $fieldReferences++
            if ($reference.FieldType.ContainsGenericParameter) { $genericReferences++ }
            if ($reference.FieldType.IsRequiredModifier -or $reference.FieldType.IsOptionalModifier) { $modifiedReferences++ }
            $uniqueReferences[($reference.DeclaringType.FullName + '::' + $reference.Name + ':' + $signature)] = $true
        }
        $count++
    }
    if ($fields.Count -ne 49 -or $methods.Count -ne 29) { throw "Mapping coverage changed: fields=$($fields.Count), methods=$($methods.Count). Update this verifier." }
    foreach ($site in @(@(0x060053BC,0x06004EAC,2,'BatchBuildNode','UnityEngine.Bounds'), @(0x06004EC1,0x06004EA7,1,'DetachEmulator','UnityEngine.Vector3'))) {
        $contact = $assembly.MainModule.LookupToken([int]$site[0])
        $convert = $assembly.MainModule.LookupToken([int]$site[1])
        if ($contact.Parameters[0].ParameterType.FullName -ne 'Assembly-CSharp.MenuOptions' -or
            $convert.DeclaringType.FullName -ne 'Assembly-CSharp.OptionsScope' -or $convert.IsStatic -or
            $convert.Name -cne $site[3] -or $convert.ReturnType.FullName -ne $site[4] -or
            $convert.Parameters.Count -ne 1 -or $convert.Parameters[0].ParameterType.FullName -ne $site[4]) {
            throw 'Native block-contact argument or conversion signature changed.'
        }
        $calls = @($contact.Body.Instructions | Where-Object {
            $_.Operand -is [Trivial.Mono.Cecil.MethodReference] -and $_.Operand.FullName -ceq $convert.FullName
        })
        if ($calls.Count -ne $site[2]) { throw 'Native block-contact conversion sites changed.' }
    }
    $contactVisit = $assembly.MainModule.LookupToken([int]0x06004EC1)
    $pointCall = $contactVisit.Body.Instructions | Where-Object {
        $_.Operand -is [Trivial.Mono.Cecil.MethodReference] -and $_.Operand.FullName -ceq 'UnityEngine.Vector3 Assembly-CSharp.OptionsScope::DetachEmulator(UnityEngine.Vector3)'
    }
    $callbacks = @($contactVisit.Body.Instructions | Where-Object {
        $_.Operand -is [Trivial.Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'Assembly-CSharp.ResourceList' -and $_.Operand.Name -in @('SelectDirectory','ExitForm')
    })
    if ($callbacks.Count -ne 2 -or @($callbacks | Where-Object Offset -LE $pointCall.Offset).Count -ne 0) { throw 'Block contact coordinates must be determined before native callbacks.' }
    Write-Output 'PASS: two contact bounds calls and one contact point call receive actor argument 1; conversion completes before native block callbacks.'
    foreach ($site in @(@(0x060045E1,0x04004134,1), @(0x060045E1,0x04004188,2), @(0x06004611,0x040040E1,2))) {
        $method = $assembly.MainModule.LookupToken([int]$site[0])
        $reads = @($method.Body.Instructions | Where-Object {
            $_.OpCode.Code -eq 'Ldfld' -and $_.Operand.MetadataToken.ToInt32() -eq $site[1]
        })
        if ($reads.Count -ne $site[2]) { throw 'Controller restore/braking sites no longer match the executable patch fixtures.' }
    }
    Write-Output 'PASS: 3 controller velocity cache reads and 2 automatic thruster braking decisions match the scoped rewrites.'
    # Recorded 0.3.1 startup failure: both owner parameters are named A. This
    # operand must bind List<!1>, not attempt to resolve a free-standing A.
    $seatMethod = $assembly.MainModule.LookupToken([int]0x0600221D)
    $seatField = ($seatMethod.Body.Instructions | Where-Object Offset -EQ 0x1AE).Operand
    $seatSignature = Get-FieldSignature $seatField
    $seatOwner = $seatField.DeclaringType.Resolve()
    if ($seatSignature -cne 'System.Collections.Generic.List`1<!1>|required=|optional=' -or
        $seatOwner.GenericParameters.Count -ne 2 -or
        $seatOwner.GenericParameters[0].Name -cne $seatOwner.GenericParameters[1].Name -or
        $seatField.DeclaringType.GenericArguments[0].FullName -cne 'System.Int32' -or
        $seatField.DeclaringType.GenericArguments[1].FullName -cne 'Assembly-CSharp.MenuOptions') {
        throw 'Recorded generic field fixture no longer matches native seat-exit IL.'
    }
    Write-Output 'PASS: recorded seat-exit List<!1> operand and duplicate generic parameter names match the executable resolver regression.'
    # Handoff must wrap the complete player override. Base-only finalizers run
    # before derived colliders, camera and native control are restored.
    foreach ($pair in @(@(0x0600221D,0x06002153), @(0x06002153,0x06001DA2), @(0x06001DA2,0x06001C2F))) {
        $outer = $assembly.MainModule.LookupToken([int]$pair[0])
        $inner = $assembly.MainModule.LookupToken([int]$pair[1])
        $baseCalls = @($outer.Body.Instructions | Where-Object {
            $_.Operand -is [Trivial.Mono.Cecil.MethodReference] -and $_.Operand.FullName -ceq $inner.FullName
        })
        if ($baseCalls.Count -ne 1 -or $baseCalls[0].Next.OpCode.Code -eq 'Ret') { throw 'Native seat override chain changed.' }
    }
    $shipUpdate = $assembly.MainModule.LookupToken([int]0x0600233C)
    foreach ($accessor in @('get_isKinematic','set_isKinematic')) {
        $sites = @($shipUpdate.Body.Instructions | Where-Object {
            $_.Operand -is [Trivial.Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -ceq 'UnityEngine.Rigidbody' -and $_.Operand.Name -ceq $accessor
        })
        if ($sites.Count -ne 1) { throw 'Native ship simulation-mode decision changed.' }
    }
    Write-Output 'PASS: complete client/server seat override chain and single ship kinematic decision match handoff hooks.'
    $velocityField = $assembly.MainModule.LookupToken([int]0x04001B43)
    $velocityGetter = $assembly.MainModule.LookupToken([int]0x06001C02)
    $velocityReads = @($velocityGetter.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldfld' -and $_.Operand.FullName -ceq $velocityField.FullName })
    if ($velocityGetter.Name -ne 'DeletePackage' -or $velocityGetter.ReturnType.FullName -ne 'UnityEngine.Vector3' -or $velocityReads.Count -ne 1) { throw 'Native replicated velocity getter mismatch.' }
    $history = $assembly.MainModule.LookupToken([int]0x06001C14)
    $historyStores = @($history.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq 'Ldflda' -and $_.Operand.FullName -ceq $velocityField.FullName
    })
    $historyInterval = @($history.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldc_R4' -and [Math]::Abs($_.Operand - .05) -lt .000001 })
    if ($historyStores.Count -ne 6 -or $historyInterval.Count -lt 3) { throw 'Native velocity history interval/update changed.' }
    $positionGetter = $assembly.MainModule.GetType('Eleon.ModBridge.EntityBridge').Methods | Where-Object Name -EQ 'get_Position'
    $positionReads = @($positionGetter.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq 'Ldfld' -and $_.Operand.FullName -ceq $assembly.MainModule.LookupToken([int]0x04001B34).FullName
    })
    if ($positionReads.Count -ne 1) { throw 'Entity API no longer exposes the mapped native world position.' }
    Write-Output 'PASS: native entity position, replicated velocity getter and 0.05 s history update match the multiplayer adapter.'
    # The root's selector cache and activation write are the only rewritten sites.
    $selector = $assembly.MainModule.LookupToken([int]0x06002367)
    $rootField = $assembly.MainModule.LookupToken([int]0x04001B17)
    $rootReads = 0; $rootWrites = 0
    foreach ($instruction in $selector.Body.Instructions) {
        if ($instruction.OpCode.Code -ne 'Ldarg_0') { continue }
        $field = $instruction.Next
        if ($field.OpCode.Code -ne 'Ldfld' -or $field.Operand.FullName -cne $rootField.FullName) { continue }
        $getter = $field.Next
        if ($getter.OpCode.Code -ne 'Callvirt' -or $getter.Operand.FullName -ne 'UnityEngine.GameObject UnityEngine.Component::get_gameObject()') { continue }
        $call = $getter.Next
        if ($call.OpCode.Code -eq 'Callvirt' -and $call.Operand.FullName -eq 'System.Boolean UnityEngine.GameObject::get_activeSelf()') { $rootReads++ }
        if ($call.OpCode.Code -eq 'Ldloc_1' -and $call.Next.OpCode.Code -eq 'Callvirt' -and $call.Next.Operand.FullName -eq 'System.Void UnityEngine.GameObject::SetActive(System.Boolean)') { $rootWrites++ }
    }
    if ($rootReads -ne 1 -or $rootWrites -ne 1) { throw 'Physics-root activation rewrite sites changed.' }
    $controllerInit = $assembly.MainModule.LookupToken([int]0x0600463E)
    if (-not ($controllerInit.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldfld' -and $_.Operand.FullName -ceq $rootField.FullName })) { throw 'Ship controller no longer uses the mapped physics root.' }
    Write-Output 'PASS: collider selector has one exact physics-root cache read and activation write; ship controller uses the same root.'
    foreach ($bridgeName in @('Eleon.ModBridge.EntityBridge','Eleon.ModBridge.PlayerBridge')) {
        $bridge = $assembly.MainModule.Types | Where-Object FullName -EQ $bridgeName
        $constructors = @($bridge.Methods | Where-Object { $_.IsConstructor -and $_.IsPublic -and -not $_.IsStatic -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'Assembly-CSharp.MenuOptions' })
        if ($constructors.Count -ne 1) { throw "API bridge constructor mismatch: $bridgeName" }
    }
    $entityBridgeType = $assembly.MainModule.GetType('Eleon.ModBridge.EntityBridge')
    $nativeEntityGetter = @($entityBridgeType.Methods | Where-Object { $_.Name -ceq 'get_Entity' -and $_.IsPublic -and -not $_.IsStatic -and $_.Parameters.Count -eq 0 -and $_.ReturnType.FullName -eq 'Assembly-CSharp.MenuOptions' })
    if ($nativeEntityGetter.Count -ne 1) { throw 'Native entity bridge getter mismatch.' }
    $playerBridgeType = $assembly.MainModule.GetType('Eleon.ModBridge.PlayerBridge')
    $currentStructureGetter = $playerBridgeType.Methods | Where-Object Name -EQ 'get_CurrentStructure'
    if ($currentStructureGetter.ReturnType.FullName -ne 'Eleon.Modding.IStructure' -or -not $currentStructureGetter.HasBody) { throw 'Current structure bridge mismatch.' }
    # Reproduce the ownership fact behind the inactive-capsule refusal. Both
    # native references are initialized from the same Physics GameObject.
    $playerInit = $assembly.MainModule.GetType('Assembly-CSharp.ViewDictionary').Methods | Where-Object Name -EQ 'IncreaseSelection'
    $physicsOwners = @()
    foreach ($fieldToken in @(0x04003E05,0x04003E06)) {
        $field = $assembly.MainModule.LookupToken([int]$fieldToken)
        $store = @($playerInit.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Stfld' -and $_.Operand.FullName -ceq $field.FullName })
        if ($store.Count -ne 1 -or $store[0].Previous.Operand.Name -ne 'GetComponent') { throw 'Character physics component initialization changed.' }
        $owner = $store[0].Previous.Previous.Previous
        if ($owner.OpCode.Code -ne 'Ldfld') { throw 'Character component source is no longer an exact field.' }
        $physicsOwners += $owner.Operand.Resolve().MetadataToken.ToInt32()
    }
    if ($physicsOwners[0] -ne 0x04001B17 -or $physicsOwners[1] -ne $physicsOwners[0]) { throw 'Native character body and capsule no longer share the mapped physics object.' }
    Write-Output 'PASS: current-structure/native-entity bridge getters and shared character body/capsule component ownership match.'
    $structureBridgeType = $assembly.MainModule.GetType('Eleon.ModBridge.StructureBridge')
    foreach ($bound in @('min','max')) {
        $getterName = if ($bound -ceq 'min') { 'get_MinPos' } else { 'get_MaxPos' }
        $boundGetter = $structureBridgeType.Methods | Where-Object Name -CEQ $getterName
        if ($boundGetter.ReturnType.FullName -ne 'Eleon.Modding.VectorInt3' -or -not $boundGetter.IsPublic) { throw 'Structure bounds API mismatch.' }
        $nativeBounds = @($boundGetter.Body.Instructions | Where-Object {
            $_.OpCode.Code -eq 'Ldflda' -and $_.Operand.DeclaringType.FullName -eq 'Assembly-CSharp.DiskToken' -and $_.Operand.FieldType.FullName -eq 'UnityEngine.Bounds'
        })
        $faceReads = @($boundGetter.Body.Instructions | Where-Object {
            $_.Operand -is [Trivial.Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'UnityEngine.Bounds' -and $_.Operand.Name -ceq ('get_' + $bound)
        })
        if ($nativeBounds.Count -ne 1 -or $faceReads.Count -ne 1) { throw 'Structure bounds no longer read the native grid envelope.' }
    }
    $toGlobal = $structureBridgeType.Methods | Where-Object Name -CEQ 'StructToGlobalPos'
    if ($toGlobal.ReturnType.FullName -ne 'UnityEngine.Vector3' -or $toGlobal.Parameters.Count -ne 1 -or
        $toGlobal.Parameters[0].ParameterType.FullName -ne 'Eleon.Modding.VectorInt3') { throw 'Structure coordinate API mismatch.' }
    $centerCalls = @($toGlobal.Body.Instructions | Where-Object {
        $_.Operand -is [Trivial.Mono.Cecil.MethodReference] -and $_.Operand.FullName -eq 'UnityEngine.Vector3 Eleon.Modding.VectorInt3::CenterToVector3()'
    })
    $transformCalls = @($toGlobal.Body.Instructions | Where-Object {
        $_.Operand -is [Trivial.Mono.Cecil.MethodReference] -and $_.Operand.FullName -eq 'UnityEngine.Vector3 Assembly-CSharp.OptionsScope::FindGroup(UnityEngine.Vector3)'
    })
    if ($centerCalls.Count -ne 1 -or $transformCalls.Count -ne 1) { throw 'Native block coordinate conversion changed.' }
    Write-Output 'PASS: ship bounds read the native grid envelope and block scale uses the verified structure coordinate conversion.'
    # Root cause: original controller release explicitly clears both velocities.
    $release = $assembly.MainModule.LookupToken([int]0x060045E4)
    foreach ($setter in @('set_velocity','set_angularVelocity')) {
        $writes = @($release.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Callvirt' -and $_.Operand.DeclaringType.FullName -eq 'UnityEngine.Rigidbody' -and $_.Operand.Name -eq $setter })
        if ($writes.Count -ne 1 -or $writes[0].Previous.Operand.Name -ne 'get_zero') { throw "Seat-release zeroing site mismatch: $setter" }
    }
    Write-Output 'PASS: original ship release clears linear/angular motion; API bridge constructors and momentum hook signatures match.'
    $lookMode = $assembly.MainModule.LookupToken([int]0x06001C3F)
    if ($lookMode.DeclaringType.FullName -ne 'Assembly-CSharp.MenuOptions' -or $lookMode.Name -ne 'BuildBookmark' -or
        $lookMode.IsStatic -or $lookMode.ReturnType.FullName -ne 'System.Boolean' -or $lookMode.Parameters.Count -ne 0) { throw 'Native look-mode accessor changed.' }
    foreach ($binding in @(@(0x06001BC4, 0x04001B4C), @(0x06001CE7, 0x04001C0B))) {
        $getter = $assembly.MainModule.LookupToken([int]$binding[0])
        $field = $assembly.MainModule.LookupToken([int]$binding[1])
        $reads = @($getter.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldfld' -and $_.Operand.FullName -ceq $field.FullName })
        if ($getter.ReturnType.FullName -ne 'System.Boolean' -or $reads.Count -ne 1) { throw 'Native climb/swim state accessor changed.' }
    }
    $lookInput = $assembly.MainModule.LookupToken([int]0x06004483)
    $lookField = $assembly.MainModule.LookupToken([int]0x04003DE9)
    $lookStores = @($lookInput.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Stfld' -and $_.Operand.FullName -ceq $lookField.FullName })
    if ($lookStores.Count -ne 1) { throw 'Native look input no longer writes the mapped accumulator.' }
    Write-Output 'PASS: native climb/swim flags, free-flight look mode and look accumulator match the movement adapters.'
    $walkingUpdate = $assembly.MainModule.LookupToken([int]0x06004487)
    $walkingMoves = @($walkingUpdate.Body.Instructions | Where-Object {
        $_.Operand -is [Trivial.Mono.Cecil.MethodReference] -and
        $_.Operand.FullName -ceq 'System.Void UnityEngine.Rigidbody::MoveRotation(UnityEngine.Quaternion)'
    })
    if ($walkingMoves.Count -ne 1) { throw 'Native walking rotation no longer has one request.' }
    $walkingMove = $walkingMoves[0]
    if ($walkingMove.Previous.Operand.Name -ne 'op_Multiply' -or
        $walkingMove.Previous.Previous.Operand.Name -ne 'AngleAxis' -or
        $walkingMove.Next.OpCode.Code -ne 'Ldarg_0' -or
        $walkingMove.Next.Next.OpCode.Code -ne 'Ldarg_0' -or
        $walkingMove.Next.Next.Next.Operand.Name -cne 'captionPosition' -or
        $walkingMove.Next.Next.Next.Next.OpCode.Code -ne 'Stfld' -or
        $walkingMove.Next.Next.Next.Next.Operand.Name -cne 'previousWindow') {
        throw 'Native walking rotation request/consumed-input order changed.'
    }
    Write-Output 'PASS: walking Update computes rotation once then advances consumed yaw; immediate adapter replaces only that request.'
    foreach ($token in @(0x04001B80,0x040071ED,0x040071EE,0x040071EF,0x040014B9)) {
        if ($assembly.MainModule.LookupToken([int]$token).IsStatic) { throw 'Occupied-seat instance field mismatch.' }
    }
    if (-not $assembly.MainModule.LookupToken([int]0x0400147F).IsStatic) { throw 'Block definitions must be static.' }
    foreach ($seatClass in @('Assembly-CSharp.StubManager','Assembly-CSharp.FileManager','Assembly-CSharp.SelectionSettings')) {
        $type = $assembly.MainModule.Types | Where-Object FullName -EQ $seatClass
        if ($null -eq $type -or -not $type.IsSealed) { throw "Seat block class mismatch: $seatClass" }
    }
    $moving = $assembly.MainModule.LookupToken([int]0x06001C04)
    if ($moving.DeclaringType.FullName -ne 'Assembly-CSharp.MenuOptions' -or $moving.Name -ne 'LoadContext' -or $moving.ReturnType.FullName -ne 'System.Boolean' -or $moving.IsStatic -or $moving.Parameters.Count -ne 0) { throw 'Moving-state accessor mismatch.' }
    $seatExit = $assembly.MainModule.LookupToken([int]0x06001C4A)
    $queries = @($seatExit.Body.Instructions | Where-Object { $_.OpCode.Code -in @('Call','Callvirt') -and $_.Operand.FullName -ceq $moving.FullName })
    if ($queries.Count -ne 1 -or $queries[0].Previous.OpCode.Code -ne 'Ldarg_0' -or $queries[0].Next.OpCode.Code -ne 'Stloc_1') { throw 'Seat-exit patch site mismatch.' }
    $seatField = $assembly.MainModule.LookupToken([int]0x04001B7E)
    if (-not ($seatExit.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldfld' -and $_.Operand.FullName -ceq $seatField.FullName })) { throw 'Seat-exit owner check is missing.' }
    Write-Output 'PASS: seat-exit moving query has one verified call site and retains the original seat-owner check.'
    $xref = $assembly.MainModule.LookupToken([int]0x06007470)
    if ($xref.DeclaringType.FullName -ne 'XRefBase' -or $xref.Name -ne 'get_ToggleClient' -or $xref.IsStatic -or $xref.ReturnType.FullName -ne 'UnityEngine.Transform' -or $xref.Parameters.Count -ne 0) { throw 'XRef transform mapping mismatch.' }
    # Both the native ship and player draw from their physics Transform (not
    # Rigidbody.position). Our render hooks must retain these exact paths.
    foreach ($token in @(0x0600222A, 0x0600233F)) {
        $presentation = $assembly.MainModule.LookupToken([int]$token)
        $reads = @($presentation.Body.Instructions | Where-Object {
            $_.OpCode.Code -eq 'Ldfld' -and $_.Operand.MetadataToken.ToInt32() -eq 0x04001B17 -and
            $_.Next.Operand -is [Trivial.Mono.Cecil.MethodReference] -and
            $_.Next.Operand.FullName -ceq 'UnityEngine.Vector3 UnityEngine.Transform::get_position()'
        })
        if ($reads.Count -lt 1) { throw 'Native presentation no longer reads the mapped physics Transform.' }
    }
    $locomotion = $assembly.MainModule.LookupToken([int]0x06001CCD)
    $divides = @($locomotion.Body.Instructions | Where-Object {
        $_.Operand -is [Trivial.Mono.Cecil.MethodReference] -and
        $_.Operand.FullName -ceq 'UnityEngine.Vector3 UnityEngine.Vector3::op_Division(UnityEngine.Vector3,System.Single)'
    })
    if ($divides.Count -ne 1 -or $divides[0].Next.OpCode.Code -ne 'Stloc_0') { throw 'Locomotion sample patch site changed.' }
    foreach ($consumer in @('GenerateBitmap','BatchBuildDatabase')) {
        $uses = @($locomotion.Body.Instructions | Where-Object {
            $_.Operand -is [Trivial.Mono.Cecil.MethodReference] -and $_.Operand.Name -ceq $consumer -and
            $_.Operand.DeclaringType.FullName -ceq 'Assembly-CSharp.ToolbarConverter'
        })
        if ($uses.Count -ne 1 -or $uses[0].Offset -le $divides[0].Offset) { throw 'Locomotion consumer order changed.' }
    }
    Write-Output 'PASS: character/ship presentation Transform reads and the shared native footstep/animation displacement patch site match.'
    $playerBase = $assembly.MainModule.Types | Where-Object FullName -EQ 'Assembly-CSharp.ToolbarConverter'
    $jetpack = $playerBase.Methods | Where-Object Name -EQ 'get_BuildProcess'
    if ($jetpack.ReturnType.FullName -ne 'System.Boolean' -or $jetpack.Parameters.Count -ne 0) { throw 'Jetpack getter mismatch.' }
    Write-Output "PASS: hash, MVID, $count mapped members, XRef and jetpack getter match the installed build (metadata only)."
    if ($fieldReferences -eq 0 -or $modifiedReferences -eq 0) { throw 'Hook field audit missed the obfuscated/volatile field references.' }
    Write-Output "PASS: all $fieldReferences local field operands ($($uniqueReferences.Count) unique signatures; $modifiedReferences with custom modifiers; $genericReferences with generic parameters) in the $($methods.Count) retained methods resolve uniquely by full field signature and parameter position (metadata only)."
} finally { $assembly.Dispose() }
