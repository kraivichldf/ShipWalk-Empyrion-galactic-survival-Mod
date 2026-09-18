param([string]$GameRoot)
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GameRoot 'Client/Empyrion_Data/Managed'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $managed 'Trivial.Mono.Cecil.dll'))
$path = Join-Path $managed 'Assembly-CSharp.dll'
if ((Get-FileHash -LiteralPath $path).Hash -ne 'F7B5C81EB3C4D502E42017AEF0CEE102B6829C04137485241C7F2568DC81B86F') { throw 'Unsupported fixture source.' }
$a = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
try {
    $packet = $a.MainModule.GetType('Assembly-CSharp.ServerDictionary')
    $base = $a.MainModule.GetType('Assembly-CSharp.FormStack')
    $factory = $base.NestedTypes | Where-Object Name -EQ LineTable
    function Get-ReceiveIL($module) {
        $form = $module.GetType('Assembly-CSharp.FormStack')
        $pool = $form.NestedTypes | Where-Object Name -EQ LineTable
        $methods = @($module.GetType('Assembly-CSharp.ServerDictionary').Methods | Where-Object Name -in 'ToggleClient','SplitControl') +
            @($form.Methods | Where-Object Name -in 'QuoteReference','get_ClearMethod') +
            @($pool.Methods | Where-Object { $_.Name -eq 'ToggleClient' -or ($_.Name -eq 'SplitControl' -and -not $_.HasGenericParameters) })
        @($methods | ForEach-Object { $_.Body.Instructions | ForEach-Object ToString }) -join "`n"
    }
    $codecBefore = Get-ReceiveIL $a.MainModule
    $keep = @('<Module>','EnumNetPackageId','Assembly-CSharp.ServerDictionary','Assembly-CSharp.FormStack',
        'Assembly-CSharp.BuilderTreeAssemblyInvoker','Assembly-CSharp.AspectAssemblyInvoker',
        'Assembly-CSharp.AspectScopeNodeCollection','Assembly-CSharp.NodeCollectionAssemblyInvoker',
        'Assembly-CSharp.ActivatorResolverAssemblyInvoker','Assembly-CSharp.XmlFileLoader','Assembly-CSharp.StreamToken')
    foreach ($type in @($a.MainModule.Types | Where-Object FullName -in $keep)) {
        foreach ($method in $type.Methods) { if ($method.HasBody) { $null = $method.Body.Instructions.Count } }
    }
    foreach ($method in $factory.Methods) { if ($method.HasBody) { $null = $method.Body.Instructions.Count } }
    $a.CustomAttributes.Clear(); $a.SecurityDeclarations.Clear(); $a.MainModule.CustomAttributes.Clear()
    $a.MainModule.GetType('<Module>').Methods.Clear(); $a.MainModule.GetType('<Module>').Fields.Clear()
    foreach ($type in @($a.MainModule.Types)) { if ($type.FullName -notin $keep) { $null = $a.MainModule.Types.Remove($type) } }
    # Keep the actual byte-ID dispatcher, pool allocator, envelope reader and
    # sender binding. Stub only engine startup, other packet registrations and
    # the encrypted error-string lookup. Never install this test-only DLL.
    $base.Properties.Clear(); $base.Events.Clear()
    foreach ($method in @($base.Methods)) {
        if ($method.Name -notin @('.ctor','ViewDisk','QuoteReference','get_ClearMethod','UpdateClient','SplitControl','ToggleClient','DeployAssembly','ShowComponent','get_CleanActivator')) {
            $null = $base.Methods.Remove($method)
        }
    }
    function Clear-Body($method) {
        $method.Body.Instructions.Clear(); $method.Body.Variables.Clear(); $method.Body.ExceptionHandlers.Clear()
        $method.Body.GetILProcessor()
    }
    $reset = $base.Methods | Where-Object Name -EQ ViewDisk
    $il = Clear-Body $reset; $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ret)
    foreach ($name in @('XmlFileLoader','StreamToken')) {
        $stub = $a.MainModule.GetType('Assembly-CSharp.' + $name)
        $stub.BaseType = $a.MainModule.TypeSystem.Object
        $stub.Methods.Clear(); $stub.Fields.Clear(); $stub.NestedTypes.Clear(); $stub.Interfaces.Clear()
        $stub.Properties.Clear(); $stub.Events.Clear(); $stub.CustomAttributes.Clear()
    }
    $nullHolder = $a.MainModule.GetType('Assembly-CSharp.ActivatorResolverAssemblyInvoker')
    $nullHolder.Methods.Clear()
    $strings = $a.MainModule.GetType('Assembly-CSharp.NodeCollectionAssemblyInvoker')
    $errorString = $strings.Methods | Where-Object { $_.Name -eq 'UpdateClient' -and $_.Parameters.Count -eq 1 }
    foreach ($method in @($strings.Methods)) { if ($method -ne $errorString) { $null = $strings.Methods.Remove($method) } }
    $strings.Fields.Clear()
    $il = Clear-Body $errorString
    $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ldstr, 'Unknown package id {0}')
    $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ret)
    $registry = $a.MainModule.GetType('Assembly-CSharp.AspectScopeNodeCollection')
    $registryInit = $registry.Methods | Where-Object Name -EQ '.cctor'
    $dictionaryConstructors = @($registryInit.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Newobj' })
    $il = Clear-Body $registryInit
    for ($i = 0; $i -lt 2; $i++) {
        $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Newobj, $dictionaryConstructors[$i].Operand)
        $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Stsfld, $registry.Fields[$i])
    }
    $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ret)
    foreach ($method in @($factory.Methods)) {
        if ($method.Name -notin @('.cctor','ToggleClient','SplitControl') -or $method.HasGenericParameters) { $null = $factory.Methods.Remove($method) }
    }
    $init = $factory.Methods | Where-Object Name -EQ '.cctor'
    $il = Clear-Body $init
    foreach ($field in $factory.Fields | Where-Object { $_.FieldType.FullName -in @('Assembly-CSharp.FormStack[][]','System.Int32[]') }) {
        $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ldc_I4, [int]198)
        $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Newarr, $field.FieldType.ElementType)
        $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Stsfld, $field)
    }
    $il.Emit([Trivial.Mono.Cecil.Cil.OpCodes]::Ret)
    $a.Name.Name = 'ShipWalk.NativeEnvelopeFixture'
    $a.MainModule.Name = 'ShipWalk.NativeEnvelopeFixture.dll'
    $a.MainModule.Mvid = [guid]::NewGuid()
    $output = Join-Path $PSScriptRoot 'bin/Release/net472/ShipWalk.NativeEnvelopeFixture.dll'
    $a.Write($output)
    $check = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($output)
    try {
        $codecAfter = Get-ReceiveIL $check.MainModule
        if ($codecBefore -cne $codecAfter) { throw 'Extracted native codec/factory/reader/sender instructions changed.' }
    } finally { $check.Dispose() }
    Write-Output 'PASS: native codec, ID factory, allocator, envelope decoder and sender getter IL preserved; engine startup and unrelated registrations omitted in test-only assembly.'
} finally { $a.Dispose() }
