import json
import os
import uuid
import threading
from datetime import datetime

IS_VERCEL = bool(os.environ.get("VERCEL"))
if IS_VERCEL:
    DATA_DIR = "/tmp/data"
else:
    DATA_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data")

CAMERAS_FILE = os.path.join(DATA_DIR, "cameras.json")
SETTINGS_FILE = os.path.join(DATA_DIR, "settings.json")

_lock = threading.Lock()

def _ensure_dir():
    try:
        if not os.path.exists(DATA_DIR):
            os.makedirs(DATA_DIR, exist_ok=True)
    except Exception:
        pass

def load_cameras():
    _ensure_dir()
    with _lock:
        if not os.path.exists(CAMERAS_FILE):
            return []
        try:
            with open(CAMERAS_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception as e:
            print(f"[Storage] Erro ao carregar câmeras: {e}")
            return []

def save_cameras(cameras):
    _ensure_dir()
    with _lock:
        try:
            with open(CAMERAS_FILE, "w", encoding="utf-8") as f:
                json.dump(cameras, f, indent=2, ensure_ascii=False)
            return True
        except Exception as e:
            print(f"[Storage] Erro ao salvar câmeras: {e}")
            return False

def add_camera(cam_data):
    cameras = load_cameras()
    cam_id = str(uuid.uuid4())[:8]
    camera = {
        "id": cam_id,
        "name": cam_data.get("name", f"Câmera {len(cameras) + 1}"),
        "host": cam_data.get("host", "").strip(),
        "port": int(cam_data.get("port", 554)),
        "protocol": cam_data.get("protocol", "rtsp"),
        "brand": cam_data.get("brand", "generic"),
        "username": cam_data.get("username", "admin").strip(),
        "password": cam_data.get("password", "").strip(),
        "channel": int(cam_data.get("channel", 1)),
        "stream_type": cam_data.get("stream_type", "sub"), # 'sub' é mais leve para telas múltiplas
        "custom_url": cam_data.get("custom_url", "").strip(),
        "ptz_enabled": bool(cam_data.get("ptz_enabled", False)),
        "order": len(cameras),
        "created_at": datetime.now().isoformat()
    }
    cameras.append(camera)
    save_cameras(cameras)
    return camera

def update_camera(cam_id, cam_data):
    cameras = load_cameras()
    for idx, c in enumerate(cameras):
        if c["id"] == cam_id:
            for k in ["name", "host", "port", "protocol", "brand", "username", "password", 
                      "channel", "stream_type", "custom_url", "ptz_enabled", "order"]:
                if k in cam_data:
                    c[k] = cam_data[k]
            cameras[idx] = c
            save_cameras(cameras)
            return c
    return None

def delete_camera(cam_id):
    cameras = load_cameras()
    new_cameras = [c for c in cameras if c["id"] != cam_id]
    if len(new_cameras) != len(cameras):
        save_cameras(new_cameras)
        return True
    return False

def get_camera(cam_id):
    cameras = load_cameras()
    for c in cameras:
        if c.get("id") == cam_id or c.get("host") == cam_id:
            return c
    return None

def build_rtsp_url(camera, stream_type_override=None):
    if camera.get("custom_url"):
        return camera["custom_url"]
    
    brand = camera.get("brand", "generic")
    host = camera.get("host", "127.0.0.1")
    port = camera.get("port", 554)
    user = camera.get("username", "admin")
    pwd = camera.get("password", "")
    ch = camera.get("channel", 1)
    stream = stream_type_override or camera.get("stream_type", "sub")
    
    # 0 = main (HD), 1 = sub (SD / fluido)
    stream_idx = 0 if stream == "main" else 1

    if brand == "icsee": # Xiongmai / iCSee
        # Exemplo: rtsp://admin:pwd@192.168.4.41:554/user=admin&pwd=pwd&channel=1&stream=0.sdp
        auth = f"{user}:{pwd}@" if user else ""
        return f"rtsp://{auth}{host}:{port}/user={user}&pwd={pwd}&channel={ch}&stream={stream_idx}.sdp"
    
    elif brand == "intelbras":
        auth = f"{user}:{pwd}@" if user else ""
        subtype = stream_idx
        return f"rtsp://{auth}{host}:{port}/cam/realmonitor?channel={ch}&subtype={subtype}"
    
    elif brand == "tapo":
        auth = f"{user}:{pwd}@" if user else ""
        st = "stream1" if stream_idx == 0 else "stream2"
        return f"rtsp://{auth}{host}:{port}/{st}"
    
    elif brand == "hikvision":
        auth = f"{user}:{pwd}@" if user else ""
        st = f"{ch}01" if stream_idx == 0 else f"{ch}02"
        return f"rtsp://{auth}{host}:{port}/Streaming/Channels/{st}"
    
    else: # Genérico
        auth = f"{user}:{pwd}@" if user else ""
        return f"rtsp://{auth}{host}:{port}/live/ch{stream_idx}"
