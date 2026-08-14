namespace S1Atlas.Cli.Configuration;

public sealed record UpstreamConfiguration(bool AutoCheck = false, TimeSpan? CheckInterval = null)
{
    public TimeSpan EffectiveCheckInterval => CheckInterval ?? TimeSpan.FromHours(24);
}
