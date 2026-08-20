using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Windows;

namespace AutoMarkerReID.App;

public partial class ReviewWindow : Window
{
    private readonly ReviewSession _session;
    private readonly IReadOnlyList<CardCandidate> _cards;
    private readonly UserSelectionState _selection;
    private readonly IImageCodec _codec;
    private readonly IBoxRenderer _boxRenderer;
    private readonly List<MatchResult> _matches;

    public ReviewWindow(ReviewSession session, ICandidateGenerator candidateGenerator, UserSelectionState selection, IImageCodec codec, IBoxRenderer boxRenderer)
    {
        _session = session;
        _selection = selection;
        _codec = codec;
        _boxRenderer = boxRenderer;
        _matches = [.. session.Matches];
        _cards = candidateGenerator.Generate(session.Original);
        InitializeComponent();
        WindowsDarkMode.Apply(this);
        PreviewImage.Source = WpfImageConversion.ToBitmapSource(session.Original);
        ImageSurface.Width = session.Original.Width;
        ImageSurface.Height = session.Original.Height;
        BoxCanvas.Width = session.Original.Width;
        BoxCanvas.Height = session.Original.Height;
        RenderBoxes();
    }

    public ReviewOutcome? Outcome { get; private set; }

    private void OnImageClick(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(BoxCanvas);
        var existing = _matches.FindIndex(match => match.BoundingBox.Contains((int)point.X, (int)point.Y));
        if (existing >= 0)
        {
            _matches.RemoveAt(existing);
            RenderBoxes();
            return;
        }

        var card = _cards.FirstOrDefault(candidate => candidate.BoundingBox.Contains((int)point.X, (int)point.Y));
        if (card is null) return;
        var queryId = _matches.GroupBy(match => match.QueryId).OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault()
                      ?? _selection.RecognitionScope ?? _selection.TargetQuery;
        var snapped = _boxRenderer.SnapToCard(_session.Original, card.BoundingBox);
        _matches.Add(new MatchResult(queryId, null, snapped, 1, null, null, card.PixelScore,
            new Dictionary<string, float>(), null, MatchSource.Manual, true));
        ApplyBoxSpacing();
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
            var box = match.BoundingBox;
            var border = new Border
            {
                Width = box.Width,
                Height = box.Height,
                BorderBrush = System.Windows.Media.Brushes.Red,
                BorderThickness = new Thickness(ReIdDefaults.BoxThickness),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(border, box.X1);
            Canvas.SetTop(border, box.Y1);
            BoxCanvas.Children.Add(border);
        }
        SummaryText.Text = $"{_matches.Count} khung · Click trái card để thêm/xóa · Chuột phải để lưu";
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

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Outcome = new ReviewOutcome(ReviewDecision.Cancel);
            Close();
        }
    }
}
