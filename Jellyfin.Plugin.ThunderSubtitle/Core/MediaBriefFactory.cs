using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 把 Jellyfin 条目翻译成 <see cref="MediaBrief"/>。
///
/// 单独放一个文件是为了让 <see cref="MediaBrief"/> 保持零依赖：
/// 离线工具（SubtitleProbe）只编译 Core 里不碰 Jellyfin 的类型，
/// 元数据卡本身要能被它引用，才能生成「和线上完全一样」的 prompt。
/// </summary>
internal static class MediaBriefFactory
{
    /// <summary>题材标签最多取 5 个。再多也不会让模型更懂这部片，只会稀释重点。</summary>
    private const int MaxGenres = 5;

    /// <summary>从 Jellyfin 条目构造元数据卡。</summary>
    /// <param name="item">The movie or episode.</param>
    /// <returns>The brief, never null.</returns>
    public static MediaBrief FromItem(BaseItem? item)
    {
        if (item is null)
        {
            return MediaBrief.Empty;
        }

        var genres = item.Genres ?? Array.Empty<string>();
        var title = string.IsNullOrWhiteSpace(item.Name) ? (item.OriginalTitle ?? string.Empty) : item.Name;

        // 原名与显示名相同就没有信息量，不要去 prompt 里重复一遍。
        var original = string.IsNullOrWhiteSpace(item.OriginalTitle)
                       || string.Equals(item.OriginalTitle, title, StringComparison.OrdinalIgnoreCase)
            ? null
            : item.OriginalTitle;

        return new MediaBrief(
            title,
            original,
            item.ProductionYear is > 0 ? item.ProductionYear : null,
            ResolveContentType(item, genres),
            genres.Where(genre => !string.IsNullOrWhiteSpace(genre)).Take(MaxGenres).ToArray(),
            item.ProductionLocations ?? Array.Empty<string>(),
            Truncate(item.Overview, MediaBrief.MaxOverviewChars),
            item.RunTimeTicks is long ticks and > 0
                ? (int)(ticks / TimeSpan.TicksPerMinute)
                : null);
    }

    private static string ResolveContentType(BaseItem item, IReadOnlyList<string> genres)
    {
        if (item is Episode)
        {
            return "剧集";
        }

        // 动画的用词尺度和真人影视差很远（术语、称呼、语气词），单独标出来。
        var isAnimation = genres.Any(genre =>
            genre.Contains("Animation", StringComparison.OrdinalIgnoreCase)
            || genre.Contains("Anime", StringComparison.OrdinalIgnoreCase)
            || genre.Contains("动画", StringComparison.Ordinal));

        if (isAnimation)
        {
            return "动画";
        }

        return item is Movie ? "电影" : "影视";
    }

    private static string? Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars] + "…";
    }
}
