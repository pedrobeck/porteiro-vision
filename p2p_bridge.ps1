param (
    [string]$serial = "DAU0007831112"
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[System.IO.Directory]::SetCurrentDirectory('C:\Program Files\Intelbras\SIMNext\SIM Next')

$asm = [System.Reflection.Assembly]::LoadFrom('C:\Program Files\Intelbras\SIMNext\SIM Next\SIMNext.P2PSDK.dll')
$type = $asm.GetType('SIMNext.P2PSDK.VVEyeAdapter')

while ($true) {
    $adapter = $null
    try {
        [Console]::WriteLine("[P2P Bridge] Inicializando VVEyeAdapter para DVR $serial...")
        $adapter = [Activator]::CreateInstance($type, @($null, $null))
        $bound = $adapter.AddSDKPort(10, $serial, [UInt16]554, [UInt16]1553)
        [Console]::WriteLine("[P2P Bridge] Porta local mapeada: $bound (Canal 3 - Portao)")

        # Loop de monitoramento: se o túnel P2P cair, detecta e reinicia imediatamente
        while ($true) {
            Start-Sleep -Seconds 15
            if (-not $adapter.P2pIsActive(10)) {
                [Console]::WriteLine("[P2P Bridge] Conexao P2P inativa, reiniciando adaptador...")
                break
            }
        }
    } catch {
        [Console]::WriteLine("[P2P Bridge] Erro: $_")
    } finally {
        if ($adapter) {
            try { $adapter.Cleanup() } catch {}
        }
    }
    Start-Sleep -Seconds 2
}
