using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Windows;
using Microsoft.Win32;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace AutoMarkerReID.App;

public partial class CaptureLibraryWindow : Window
{
    private readonly IImageCodec _codec;
    private readonly ImageFrame? _latestCapture;
    private readonly string _captureDirectory;
    private List<CaptureListItem> _items = [];
    private ImageFrame? _currentImage;

    public CaptureLibraryWindow(IImageCodec codec, ImageFrame? latestCapture)
    {
        _codec = codec;
        _latestCapture = latestCapture;
        _captureDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        InitializeComponent();
        WindowsDarkMode.Apply(this);
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync(string? selectPath = null)
    {
        var items = new List<CaptureListItem>();
        if (_latestCapture is not null)
        {
            items.Add(new CaptureListItem("Ảnh vừa chụp", "Có thể xem ngay cả khi chưa lưu thành tệp", null, _latestCapture));
        }

        if (Directory.Exists(_captureDirectory))
        {
            var extensions = new HashSet<string>([".png", ".jpg", ".jpeg", ".bmp", ".webp"], StringComparer.OrdinalIgnoreCase);
            items.AddRange(Directory.EnumerateFiles(_captureDirectory)
                .Where(path => extensions.Contains(Path.GetExtension(path)))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTime)
                .Select(file => new CaptureListItem(
                    Path.GetFileNameWithoutExtension(file.Name),
                    $"{file.LastWriteTime:dd/MM/yyyy HH:mm:ss} · {FormatSize(file.Length)}",
                    file.FullName,
                    null)));
        }

        _items = items;
        CaptureList.ItemsSource = _items;
        var selectedIndex = selectPath is null
            ? (_items.Count > 0 ? 0 : -1)
            : _items.Select((item, index) => (item, index))
                .FirstOrDefault(pair => string.Equals(pair.item.Path, selectPath, StringComparison.OrdinalIgnoreCase)).index;
        CaptureList.SelectedIndex = selectedIndex;
        if (_items.Count == 0)
        {
            _currentImage = null;
            PreviewImage.Source = null;
            InfoText.Text = $"Chưa có ảnh nào trong {_captureDirectory}. Hãy bật tính năng tự động lưu ảnh chụp để tạo danh sách.";
        }
        await Task.CompletedTask;
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CaptureList.SelectedItem is not CaptureListItem item) return;
        try
        {
            _currentImage = item.SessionImage ?? _codec.Decode(await File.ReadAllBytesAsync(item.Path!));
            PreviewImage.Source = WpfImageConversion.ToBitmapSource(_currentImage);
            PreviewImage.Width = _currentImage.Width;
            PreviewImage.Height = _currentImage.Height;
            InfoText.Text = $"{item.DisplayName} · {_currentImage.Width} × {_currentImage.Height} px · Nhấp đúp để chỉnh sửa";
        }
        catch (Exception exception)
        {
            _currentImage = null;
            PreviewImage.Source = null;
            InfoText.Text = exception.Message;
        }
    }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_currentImage is null || CaptureList.SelectedItem is not CaptureListItem item) return;
        var editor = new ImageEditorWindow(_currentImage, _codec) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } edited) return;

        Directory.CreateDirectory(_captureDirectory);
        var baseName = item.Path is null ? $"ReID_{DateTime.Now:yyyyMMdd_HHmmss}" : Path.GetFileNameWithoutExtension(item.Path);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            InitialDirectory = _captureDirectory,
            FileName = baseName + "_edited.png",
        };
        if (dialog.ShowDialog(this) != true) return;
        await File.WriteAllBytesAsync(dialog.FileName, _codec.EncodePng(edited));
        await RefreshAsync(dialog.FileName);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OnPrevious(object sender, RoutedEventArgs e) { if (CaptureList.SelectedIndex > 0) CaptureList.SelectedIndex--; }
    private void OnNext(object sender, RoutedEventArgs e) { if (CaptureList.SelectedIndex + 1 < _items.Count) CaptureList.SelectedIndex++; }
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Left) OnPrevious(sender, e);
        else if (e.Key == Key.Right) OnNext(sender, e);
        else if (e.Key == Key.Enter) OnEdit(sender, e);
        else if (e.Key == Key.Escape) Close();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B",
    };

    private sealed record CaptureListItem(string DisplayName, string Detail, string? Path, ImageFrame? SessionImage);
}
