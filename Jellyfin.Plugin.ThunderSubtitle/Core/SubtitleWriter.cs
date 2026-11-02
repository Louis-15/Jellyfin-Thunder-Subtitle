using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>Writes one chosen candidate next to the video and makes Jellyfin pick it up.</summary>
public interface ISubtitleWriter
{
    /// <summary>Writes the candidate as the sidecar of <paramref name="item"/>.</summary>
    /// <param name="item">The movie the subtitle belongs to.</param>
    /// <param name="code">The code that was searched, stored in the provenance ledger.</param>
    /// <param name="pick">The candidate to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path that was written.</returns>
    Task<string> WriteAsync(BaseItem item, string code, SubtitlePick pick, CancellationToken cancellationToken);
}

/// <summary>
/// The manual counterpart of <see cref="SubtitleJob"/>: the preview page lets the user read a
/// candidate first, and only this class actually puts it on disk.
/// </summary>
public sealed class SubtitleWriter : ISubtitleWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IDirectoryService _directoryService;
    private readonly ProvenanceStore _provenance;
    private readonly PluginLog _log;

    /// <summary>Initializes a new instance of the <see cref="SubtitleWriter"/> class.</summary>
    /// <param name="libraryManager">Library manager, used to re-read the item after writing.</param>
    /// <param name="providerManager">Used to refresh the item so Jellyfin sees the new sidecar.</param>
    /// <param name="directoryService">Needed to build <see cref="MetadataRefreshOptions"/>.</param>
    /// <param name="provenance">Ledger of subtitles this plugin wrote.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleWriter(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IDirectoryService directoryService,
        ProvenanceStore provenance,
        Microsoft.Extensions.Logging.ILogger<SubtitleWriter> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _directoryService = directoryService;
        _provenance = provenance;
        _log = new PluginLog(logger);
    }

    /// <inheritdoc />
    public async Task<string> WriteAsync(BaseItem item, string code, SubtitlePick pick, CancellationToken cancellationToken)
    {
        var mediaPath = item.Path;
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            throw new InvalidOperationException("这个条目没有文件路径，无法写入字幕。");
        }

        var target = Sidecar.TargetPath(mediaPath, pick.Extension);
        foreach (var stale in Sidecar.RemoveStalePluginSubtitles(mediaPath, target))
        {
            _log.Info($"预览写入：删除旧扩展名字幕「{Path.GetFileName(stale)}」。");
        }

        var payload = Utf8NoBom.GetBytes(SubtitleText.Normalize(pick.Text) + "\n");
        await Sidecar.WriteAtomicAsync(target, payload, cancellationToken).ConfigureAwait(false);
        _log.Info($"预览写入：「{Path.GetFileName(target)}」{payload.Length} B（{pick.CandidateName}，{pick.EncodingName} → utf-8）。");

        _provenance.Set(new SubtitleProvenance(
            item.Id.ToString("N"),
            code,
            target,
            pick.CandidateName,
            payload.Length,
            pick.AiMarkers,
            DateTimeOffset.UtcNow));

        await RefreshAsync(item, cancellationToken).ConfigureAwait(false);
        return target;
    }

    /// <summary>
    /// Re-probes the local files so the new sidecar is attached. <c>ValidationOnly</c> keeps the
    /// library's own subtitle downloaders out of it, which is what produces <c>.chi.0.srt</c>.
    /// </summary>
    private async Task RefreshAsync(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            var options = new MetadataRefreshOptions(_directoryService)
            {
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ImageRefreshMode = MetadataRefreshMode.None,
                EnableRemoteContentProbe = false,
                IsAutomated = true
            };

            var fresh = _libraryManager.GetItemById(item.Id) ?? item;
            await _providerManager.RefreshSingleItem(fresh, options, cancellationToken).ConfigureAwait(false);

            var streams = _libraryManager.GetItemById(item.Id)?.GetMediaStreams()
                .Count(stream => stream.Type == MediaStreamType.Subtitle) ?? 0;
            _log.Info($"预览写入：刷新后「{item.Name}」识别到 {streams} 条字幕流。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The file is on disk either way; the next library scan attaches it.
            _log.Warn($"预览写入：刷新条目失败（{ex.Message}），文件已写入，下次扫描会识别。");
        }
    }
}
