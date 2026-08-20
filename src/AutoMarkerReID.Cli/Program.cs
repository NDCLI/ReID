using AutoMarkerReID.Application;
using AutoMarkerReID.Domain;
using AutoMarkerReID.Imaging;
using AutoMarkerReID.Inference;
using AutoMarkerReID.Persistence;
using AutoMarkerReID.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

var parsed = CliOptions.Parse(args);
if (parsed.ShowHelp)
{
    CliOptions.PrintHelp();
    return 0;
}

var baseDirectory = Environment.CurrentDirectory;
var assetDirectory = Path.Combine(AppContext.BaseDirectory, "assets");
var paths = new StoragePaths(
    baseDirectory,
    parsed.ModelsDirectory ?? Path.Combine(assetDirectory, "models"),
    parsed.QueriesDirectory,
    parsed.OutputDirectory,
    Path.Combine(assetDirectory, "ui_template.png"));
paths.EnsureCreated();

var builder = Host.CreateApplicationBuilder();
builder.Logging.SetMinimumLevel(parsed.Verbose ? Microsoft.Extensions.Logging.LogLevel.Debug : Microsoft.Extensions.Logging.LogLevel.Information);
ConfigureServices(builder.Services, paths);
using var host = builder.Build();

var selection = host.Services.GetRequiredService<UserSelectionState>();
selection.RecognitionScope = parsed.Query;
selection.TargetQuery = parsed.Query ?? "Query_1";
selection.MatchThresholdOverride = parsed.Threshold;

await host.Services.GetRequiredService<IEngineInitializer>().InitializeAsync(CancellationToken.None);
var processor = host.Services.GetRequiredService<IImageJobProcessor>();
var repository = host.Services.GetRequiredService<IResultRepository>();
var renderer = host.Services.GetRequiredService<IBoxRenderer>();

if (parsed.SingleFile is { } file)
{
    if (!File.Exists(file)) throw new FileNotFoundException("Không tìm thấy ảnh --single.", file);
    var codec = host.Services.GetRequiredService<IImageCodec>();
    var image = codec.Decode(await File.ReadAllBytesAsync(file));
    await ProcessAsync(ImageJob.Create(image, ImageJobSource.CommandLine, Path.GetFullPath(file)), processor, repository, renderer, parsed.DebugWindow, CancellationToken.None);
    return 0;
}

