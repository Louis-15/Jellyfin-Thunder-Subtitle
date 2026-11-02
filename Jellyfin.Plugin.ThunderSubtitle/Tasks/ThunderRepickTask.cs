using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ThunderSubtitle.Tasks;

/// <summary>
/// Re-runs the search for movies whose subtitle was written by this plugin and replaces
/// it when a better candidate exists (fewer machine-translation markers, then bigger).
/// </summary>
public sealed class ThunderRepickTask : IScheduledTask
{
    private readonly SubtitleJob _job;

    /// <summary>Initializes a new instance of the <see cref="ThunderRepickTask"/> class.</summary>
    /// <param name="job">The shared job runner.</param>
    public ThunderRepickTask(SubtitleJob job)
    {
        _job = job;
    }

    /// <inheritdoc />
    public string Name => "迅雷字幕 · 重新择优";

    /// <inheritdoc />
    public string Key => "ThunderSubtitleRepick";

    /// <inheritdoc />
    public string Description =>
        "对已经由本插件写入的 .chi 字幕重新搜索，遇到更完整（或非机翻）的候选就替换。不会碰 115 同步下来的字幕。";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return _job.RunAsync(repick: true, progress, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Rare and at a quiet hour: re-picking is meant to be a manual/occasional action.
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.WeeklyTrigger,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        ];
    }
}
