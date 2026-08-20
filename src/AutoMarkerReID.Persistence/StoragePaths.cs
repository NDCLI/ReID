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
    public string UiTemplate => UiTemplateOverride ?? Path.Combine(BaseDirectory, "assets", "ui_template.png");

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