Console.WriteLine("AutoMarker Re-ID CLI đang theo dõi Clipboard. Nhấn Ctrl+C để thoát.");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
var monitor = host.Services.GetRequiredService<IClipboardMonitor>();
try
{
    await monitor.RunAsync(async (job, token) =>
    {
        await ProcessAsync(job, processor, repository, renderer, parsed.DebugWindow, token);
    }, shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}
return 0;

static async Task ProcessAsync(
    ImageJob job,
    IImageJobProcessor processor,
    IResultRepository repository,
    IBoxRenderer renderer,
    bool debugWindow,
    CancellationToken cancellationToken)
{
    var result = await processor.ProcessAsync(job, cancellationToken);
    switch (result)
    {
        case ProcessingResult.Ignored ignored:
            Console.WriteLine($"Bỏ qua: {ignored.Reason}");
            break;
        case ProcessingResult.QueryCollected collected:
            Console.WriteLine($"Đã thêm {collected.QueryId}: {collected.ImagePath}");
            break;
        case ProcessingResult.ReviewRequired review:
            var saved = await repository.SaveAsync(review.Session, cancellationToken);
            Console.WriteLine($"Đã lưu {review.Session.Matches.Count} khung: {saved.MarkedImagePath}");
            if (debugWindow)
            {
                using var mat = MatConversion.ToMat(renderer.Draw(review.Session.Original, review.Session.Matches));
                Cv2.ImShow("AutoMarker Re-ID CLI", mat);
                Cv2.WaitKey(0);
                Cv2.DestroyAllWindows();
            }
            break;
    }
}

static void ConfigureServices(IServiceCollection services, StoragePaths paths)
{
    services.AddSingleton(paths);
    services.AddSingleton(new ModelLocations(paths.Models));
    services.AddSingleton<UserSelectionState>();
    services.AddSingleton<QueryCatalog>();
    services.AddSingleton<IImageCodec, OpenCvImageCodec>();
    services.AddSingleton<ICandidateGenerator, OpenCvCandidateGenerator>();
    services.AddSingleton<IBoxRenderer, OpenCvBoxRenderer>();
    services.AddSingleton<IInterfaceDetector>(_ => new OpenCvInterfaceDetector(paths.UiTemplate));
    services.AddSingleton<IFileTrashService, WindowsFileTrashService>();
    services.AddSingleton<IClipboardMonitor, WindowsClipboardMonitor>();
    services.AddSingleton<IFeatureCache, FileFeatureCache>();
    services.AddSingleton<IQueryRepository, FileQueryRepository>();
    services.AddSingleton<IResultRepository, FileResultRepository>();
    services.AddSingleton<IModelRuntime, OpenVinoModelRuntime>();
    services.AddSingleton<IOcrService>(provider => new OpenVinoOcrService(
        Path.Combine(AppContext.BaseDirectory, "assets", "ocr", "PP-OCRv6_rec_small.onnx"),
        Path.Combine(AppContext.BaseDirectory, "assets", "ocr", "ppocrv6_dict.txt"),
        provider.GetRequiredService<ILogger<OpenVinoOcrService>>()));
    services.AddSingleton<IEngineInitializer, EngineInitializer>();
    services.AddSingleton<IQueryCollector, QueryCollector>();
    services.AddSingleton<IMatchEngine, OpenVinoMatchEngine>();
    services.AddSingleton<IImageJobProcessor, ImageProcessingPipeline>();
}

internal sealed record CliOptions(
    string? SingleFile,
    string? Query,
    string? QueriesDirectory,
    string? OutputDirectory,
    string? ModelsDirectory,
    float? Threshold,
    bool DebugWindow,
    bool Verbose,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] arguments)
    {
        string? single = null, query = null, queries = null, output = null, models = null;
        float? threshold = null;
        var debug = false;
        var verbose = false;
        var help = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            var option = arguments[index];
            string Value() => index + 1 < arguments.Length
                ? arguments[++index]
                : throw new ArgumentException($"Thiếu giá trị cho {option}.");
            switch (option)
            {
                case "--single": single = Path.GetFullPath(Value()); break;
                case "--query": query = Value(); break;
                case "--queries-dir": queries = Path.GetFullPath(Value()); break;
                case "--output-dir": output = Path.GetFullPath(Value()); break;
                case "--models-dir": models = Path.GetFullPath(Value()); break;
                case "--threshold": threshold = float.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--debug-window":
                case "--window": debug = true; break;
                case "--verbose": verbose = true; break;
                case "--help":
                case "-h": help = true; break;
                default: throw new ArgumentException($"Tham số không hỗ trợ: {option}");
            }
        }

        if (query is not null && (!query.StartsWith("Query_", StringComparison.OrdinalIgnoreCase) ||
                                  !int.TryParse(query.AsSpan("Query_".Length), out var queryNumber) || queryNumber is < 1 or > 999))
            throw new ArgumentException("--query phải có dạng Query_1 đến Query_999.");
        if (threshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(arguments), "--threshold phải nằm trong [0,1].");
        return new CliOptions(single, query, queries, output, models, threshold, debug, verbose, help);
    }

    public static void PrintHelp() => Console.WriteLine("""
        AutoMarker Re-ID CLI (.NET 10, không Python)

        --single <file>       Xử lý một ảnh rồi thoát
        --query Query_N       Giới hạn matching và Query thu thập
        --queries-dir <dir>   Đổi thư mục queries
        --output-dir <dir>    Đổi thư mục output
        --models-dir <dir>    Đổi thư mục model OpenVINO
        --threshold <0..1>    Ghi đè ngưỡng body Re-ID
        --debug-window        Hiện ảnh kết quả; nhấn phím để đóng
        --verbose             Log chi tiết
        --help                Hiện trợ giúp

        Không có --single: theo dõi Clipboard đến khi nhấn Ctrl+C.
        """);
}
