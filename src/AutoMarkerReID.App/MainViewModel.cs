using System.Collections.ObjectModel;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Inference;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoMarkerReID.App;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ApplicationController _controller;
    private readonly QueryCatalog _catalog;
    private readonly UserSelectionState _selection;

    public MainViewModel(ApplicationController controller, QueryCatalog catalog, UserSelectionState selection, ObservableLogStore logs)
    {
        _controller = controller;
        _catalog = catalog;
        _selection = selection;
        _controller.StateChanged += OnStateChanged;
        logs.MessageAdded += OnLogAdded;
        Queries.Add(new QueryListItem("Tất cả Query", null, 0));
    }

    public ObservableCollection<QueryListItem> Queries { get; } = [];
    public ObservableCollection<string> TargetQueries { get; } = [];
    public ObservableCollection<string> ActivityLog { get; } = [];

    [ObservableProperty]
    private string _statusText = "Đang khởi động engine…";

    [ObservableProperty]
    private string _statusColor = "#EF4444";

    [ObservableProperty]
    private QueryListItem? _selectedQuery;

    [ObservableProperty]
    private string _targetQuery = "Query_1";

    [ObservableProperty]
    private bool _appearanceEnabled;

    [ObservableProperty]
    private bool _saveCaptures;

    [ObservableProperty]
    private string _hotkeyStatus = "Phím tắt chưa đăng ký";

    public event EventHandler? CaptureRequested;
    public event EventHandler? RepeatCaptureRequested;
    public event EventHandler? LibraryRequested;
    public event EventHandler? EditImageRequested;
    public event EventHandler? LatestCaptureRequested;
    public event EventHandler? DeleteQueryRequested;
    public event EventHandler? HotkeyDetailsRequested;
    public event EventHandler<string>? SelectionFeedback;
    public Func<bool>? ConfirmCacheRebuild { get; set; }

    partial void OnSelectedQueryChanged(QueryListItem? value) => _selection.RecognitionScope = value?.Id;
    partial void OnTargetQueryChanged(string value)
    {
        if (value.StartsWith("Query_", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value.AsSpan("Query_".Length), out var number) && number is >= 1 and <= 999)
        {
            _selection.TargetQuery = $"Query_{number}";
        }
    }
    partial void OnAppearanceEnabledChanged(bool value) => _selection.AppearanceEnabled = value;
    partial void OnSaveCapturesChanged(bool value) => _selection.SaveCaptures = value;

    [RelayCommand]
    private void Capture() => CaptureRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RepeatCapture() => RepeatCaptureRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenLibrary() => LibraryRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void EditImage() => EditImageRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenLatestCapture() => LatestCaptureRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void DeleteQuery() => DeleteQueryRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ShowHotkeys() => HotkeyDetailsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void SelectEmptyTarget() => SelectEmptyQuery();

    [RelayCommand]
    private void ClearLog() => ActivityLog.Clear();

    [RelayCommand]
    private async Task RebuildCacheAsync()
    {
        if (ConfirmCacheRebuild?.Invoke() == false) return;
        await _controller.RebuildCacheAsync(null, CancellationToken.None);
        RefreshQueries();
    }

    public void RefreshQueries()
    {
        var selectedId = SelectedQuery?.Id;
        Queries.Clear();
        Queries.Add(new QueryListItem("Tất cả Query", null, _catalog.Snapshot.Values.Sum(query => query.References.Count)));
        foreach (var query in _catalog.Snapshot.Values.OrderBy(query => QueryNumber(query.Id)))
        {
            Queries.Add(new QueryListItem(query.Id, query.Id, query.References.Count));
        }

        TargetQueries.Clear();
        foreach (var id in Queries.Where(query => query.Id is not null).Select(query => query.Id!))
        {
            TargetQueries.Add(id);
        }

        SelectedQuery = Queries.FirstOrDefault(query => query.Id == selectedId) ?? Queries[0];
    }

    public void SelectPrevious() => SelectRelative(-1);
    public void SelectNext() => SelectRelative(1);
    public void SelectRoot() => SelectAt(0);
    public void SelectQueryPosition(int oneBasedPosition) => SelectAt(oneBasedPosition);

    public void SelectEmptyQuery()
    {
        var empty = Queries.Skip(1).FirstOrDefault(query => query.ReferenceCount == 0);
        if (empty is null) return;
        SelectedQuery = empty;
        TargetQuery = empty.Id!;
        SelectionFeedback?.Invoke(this, $"Đã chọn {empty.Id}");
    }

    private void SelectRelative(int delta)
    {
        if (Queries.Count == 0) return;
        var current = Math.Max(0, Queries.IndexOf(SelectedQuery!));
        SelectAt((current + delta + Queries.Count) % Queries.Count);
    }

    private void SelectAt(int index)
    {
        if (index < 0 || index >= Queries.Count) return;
        SelectedQuery = Queries[index];
        SelectionFeedback?.Invoke(this, $"Phạm vi: {SelectedQuery.DisplayName}");
    }

    private void OnStateChanged(object? sender, AppStateChangedEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusText = e.State switch
            {
                AppRuntimeState.Starting => "Đang nạp model và Query…",
                AppRuntimeState.Monitoring => "Đang theo dõi Clipboard",
                AppRuntimeState.Capturing => "Đang chọn vùng chụp",
                AppRuntimeState.Processing => "Đang nhận diện…",
                AppRuntimeState.Reviewing => "Đang duyệt kết quả",
                AppRuntimeState.RebuildingCache => "Đang làm mới cache…",
                AppRuntimeState.Error => e.Error ?? "Engine lỗi",
                AppRuntimeState.ShuttingDown => "Đang thoát…",
                _ => e.State.ToString(),
            };
            StatusColor = e.State switch
            {
                AppRuntimeState.Monitoring => "#22C55E",
                AppRuntimeState.Error => "#EF4444",
                _ => "#F59E0B",
            };
            if (e.State == AppRuntimeState.Monitoring)
            {
                RefreshQueries();
            }
        });
    }

    private void OnLogAdded(object? sender, string message)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ActivityLog.Add(message);
            while (ActivityLog.Count > 1_000)
            {
                ActivityLog.RemoveAt(0);
            }
        });
    }

    private static int QueryNumber(string queryId) => int.TryParse(queryId.AsSpan("Query_".Length), out var number) ? number : int.MaxValue;
}

public sealed record QueryListItem(string DisplayName, string? Id, int ReferenceCount)
{
    public string Summary => $"{DisplayName} · {ReferenceCount} ảnh";
}
