using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// <see cref="SubtitleJob"/> 的辅助方法：刷新条目、读盘、日志用名字。
///
/// 拆成 partial 而不是新类：这些方法都要用 SubtitleJob 的私有字段
/// （_libraryManager / _providerManager / _directoryService / _log），
/// 另外包一层只会让调用点更难读。主文件（SubtitleJob.cs）因此保持在 600 行以内。
/// </summary>
public sealed partial class SubtitleJob
{
    /// <summary>
    /// Refreshes the item so Jellyfin attaches the new sidecar, then reports what it saw.
    /// <c>ValidationOnly</c> re-probes the local files without running the library's own
    /// subtitle downloaders, which is what produces the <c>.chi.0.srt</c> duplicates.
    /// </summary>
    private async Task VerifyAsync(BaseItem item, string target, CancellationToken cancellationToken)
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

            var subtitleStreams = _libraryManager.GetItemById(item.Id)?.GetMediaStreams()
                .Count(stream => stream.Type == MediaStreamType.Subtitle) ?? 0;

            if (subtitleStreams > 0)
            {
                _log.Info($"{Display(item)}：刷新后识别到 {subtitleStreams} 条字幕流，「{Path.GetFileName(target)}」已生效。");
            }
            else
            {
                _log.Warn($"{Display(item)}：字幕已写入，但刷新后 Jellyfin 仍没识别到字幕流，稍后重试刷新。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"{Display(item)}：字幕已写入，但刷新条目失败 - {ex.Message}");
        }
    }

    private static string? TryReadText(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return SubtitleText.TryDecode(bytes)?.Text;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ItemKey(BaseItem item) => item.Id.ToString("N");

    /// <summary>Human readable language name for the log.</summary>
    private static string Describe(SubtitleLanguage language) => language switch
    {
        SubtitleLanguage.SimplifiedChinese => "简体",
        SubtitleLanguage.TraditionalChinese => "繁体",
        SubtitleLanguage.SimplifiedChineseEnglish => "简英双语",
        SubtitleLanguage.TraditionalChineseEnglish => "繁英双语",
        SubtitleLanguage.Japanese => "日文",
        SubtitleLanguage.Korean => "韩文",
        SubtitleLanguage.English => "英文",
        SubtitleLanguage.Other => "其它语言",
        _ => "语言未知"
    };

    private static string Display(BaseItem item)
    {
        var name = Path.GetFileName(item.Path);
        return string.IsNullOrEmpty(name) ? item.Name : name;
    }

    /// <summary>
    /// 把本次送审的每份候选的评分打出来。
    ///
    /// 单独一个方法是为了让 <c>SubtitleJob.Process.cs</c> 的主流程不被 20 行日志撑散：
    /// 读那条流程时关心的是「选谁、要不要写」，评分明细是排查时才看的。
    /// </summary>
    /// <param name="item">The item, for the log prefix.</param>
    /// <param name="code">The code or title that was searched.</param>
    /// <param name="outcome">What the pipeline produced.</param>
    private void LogVerdicts(BaseItem item, string code, FindOutcome outcome)
    {
        if (outcome.Verdicts is null)
        {
            return;
        }

        foreach (var verdict in outcome.Verdicts.Values.OrderBy(value => value.Id))
        {
            var judged = outcome.Shortlist.Count >= verdict.Id ? outcome.Shortlist[verdict.Id - 1] : null;

            // 来源标签和模型是否认可由它自己给（人工 / 机翻 / 语音识别 / 混合），
            // 里面含机翻就按机翻处理，避免模型给出「mixed」后我们误当人工。
            var source = verdict.EffectiveSource switch
            {
                "human" => "人工",
                "ai" => "机翻",
                "asr" => "语音识别",
                "mixed" => "混合",
                _ => verdict.EffectiveSource
            };

            // ✓ 表示模型引用的「问题原句」确实能在候选台词里核对到；
            // ✗ 表示它引了原句但核对不上（多半是编的），审核日志时重点看 ✗。
            var mark = verdict.EvidenceVerified ? "✓" : (verdict.Evidence.Length > 0 ? "✗" : "·");
            var evidence = verdict.Evidence.Length > 0 ? $"「{verdict.Evidence}」" : string.Empty;
            var typos = verdict.Typos > 0 ? $"，错别字 {verdict.Typos} 处" : string.Empty;
            _log.Info($"  AI #{verdict.Id}「{judged?.CandidateName}」{verdict.Score:F0} 分（{source}{typos}）{mark}{evidence} {verdict.Reason}");
        }
    }
}
