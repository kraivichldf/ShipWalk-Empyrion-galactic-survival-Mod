param([string]$GameRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GameRoot 'Client/Empyrion_Data/Managed'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $managed 'Trivial.Mono.Cecil.dll'))
$path = Join-Path $managed 'Assembly-CSharp.dll'
if ((Get-FileHash -LiteralPath $path).Hash -ne 'F7B5C81EB3C4D502E42017AEF0CEE102B6829C04137485241C7F2568DC81B86F') { throw 'Unsupported travel metadata build.' }
$resolver = [Trivial.Mono.Cecil.DefaultAssemblyResolver]::new(); $resolver.AddSearchDirectory($managed)
$rp = [Trivial.Mono.Cecil.ReaderParameters]::new(); $rp.AssemblyResolver = $resolver
$a = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($path, $rp)
function Split-Arguments([string]$text) {
    $depth=0; $start=0; $quoted=$false
    for ($i=0; $i -lt $text.Length; $i++) {
        $ch=$text[$i]
        if ($ch -eq '"') { $quoted = -not $quoted }
        if ($quoted) { continue }
        if ($ch -eq '(') { $depth++ }; if ($ch -eq ')') { $depth-- }
        if ($ch -eq ',' -and $depth -eq 0) { $text.Substring($start,$i-$start).Trim(); $start=$i+1 }
    }
    $text.Substring($start).Trim()
}
function Type-Name([string]$text) {
    switch ($text) {
        'reasonType' { return 'Assembly-CSharp.WindowScope' }
        'effectType' { return 'Assembly-CSharp.DeviceSite' }
        'flagsType' { return 'Assembly-CSharp.PackageService' }
        'managerKind' { return 'Assembly-CSharp.DatabaseToken/VectorOptions' }
        'typeof(List<>).MakeGenericType(T("AspectContext"))' { return 'System.Collections.Generic.List`1<Assembly-CSharp.AspectContext>' }
    }
    if ($text -match '^T\("([^"]+)"\)$') { return 'Assembly-CSharp.' + $Matches[1].Replace('+','/') }
    if ($text -match '^game.GetType\("([^"]+)", true\)$') { return $Matches[1] }
    if ($text -match '^typeof\(([^)]+)\)$') {
        switch ($Matches[1]) {
            'void' { return 'System.Void' }; 'bool' { return 'System.Boolean' }; 'int' { return 'System.Int32' }
            'int[]' { return 'System.Int32[]' }; 'byte[]' { return 'System.Byte[]' }; 'string' { return 'System.String' }
            'float' { return 'System.Single' }; 'ulong' { return 'System.UInt64' }
            'Vector3' { return 'UnityEngine.Vector3' }; 'Quaternion' { return 'UnityEngine.Quaternion' }
        }
    }
    throw "Unrecognized travel binding type: $text"
}
try {
    $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src/NativeTravelMap.cs') -Raw
    $bindings = [regex]::Matches($source, '(?s)\b([MFC])\((0x[0-9A-F]{8}),\s*(.*?)\);')
    foreach ($binding in $bindings) {
        $kind=$binding.Groups[1].Value; $token=[Convert]::ToInt32($binding.Groups[2].Value.Substring(2),16)
        $args=@(Split-Arguments $binding.Groups[3].Value); $native=$a.MainModule.LookupToken($token)
        if ($kind -eq 'F') {
            if ($native.DeclaringType.FullName -cne (Type-Name $args[0]) -or $native.FieldType.FullName -cne (Type-Name $args[1]) -or
                $native.IsStatic -ne ($args.Count -gt 2 -and $args[2] -eq 'true')) { throw "Travel field mismatch: $native" }
        } else {
            $skip=1
            if ($kind -eq 'M') {
                if ($native.DeclaringType.FullName -cne ('Assembly-CSharp.' + $args[0].Trim('"').Replace('+','/')) -or
                    $native.Name -cne $args[1].Trim('"') -or $native.ReturnType.FullName -cne (Type-Name $args[2])) { throw "Travel method mismatch: $native" }
                $skip=3
            } elseif ($native.DeclaringType.FullName -cne (Type-Name $args[0]) -or $native.Name -ne '.ctor') { throw "Travel constructor mismatch: $native" }
            $parameters=@()
            for ($i=$skip; $i -lt $args.Count; $i++) {
                if ($args[$i] -eq 'coordinates') { $parameters += @('Assembly-CSharp.StreamToken','UnityEngine.Vector3','System.Single') }
                else { $parameters += Type-Name $args[$i] }
            }
            if (($native.Parameters.ParameterType.FullName -join '|') -cne ($parameters -join '|')) { throw "Travel parameters mismatch: $native" }
        }
    }
    $tokens = @([regex]::Matches($source,'0x[0-9A-F]{8}'))
    if ($bindings.Count -ne $tokens.Count) { throw 'Travel metadata verifier missed a binding.' }
    $fieldCount=0
    foreach ($token in @(0x0600539E,0x06006B99,0x06006B9E,0x060022C1,0x06003E1D,0x06005AFA,0x06003F1E,0x06001BDC)) {
        $hook = $a.MainModule.LookupToken([int]$token)
        foreach ($instruction in $hook.Body.Instructions) {
            $reference=$instruction.Operand
            if ($reference -isnot [Trivial.Mono.Cecil.FieldReference] -or $reference.DeclaringType.Scope.Name -ne $a.MainModule.Name) { continue }
            $owner=$reference.DeclaringType.Resolve()
            $matches=@($owner.Fields | Where-Object { $_.Name -ceq $reference.Name -and $_.FieldType.FullName -ceq $reference.FieldType.FullName })
            if ($matches.Count -ne 1) { throw "Ambiguous field copied into travel hook: $reference" }
            $fieldCount++
        }
    }
    $boundary=$a.MainModule.LookupToken([int]0x0600539E)
    $request=$a.MainModule.LookupToken([int]0x06006B99)
    foreach ($method in @($boundary,$request)) {
        if (-not ($method.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldfld' -and $_.Operand.MetadataToken.ToInt32() -eq 0x04001B7E })) { throw 'Native seat-only travel branch changed.' }
    }
    $snapshot=$a.MainModule.LookupToken([int]0x06001C4E)
    if (-not ($snapshot.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Stfld' -and $_.Operand.MetadataToken.ToInt32() -eq 0x040055B0 })) { throw 'Vessel snapshot passenger-list binding changed.' }
    $recovery=$a.MainModule.LookupToken([int]0x06001BDC)
    if (@($recovery.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ret' }).Count -ne 8 -or $recovery.Body.ExceptionHandlers.Count -ne 0) { throw 'Boundary recovery return layout changed.' }
    $failure=$a.MainModule.GetType('EnumOutOfPlayfieldTypes')
    foreach ($pair in @(@('None',0),@('Planet_AboveSky',2),@('Space_WithinPlanet',5))) {
        if (($failure.Fields | Where-Object Name -EQ $pair[0]).Constant -ne $pair[1]) { throw 'Native boundary recovery enum changed.' }
    }
    $cancel=$a.MainModule.LookupToken([int]0x06006B9D)
    if (-not ($cancel.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Newobj' -and $_.Operand.DeclaringType.FullName -eq 'Assembly-CSharp.ToolbarInvoker' })) { throw 'Native world-change cancellation route changed.' }
    Write-Output "PASS: $($bindings.Count) native travel bindings, full signatures, $fieldCount hook field operands; player boundary, seat-only request and independent passenger-list sites."
} finally { $a.Dispose(); $resolver.Dispose() }
