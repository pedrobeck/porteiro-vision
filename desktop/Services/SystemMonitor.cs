using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using PorteiroVision.Models;

namespace PorteiroVision.Services;

public class SystemMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public double CpuPercent { get; private set; } = 0.0;
    public double RamUsedMb { get; private set; } = 0.0;
    public double RamPercent { get; private set; } = 0.0;
    public double RamTotalGb { get; private set; } = 0.0;
    public double NetDownKbps { get; private set; } = 0.0;
    public double NetUpKbps { get; private set; } = 0.0;

    private bool _running = false;
    private Thread? _thread;
    private readonly Process _process = Process.GetCurrentProcess();

    public SystemMonitor()
    {
        Start();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Name = "SystemMonitorWorker"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
    }

    private void MonitorLoop()
    {
        var lastCpuTime = _process.TotalProcessorTime;
        var lastTime = Stopwatch.StartNew();
        long lastBytesRecv = GetTotalBytesReceived();
        long lastBytesSent = GetTotalBytesSent();

        var memStatus = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(memStatus))
        {
            RamTotalGb = Math.Round(memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0), 1);
        }

        while (_running)
        {
            Thread.Sleep(2500);

            try
            {
                _process.Refresh();

                // CPU
                double elapsedSeconds = Math.Max(lastTime.Elapsed.TotalSeconds, 0.1);
                var currentCpuTime = _process.TotalProcessorTime;
                double cpuUsedMs = (currentCpuTime - lastCpuTime).TotalMilliseconds;
                double totalCpuMs = elapsedSeconds * 1000.0 * Environment.ProcessorCount;
                CpuPercent = Math.Round(Math.Clamp((cpuUsedMs / totalCpuMs) * 100.0, 0.0, 100.0), 1);

                lastCpuTime = currentCpuTime;
                lastTime.Restart();

                // RAM
                RamUsedMb = Math.Round(_process.WorkingSet64 / (1024.0 * 1024.0), 1);
                if (GlobalMemoryStatusEx(memStatus) && memStatus.ullTotalPhys > 0)
                {
                    RamTotalGb = Math.Round(memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0), 1);
                    RamPercent = Math.Round((double)_process.WorkingSet64 / memStatus.ullTotalPhys * 100.0, 1);
                }

                // REDE
                long currBytesRecv = GetTotalBytesReceived();
                long currBytesSent = GetTotalBytesSent();

                long rxDelta = Math.Max(0, currBytesRecv - lastBytesRecv);
                long txDelta = Math.Max(0, currBytesSent - lastBytesSent);

                NetDownKbps = Math.Round((rxDelta / elapsedSeconds) / 1024.0, 1);
                NetUpKbps = Math.Round((txDelta / elapsedSeconds) / 1024.0, 1);

                lastBytesRecv = currBytesRecv;
                lastBytesSent = currBytesSent;
            }
            catch { }
        }
    }

    private static long GetTotalBytesReceived()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("Multiplexor", StringComparison.OrdinalIgnoreCase)
                            && !n.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
                            && !n.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase))
                .Sum(n => n.GetIPStatistics().BytesReceived);
        }
        catch { return 0; }
    }

    private static long GetTotalBytesSent()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("Multiplexor", StringComparison.OrdinalIgnoreCase)
                            && !n.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
                            && !n.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase))
                .Sum(n => n.GetIPStatistics().BytesSent);
        }
        catch { return 0; }
    }

    private static string FormatSpeed(double kbps)
    {
        if (kbps >= 1024.0)
            return $"{kbps / 1024.0:F1} MB/s";
        return $"{kbps:F0} KB/s";
    }

    public SystemTelemetry GetStats()
    {
        string ramStr = RamUsedMb < 1024.0 ? $"{RamUsedMb:F0} MB" : $"{RamUsedMb / 1024.0:F1} GB";

        return new SystemTelemetry
        {
            CpuPercent = CpuPercent,
            RamPercent = RamPercent,
            RamUsedMb = RamUsedMb,
            RamUsedGb = Math.Round(RamUsedMb / 1024.0, 2),
            RamTotalGb = RamTotalGb,
            RamStr = ramStr,
            NetDownStr = FormatSpeed(NetDownKbps),
            NetUpStr = FormatSpeed(NetUpKbps)
        };
    }

    public void Dispose()
    {
        Stop();
    }
}
