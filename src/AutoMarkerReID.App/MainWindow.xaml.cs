using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AutoMarkerReID.App;

public partial class MainWindow : Window, IDisposable
{
    private readonly ApplicationController _controller;
    private readonly IScreenCaptureService _captureService;
    private readonly IClipboardMonitor _clipboardMonitor;
    private readonly IClipboardWriter _clipboardWriter;
    private readonly ICandidateGenerator _candidateGenerator;
    private readonly UserSelectionState _selection;
    private readonly MainViewModel _viewModel;
    private readonly IImageCodec _codec;
    private readonly IInterfaceDetector _interfaceDetector;
    private readonly IResultRepository _resultRepository;
    private readonly IBoxRenderer _boxRenderer;
    private readonly IQueryRepository _queryRepository;
    private readonly Forms.NotifyIcon _trayIcon;
    private System.Drawing.Icon _trayDrawingIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly GlobalHotkeyManager _hotkeys = new();
    private readonly GlobalMouseHook _mouseHook = new();
    private readonly List<HotkeyRegistration> _hotkeyRegistrations = [];
    private BoundingBox? _lastCaptureRegion;
    private ImageFrame? _latestCapture;
    private bool _allowClose;

    public MainWindow(
        MainViewModel viewModel,
        ApplicationController controller,
        IScreenCaptureService captureService,
        IClipboardMonitor clipboardMonitor,
        IClipboardWriter clipboardWriter,
        ICandidateGenerator candidateGenerator,
        UserSelectionState selection,
        IImageCodec codec,
        IInterfaceDetector interfaceDetector,
        IResultRepository resultRepository,
        IBoxRenderer boxRenderer,
        IQueryRepository queryRepository)
    {
        _controller = controller;
        _captureService = captureService;
        _clipboardMonitor = clipboardMonitor;
        _clipboardWriter = clipboardWriter;
        _candidateGenerator = candidateGenerator;
        _selection = selection;
        _viewModel = viewModel;
        _codec = codec;
        _interfaceDetector = interfaceDetector;
        _resultRepository = resultRepository;
        _boxRenderer = boxRenderer;
        _queryRepository = queryRepository;
        InitializeComponent();
        WindowsDarkMode.Apply(this);
        DataContext = viewModel;
        viewModel.CaptureRequested += (_, _) => _ = StartCaptureAsync();
        viewModel.RepeatCaptureRequested += (_, _) => _ = RepeatCaptureAsync();
        viewModel.LibraryRequested += (_, _) => OpenLibrary();
        viewModel.EditImageRequested += (_, _) => OpenExternalImage();
        viewModel.LatestCaptureRequested += (_, _) => OpenLatestCapture();
        viewModel.DeleteQueryRequested += (_, _) => _ = DeleteSelectedQueryAsync();
        viewModel.HotkeyDetailsRequested += (_, _) => ShowHotkeyDetails();
        viewModel.SelectionFeedback += (_, message) => ShowOsd(message);
        viewModel.ConfirmCacheRebuild = () => DarkMessageBox.Show(this,
            "Tạo lại toàn bộ dữ liệu đặc trưng nhận diện và OCR? Ảnh tham chiếu trong Query vẫn được giữ nguyên.",
            "Tạo lại dữ liệu AI", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        viewModel.ActivityLog.CollectionChanged += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        });
        _controller.ReviewRequested += OnReviewRequested;
        SourceInitialized += (_, _) => RegisterInputHooks();
        (_trayIcon, _trayDrawingIcon, _trayMenu) = CreateTrayIcon();
        _controller.StateChanged += OnTrayStateChanged;
        UpdateTrayState(_controller.State, null);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    public void Dispose()
    {
        _trayIcon.Visible = false;
        _controller.StateChanged -= OnTrayStateChanged;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        _trayDrawingIcon.Dispose();
        _hotkeys.Dispose();
        _mouseHook.Dispose();
        GC.SuppressFinalize(this);
    }

