using System.Threading.Channels;
using AutoMarkerReID.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMarkerReID.Application;

public sealed class ApplicationController : BackgroundService
{
    private readonly IEngineInitializer _initializer;
    private readonly IClipboardMonitor _clipboardMonitor;
    private readonly IImageJobProcessor _processor;
    private readonly IReviewCompletionService _reviewCompletion;
    private readonly IMatchEngine _matchEngine;
    private readonly UserSelectionState _selection;
    private readonly ClipboardActivityStats _clipboardActivity;
    private readonly ILogger<ApplicationController> _logger;
    private readonly Channel<ImageJob> _jobs;
    private readonly Lock _stateLock = new();
    private AppRuntimeState _state = AppRuntimeState.Starting;

    public ApplicationController(
        IEngineInitializer initializer,
        IClipboardMonitor clipboardMonitor,
        IImageJobProcessor processor,
        IReviewCompletionService reviewCompletion,
        IMatchEngine matchEngine,
        UserSelectionState selection,
        ClipboardActivityStats clipboardActivity,
        ILogger<ApplicationController> logger)
    {
        _initializer = initializer;
        _clipboardMonitor = clipboardMonitor;
        _processor = processor;
        _reviewCompletion = reviewCompletion;
        _matchEngine = matchEngine;
        _selection = selection;
        _clipboardActivity = clipboardActivity;
        _logger = logger;
        _jobs = Channel.CreateBounded<ImageJob>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
    }

    public AppRuntimeState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public event EventHandler<AppStateChangedEventArgs>? StateChanged;
    public event EventHandler<ReviewRequestedEventArgs>? ReviewRequested;

    public bool TryQueue(ImageJob job)
    {
        if (State != AppRuntimeState.Monitoring)
        {
            return false;
        }

        return _jobs.Writer.TryWrite(job);
    }

    public bool TryBeginCapture() => TryTransition(AppRuntimeState.Monitoring, AppRuntimeState.Capturing);

    public void EndCapture()
    {
        if (State == AppRuntimeState.Capturing)
        {
            SetState(AppRuntimeState.Monitoring);
        }
    }

    public async Task RebuildCacheAsync(IProgress<double>? progress, CancellationToken cancellationToken)
        => await RunMaintenanceAsync(token => _initializer.RebuildCacheAsync(progress, token), cancellationToken).ConfigureAwait(false);

