namespace AutoMarkerReID.Domain;

public enum AppRuntimeState
{
    Starting,
    Monitoring,
    Capturing,
    Processing,
    Reviewing,
    RebuildingCache,
    Error,
    ShuttingDown,
}

public static class ReIdDefaults
{
    public static readonly TimeSpan ClipboardPollInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan ClipboardReadyTimeout = TimeSpan.FromSeconds(5);
    public const float InterfaceMatchThreshold = 0.70f;
    public const float AutoPixelThreshold = 0.86f;
    public const float AiMatchThreshold = 0.68f;
    public const float AiMatchMargin = 0.06f;
    public const float BestReferenceThreshold = 0.62f;
    public const int TopReferenceCount = 2;
    public const float FaceDetectionThreshold = 0.75f;
    public const float FaceMatchThreshold = 0.65f;
    public const float FaceMatchMargin = 0.20f;
    public const float FastShortlistThreshold = 0.45f;
    public const float FastFallbackShortlistThreshold = 0.35f;
    public const int FastFallbackMaxCards = 5;
    public const int FastMaxRows = 3;
    public const int MaxPixelCandidates = 150;
    public const float IgnoreLeftRatio = 0.25f;
    public const float IgnoreBottomRatio = 0f;
    public const float NmsThreshold = 0.30f;
    public const float AppearanceFloor = 0.75f;
    public const float AppearanceMargin = 0.02f;
    public const int BoxThickness = 2;
    public const int BoxMinimumGap = 4;
}
