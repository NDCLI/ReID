using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Rectangle = System.Windows.Shapes.Rectangle;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Windows;

namespace AutoMarkerReID.App;

public partial class ReviewWindow : Window
{
    private readonly ReviewSession _session;
    private readonly UserSelectionState _selection;
    private readonly IImageCodec _codec;
    private readonly IBoxRenderer _boxRenderer;
    private readonly List<MatchResult> _matches;
    private readonly Stack<List<MatchResult>> _history = new();
    private readonly float _calibratedThreshold;
    private bool _sliderReady;
    private BoundingBox? _highlight;

    public ReviewWindow(
        ReviewSession session,
        UserSelectionState selection,
        IImageCodec codec,
        IBoxRenderer boxRenderer,
        IReadOnlyDictionary<string, QueryIdentity> queries)
    {
        _session = session;
        _selection = selection;
        _codec = codec;
        _boxRenderer = boxRenderer;
        _matches = [.. session.Matches];
        _calibratedThreshold = ResolveCalibratedThreshold(session, selection, queries);
        InitializeComponent();
        WindowsDarkMode.Apply(this);
        PreviewImage.Source = WpfImageConversion.ToBitmapSource(session.Original);
        ImageSurface.Width = session.Original.Width;
        ImageSurface.Height = session.Original.Height;
        BoxCanvas.Width = session.Original.Width;
        BoxCanvas.Height = session.Original.Height;
        ThresholdSlider.Value = selection.MatchThresholdOverride ?? _calibratedThreshold;
        _sliderReady = true;
        UpdateThresholdText();
        DiagnosticsList.ItemsSource = BuildDiagnosticItems(session);
        RenderBoxes();
    }

    public ReviewOutcome? Outcome { get; private set; }

    private void OnImageClick(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(BoxCanvas);
        var existing = _matches.FindIndex(match => match.BoundingBox.Contains((int)point.X, (int)point.Y));
        if (existing >= 0)
        {
            PushHistory();
            _matches.RemoveAt(existing);
            RenderBoxes();
            return;
        }

        var card = _boxRenderer.FindCardAtPoint(_session.Original, (int)Math.Round(point.X), (int)Math.Round(point.Y));
        if (card is null) return;
        // Manual boxes belong to the explicitly selected recognition scope. If
        // recognition is intentionally set to "Tất cả Query", use the target
        // Query for saving instead of inheriting an unrelated dominant result.
        var queryId = _selection.RecognitionScope ?? _selection.TargetQuery;
        PushHistory();
        _matches.Add(new MatchResult(queryId, null, card.Value, 1, null, null, 1,
            new Dictionary<string, float>(), null, MatchSource.Manual, true));
        ApplyBoxSpacing();
        RenderBoxes();
    }

