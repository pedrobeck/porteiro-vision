import os
import time
import threading

try:
    import psutil
    HAS_PSUTIL = True
except ImportError:
    HAS_PSUTIL = False

class SystemMonitor:
    """
    Monitor contínuo e assíncrono de recursos EXCLUSIVOS do Porteiro Vision:
    - Uso de CPU do processo (%)
    - Uso de Memória RAM do processo (MB)
    - Taxa de transferência de Vídeo / Streaming das câmeras (Download e Upload em KB/s)
    """
    def __init__(self, sample_interval=2.5):
        self.sample_interval = sample_interval
        self.cpu_percent = 0.0
        self.ram_used_mb = 0.0
        self.ram_percent = 0.0
        self.net_down_kbps = 0.0
        self.net_up_kbps = 0.0
        self.running = False
        self._thread = None
        self._proc = None
        self._parent_proc = None
        
        if HAS_PSUTIL:
            try:
                self._proc = psutil.Process(os.getpid())
                p = self._proc.parent()
                if p and p.is_running() and "porteiro" in p.name().lower():
                    self._parent_proc = p
            except Exception:
                pass
            self.running = True
            self._thread = threading.Thread(target=self._worker, daemon=True, name="SystemMonitorWorker")
            self._thread.start()

    def _worker(self):
        last_time = time.time()
        last_net = None

        if self._proc:
            try:
                self._proc.cpu_percent(interval=None)
            except Exception:
                pass

        try:
            last_net = psutil.net_io_counters()
        except Exception:
            pass

        while self.running:
            time.sleep(self.sample_interval)
            try:
                curr_time = time.time()
                dt = max(curr_time - last_time, 0.1)

                if self._proc and self._proc.is_running():
                    # Mede CPU e RAM do processo atual (Python)
                    total_cpu = self._proc.cpu_percent(interval=None)
                    total_rss = self._proc.memory_info().rss

                    # Soma processos filhos (ex: threads, subprocessos)
                    try:
                        for child in self._proc.children(recursive=True):
                            if child.is_running():
                                total_cpu += child.cpu_percent(interval=None)
                                total_rss += child.memory_info().rss
                    except Exception:
                        pass

                    # Se foi iniciado pelo PorteiroVision.exe (WPF), soma o consumo da casca C#
                    if self._parent_proc:
                        try:
                            if self._parent_proc.is_running():
                                total_cpu += self._parent_proc.cpu_percent(interval=None)
                                total_rss += self._parent_proc.memory_info().rss
                            else:
                                self._parent_proc = None
                        except Exception:
                            self._parent_proc = None

                    cpu_cores = psutil.cpu_count() or 1
                    self.cpu_percent = round(total_cpu / cpu_cores, 1)
                    self.ram_used_mb = round(total_rss / (1024.0 * 1024.0), 1)

                    mem_total = psutil.virtual_memory().total
                    self.ram_percent = round((total_rss / mem_total) * 100.0, 1)

                # Tráfego de rede (taxa de vídeo que entra das câmeras e sai para o navegador/tablet)
                curr_net = psutil.net_io_counters()
                if last_net:
                    rx_bytes = max(curr_net.bytes_recv - last_net.bytes_recv, 0)
                    tx_bytes = max(curr_net.bytes_sent - last_net.bytes_sent, 0)
                    self.net_down_kbps = (rx_bytes / dt) / 1024.0
                    self.net_up_kbps = (tx_bytes / dt) / 1024.0
                last_net = curr_net
                last_time = curr_time

            except Exception:
                pass

    def get_stats(self):
        if not HAS_PSUTIL:
            return None

        def fmt_speed(kbps):
            if kbps >= 1024.0:
                return f"{kbps / 1024.0:.1f} MB/s"
            return f"{kbps:.0f} KB/s"

        ram_str = f"{self.ram_used_mb:.0f} MB" if self.ram_used_mb < 1024 else f"{self.ram_used_mb / 1024.0:.1f} GB"

        return {
            "cpu_percent": round(self.cpu_percent, 1),
            "ram_percent": round(self.ram_percent, 1),
            "ram_used_mb": round(self.ram_used_mb, 1),
            "ram_used_gb": round(self.ram_used_mb / 1024.0, 2),
            "ram_total_gb": round(psutil.virtual_memory().total / (1024.0 ** 3), 1) if HAS_PSUTIL else 0.0,
            "ram_str": ram_str,
            "net_down_kbps": round(self.net_down_kbps, 1),
            "net_up_kbps": round(self.net_up_kbps, 1),
            "net_down_str": fmt_speed(self.net_down_kbps),
            "net_up_str": fmt_speed(self.net_up_kbps)
        }

monitor = SystemMonitor()
