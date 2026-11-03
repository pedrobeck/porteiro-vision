import sys
import socket
import hashlib
import cv2

passwords = [
    "",
    "admin",
    "123456",
    "888888",
    "Beck182Beck",
    "Porteiro@2026",
    "admin123",
    "12345",
    "666666",
    "111111",
    "pedro9984"
]

def test_rtsp(ip, user="admin", pwd=""):
    # URLs comuns Xiongmai/iCSee
    urls = [
        f"rtsp://{user}:{pwd}@{ip}:554/user={user}&pwd={pwd}&channel=1&stream=0.sdp",
        f"rtsp://{user}:{pwd}@{ip}:554/user={user}&pwd={pwd}&channel=1&stream=1.sdp",
        f"rtsp://{user}:{pwd}@{ip}:554/stream0",
        f"rtsp://{user}:{pwd}@{ip}:554/h264/ch1/main/av_stream",
        f"rtsp://{user}:{pwd}@{ip}:554/onvif1"
    ] if pwd else [
        f"rtsp://{ip}:554/user={user}&pwd=&channel=1&stream=0.sdp",
        f"rtsp://{ip}:554/user={user}&pwd=&channel=1&stream=1.sdp",
        f"rtsp://{ip}:554/stream0",
        f"rtsp://{ip}:554/live/ch0"
    ]
    
    for u in urls:
        cap = cv2.VideoCapture(u, cv2.CAP_FFMPEG)
        cap.set(cv2.CAP_PROP_OPEN_TIMEOUT_MSEC, 1500)
        if cap.isOpened():
            ret, frame = cap.read()
            cap.release()
            if ret and frame is not None:
                h, w = frame.shape[:2]
                return True, u, f"{w}x{h}"
        cap.release()
    return False, None, None

def test_rtsp_options(ip):
    s = socket.socket()
    s.settimeout(2)
    try:
        s.connect((ip, 554))
        req = f"OPTIONS rtsp://{ip}:554/ RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: PorteiroVision\r\n\r\n"
        s.sendall(req.encode())
        resp = s.recv(1024).decode(errors="ignore")
        return resp
    except Exception as e:
        return str(e)
    finally:
        s.close()

def test_rtsp_describe(ip, pwd=""):
    s = socket.socket()
    s.settimeout(2)
    try:
        s.connect((ip, 554))
        url = f"rtsp://{ip}:554/user=admin&pwd={pwd}&channel=1&stream=0.sdp"
        req = f"DESCRIBE {url} RTSP/1.0\r\nCSeq: 2\r\nAccept: application/sdp\r\nUser-Agent: PorteiroVision\r\n\r\n"
        s.sendall(req.encode())
        resp = s.recv(2048).decode(errors="ignore")
        return resp
    except Exception as e:
        return str(e)
    finally:
        s.close()

for ip in ["192.168.4.41", "192.168.4.43"]:
    print(f"\n==========================================")
    print(f"Testando Câmera em {ip}")
    print(f"==========================================")
    
    # 1. Checa resposta OPTIONS no RTSP
    options_resp = test_rtsp_options(ip)
    first_line = options_resp.split("\r\n")[0] if "\r\n" in options_resp else options_resp
    print(f"Porta 554 OPTIONS: {first_line}")
    
    # 2. Testa DESCRIBE com senha vazia
    desc_resp = test_rtsp_describe(ip, "")
    print(f"DESCRIBE (senha vazia):\n{desc_resp[:200]}")
    
    # 3. Testa senhas com OpenCV
    print("Testando senhas com OpenCV...")
    found = False
    for p in passwords:
        ok, url, res = test_rtsp(ip, "admin", p)
        if ok:
            print(f"  [SUCESSO RTSP!] Senha: '{p}' | Resolucao: {res} | URL: {url}")
            found = True
            break
        else:
            print(f"  Tentou '{p}': falhou")
            
    if not found:
        print(f"  [AVISO] Nenhuma das senhas comuns funcionou para {ip}.")
