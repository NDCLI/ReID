using AutoMarkerReID.App;
using AutoMarkerReID.Application;
using AutoMarkerReID.Persistence;
using Microsoft.Extensions.Logging;

namespace AutoMarkerReID.Tests;

public sealed class UiInfrastructureTests
{
    [Fact]
    public void ObservableLogStoreKeepsLatestTwoHundredEntriesBeforeUiSubscribes()
    {
        var store = new ObservableLogStore();
        using var provider = new ObservableLogProvider(store);
        var logger = provider.CreateLogger("Tests.Startup");

        for (var index = 0; index < 250; index++)
            logger.Log(LogLevel.Information, new EventId(1), index, null, static (value, _) => $"Dòng {value}");

        Assert.Equal(200, store.Snapshot.Count);
        Assert.Contains("Dòng 50", store.Snapshot[0].Message, StringComparison.Ordinal);
        Assert.Contains("Dòng 249", store.Snapshot[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservableLogStoreIncludesRootCauseMessage()
    {
        var store = new ObservableLogStore();
        using var provider = new ObservableLogProvider(store);
        var logger = provider.CreateLogger("Tests.Startup");
        var exception = new TypeInitializationException("OpenVinoSharp.Core", new DllNotFoundException("MSVCP140.dll"));

        logger.Log(LogLevel.Error, new EventId(2), "Không thể khởi động OpenVINO.", exception,
            static (message, _) => message);

        Assert.Contains("OpenVinoSharp.Core", store.Snapshot[0].Message, StringComparison.Ordinal);
        Assert.Contains("MSVCP140.dll", store.Snapshot[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UserPreferencesRoundTripSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"automarker-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new UserPreferencesStore(new StoragePaths(root));
            var saved = new UserSelectionState
            {
                RecognitionScope = "Query_3",
                TargetQuery = "Query_7",
                AppearanceEnabled = true,
                SaveCaptures = false,
            };
            store.Save(saved);
            var restored = new UserSelectionState();

            store.Apply(restored);

            Assert.Equal("Query_3", restored.RecognitionScope);
            Assert.Equal("Query_7", restored.TargetQuery);
            Assert.True(restored.AppearanceEnabled);
            Assert.True(restored.SaveCaptures);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ClipboardActivityStatsTracksVisibleCounters()
    {
        var stats = new ClipboardActivityStats();
        stats.RecordReceived();
        stats.RecordReceived();
        stats.RecordSkipped();
        Assert.Equal(2, stats.Received);
        Assert.Equal(1, stats.Skipped);
    }
}
