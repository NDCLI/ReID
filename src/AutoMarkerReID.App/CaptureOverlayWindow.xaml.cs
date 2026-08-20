using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Windows;

namespace AutoMarkerReID.App;

public partial class CaptureOverlayWindow : Window
{
    private readonly BoundingBox _virtualBounds;
    private System.Windows.Point? _start;

    public CaptureOverlayWindow(BoundingBox virtualBounds)
    {
        _virtualBounds = virtualBounds;
        InitializeComponent();
        WindowsDarkMode.Apply(this);
        Left = virtualBounds.X1;
        Top = virtualBounds.Y1;
        Width = virtualBounds.Width;
        Height = virtualBounds.Height;
        var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        watchdog.Tick += (_, _) => { watchdog.Stop(); DialogResult = false; };
        watchdog.Start();
        Closed += (_, _) => watchdog.Stop();
    }

    public BoundingBox? SelectedRegion { get; private set; }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(SelectionCanvas);
        SelectionCanvas.CaptureMouse();
        SelectionBorder.Visibility = Visibility.Visible;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_start is not null && e.LeftButton == MouseButtonState.Pressed)
            UpdateSelection(_start.Value, e.GetPosition(SelectionCanvas));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null) return;
        var end = e.GetPosition(SelectionCanvas);
        SelectionCanvas.ReleaseMouseCapture();
        var local = new BoundingBox((int)_start.Value.X, (int)_start.Value.Y, (int)end.X, (int)end.Y).Normalize();
        if (local.Width >= 5 && local.Height >= 5)
        {
            SelectedRegion = new BoundingBox(local.X1 + _virtualBounds.X1, local.Y1 + _virtualBounds.Y1,
                local.X2 + _virtualBounds.X1, local.Y2 + _virtualBounds.Y1);
            DialogResult = true;
        }
        else
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            _start = null;
        }
    }

    private void UpdateSelection(System.Windows.Point start, System.Windows.Point end)
    {
        Canvas.SetLeft(SelectionBorder, Math.Min(start.X, end.X));
        Canvas.SetTop(SelectionBorder, Math.Min(start.Y, end.Y));
        SelectionBorder.Width = Math.Abs(end.X - start.X);
        SelectionBorder.Height = Math.Abs(end.Y - start.Y);
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
