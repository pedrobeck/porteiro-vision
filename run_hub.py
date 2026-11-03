import os
import sys
import time
import re
import socket
import subprocess
import threading
import webbrowser

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
CLOUDFLARED_BIN = os.path.join(BASE_DIR, "cloudflared.exe")

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

def start_tunnel():
    if not os.path.exists(CLOUDFLARED_BIN):
        print(f"[Aviso] cloudflared.exe nao encontrado em {CLOUDFLARED_BIN}")
        return None, None

    proc = subprocess.Popen(
        [CLOUDFLARED_BIN, "tunnel", "--url", "http://127.0.0.1:8080"],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1
    )

    tunnel_url = None
    start = time.time()
    
    # Le primeiras linhas para extrair URL
    while time.time() - start < 15:
        line = proc.stdout.readline()
        if not line:
            time.sleep(0.1)
            continue
        match = re.search(r"https://[a-zA-Z0-9-]+\.trycloudflare\.com", line)
        if match:
            tunnel_url = match.group(0)
            break
            
    return proc, tunnel_url

def main():
    local_ip = get_local_ip()
    port = 8080
    
    print("\n" + "=" * 68)
    print("🛡️  PORTEIRO VISION - INICIANDO STREAMING HUB & TUNEL CLOUDFLARE")
    print("=" * 68)
    print("[1/3] Conectando ao Cloudflare Tunnel (aguarde alguns segundos)...")
    
    tunnel_proc, tunnel_url = start_tunnel()
    
    if tunnel_url:
        copy_to_clipboard(tunnel_url)
        print("[2/3] Túnel seguro HTTPS estabelecido com sucesso!")
    else:
        print("[2/3] Túnel não pôde ser criado imediatamente. Usando apenas rede local.")
        
    print("[3/3] Servidor de decodificação OpenCV ativo na porta 8080.")
    print("=" * 68)
    print(f"👉 Acesso no seu PC:               http://localhost:{port}")
    print(f"👉 Acesso no Tablet/Celular Wi-Fi: http://{local_ip}:{port}")
    if tunnel_url:
        print(f"🌐 Link Seguro Cloudflare (HTTPS): {tunnel_url}")
        print("   (✓ Link já copiado automaticamente para o seu Ctrl+V!)")
        print("\n💡 Para usar na Vercel (https://porteiro-vision.vercel.app):")
        print("   Basta clicar em 'Hub Local' no topo da tela e colar este link!")
    print("=" * 68 + "\n")

    # Abre o navegador local automaticamente após 1 segundo
    def _open_browser():
        time.sleep(1.5)
        webbrowser.open(f"http://localhost:{port}")
    
    threading.Thread(target=_open_browser, daemon=True).start()

    # Importa e roda o app Flask local
    sys.path.insert(0, BASE_DIR)
    from app import app
    
    try:
        app.run(host="0.0.0.0", port=port, debug=False, threaded=True)
    finally:
        if tunnel_proc:
            try:
                tunnel_proc.terminate()
            except Exception:
                pass

if __name__ == "__main__":
    main()
