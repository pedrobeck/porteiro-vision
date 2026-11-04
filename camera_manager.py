try:
    import cv2
    import numpy as np
    HAS_CV2 = True
except Exception as e:
    cv2 = None
    np = None
    HAS_CV2 = False
    print(f"[CameraManager] OpenCV indisponível neste ambiente: {e}")

import threading
import time
import os
import subprocess
from datetime import datetime
from storage import load_cameras, build_rtsp_url

class CameraWorker:
    def __init__(self, camera):
        self.camera = camera
        self.cam_id = camera["id"]
        self.name = camera["name"]
        self.running = True
        self.thread = None
        self.proc = None
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
        if self.proc:
            try:
                self.proc.terminate()
            except Exception:
                pass
        if self.thread and self.thread.is_alive():
            self.thread.join(timeout=1.0)

    def _generate_placeholder(self, status_text, detail_text=""):
        if not HAS_CV2 or cv2 is None or np is None:
            # Retorna JPEG mínimo de 1x1 pixel cinza escuro
            return b'\xff\xd8\xff\xe0\x00\x10JFIF\x00\x01\x01\x01\x00H\x00H\x00\x00\xff\xdb\x00C\x00\x08\x06\x06\x07\x06\x05\x08\x07\x07\x07\t\t\x08\n\x0c\x14\r\x0c\x0b\x0b\x0c\x19\x12\x13\x0f\x14\x1d\x1a\x1f\x1e\x1d\x1a\x1c\x1c $.\' \",#\x1c\x1c(7),01444\x1f\'9=82<.342\xff\xc0\x00\x0b\x08\x00\x01\x00\x01\x01\x01\x11\x00\xff\xc4\x00\x1f\x00\x00\x01\x05\x01\x01\x01\x01\x01\x01\x00\x00\x00\x00\x00\x00\x00\x00\x01\x02\x03\x04\x05\x06\x07\x08\t\n\x0b\xff\xda\x00\x08\x01\x01\x00\x00?\x00\xbf\x00\xff\xd9'

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

    def _run_ffmpeg(self, url, retry_delay):
        ffmpeg_bin = r"C:\Program Files\Intelbras\SIMNext\broadcaster\ffmpeg-simnext.exe"
        if not os.path.exists(ffmpeg_bin):
            ffmpeg_bin = "ffmpeg"

        cmd = [
            ffmpeg_bin,
            "-y",
            "-use_wallclock_as_timestamps", "1",
            "-rtsp_transport", "tcp",
            "-stimeout", "15000000",
            "-flags", "+low_delay",
            "-fflags", "+nobuffer+discardcorrupt",
            "-i", url,
            "-vsync", "0",
            "-f", "image2pipe",
            "-vcodec", "mjpeg",
            "-q:v", "5",
            "-"
        ]
        try:
            proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=2*1024*1024)
        except Exception as e:
            print(f"[Worker {self.cam_id}] Erro ao iniciar ffmpeg: {e}", flush=True)
            self.status = "OFFLINE"
            time.sleep(retry_delay)
            return

        self.status = "CONNECTING"
        self.proc = proc
        self.last_seen = datetime.now()
        connect_start = time.time()
        fps_timer = time.time()
        frames_in_sec = 0
        buffer = b""

        # Watchdog inteligente: dá tempo para handshake P2P e só reseta se travar
        def watchdog():
            while self.running and proc.poll() is None:
                time.sleep(2.0)
                # Durante conexão inicial, aguarda até 30s pelo handshake P2P do DVR
                if self.status == "CONNECTING":
                    if time.time() - connect_start > 30.0:
                        print(f"[Worker {self.cam_id}] Timeout de handshake P2P (30s), reiniciando...", flush=True)
                        try:
                            proc.terminate()
                        except Exception:
                            pass
                        break
                # Se já estava ONLINE e o fluxo de vídeo parou há mais de 15s
                elif self.status == "ONLINE":
                    if self.last_seen and (datetime.now() - self.last_seen).total_seconds() > 15.0:
                        print(f"[Worker {self.cam_id}] Watchdog: fluxo travou há mais de 15s, reiniciando conexão...", flush=True)
                        try:
                            proc.terminate()
                        except Exception:
                            pass
                        break

        threading.Thread(target=watchdog, daemon=True).start()

        # Drena o pipe continuamente sem sleeps artificiais para evitar acúmulo no buffer do SO
        while self.running and proc.poll() is None:
            chunk = proc.stdout.read(32768)
            if not chunk:
                break
            buffer += chunk

            # Processa todos os quadros JPEG completos já recebidos
            while True:
                a = buffer.find(b"\xff\xd8")
                if a == -1:
                    buffer = b""
                    break
                if a > 0:
                    buffer = buffer[a:]

                b = buffer.find(b"\xff\xd9", 2)
                if b == -1:
                    if len(buffer) > 2000000:
                        buffer = b""
                    break

                jpg = buffer[:b+2]
                buffer = buffer[b+2:]

                if len(jpg) > 1000:
                    self.status = "ONLINE"
                    self.last_seen = datetime.now()
                    self.frame_count += 1
                    frames_in_sec += 1

                    now = time.time()
                    if now - fps_timer >= 1.0:
                        self.fps = round(frames_in_sec / (now - fps_timer), 1)
                        frames_in_sec = 0
                        fps_timer = now

                    with self.lock:
                        self.latest_jpeg = jpg

        try:
            proc.terminate()
        except Exception:
            pass
        self.proc = None

        # Só exibe "SINAL PERDIDO" e marca OFFLINE se estiver há mais de 10 segundos sem sinal,
        # evitando piscar na tela do usuário durante breves reconexões de rede
        if not self.last_seen or (datetime.now() - self.last_seen).total_seconds() > 10.0:
            self.status = "OFFLINE"
            with self.lock:
                self.latest_jpeg = self._generate_placeholder("SINAL PERDIDO", "Tentando reconectar...")
        time.sleep(retry_delay)

    def _run(self):
        retry_delay = 2.0
        while self.running:
            if not HAS_CV2:
                time.sleep(5)
                continue
            url = build_rtsp_url(self.camera)
            # Remove senha do log por segurança
            safe_url = url
            if "@" in url:
                pre, post = url.split("@", 1)
                safe_url = "rtsp://***:***@" + post

            if self.camera.get("brand") == "intelbras":
                print(f"[Worker {self.cam_id}] Conectando Intelbras HEVC via FFmpeg em {safe_url}...")
                self._run_ffmpeg(url, retry_delay)
                continue

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
