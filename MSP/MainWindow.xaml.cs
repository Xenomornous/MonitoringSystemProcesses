using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms; // alias, aby uniknąć konfliktu

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

        private Dictionary<string, string> commands = new();
        private string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "commands.json");
        private FileSystemWatcher jsonWatcher;
        private WinForms.NotifyIcon trayIcon;

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

            // Załaduj komendy
            LoadCommands();

            // FileSystemWatcher - dynamiczna aktualizacja JSON
            jsonWatcher = new FileSystemWatcher(AppDomain.CurrentDomain.BaseDirectory, "commands.json");
            jsonWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
            jsonWatcher.Changed += (s, e) => Dispatcher.Invoke(LoadCommands);
            jsonWatcher.Created += (s, e) => Dispatcher.Invoke(LoadCommands);
            jsonWatcher.EnableRaisingEvents = true;

            // Tray Icon
            //trayIcon = new WinForms.NotifyIcon();
            //trayIcon.Icon = new System.Drawing.Icon(System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("MSP.icon.ico"));
            //trayIcon.Visible = true;
            //trayIcon.Text = "MSP Monitor";

            // CPU / RAM / Disk / Network
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
                Left = screen.Right - Width + 55;
                Top = screen.Top + 10;

                Topmost = false;
                ShowInTaskbar = false; // nie pokazuje w pasku zadań
            };

            InputTextBox.KeyDown += InputTextBox_KeyDown;
        }

        private void LoadCommands()
        {
            try
            {
                if (File.Exists(jsonPath))
                {
                    string json = File.ReadAllText(jsonPath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (loaded != null)
                        commands = loaded;
                }
                else
                    commands = new Dictionary<string, string>();
            }
            catch
            {
                commands = new Dictionary<string, string>();
            }
        }

        private void SaveCommands()
        {
            try
            {
                string json = JsonSerializer.Serialize(commands, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(jsonPath, json);
            }
            catch { }
        }

        private void InputTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            ErrorText.Text = "";

            if (e.Key != Key.Enter) return;

            string input = InputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(input)) return;

            // NEW:
            if (input.StartsWith("NEW:", StringComparison.OrdinalIgnoreCase))
            {
                string cmdName = input.Substring(4).Trim().ToUpper();
                if (!string.IsNullOrEmpty(cmdName))
                {
                    if (!commands.ContainsKey(cmdName))
                    {
                        commands[cmdName] = cmdName + ".ps1";
                        SaveCommands();
                        ErrorText.Text = $"Dodano {cmdName}";
                    }
                    else
                        ErrorText.Text = $"Komenda {cmdName} już istnieje";
                }
                InputTextBox.Clear();
                return;
            }

            // DELETE:
            if (input.StartsWith("DELETE:", StringComparison.OrdinalIgnoreCase))
            {
                string cmdName = input.Substring(7).Trim().ToUpper();
                if (!string.IsNullOrEmpty(cmdName))
                {
                    if (commands.ContainsKey(cmdName))
                    {
                        commands.Remove(cmdName);
                        SaveCommands();
                        ErrorText.Text = $"Usunięto {cmdName}";
                    }
                    else
                        ErrorText.Text = $"Nie znaleziono {cmdName}";
                }
                InputTextBox.Clear();
                return;
            }

            // INFO:
            if (input.Equals("INFO", StringComparison.OrdinalIgnoreCase))
            {
                if (commands.Count > 0)
                {
                    string infoOutput = "Dostępne komendy: INFO, NEW:, DELETE: . Komendy do aktualizacji GIT repo projektów: " + string.Join(", ", commands.Keys);
                    ErrorText.Text = infoOutput;
                }
                else
                {
                    ErrorText.Text = "Brak zdefiniowanych komend.";
                }

                InputTextBox.Clear();
                return;
            }

            // Uruchomienie normalnej komendy
            string cmdKey = input.ToUpper();
            if (commands.ContainsKey(cmdKey))
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string ppsaFolder = Path.Combine(desktopPath, "PPSA");
                string scriptPath = Path.Combine(ppsaFolder, commands[cmdKey]);

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
                    ErrorText.Text = $"Błąd uruchamiania skryptu: {ex.Message}";
                }
            }
            else
            {
                ErrorText.Text = $"Nieznana komenda: {input}";
            }

            InputTextBox.Clear();
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

            try
            {
                int processCount = Process.GetProcesses().Length;
                ProcessCountText.Text = processCount.ToString();
            }
            catch
            {
                ProcessCountText.Text = "Błąd";
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
            if (value < 50) bar.Foreground = System.Windows.Media.Brushes.LightGreen;
            else if (value < 85) bar.Foreground = System.Windows.Media.Brushes.Yellow;
            else bar.Foreground = System.Windows.Media.Brushes.Red;
        }
    }
}
