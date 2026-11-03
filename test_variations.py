import socket
import hashlib
import re

def parse_auth_header(header):
    realm = re.search(r'realm="([^"]+)"', header)
    nonce = re.search(r'nonce="([^"]+)"', header)
    return (realm.group(1) if realm else ""), (nonce.group(1) if nonce else "")

def calc_digest(username, password, realm, nonce, method, uri):
    ha1 = hashlib.md5(f"{username}:{realm}:{password}".encode()).hexdigest()
    ha2 = hashlib.md5(f"{method}:{uri}".encode()).hexdigest()
    resp = hashlib.md5(f"{ha1}:{nonce}:{ha2}".encode()).hexdigest()
    return resp

def test_rtsp_auth(ip, user, pwd):
    s = socket.socket()
    s.settimeout(1.5)
    try:
        s.connect((ip, 554))
        # Testa primeiro URI padrao
        uri = f"rtsp://{ip}:554/user={user}&pwd={pwd}&channel=1&stream=0.sdp"
        
        req1 = f"DESCRIBE {uri} RTSP/1.0\r\nCSeq: 1\r\nAccept: application/sdp\r\nUser-Agent: PorteiroVision\r\n\r\n"
        s.sendall(req1.encode())
        res1 = s.recv(2048).decode(errors="ignore")
        
        if "200 OK" in res1:
            return True
            
        realm, nonce = parse_auth_header(res1)
        if not realm or not nonce:
            return False
            
        auth_resp = calc_digest(user, pwd, realm, nonce, "DESCRIBE", uri)
        auth_header = f'Digest username="{user}", realm="{realm}", nonce="{nonce}", uri="{uri}", response="{auth_resp}"'
        
        req2 = f"DESCRIBE {uri} RTSP/1.0\r\nCSeq: 2\r\nAuthorization: {auth_header}\r\nAccept: application/sdp\r\nUser-Agent: PorteiroVision\r\n\r\n"
        s.sendall(req2.encode())
        res2 = s.recv(2048).decode(errors="ignore")
        
        return "200 OK" in res2
    except Exception:
        return False
    finally:
        s.close()

variations = [
    "Beck182Beck", "beck182beck", "Beck182", "beck182", "Beck@182", "beck@182",
    "Beck182!", "Beck182#", "Beck182*", "Beck182@Beck",
    "Porteiro@2026", "Porteiro2026", "PorteiroPass2026!#Cam",
    "pedro9984", "Pedro9984", "pedro182", "Pedro182",
    "admin", "admin123", "admin1234", "admin12345", "admin888",
    "123456", "12345678", "888888", "666666", "111111", "000000",
    "12345", "1234", "123456789"
]

for ip in ["192.168.4.41", "192.168.4.43"]:
    print(f"Testando {ip}...")
    for p in variations:
        if test_rtsp_auth(ip, "admin", p):
            print(f"  >>> SUCESSO! Senha correta em {ip}: '{p}' <<<")
            break
    else:
        print(f"  Nenhuma das {len(variations)} variacoes funcionou em {ip}.")