    private (Forms.NotifyIcon NotifyIcon, System.Drawing.Icon DrawingIcon, Forms.ContextMenuStrip Menu) CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Mở cửa sổ chính", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Chọn vùng chụp", null, (_, _) => Dispatcher.InvokeAsync(StartCaptureAsync));
        menu.Items.Add("Chụp lại vùng cũ", null, (_, _) => Dispatcher.InvokeAsync(RepeatCaptureAsync));
        var queryMenu = new Forms.ToolStripMenuItem("Chọn phạm vi nhận diện");
        queryMenu.DropDownItems.Add("Tất cả Query", null, (_, _) => Dispatcher.Invoke(_viewModel.SelectRoot));
        for (var index = 1; index <= 14; index++)
        {
            var position = index;
            queryMenu.DropDownItems.Add($"Query_{index}", null, (_, _) => Dispatcher.Invoke(() => _viewModel.SelectQueryPosition(position)));
        }
        menu.Items.Add(queryMenu);
        var lbpItem = new Forms.ToolStripMenuItem("Hỗ trợ đối chiếu trang phục (LBP)") { CheckOnClick = true };
        lbpItem.CheckedChanged += (_, _) => Dispatcher.Invoke(() => _viewModel.AppearanceEnabled = lbpItem.Checked);
        menu.Items.Add(lbpItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Khởi động lại ứng dụng", null, (_, _) => Dispatcher.Invoke(Restart));
        menu.Items.Add("Thoát ứng dụng", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        var drawingIcon = CreateStatusIcon(_controller.State);
        var notifyIcon = new Forms.NotifyIcon
        {
            Icon = drawingIcon,
            Text = "AutoMarker Re-ID",
            ContextMenuStrip = menu,
            Visible = true,
        };
        notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return (notifyIcon, drawingIcon, menu);
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            using var icon = new System.Drawing.Icon(iconPath);
            return (System.Drawing.Icon)icon.Clone();
        }

        if (Environment.ProcessPath is { } executable && System.Drawing.Icon.ExtractAssociatedIcon(executable) is { } associated)
            return associated;

        return (System.Drawing.Icon)SystemIcons.Application.Clone();
    }

    private static System.Drawing.Icon CreateStatusIcon(AppRuntimeState state)
    {
        using var baseIcon = LoadApplicationIcon();
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.DrawIcon(baseIcon, new Rectangle(0, 0, 32, 32));
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var color = state switch
            {
                AppRuntimeState.Monitoring => System.Drawing.Color.FromArgb(34, 197, 94),
                AppRuntimeState.Error => System.Drawing.Color.FromArgb(239, 68, 68),
                _ => System.Drawing.Color.FromArgb(245, 158, 11),
            };
            using var outline = new SolidBrush(System.Drawing.Color.FromArgb(245, 15, 23, 42));
            using var fill = new SolidBrush(color);
            graphics.FillEllipse(outline, 17, 17, 15, 15);
            graphics.FillEllipse(fill, 19, 19, 11, 11);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private void OnTrayStateChanged(object? sender, AppStateChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() => UpdateTrayState(e.State, e.Error));

    private void UpdateTrayState(AppRuntimeState state, string? error)
    {
        var replacement = CreateStatusIcon(state);
        var previous = _trayDrawingIcon;
        _trayDrawingIcon = replacement;
        _trayIcon.Icon = replacement;
        _trayIcon.Text = state switch
        {
            AppRuntimeState.Monitoring => "AutoMarker Re-ID · Đang theo dõi",
            AppRuntimeState.Processing => "AutoMarker Re-ID · Đang nhận diện",
            AppRuntimeState.Error => $"AutoMarker Re-ID · {Truncate(error ?? "Lỗi", 42)}",
            _ => $"AutoMarker Re-ID · {state}",
        };
        previous.Dispose();
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);

    private void OnReviewRequested(object? sender, ReviewRequestedEventArgs args)
    {
        args.MarkHandled();
        Dispatcher.InvokeAsync(() =>
        {
            var best = args.Session.Matches.OrderByDescending(match => match.Score).FirstOrDefault();
            ShowOsd(best is null ? "Không có kết quả đạt ngưỡng" : $"{best.QueryId} · {best.Score:P0}");
            var review = new ReviewWindow(args.Session, _candidateGenerator, _selection, _codec, _boxRenderer) { Owner = IsVisible ? this : null };
            review.ShowDialog();
            args.Complete(review.Outcome ?? new ReviewOutcome(ReviewDecision.Cancel));
        });
    }

    private async Task StartCaptureAsync()
    {
        if (!_controller.TryBeginCapture()) return;
        Hide();
        await Task.Delay(120);
        var overlay = new CaptureOverlayWindow(_captureService.VirtualScreenBounds);
        try
        {
            if (overlay.ShowDialog() == true && overlay.SelectedRegion is { } region)
            {
                _lastCaptureRegion = region;
                _controller.EndCapture();
                await CaptureRegionAsync(region, ImageJobSource.NewCapture);
            }
        }
        finally
        {
            _controller.EndCapture();
        }
    }

    private async Task RepeatCaptureAsync()
    {
        if (_lastCaptureRegion is not { } region || !_controller.TryBeginCapture()) return;
        _controller.EndCapture();
        await CaptureRegionAsync(region, ImageJobSource.RepeatCapture);
    }

    private async Task CaptureRegionAsync(BoundingBox region, ImageJobSource source)
    {
        var image = await _captureService.CaptureAsync(region, CancellationToken.None);
        _latestCapture = image;
        if (image.Width > image.Height && !_interfaceDetector.IsReIdInterface(image, out _))
        {
            var editor = new ImageEditorWindow(image, _codec) { Owner = IsVisible ? this : null };
            if (editor.ShowDialog() == true && editor.Result is { } edited) image = edited;
            else return;
        }
        // Persist and publish the final image. The editor is part of the
        // capture flow, so saving before it opened silently wrote the original.
        var sourcePath = _selection.SaveCaptures ? await SaveScreenshotAsync(image) : null;
        _clipboardMonitor.IgnoreNextWrite();
        await _clipboardWriter.WriteImageAsync(image, CancellationToken.None);
        _controller.TryQueue(ImageJob.Create(image, source, sourcePath));
    }

    private async Task<string> SaveScreenshotAsync(ImageFrame image)
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var directory = Path.Combine(pictures, "Screenshots");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"ReID_{DateTime.Now:yyyyMMdd_HHmmss_ffffff}.png");
        await File.WriteAllBytesAsync(file, _codec.EncodePng(image));
        return file;
    }

    private void OpenLibrary()
    {
        var window = new LibraryWindow(_resultRepository, _codec, _boxRenderer, _clipboardWriter, _clipboardMonitor, _candidateGenerator)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    private void OpenExternalImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Ảnh|*.png;*.jpg;*.jpeg;*.bmp;*.webp|Tất cả file|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var image = _codec.Decode(File.ReadAllBytes(dialog.FileName));
            OpenStandaloneEditor(image, Path.GetFileNameWithoutExtension(dialog.FileName) + "_edited.png");
        }
        catch (Exception exception)
        {
            DarkMessageBox.Show(this, exception.Message, "Chỉnh sửa ảnh", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLatestCapture()
    {
        var window = new CaptureLibraryWindow(_codec, _latestCapture)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    private void OpenStandaloneEditor(ImageFrame image, string suggestedName)
    {
        var editor = new ImageEditorWindow(image, _codec) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } edited) return;
        var save = new Microsoft.Win32.SaveFileDialog { Filter = "PNG|*.png", FileName = suggestedName, DefaultExt = ".png" };
        if (save.ShowDialog(this) == true) File.WriteAllBytes(save.FileName, _codec.EncodePng(edited));
    }

    private async Task DeleteSelectedQueryAsync()
    {
        var scope = _viewModel.SelectedQuery?.Id;
        var label = scope ?? "toàn bộ Query";
        var answer = DarkMessageBox.Show(this,
            $"Xóa ảnh trong {label}, dữ liệu AI tương ứng và chuyển toàn bộ kết quả đã lưu vào Thùng rác?",
            "Xóa dữ liệu Query", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await _queryRepository.DeleteScopeAsync(scope, CancellationToken.None);
            foreach (var result in await _resultRepository.ListAsync(CancellationToken.None))
                await _resultRepository.MoveToRecycleBinAsync(result, CancellationToken.None);
            await _controller.RebuildCacheAsync(null, CancellationToken.None);
            _viewModel.RefreshQueries();
            ShowOsd($"Đã chuyển {label} vào Thùng rác");
        }
        catch (Exception exception)
        {
            DarkMessageBox.Show(this, exception.Message, "Xóa dữ liệu Query", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RegisterHotkeys()
    {
        _hotkeys.Attach(this);
        _hotkeys.Pressed += OnHotkeyPressed;
        Register("PreviousQuery", HotkeyModifiers.Control | HotkeyModifiers.Shift, Key.A);
        Register("NextQuery", HotkeyModifiers.Control | HotkeyModifiers.Shift, Key.D);
        Register("RootQuery", HotkeyModifiers.Control | HotkeyModifiers.Shift, Key.Q);
        Register("EmptyQuery", HotkeyModifiers.Control | HotkeyModifiers.Shift, Key.N);
        for (var index = 1; index <= 9; index++)
            Register($"Query{index}", HotkeyModifiers.Control | HotkeyModifiers.Shift, (Key)((int)Key.D0 + index));
        Register("RepeatCapture", HotkeyModifiers.Alt, Key.S);
        Register("IntegrationCapture", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, Key.F10);

        var captureFallbacks = new[]
        {
            new HotkeyBinding("NewCapture", HotkeyModifiers.Alt, Key.PrintScreen),
            new HotkeyBinding("NewCapture", HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.PrintScreen),
            new HotkeyBinding("NewCapture", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, Key.R),
        };
        foreach (var fallback in captureFallbacks)
        {
            var result = _hotkeys.Register(fallback);
            _hotkeyRegistrations.Add(result);
            if (result.Registered) break;
        }

        var active = _hotkeyRegistrations.Count(item => item.Registered);
        var failed = _hotkeyRegistrations.Count(item => !item.Registered);
        _viewModel.HotkeyStatus = $"Phím tắt: {active} khả dụng{(failed > 0 ? $", {failed} không khả dụng" : string.Empty)}";
    }

    private void RegisterInputHooks()
    {
        RegisterHotkeys();
        try
        {
            _mouseHook.BlazeRightClicked += (_, _) => Dispatcher.InvokeAsync(StartCaptureAsync);
            _mouseHook.Start();
        }
        catch (Win32Exception exception)
        {
            _viewModel.HotkeyStatus += $" · Theo dõi chuột gặp lỗi {exception.NativeErrorCode}";
        }
    }

    private void Register(string name, HotkeyModifiers modifiers, Key key) =>
        _hotkeyRegistrations.Add(_hotkeys.Register(new HotkeyBinding(name, modifiers, key)));

    private void OnHotkeyPressed(object? sender, HotkeyBinding binding)
    {
        Dispatcher.InvokeAsync(() =>
        {
            switch (binding.Name)
            {
                case "PreviousQuery": _viewModel.SelectPrevious(); break;
                case "NextQuery": _viewModel.SelectNext(); break;
                case "RootQuery": _viewModel.SelectRoot(); break;
                case "EmptyQuery" when ForegroundApplication.IsBlazeOrExcel(): _viewModel.SelectEmptyQuery(); break;
                case "NewCapture":
                case "IntegrationCapture": _ = StartCaptureAsync(); break;
                case "RepeatCapture": _ = RepeatCaptureAsync(); break;
                default:
                    if (binding.Name.StartsWith("Query", StringComparison.Ordinal) &&
                        int.TryParse(binding.Name.AsSpan("Query".Length), out var position))
                        _viewModel.SelectQueryPosition(position);
                    break;
            }
        });
    }

    private void ShowHotkeyDetails()
    {
        var lines = _hotkeyRegistrations.Select(item =>
            $"{item.Binding.Gesture}: {(item.Registered ? "Sẵn sàng" : $"Không khả dụng (mã lỗi {item.ErrorCode})")}");
        DarkMessageBox.Show(this, string.Join(Environment.NewLine, lines), "Phím tắt toàn cục",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void ShowOsd(string message)
    {
        var osd = new Window
        {
            Width = 340,
            Height = 70,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 17, 24, 39)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18),
                Child = new System.Windows.Controls.TextBlock
                {
                    Text = message,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 18,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                },
            },
        };
        osd.Show();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { timer.Stop(); osd.Close(); };
        timer.Start();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Restart()
    {
        if (Environment.ProcessPath is { } executable)
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        ExitApplication();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }
}
