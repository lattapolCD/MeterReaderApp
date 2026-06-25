using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using OpenCvSharp;
using AvaloniaWindow = Avalonia.Controls.Window;
using OcvRect = OpenCvSharp.Rect;
using OcvSize = OpenCvSharp.Size;
using OcvPoint = OpenCvSharp.Point;

namespace MeterReaderApp
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .StartWithClassicDesktopLifetime(args);
        }
    }

    class App : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = new MainWindow();
            base.OnFrameworkInitializationCompleted();
        }
    }

    class MainWindow : AvaloniaWindow
    {
        Image _videoImage = new Image { Stretch = Stretch.Uniform };
        TextBlock _statusText = new TextBlock { Text = "No video loaded", Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(10, 5) };
        TextBlock _digitText = new TextBlock { Text = "Digits: -  -  -  -  -  -", Foreground = Brushes.LightGreen, FontSize = 18, FontWeight = FontWeight.Bold, Margin = new Thickness(10, 5) };
        TextBlock _csvText = new TextBlock { Text = "CSV: Not set", Foreground = Brushes.LightBlue, FontSize = 12, Margin = new Thickness(10, 2), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        TextBox _meterIdBox = new TextBox
        {
            Watermark = "Enter Meter ID (optional)",
            FontSize = 13,
            Margin = new Thickness(10, 5),
            Background = new SolidColorBrush(Color.Parse("#45475a")),
            Foreground = Brushes.White,
        };
        ComboBox _cameraSelector = new ComboBox
        {
            PlaceholderText = "Select Camera Index",
            Margin = new Thickness(10, 5),
            Width = 150,
            Background = new SolidColorBrush(Color.Parse("#45475a")),
        };
        TextBox _ipCameraBox = new TextBox
        {
            Watermark = "rtsp://user:pass@192.168.1.10:554/stream",
            FontSize = 13,
            Margin = new Thickness(10, 5),
            Background = new SolidColorBrush(Color.Parse("#45475a")),
            Foreground = Brushes.White,
        };
        ComboBox _discoveredSelector = new ComboBox
        {
            PlaceholderText = "Discovered cameras",
            Margin = new Thickness(10, 5),
            Width = 330,
            Background = new SolidColorBrush(Color.Parse("#45475a")),
        };
        readonly List<(string label, string url)> _discovered = new();
        Slider _xSlider = new Slider { Minimum = 0, Maximum = 1280, Value = 144, Width = 260 };
        Slider _ySlider = new Slider { Minimum = 0, Maximum = 720, Value = 247, Width = 260 };
        Slider _wSlider = new Slider { Minimum = 10, Maximum = 800, Value = 237, Width = 260 };
        Slider _hSlider = new Slider { Minimum = 10, Maximum = 400, Value = 51, Width = 260 };

        string[] steadyDigits = { "?", "?", "?", "?", "?", "?" };
        int[] frameCounters = new int[6];
        int maxMeterValue = 0;
        int initFrames = 0;
        int logId = 1;
        string csvPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MeterLog.csv");
        DateTime lastLogTime = DateTime.MinValue;
        CancellationTokenSource? _cts;

        public MainWindow()
        {
            Title = "Meter Reader";
            Width = 1100;
            Height = 720;
            Background = new SolidColorBrush(Color.Parse("#1e1e2e"));
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "meterreader"));

            if (!File.Exists(csvPath))
                File.WriteAllText(csvPath, "ID,MeterID,Timestamp,VideoTime,MeterValue\n");

            for (int i = 0; i <= 4; i++)
                _cameraSelector.Items.Add($"Camera {i}");
            _cameraSelector.SelectedIndex = 0;

            var openBtn = MakeButton("Open Video", "#89b4fa");
            openBtn.Click += OnOpenVideo;

            var cameraBtn = MakeButton("Open Camera", "#89dceb");
            cameraBtn.Click += OnOpenCamera;

            var ipCameraBtn = MakeButton("Connect IP", "#94e2d5");
            ipCameraBtn.Click += OnOpenIpCamera;

            var findBtn = MakeButton("Find Cameras", "#f9e2af");
            findBtn.Click += OnFindCameras;

            _discoveredSelector.SelectionChanged += (_, _) =>
            {
                int i = _discoveredSelector.SelectedIndex;
                if (i >= 0 && i < _discovered.Count)
                    _ipCameraBox.Text = _discovered[i].url;
            };

            var stopBtn = MakeButton("Stop", "#f38ba8");
            stopBtn.Click += (_, _) =>
            {
                _cts?.Cancel();
                Dispatcher.UIThread.Post(() =>
                {
                    _digitText.Text = "Digits: -  -  -  -  -  -";
                    _statusText.Text = "Stopped";
                    _videoImage.Source = null;
                });
            };

            var setCsvBtn = MakeButton("Set CSV", "#cba6f7");
            setCsvBtn.Click += OnSetCsvLocation;

            var openCsvBtn = MakeButton("Open CSV", "#a6e3a1");
            openCsvBtn.Click += (_, _) =>
            {
                if (File.Exists(csvPath))
                    Process.Start(new ProcessStartInfo { FileName = csvPath, UseShellExecute = true });
            };

            var clearCsvBtn = MakeButton("Clear CSV", "#fab387");
            clearCsvBtn.Click += (_, _) =>
            {
                File.WriteAllText(csvPath, "ID,MeterID,Timestamp,VideoTime,MeterValue\n");
                logId = 1;
                Dispatcher.UIThread.Post(() => _statusText.Text = "CSV cleared");
            };

            var controls = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Background = new SolidColorBrush(Color.Parse("#313244")),
                Width = 360,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4), Children = { openBtn, stopBtn } },
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4), Children = { setCsvBtn, openCsvBtn, clearCsvBtn } },
                    _csvText,
                    new Separator { Margin = new Thickness(10, 4) },
                    new TextBlock { Text = "Camera", Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(10, 4, 10, 2) },
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4), Children = { _cameraSelector, cameraBtn } },
                    new TextBlock { Text = "IP Camera (RTSP / HTTP / MJPEG)", Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(10, 8, 10, 2) },
                    _ipCameraBox,
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4), Children = { ipCameraBtn, findBtn } },
                    _discoveredSelector,
                    new Separator { Margin = new Thickness(10, 4) },
                    new TextBlock { Text = "Meter ID", Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(10, 8, 10, 2) },
                    _meterIdBox,
                    _digitText,
                    _statusText,
                    new Separator { Margin = new Thickness(10, 5) },
                    new TextBlock { Text = "ROI Adjustment", Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(10, 5) },
                    MakeSliderRow("X", _xSlider),
                    MakeSliderRow("Y", _ySlider),
                    MakeSliderRow("Width", _wSlider),
                    MakeSliderRow("Height", _hSlider),
                }
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(360)));

            var videoBorder = new Border { Child = _videoImage, Background = Brushes.Black };
            Grid.SetColumn(videoBorder, 0);
            grid.Children.Add(videoBorder);

            var scroll = new ScrollViewer { Content = controls };
            Grid.SetColumn(scroll, 1);
            grid.Children.Add(scroll);

            Content = grid;
            _csvText.Text = $"CSV: {csvPath}";
        }

        Button MakeButton(string text, string color) => new Button
        {
            Content = text,
            FontSize = 13,
            Padding = new Thickness(10, 7),
            Margin = new Thickness(4),
            Background = new SolidColorBrush(Color.Parse(color)),
            Foreground = Brushes.Black
        };

        StackPanel MakeSliderRow(string label, Slider slider)
        {
            var val = new TextBlock { Width = 40, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            val.Text = ((int)slider.Value).ToString();
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property.Name == "Value") val.Text = ((int)slider.Value).ToString();
            };
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(10, 4),
                Children =
                {
                    new TextBlock { Text = label, Width = 55, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                    slider,
                    val
                }
            };
        }

        async void OnSetCsvLocation(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Choose CSV Save Location",
                SuggestedFileName = "MeterLog.csv",
                FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } }
            });

            if (file == null) return;
            csvPath = file.Path.LocalPath;
            if (!File.Exists(csvPath))
                File.WriteAllText(csvPath, "ID,MeterID,Timestamp,VideoTime,MeterValue\n");
            logId = 1;
            _csvText.Text = $"CSV: {csvPath}";
        }

        async void OnOpenVideo(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Video File",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.avi", "*.mov", "*.mkv" } }
                }
            });

            if (files.Count == 0) return;
            StartCapture(files[0].Path.LocalPath, -1, isLive: false);
        }

        void OnOpenCamera(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            int cameraIndex = _cameraSelector.SelectedIndex >= 0 ? _cameraSelector.SelectedIndex : 0;
            StartCapture(null, cameraIndex, isLive: true);
        }

        void OnOpenIpCamera(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            string url = _ipCameraBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(url))
            {
                _statusText.Text = "Enter an IP camera URL first";
                return;
            }
            _statusText.Text = "Connecting to IP camera...";
            StartCapture(url, -1, isLive: true);
        }

        async void OnFindCameras(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn != null) btn.IsEnabled = false;
            _statusText.Text = "Scanning network for cameras…";
            _discovered.Clear();
            _discoveredSelector.Items.Clear();

            // ip -> (port, protocol label). Port 554 (RTSP) wins over HTTP ports.
            var found = new Dictionary<string, (int port, string proto)>();

            try
            {
                await Task.Run(async () =>
                {
                    // 1) ONVIF WS-Discovery — cameras announce themselves over multicast.
                    foreach (var ip in await WsDiscoverAsync(2000))
                        lock (found) { if (!found.ContainsKey(ip)) found[ip] = (554, "ONVIF/RTSP"); }

                    // 2) TCP port scan of the local /24 for common camera ports.
                    string? baseIp = GetLocalSubnetBase();
                    if (baseIp != null)
                    {
                        int[] ports = { 554, 8554, 80, 8080 };
                        using var sem = new SemaphoreSlim(256);
                        var tasks = new List<Task>();
                        for (int i = 1; i <= 254; i++)
                        {
                            string ip = $"{baseIp}.{i}";
                            foreach (int p in ports)
                            {
                                await sem.WaitAsync();
                                tasks.Add(Task.Run(async () =>
                                {
                                    try
                                    {
                                        if (await IsPortOpenAsync(ip, p, 350))
                                        {
                                            string proto = (p == 554 || p == 8554) ? "RTSP" : "HTTP";
                                            lock (found)
                                            {
                                                bool preferNew = !found.ContainsKey(ip)
                                                    || (found[ip].port != 554 && p == 554);
                                                if (preferNew) found[ip] = (p, proto);
                                            }
                                        }
                                    }
                                    finally { sem.Release(); }
                                }));
                            }
                        }
                        await Task.WhenAll(tasks);
                    }
                });
            }
            catch (Exception ex)
            {
                _statusText.Text = "Scan error: " + ex.Message;
                if (btn != null) btn.IsEnabled = true;
                return;
            }

            foreach (var kv in found.OrderBy(k => k.Value.port == 554 ? 0 : 1).ThenBy(k => k.Key))
            {
                string ip = kv.Key;
                int port = kv.Value.port;
                string url = (port == 554 || port == 8554)
                    ? $"rtsp://{ip}:{port}/"
                    : $"http://{ip}:{port}/";
                string label = $"{ip}:{port}  ({kv.Value.proto})";
                _discovered.Add((label, url));
                _discoveredSelector.Items.Add(label);
            }

            if (_discovered.Count > 0)
            {
                _discoveredSelector.SelectedIndex = 0;
                _statusText.Text = $"Found {_discovered.Count} device(s). Pick one, then add path/credentials.";
            }
            else
            {
                _statusText.Text = "No cameras found on the local network.";
            }

            if (btn != null) btn.IsEnabled = true;
        }

        // Returns the first three octets of this machine's primary IPv4 address (e.g. "192.168.1").
        static string? GetLocalSubnetBase()
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Connect("8.8.8.8", 65530); // no packet is sent; this just selects the outbound interface
                if (s.LocalEndPoint is IPEndPoint ep)
                {
                    byte[] b = ep.Address.GetAddressBytes();
                    return $"{b[0]}.{b[1]}.{b[2]}";
                }
            }
            catch { }
            return null;
        }

        static async Task<bool> IsPortOpenAsync(string ip, int port, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(ip, port);
                var done = await Task.WhenAny(connect, Task.Delay(timeoutMs));
                if (done == connect)
                {
                    await connect; // surface/observe any connection exception
                    return client.Connected;
                }
            }
            catch { }
            return false;
        }

        // Sends an ONVIF WS-Discovery probe and collects the IPs of responding devices.
        static async Task<List<string>> WsDiscoverAsync(int timeoutMs)
        {
            var ips = new List<string>();
            string probe =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\" " +
                "xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
                "xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" " +
                "xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\">" +
                "<e:Header>" +
                $"<w:MessageID>uuid:{Guid.NewGuid()}</w:MessageID>" +
                "<w:To e:mustUnderstand=\"true\">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>" +
                "<w:Action e:mustUnderstand=\"true\">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>" +
                "</e:Header>" +
                "<e:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></e:Body>" +
                "</e:Envelope>";
            byte[] data = Encoding.UTF8.GetBytes(probe);

            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702);
                await udp.SendAsync(data, data.Length, multicast);

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    var receiveTask = udp.ReceiveAsync();
                    int remaining = (int)Math.Max(1, timeoutMs - sw.ElapsedMilliseconds);
                    var done = await Task.WhenAny(receiveTask, Task.Delay(remaining));
                    if (done != receiveTask) break;

                    string xml = Encoding.UTF8.GetString(receiveTask.Result.Buffer);
                    foreach (System.Text.RegularExpressions.Match m in
                        System.Text.RegularExpressions.Regex.Matches(xml, @"https?://([0-9]{1,3}(?:\.[0-9]{1,3}){3})"))
                    {
                        string ip = m.Groups[1].Value;
                        if (!ips.Contains(ip)) ips.Add(ip);
                    }
                }
            }
            catch { }
            return ips;
        }

        void StartCapture(string? source, int cameraIndex, bool isLive)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            steadyDigits = new[] { "?", "?", "?", "?", "?", "?" };
            frameCounters = new int[6];
            maxMeterValue = 0;
            initFrames = 0;
            lastLogTime = DateTime.MinValue;
            logId = 1;

            string label = source != null
                ? (isLive ? source : Path.GetFileName(source))
                : $"Camera {cameraIndex}";
            _statusText.Text = $"Loaded: {label}";

            _ = Task.Run(() =>
            {
                try { RunVideo(source, cameraIndex, isLive, _cts.Token); }
                catch (Exception ex) { Dispatcher.UIThread.Post(() => _statusText.Text = "Error: " + ex.Message); }
            });
        }

        void RunVideo(string? source, int cameraIndex, bool isLive, CancellationToken ct)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "meterreader");
            using var video = source != null
                ? new VideoCapture(source)
                : new VideoCapture(cameraIndex);

            if (!video.IsOpened())
            {
                Dispatcher.UIThread.Post(() => _statusText.Text = source != null
                    ? (isLive ? $"Failed to connect: {source}" : "Failed to open video")
                    : $"Failed to open Camera {cameraIndex} — try a different index");
                return;
            }

            bool isCamera = isLive;
            using Mat frame = new Mat();
            using Mat grayFull = new Mat();
            using Mat blurred = new Mat();
            using Mat edged = new Mat();
            long frameNum = 0;

            while (!ct.IsCancellationRequested)
            {
                if (!video.Read(frame) || frame.Empty())
                {
                    if (isCamera) { Thread.Sleep(10); continue; }
                    break;
                }

                frameNum++;
                double msec = isCamera ? frameNum * 33.3 : video.PosMsec;
                string timestamp = $"{(int)(msec/60000):D2}:{(int)((msec%60000)/1000):D2}.{(int)(msec%1000):D3}";

                int cX = 0, cY = 0, cW = 0, cH = 0;
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    cX = (int)_xSlider.Value;
                    cY = (int)_ySlider.Value;
                    cW = (int)_wSlider.Value;
                    cH = (int)_hSlider.Value;
                }).Wait();

                Cv2.CvtColor(frame, grayFull, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(grayFull, blurred, new OcvSize(5, 5), 0);
                Cv2.Canny(blurred, edged, 50, 150);
                Cv2.FindContours(edged, out OcvPoint[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                foreach (var c in contours)
                {
                    double sq = Cv2.ArcLength(c, true);
                    OcvPoint[] approx = Cv2.ApproxPolyDP(c, 0.02 * sq, true);
                    if (approx.Length == 4)
                    {
                        OcvRect r = Cv2.BoundingRect(approx);
                        double ar = (double)r.Width / r.Height;
                        if (ar > 3.0 && ar < 5.8 && r.Width > 140 && r.Height > 35 && r.Y > frame.Rows * 0.4)
                        {
                            cX = r.X; cY = r.Y; cW = r.Width; cH = r.Height;
                            break;
                        }
                    }
                }

                cX = Math.Clamp(cX, 0, frame.Cols - 1);
                cY = Math.Clamp(cY, 0, frame.Rows - 1);
                cW = Math.Min(cW, frame.Cols - cX);
                cH = Math.Min(cH, frame.Rows - cY);

                Cv2.Rectangle(frame, new OcvRect(cX, cY, cW, cH), new Scalar(0, 255, 0), 2);

                if (cW > 0 && cH > 0)
                {
                    using Mat roi = new Mat(frame, new OcvRect(cX, cY, cW, cH));
                    using Mat gray2 = new Mat();
                    using Mat bin = new Mat();
                    Cv2.CvtColor(roi, gray2, ColorConversionCodes.BGR2GRAY);
                    Cv2.Threshold(gray2, bin, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

                    int cols = bin.Cols, rows = bin.Rows;
                    int[] hist = new int[cols];
                    for (int x = 0; x < cols; x++)
                        for (int y = 0; y < rows; y++)
                            if (bin.At<byte>(y, x) == 255) hist[x]++;

                    int edw = cols / 6;
                    bool isInit = initFrames < 15;
                    if (isInit) initFrames++;

                    for (int idx = 0; idx < 6; idx++)
                    {
                        int ss = idx * edw, se = Math.Min(ss + edw, cols);
                        int best = ss, maxD = 0, ws = (int)(edw * 0.75);
                        for (int x = ss; x <= se - ws; x++)
                        {
                            int d = 0;
                            for (int wx = 0; wx < ws; wx++) d += hist[x + wx];
                            if (d > maxD) { maxD = d; best = x; }
                        }

                        OcvRect dr = new OcvRect(best, (int)(rows * 0.02), ws, (int)(rows * 0.96));
                        using Mat crop = new Mat(bin, dr);
                        if (!crop.Empty())
                        {
                            string digit = RunTesseract(crop, tempDir);
                            if (!string.IsNullOrEmpty(digit))
                            {
                                if (isInit || steadyDigits[idx] == "?")
                                    steadyDigits[idx] = digit;
                                else if (digit != steadyDigits[idx])
                                {
                                    int cur = int.Parse(digit), prev = int.Parse(steadyDigits[idx]);
                                    if (cur > prev || (prev == 9 && cur == 0))
                                        if (++frameCounters[idx] >= 4) { steadyDigits[idx] = digit; frameCounters[idx] = 0; }
                                }
                                else frameCounters[idx] = 0;
                            }
                        }
                    }

                    string final = string.Join("", steadyDigits);
                    if (!final.Contains("?") && int.TryParse(final, out int val))
                        if (val >= maxMeterValue) maxMeterValue = val;

                    string currentValue = string.Join("", steadyDigits);
                    if (!currentValue.Contains("?") && (DateTime.Now - lastLogTime).TotalSeconds >= 3)
                    {
                        lastLogTime = DateTime.Now;
                        string meterId = "-";
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            meterId = string.IsNullOrWhiteSpace(_meterIdBox.Text) ? "-" : _meterIdBox.Text.Trim();
                        }).Wait();
                        try
                        {
                            if (!File.Exists(csvPath))
                                File.WriteAllText(csvPath, "ID,MeterID,Timestamp,VideoTime,MeterValue\n");
                            File.AppendAllText(csvPath, $"{logId++},{meterId},{DateTime.Now:yyyy-MM-dd HH:mm:ss},{timestamp},{currentValue}\n");
                        }
                        catch { }
                    }
                }

                Cv2.PutText(frame, timestamp, new OcvPoint(20, 40), HersheyFonts.HersheySimplex, 0.8, Scalar.White, 2);
                if (isCamera)
                    Cv2.PutText(frame, "LIVE", new OcvPoint(20, 75), HersheyFonts.HersheySimplex, 0.8, new Scalar(0, 0, 255), 2);

                var bitmap = MatToBitmap(frame);
                string currentVal = string.Join("", steadyDigits).Contains("?") ? "------" : string.Join("", steadyDigits);
                string digits = string.Join("  ", steadyDigits).Replace("?", "-");
                string status = $"Time: {timestamp}  |  Value: {currentVal}  |  Max: {maxMeterValue}";

                Dispatcher.UIThread.Post(() =>
                {
                    _videoImage.Source = bitmap;
                    _digitText.Text = $"Digits: {digits}  =  {currentVal}";
                    _statusText.Text = status;
                });

                Thread.Sleep(25);
            }

            if (!isCamera)
                Dispatcher.UIThread.Post(() => _statusText.Text = $"Done. Max: {maxMeterValue}. CSV: {csvPath}");
        }

        Bitmap MatToBitmap(Mat mat)
        {
            using Mat rgb = new Mat();
            Cv2.CvtColor(mat, rgb, ColorConversionCodes.BGR2BGRA);
            var bmp = new WriteableBitmap(
                new Avalonia.PixelSize(rgb.Cols, rgb.Rows),
                new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Unpremul);
            using var fb = bmp.Lock();
            unsafe
            {
                Buffer.MemoryCopy(
                    (void*)rgb.DataPointer,
                    (void*)fb.Address,
                    fb.RowBytes * rgb.Rows,
                    fb.RowBytes * rgb.Rows);
            }
            return bmp;
        }

        string RunTesseract(Mat digitMat, string tempDir)
        {
            string imgPath = Path.Combine(tempDir, "digit.png");
            string outBase = Path.Combine(tempDir, "result");

            using Mat resized = new Mat();
            Cv2.Resize(digitMat, resized, new OcvSize(192, 192), 0, 0, InterpolationFlags.Cubic);

            using Mat bordered = new Mat();
            Cv2.CopyMakeBorder(resized, bordered, 30, 30, 30, 30, BorderTypes.Constant, new Scalar(0));

            using Mat dilated = new Mat();
            var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OcvSize(2, 2));
            Cv2.Dilate(bordered, dilated, kernel);

            Cv2.ImWrite(imgPath, dilated);

            string tesseractPath = OperatingSystem.IsWindows() ? "tesseract" : "/opt/homebrew/bin/tesseract";

            var psi = new ProcessStartInfo(tesseractPath,
                $"{imgPath} {outBase} --psm 10 --oem 1 -c tessedit_char_whitelist=0123456789")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();

            string resultFile = outBase + ".txt";
            if (!File.Exists(resultFile)) return "";
            string text = File.ReadAllText(resultFile).Trim();
            var match = System.Text.RegularExpressions.Regex.Match(text, @"\d");
            return match.Success ? match.Value : "";
        }
    }
}
