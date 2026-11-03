param (
    [string]$serial = "DAU0007831112"
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[System.IO.Directory]::SetCurrentDirectory('C:\Program Files\Intelbras\SIMNext\SIM Next')

[Console]::WriteLine("[P2P Bridge] Inicializando VVEyeAdapter para DVR $serial...")
$asm = [System.Reflection.Assembly]::LoadFrom('C:\Program Files\Intelbras\SIMNext\SIM Next\SIMNext.P2PSDK.dll')
$type = $asm.GetType('SIMNext.P2PSDK.VVEyeAdapter')
$adapter = [Activator]::CreateInstance($type, @($null, $null))

# Mapeia portas dedicadas para cada canal do condomínio (evita colisão de pacotes TCP):
# 1553 -> Canal 3
# 1554 -> Canal 8
# 1555 -> Canal 13
# 1556 -> Canal 15
$portMappings = @(
    @{ DeviceId = 10; Port = 1553; Channel = 3 },
    @{ DeviceId = 11; Port = 1554; Channel = 8 },
    @{ DeviceId = 12; Port = 1555; Channel = 13 },
    @{ DeviceId = 13; Port = 1556; Channel = 15 }
)

foreach ($mapping in $portMappings) {
    $p = $mapping.Port
    $bound = $adapter.AddSDKPort($mapping.DeviceId, $serial, [UInt16]554, [UInt16]$p)
    [Console]::WriteLine("[P2P Bridge] Porta local mapeada: $bound (Canal $($mapping.Channel))")
    Start-Sleep -Milliseconds 300
}

try {
    while ($true) {
        Start-Sleep -Seconds 3600
    }
} finally {
    [Console]::WriteLine("[P2P Bridge] Encerrando tunel...")
    if ($adapter) {
        $adapter.Cleanup()
    }
}
