import os
import subprocess
import atexit
import time

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
GO2RTC_BIN = os.path.join(BASE_DIR, "go2rtc.exe")
GO2RTC_CONFIG = os.path.join(BASE_DIR, "go2rtc.yaml")

_proc = None
_last_config = ""

def ensure_go2rtc_bin():
    if os.path.exists(GO2RTC_BIN):
        return True
    print("[go2rtc] Baixando binário go2rtc.exe v1.9.15...")
    try:
        import urllib.request
        import zipfile
        import io
        url = "https://github.com/AlexxIT/go2rtc/releases/download/v1.9.15/go2rtc_win64.zip"
        req = urllib.request.Request(url, headers={"User-Agent": "PorteiroCam/1.0"})
        with urllib.request.urlopen(req) as resp:
            data = resp.read()
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            z.extract("go2rtc.exe", BASE_DIR)
        print("[go2rtc] go2rtc.exe baixado e pronto!")
        return True
    except Exception as e:
        print(f"[go2rtc] Erro ao baixar go2rtc.exe: {e}")
        return False

def generate_go2rtc_config(cameras):
    global _last_config
    lines = [
        "api:",
        '  listen: "127.0.0.1:1984"',
        "",
        "rtsp:",
        '  listen: "127.0.0.1:8554"',
        "",
        "streams:"
    ]
    has_streams = False
    for c in cameras:
        if c.get("brand") == "icsee":
            cid = c["id"]
            user = c.get("username", "admin")
            pwd = c.get("password", "")
            host = c.get("host", "127.0.0.1")
            port = 34567

            # Canal 0 (Móvel PTZ)
            lines.append(f'  {cid}_ch0: "dvrip://{user}:{pwd}@{host}:{port}?channel=0"')
            # Canal 1 (Fixa Esquerda)
            lines.append(f'  {cid}_ch1: "dvrip://{user}:{pwd}@{host}:{port}?channel=1"')
            # Canal 2 (Fixa Direita)
            lines.append(f'  {cid}_ch2: "dvrip://{user}:{pwd}@{host}:{port}?channel=2"')
            has_streams = True

    if not has_streams:
        lines.append('  dummy: "ffmpeg:null"')

    new_config = "\n".join(lines) + "\n"
    changed = (new_config != _last_config)
    
    if changed or not os.path.exists(GO2RTC_CONFIG):
        with open(GO2RTC_CONFIG, "w", encoding="utf-8") as f:
            f.write(new_config)
        _last_config = new_config

    return changed

def start_go2rtc(cameras):
    global _proc
    if not ensure_go2rtc_bin():
        print(f"[go2rtc] Binário não encontrado em {GO2RTC_BIN}")
        return False

    changed = generate_go2rtc_config(cameras)

    if _proc and _proc.poll() is None:
        if not changed:
            return True
        print("[go2rtc] Configuração alterada, reiniciando go2rtc...")
        stop_go2rtc()

    try:
        _proc = subprocess.Popen(
            [GO2RTC_BIN, "-config", GO2RTC_CONFIG],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL
        )
        time.sleep(1.2)
        print("[go2rtc] Servidor multi-lente Xiongmai (DVRIP) iniciado com sucesso!")
        return True
    except Exception as e:
        print(f"[go2rtc] Erro ao iniciar: {e}")
        return False

def stop_go2rtc():
    global _proc
    if _proc:
        try:
            _proc.terminate()
            _proc.wait(timeout=2.0)
        except Exception:
            try:
                _proc.kill()
            except Exception:
                pass
        _proc = None
        print("[go2rtc] Servidor multi-lente encerrado.")

atexit.register(stop_go2rtc)
