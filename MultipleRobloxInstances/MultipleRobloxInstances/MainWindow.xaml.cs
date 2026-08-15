using MultipleRobloxInstances.Resources;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MultipleRobloxInstances
{
    public partial class MainWindow : Window
    {
        private const long MaxRobloxId = 10_000_000_000;
        private const string RobloxProcessName = "RobloxPlayerBeta";
        private const string AvatarHost = "thumbnails.roblox.com";
        private const string AvatarCdnHost = "tr.rbxcdn.com";
        private static readonly TimeSpan ProcessLogStartupTolerance = TimeSpan.FromSeconds(2);

        public readonly string Version = "2.2";
        public readonly string RobloxPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox");
        public bool LastTaskIsRoblox = false; // 2nd log is the real one
        public FileInfo? Last;
        public Mutex? RobloxLock;
        public FileStream? RobloxCookieLock;
        public readonly string WindowsUser = Environment.UserDomainName + "\\" + Environment.UserName;

        private static readonly HttpClient RobloxAPI = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly object _debounceLock = new();
        private readonly object _lifecycleLock = new();
        private readonly object _logLock = new();
        private readonly object _monitoringTasksLock = new();
        private readonly HashSet<Task> _monitoringTasks = new();
        private readonly HashSet<string> _claimedLogPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _observedRobloxPids = new();
        private DispatcherTimer? _processPollingTimer;
        private Task? _updateCheckTask;
        private int _isShuttingDown;
        private int _shutdownStarted;
        private int _shutdownComplete;
        private int _shutdownCtsDisposed;
        private int _shutdownDrainStarted;

        // -------- Theme engine (light "Lumina" / dark) --------
        private static readonly Dictionary<string, Color> LightColors = new()
        {
            ["BrushShellBg"]=Color.FromRgb(0xF9,0xFA,0xFB), ["BrushSurface"]=Color.FromRgb(0xF9,0xF9,0xFF),
            ["BrushCard"]=Color.FromRgb(0xFF,0xFF,0xFF), ["BrushGlass"]=Color.FromArgb(0xF7,0xFF,0xFF,0xFF),
            ["BrushTextPrimary"]=Color.FromRgb(0x14,0x1B,0x2B), ["BrushTextSecondary"]=Color.FromRgb(0x46,0x45,0x54),
            ["BrushTextMuted"]=Color.FromRgb(0x76,0x75,0x86), ["BrushMonoFooter"]=Color.FromRgb(0x51,0x60,0x72),
            ["BrushMonoStatus"]=Color.FromRgb(0x43,0x47,0x4B), ["BrushBorderOutline"]=Color.FromRgb(0xC7,0xC4,0xD7),
            ["BrushBorderVariant"]=Color.FromRgb(0xDC,0xE2,0xF7), ["BrushCardBorder"]=Color.FromRgb(0xF1,0xF5,0xF9),
            ["BrushNavActiveBg"]=Color.FromRgb(0xD2,0xE1,0xF7), ["BrushNavActiveText"]=Color.FromRgb(0x55,0x64,0x77),
            ["BrushNavHover"]=Color.FromRgb(0xE1,0xE8,0xFD), ["BrushRowHover"]=Color.FromRgb(0xF1,0xF3,0xFF),
            ["BrushAccent"]=Color.FromRgb(0x46,0x48,0xD4), ["BrushAccentHover"]=Color.FromRgb(0x3B,0x3D,0xCB),
            ["BrushPrimaryContainer"]=Color.FromRgb(0x60,0x63,0xEE), ["BrushPrimaryContainerBg"]=Color.FromRgb(0xED,0xED,0xFC),
            ["BrushPillBg"]=Color.FromRgb(0xE9,0xED,0xFF), ["BrushPillBorder"]=Color.FromRgb(0xDC,0xE2,0xF7),
            ["BrushChipBg"]=Color.FromRgb(0xDC,0xE2,0xF7), ["BrushError"]=Color.FromRgb(0xBA,0x1A,0x1A),
            ["BrushErrorContainer"]=Color.FromRgb(0xFF,0xDA,0xD6), ["BrushStatusGreen"]=Color.FromRgb(0x10,0xB9,0x81),
            ["BrushScrollThumb"]=Color.FromRgb(0xDC,0xE2,0xF7), ["BrushIcon"]=Color.FromRgb(0x76,0x75,0x86),
            ["BrushTextOnAccent"]=Color.FromRgb(0xFF,0xFF,0xFF)
        };

        private static readonly Dictionary<string, Color> DarkColors = new()
        {
            ["BrushShellBg"]=Color.FromRgb(0x12,0x14,0x1C), ["BrushSurface"]=Color.FromRgb(0x17,0x19,0x23),
            ["BrushCard"]=Color.FromRgb(0x1E,0x21,0x30), ["BrushGlass"]=Color.FromArgb(0xF7,0x1E,0x21,0x30),
            ["BrushTextPrimary"]=Color.FromRgb(0xE9,0xE9,0xF4), ["BrushTextSecondary"]=Color.FromRgb(0xB9,0xB8,0xC8),
            ["BrushTextMuted"]=Color.FromRgb(0x8A,0x8A,0x9C), ["BrushMonoFooter"]=Color.FromRgb(0x8A,0x8A,0x9C),
            ["BrushMonoStatus"]=Color.FromRgb(0x9A,0x9A,0xAC), ["BrushBorderOutline"]=Color.FromRgb(0x3A,0x3D,0x4E),
            ["BrushBorderVariant"]=Color.FromRgb(0x2A,0x2D,0x3D), ["BrushCardBorder"]=Color.FromRgb(0x26,0x29,0x38),
            ["BrushNavActiveBg"]=Color.FromRgb(0x2E,0x33,0x50), ["BrushNavActiveText"]=Color.FromRgb(0xC5,0xC9,0xE8),
            ["BrushNavHover"]=Color.FromRgb(0x23,0x27,0x3A), ["BrushRowHover"]=Color.FromRgb(0x23,0x27,0x3A),
            ["BrushAccent"]=Color.FromRgb(0x7C,0x7F,0xF2), ["BrushAccentHover"]=Color.FromRgb(0x8E,0x91,0xF5),
            ["BrushPrimaryContainer"]=Color.FromRgb(0x8E,0x91,0xF5), ["BrushPrimaryContainerBg"]=Color.FromRgb(0x2A,0x2D,0x4A),
            ["BrushPillBg"]=Color.FromRgb(0x23,0x2A,0x34), ["BrushPillBorder"]=Color.FromRgb(0x33,0x3A,0x48),
            ["BrushChipBg"]=Color.FromRgb(0x2A,0x2D,0x3D), ["BrushError"]=Color.FromRgb(0xFF,0x8A,0x80),
            ["BrushErrorContainer"]=Color.FromRgb(0x4A,0x23,0x26), ["BrushStatusGreen"]=Color.FromRgb(0x10,0xB9,0x81),
            ["BrushScrollThumb"]=Color.FromRgb(0x3A,0x3D,0x4E), ["BrushIcon"]=Color.FromRgb(0x8A,0x8A,0x9C),
            ["BrushTextOnAccent"]=Color.FromRgb(0xFF,0xFF,0xFF)
        };

        private bool _isDark;
        private void ThemeToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            _isDark = !_isDark;
            var map = _isDark ? DarkColors : LightColors;
            foreach (var kv in map)
            {
                if (Application.Current.Resources[kv.Key] is SolidColorBrush b)
                    b.Color = kv.Value;
            }
        }

        private static SolidColorBrush ThemeBrush(string key)
        {
            if (Application.Current.Resources[key] is SolidColorBrush themeBrush)
            {
                return themeBrush;
            }
            return new SolidColorBrush(LightColors[key]);
        }

        private bool IsShuttingDown => Volatile.Read(ref _isShuttingDown) != 0;

        public int RobloxInstancesOpen()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(RobloxProcessName);
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
            catch (Win32Exception)
            {
                return 0;
            }

            try
            {
                return processes.Length;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        public FileInfo? MostRecentRobloxLogFile()
        {
            try
            {
                DirectoryInfo directory = new(Path.Combine(RobloxPath, "logs"));
                if (!directory.Exists)
                {
                    return null;
                }

                return directory.EnumerateFiles()
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private FileInfo[] RobloxLogFiles()
        {
            try
            {
                DirectoryInfo directory = new(Path.Combine(RobloxPath, "logs"));
                if (!directory.Exists)
                {
                    return Array.Empty<FileInfo>();
                }

                return directory.EnumerateFiles()
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<FileInfo>();
            }
            catch (IOException)
            {
                return Array.Empty<FileInfo>();
            }
        }

        public string[] ReadViaShadowCopy(string filePath)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            try
            {
                using FileStream sourceStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using FileStream tempStream = new(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                sourceStream.CopyTo(tempStream);
                tempStream.Position = 0;

                using StreamReader reader = new(tempStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                return reader.ReadToEnd().Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            }
            finally
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }
        }

        public bool CheckIfProcessExists(int pid)
        {
            if (pid <= 0)
            {
                return false;
            }

            try
            {
                using Process process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        public Dictionary<string, long>? GetRobloxDetails(string[] lines)
        {
            if (lines is null)
            {
                return null;
            }

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.Contains("game_join_loadtime", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Match match;
                try
                {
                    match = Regex.Match(
                        line,
                        @"universeid:(\d{1,11}),.*?userid:(\d{1,11})",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100));
                }
                catch (RegexMatchTimeoutException)
                {
                    continue;
                }

                if (!match.Success ||
                    !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long universeId) ||
                    !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long userId) ||
                    universeId <= 0 || universeId > MaxRobloxId ||
                    userId <= 0 || userId > MaxRobloxId)
                {
                    continue;
                }

                return new Dictionary<string, long>
                {
                    ["Universe"] = universeId,
                    ["UserID"] = userId
                };
            }

            return null;
        }

        // UI animations taken from MainDab
        public void Fade(DependencyObject elementName, double start, double end, double time)
        {
            DoubleAnimation animation = new()
            {
                From = start,
                To = end,
                Duration = TimeSpan.FromSeconds(time),
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(animation, elementName);
            Storyboard.SetTargetProperty(animation, new PropertyPath(OpacityProperty));
            Storyboard storyboard = new();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        public void Move(DependencyObject elementName, Thickness origin, Thickness location, double time)
        {
            ThicknessAnimation animation = new()
            {
                From = origin,
                To = location,
                Duration = TimeSpan.FromSeconds(time),
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(animation, elementName);
            Storyboard.SetTargetProperty(animation, new PropertyPath(MarginProperty));
            Storyboard storyboard = new();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        public void Scaling(DependencyObject elementName, double before, double after, double time)
        {
            DoubleAnimation scalingX = new()
            {
                From = before,
                To = after,
                Duration = TimeSpan.FromSeconds(time),
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseInOut }
            };

            Storyboard.SetTarget(scalingX, elementName);
            Storyboard.SetTargetProperty(scalingX, new PropertyPath("RenderTransform.Children[0].ScaleX"));
            Storyboard storyboardX = new();
            storyboardX.Children.Add(scalingX);

            DoubleAnimation scalingY = new()
            {
                From = before,
                To = after,
                Duration = TimeSpan.FromSeconds(time),
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseInOut }
            };

            Storyboard.SetTarget(scalingY, elementName);
            Storyboard.SetTargetProperty(scalingY, new PropertyPath("RenderTransform.Children[0].ScaleY"));
            Storyboard storyboardY = new();
            storyboardY.Children.Add(scalingY);

            storyboardX.Begin();
            storyboardY.Begin();
        }

        // ---------------- //
        // ** MAIN LOGIC ** //
        // ---------------- //

        private void StartProcessPolling()
        {
            lock (_lifecycleLock)
            {
                if (IsShuttingDown || _processPollingTimer != null)
                {
                    return;
                }

                DispatcherTimer timer = new(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                timer.Tick += ProcessPollingTimer_Tick;
                _processPollingTimer = timer;
                timer.Start();
            }
        }

        private void StopProcessPolling()
        {
            DispatcherTimer? timer;
            lock (_lifecycleLock)
            {
                timer = _processPollingTimer;
                _processPollingTimer = null;
            }

            if (timer == null)
            {
                return;
            }

            timer.Tick -= ProcessPollingTimer_Tick;
            timer.Stop();
        }

        private void ProcessPollingTimer_Tick(object? sender, EventArgs e)
        {
            if (IsShuttingDown)
            {
                StopProcessPolling();
                return;
            }

            TrackMonitoringTask(PollProcessesAsync);
        }

        private Task PollProcessesAsync()
        {
            if (IsShuttingDown)
            {
                return Task.CompletedTask;
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(RobloxProcessName);
            }
            catch (InvalidOperationException)
            {
                return Task.CompletedTask;
            }
            catch (Win32Exception)
            {
                return Task.CompletedTask;
            }

            HashSet<int> activePids = new();
            try
            {
                foreach (Process process in processes)
                {
                    using (process)
                    {
                        try
                        {
                            if (!process.HasExited && process.Id > 0)
                            {
                                activePids.Add(process.Id);
                            }
                        }
                        catch (InvalidOperationException)
                        {
                        }
                        catch (Win32Exception)
                        {
                        }
                    }
                }
            }
            finally
            {
                lock (_lifecycleLock)
                {
                    _observedRobloxPids.RemoveWhere(pid => !activePids.Contains(pid));
                }
            }

            foreach (int processId in activePids)
            {
                bool isNewProcess;
                lock (_lifecycleLock)
                {
                    isNewProcess = _observedRobloxPids.Add(processId);
                }

                if (isNewProcess)
                {
                    TrackMonitoringTask(processId);
                }
            }

            return Task.CompletedTask;
        }

        private void TrackMonitoringTask(int processId)
        {
            TrackMonitoringTask(() => CheckMoitorLog(processId));
        }

        private void TrackMonitoringTask(Func<Task> monitoringOperation)
        {
            Task monitoringTask;
            lock (_monitoringTasksLock)
            {
                if (IsShuttingDown)
                {
                    return;
                }

                monitoringTask = Task.Run(monitoringOperation, _shutdownCts.Token);
                _monitoringTasks.Add(monitoringTask);
            }

            _ = ObserveMonitoringTaskAsync(monitoringTask);
        }

        private async Task ObserveMonitoringTaskAsync(Task monitoringTask)
        {
            try
            {
                await monitoringTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Unexpected monitoring task exception: {exception}");
            }
            finally
            {
                lock (_monitoringTasksLock)
                {
                    _monitoringTasks.Remove(monitoringTask);
                }
            }
        }

        public async Task CheckMoitorLog(int robloxProcessId)
        {
            if (robloxProcessId <= 0 || IsShuttingDown)
            {
                return;
            }

            if (!TryGetProcessStartTimeUtc(robloxProcessId, out DateTime processStartTimeUtc))
            {
                RemoveObservedProcess(robloxProcessId);
                return;
            }

            bool claimedLog = false;
            try
            {
                DateTime earliestCandidateTimeUtc = processStartTimeUtc - ProcessLogStartupTolerance;
                for (int i = 0; i < 30 && !IsShuttingDown; i++)
                {
                    foreach (FileInfo candidate in RobloxLogFiles())
                    {
                        DateTime candidateWriteTimeUtc;
                        string candidatePath;
                        try
                        {
                            candidateWriteTimeUtc = candidate.LastWriteTimeUtc;
                            candidatePath = candidate.FullName;
                        }
                        catch (IOException)
                        {
                            continue;
                        }

                        if (candidateWriteTimeUtc < earliestCandidateTimeUtc)
                        {
                            continue;
                        }

                        lock (_logLock)
                        {
                            if (!_claimedLogPaths.Add(candidatePath))
                            {
                                continue;
                            }

                            Last = candidate;
                            claimedLog = true;
                        }

                        await ReadFromLog(candidatePath, robloxProcessId);
                        return;
                    }

                    try
                    {
                        await Task.Delay(500, _shutdownCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
            finally
            {
                if (!claimedLog)
                {
                    RemoveObservedProcess(robloxProcessId);
                }
            }
        }

        private static bool TryGetProcessStartTimeUtc(int processId, out DateTime processStartTimeUtc)
        {
            processStartTimeUtc = default;
            try
            {
                using Process process = Process.GetProcessById(processId);
                processStartTimeUtc = process.StartTime.ToUniversalTime();
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        private void RemoveObservedProcess(int processId)
        {
            lock (_lifecycleLock)
            {
                _observedRobloxPids.Remove(processId);
            }
        }

        public async Task ReadFromLog(string filePath, int robloxProcessId)
        {
            string gameName = "Failed to obtain";
            string displayName = "Failed to obtain";
            string robloxUsername = "Failed to obtain";
            string? robloxAvatarUrl = null;
            bool obtainedRobloxDetails = false;
            Process robloxProcess;
            RobloxInstance? robloxInstance = null;

            try
            {
                robloxProcess = Process.GetProcessById(robloxProcessId);
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (Win32Exception)
            {
                return;
            }

            using (robloxProcess)
            {
                try
                {
                    while (!IsShuttingDown)
                    {
                        bool processExited;
                        try
                        {
                            processExited = robloxProcess.HasExited;
                        }
                        catch (InvalidOperationException)
                        {
                            break;
                        }
                        catch (Win32Exception)
                        {
                            break;
                        }

                        if (processExited)
                        {
                            break;
                        }

                        if (!obtainedRobloxDetails)
                        {
                            string[] data;
                            try
                            {
                                data = ReadViaShadowCopy(filePath);
                            }
                            catch (UnauthorizedAccessException)
                            {
                                data = Array.Empty<string>();
                            }
                            catch (IOException)
                            {
                                data = Array.Empty<string>();
                            }

                            Dictionary<string, long>? robloxDetails = GetRobloxDetails(data);
                            if (robloxDetails != null)
                            {
                                long universeId = robloxDetails["Universe"];
                                long userId = robloxDetails["UserID"];
                                string? universeIdText = universeId.ToString(CultureInfo.InvariantCulture);
                                string? userIdText = userId.ToString(CultureInfo.InvariantCulture);

                                try
                                {
                                    using HttpResponseMessage universeResponse = await RobloxAPI.GetAsync(
                                        $"https://games.roblox.com/v1/games?universeIds={universeIdText}",
                                        HttpCompletionOption.ResponseHeadersRead,
                                        _shutdownCts.Token);
                                    universeResponse.EnsureSuccessStatusCode();
                                    string universeResponseString = await universeResponse.Content.ReadAsStringAsync(_shutdownCts.Token);
                                    JObject universeJson = JObject.Parse(universeResponseString);
                                    if (universeJson["data"] is JArray universeData)
                                    {
                                        if (universeData.Count == 0)
                                        {
                                            gameName = "[Private experience]";
                                        }
                                        else if (universeData[0]?["name"]?.Type == JTokenType.String &&
                                                 !string.IsNullOrWhiteSpace(universeData[0]!["name"]!.Value<string>()))
                                        {
                                            gameName = universeData[0]!["name"]!.Value<string>()!;
                                        }
                                    }
                                }
                                catch (HttpRequestException)
                                {
                                }
                                catch (JsonException)
                                {
                                }
                                catch (TaskCanceledException) when (!IsShuttingDown)
                                {
                                }

                                try
                                {
                                    using HttpResponseMessage usernameResponse = await RobloxAPI.GetAsync(
                                        $"https://users.roblox.com/v1/users/{userIdText}",
                                        HttpCompletionOption.ResponseHeadersRead,
                                        _shutdownCts.Token);
                                    usernameResponse.EnsureSuccessStatusCode();
                                    string usernameResponseString = await usernameResponse.Content.ReadAsStringAsync(_shutdownCts.Token);
                                    JObject usernameJson = JObject.Parse(usernameResponseString);
                                    if (usernameJson["displayName"]?.Type == JTokenType.String &&
                                        usernameJson["name"]?.Type == JTokenType.String)
                                    {
                                        displayName = usernameJson["displayName"]!.Value<string>() ?? displayName;
                                        robloxUsername = usernameJson["name"]!.Value<string>() ?? robloxUsername;
                                    }
                                }
                                catch (HttpRequestException)
                                {
                                }
                                catch (JsonException)
                                {
                                }
                                catch (TaskCanceledException) when (!IsShuttingDown)
                                {
                                }

                                try
                                {
                                    using HttpResponseMessage avatarResponse = await RobloxAPI.GetAsync(
                                        $"https://thumbnails.roblox.com/v1/users/avatar-headshot?size=150x150&format=png&userIds={userIdText}",
                                        HttpCompletionOption.ResponseHeadersRead,
                                        _shutdownCts.Token);
                                    avatarResponse.EnsureSuccessStatusCode();
                                    string avatarResponseString = await avatarResponse.Content.ReadAsStringAsync(_shutdownCts.Token);
                                    JObject avatarJson = JObject.Parse(avatarResponseString);
                                    string? imageUrl = avatarJson["data"]?[0]?["imageUrl"]?.Value<string>();
                                    if (Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? avatarUri) &&
                                        avatarUri.Scheme == Uri.UriSchemeHttps &&
                                        (string.Equals(avatarUri.Host, AvatarHost, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(avatarUri.Host, AvatarCdnHost, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        robloxAvatarUrl = avatarUri.AbsoluteUri;
                                    }
                                }
                                catch (HttpRequestException)
                                {
                                }
                                catch (JsonException)
                                {
                                }
                                catch (TaskCanceledException) when (!IsShuttingDown)
                                {
                                }

                                robloxInstance = await AddRobloxInstanceAsync(
                                    robloxProcess,
                                    displayName,
                                    robloxUsername,
                                    gameName,
                                    robloxAvatarUrl);
                                obtainedRobloxDetails = true;
                            }
                        }

                        await Task.Delay(1000, _shutdownCts.Token);
                    }
                }
                catch (OperationCanceledException) when (IsShuttingDown)
                {
                }
                finally
                {
                    if (robloxInstance != null && !IsShuttingDown)
                    {
                        await RemoveRobloxInstanceAsync(robloxInstance);
                    }
                }
            }
        }

        private async Task<RobloxInstance?> AddRobloxInstanceAsync(
            Process robloxProcess,
            string displayName,
            string robloxUsername,
            string gameName,
            string? robloxAvatarUrl)
        {
            if (IsShuttingDown)
            {
                return null;
            }

            if (!Dispatcher.CheckAccess())
            {
                Task<RobloxInstance?> uiTask = await Dispatcher.InvokeAsync(() => AddRobloxInstanceAsync(
                    robloxProcess,
                    displayName,
                    robloxUsername,
                    gameName,
                    robloxAvatarUrl));
                return await uiTask;
            }

            if (IsShuttingDown)
            {
                return null;
            }

            RobloxInstance newInstance = new();
            WP1.Children.Add(newInstance);

            void KillRoblox()
            {
                try
                {
                    if (!robloxProcess.HasExited)
                    {
                        robloxProcess.Kill();
                    }
                }
                catch (InvalidOperationException)
                {
                    WP1.Children.Remove(newInstance);
                }
                catch (Win32Exception)
                {
                    WP1.Children.Remove(newInstance);
                }
            }

            newInstance.KilInstance.Click += (_, _) => KillRoblox();
            newInstance.DisplayName.Content = displayName;
            newInstance.FullUsername.Content = $"(@{robloxUsername})";
            newInstance.PidLabel.Content = $"PID: {robloxProcess.Id}";
            newInstance.GameName.Content = gameName;

            if (robloxAvatarUrl != null)
            {
                try
                {
                    BitmapImage bitmap = new();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(robloxAvatarUrl, UriKind.Absolute);
                    bitmap.EndInit();
                    newInstance.PFP.Source = bitmap;
                }
                catch (ArgumentException)
                {
                }
                catch (IOException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            UpdateSessionCountUI();
            await AnimateRobloxInstanceAsync(newInstance);
            return newInstance;
        }

        private async Task AnimateRobloxInstanceAsync(RobloxInstance newInstance)
        {
            if (IsShuttingDown)
            {
                return;
            }

            Fade(newInstance.PFP, 1, 0, 0);
            Fade(newInstance.DisplayName, 1, 0, 0);
            Fade(newInstance.FullUsername, 1, 0, 0);
            Fade(newInstance.GameName, 1, 0, 0);
            Fade(newInstance.KilInstance, 1, 0, 0);

            await Task.Delay(100, _shutdownCts.Token);
            Fade(newInstance.PFP, 0, 1, 0.5);
            Move(newInstance.PFP, new Thickness(newInstance.PFP.Margin.Left, newInstance.PFP.Margin.Top - 20, newInstance.PFP.Margin.Right, newInstance.PFP.Margin.Bottom), newInstance.PFP.Margin, 0.75);
            await Task.Delay(100, _shutdownCts.Token);
            Fade(newInstance.DisplayName, 0, 1, 0.5);
            Move(newInstance.DisplayName, new Thickness(newInstance.DisplayName.Margin.Left, newInstance.DisplayName.Margin.Top - 20, newInstance.DisplayName.Margin.Right, newInstance.DisplayName.Margin.Bottom), newInstance.DisplayName.Margin, 0.75);
            await Task.Delay(100, _shutdownCts.Token);
            Fade(newInstance.FullUsername, 0, 1, 0.5);
            Move(newInstance.FullUsername, new Thickness(newInstance.FullUsername.Margin.Left, newInstance.FullUsername.Margin.Top - 20, newInstance.FullUsername.Margin.Right, newInstance.FullUsername.Margin.Bottom), newInstance.FullUsername.Margin, 0.75);
            await Task.Delay(100, _shutdownCts.Token);
            Fade(newInstance.GameName, 0, 1, 0.5);
            Move(newInstance.GameName, new Thickness(newInstance.GameName.Margin.Left, newInstance.GameName.Margin.Top - 20, newInstance.GameName.Margin.Right, newInstance.GameName.Margin.Bottom), newInstance.GameName.Margin, 0.75);
            await Task.Delay(100, _shutdownCts.Token);
            Fade(newInstance.KilInstance, 0, 1, 0.5);
            Move(newInstance.KilInstance, new Thickness(newInstance.KilInstance.Margin.Left, newInstance.KilInstance.Margin.Top - 20, newInstance.KilInstance.Margin.Right, newInstance.KilInstance.Margin.Bottom), newInstance.KilInstance.Margin, 0.75);
        }

        private async Task RemoveRobloxInstanceAsync(RobloxInstance instance)
        {
            if (IsShuttingDown)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Task uiTask = await Dispatcher.InvokeAsync(() => RemoveRobloxInstanceAsync(instance));
                await uiTask;
                return;
            }

            if (WP1.Children.Contains(instance))
            {
                WP1.Children.Remove(instance);
                UpdateSessionCountUI();
            }
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                using HttpResponseMessage response = await RobloxAPI.GetAsync(
                    "https://raw.githubusercontent.com/Avaluate/MultipleRobloxInstances/refs/heads/main/UpdateAssets/Version",
                    HttpCompletionOption.ResponseHeadersRead,
                    _shutdownCts.Token);
                response.EnsureSuccessStatusCode();
                string versionText = await response.Content.ReadAsStringAsync(_shutdownCts.Token);
                string? onlineVersionText = versionText
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.Length > 0);

                if (onlineVersionText == null ||
                    !Regex.IsMatch(onlineVersionText, @"^\d+(?:\.\d+){1,3}$", RegexOptions.CultureInvariant) ||
                    !System.Version.TryParse(Version, out System.Version? currentVersion) ||
                    !System.Version.TryParse(onlineVersionText, out System.Version? onlineVersion) ||
                    onlineVersion.CompareTo(currentVersion) <= 0 ||
                    IsShuttingDown)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsShuttingDown)
                    {
                        UpdateAvailable.Text = "Update Available: Version " + onlineVersionText;
                        UpdateAvailable.Visibility = Visibility.Visible;
                    }
                });
            }
            catch (HttpRequestException)
            {
            }
            catch (JsonException)
            {
            }
            catch (TaskCanceledException) when (!IsShuttingDown)
            {
            }
            catch (OperationCanceledException) when (IsShuttingDown)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Unexpected update check task exception: {exception}");
            }
        }

        private static void OpenExternalUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        }

        private static void CloseRobloxProcesses()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(RobloxProcessName);
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (Win32Exception)
            {
                return;
            }

            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (Win32Exception)
                    {
                    }
                }
            }
        }

        private async Task ShutdownAsync()
        {
            Volatile.Write(ref _isShuttingDown, 1);
            _shutdownCts.Cancel();
            StopProcessPolling();
            DisposeLocks();

            Task[] monitoringTasks;
            lock (_monitoringTasksLock)
            {
                monitoringTasks = _monitoringTasks.ToArray();
            }

            Task? updateCheckTask = _updateCheckTask;
            if (updateCheckTask != null)
            {
                monitoringTasks = monitoringTasks.Append(updateCheckTask).ToArray();
            }

            if (monitoringTasks.Length == 0)
            {
                DisposeShutdownCts();
                return;
            }

            Task allTasks = Task.WhenAll(monitoringTasks);
            Task completedTask = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(3)));
            if (completedTask == allTasks)
            {
                await AwaitTrackedTasksAsync(allTasks);
                DisposeShutdownCts();
                return;
            }

            StartShutdownDrain(allTasks);
        }

        private async Task AwaitTrackedTasksAsync(Task allTasks)
        {
            try
            {
                await allTasks.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Unexpected shutdown task exception: {exception}");
            }
            finally
            {
                LogUnexpectedTaskExceptions(allTasks);
            }
        }

        private static void LogUnexpectedTaskExceptions(Task allTasks)
        {
            if (allTasks.Exception is not AggregateException aggregateException)
            {
                return;
            }

            foreach (Exception exception in aggregateException.Flatten().InnerExceptions)
            {
                if (exception is not OperationCanceledException &&
                    exception is not IOException &&
                    exception is not InvalidOperationException)
                {
                    Debug.WriteLine($"Unexpected shutdown task exception: {exception}");
                }
            }
        }

        private void StartShutdownDrain(Task allTasks)
        {
            if (Interlocked.Exchange(ref _shutdownDrainStarted, 1) != 0)
            {
                return;
            }

            _ = DrainShutdownTasksAsync(allTasks);
        }

        private async Task DrainShutdownTasksAsync(Task allTasks)
        {
            try
            {
                await AwaitTrackedTasksAsync(allTasks).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Unexpected shutdown drain exception: {exception}");
            }
            finally
            {
                DisposeShutdownCts();
            }
        }

        private void DisposeShutdownCts()
        {
            if (Interlocked.Exchange(ref _shutdownCtsDisposed, 1) != 0)
            {
                return;
            }

            try
            {
                _shutdownCts.Dispose();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Unexpected shutdown CTS disposal exception: {exception}");
            }
        }

        private void DisposeLocks()
        {
            try
            {
                RobloxCookieLock?.Dispose();
            }
            catch (IOException)
            {
            }
            finally
            {
                RobloxCookieLock = null;
            }

            try
            {
                RobloxLock?.Dispose();
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                RobloxLock = null;
            }
        }

        private async void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (Volatile.Read(ref _shutdownComplete) != 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _shutdownStarted, 1, 0) != 0)
            {
                e.Cancel = true;
                return;
            }

            e.Cancel = true;
            try
            {
                await ShutdownAsync();
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Unexpected window shutdown exception: {exception}");
            }
            finally
            {
                Volatile.Write(ref _shutdownComplete, 1);
                Close();
            }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            Volatile.Write(ref _isShuttingDown, 1);
            StopProcessPolling();
            DisposeLocks();
        }

        public MainWindow()
        {
            InitializeComponent();
            Closing += MainWindow_Closing;
            Closed += MainWindow_Closed;

            Last = MostRecentRobloxLogFile();

            // make sure Roblox is installed
            if (!Directory.Exists(RobloxPath))
            {
                MessageBox.Show($"Roblox does not exist or cannot be found at {RobloxPath}. Make sure you are using the official standard Roblox client (not Microsoft Store version).", "Roblox Not Found");
                Dispatcher.BeginInvoke(new Action(Close));
                return;
            }

            // check and see if roblox is open
            if (RobloxInstancesOpen() > 0)
            {
                MessageBoxResult result = MessageBox.Show("Multiple Roblox Instances needs to close existing Roblox processes before initializing.", "Existing Roblox Instances Detected\n\nClose all instances of Roblox to initialize Multiple Roblox Instances?", MessageBoxButton.YesNo);
                if (result == MessageBoxResult.Yes)
                {
                    CloseRobloxProcesses();
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(Close));
                    return;
                }
            }

            _updateCheckTask = CheckForUpdatesAsync();
            StartProcessPolling();

            try
            {
                using Mutex existingMutex = Mutex.OpenExisting("ROBLOX_singletonMutex");
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            try
            {
                RobloxLock = new Mutex(true, "ROBLOX_singletonMutex", out bool success);
                if (!success)
                {
                    MutexStatusText.Text = "MUTEX: UNLOCKED";
                    MessageBox.Show("Failed to lock Roblox singleton mutex. Another software (e.g. launcher) may already be using it.");
                }
                else
                {
                    MutexStatusText.Text = "MUTEX: LOCKED";
                }
            }
            catch (UnauthorizedAccessException)
            {
                MutexStatusText.Text = "MUTEX: DENIED";
                MessageBox.Show("Permission denied while locking Roblox singleton mutex.");
            }

            /*
             Voidstrap deals with error 773 using an ingenius method by making RobloxCookies.dat write-only, however we can lock the file to achieve the same result
             (so thank you Voidstrap for this idea)
             https://github.com/voidstrap/Voidstrap/blob/main/Bloxstrap/Utilities.cs
            */
            try
            {
                RobloxCookieLock = new FileStream(Path.Combine(RobloxPath, "LocalStorage", "RobloxCookies.dat"), FileMode.Open, FileAccess.Read, FileShare.None);
                CookieLockStatusText.Text = "COOKIE ISOLATION: ACTIVE";
            }
            catch (Exception)
            {
                CookieLockStatusText.Text = "COOKIE ISOLATION: OFF";
            }

            UpdateSessionCountUI();
        }

        // ------------------ //
        // ** TAB SWITCHING ** //
        // ------------------ //
        private void InstancesTabBtn_Click(object sender, RoutedEventArgs e)
        {
            InstancesView.Visibility = Visibility.Visible;
            InstructionsView.Visibility = Visibility.Collapsed;

            InstancesTabBtn.Background = ThemeBrush("BrushNavActiveBg");
            InstancesTabBtn.Foreground = ThemeBrush("BrushNavActiveText");
            InstancesTabBtn.BorderBrush = ThemeBrush("BrushAccent");
            InstancesTabBtn.BorderThickness = new Thickness(4, 0, 0, 0);

            InstructionsTabBtn.Background = Brushes.Transparent;
            InstructionsTabBtn.Foreground = ThemeBrush("BrushTextSecondary");
            InstructionsTabBtn.BorderBrush = Brushes.Transparent;
            InstructionsTabBtn.BorderThickness = new Thickness(4, 0, 0, 0);
        }

        private void InstructionsTabBtn_Click(object sender, RoutedEventArgs e)
        {
            InstancesView.Visibility = Visibility.Collapsed;
            InstructionsView.Visibility = Visibility.Visible;

            InstructionsTabBtn.Background = ThemeBrush("BrushNavActiveBg");
            InstructionsTabBtn.Foreground = ThemeBrush("BrushNavActiveText");
            InstructionsTabBtn.BorderBrush = ThemeBrush("BrushAccent");
            InstructionsTabBtn.BorderThickness = new Thickness(4, 0, 0, 0);

            InstancesTabBtn.Background = Brushes.Transparent;
            InstancesTabBtn.Foreground = ThemeBrush("BrushTextSecondary");
            InstancesTabBtn.BorderBrush = Brushes.Transparent;
            InstancesTabBtn.BorderThickness = new Thickness(4, 0, 0, 0);
        }

        private void TerminateAllBtn_Click(object sender, RoutedEventArgs e)
        {
            CloseRobloxProcesses();
            WP1.Children.Clear();
            UpdateSessionCountUI();
        }

        private void UpdateSessionCountUI()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(UpdateSessionCountUI);
                return;
            }

            int count = WP1.Children.Count;
            ActiveSessionsHeader.Content = count == 1 ? "1 Roblox instance currently running" : $"{count} Roblox instances currently running";
            EmptyStateGrid.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            InstancesScrollView.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            TrackMonitoringTask(PollProcessesAsync);
        }

        // ------------- //
        // ** TOP BAR ** //
        // ------------- //
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void Minimise_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Handling startup anim
        private async void Border_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Fade(MW, 1, 0, 0);
                Fade(InstancesTabBtn, 1, 0, 0);
                Fade(InstructionsTabBtn, 1, 0, 0);
                Fade(TerminateAllBtn, 1, 0, 0);
                Fade(Minimise, 1, 0, 0);
                Fade(CloseWindow, 1, 0, 0);

                await Task.Delay(100, _shutdownCts.Token);
                Fade(MW, 0, 1, 0.4);
                await Task.Delay(50, _shutdownCts.Token);
                Fade(InstancesTabBtn, 0, 1, 0.3);
                Fade(InstructionsTabBtn, 0, 1, 0.3);
                Fade(TerminateAllBtn, 0, 1, 0.3);
                Fade(Minimise, 0, 1, 0.3);
                Fade(CloseWindow, 0, 1, 0.3);
            }
            catch (OperationCanceledException) when (IsShuttingDown)
            {
            }
        }
    }
}
