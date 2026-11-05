import os
import socket
import threading

try:
    import cv2
    HAS_CV2 = True
except Exception:
    cv2 = None
    HAS_CV2 = False

from flask import Flask, render_template, request, jsonify, Response, send_from_directory, redirect
from storage import load_cameras, save_cameras, add_camera, update_camera, delete_camera, get_camera, build_rtsp_url, load_settings, save_settings
from camera_manager import manager, SENSITIVITY_PROFILES
from dvrip_client import DVRIPClient

try:
    from system_monitor import monitor as system_monitor
except Exception:
    system_monitor = None

# Carrega .env se existir
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
STATIC_DIR = os.path.join(BASE_DIR, "static")
TEMPLATE_DIR = os.path.join(BASE_DIR, "templates")

env_file = os.path.join(BASE_DIR, ".env")
if os.path.exists(env_file):
    with open(env_file, "r", encoding="utf-8") as f:
        for line in f:
            if "=" in line and not line.startswith("#"):
                k, v = line.strip().split("=", 1)
                os.environ[k.strip()] = v.strip().strip('"').strip("'")

app = Flask(
    __name__,
    static_folder=STATIC_DIR,
    template_folder=TEMPLATE_DIR
)

@app.after_request
def add_cors_headers(response):
    response.headers["Access-Control-Allow-Origin"] = "*"
    response.headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS"
    response.headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization"
    if request.path.startswith("/static/"):
        response.headers["Cache-Control"] = "no-cache, must-revalidate"
    return response

@app.route("/static/<path:filename>")
def serve_static(filename):
    return send_from_directory(STATIC_DIR, filename)

# Inicia workers de todas as câmeras salvas apenas em ambiente local com OpenCV
if HAS_CV2 and not os.environ.get("VERCEL"):
    manager.start_all()

def get_local_ip():
    if os.environ.get("VERCEL"):
        return "127.0.0.1"
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "127.0.0.1"

LOCAL_IP = get_local_ip()

@app.route("/")
def index():
    return render_template(
        "index.html",
        server_ip=LOCAL_IP
    )

@app.route("/manifest.json")
def manifest():
    return send_from_directory("static", "manifest.json", mimetype="application/manifest+json")

@app.route("/sw.js")
def service_worker():
    return send_from_directory("static", "sw.js", mimetype="application/javascript")

@app.route("/download/PorteiroVision.apk")
@app.route("/download/apk")
def download_apk():
    apk_path = os.path.join(BASE_DIR, "static", "PorteiroVision.apk")
    if os.path.exists(apk_path):
        return send_from_directory("static", "PorteiroVision.apk", as_attachment=True, mimetype="application/vnd.android.package-archive")
    return redirect("https://github.com/pedrobeck/porteiro-vision/releases/download/v1.0.0-apk/PorteiroVision.apk")

@app.route("/api/motion", methods=["GET"])
def api_motion_status():
    return jsonify(manager.get_motion_status())

@app.route("/api/motion/config", methods=["GET", "POST"])
def api_motion_config():
    if request.method == "POST":
        data = request.get_json() or {}
        sensitivity = data.get("sensitivity")
        cam_id = data.get("camera_id")
        if sensitivity in SENSITIVITY_PROFILES:
            manager.set_motion_sensitivity(sensitivity, cam_id)
            if cam_id:
                update_camera(cam_id, {"motion_sensitivity": sensitivity})
            else:
                save_settings({"motion_sensitivity": sensitivity})
                cams = load_cameras()
                for c in cams:
                    c["motion_sensitivity"] = sensitivity
                save_cameras(cams)
            return jsonify({"status": "ok", "sensitivity": sensitivity, "camera_id": cam_id})
        return jsonify({"error": "Perfil de sensibilidade inválido"}), 400

    settings = load_settings()
    profiles = {k: v["label"] for k, v in SENSITIVITY_PROFILES.items()}
    return jsonify({
        "current_sensitivity": settings.get("motion_sensitivity", "medium"),
        "profiles": profiles
    })

