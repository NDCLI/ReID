using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.IO;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Imaging;
using AutoMarkerReID.Windows;
using Microsoft.Win32;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace AutoMarkerReID.App;

public partial class ImageEditorWindow : Window
{
    private readonly IImageCodec _codec;
    private readonly ImageFrame _original;
    private readonly Stack<ImageFrame> _undo = [];
    private ImageFrame _current;
    private WpfPoint? _dragStart;

    public ImageEditorWindow(ImageFrame image, IImageCodec codec)
    {
        _original = image;
        _current = image;
        _codec = codec;
        InitializeComponent();
        WindowsDarkMode.Apply(this);
        RefreshImage();
    }

    public ImageFrame? Result { get; private set; }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Receive the drag from the whole preview viewport, then clamp the
        // coordinates back to the image just like the legacy canvas editor.
        // This lets users start or finish the gesture outside the image.
        _dragStart = Clamp(e.GetPosition(ImageSurface));
        Mouse.Capture(this, CaptureMode.SubTree);
        UpdateSelection(_dragStart.Value, _dragStart.Value);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_dragStart is { } start && e.LeftButton == MouseButtonState.Pressed)
            UpdateSelection(start, Clamp(e.GetPosition(ImageSurface)));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start) return;
        var end = Clamp(e.GetPosition(ImageSurface));
        var removeVerticalStrip = Math.Abs(end.X - start.X) > Math.Abs(end.Y - start.Y);
        _dragStart = null;
        Mouse.Capture(null);
        Selection.Visibility = Visibility.Collapsed;
        var box = new BoundingBox((int)Math.Min(start.X, end.X), (int)Math.Min(start.Y, end.Y),
            (int)Math.Max(start.X, end.X), (int)Math.Max(start.Y, end.Y));
        // Cut-out follows the drag direction: a short movement in the other
        // axis is enough to select a strip, matching the legacy editor.
        if (CutMode.IsChecked != true && (box.Width < 5 || box.Height < 5)) return;
        if (CutMode.IsChecked == true && box.Width < 5 && box.Height < 5) return;
        try
        {
            _undo.Push(_current);
            _current = CropMode.IsChecked == true
                ? _codec.Crop(_current, box)
                : ImageEditorOperations.CutOut(_current, box, removeVerticalStrip);
            RefreshImage();
        }
        catch (Exception exception)
        {
            if (_undo.Count > 0) _undo.Pop();
            DarkMessageBox.Show(this, exception.Message, "Chỉnh sửa ảnh", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLostMouseCapture(object sender, WpfMouseEventArgs e)
    {
        if (_dragStart is null) return;
        _dragStart = null;
        Selection.Visibility = Visibility.Collapsed;
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo.TryPop(out var image)) { _current = image; RefreshImage(); }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _undo.Push(_current);
        _current = _original;
        RefreshImage();
    }

    private void OnMergeLeft(object sender, RoutedEventArgs e) => Merge(true);
    private void OnMergeRight(object sender, RoutedEventArgs e) => Merge(false);

    private void Merge(bool onLeft)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Ảnh|*.png;*.jpg;*.jpeg;*.bmp;*.webp|Tất cả file|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var other = _codec.Decode(File.ReadAllBytes(dialog.FileName));
            _undo.Push(_current);
            _current = ImageEditorOperations.Merge(_current, other, onLeft);
            RefreshImage();
        }
        catch (Exception exception)
        {
            DarkMessageBox.Show(this, exception.Message, "Chỉnh sửa ảnh", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) { Result = _current; DialogResult = true; }
    private void OnCancel(object sender, RoutedEventArgs e) { Result = null; DialogResult = false; }

    private void OnKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { OnUndo(sender, e); e.Handled = true; }
        else if ((e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) || e.Key == Key.Enter) { OnSave(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            if (_dragStart is not null) { _dragStart = null; Selection.Visibility = Visibility.Collapsed; Mouse.Capture(null); }
            else OnCancel(sender, e);
            e.Handled = true;
        }
    }

    private void RefreshImage()
    {
        PreviewImage.Source = WpfImageConversion.ToBitmapSource(_current);
        ImageSurface.Width = Overlay.Width = _current.Width;
        ImageSurface.Height = Overlay.Height = _current.Height;
        InfoText.Text = $"{_current.Width} × {_current.Height} px · Kéo chuột để {(CropMode.IsChecked == true ? "chọn vùng cần giữ" : "chọn dải cần xóa")} · Ctrl+Z để hoàn tác";
    }

    private WpfPoint Clamp(WpfPoint point) => new(Math.Clamp(point.X, 0, _current.Width), Math.Clamp(point.Y, 0, _current.Height));

    private void UpdateSelection(WpfPoint start, WpfPoint end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);

        // In Cut-out mode, fill the perpendicular axis so the preview makes
        // the operation obvious: horizontal drag removes a vertical strip,
        // vertical drag removes a horizontal strip.
        if (CutMode.IsChecked == true && (width > 0 || height > 0))
        {
            if (width > height)
            {
                top = 0;
                height = _current.Height;
            }
            else
            {
                left = 0;
                width = _current.Width;
            }
        }

        Canvas.SetLeft(Selection, left);
        Canvas.SetTop(Selection, top);
        Selection.Width = width;
        Selection.Height = height;
        Selection.Visibility = Visibility.Visible;
    }
}
