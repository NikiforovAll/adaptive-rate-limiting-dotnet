using Microsoft.Extensions.Configuration;
using static Nall.RateLimiting.Adaptive.ConfigurationReader;

namespace Nall.RateLimiting.Adaptive;

internal static class AdaptiveRateLimiterOptionsBinder
{
    public static void Bind(IConfiguration section, AdaptiveRateLimiterOptions o)
    {
        o.Enabled = Read(section, nameof(o.Enabled), o.Enabled);
        o.InstanceCount = Read(section, nameof(o.InstanceCount), o.InstanceCount);
        o.InitialUnitsPerMinute = Read(
            section,
            nameof(o.InitialUnitsPerMinute),
            o.InitialUnitsPerMinute
        );
        o.MinUnitsPerMinute = Read(section, nameof(o.MinUnitsPerMinute), o.MinUnitsPerMinute);
        o.MaxUnitsPerMinute = Read(section, nameof(o.MaxUnitsPerMinute), o.MaxUnitsPerMinute);
        o.DecreaseCooldown = Read(section, nameof(o.DecreaseCooldown), o.DecreaseCooldown);
        o.Burst = Read(section, nameof(o.Burst), o.Burst);
        o.QueueLimit = Read(section, nameof(o.QueueLimit), o.QueueLimit);
        o.MaxWait = Read(section, nameof(o.MaxWait), o.MaxWait);
        o.MaxPause = Read(section, nameof(o.MaxPause), o.MaxPause);
        o.MaxRequestUnits = Read(section, nameof(o.MaxRequestUnits), o.MaxRequestUnits);
        o.Controller = ReadEnum(section, nameof(o.Controller), o.Controller);

        var aimd = section.GetSection(nameof(o.Aimd));
        o.Aimd ??= new();
        o.Aimd.DecreaseFactor = Read(
            aimd,
            nameof(AimdOptions.DecreaseFactor),
            o.Aimd.DecreaseFactor
        );
        o.Aimd.IncreaseStep = Read(aimd, nameof(AimdOptions.IncreaseStep), o.Aimd.IncreaseStep);
        o.Aimd.IncreaseInterval = Read(
            aimd,
            nameof(AimdOptions.IncreaseInterval),
            o.Aimd.IncreaseInterval
        );

        var cubic = section.GetSection(nameof(o.Cubic));
        o.Cubic ??= new();
        o.Cubic.Beta = Read(cubic, nameof(CubicOptions.Beta), o.Cubic.Beta);
        o.Cubic.ScaleConstant = Read(
            cubic,
            nameof(CubicOptions.ScaleConstant),
            o.Cubic.ScaleConstant
        );
    }
}
