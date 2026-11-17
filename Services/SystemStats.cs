using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>CPU, memory, GPU, battery and network numbers for the stats tab.</summary>
internal sealed class SystemStats
{
    private long _lastIdle, _lastKernel, _lastUser;
    private long _lastRx, _lastTx;
    private DateTime _lastNet = DateTime.MinValue;
    private bool _gpuUnavailable;
    private bool _gpuBusy;

    public double Cpu { get; private set; }
    public double RamPercent { get; private set; }
    public double RamUsedGb { get; private set; }
    public double RamTotalGb { get; private set; }
    public bool HasGpu { get; private set; }
    public double GpuUsage { get; private set; }
    public double GpuTemp { get; private set; }
    public double DownBytesPerSec { get; private set; }
    public double UpBytesPerSec { get; private set; }

    public void Sample()
    {
        SampleCpu();
        SampleMemory();
        SampleNetwork();
    }

    private void SampleCpu()
    {
        if (!Native.GetSystemTimes(out long idle, out long kernel, out long user)) return;

        long idleDelta = idle - _lastIdle;
        long totalDelta = (kernel - _lastKernel) + (user - _lastUser); // kernel time includes idle

        if (_lastKernel != 0 && totalDelta > 0)
        {
            Cpu = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
        }

        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
    }

    private void SampleMemory()
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref status)) return;

        const double Gb = 1024.0 * 1024 * 1024;
        RamTotalGb = status.ullTotalPhys / Gb;
        RamUsedGb = (status.ullTotalPhys - status.ullAvailPhys) / Gb;
        RamPercent = status.dwMemoryLoad;
    }

    private void SampleNetwork()
    {
        try
        {
            long rx = 0, tx = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }

            var now = DateTime.UtcNow;
            double seconds = (now - _lastNet).TotalSeconds;
            if (_lastNet != DateTime.MinValue && seconds > 0.2 && seconds < 30)
            {
                DownBytesPerSec = Math.Max(0, (rx - _lastRx) / seconds);
                UpBytesPerSec = Math.Max(0, (tx - _lastTx) / seconds);
            }
            else
            {
                DownBytesPerSec = 0;
                UpBytesPerSec = 0;
            }

            _lastRx = rx;
            _lastTx = tx;
            _lastNet = now;
        }
        catch
        {
            // adapters changed mid-read
        }
    }

    /// <summary>GPU load and temperature via nvidia-smi. Quietly disables itself on non-NVIDIA machines.</summary>
    public async Task UpdateGpuAsync()
    {
        if (_gpuUnavailable || _gpuBusy) return;
        _gpuBusy = true;
        try
        {
            // Full path only: launching by bare name would run whatever "nvidia-smi.exe"
            // happens to sit in the current folder or earlier on PATH.
            string tool = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(tool))
            {
                _gpuUnavailable = true;
                return;
            }

            var info = new ProcessStartInfo(
                tool,
                "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(info);
            if (process == null)
            {
                _gpuUnavailable = true;
                return;
            }

            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            string[] parts = output.Split('\n')[0].Split(',');
            GpuUsage = double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            GpuTemp = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
            HasGpu = true;
        }
        catch
        {
            _gpuUnavailable = true;
            HasGpu = false;
        }
        finally
        {
            _gpuBusy = false;
        }
    }

    public static (bool HasBattery, bool Plugged, int Percent) Power()
    {
        if (!Native.GetSystemPowerStatus(out var status)) return (false, true, 100);
        bool hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryLifePercent != 255;
        return (hasBattery, status.ACLineStatus == 1, status.BatteryLifePercent);
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024) return (bytesPerSec / (1024 * 1024)).ToString("0.0") + " MB/s";
        if (bytesPerSec >= 1024) return (bytesPerSec / 1024).ToString("0") + " KB/s";
        return bytesPerSec.ToString("0") + " B/s";
    }
}
