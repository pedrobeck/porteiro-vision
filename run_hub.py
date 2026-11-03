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
CLOUDFLARED_BIN = os.path.join(BASE_DIR, "cloudflared.exe")

# Carrega .env se existir
env_file = os.path.join(BASE_DIR, ".env")
if os.path.exists(env_file):
    with open(env_file, "r", encoding="utf-8") as f:
        for line in f:
            if "=" in line and not line.startswith("#"):
                k, v = line.strip().split("=", 1)
                os.environ[k.strip()] = v.strip().strip('"').strip("'")

SUPABASE_URL = os.environ.get("SUPABASE_URL", "https://ravhvhqvvajobgciudsh.supabase.co")
SUPABASE_ANON_KEY = os.environ.get("SUPABASE_ANON_KEY", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InJhdmh2aHF2dmFqb2JnY2l1ZHNoIiwicm9sZSI6ImFub24iLCJpYXQiOjE3OTM3MDU4NTgsImV4cCI6MjEwOTI4MTg1OH0.-8njNYlFg3VcSnEqsb3TZWra5T-hTrakkJvkYPaxL0g")

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

def sync_hub_to_supabase(tunnel_url, is_online=True):
    if not SUPABASE_URL or not SUPABASE_ANON_KEY:
        return
    try:
        import requests
        headers = {
            "apikey": SUPABASE_ANON_KEY,
            "Authorization": f"Bearer {SUPABASE_ANON_KEY}",
            "Content-Type": "application/json",
            "Prefer": "resolution=merge-duplicates"
        }
        payload = {
            "id": "active_hub",
            "tunnel_url": tunnel_url or "",
            "is_online": is_online
        }
        res = requests.post(
            f"{SUPABASE_URL}/rest/v1/hub_sync",
            headers=headers,
            json=payload,
            timeout=6
        )
        if res.status_code in [200, 201]:
            status_desc = "ONLINE" if is_online else "OFFLINE"
            print(f"[HubSync] Sincronizado automaticamente com Supabase: {status_desc}")
    except Exception as e:
        print(f"[HubSync] Erro ao sincronizar com nuvem: {e}")

def sync_cameras_from_supabase():
    try:
        import requests
        import json
        from storage import load_cameras, save_cameras

        headers = {
            "apikey": SUPABASE_ANON_KEY,
            "Authorization": f"Bearer {SUPABASE_ANON_KEY}"
        }
        res = requests.get(f"{SUPABASE_URL}/rest/v1/cameras?select=*", headers=headers, timeout=5)
        if res.status_code == 200:
            cloud_cams = res.json()
            if cloud_cams:
                local_cams = load_cameras()
                cam_map = {c["id"]: c for c in local_cams}
                # Atualiza com IDs da nuvem
                merged = []
                for cc in cloud_cams:
                    cid = cc["id"]
                    existing = cam_map.get(cid, {})
                    host = cc.get("host", existing.get("host", "127.0.0.1"))
                    merged.append({
                        "id": cid,
                        "name": cc.get("name", existing.get("name", "Camera")),
                        "brand": cc.get("brand", existing.get("brand", "generic")),
                        "host": host,
                        "port": cc.get("port", existing.get("port", 554)),
                        "protocol": "rtsp",
                        "username": cc.get("username", existing.get("username", "admin")),
                        "password": cc.get("password", existing.get("password", "")),
                        "channel": existing.get("channel", cc.get("channel", 1)),
                        "stream_type": cc.get("stream_type", existing.get("stream_type", "main")),
                        "custom_url": cc.get("custom_url", existing.get("custom_url", "")),
                        "ptz_enabled": bool(cc.get("ptz_enabled", existing.get("ptz_enabled", False))),
                        "order": len(merged),
                        "created_at": cc.get("created_at", existing.get("created_at", ""))
                    })
                save_cameras(merged)
                print(f"[HubSync] {len(merged)} câmeras sincronizadas com sucesso do Supabase.")
    except Exception as e:
        print(f"[HubSync] Aviso ao sincronizar cameras da nuvem: {e}")

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
        return proc
    except Exception as e:
        print(f"[P2P Bridge] Erro ao iniciar ponte P2P: {e}")
        return None

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
    
    print("\n" + "=" * 70)
    print("[PORTEIRO VISION] CENTRAL HIBRIDA INTELIGENTE (LOCAL + CLOUD)")
    print("=" * 70)
    
    # Sincroniza câmeras do Supabase
    sync_cameras_from_supabase()

    print("[1/4] Inicializando túnel P2P para as câmeras do condomínio...")
    bridge_proc = start_p2p_bridge()

    print("[2/4] Estabelecendo tunel Cloudflare para acesso externo...")
    tunnel_proc, tunnel_url = start_tunnel()
    
    if tunnel_url:
        copy_to_clipboard(tunnel_url)
        print("[3/4] Tunel ativo! Sincronizando com a Vercel via Supabase...")
        sync_hub_to_supabase(tunnel_url, is_online=True)
    else:
        print("[3/4] Tunel nao iniciado. Central funcionara exclusivamente na rede local.")
        
    print("[4/4] Servidor de streaming local ativo na porta 8080.")
    print("=" * 70)
    print("DENTRO DE CASA (Zero Latencia):")
    print(f"   Acesso no Computador:            http://localhost:{port}")
    print(f"   Acesso no Tablet/Celular (Wi-Fi): http://{local_ip}:{port}")
    print("=" * 70)
    print("FORA DE CASA (Na rua / 4G / 5G):")
    print("   Acesse: https://porteiro-vision.vercel.app")
    if tunnel_url:
        print("   Conexao 100% AUTOMATICA! O site reconhece seu PC ligado.")
        print(f"   Link Seguro: {tunnel_url}")
    print("=" * 70 + "\n")

    # Abre o navegador local automaticamente apos 1.5s
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
        print("\nDesligando servidor... Notificando nuvem...")
        sync_hub_to_supabase("", is_online=False)
        if tunnel_proc:
            try:
                tunnel_proc.terminate()
            except Exception:
                pass
        if bridge_proc:
            try:
                bridge_proc.terminate()
            except Exception:
                pass

if __name__ == "__main__":
    main()
