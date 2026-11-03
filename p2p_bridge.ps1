param (
    [string]$serial = "DAU0007831112"
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[System.IO.Directory]::SetCurrentDirectory('C:\Program Files\Intelbras\SIMNext\SIM Next')

[Console]::WriteLine("[P2P Bridge] Inicializando VVEyeAdapter para DVR $serial...")
$asm = [System.Reflection.Assembly]::LoadFrom('C:\Program Files\Intelbras\SIMNext\SIM Next\SIMNext.P2PSDK.dll')
$type = $asm.GetType('SIMNext.P2PSDK.VVEyeAdapter')
$adapter = [Activator]::CreateInstance($type, @($null, $null))

# Mapeia apenas a porta 1553 para a Câmera 03 - Portão
$bound = $adapter.AddSDKPort(10, $serial, [UInt16]554, [UInt16]1553)
[Console]::WriteLine("[P2P Bridge] Porta local mapeada: $bound (Canal 3 - Portao)")

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