    public async Task ClearAllDataAsync(Func<CancellationToken, Task> clearData, CancellationToken cancellationToken)
        => await RunMaintenanceAsync(async token =>
        {
            await clearData(token).ConfigureAwait(false);
            await _initializer.RebuildCacheAsync(null, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

    private async Task RunMaintenanceAsync(Func<CancellationToken, Task> maintenance, CancellationToken cancellationToken)
    {
        if (!TryTransition(AppRuntimeState.Monitoring, AppRuntimeState.RebuildingCache))
        {
            throw new InvalidOperationException("Chỉ có thể làm mới cache khi ứng dụng đang theo dõi.");
        }

        _clipboardMonitor.SetSuspended(true);
        try
        {
            await maintenance(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _clipboardMonitor.SynchronizeGeneration();
            _clipboardMonitor.SetSuspended(false);
            SetState(AppRuntimeState.Monitoring);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            SetState(AppRuntimeState.Starting);
            await _initializer.InitializeAsync(stoppingToken).ConfigureAwait(false);
            if (!_initializer.IsReady)
            {
                throw new InvalidOperationException("Không có body model khả dụng.");
            }

            SetState(AppRuntimeState.Monitoring);
            var monitorTask = _clipboardMonitor.RunAsync(QueueFromClipboardAsync, stoppingToken);
            var processingTask = ProcessJobsAsync(stoppingToken);
            await Task.WhenAll(monitorTask, processingTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            SetState(AppRuntimeState.ShuttingDown);
        }
        catch (Exception exception)
        {
            ApplicationControllerLog.EngineStopped(_logger, exception);
            SetState(AppRuntimeState.Error, exception.Message);
        }
    }

    private ValueTask QueueFromClipboardAsync(ImageJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryQueue(job))
        {
            _clipboardActivity.RecordSkipped();
            ApplicationControllerLog.ClipboardSkipped(_logger, State);
        }
        else
        {
            _clipboardActivity.RecordReceived();
        }

        return ValueTask.CompletedTask;
    }

    private async Task ProcessJobsAsync(CancellationToken cancellationToken)
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!TryTransition(AppRuntimeState.Monitoring, AppRuntimeState.Processing))
            {
                continue;
            }

            _clipboardMonitor.SetSuspended(true);
            try
            {
                ApplicationControllerLog.RecognitionScope(_logger, _selection.RecognitionScope ?? "Tất cả Query", _selection.TargetQuery);
                var result = await _processor.ProcessAsync(job, cancellationToken).ConfigureAwait(false);
                await HandleResultAsync(job, result, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ApplicationControllerLog.ImageProcessingFailed(_logger, job.Id, exception);
            }
            finally
            {
                _clipboardMonitor.SynchronizeGeneration();
                _clipboardMonitor.SetSuspended(false);
                if (State is not AppRuntimeState.Error and not AppRuntimeState.ShuttingDown)
                {
                    SetState(AppRuntimeState.Monitoring);
                }
            }
        }
    }

    private async Task HandleResultAsync(ImageJob job, ProcessingResult result, CancellationToken cancellationToken)
    {
        switch (result)
        {
            case ProcessingResult.Ignored ignored:
                ApplicationControllerLog.ImageIgnored(_logger, ignored.Reason);
                break;
            case ProcessingResult.QueryCollected collected:
                ApplicationControllerLog.ReferenceAdded(_logger, collected.QueryId);
                DirectCaptureCleanupPolicy.TryDeleteSavedCopy(job, result, _logger);
                break;
            case ProcessingResult.ReviewRequired review:
                var activeSession = review.Session;
                var edited = false;
                while (true)
                {
                    SetState(AppRuntimeState.Reviewing);
                    var args = new ReviewRequestedEventArgs(activeSession, cancellationToken);
                    ReviewRequested?.Invoke(this, args);
                    if (!args.HasHandler)
                    {
                        ApplicationControllerLog.ReviewHandlerMissing(_logger);
                        args.Complete(new ReviewOutcome(ReviewDecision.Cancel));
                    }

                    var outcome = await args.Completion.ConfigureAwait(false);
                    if (outcome is { Decision: ReviewDecision.RematchEditedImage, EditedImage: not null })
                    {
                        SetState(AppRuntimeState.Processing);
                        var matches = await _matchEngine.MatchAsync(outcome.EditedImage, _selection.RecognitionScope, cancellationToken).ConfigureAwait(false);
                        activeSession = activeSession with
                        {
                            Original = outcome.EditedImage,
                            Matches = matches,
                            Explanations = _matchEngine.LastExplanations,
                        };
                        edited = true;
                        continue;
                    }

                    await _reviewCompletion.CompleteAsync(activeSession, outcome, cancellationToken).ConfigureAwait(false);
                    // An edit means output holds the cropped image, so the capture
                    // copy is still the only full-resolution record and is kept.
                    if (!edited)
                    {
                        DirectCaptureCleanupPolicy.TryDeleteSavedCopy(job, outcome, _logger);
                    }

                    break;
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    private bool TryTransition(AppRuntimeState expected, AppRuntimeState next)
    {
        lock (_stateLock)
        {
            if (_state != expected)
            {
                return false;
            }

            _state = next;
        }

        StateChanged?.Invoke(this, new AppStateChangedEventArgs(next));
        return true;
    }

    private void SetState(AppRuntimeState state, string? error = null)
    {
        lock (_stateLock)
        {
            _state = state;
        }

        StateChanged?.Invoke(this, new AppStateChangedEventArgs(state, error));
    }
}

internal static partial class ApplicationControllerLog
{
    [LoggerMessage(EventId = 1010, Level = LogLevel.Information, Message = "Bắt đầu xử lý ảnh: phạm vi nhận diện={RecognitionScope}; Query lưu ảnh={TargetQuery}.")]
    public static partial void RecognitionScope(ILogger logger, string recognitionScope, string targetQuery);

    [LoggerMessage(EventId = 1000, Level = LogLevel.Critical, Message = "Hệ thống nhận diện không thể khởi động hoặc tiến trình xử lý đã dừng.")]
    public static partial void EngineStopped(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Bỏ qua thay đổi Clipboard vì ứng dụng đang ở trạng thái {state}.")]
    public static partial void ClipboardSkipped(ILogger logger, AppRuntimeState state);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Xử lý ảnh {jobId} thất bại.")]
    public static partial void ImageProcessingFailed(ILogger logger, Guid jobId, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Ảnh bị bỏ qua: {reason}")]
    public static partial void ImageIgnored(ILogger logger, string reason);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information, Message = "Đã thêm ảnh tham chiếu vào {queryId}.")]
    public static partial void ReferenceAdded(ILogger logger, string queryId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning, Message = "Không có giao diện kiểm tra kết quả; phiên hiện tại đã được hủy an toàn.")]
    public static partial void ReviewHandlerMissing(ILogger logger);
}

public sealed class AppStateChangedEventArgs(AppRuntimeState state, string? error = null) : EventArgs
{
    public AppRuntimeState State { get; } = state;
    public string? Error { get; } = error;
}

public sealed class ReviewRequestedEventArgs : EventArgs
{
    private readonly TaskCompletionSource<ReviewOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completed;

    public ReviewRequestedEventArgs(ReviewSession session, CancellationToken cancellationToken)
    {
        Session = session;
        cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
    }

    public ReviewSession Session { get; }
    public Task<ReviewOutcome> Completion => _completion.Task;
    public bool HasHandler { get; private set; }

    public void MarkHandled() => HasHandler = true;

    public void Complete(ReviewOutcome outcome)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _completion.TrySetResult(outcome);
        }
    }
}
