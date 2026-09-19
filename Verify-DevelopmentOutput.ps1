param([Parameter(Mandatory=$true)][string]$GameRoot)
$ErrorActionPreference = 'Stop'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $GameRoot 'Client/Empyrion_Data/Managed/Trivial.Mono.Cecil.dll'))
$assembly = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $PSScriptRoot 'src/bin/Release/net472/ShipWalk.dll'))
function All-Types($type) {
    $type
    foreach ($nested in $type.NestedTypes) { All-Types $nested }
}
$traceCalls = 0
$writerCalls = 0
try {
    foreach ($root in $assembly.MainModule.Types) {
        foreach ($type in (All-Types $root)) {
            foreach ($method in $type.Methods) {
                if (-not $method.HasBody) { continue }
                foreach ($instruction in $method.Body.Instructions) {
                    $target = $instruction.Operand
                    if ($target -isnot [Trivial.Mono.Cecil.MethodReference]) { continue }
                    $name = $target.Name; $owner = $target.DeclaringType.FullName
                    if ($name -eq 'ShowGameMessage') { throw 'Development diagnostics must not add popups.' }
                    if ($owner -eq 'ShipWalk.TraceLog' -and $name -eq 'Info') { $traceCalls++ }
                    if ($owner -eq 'System.IO.StreamWriter') {
                        if ($type.FullName -ne 'ShipWalk.TraceLog') { throw "Unexpected writer: $($method.FullName)" }
                        $writerCalls++
                    }
                    if (($owner -eq 'System.IO.FileStream' -and $name -in @('.ctor','Write','Flush')) -or
                        ($owner -eq 'System.IO.File' -and $name -match '^(Write|Append|Create|Replace|Move)$') -or
                        ($owner -eq 'System.IO.Directory' -and $name -eq 'CreateDirectory')) {
                        if ($type.FullName -ne 'ShipWalk.TraceLog' -and
                            ($type.FullName -ne 'ShipWalk.PassengerStore' -or $method.Name -ne 'Flush')) {
                            throw "File mutation outside diagnostics or passenger storage: $($method.FullName)"
                        }
                    }
                }
            }
        }
    }
    $tick = $assembly.MainModule.GetType('ShipWalk.PeerDiagnostics').Methods | Where-Object Name -eq 'Tick'
    if (-not $traceCalls -or -not $writerCalls -or
        -not @($tick.Body.Instructions | Where-Object { $_.Operand -is [Trivial.Mono.Cecil.MethodReference] }).Count) {
        throw 'Development build is missing its expected diagnostic calls.'
    }
} finally { $assembly.Dispose() }
Write-Output 'PASS: development tracing and periodic peer observations are compiled; no popup sink; disk writes confined to tracing and passenger storage.'
