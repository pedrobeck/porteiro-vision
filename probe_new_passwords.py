import subprocess
import cv2

passwords = [
    "Beck182Beck", "beck182beck", "Porteiro@2026", "Porteiro2026",
    "Beck182", "beck182", "Beck@182", "Pedro9984", "pedro9984",
    "admin", "123456", "12345678", "888888", "admin123", "",
    "Pedro182", "pedro182", "Pedro@182", "Beck@2026", "Porteiro123"
]

urls_templates = [
    "rtsp://admin:{pwd}@{ip}:554/user=admin&pwd={pwd}&channel=1&stream=0.sdp",
    "rtsp://admin:{pwd}@{ip}:554/user=admin&pwd={pwd}&channel=1&stream=1.sdp",
    "rtsp://admin:{pwd}@{ip}:554/stream0",
    "rtsp://admin:{pwd}@{ip}:554/stream1",
    "rtsp://admin:{pwd}@{ip}:554/h264/ch1/main/av_stream",
    "rtsp://admin:{pwd}@{ip}:554/live/ch0",
    "rtsp://admin:{pwd}@{ip}:554/onvif1"
]

found = {}

for ip in ["192.168.4.41", "192.168.4.43"]:
    print(f"\n==========================================")
    print(f"Testando Câmera em {ip}")
    print(f"==========================================")
    
    ip_found = False
    for pwd in passwords:
        # Testa primeiro template padrao Xiongmai
        url = f"rtsp://admin:{pwd}@{ip}:554/user=admin&pwd={pwd}&channel=1&stream=0.sdp"
        cmd = ["ffprobe", "-v", "error", "-rtsp_transport", "tcp", "-i", url]
        try:
            res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=2.5)
            if res.returncode == 0 or ("401" not in res.stderr and "Invalid" not in res.stderr and "Connection refused" not in res.stderr and res.stderr == ""):
                print(f"  [SUCESSO!] Encontrado no {ip} com ffprobe! Senha: '{pwd}' | URL: {url}")
                found[ip] = (pwd, url)
                ip_found = True
                break
        except Exception:
            pass
            
        # Testa tambem com OpenCV
        cap = cv2.VideoCapture(url, cv2.CAP_FFMPEG)
        cap.set(cv2.CAP_PROP_OPEN_TIMEOUT_MSEC, 1500)
        if cap.isOpened():
            ret, frame = cap.read()
            cap.release()
            if ret and frame is not None:
                h, w = frame.shape[:2]
                print(f"  [SUCESSO OpenCV!] Encontrado no {ip}! Senha: '{pwd}' | Resolucao: {w}x{h}")
                found[ip] = (pwd, url)
                ip_found = True
                break
        cap.release()
        
    if not ip_found:
        # Testa outros templates de URL para as senhas mais prováveis
        for pwd in ["Beck182Beck", "Porteiro@2026", "Beck182", "admin", "123456"]:
            for tmpl in urls_templates[2:]:
                url = tmpl.format(ip=ip, pwd=pwd)
                cmd = ["ffprobe", "-v", "error", "-rtsp_transport", "tcp", "-i", url]
                try:
                    res = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=2.5)
                    if res.returncode == 0 or (res.stderr == ""):
                        print(f"  [SUCESSO Template!] Encontrado no {ip}! Senha: '{pwd}' | URL: {url}")
                        found[ip] = (pwd, url)
                        ip_found = True
                        break
                except Exception:
                    pass
            if ip_found:
                break

if not found:
    print("\n[RESULTADO] Nenhuma das senhas usuais funcionou automaticamente.")
    print("Por favor, me informe qual senha você definiu no app para que eu teste diretamente.")
else:
    print(f"\n[FINAL] Sucessos encontrados: {found}")
