namespace AutoMarkerReID.Application;

public sealed class UserSelectionState
{
    private readonly Lock _sync = new();
    private string? _recognitionScope;
    private string _targetQuery = "Query_1";
    private bool _appearanceEnabled;
    private bool _saveCaptures;
    private float? _matchThresholdOverride;

    public string? RecognitionScope
    {
        get { lock (_sync) { return _recognitionScope; } }
        set { lock (_sync) { _recognitionScope = value; } }
    }

    public string TargetQuery
    {
        get { lock (_sync) { return _targetQuery; } }
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            lock (_sync) { _targetQuery = value; }
        }
    }

    public bool AppearanceEnabled
    {
        get { lock (_sync) { return _appearanceEnabled; } }
        set { lock (_sync) { _appearanceEnabled = value; } }
    }

    public bool SaveCaptures
    {
        get { lock (_sync) { return _saveCaptures; } }
        set { lock (_sync) { _saveCaptures = value; } }
    }

    public float? MatchThresholdOverride
    {
        get { lock (_sync) { return _matchThresholdOverride; } }
        set { lock (_sync) { _matchThresholdOverride = value; } }
    }
}
