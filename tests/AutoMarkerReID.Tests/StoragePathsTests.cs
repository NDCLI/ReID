using AutoMarkerReID.Persistence;

namespace AutoMarkerReID.Tests;

public sealed class StoragePathsTests
{
    [Fact]
    public void UsesBundledInterfaceTemplateInsteadOfUserDataDirectory()
    {
        var userData = Path.Combine(Path.GetTempPath(), $"automarker-data-{Guid.NewGuid():N}");
        var paths = new StoragePaths(userData);

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "assets", "ui_template.png"), paths.UiTemplate);
    }
}