    private void PushHistory() => _history.Push([.. _matches]);

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (!_history.TryPop(out var previous)) return;
        _matches.Clear();
        _matches.AddRange(previous);
        RenderBoxes();
    }

    private void ApplyBoxSpacing()
    {
        var spaced = MatchPostProcessor.EnsureMinimumHorizontalGap(_matches);
        _matches.Clear();
        _matches.AddRange(spaced);
    }

    private void RenderBoxes()
    {
        BoxCanvas.Children.Clear();
        foreach (var match in _matches)
        {
            DrawBox(match.BoundingBox, Brushes.Red, $"{match.QueryId} · {match.Score:P0}",
                Color.FromArgb(210, 185, 28, 28));
        }

        if (_highlight is { } highlight && !_matches.Any(match => match.BoundingBox == highlight))
        {
            // A rejected candidate picked in the diagnostics list. Drawn dashed so it
            // reads as "considered but not kept" rather than a result.
            DrawBox(highlight, Brushes.Gold, "bị loại", Color.FromArgb(210, 161, 98, 7), dashed: true);
        }

        SummaryText.Text = $"{_matches.Count} khung · Nhấp trái vào thẻ để thêm hoặc xóa khung · Nhấp phải để lưu · Ctrl+Z hoàn tác";
    }

    private void DrawBox(BoundingBox box, Brush stroke, string caption, Color captionColor, bool dashed = false)
    {
        var border = new Rectangle
        {
            Width = box.Width,
            Height = box.Height,
            Stroke = stroke,
            StrokeThickness = ReIdDefaults.BoxThickness,
            StrokeDashArray = dashed ? [4, 2] : null,
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(border, box.X1);
        Canvas.SetTop(border, box.Y1);
        BoxCanvas.Children.Add(border);
        var label = new TextBlock
        {
            Text = caption,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(captionColor),
            Padding = new Thickness(4, 1, 4, 1),
            FontSize = 11,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(label, box.X1);
        Canvas.SetTop(label, Math.Max(0, box.Y1 - 20));
        BoxCanvas.Children.Add(label);
    }

    internal sealed record DiagnosticItem(
        string Headline,
        string Detail,
        Brush Accent,
        BoundingBox BoundingBox,
        bool Accepted);

    private static DiagnosticItem[] BuildDiagnosticItems(ReviewSession session)
    {
        if (session.Explanations is not { Count: > 0 })
        {
            return [];
        }

        return session.Explanations
            .OrderByDescending(item => item.Accepted)
            .ThenByDescending(item => item.Score)
            .Select(item =>
            {
                var query = item.QueryId ?? "Không xác định";
                var headline = $"{(item.Accepted ? "NHẬN" : "LOẠI")} · {query} · {item.Score:P0} / ngưỡng {item.Threshold:P0}";
                var lines = new List<string>();
                if (item.Margin is { } margin) lines.Add($"cách Query kế tiếp {margin:P0}");
                if (item.BestReferenceScore is { } best) lines.Add($"reference tốt nhất {best:P0}");
                if (item.ModelScores.Count > 0)
                {
                    lines.Add(string.Join(", ", item.ModelScores.OrderBy(pair => pair.Key)
                        .Select(pair => $"{pair.Key} {pair.Value:P0}")));
                }

                lines.Add(item.Reason);
                var accent = item.Accepted
                    ? new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7))
                    : new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5));
                return new DiagnosticItem(headline, string.Join(" · ", lines), accent, item.BoundingBox, item.Accepted);
            })
            .ToArray();
    }

    private void OnDiagnosticSelected(object sender, SelectionChangedEventArgs e)
    {
        _highlight = DiagnosticsList.SelectedItem is DiagnosticItem { Accepted: false } item
            ? item.BoundingBox
            : null;
        RenderBoxes();
    }

    private static float ResolveCalibratedThreshold(
        ReviewSession session,
        UserSelectionState selection,
        IReadOnlyDictionary<string, QueryIdentity> queries)
    {
        var queryId = selection.RecognitionScope
                      ?? session.Matches.OrderByDescending(match => match.Score).FirstOrDefault()?.QueryId
                      ?? session.Explanations?.OrderByDescending(item => item.Score)
                          .FirstOrDefault(item => item.QueryId is not null)?.QueryId;
        return queryId is not null && queries.TryGetValue(queryId, out var query)
            ? query.CalibratedThreshold
            : ReIdDefaults.AiMatchThreshold;
    }

    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_sliderReady) return;
        UpdateThresholdText();
    }

    private void UpdateThresholdText()
    {
        var value = (float)ThresholdSlider.Value;
        var difference = value - _calibratedThreshold;
        var suffix = Math.Abs(difference) < 0.005f
            ? "đúng bằng ngưỡng tự hiệu chỉnh"
            : $"{(difference > 0 ? "cao hơn" : "thấp hơn")} ngưỡng tự hiệu chỉnh {Math.Abs(difference):P0}";
        ThresholdText.Text = $"{value:P0} · {suffix} ({_calibratedThreshold:P0}). Bấm “Nhận diện lại” để áp dụng.";
    }

    private void OnResetThresholdClick(object sender, RoutedEventArgs e)
    {
        ThresholdSlider.Value = _calibratedThreshold;
        UpdateThresholdText();
    }

    private void OnRematchClick(object sender, RoutedEventArgs e)
    {
        var current = (float)ThresholdSlider.Value;
        var atCalibrated = Math.Abs(current - _calibratedThreshold) < 0.005f;
        Outcome = new ReviewOutcome(
            ReviewDecision.Rematch,
            MatchThresholdOverride: atCalibrated ? null : current,
            ResetMatchThreshold: atCalibrated);
        Close();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Outcome = new ReviewOutcome(ReviewDecision.SaveAndCopy, Matches: _matches.ToArray());
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Outcome = new ReviewOutcome(ReviewDecision.Cancel);
        Close();
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        var editor = new ImageEditorWindow(_session.Original, _codec) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } edited)
        {
            Outcome = new ReviewOutcome(ReviewDecision.RematchEditedImage, edited);
            Close();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            Undo();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            Outcome = new ReviewOutcome(ReviewDecision.Cancel);
            Close();
        }
    }

    internal static string[] BuildDiagnostics(ReviewSession session)
    {
        if (session.Explanations is not { Count: > 0 })
            return ["Không có đối tượng nào đủ dữ liệu để phân tích."];

        return session.Explanations
            .OrderByDescending(item => item.Accepted)
            .ThenByDescending(item => item.Score)
            .Select(item =>
            {
                var models = item.ModelScores.Count == 0
                    ? "mô hình: không có dữ liệu"
                    : "mô hình: " + string.Join(", ", item.ModelScores.OrderBy(pair => pair.Key)
                        .Select(pair => $"{pair.Key} {pair.Value:P0}"));
                var state = item.Accepted ? "NHẬN" : "LOẠI";
                var query = item.QueryId ?? "Không xác định";
                return $"[{state}] {query} · độ tương đồng {item.Score:P0} / ngưỡng {item.Threshold:P0} · {models} · {item.Reason}";
            }).ToArray();
    }
}