# --- API DE CÂMERAS ---

@app.route("/api/cameras", methods=["GET"])
def api_list_cameras():
    cameras = load_cameras()
    expanded = []
    for c in cameras:
        expanded.append(c)
        if c.get("multi_lens") and c.get("brand") == "icsee":
            for sub_idx in (1, 2):
                expanded.append({
                    "id": f"{c['id']}_sub{sub_idx}",
                    "parent_id": c["id"],
                    "name": f"{c['name']} (Fixa {sub_idx})",
                    "host": c.get("host"),
                    "port": 8554,
                    "brand": "icsee",
                    "protocol": "rtsp",
                    "ptz_enabled": False,
                    "is_sub_lens": True,
                    "order": c.get("order", 0) + sub_idx * 0.1
                })
    status_map = {s["id"]: s for s in manager.get_all_status()}
    for c in expanded:
        st = status_map.get(c["id"], {})
        c["status"] = st.get("status", "OFFLINE")
        c["fps"] = st.get("fps", 0.0)
        c["last_seen"] = st.get("last_seen")
    return jsonify(expanded)

@app.route("/api/cameras/<cam_id>/toggle_multilens", methods=["POST"])
def api_toggle_multilens(cam_id):
    cam = get_camera(cam_id)
    if not cam:
        return jsonify({"error": "Câmera não encontrada"}), 404
    
    current_state = bool(cam.get("multi_lens", False))
    new_state = not current_state
    updated = update_camera(cam_id, {"multi_lens": new_state})
    if updated:
        manager.start_worker(updated, restart_main=False)
    return jsonify({"success": True, "multi_lens": new_state})

@app.route("/api/cameras", methods=["POST"])
def api_add_camera():
    data = request.json or {}
    if not data.get("host"):
        return jsonify({"error": "Host/IP da câmera é obrigatório"}), 400
    
    new_cam = add_camera(data)
    threading.Thread(target=manager.start_worker, args=(new_cam,), daemon=True).start()
    return jsonify(new_cam), 201

@app.route("/api/cameras/<cam_id>", methods=["GET"])
def api_get_camera(cam_id):
    cam = get_camera(cam_id)
    if not cam:
        return jsonify({"error": "Câmera não encontrada"}), 404
    return jsonify(cam)

@app.route("/api/cameras/<cam_id>", methods=["PUT"])
def api_update_camera(cam_id):
    data = request.json or {}
    updated = update_camera(cam_id, data)
    if not updated:
        return jsonify({"error": "Câmera não encontrada"}), 404
    threading.Thread(target=manager.restart_worker, args=(updated,), daemon=True).start()
    return jsonify(updated)

@app.route("/api/cameras/<cam_id>", methods=["DELETE"])
def api_delete_camera(cam_id):
    if delete_camera(cam_id):
        threading.Thread(target=manager.stop_worker, args=(cam_id,), daemon=True).start()
        return jsonify({"success": True})
    return jsonify({"error": "Câmera não encontrada"}), 404

# --- TESTE DE CONEXÃO ---

@app.route("/api/cameras/test", methods=["POST"])
def api_test_camera():
    if not HAS_CV2 or cv2 is None:
        return jsonify({"success": False, "message": "Teste RTSP requer ambiente com OpenCV local."}), 400

    data = request.json or {}
    test_cam = {
        "brand": data.get("brand", "generic"),
        "host": data.get("host", ""),
        "port": int(data.get("port", 554)),
        "username": data.get("username", "admin"),
        "password": data.get("password", ""),
        "channel": int(data.get("channel", 1)),
        "stream_type": data.get("stream_type", "sub"),
        "custom_url": data.get("custom_url", "")
    }
    url = build_rtsp_url(test_cam)
    
    os.environ["OPENCV_FFMPEG_CAPTURE_OPTIONS"] = "rtsp_transport;tcp|fflags;nobuffer|max_delay;500000"
    cap = cv2.VideoCapture(url, cv2.CAP_FFMPEG)
    cap.set(cv2.CAP_PROP_OPEN_TIMEOUT_MSEC, 3000)
    
    if cap.isOpened():
        ret, frame = cap.read()
        cap.release()
        if ret and frame is not None:
            h, w = frame.shape[:2]
            return jsonify({
                "success": True,
                "message": f"Conexão bem-sucedida! Resolução: {w}x{h}"
            })
        return jsonify({
            "success": True,
            "message": "Stream RTSP aberto, aguardando quadros."
        })
    else:
        cap.release()
        return jsonify({
            "success": False,
            "message": "Não foi possível conectar. Verifique IP, porta RTSP e senha."
        }), 400

