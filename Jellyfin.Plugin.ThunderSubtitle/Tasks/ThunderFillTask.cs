using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.ThunderSubtitle.Core;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ThunderSubtitle.Tasks;

/// <summary>
/// Finds movies in the configured libraries that have no subtitle at all, searches the
/// Xunlei API by their release code and writes the best candidate next to the video file.
/// </summary>
public sealed class ThunderFillTask : IScheduledTask
{
    private readonly SubtitleJob _job;

    /// <summary>Initializes a new instance of the <see cref="ThunderFillTask"/> class.</summary>
    /// <param name="job">The shared job runner.</param>
    public ThunderFillTask(SubtitleJob job)
    {
        _job = job;
    }

    /// <inheritdoc />
    public string Name => "迅雷字幕 · 自动补全";

    /// <inheritdoc />
    public string Key => "ThunderSubtitleFill";

    /// <inheritdoc />
    public string Description =>
        "扫描目标影视库中完全没有字幕的影片，按番号从迅雷字幕搜索并下载最合适的字幕，写成 <片名>.chi.srt/.ass。";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return _job.RunAsync(repick: false, progress, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            }
        ];
    }
}
