param(
    [Parameter(Mandatory=$true)][string]$StandaloneAssembly,
    [string]$GameRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)
$ErrorActionPreference='Stop'
$managed=Join-Path $GameRoot 'Client/Empyrion_Data/Managed'
$profile=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bindings/Standalone5150.json') -Raw | ConvertFrom-Json
$clientPath=Join-Path $managed 'Assembly-CSharp.dll'
$serverPath=(Resolve-Path -LiteralPath $StandaloneAssembly).Path
if((Get-FileHash -LiteralPath $clientPath).Hash -ne $profile.clientSha -or
   (Get-FileHash -LiteralPath $serverPath).Hash -ne $profile.serverSha){throw 'Native profile verification requires the reviewed client and standalone assemblies.'}
$null=[Reflection.Assembly]::LoadFrom((Join-Path $managed 'Trivial.Mono.Cecil.dll'))
$client=[Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($clientPath)
$server=[Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($serverPath)
$reverse=@{}
foreach($p in $profile.types.PSObject.Properties){$reverse[$p.Value.Replace('+','/')]=$p.Name.Replace('+','/')}
function Canon([string]$name){
    [regex]::Replace($name,'[\w`-]+(?:[./+][\w`-]+)*',{param($m) if($reverse.ContainsKey($m.Value)){$reverse[$m.Value]}else{$m.Value}})
}
function Type-Signature($type){
    if($type.IsGenericParameter){return $(if($type.Type.ToString() -eq 'Type'){'!'}else{'!!'})+$type.Position}
    if($type.IsGenericInstance){return $type.ElementType.FullName+'<'+(@($type.GenericArguments | ForEach-Object {Type-Signature $_}) -join ',')+'>'}
    if($type.IsArray){return (Type-Signature $type.ElementType)+$(if($type.IsVector){'[]'}else{'[rank='+$type.Rank+']'})}
    if($type.IsByReference){return (Type-Signature $type.ElementType)+'&'}
    if($type.IsPointer){return (Type-Signature $type.ElementType)+'*'}
    return $type.FullName
}
function Field-Signature($field){
    $type=$field.FieldType;$required=@();$optional=@()
    while($type.IsRequiredModifier -or $type.IsOptionalModifier){
        if($type.IsRequiredModifier){$required+=Type-Signature $type.ModifierType}else{$optional+=Type-Signature $type.ModifierType}
        $type=$type.ElementType
    }
    return (Type-Signature $type)+'|required='+($required -join ',')+'|optional='+($optional -join ',')
}
try {
    if($client.MainModule.Mvid.ToString() -ne $profile.clientMvid -or $server.MainModule.Mvid.ToString() -ne $profile.serverMvid){throw 'Native MVID mismatch.'}
    $mod=[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'src/bin/Release/net472/ShipWalk.dll'))
    $runtime=$mod.GetType('ShipWalk.NativeBuildBindings',$true)
    $flags=[Reflection.BindingFlags]'Static,NonPublic'
    $tokenMap=$runtime.GetMethod('StandaloneToken',$flags)
    $typeMap=$runtime.GetMethod('StandaloneTypeName',$flags)
    $select=$runtime.GetMethod('SelectStandalone',$flags)
    if(-not $select.Invoke($null,@($profile.serverSha,$profile.serverMvid,$true)) -or
       $select.Invoke($null,@($profile.clientSha,$profile.clientMvid,$true))){throw 'Compiled binary selection failed.'}
    $source=(@('Build5150.cs','NativeFrameTransport.cs','NativeTravelMap.cs') | ForEach-Object {Get-Content -LiteralPath (Join-Path $PSScriptRoot ('src/'+$_)) -Raw}) -join "`n"
    $sourceTokens=@([regex]::Matches($source,'0x[0-9A-F]{8}') | ForEach-Object {[Convert]::ToInt32($_.Value.Substring(2),16)} | Sort-Object -Unique)
    if(@(Compare-Object $sourceTokens @($profile.members.token | Sort-Object -Unique)).Count -ne 0){throw 'Standalone mapping does not cover every native binding.'}
    foreach($p in $profile.types.PSObject.Properties){
        $c=$client.MainModule.GetType($p.Name.Replace('+','/'));$s=$server.MainModule.GetType($p.Value.Replace('+','/'))
        if(-not $c -or -not $s -or $c.Attributes -ne $s.Attributes -or $c.IsEnum -ne $s.IsEnum){throw ('Native type mismatch: '+$p.Name)}
        if($typeMap.Invoke($null,@($p.Name)) -cne $p.Value){throw ('Compiled native type mismatch: '+$p.Name)}
        if($c.IsEnum){
            $cv=@($c.Fields | Where-Object HasConstant | ForEach-Object {[string]$_.Constant})
            $sv=@($s.Fields | Where-Object HasConstant | ForEach-Object {[string]$_.Constant})
            if(($cv -join ',') -cne ($sv -join ',')){throw ('Native enum values changed: '+$p.Name)}
        }
    }
    $methodCount=0;$fieldCount=0;$operandCount=0;$operandSignatures=@{}
    foreach($p in $profile.members){
        $c=$client.MainModule.LookupToken([int]$p.token);$s=$server.MainModule.LookupToken([int]$p.serverToken)
        if($tokenMap.Invoke($null,@([int]$p.token)) -ne [int]$p.serverToken){throw 'Compiled native token mismatch.'}
        if($c.Name -cne $p.name -or $s.Name -cne $p.serverName -or
           $c.DeclaringType.FullName -cne $p.owner -or $s.DeclaringType.FullName -cne $p.serverOwner -or
           (Canon $s.DeclaringType.FullName) -cne $c.DeclaringType.FullName -or $c.Attributes -ne $s.Attributes){throw ('Native member mismatch: '+$p.token)}
        if($c -is [Trivial.Mono.Cecil.FieldDefinition]){
            if((Canon $s.FieldType.FullName) -cne $c.FieldType.FullName){throw ('Native field type mismatch: '+$c.FullName)}
            $fieldCount++
        }else{
            if((Canon $s.ReturnType.FullName) -cne $c.ReturnType.FullName -or $c.Parameters.Count -ne $s.Parameters.Count -or
               $c.GenericParameters.Count -ne $s.GenericParameters.Count -or $c.CallingConvention -ne $s.CallingConvention){throw ('Native method signature mismatch: '+$c.FullName)}
            for($i=0;$i -lt $c.Parameters.Count;$i++){
                if((Canon $s.Parameters[$i].ParameterType.FullName) -cne $c.Parameters[$i].ParameterType.FullName -or
                   $s.Parameters[$i].Attributes -ne $c.Parameters[$i].Attributes){throw ('Native parameter mismatch: '+$c.FullName)}
            }
            $co=@($c.Body.Instructions | ForEach-Object {$_.OpCode.Code}) -join ','
            $so=@($s.Body.Instructions | ForEach-Object {$_.OpCode.Code}) -join ','
            if($co -cne $so){throw ('Native instruction layout changed: '+$c.FullName)}
            foreach($instruction in $s.Body.Instructions){
                $reference=$instruction.Operand
                if($reference -isnot [Trivial.Mono.Cecil.FieldReference] -or $reference.DeclaringType.Scope.Name -ne $server.MainModule.Name){continue}
                $operandCount++
                $signature=Field-Signature $reference
                $key=$reference.DeclaringType.FullName+'::'+$reference.Name+':'+$signature
                if($operandSignatures.ContainsKey($key)){continue}
                $owner=$reference.DeclaringType.Resolve()
                $candidates=@($owner.Fields | Where-Object {$_.Name -ceq $reference.Name -and (Field-Signature $_) -ceq $signature})
                if($candidates.Count -ne 1){throw ('Ambiguous standalone field operand in '+$s.FullName+': '+$reference)}
                $operandSignatures[$key]=$true
            }
            $methodCount++
        }
    }
    # Public mod bridges are used in addition to the private native members.
    $entity=$server.MainModule.GetType($profile.types.'Assembly-CSharp.MenuOptions')
    foreach($name in @('EntityBridge','PlayerBridge')){
        $bridge=$server.MainModule.GetType('Eleon.ModBridge.'+$name)
        $ctors=@($bridge.Methods | Where-Object {$_.IsConstructor -and $_.IsPublic -and -not $_.IsStatic -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq $entity.FullName})
        if($ctors.Count -ne 1){throw ('Standalone API bridge mismatch: '+$name)}
    }
    $getter=$server.MainModule.GetType('Eleon.ModBridge.EntityBridge').Methods | Where-Object Name -eq 'get_Entity'
    if(-not $getter.IsPublic -or $getter.ReturnType.FullName -ne $entity.FullName){throw 'Standalone native entity bridge getter changed.'}
    foreach($name in @('ModApi','Mif','UnityEngine.CoreModule','UnityEngine.PhysicsModule')){
        $c=$client.MainModule.AssemblyReferences | Where-Object Name -eq $name
        $s=$server.MainModule.AssemblyReferences | Where-Object Name -eq $name
        if($c.FullName -cne $s.FullName){throw ('Standalone dependency identity mismatch: '+$name)}
    }
    $packet=$server.MainModule.GetType($profile.types.'Assembly-CSharp.ServerDictionary')
    $receiveEntry=$profile.members | Where-Object token -eq 0x06003A70
    $receive=$server.MainModule.LookupToken([int]$receiveEntry.serverToken)
    if(@($receive.Body.Instructions | Where-Object {$_.OpCode.Code -notin @('Nop','Ret')}).Count -ne 0){throw 'Standalone gameplay packet handler is no longer inert.'}
    if(($server.MainModule.GetType('EnumNetPackageId').Fields | Where-Object Name -eq ModGameEvent).Constant -ne 139){throw 'Standalone ModGameEvent packet ID changed.'}
    Write-Output "PASS: standalone SHA/MVID, $(@($profile.types.PSObject.Properties).Count) types, $fieldCount fields, $methodCount methods, $operandCount unambiguous field operands ($($operandSignatures.Count) unique), compiled mapping coverage, bridges, dependency identities and ModGameEvent=139."
} finally {$client.Dispose();$server.Dispose()}
