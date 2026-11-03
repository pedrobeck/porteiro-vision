import cv2
import threading
import time
import os
import numpy as np
from datetime import datetime
from storage import load_cameras, build_rtsp_url

class CameraWorker:
    def __init__(self, camera):
        self.camera = camera
        self.cam_id = camera["id"]
        self.name = camera["name"]
        self.running = True
        self.thread = None
        self.lock = threading.Lock()
        
        self.latest_frame = None
        self.latest_jpeg = None
        self.fps = 0.0
        self.frame_count = 0
        self.status = "INIT" # INIT, CONNECTING, ONLINE, OFFLINE, AUTH_FAILED
        self.last_error = ""
        self.last_seen = None

    def start(self):
        self.running = True
        self.thread = threading.Thread(target=self._run, daemon=True, name=f"Worker-{self.cam_id}")
        self.thread.start()

    def stop(self):
        self.running = False
        if self.thread and self.thread.is_alive():
            self.thread.join(timeout=1.0)

    def _generate_placeholder(self, status_text, detail_text=""):
        # Cria imagem 640x360 com visual de tela CCTV de comando
        img = np.zeros((360, 640, 3), dtype=np.uint8)
        # Tom de fundo escuro moderno
        img[:] = (18, 22, 28)
        
        # Grid discreto de fundo
        for y in range(0, 360, 40):
            cv2.line(img, (0, y), (640, y), (25, 32, 40), 1)
        for x in range(0, 640, 40):
            cv2.line(img, (x, 0), (x, 360), (25, 32, 40), 1)

        # Mira / cantos de segurança (CCTV HUD)
        color_hud = (0, 215, 255) if status_text == "ONLINE" else (70, 80, 95)
        # Top-left
        cv2.line(img, (20, 20), (50, 20), color_hud, 2)
        cv2.line(img, (20, 20), (20, 50), color_hud, 2)
        # Top-right
        cv2.line(img, (620, 20), (590, 20), color_hud, 2)
        cv2.line(img, (620, 20), (620, 50), color_hud, 2)
        # Bottom-left
        cv2.line(img, (20, 340), (50, 340), color_hud, 2)
        cv2.line(img, (20, 340), (20, 310), color_hud, 2)
        # Bottom-right
        cv2.line(img, (620, 340), (590, 340), color_hud, 2)
        cv2.line(img, (620, 340), (620, 310), color_hud, 2)

        # Informações da Câmera
        cv2.putText(img, f"CAM: {self.name.upper()}", (30, 45), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (200, 220, 240), 2, cv2.LINE_AA)
        ts = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        cv2.putText(img, ts, (400, 45), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (140, 160, 180), 1, cv2.LINE_AA)

        # Status Central
        badge_color = (0, 165, 255) if "CONECTANDO" in status_text else (0, 0, 220)
        cv2.putText(img, status_text, (200, 180), cv2.FONT_HERSHEY_DUPLEX, 0.9, badge_color, 2, cv2.LINE_AA)
        if detail_text:
            cv2.putText(img, detail_text[:45], (140, 215), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (130, 140, 150), 1, cv2.LINE_AA)

        _, jpeg = cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, 75])
        return jpeg.tobytes()

    def _run(self):
        retry_delay = 2.0
        while self.running:
            url = build_rtsp_url(self.camera)
            # Remove senha do log por segurança
            safe_url = url
            if "@" in url:
                pre, post = url.split("@", 1)
                safe_url = "rtsp://***:***@" + post

            print(f"[Worker {self.cam_id}] Conectando em {safe_url}...")
            self.status = "CONNECTING"
            with self.lock:
                self.latest_jpeg = self._generate_placeholder("CONECTANDO...", f"IP: {self.camera.get('host')}")

            # Define opções de baixa latência para OpenCV com FFmpeg
            os.environ["OPENCV_FFMPEG_CAPTURE_OPTIONS"] = "rtsp_transport;tcp|fflags;nobuffer|max_delay;500000"
            cap = cv2.VideoCapture(url, cv2.CAP_FFMPEG)
            
            if not cap.isOpened():
                print(f"[Worker {self.cam_id}] Não foi possível abrir o fluxo.")
                self.status = "OFFLINE"
                with self.lock:
                    self.latest_jpeg = self._generate_placeholder("SEM SINAL", "Verifique o IP, porta e senha")
                time.sleep(retry_delay)
                continue

            self.status = "ONLINE"
            self.last_error = ""
            print(f"[Worker {self.cam_id}] Conectado com sucesso!")
            
            fps_timer = time.time()
            frames_in_sec = 0

            while self.running:
                ret, frame = cap.read()
                if not ret or frame is None:
                    print(f"[Worker {self.cam_id}] Perda de quadro / conexão encerrada.")
                    self.status = "OFFLINE"
                    with self.lock:
                        self.latest_jpeg = self._generate_placeholder("SINAL PERDIDO", "Tentando reconectar...")
                    break

                self.last_seen = datetime.now()
                self.frame_count += 1
                frames_in_sec += 1

                # Calcula FPS a cada 1 segundo
                now = time.time()
                if now - fps_timer >= 1.0:
                    self.fps = round(frames_in_sec / (now - fps_timer), 1)
                    frames_in_sec = 0
                    fps_timer = now

                # Estampa d'água de telemetria CCTV no canto do frame real
                h, w = frame.shape[:2]
                ts_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
                # Barra inferior semi-transparente ou texto direto com sombra
                cv2.putText(frame, f"{self.name} | {ts_str} | {self.fps} FPS", (15, h - 15), 
                            cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 0, 0), 3, cv2.LINE_AA)
                cv2.putText(frame, f"{self.name} | {ts_str} | {self.fps} FPS", (15, h - 15), 
                            cv2.FONT_HERSHEY_SIMPLEX, 0.55, (255, 255, 255), 1, cv2.LINE_AA)

                # Codifica para JPEG
                encode_param = [int(cv2.IMWRITE_JPEG_QUALITY), 80]
                _, jpeg = cv2.imencode(".jpg", frame, encode_param)
                
                with self.lock:
                    self.latest_frame = frame
                    self.latest_jpeg = jpeg.tobytes()

                # Pequeno sleep para não saturar 100% de CPU caso a câmera envie acima de 30 FPS
                time.sleep(0.015)

            cap.release()
            time.sleep(retry_delay)

    def get_jpeg(self):
        with self.lock:
            if self.latest_jpeg:
                return self.latest_jpeg
            return self._generate_placeholder("INICIALIZANDO...")


