import os
import sys
import time
import re
import socket
import subprocess
import threading
import webbrowser

# Garante suporte a UTF-8 no terminal
if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

BASE_DIR = os.path.dirname(os.path.abspath(__file__))

# Carrega .env se existir
env_file = os.path.join(BASE_DIR, ".env")
if os.path.exists(env_file):
    with open(env_file, "r", encoding="utf-8") as f:
        for line in f:
            if "=" in line and not line.startswith("#"):
                k, v = line.strip().split("=", 1)
                os.environ[k.strip()] = v.strip().strip('"').strip("'")

ENABLE_CONDO_P2P = os.environ.get("ENABLE_CONDO_P2P", "0") == "1"

def get_local_ip():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "127.0.0.1"

def copy_to_clipboard(text):
    try:
        proc = subprocess.Popen(["clip"], stdin=subprocess.PIPE, text=True)
        proc.communicate(input=text)
    except Exception:
        pass

def start_p2p_bridge():
    bridge_script = os.path.join(BASE_DIR, "p2p_bridge.ps1")
    if not os.path.exists(bridge_script):
        print(f"[P2P Bridge] Script {bridge_script} nao encontrado.")
        return None
    try:
        proc = subprocess.Popen(
            ["powershell", "-ExecutionPolicy", "Bypass", "-File", bridge_script],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1
        )
        print("[P2P Bridge] Inicializando ponte P2P para as câmeras do condomínio...")
        start = time.time()
        while time.time() - start < 10:
            line = proc.stdout.readline()
            if not line:
                time.sleep(0.1)
                continue
            if "Porta local mapeada" in line:
                print(f"[P2P Bridge] {line.strip()}")
                break

        # Thread de drenagem de saída para nunca travar o buffer do pipe no Windows
        def _drain_bridge_stdout():
            while proc.poll() is None:
                l = proc.stdout.readline()
                if not l:
                    break
                s = l.strip()
                if s and "P2P Bridge" in s:
                    print(s, flush=True)
        threading.Thread(target=_drain_bridge_stdout, daemon=True).start()
        return proc
    except Exception as e:
        print(f"[P2P Bridge] Erro ao iniciar ponte P2P: {e}")
        return None

def main():
    local_ip = get_local_ip()
    port = 8090
    
    print("\n" + "=" * 70)
    print("[PORTEIRO VISION] CENTRAL DE STREAMING LOCAL (ZERO LATENCIA)")
    print("=" * 70)
    print("DENTRO DE CASA (Zero Latencia):")
    print(f"   Acesso no Computador:            http://127.0.0.1:{port}")
    print(f"   Acesso no Tablet/Celular (Wi-Fi): http://{local_ip}:{port}")
    print("=" * 70 + "\n")

    # Abre o navegador local apenas quando o Flask estiver respondendo com 200 OK
    def _open_browser():
        if os.environ.get("NO_AUTO_BROWSER"):
            return
        for _ in range(40):
            time.sleep(0.5)
            try:
                import urllib.request
                req = urllib.request.urlopen(f"http://127.0.0.1:{port}/api/status", timeout=1)
                if req.status == 200:
                    webbrowser.open(f"http://127.0.0.1:{port}")
                    break
            except Exception:
                pass

    threading.Thread(target=_open_browser, daemon=True).start()

    # Monitor de processos filhos (auto-cura se a ponte cair)
    def _monitor_subprocesses():
        nonlocal bridge_proc
        if not ENABLE_CONDO_P2P:
            return
        while True:
            time.sleep(5)
            if bridge_proc and bridge_proc.poll() is not None:
                print("[Watchdog Hub] Ponte P2P encerrou inesperadamente. Reiniciando...", flush=True)
                bridge_proc = start_p2p_bridge()
    if ENABLE_CONDO_P2P:
        threading.Thread(target=_monitor_subprocesses, daemon=True).start()

    # Importa e roda o app Flask local
    sys.path.insert(0, BASE_DIR)
    from app import app
    
    try:
        app.run(host="0.0.0.0", port=port, debug=False, threaded=True)
    finally:
        print("\nDesligando servidor local...")
        try:
            from go2rtc_manager import stop_go2rtc
            stop_go2rtc()
        except Exception:
            pass

if __name__ == "__main__":
    main()
