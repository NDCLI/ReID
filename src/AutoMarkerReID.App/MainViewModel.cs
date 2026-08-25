using System.Collections.ObjectModel;
using System.IO;
using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Inference;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AutoMarkerReID.App;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ApplicationController _controller;
    private readonly QueryCatalog _catalog;
    private readonly UserSelectionState _selection;
    private readonly UserPreferencesStore _preferences;
    private readonly IModelRuntime _runtime;
    private readonly IOcrService _ocr;
    private readonly ClipboardActivityStats _clipboardActivity;
    private readonly List<ObservableLogEntry> _allLogEntries = [];
    private string? _restoredRecognitionScope;

    public MainViewModel(ApplicationController controller, QueryCatalog catalog, UserSelectionState selection,
        UserPreferencesStore preferences, IModelRuntime runtime, IOcrService ocr,
        ClipboardActivityStats clipboardActivity, ObservableLogStore logs)
    {
        _controller = controller;
        _catalog = catalog;
        _selection = selection;
        _preferences = preferences;
        _runtime = runtime;
        _ocr = ocr;
        _clipboardActivity = clipboardActivity;
        _restoredRecognitionScope = selection.RecognitionScope;
        _targetQuery = selection.TargetQuery;
        _appearanceEnabled = selection.AppearanceEnabled;
        _saveCaptures = selection.SaveCaptures;
        _allLogEntries.AddRange(logs.Snapshot);
        RefreshVisibleLog();
        RefreshClipboardCounters();
        _controller.StateChanged += OnStateChanged;
        _clipboardActivity.Changed += OnClipboardActivityChanged;
        logs.MessageAdded += OnLogAdded;
        Queries.Add(new QueryListItem("Tất cả Query", null, 0));
        OnStateChanged(controller, new AppStateChangedEventArgs(controller.State));
    }

    public ObservableCollection<QueryListItem> Queries { get; } = [];
    public ObservableCollection<string> TargetQueries { get; } = [];
    public ObservableCollection<ObservableLogEntry> ActivityLog { get; } = [];

    [ObservableProperty] private string _statusText = "Đang khởi động hệ thống nhận diện…";
    [ObservableProperty] private string _statusColor = "#F59E0B";
    [ObservableProperty] private QueryListItem? _selectedQuery;
    [ObservableProperty] private string _targetQuery = "Query_1";
    [ObservableProperty] private bool _appearanceEnabled;
    [ObservableProperty] private bool _saveCaptures = true;
    [ObservableProperty] private string _hotkeyStatus = "Phím tắt chưa đăng ký";
    [ObservableProperty] private string _clipboardSummary = "Đã tiếp nhận: 0 · Đã bỏ qua: 0";
    [ObservableProperty] private string _startupHealthText = "Đang kiểm tra mô hình nhận diện, OCR và dữ liệu Query…";
    [ObservableProperty] private string _startupIssueText = string.Empty;
    [ObservableProperty] private string _logFilter = "All";

    public event EventHandler? CaptureRequested;
    public event EventHandler? RepeatCaptureRequested;
    public event EventHandler? LibraryRequested;
    public event EventHandler? EditImageRequested;
    public event EventHandler? LatestCaptureRequested;
    public event EventHandler? DeleteQueryRequested;
    public event EventHandler? HotkeyDetailsRequested;
    public event EventHandler<string>? SelectionFeedback;
    public Func<bool>? ConfirmCacheRebuild { get; set; }

    partial void OnSelectedQueryChanged(QueryListItem? value)
    {
        _selection.RecognitionScope = value?.Id;
        SavePreferences();
    }

    partial void OnTargetQueryChanged(string value)
    {
        if (value.StartsWith("Query_", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value.AsSpan("Query_".Length), out var number) && number is >= 1 and <= 999)
        {
            _selection.TargetQuery = $"Query_{number}";
            SavePreferences();
        }
    }

    partial void OnAppearanceEnabledChanged(bool value) { _selection.AppearanceEnabled = value; SavePreferences(); }
    partial void OnSaveCapturesChanged(bool value)
    {
        if (!value)
        {
            SaveCaptures = true;
            return;
        }
        _selection.SaveCaptures = true;
        SavePreferences();
    }

    [RelayCommand] private void Capture() => CaptureRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void RepeatCapture() => RepeatCaptureRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void OpenLibrary() => LibraryRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void EditImage() => EditImageRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void OpenLatestCapture() => LatestCaptureRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void DeleteQuery() => DeleteQueryRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void ShowHotkeys() => HotkeyDetailsRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void SelectEmptyTarget() => SelectEmptyQuery();

    [RelayCommand]
    private void ClearLog()
    {
        _allLogEntries.Clear();
        ActivityLog.Clear();
    }

    [RelayCommand]
    private void SetLogFilter(string? filter)
    {
        LogFilter = filter is "Warning" or "Error" ? filter : "All";
        RefreshVisibleLog();
    }

    [RelayCommand]
    private async Task RebuildCacheAsync()
    {
        if (ConfirmCacheRebuild?.Invoke() == false) return;
        await _controller.RebuildCacheAsync(null, CancellationToken.None);
        RefreshQueries();
    }

    public void RefreshQueries()
    {
        var selectedId = SelectedQuery?.Id ?? _restoredRecognitionScope;
        Queries.Clear();
        Queries.Add(new QueryListItem("Tất cả Query", null, _catalog.Snapshot.Values.Sum(query => query.References.Count)));
        foreach (var query in _catalog.Snapshot.Values.OrderBy(query => QueryNumber(query.Id)))
            Queries.Add(new QueryListItem(query.Id, query.Id, query.References.Count));

        TargetQueries.Clear();
        foreach (var id in Queries.Where(query => query.Id is not null).Select(query => query.Id!)) TargetQueries.Add(id);

        SelectedQuery = Queries.FirstOrDefault(query => query.Id == selectedId) ?? Queries[0];
        _restoredRecognitionScope = null;
        RefreshStartupHealth();
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
        SelectionFeedback?.Invoke(this, $"Phạm vi nhận diện: {SelectedQuery.DisplayName}");
    }

    private void OnStateChanged(object? sender, AppStateChangedEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusText = e.State switch
            {
                AppRuntimeState.Starting => "Đang nạp mô hình nhận diện và dữ liệu Query…",
                AppRuntimeState.Monitoring => "Đang theo dõi Clipboard",
                AppRuntimeState.Capturing => "Đang chọn vùng chụp",
                AppRuntimeState.Processing => "Đang nhận diện…",
                AppRuntimeState.Reviewing => "Đang kiểm tra kết quả",
                AppRuntimeState.RebuildingCache => "Đang tạo lại dữ liệu AI…",
                AppRuntimeState.Error => e.Error ?? "Hệ thống nhận diện gặp lỗi",
                AppRuntimeState.ShuttingDown => "Đang thoát…",
                _ => e.State.ToString(),
            };
            StatusColor = e.State switch
            {
                AppRuntimeState.Monitoring => "#22C55E",
                AppRuntimeState.Error => "#EF4444",
                _ => "#F59E0B",
            };
            if (e.State == AppRuntimeState.Monitoring) RefreshQueries();
            else if (e.State == AppRuntimeState.Error) RefreshStartupHealth(e.Error);
        });
    }

    private void OnLogAdded(object? sender, ObservableLogEntry entry)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _allLogEntries.Add(entry);
            while (_allLogEntries.Count > 1_000) _allLogEntries.RemoveAt(0);
            RefreshVisibleLog();
        });
    }

    private void RefreshVisibleLog()
    {
        ActivityLog.Clear();
        var repeatedInformation = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _allLogEntries)
        {
            if (LogFilter == "Warning" && entry.Level < LogLevel.Warning) continue;
            if (LogFilter == "Error" && entry.Level < LogLevel.Error) continue;
            if (LogFilter == "All" && entry.Level == LogLevel.Information &&
                !repeatedInformation.Add($"{entry.Category}\0{entry.Message}")) continue;
            ActivityLog.Add(entry);
        }
    }

    private void OnClipboardActivityChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(RefreshClipboardCounters);

    private void RefreshClipboardCounters() =>
        ClipboardSummary = $"Đã tiếp nhận: {_clipboardActivity.Received} · Đã bỏ qua: {_clipboardActivity.Skipped}";

    private void RefreshStartupHealth(string? runtimeError = null)
    {
        var queryCount = _catalog.Snapshot.Count;
        var referenceCount = _catalog.Snapshot.Values.Sum(query => query.References.Count);
        var models = _runtime.ActiveBodyModels.Count == 0 ? "không có" : string.Join(", ", _runtime.ActiveBodyModels);
        StartupHealthText = $"Mô hình nhận diện: {models} · OCR: {(_ocr.IsReady ? "sẵn sàng" : "chưa sẵn sàng")} · {queryCount} Query / {referenceCount} ảnh tham chiếu";
        var issues = new List<string>();
        if (_runtime.ActiveBodyModels.Count == 0) issues.Add("Không có mô hình nhận diện người nào hoạt động.");
        if (!_ocr.IsReady) issues.Add("OCR chưa sẵn sàng; vui lòng kiểm tra mô hình OCR.");
        if (referenceCount == 0) issues.Add("Chưa có ảnh tham chiếu trong Query.");
        if (!string.IsNullOrWhiteSpace(runtimeError)) issues.Add(runtimeError);
        StartupIssueText = string.Join(" ", issues.Distinct(StringComparer.Ordinal));
    }

    private void SavePreferences()
    {
        try { _preferences.Save(_selection); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static int QueryNumber(string queryId) => int.TryParse(queryId.AsSpan("Query_".Length), out var number) ? number : int.MaxValue;
}

public sealed record QueryListItem(string DisplayName, string? Id, int ReferenceCount)
{
    public string Summary => $"{DisplayName} · {ReferenceCount} ảnh";
    public bool IsWeak => Id is not null && ReferenceCount <= 1;
    public string Foreground => IsWeak ? "#F59E0B" : "#E5E7EB";
    public string ToolTip => IsWeak
        ? $"{DisplayName} mới có {ReferenceCount} ảnh. Nên thêm ảnh khác góc, ánh sáng hoặc trang phục."
        : $"{DisplayName} có {ReferenceCount} ảnh tham chiếu.";
}
