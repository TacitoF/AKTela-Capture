namespace AKTelaCapture;

internal sealed class PerformanceQualityPolicy
{
    internal const long RecoveryWindowMs = 60_000;
    private long _lastSampleAt = -1;
    private long? _stableSince;
    private int _poorSamples;
    public string LimitKey { get; private set; } = "1080p60";

    public void Reset(string requested)
    {
        LimitKey = requested;
        _lastSampleAt = -1;
        _stableSince = null;
        _poorSamples = 0;
    }

    public void LimitForSoftware()
    {
        LimitKey = "720p30";
        _stableSince = null;
        _poorSamples = 0;
    }

    public bool Evaluate(string requested, string active, int targetFps, double actualFps,
        bool running, bool software, bool healthy, long now)
    {
        var previous = LimitKey;
        if (software)
        {
            LimitForSoftware();
            return LimitKey != previous;
        }
        if (!running || actualFps <= 0 || !double.IsFinite(actualFps))
        {
            _stableSince = null;
            _poorSamples = 0;
            return false;
        }
        if (_lastSampleAt >= 0 && now - _lastSampleAt < 4000) return false;
        _lastSampleAt = now;

        var overloaded = actualFps < targetFps * 0.82;
        _poorSamples = overloaded ? _poorSamples + 1 : 0;
        if (_poorSamples >= 2)
        {
            LimitKey = QualityOption.LowerForPerformance(LimitKey);
            _poorSamples = 0;
            _stableSince = null;
            return LimitKey != previous;
        }

        if (!healthy || actualFps < targetFps * 0.95 || active != LimitKey)
        {
            _stableSince = null;
            return false;
        }
        _stableSince ??= now;
        if (now - _stableSince.Value < RecoveryWindowMs) return false;
        _stableSince = null;
        LimitKey = QualityOption.HigherForPerformance(LimitKey, requested);
        return LimitKey != previous;
    }
}