class CameraManager:
    def __init__(self):
        self.workers = {}
        self.lock = threading.Lock()

    def start_all(self):
        cameras = load_cameras()
        print(f"[CameraManager] Iniciando {len(cameras)} câmeras cadastradas...")
        for cam in cameras:
            self.start_worker(cam)

    def start_worker(self, camera):
        cam_id = camera["id"]
        with self.lock:
            if cam_id in self.workers:
                self.workers[cam_id].stop()
            worker = CameraWorker(camera)
            worker.start()
            self.workers[cam_id] = worker

    def stop_worker(self, cam_id):
        with self.lock:
            if cam_id in self.workers:
                self.workers[cam_id].stop()
                del self.workers[cam_id]

    def restart_worker(self, camera):
        self.start_worker(camera)

    def get_worker(self, cam_id):
        with self.lock:
            return self.workers.get(cam_id)

    def get_all_status(self):
        status_list = []
        cameras = load_cameras()
        with self.lock:
            for cam in cameras:
                cid = cam["id"]
                w = self.workers.get(cid)
                status_list.append({
                    "id": cid,
                    "name": cam["name"],
                    "host": cam["host"],
                    "brand": cam["brand"],
                    "status": w.status if w else "STOPPED",
                    "fps": w.fps if w else 0.0,
                    "last_seen": w.last_seen.strftime("%H:%M:%S") if (w and w.last_seen) else None
                })
        return status_list

    def generate_mjpeg(self, cam_id):
        worker = self.get_worker(cam_id)
        while True:
            if not worker:
                time.sleep(0.5)
                worker = self.get_worker(cam_id)
                continue
            
            frame_bytes = worker.get_jpeg()
            yield (b"--frame\r\n"
                   b"Content-Type: image/jpeg\r\n\r\n" + frame_bytes + b"\r\n")
            time.sleep(0.04) # ~25 FPS máx

manager = CameraManager()
