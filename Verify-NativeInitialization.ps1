#Requires -Version 7.0
param(
    [Parameter(Mandatory=$true)][string]$GameAssembly,
    [Parameter(Mandatory=$true)][ValidateSet('Client','Coop','Standalone')][string]$Role,
    [string]$GameRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)
$ErrorActionPreference='Stop'
# Run each role in a separate PowerShell process: the two game binaries have
# the same assembly identity and must not share a loader context during a test.
$managed=Join-Path $GameRoot 'Client/Empyrion_Data/Managed'
$handler=[ResolveEventHandler]{param($sender,$event)
    $name=[Reflection.AssemblyName]::new($event.Name).Name
    $path=Join-Path $managed ($name+'.dll')
    if(Test-Path -LiteralPath $path){return [Reflection.Assembly]::LoadFrom($path)}
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($handler)
try {
    $game=[Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $GameAssembly).Path)
    $mod=[Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'src/bin/Release/net472/ShipWalk.dll'))
    $flags=[Reflection.BindingFlags]'Public,NonPublic,Instance'
    $mapType=$mod.GetType('ShipWalk.Build5150',$true)
    $ctor=$mapType.GetConstructor($flags,$null,[Type[]]@([Reflection.Assembly],[bool]),$null)
    $map=$ctor.Invoke(@($game,($Role -ne 'Client')))
    $native=$mapType.GetField('Native',$flags).GetValue($map)
    $transportType=$mod.GetType('ShipWalk.NativeFrameTransport',$true)
    $transport=$transportType.GetConstructor($flags,$null,[Type[]]@($native.GetType()),$null).Invoke(@($native))
    $travelType=$mod.GetType('ShipWalk.NativeTravelMap',$true)
    $travel=$travelType.GetConstructor($flags,$null,[Type[]]@([Reflection.Assembly],$mapType),$null).Invoke(@($game,$map))
    $profile=$native.GetType().GetProperty('ProfileName',$flags).GetValue($native)
    $expected=if($Role -eq 'Standalone'){'StandalonePlayfield5150'}else{'ClientCoop5150'}
    if($profile -ne $expected){throw 'Wrong native profile selected.'}
    [pscustomobject]@{GameAssembly=$game.Location;Role=$Role;Profile=$profile;MovementBindings=$null-ne $map;TransportBindings=$null-ne $transport;TravelBindings=$null-ne $travel;GameplayExecuted=$false} | ConvertTo-Json
}finally{[AppDomain]::CurrentDomain.remove_AssemblyResolve($handler)}
