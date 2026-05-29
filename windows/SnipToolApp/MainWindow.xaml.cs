using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SnipToolApp
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private async void NewSnip_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Hide();
                StatusText.Text = "Preparing capture overlay...";
                var overlay = new OverlayWindow();
                overlay.SelectionCompleted += OnSelectionCompleted;
                overlay.CaptureCanceled += OnCaptureCanceled;
                overlay.ShowDialog();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Capture failed: {ex.Message}";
                Show();
            }
        }

        private void OnSelectionCompleted(BitmapSource selectionImage)
        {
            CapturePreview.Source = selectionImage;
            PreviewHintText.Text = "Snip captured successfully.";
            StatusText.Text = "Capture completed.";
            Show();
        }

        private void OnCaptureCanceled()
        {
            StatusText.Text = "Capture canceled.";
            Show();
        }

        private void StartRecording_Click(object sender, RoutedEventArgs e)
        {
            var recordingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SnipToolRecording.mp4");
            if (NativeMethods.StartScreenRecording(recordingsPath))
            {
                StartRecordingButton.IsEnabled = false;
                StopRecordingButton.IsEnabled = true;
                RecordingText.Text = $"Recording to: {recordingsPath}";
                StatusText.Text = "Screen recording started.";
            }
            else
            {
                StatusText.Text = "Unable to start recording.";
            }
        }

        private void StopRecording_Click(object sender, RoutedEventArgs e)
        {
            NativeMethods.StopScreenRecording();
            StartRecordingButton.IsEnabled = true;
            StopRecordingButton.IsEnabled = false;
            RecordingText.Text = "Stopped";
            StatusText.Text = "Screen recording stopped.";
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Settings will be implemented in the next phase.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
