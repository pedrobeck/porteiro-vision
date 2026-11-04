import time
import threading

try:
    import psutil
    HAS_PSUTIL = True
except ImportError:
    HAS_PSUTIL = False

class SystemMonitor:
    """
    Monitor contínuo e assíncrono de recursos do sistema:
    - Uso de CPU (%)
    - Uso de Memória RAM (% e GB)
    - Taxa de transferência de Rede / Internet (Download e Upload em KB/s ou MB/s)
    """
    def __init__(self, sample_interval=1.5):
        self.sample_interval = sample_interval
        self.cpu_percent = 0.0
        self.ram_percent = 0.0
        self.ram_used_gb = 0.0
        self.ram_total_gb = 0.0
        self.net_down_kbps = 0.0
        self.net_up_kbps = 0.0
        self.running = False
        self._thread = None
        
        if HAS_PSUTIL:
            self.running = True
            self._thread = threading.Thread(target=self._worker, daemon=True, name="SystemMonitorWorker")
            self._thread.start()

    def _worker(self):
        try:
            last_io = psutil.net_io_counters()
            last_time = time.time()
            psutil.cpu_percent(interval=None)
        except Exception:
            return

        while self.running:
            time.sleep(self.sample_interval)
            try:
                curr_io = psutil.net_io_counters()
                curr_time = time.time()
                dt = max(curr_time - last_time, 0.1)

                rx_bytes = max(curr_io.bytes_recv - last_io.bytes_recv, 0)
                tx_bytes = max(curr_io.bytes_sent - last_io.bytes_sent, 0)

                self.net_down_kbps = (rx_bytes / dt) / 1024.0
                self.net_up_kbps = (tx_bytes / dt) / 1024.0

                last_io = curr_io
                last_time = curr_time

                self.cpu_percent = psutil.cpu_percent(interval=None)
                mem = psutil.virtual_memory()
                self.ram_percent = mem.percent
                self.ram_used_gb = mem.used / (1024.0 ** 3)
                self.ram_total_gb = mem.total / (1024.0 ** 3)
            except Exception:
                pass

    def get_stats(self):
        if not HAS_PSUTIL:
            return None

        def fmt_speed(kbps):
            if kbps >= 1024.0:
                return f"{kbps / 1024.0:.1f} MB/s"
            return f"{kbps:.0f} KB/s"

        return {
            "cpu_percent": round(self.cpu_percent, 1),
            "ram_percent": round(self.ram_percent, 1),
            "ram_used_gb": round(self.ram_used_gb, 1),
            "ram_total_gb": round(self.ram_total_gb, 1),
            "net_down_kbps": round(self.net_down_kbps, 1),
            "net_up_kbps": round(self.net_up_kbps, 1),
            "net_down_str": fmt_speed(self.net_down_kbps),
            "net_up_str": fmt_speed(self.net_up_kbps)
        }

monitor = SystemMonitor()
