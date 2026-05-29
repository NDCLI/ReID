using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SnipToolApp
{
    public partial class OverlayWindow : Window
    {
        private Point? _dragStart;
        private Rect _currentSelection;
        private double _dpiX = 1.0;
        private double _dpiY = 1.0;

        public event Action<BitmapSource>? SelectionCompleted;
        public event Action? CaptureCanceled;

        public OverlayWindow()
        {
            InitializeComponent();
            Loaded += OverlayWindow_Loaded;
        }

        private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            {
                _dpiX = target.TransformToDevice.M11;
                _dpiY = target.TransformToDevice.M22;
            }

            var screenWidth = (int)(SystemParameters.PrimaryScreenWidth * _dpiX);
            var screenHeight = (int)(SystemParameters.PrimaryScreenHeight * _dpiY);

            if (NativeMethods.CaptureScreenToPng(0, 0, screenWidth, screenHeight, out var dataPtr, out var dataSize))
            {
                try
                {
                    PreviewImage.Source = LoadBitmapImage(dataPtr, dataSize);
                }
                finally
                {
                    NativeMethods.FreeCaptureData(dataPtr);
                }
            }
        }

        private BitmapImage LoadBitmapImage(IntPtr dataPtr, int dataSize)
        {
            var buffer = new byte[dataSize];
            System.Runtime.InteropServices.Marshal.Copy(dataPtr, buffer, 0, dataSize);
            using var stream = new MemoryStream(buffer);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            _dragStart = e.GetPosition(this);
            SelectionRect.Visibility = Visibility.Visible;
            UpdateSelectionRect(_dragStart.Value, _dragStart.Value);
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragStart.HasValue || e.LeftButton != MouseButtonState.Pressed)
                return;

            var point = e.GetPosition(this);
            UpdateSelectionRect(_dragStart.Value, point);
        }

        private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragStart.HasValue)
                return;

            if (_currentSelection.Width < 16 || _currentSelection.Height < 16)
            {
                CancelCapture();
                return;
            }

            var x = (int)(_currentSelection.X * _dpiX);
            var y = (int)(_currentSelection.Y * _dpiY);
            var width = (int)(_currentSelection.Width * _dpiX);
            var height = (int)(_currentSelection.Height * _dpiY);

            if (NativeMethods.CaptureScreenToPng(x, y, width, height, out var dataPtr, out var dataSize))
            {
                try
                {
                    SelectionCompleted?.Invoke(LoadBitmapImage(dataPtr, dataSize));
                }
                finally
                {
                    NativeMethods.FreeCaptureData(dataPtr);
                }
            }
            else
            {
                CancelCapture();
            }

            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CancelCapture();
            }
        }

        private void UpdateSelectionRect(Point start, Point end)
        {
            var x = Math.Min(start.X, end.X);
            var y = Math.Min(start.Y, end.Y);
            var width = Math.Abs(start.X - end.X);
            var height = Math.Abs(start.Y - end.Y);
            _currentSelection = new Rect(x, y, width, height);
            Canvas.SetLeft(SelectionRect, x);
            Canvas.SetTop(SelectionRect, y);
            SelectionRect.Width = width;
            SelectionRect.Height = height;
        }

        private void CancelCapture()
        {
            CaptureCanceled?.Invoke();
            Close();
        }
    }
}