# --- STREAMING & SNAPSHOTS ---

@app.route("/api/stream/<cam_id>")
def api_stream_camera(cam_id):
    cam = get_camera(cam_id)
    if not cam:
        return "Câmera não encontrada", 404
    
    actual_id = cam["id"]
    worker = manager.get_worker(actual_id)
    if not worker or not worker.running:
        manager.start_worker(cam)
    
    resp = Response(
        manager.generate_mjpeg(actual_id),
        mimetype="multipart/x-mixed-replace; boundary=frame"
    )
    resp.headers["Cache-Control"] = "no-cache, no-store, must-revalidate, max-age=0"
    resp.headers["Pragma"] = "no-cache"
    resp.headers["Expires"] = "0"
    resp.headers["Connection"] = "close"
    return resp

@app.route("/api/snapshot/<cam_id>")
def api_snapshot_camera(cam_id):
    cam = get_camera(cam_id)
    if not cam:
        return "Câmera não encontrada", 404
    
    actual_id = cam["id"]
    worker = manager.get_worker(actual_id)
    if not worker or not worker.running:
        manager.start_worker(cam)
        import time
        time.sleep(0.5)
    
    jpeg_data = worker.get_jpeg()
    resp = Response(jpeg_data, mimetype="image/jpeg")
    resp.headers["Cache-Control"] = "no-cache, no-store, must-revalidate, max-age=0"
    resp.headers["Pragma"] = "no-cache"
    resp.headers["Expires"] = "0"
    return resp

# --- CONTROLE PTZ ---

@app.route("/api/cameras/<cam_id>/ptz", methods=["POST"])
def api_ptz_control(cam_id):
    cam = get_camera(cam_id)
    if not cam:
        return jsonify({"error": "Câmera não encontrada"}), 404
    
    data = request.json or {}
    cmd = data.get("command")
    step = int(data.get("step", 4))

    cmd_map = {
        "up": "DirectionUp",
        "down": "DirectionDown",
        "left": "DirectionLeft",
        "right": "DirectionRight",
        "stop": "Stop",
        "zoomin": "IrisLarge",
        "zoomout": "IrisSmall"
    }
    dvrip_cmd = cmd_map.get(cmd.lower() if cmd else "", "Stop")
    
    client = DVRIPClient(
        host=cam["host"],
        port=34567,
        username=cam.get("username", "admin"),
        password=cam.get("password", "")
    )
    ok = client.ptz_control(dvrip_cmd, step)
    client.close()
    
    return jsonify({"success": ok, "command": dvrip_cmd})

# --- STATUS DO SERVIDOR ---

@app.route("/api/status")
def api_status():
    stats = system_monitor.get_stats() if system_monitor else None
    return jsonify({
        "server_ip": LOCAL_IP,
        "cameras": manager.get_all_status(),
        "system": stats
    })

if __name__ == "__main__":
    port = int(os.environ.get("PORT", 8090))
    print("=" * 60)
    print("🚀 PORTEIRO VISION - CENTRAL DE CÂMERAS INTELIGENTE")
    print(f"👉 Acesso Local:     http://localhost:{port}")
    print(f"👉 Acesso no Tablet: http://{LOCAL_IP}:{port}")
    print("=" * 60)
    app.run(host="0.0.0.0", port=port, debug=False, threaded=True)
