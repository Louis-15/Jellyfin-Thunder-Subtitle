using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>Verbosity of the plugin's own log lines.</summary>
public enum PluginLogLevel
{
    /// <summary>Only errors.</summary>
    Error = 0,

    /// <summary>Errors and warnings.</summary>
    Warning = 1,

    /// <summary>Normal run information (default).</summary>
    Information = 2,

    /// <summary>Per-candidate detail, useful when debugging a single movie.</summary>
    Debug = 3
}

/// <summary>
/// Thin level-filtering wrapper around Jellyfin's logger. Every line is stamped with
/// Asia/Shanghai time because the host runs in UTC and these lines are matched against
/// the python helper scripts' log.
/// </summary>
public sealed class PluginLog
{
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="PluginLog"/> class.</summary>
    /// <param name="logger">The underlying Jellyfin logger.</param>
    public PluginLog(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Gets the level currently configured on the plugin.</summary>
    public static PluginLogLevel ConfiguredLevel => Plugin.Current.ResolveLogLevel();

    /// <summary>Writes a line when the configured level is at least <see cref="PluginLogLevel.Error"/>.</summary>
    /// <param name="message">The message.</param>
    public void Error(string message) => Write(PluginLogLevel.Error, message);

    /// <summary>Writes a line when the configured level is at least <see cref="PluginLogLevel.Warning"/>.</summary>
    /// <param name="message">The message.</param>
    public void Warn(string message) => Write(PluginLogLevel.Warning, message);

    /// <summary>Writes a line when the configured level is at least <see cref="PluginLogLevel.Information"/>.</summary>
    /// <param name="message">The message.</param>
    public void Info(string message) => Write(PluginLogLevel.Information, message);

    /// <summary>Writes a line when the configured level is at least <see cref="PluginLogLevel.Debug"/>.</summary>
    /// <param name="message">The message.</param>
    public void Debug(string message) => Write(PluginLogLevel.Debug, message);

    /// <summary>Builds the standard <c>[time] message</c> prefix used in every line.</summary>
    /// <param name="message">The raw message.</param>
    /// <returns>The stamped message.</returns>
    public static string Stamp(string message)
    {
        return $"[{Plugin.FormatBeijingTime(DateTimeOffset.UtcNow)} CST] {message}";
    }

    private void Write(PluginLogLevel level, string message)
    {
        if (level > ConfiguredLevel)
        {
            return;
        }

        var text = Stamp(message);
        switch (level)
        {
            case PluginLogLevel.Error:
                _logger.LogError("{Message}", text);
                break;
            case PluginLogLevel.Warning:
                _logger.LogWarning("{Message}", text);
                break;
            case PluginLogLevel.Debug:
                _logger.LogDebug("{Message}", text);
                break;
            default:
                _logger.LogInformation("{Message}", text);
                break;
        }
    }
}
