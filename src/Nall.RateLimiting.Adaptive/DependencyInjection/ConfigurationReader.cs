using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Nall.RateLimiting.Adaptive;

/// <summary>
/// Reads settings from configuration without reflection, so the package stays trim and AOT
/// safe. A key that is not set keeps the current value.
/// </summary>
internal static class ConfigurationReader
{
    public static bool Read(IConfiguration section, string key, bool current) =>
        Read(section, key, current, static v => bool.Parse(v));

    public static int Read(IConfiguration section, string key, int current) =>
        Read(section, key, current, static v => int.Parse(v, CultureInfo.InvariantCulture));

    public static double Read(IConfiguration section, string key, double current) =>
        Read(section, key, current, static v => double.Parse(v, CultureInfo.InvariantCulture));

    public static TimeSpan Read(IConfiguration section, string key, TimeSpan current) =>
        Read(section, key, current, static v => TimeSpan.Parse(v, CultureInfo.InvariantCulture));

    public static TEnum ReadEnum<TEnum>(IConfiguration section, string key, TEnum current)
        where TEnum : struct, Enum =>
        Read(section, key, current, static v => Enum.Parse<TEnum>(v, ignoreCase: true));

    private static T Read<T>(IConfiguration section, string key, T current, Func<string, T> parse)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return current;
        }

        try
        {
            return parse(value.Trim());
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Failed to convert configuration value '{value}' at '{Path(section, key)}' to type '{typeof(T)}'.",
                ex
            );
        }
    }

    private static string Path(IConfiguration section, string key) =>
        section is IConfigurationSection { Path: { Length: > 0 } path }
            ? ConfigurationPath.Combine(path, key)
            : key;
}
