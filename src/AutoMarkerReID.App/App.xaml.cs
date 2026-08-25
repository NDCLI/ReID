using System.IO;
using System.Windows;
using AutoMarkerReID.Application;
using AutoMarkerReID.Imaging;
using AutoMarkerReID.Inference;
using AutoMarkerReID.Persistence;
using AutoMarkerReID.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMarkerReID.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private SingleInstanceGuard? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = SingleInstanceGuard.TryAcquire("AutoMarkerReID-CSharp");
        if (!_singleInstance.IsOwner)
        {
            DarkMessageBox.Show(null, "AutoMarker Re-ID đang chạy trong khay hệ thống.", "AutoMarker Re-ID", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var baseDirectory = ResolveBaseDirectory();
        var paths = new StoragePaths(baseDirectory, Path.Combine(AppContext.BaseDirectory, "assets", "models"));
        paths.EnsureCreated();
        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            })
            .ConfigureServices(services => ConfigureServices(services, paths))
            .Build();

        var preferences = _host.Services.GetRequiredService<UserPreferencesStore>().Load();
        LocalizationService.Configure(preferences.Language);

        await _host.StartAsync().ConfigureAwait(true);
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
        if (e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
        {
            mainWindow.Hide();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host.Dispose();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services, StoragePaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(new ModelLocations(paths.Models));
        services.AddSingleton<UserPreferencesStore>();
        services.AddSingleton(provider =>
        {
            var selection = new UserSelectionState();
            provider.GetRequiredService<UserPreferencesStore>().Apply(selection);
            return selection;
        });
        services.AddSingleton<ClipboardActivityStats>();
        services.AddSingleton<QueryCatalog>();
        services.AddSingleton<ObservableLogStore>();
        services.AddSingleton<ILoggerProvider, ObservableLogProvider>();

        services.AddSingleton<IImageCodec, OpenCvImageCodec>();
        services.AddSingleton<ICandidateGenerator, OpenCvCandidateGenerator>();
        services.AddSingleton<IBoxRenderer, OpenCvBoxRenderer>();
        services.AddSingleton<IInterfaceDetector>(_ => new OpenCvInterfaceDetector(paths.UiTemplate));

        services.AddSingleton<IFileTrashService, WindowsFileTrashService>();
        services.AddSingleton<IClipboardMonitor, WindowsClipboardMonitor>();
        services.AddSingleton<IClipboardWriter, WindowsClipboardWriter>();
        services.AddSingleton<IScreenCaptureService, WindowsScreenCaptureService>();

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
        services.AddSingleton<IReviewCompletionService, ReviewCompletionService>();

        services.AddSingleton<ApplicationController>();
        services.AddHostedService(provider => provider.GetRequiredService<ApplicationController>());
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private static string ResolveBaseDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("AUTOMARKER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        if (File.Exists(Path.Combine(Environment.CurrentDirectory, "APP_FEATURES_AND_LOGIC.md")))
        {
            return Environment.CurrentDirectory;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoMarkerReID");
    }
}
