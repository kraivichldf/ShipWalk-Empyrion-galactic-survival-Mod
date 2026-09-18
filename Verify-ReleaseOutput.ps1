param([Parameter(Mandatory=$true)][string]$GameRoot)
$ErrorActionPreference = 'Stop'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $GameRoot 'Client/Empyrion_Data/Managed/Trivial.Mono.Cecil.dll'))
$assembly = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $PSScriptRoot 'src/bin/Release/net472/ShipWalk.dll'))
function All-Types($type) {
    $type
    foreach ($nested in $type.NestedTypes) { All-Types $nested }
}
try {
    foreach ($root in $assembly.MainModule.Types) {
        foreach ($type in (All-Types $root)) {
            foreach ($method in $type.Methods) {
                if (-not $method.HasBody) { continue }
                foreach ($instruction in $method.Body.Instructions) {
                    $target = $instruction.Operand
                    if ($target -isnot [Trivial.Mono.Cecil.MethodReference]) { continue }
                    $name = $target.Name; $owner = $target.DeclaringType.FullName
                    if ($name -eq 'ShowGameMessage' -or $owner -eq 'System.IO.StreamWriter' -or $owner -eq 'System.Console' -or
                        ($owner -eq 'ShipWalk.TraceLog' -and $name -in @('Info','Event')) -or
                        ($owner -eq 'System.IO.File' -and $name -match '^(Write|Append|Create)')) {
                        throw "Automatic release output found: $($method.FullName) -> $target"
                    }
                    if ($owner -like 'Eleon*' -and $name -in @('Log','LogWarning','LogError')) {
                        $command = $type.FullName -eq 'ShipWalk.ShipWalkMod' -and $method.Name -eq 'ExecCommand'
                        $reply = $type.FullName -like 'ShipWalk.TraceLog/*'
                        if (-not ($command -or $reply)) { throw "Non-command API log sink: $($method.FullName)" }
                    }
                }
            }
        }
    }
    $tick = $assembly.MainModule.GetType('ShipWalk.PeerDiagnostics').Methods | Where-Object Name -eq 'Tick'
    if (@($tick.Body.Instructions | Where-Object { $_.Operand -is [Trivial.Mono.Cecil.MethodReference] }).Count) {
        throw 'Automatic peer diagnostic work remains in the release.'
    }
} finally { $assembly.Dispose() }
Write-Output 'PASS: quiet release has no popup, automatic trace, CSV/file-write or periodic peer-output calls; explicit console replies remain.'
