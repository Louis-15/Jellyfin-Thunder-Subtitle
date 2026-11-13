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
        "扫描开启了「自动补全」的影视库，对完全没有字幕的条目按各库配置的检索方式（番号 / 片名）搜索并下载最合适的字幕，写成 <片名>.chi.srt/.ass。已有字幕的条目不会覆盖，需要升级请用「迅雷字幕 · 自动择优」。";

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
