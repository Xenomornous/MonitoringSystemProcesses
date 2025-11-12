using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MSP
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer timer;
        private ulong prevIdleTime = 0;
        private ulong prevKernelTime = 0;
        private ulong prevUserTime = 0;

        private PerformanceCounter diskCounter;
        private PerformanceCounter? netSentCounter;
        private PerformanceCounter? netReceivedCounter;

        [StructLayout(LayoutKind.Sequential)]
        struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

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
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public MainWindow()
        {
            InitializeComponent();

            GetCpuTimes(out prevIdleTime, out prevKernelTime, out prevUserTime);

            diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");

            var category = new PerformanceCounterCategory("Network Interface");
            string[] instances = category.GetInstanceNames();
            if (instances.Length > 0)
            {
                netSentCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", instances[0]);
                netReceivedCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", instances[0]);
            }

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += Timer_Tick;
            timer.Start();

            Loaded += (_, _) =>
            {
                var screen = SystemParameters.WorkArea;
                Left = screen.Right - Width - 20;
                Top = screen.Top + 20;
                Topmost = false;
                ShowInTaskbar = false;
            };

            // Obsługa Enter w TextBoxie
            InputTextBox.KeyDown += InputTextBox_KeyDown;
        }

        private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string input = InputTextBox.Text.Trim().ToUpper(); // ignoruje wielkość liter
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string scriptPath = string.Empty;

                if (input == "EDR")
                    scriptPath = System.IO.Path.Combine(desktopPath, "EDR.ps1");
                else if (input == "MSP")
                    scriptPath = System.IO.Path.Combine(desktopPath, "MSP.ps1");
                else
                {
                    MessageBox.Show("Nieznana komenda!");
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo()
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = true
                };

                try
                {
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Błąd uruchamiania skryptu: {ex.Message}");
                }

                InputTextBox.Clear(); // czyści pole po Enterze
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            // CPU
            GetCpuTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
            ulong idle = idleTime - prevIdleTime;
            ulong kernel = kernelTime - prevKernelTime;
            ulong user = userTime - prevUserTime;

            double cpuUsage = (kernel + user != 0) ? ((double)(kernel + user - idle) * 100.0) / (kernel + user) : 0;
            prevIdleTime = idleTime;
            prevKernelTime = kernelTime;
            prevUserTime = userTime;

            CpuProgressBar.Value = cpuUsage;
            CpuText.Text = $"{cpuUsage:F1}%";
            SetProgressBarColor(CpuProgressBar, cpuUsage);

            // RAM
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
            GlobalMemoryStatusEx(memStatus);
            double usedRamPercent = memStatus.dwMemoryLoad;
            RamProgressBar.Value = usedRamPercent;
            RamText.Text = $"{usedRamPercent:F1}% ({(memStatus.ullTotalPhys - memStatus.ullAvailPhys) / 1024 / 1024:F0} MB / {memStatus.ullTotalPhys / 1024 / 1024:F0} MB)";
            SetProgressBarColor(RamProgressBar, usedRamPercent);

            // Disk
            float diskUsage = diskCounter.NextValue();
            DiskProgressBar.Value = diskUsage;
            DiskText.Text = $"{diskUsage:F1}%";
            SetProgressBarColor(DiskProgressBar, diskUsage);

            // Network
            if (netSentCounter != null && netReceivedCounter != null)
            {
                float sent = netSentCounter.NextValue() / 1024;
                float received = netReceivedCounter.NextValue() / 1024;
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

        private void SetProgressBarColor(System.Windows.Controls.ProgressBar bar, double value)
        {
            if (value < 50) bar.Foreground = Brushes.LightGreen;
            else if (value < 85) bar.Foreground = Brushes.Yellow;
            else bar.Foreground = Brushes.Red;
        }
    }
}
