using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Windows;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace AutoMarkerReID.App;

public partial class LibraryWindow : Window
{
    private readonly IResultRepository _repository;
    private readonly IImageCodec _codec;
    private readonly IBoxRenderer _renderer;
    private readonly IClipboardWriter _clipboard;
    private readonly IClipboardMonitor _monitor;
    private readonly ICandidateGenerator _candidates;
    private IReadOnlyList<SavedResult> _results = [];
    private SavedResult? _current;
    private ImageFrame? _original;
    private readonly List<MatchResult> _matches = [];
    private bool _dirty;

    public LibraryWindow(IResultRepository repository, IImageCodec codec, IBoxRenderer renderer,
        IClipboardWriter clipboard, IClipboardMonitor monitor, ICandidateGenerator candidates)
    {
        _repository = repository;
        _codec = codec;
        _renderer = renderer;
        _clipboard = clipboard;
        _monitor = monitor;
        _candidates = candidates;
        InitializeComponent();
        WindowsDarkMode.Apply(this);
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        await SaveDirtyAsync();
        _results = await _repository.ListAsync(CancellationToken.None);
        ResultList.ItemsSource = _results;
        ResultList.SelectedIndex = _results.Count > 0 ? 0 : -1;
        if (_results.Count == 0) ClearPreview("Chưa có kết quả nhận diện nào được lưu.");
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is not SavedResult selected || ReferenceEquals(selected, _current)) return;
        await SaveDirtyAsync();
        await LoadAsync(selected);
    }

    private async Task LoadAsync(SavedResult result)
    {
        _current = result;
        _matches.Clear();
        _matches.AddRange(result.Matches);
        var editable = !string.IsNullOrWhiteSpace(result.OriginalImagePath) && File.Exists(result.OriginalImagePath);
        var path = editable ? result.OriginalImagePath : result.MarkedImagePath;
        try
        {
            _original = _codec.Decode(await File.ReadAllBytesAsync(path));
            var boxesAdjusted = false;
            if (editable)
            {
                var cards = _candidates.Generate(_original).Where(candidate => !candidate.IsSource).ToArray();
                for (var index = 0; index < _matches.Count; index++)
                {
                    var originalBox = _matches[index].BoundingBox;
                    var matchingCard = cards
                        .Select(card =>
                        {
                            var intersectionWidth = Math.Max(0, Math.Min(card.BoundingBox.X2, originalBox.X2) - Math.Max(card.BoundingBox.X1, originalBox.X1));
                            var intersectionHeight = Math.Max(0, Math.Min(card.BoundingBox.Y2, originalBox.Y2) - Math.Max(card.BoundingBox.Y1, originalBox.Y1));
                            var coverage = card.BoundingBox.Area == 0 ? 0 : (double)(intersectionWidth * intersectionHeight) / card.BoundingBox.Area;
                            var iou = card.BoundingBox.IntersectionOverUnion(originalBox);
                            return (Card: card, Score: Math.Max(iou, coverage));
                        })
                        .Where(item => item.Score >= 0.20)
                        .OrderByDescending(item => item.Score)
                        .FirstOrDefault().Card;
                    var snapped = _renderer.SnapToCard(_original, matchingCard?.BoundingBox ?? originalBox);
                    if (snapped == _matches[index].BoundingBox) continue;
                    _matches[index] = _matches[index] with { BoundingBox = snapped };
                    boxesAdjusted = true;
                }
                var spaced = MatchPostProcessor.EnsureMinimumHorizontalGap(_matches);
                if (!_matches.SequenceEqual(spaced))
                {
                    _matches.Clear();
                    _matches.AddRange(spaced);
                    boxesAdjusted = true;
                }
            }
            Render(editable);
            _dirty = boxesAdjusted;
        }
        catch (Exception exception)
        {
            ClearPreview(exception.Message);
        }
    }

    private void Render(bool editable)
    {
        if (_original is null) return;
        var preview = editable ? _renderer.Draw(_original, _matches) : _original;
        PreviewImage.Source = WpfImageConversion.ToBitmapSource(preview);
        ImageSurface.Width = BoxCanvas.Width = preview.Width;
        ImageSurface.Height = BoxCanvas.Height = preview.Height;
        BoxCanvas.Children.Clear();
        InfoText.Text = editable
            ? $"{_matches.Count} khung · Nhấp vào thẻ để thêm hoặc xóa khung · Thay đổi được tự động lưu khi chuyển ảnh"
            : "Kết quả cũ chỉ hỗ trợ xem và sao chép vì không có ảnh gốc cùng dữ liệu chỉnh sửa.";
    }

    private void OnImageClick(object sender, MouseButtonEventArgs e)
    {
        if (_current is null || _original is null || string.IsNullOrWhiteSpace(_current.OriginalImagePath)) return;
        var point = e.GetPosition(BoxCanvas);
        var existing = _matches.FindIndex(match => match.BoundingBox.Contains((int)point.X, (int)point.Y));
        if (existing >= 0) _matches.RemoveAt(existing);
        else
        {
            var card = _candidates.Generate(_original).FirstOrDefault(item => item.BoundingBox.Contains((int)point.X, (int)point.Y));
            if (card is null) return;
            var query = _current.DominantQueryId ?? "Manual";
            var snapped = _renderer.SnapToCard(_original, card.BoundingBox);
            _matches.Add(new MatchResult(query, null, snapped, 1, null, null, card.PixelScore,
                new Dictionary<string, float>(), null, MatchSource.Manual, true));
            var spaced = MatchPostProcessor.EnsureMinimumHorizontalGap(_matches);
            _matches.Clear();
            _matches.AddRange(spaced);
        }
        _dirty = true;
        Render(true);
    }

    private async Task SaveDirtyAsync()
    {
        if (!_dirty || _current is null) return;
        await _repository.UpdateMatchesAsync(_current, _matches.ToArray(), CancellationToken.None);
        _current = _current with { Matches = _matches.ToArray() };
        _dirty = false;
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_current is null || _original is null) return;
        var image = string.IsNullOrWhiteSpace(_current.OriginalImagePath) ? _original : _renderer.Draw(_original, _matches);
        _monitor.IgnoreNextWrite();
        await _clipboard.WriteImageAsync(image, CancellationToken.None);
        System.Media.SystemSounds.Asterisk.Play();
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        await _repository.MoveToRecycleBinAsync(_current, CancellationToken.None);
        _dirty = false;
        await RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OnPrevious(object sender, RoutedEventArgs e) { if (ResultList.SelectedIndex > 0) ResultList.SelectedIndex--; }
    private void OnNext(object sender, RoutedEventArgs e) { if (ResultList.SelectedIndex + 1 < _results.Count) ResultList.SelectedIndex++; }
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        QueueSaveDirty();
        base.OnClosed(e);
    }

    private void QueueSaveDirty()
    {
        if (!_dirty || _current is null) return;
        var result = _current;
        var matches = _matches.ToArray();
        _current = result with { Matches = matches };
        _dirty = false;
        _ = PersistAfterCloseAsync(result, matches);
    }

    private async Task PersistAfterCloseAsync(SavedResult result, IReadOnlyList<MatchResult> matches)
    {
        try
        {
            await _repository.UpdateMatchesAsync(result, matches, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Không thể tự lưu kết quả khi đóng Library: {exception}");
        }
    }

    private void OnKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Left) OnPrevious(sender, e);
        else if (e.Key == Key.Right) OnNext(sender, e);
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) OnCopy(sender, e);
        else if (e.Key == Key.Delete) OnDelete(sender, e);
        else if (e.Key == Key.Escape) Close();
    }

    private void ClearPreview(string message)
    {
        _current = null;
        _original = null;
        PreviewImage.Source = null;
        BoxCanvas.Children.Clear();
        InfoText.Text = message;
    }
}
