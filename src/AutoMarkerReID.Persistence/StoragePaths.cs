namespace AutoMarkerReID.Persistence;

public sealed record StoragePaths(
    string BaseDirectory,
    string? ModelsOverride = null,
    string? QueriesOverride = null,
    string? OutputOverride = null,
    string? UiTemplateOverride = null)
{
    public string Queries => QueriesOverride ?? Path.Combine(BaseDirectory, "queries");
    public string Output => OutputOverride ?? Path.Combine(BaseDirectory, "output");
    public string Models => ModelsOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReIDAutoOSNet",
            "models");
    // The interface template is an application asset, not user data. The data
    // directory normally lives under LocalAppData and does not contain assets.
    public string UiTemplate => UiTemplateOverride ?? Path.Combine(AppContext.BaseDirectory, "assets", "ui_template.png");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Queries);
        Directory.CreateDirectory(Output);
        Directory.CreateDirectory(Models);
        for (var index = 1; index <= 14; index++)
        {
            Directory.CreateDirectory(Path.Combine(Queries, $"Query_{index}"));
        }
    }
}
