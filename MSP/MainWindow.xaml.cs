using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace MSP
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer timer;
        private ulong prevIdleTime = 0;
        private ulong prevKernelTime = 0;
        private ulong prevUserTime = 0;

        // Dysk
        private PerformanceCounter diskCounter;

        // Sieć
        private PerformanceCounter? netSentCounter;
        private PerformanceCounter? netReceivedCounter;

        [StructLayout(LayoutKind.Sequential)]
        struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

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

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        // 🔹 WinAPI do osadzania na pulpicie
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        static extern IntPtr GetDesktopWindow();

        public MainWindow()
        {
            InitializeComponent();

            // CPU
            GetCpuTimes(out prevIdleTime, out prevKernelTime, out prevUserTime);

            // Disk
            diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");

            // Network (pierwsza aktywna karta)
            var category = new PerformanceCounterCategory("Network Interface");
            string[] instances = category.GetInstanceNames();
            if (instances.Length > 0)
            {
                netSentCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", instances[0]);
                netReceivedCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", instances[0]);
            }

            // Timer co sekundę
            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += Timer_Tick;
            timer.Start();

            // 🔹 Po załadowaniu okna — ustaw pozycję i przypnij do pulpitu
            Loaded += (s, e) =>
            {
                var screen = SystemParameters.WorkArea;
                Left = screen.Right - Width - 20;
                Top = screen.Top + 20;

                // 🔹 Umieść okno na pulpicie (pod innymi oknami)
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var desktop = GetDesktopWindow();
                SetParent(hwnd, desktop);
            };
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            // CPU
            GetCpuTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
            ulong idle = idleTime - prevIdleTime;
            ulong kernel = kernelTime - prevKernelTime;
            ulong user = userTime - prevUserTime;

            double cpuUsage = 0;
            if (kernel + user != 0)
                cpuUsage = ((double)(kernel + user - idle) * 100.0) / (kernel + user);

            prevIdleTime = idleTime;
            prevKernelTime = kernelTime;
            prevUserTime = userTime;

            CpuProgressBar.Value = cpuUsage;
            CpuText.Text = $"{cpuUsage:F1}%";

            // RAM
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
            GlobalMemoryStatusEx(memStatus);
            double usedRamPercent = memStatus.dwMemoryLoad;
            RamProgressBar.Value = usedRamPercent;
            RamText.Text = $"{usedRamPercent:F1}% ({(memStatus.ullTotalPhys - memStatus.ullAvailPhys) / 1024 / 1024:F0} MB / {memStatus.ullTotalPhys / 1024 / 1024:F0} MB)";

            // Disk
            float diskUsage = diskCounter.NextValue();
            DiskProgressBar.Value = diskUsage;
            DiskText.Text = $"{diskUsage:F1}%";

            // Network
            if (netSentCounter != null && netReceivedCounter != null)
            {
                float sent = netSentCounter.NextValue() / 1024; // KB/s
                float received = netReceivedCounter.NextValue() / 1024; // KB/s
                NetText.Text = $"↑ {sent:F0} KB/s | ↓ {received:F0} KB/s";
            }
        }

        private void GetCpuTimes(out ulong idle, out ulong kernel, out ulong user)
        {
            GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);
            idle = ((ulong)idleTime.dwHighDateTime << 32) | idleTime.dwLowDateTime;
            kernel = ((ulong)kernelTime.dwHighDateTime << 32) | kernelTime.dwLowDateTime;
            user = ((ulong)userTime.dwHighDateTime << 32) | userTime.dwLowDateTime;
        }
    }
}
