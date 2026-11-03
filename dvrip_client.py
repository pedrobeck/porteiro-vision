import socket
import struct
import json
import time

# Protocolo DVRIP (Porta 34567 - Xiongmai / iCSee)
# Header: 20 bytes: 0xFF, 0x00, 0x00, 0x00 (magic), Session(4B), Seq(4B), Total(2B), Cur(2B), MsgId(2B), Len(2B)

MSG_LOGIN_REQ = 1000
MSG_LOGIN_RESP = 1001
MSG_LOGOUT = 1002
MSG_PTZ_REQ = 1400

class DVRIPClient:
    def __init__(self, host, port=34567, username="admin", password="", timeout=2.5):
        self.host = host
        self.port = port
        self.username = username
        self.password = password
        self.timeout = timeout
        self.session_id = 0
        self.seq = 0
        self.sock = None

    def connect(self):
        try:
            self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self.sock.settimeout(self.timeout)
            self.sock.connect((self.host, self.port))
            return True
        except Exception as e:
            print(f"[DVRIP] Falha ao conectar em {self.host}:{self.port} - {e}")
            return False

    def close(self):
        if self.sock:
            try:
                self.sock.close()
            except Exception:
                pass
            self.sock = None

    def _send_cmd(self, msg_id, payload_dict):
        if not self.sock:
            return None
        payload_bytes = (json.dumps(payload_dict) + "\n\x00").encode("utf-8")
        header = struct.pack(
            "<4BIIHHHH",
            0xFF, 0x00, 0x00, 0x00,
            self.session_id,
            self.seq,
            0, 0,
            msg_id,
            len(payload_bytes)
        )
        self.seq += 1
        try:
            self.sock.sendall(header + payload_bytes)
            # Lê resposta: 20 bytes de cabeçalho
            resp_header = self.sock.recv(20)
            if len(resp_header) < 20:
                return None
            _, s_id, s_seq, _, _, r_msg_id, r_len = struct.unpack("<4sIIHHHH", resp_header)
            resp_body = b""
            while len(resp_body) < r_len:
                chunk = self.sock.recv(r_len - len(resp_body))
                if not chunk:
                    break
                resp_body += chunk
            data_str = resp_body.decode("utf-8", errors="ignore").strip().rstrip("\x00")
            return json.loads(data_str) if data_str else {}
        except Exception as e:
            print(f"[DVRIP] Erro na requisição: {e}")
            return None

    def login(self):
        if not self.sock and not self.connect():
            return False
        
        login_payload = {
            "EncryptType": "MD5",
            "LoginType": "DVRIP-Web",
            "PassWord": self.password,
            "UserName": self.username
        }
        res = self._send_cmd(MSG_LOGIN_REQ, {
            "Name": "SysManager.Auth",
            "SessionID": "0x00000000",
            "SysManager.Auth": login_payload
        })
        if res and res.get("Ret") == 100:
            s_hex = res.get("SessionID", "0x0")
            self.session_id = int(s_hex, 16) if isinstance(s_hex, str) else s_hex
            return True
        return False

    def ptz_control(self, command, step=4):
        # command: DirectionUp, DirectionDown, DirectionLeft, DirectionRight, IrisLarge, IrisSmall, Stop
        if not self.session_id:
            if not self.login():
                return False
        
        ptz_data = {
            "Channel": 0,
            "Command": command,
            "Parameter": {
                "Channel": 0,
                "Step": step
            }
        }
        res = self._send_cmd(MSG_PTZ_REQ, {
            "Name": "OPPTZControl",
            "SessionID": hex(self.session_id),
            "OPPTZControl": ptz_data
        })
        return res is not None and res.get("Ret") in [100, 200]
