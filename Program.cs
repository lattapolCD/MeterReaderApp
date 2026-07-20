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
        Image _roiPreviewImage = new Image { Stretch = Stretch.Uniform };
        TextBlock _statusText = new TextBlock { Text = "No video loaded", Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(10, 5)  };
        TextBlock _digitText = new TextBlock { Text = "Digits: -  -  -  -  -  -", Foreground = Brushes.LightGreen, FontSize = 18, FontWeight = FontWeight.Bold, Margin = new Thickness(10, 5) };
        TextBlock _csvText = new TextBlock { Text = "CSV: Not set", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(10, 2), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        TextBox _meterIdBox = new TextBox
        {
            Watermark = "Enter Meter ID (optional)",
            FontSize = 13,
            Margin = new Thickness(10, 5),
            Background = new SolidColorBrush(Color.Parse("#ffffff")),
            Foreground = Brushes.Black,
            CornerRadius = new CornerRadius(8)
        };
        ComboBox _cameraSelector = new ComboBox
        {
            PlaceholderText = "Select Camera Index",
            Margin = new Thickness(10, 5),
            Width = 150,
            Background = new SolidColorBrush(Color.Parse("#4756f7")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(8)
        };
        TextBox _ipCameraBox = new TextBox
        {
            Watermark = "rtsp://user:pass@192.168.1.10:554/stream",
            FontSize = 13,
            Margin = new Thickness(10, 5),
            Background = new SolidColorBrush(Color.Parse("#cccccc")),
            Foreground = Brushes.Black,
            CornerRadius = new CornerRadius(8)
        };
        ComboBox _discoveredSelector = new ComboBox
        {
            PlaceholderText = "Discovered cameras",
            Margin = new Thickness(10, 5),
            Width = 330,
            Background = new SolidColorBrush(Color.Parse("#4756f7")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(8)
        };
        readonly List<(string label, string url)> _discovered = new();
        Slider _xSlider = new Slider { Minimum = 0, Maximum = 1280, Value = 144, Width = 260 };
        Slider _ySlider = new Slider { Minimum = 0, Maximum = 720, Value = 247, Width = 260 };
        Slider _wSlider = new Slider { Minimum = 10, Maximum = 800, Value = 237, Width = 260 };
        Slider _hSlider = new Slider { Minimum = 10, Maximum = 400, Value = 51, Width = 260 };

        string[] steadyDigits = { "?", "?", "?", "?", "?", "?" };
        const int DigitCount = 6;
        const int VoteWindow = 25;                 // frames of history per digit position
        const int ConfidenceThreshold = 90;        // % of the window that must agree, else show "?"
        const int MaxAdvance = 2;                   // a watched meter ticks up ~1 at a time; reject bigger jumps
        Queue<char>[] digitVotes = MakeVotes();
        string lastStable = "";                    // last fully-confident reading; held while a dial turns
        static Queue<char>[] MakeVotes() =>
            new[] { new Queue<char>(), new Queue<char>(), new Queue<char>(),
                    new Queue<char>(), new Queue<char>(), new Queue<char>() };
        int maxMeterValue = 0;
        int logId = 1;
        string csvPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "MeterLog.csv");
        DateTime lastLogTime = DateTime.MinValue;
        CancellationTokenSource? _cts;

        // --- Shared state between the fast display loop and the background OCR worker ---
        readonly object _roiLock = new();
        Mat? _pendingRoi;                 // newest ROI awaiting OCR; replaced (not queued) so OCR always works on the latest frame
        string _pendingTimestamp = "";
        volatile string _publishedValue = "------";
        volatile string _publishedDigitsLine = "-  -  -  -  -  -";
        // ROI rectangle, mirrored from the sliders so the capture thread never blocks on the UI thread
        volatile int _roiX = 144, _roiY = 247, _roiW = 237, _roiH = 51;
        volatile string _meterIdValue = "-";

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

            var openBtn = MakeButton("Open Video", "#4756f7","#ffffff");
            openBtn.Click += OnOpenVideo;

            var cameraBtn = MakeButton("Open Camera", "#4756f7","#ffffff");
            cameraBtn.Click += OnOpenCamera;

            var ipCameraBtn = MakeButton("Connect IP", "#4756f7","#ffffff");
            ipCameraBtn.Click += OnOpenIpCamera;

            var findBtn = MakeButton("Find Cameras", "#4756f7","#ffffff");
            findBtn.Click += OnFindCameras;

            _discoveredSelector.SelectionChanged += (_, _) =>
            {
                int i = _discoveredSelector.SelectedIndex;
                if (i >= 0 && i < _discovered.Count)
                    _ipCameraBox.Text = _discovered[i].url;
            };

            var stopBtn = MakeButton("Stop", "#f00606","#ffffff");
            stopBtn.Click += (_, _) =>
            {
                _cts?.Cancel();
                Dispatcher.UIThread.Post(() =>
                {
                    _digitText.Text = "Digits: -  -  -  -  -  -";
                    _statusText.Text = "Stopped";
                    _videoImage.Source = null;
                    _roiPreviewImage.Source = null;
                });
            };

            var setCsvBtn = MakeButton("Set CSV", "#4756f7","#ffffff");
            setCsvBtn.Click += OnSetCsvLocation;

            var openCsvBtn = MakeButton("Open CSV", "#4756f7","#ffffff");
            openCsvBtn.Click += (_, _) =>
            {
                if (File.Exists(csvPath))
                    Process.Start(new ProcessStartInfo { FileName = csvPath, UseShellExecute = true });
            };

            var clearCsvBtn = MakeButton("Clear CSV", "#000000","#ffffff");
            clearCsvBtn.Click += (_, _) =>
            {
                File.WriteAllText(csvPath, "ID,MeterID,Timestamp,VideoTime,MeterValue\n");
                logId = 1;
                Dispatcher.UIThread.Post(() => _statusText.Text = "CSV cleared");
            };

            var controls = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Background = new SolidColorBrush(Color.Parse("#2f354f")),
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
                    new TextBlock { Text = "Image sent to OCR", Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(10, 8, 10, 2) },
                    new Border { Child = _roiPreviewImage, Background = Brushes.Black, Height = 80, Margin = new Thickness(10, 2), CornerRadius = new CornerRadius(6) },
                    new Separator { Margin = new Thickness(10, 5) },
                    new TextBlock { Text = "ROI Adjustment", Foreground = new SolidColorBrush(Color.Parse("#ffffff")) , FontSize = 12, Margin = new Thickness(10, 5) },
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

            // Mirror UI values into fields so the capture/OCR threads read them without a blocking
            // hop back to the UI thread (that round-trip was a major source of video stutter).
            _xSlider.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") _roiX = (int)_xSlider.Value; };
            _ySlider.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") _roiY = (int)_ySlider.Value; };
            _wSlider.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") _roiW = (int)_wSlider.Value; };
            _hSlider.PropertyChanged += (_, e) => { if (e.Property.Name == "Value") _roiH = (int)_hSlider.Value; };
            _meterIdBox.TextChanged += (_, _) => _meterIdValue = string.IsNullOrWhiteSpace(_meterIdBox.Text) ? "-" : _meterIdBox.Text.Trim();
        }

        Button MakeButton(string text, string color,string colorbackground) => new Button
        {
            Content = text,
            FontSize = 13,
            Padding = new Thickness(10, 7),
            Margin = new Thickness(4),
            Background = new SolidColorBrush(Color.Parse(color)),
            Foreground = new SolidColorBrush(Color.Parse(colorbackground)),
            CornerRadius = new CornerRadius(8)
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
            digitVotes = MakeVotes();
            lastStable = "";
            maxMeterValue = 0;
            lastLogTime = DateTime.MinValue;
            logId = 1;
            _publishedValue = "------";
            _publishedDigitsLine = "-  -  -  -  -  -";
            lock (_roiLock) { _pendingRoi?.Dispose(); _pendingRoi = null; }

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

            // OCR runs on its own thread so a slow Tesseract pass never stalls video playback.
            var ocrTask = Task.Run(() => OcrWorker(tempDir, ct));
            var frameTimer = Stopwatch.StartNew();

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

                // ROI comes straight from the mirrored slider fields — no blocking hop to the UI thread.
                int cX = _roiX, cY = _roiY, cW = _roiW, cH = _roiH;

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

                // Hand the latest ROI (grayscale, captured BEFORE the green overlay is drawn so the box
                // never bleeds into what the OCR sees) to the worker. We replace any ROI it hasn't picked
                // up yet, so OCR always reads the freshest frame and never falls behind the video.
                if (cW > 0 && cH > 0)
                {
                    using var sub = new Mat(frame, new OcvRect(cX, cY, cW, cH));
                    var roiClone = new Mat();
                    Cv2.CvtColor(sub, roiClone, ColorConversionCodes.BGR2GRAY);
                    lock (_roiLock)
                    {
                        _pendingRoi?.Dispose();
                        _pendingRoi = roiClone;
                        _pendingTimestamp = timestamp;
                    }
                }

                Cv2.Rectangle(frame, new OcvRect(cX, cY, cW, cH), new Scalar(0, 255, 0), 2);
                Cv2.PutText(frame, timestamp, new OcvPoint(20, 40), HersheyFonts.HersheySimplex, 0.8, Scalar.White, 2);
                if (isCamera)
                    Cv2.PutText(frame, "LIVE", new OcvPoint(20, 75), HersheyFonts.HersheySimplex, 0.8, new Scalar(0, 0, 255), 2);

                var bitmap = MatToBitmap(frame);
                string currentVal = _publishedValue;
                string digits = _publishedDigitsLine;
                string status = $"Time: {timestamp}  |  Value: {currentVal}  |  Max: {maxMeterValue}";

                Dispatcher.UIThread.Post(() =>
                {
                    _videoImage.Source = bitmap;
                    _digitText.Text = $"Digits: {digits}  =  {currentVal}";
                    _statusText.Text = status;
                });

                // Pace playback. For files, match the source frame rate so it plays at real speed and
                // stays smooth; for a live camera, just yield briefly and take the freshest frame.
                if (isCamera)
                {
                    frameTimer.Restart();
                    Thread.Sleep(1);
                }
                else
                {
                    double fps = video.Fps;
                    if (fps <= 1 || double.IsNaN(fps)) fps = 30;
                    double remaining = 1000.0 / fps - frameTimer.Elapsed.TotalMilliseconds;
                    if (remaining > 1) Thread.Sleep((int)remaining);
                    frameTimer.Restart();
                }
            }

            try { ocrTask.Wait(1000); } catch { }

            if (!isCamera)
                Dispatcher.UIThread.Post(() => _statusText.Text = $"Done. Max: {maxMeterValue}. CSV: {csvPath}");
        }

        // Background OCR: pulls the most recent ROI handed over by the capture loop, reads the digit
        // strip, stabilises it by voting, logs to CSV, and publishes the preview image + reading. None
        // of this is on the display loop, so however slow Tesseract is, the video keeps playing.
        void OcrWorker(string tempDir, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Mat? job = null;
                string ts = "";
                lock (_roiLock)
                {
                    if (_pendingRoi != null) { job = _pendingRoi; _pendingRoi = null; ts = _pendingTimestamp; }
                }
                if (job == null) { Thread.Sleep(8); continue; }

                using (job)
                {
                    // Read the whole digit strip in one pass, then stabilise each position by voting
                    // over recent frames — this rejects wheels caught mid-rotation. `processed` is the
                    // exact (upscaled, thresholded) image fed to Tesseract.
                    Mat? processed = null;
                    string raw = "";
                    try { raw = ReadMeterStrip(job, tempDir, out processed); }
                    catch { }

                    // Show exactly what the OCR backend receives.
                    if (processed != null)
                    {
                        var preview = MatToBitmap(processed);
                        processed.Dispose();
                        Dispatcher.UIThread.Post(() => _roiPreviewImage.Source = preview);
                    }

                    // Accept full reads, or short-by-one reads (a fast wheel mid-roll often drops the
                    // last digit) — left-aligned, since the leading digits are the stable ones.
                    if (raw.Length == DigitCount || raw.Length == DigitCount - 1)
                    {
                        for (int i = 0; i < raw.Length; i++)
                        {
                            var q = digitVotes[i];
                            q.Enqueue(raw[i]);
                            while (q.Count > VoteWindow) q.Dequeue();
                        }
                    }

                    // Per-position candidate, gated by confidence (low agreement => uncertain).
                    var cand = new string[DigitCount];
                    for (int i = 0; i < DigitCount; i++)
                    {
                        var q = digitVotes[i];
                        if (q.Count < 3) { cand[i] = "?"; continue; }
                        var top = q.GroupBy(c => c).OrderByDescending(g => g.Count()).First();
                        int confidence = top.Count() * 100 / q.Count;   // how consistently recent frames agree
                        cand[i] = confidence >= ConfidenceThreshold ? top.Key.ToString() : "?";
                    }
                    string candStr = string.Join("", cand);

                    // A reading only advances once EVERY dial is confident. While any wheel is mid-roll
                    // (e.g. 272589 -> 272590) the value is held at the last settled reading, so a turning
                    // dial shows the lower value it is leaving, not the one it is rolling toward.
                    // The meter is watched continuously, so it only ticks up a step at a time: advance
                    // by at most MaxAdvance. This rejects sticky misreads of a half-rolled wheel (e.g.
                    // a "1" read as "4"/"9" giving 272594/272599) that a plain "counts up" rule would
                    // latch onto and never release.
                    if (!candStr.Contains('?') && int.TryParse(candStr, out int candVal))
                    {
                        if (lastStable.Length == 0)
                            lastStable = candStr;
                        else
                        {
                            int step = candVal - int.Parse(lastStable);
                            if (step >= 1 && step <= MaxAdvance) lastStable = candStr;
                        }
                    }

                    string display = lastStable.Length > 0 ? lastStable : candStr;
                    for (int i = 0; i < DigitCount; i++)
                        steadyDigits[i] = display[i].ToString();

                    string final = string.Join("", steadyDigits);
                    if (!final.Contains("?") && int.TryParse(final, out int val))
                        if (val >= maxMeterValue) maxMeterValue = val;

                    string currentValue = string.Join("", steadyDigits);
                    _publishedValue = currentValue.Contains("?") ? "------" : currentValue;
                    _publishedDigitsLine = string.Join("  ", steadyDigits).Replace("?", "-");

                    if (!currentValue.Contains("?") && (DateTime.Now - lastLogTime).TotalSeconds >= 3)
                    {
                        lastLogTime = DateTime.Now;
                        string meterId = _meterIdValue;
                        try
                        {
                            if (!File.Exists(csvPath))
                                File.WriteAllText(csvPath, "ID,MeterID,Timestamp,VideoTime,MeterValue\n");
                            File.AppendAllText(csvPath, $"{logId++},{meterId},{DateTime.Now:yyyy-MM-dd HH:mm:ss},{ts},{currentValue}\n");
                        }
                        catch { }
                    }
                }
            }
        }

        Bitmap MatToBitmap(Mat mat)
        {
            using Mat rgb = new Mat();
            Cv2.CvtColor(mat, rgb, mat.Channels() == 1 ? ColorConversionCodes.GRAY2BGRA : ColorConversionCodes.BGR2BGRA);
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

        // Reads the entire digit strip in one pass. Upscaling + Otsu + denoise gives Tesseract a
        // clean, undistorted text line (psm 7), which is far more reliable than slicing the strip
        // into fixed-width windows and reading each digit on its own.
        static string ReadMeterStrip(Mat roiGray, string tempDir, out Mat? processed)
        {
            processed = null;
            if (roiGray.Empty() || roiGray.Cols < 12 || roiGray.Rows < 8) return "";

            // Trim the wheel-divider lines at the far left/right (otherwise a vertical edge reads as a
            // stray "1") and shave the rims top/bottom.
            int mx = Math.Max(1, roiGray.Cols * 5 / 100);
            int my = Math.Max(1, roiGray.Rows * 6 / 100);
            using Mat strip = new Mat(roiGray, new OcvRect(mx, my, roiGray.Cols - 2 * mx, roiGray.Rows - 2 * my));

            using Mat up = new Mat();
            Cv2.Resize(strip, up, new OcvSize(0, 0), 4, 4, InterpolationFlags.Cubic);

            using Mat blur = new Mat();
            Cv2.GaussianBlur(up, blur, new OcvSize(3, 3), 0);

            using Mat bin = new Mat();
            Cv2.Threshold(blur, bin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu); // dark digits on light

            // Drop salt-and-pepper specks left by thresholding.
            using Mat clean = new Mat();
            Cv2.MedianBlur(bin, clean, 3);

            using Mat bordered = new Mat();
            Cv2.CopyMakeBorder(clean, bordered, 24, 24, 24, 24, BorderTypes.Constant, new Scalar(255));

            // Hand back a copy of the exact image Tesseract is about to read, for the UI preview.
            processed = bordered.Clone();

            string imgPath = Path.Combine(tempDir, "strip.png");
            string outBase = Path.Combine(tempDir, "strip_out");
            string boxFile = outBase + ".box";
            Cv2.ImWrite(imgPath, bordered);
            try { File.Delete(boxFile); } catch { }

            string tesseractPath = OperatingSystem.IsWindows() ? "tesseract" : "/opt/homebrew/bin/tesseract";

            // "makebox" emits one line per character with its bounding box, which lets us discard
            // anomalously thin glyphs — frame dividers that Tesseract mistakes for a "1".
            var psi = new ProcessStartInfo(tesseractPath,
                $"\"{imgPath}\" \"{outBase}\" --psm 7 --oem 1 -c tessedit_char_whitelist=0123456789 makebox")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();

            if (!File.Exists(boxFile)) return "";

            // Box line: "<char> <x0> <y0> <x1> <y1> <page>", ordered left-to-right.
            var glyphs = new List<(char ch, int width)>();
            foreach (string line in File.ReadAllLines(boxFile))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 5 && p[0].Length == 1 && char.IsDigit(p[0][0])
                    && int.TryParse(p[1], out int x0) && int.TryParse(p[3], out int x1))
                    glyphs.Add((p[0][0], Math.Abs(x1 - x0)));
            }
            if (glyphs.Count == 0) return "";

            // Drop glyphs far narrower than the median digit — these are divider lines, not digits
            // (a real "1" is ~half a digit wide, a divider is much thinner).
            var widths = glyphs.Select(g => g.width).OrderBy(w => w).ToList();
            int median = widths[widths.Count / 2];
            int minWidth = Math.Max(1, median * 35 / 100);
            var kept = glyphs.Where(g => g.width >= minWidth).ToList();

            // The meter has DigitCount wheels; if extra glyphs survive (a divider the width filter
            // missed, or a partial neighbouring wheel) drop the narrowest until the count matches.
            // This keeps the digits aligned, instead of blindly trimming the end and shifting them.
            while (kept.Count > DigitCount)
            {
                int minIdx = 0;
                for (int j = 1; j < kept.Count; j++) if (kept[j].width < kept[minIdx].width) minIdx = j;
                kept.RemoveAt(minIdx);
            }
            return new string(kept.Select(g => g.ch).ToArray());
        }
    }
}
