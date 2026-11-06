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
