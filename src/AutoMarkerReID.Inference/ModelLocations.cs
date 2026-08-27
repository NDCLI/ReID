namespace AutoMarkerReID.Inference;

public sealed record ModelLocations(string Directory)
{
    public IReadOnlyList<BodyModelDefinition> BodyModels =>
    [
        new("osnet_0288", Path.Combine(Directory, "reid.xml"), 0.25f, 256, 128),
        new("osnet_lct_0277", Path.Combine(Directory, "reid_0277.xml"), 0.75f, 256, 128),
        new("osnet_lct_0286", Path.Combine(Directory, "reid_0286.xml"), 1.00f, 256, 128),
    ];

    public string FaceDetection => Path.Combine(Directory, "face-detection-retail-0005.xml");
}

public sealed record BodyModelDefinition(string Name, string Path, float Weight, int InputHeight, int InputWidth);
