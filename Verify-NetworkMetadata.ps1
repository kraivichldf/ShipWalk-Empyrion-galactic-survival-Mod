param([string]$GameRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GameRoot 'Client/Empyrion_Data/Managed'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $managed 'Trivial.Mono.Cecil.dll'))
$path = Join-Path $managed 'Assembly-CSharp.dll'
if ((Get-FileHash -LiteralPath $path).Hash -ne 'F7B5C81EB3C4D502E42017AEF0CEE102B6829C04137485241C7F2568DC81B86F') { throw 'Unsupported transport build.' }
$a = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
try {
    $fields = @(
        @(0x040054EE,'ControlToken','Assembly-CSharp.ControlToken',$true),
        @(0x0400647E,'FunctionAttributeNodeCollection','Assembly-CSharp.FunctionAttributeNodeCollection',$true),
        @(0x04003636,'ServerDictionary','System.String',$false),
        @(0x0400363C,'ServerDictionary','Assembly-CSharp.ServerDictionary/PartitionTree',$false),
        @(0x040033D7,'XmlFileLoader','System.Int32',$false),
        @(0x040033E3,'XmlFileLoader','Assembly-CSharp.StreamToken',$false),
        @(0x040033E2,'XmlFileLoader','System.Boolean',$false),
        @(0x04001B1D,'MenuOptions','Assembly-CSharp.StreamToken',$false),
        @(0x04003564,'FormStack','Assembly-CSharp.StreamToken',$false),
        @(0x040054F8,'ControlToken','Assembly-CSharp.EditorType[]',$false),
        @(0x04006787,'AspectScopeNodeCollection','System.Collections.Generic.Dictionary`2<System.Int32,System.Type>',$true),
        @(0x04006788,'AspectScopeNodeCollection','System.Collections.Generic.Dictionary`2<System.Type,System.Int32>',$true)
    )
    foreach ($entry in $fields) {
        $field = $a.MainModule.LookupToken([int]$entry[0])
        if ($field.DeclaringType.FullName -cne ('Assembly-CSharp.'+$entry[1]) -or $field.FieldType.FullName -cne $entry[2] -or $field.IsStatic -ne $entry[3]) { throw "Network field changed: $field" }
    }
    $methods = @(
        @(0x06005AE8,'ControlToken','CopyImage','System.Void','Assembly-CSharp.FormStack,System.Boolean'),
        @(0x060037FA,'XmlFileLoader','SaveTemplate','System.Void','Assembly-CSharp.FormStack,System.Int32'),
        @(0x06005AF3,'ControlToken','ClearMethod','Assembly-CSharp.XmlFileLoader','System.Int32'),
        @(0x06006C3F,'FunctionAttributeNodeCollection','ToggleDevice','Assembly-CSharp.MenuOptions','System.Int32'),
        @(0x060038C7,'FormStack','get_ClearMethod','Assembly-CSharp.XmlFileLoader',''),
        @(0x06003A70,'ServerDictionary','DeployAssembly','System.Void',''),
        @(0x06003A6C,'ServerDictionary','get_CleanActivator','System.Int32',''),
        @(0x06003A69,'ServerDictionary','.ctor','System.Void','GameEventType,System.String')
    )
    foreach ($entry in $methods) {
        $method = $a.MainModule.LookupToken([int]$entry[0])
        if ($method.DeclaringType.FullName -cne ('Assembly-CSharp.'+$entry[1]) -or $method.Name -cne $entry[2] -or $method.ReturnType.FullName -cne $entry[3] -or
            ($method.Parameters.ParameterType.FullName -join ',') -cne $entry[4] -or $method.IsStatic) { throw "Network method changed: $method" }
    }
    $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/NativeFrameTransport.cs') -Raw
    if ([regex]::Matches($source,'0x[0-9A-F]{8}').Count -ne ($fields.Count + $methods.Count)) { throw 'Network mapping coverage changed.' }
    if ($a.MainModule.LookupToken([int]0x06003A69).Parameters[0].ParameterType.Scope.Name -cne 'ModApi') { throw 'GameEventType assembly changed.' }
    $process = $a.MainModule.LookupToken([int]0x06003A70)
    if (@($process.Body.Instructions | Where-Object { $_.OpCode.Code -notin @('Ret','Nop') }).Count -ne 0) { throw 'Native mod-event processing is no longer inert in the gameplay assembly.' }
    $decode = $a.MainModule.GetType('Assembly-CSharp.FormStack').Methods | Where-Object Name -EQ QuoteReference
    $registry = $a.MainModule.GetType('Assembly-CSharp.AspectScopeNodeCollection')
    $registryInit = $registry.Methods | Where-Object Name -EQ '.cctor'
    if (@($registryInit.Body.Instructions | Where-Object { $_.OpCode.Code -in @('Ldc_I4','Ldc_I4_S') -and [int]$_.Operand -eq 139 }).Count -ne 0) { throw 'Native packet registration baseline changed.' }
    $packetId = $a.MainModule.GetType('EnumNetPackageId').Fields | Where-Object Name -EQ ModGameEvent
    $packetIdGetter = $a.MainModule.GetType('Assembly-CSharp.ServerDictionary').Methods | Where-Object Name -EQ UpdateClient
    if ($packetId.Constant -ne 139 -or @($packetIdGetter.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldc_I4' -and [int]$_.Operand -eq 139 }).Count -ne 1) { throw 'Native ModGameEvent ID changed.' }
    $factory = $a.MainModule.GetType('Assembly-CSharp.FormStack').NestedTypes | Where-Object Name -EQ LineTable
    $lookup = $factory.Methods | Where-Object { $_.Name -eq 'SplitControl' -and -not $_.HasGenericParameters }
    if (@($lookup.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldsfld' -and $_.Operand.MetadataToken.ToInt32() -eq 0x04006787 }).Count -ne 1) { throw 'Native decoder no longer uses the bound packet registry.' }
    if (@($decode.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Stfld' -and $_.Operand.MetadataToken.ToInt32() -eq 0x04003562 }).Count -ne 1) { throw 'Native sender connection binding changed.' }
    $history = $a.MainModule.LookupToken([int]0x06001C14)
    if (@($history.Body.Instructions | Where-Object { $_.Offset -eq 0x0281 -and $_.OpCode.Code -eq 'Stfld' -and $_.Operand.FullName -eq 'System.Single UnityEngine.Vector3::x' }).Count -ne 1) { throw 'History argument mutation changed.' }
    Write-Output "PASS: $($fields.Count + $methods.Count) native transport bindings; ModGameEvent=139 absent from native gameplay registry; factory map and sender binding; inert handler; history mutation."
} finally { $a.Dispose() }
