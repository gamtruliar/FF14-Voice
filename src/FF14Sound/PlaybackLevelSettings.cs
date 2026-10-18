namespace FF14Sound;

public sealed record PlaybackLevelSettings(bool Enabled = false, double MinimumDb = -26, double MaximumDb = -16, double PeakDb = -3)
{
    public PlaybackLevelSettings Validated()
    {
        var maximum = double.IsFinite(MaximumDb) ? Math.Clamp(MaximumDb, -30, -8) : -16;
        var minimum = double.IsFinite(MinimumDb) ? Math.Clamp(MinimumDb, -40, maximum - 3) : Math.Min(-26, maximum - 3);
        return this with { MinimumDb = minimum, MaximumDb = maximum, PeakDb = double.IsFinite(PeakDb) ? Math.Clamp(PeakDb, -12, -1) : -3 };
    }
}
