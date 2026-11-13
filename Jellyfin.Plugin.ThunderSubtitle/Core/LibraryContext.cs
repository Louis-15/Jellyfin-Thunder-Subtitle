using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 把「条目 / 媒体路径」翻译成「该用哪种检索方式」和具体的 <see cref="SearchTarget"/>。
///
/// 抽成接口是为了可测试：真实实现要查 Jellyfin 的影视库归属，
/// 而单元测试里没有（也不需要）一整套 ILibraryManager。
/// </summary>
public interface ILibraryContext
{
    /// <summary>取条目所属的、且在配置里勾选过的影视库 Id。</summary>
    /// <param name="item">The item.</param>
    /// <returns>The configured library id, or <c>null</c>.</returns>
    Guid? LibraryIdForItem(BaseItem? item);

    /// <summary>取条目所在影视库配置的检索方式。</summary>
    /// <param name="item">The item.</param>
    /// <returns>The configured mode, defaulting to code search.</returns>
    SubtitleSearchMode ModeForItem(BaseItem? item);

    /// <summary>按媒体路径推断检索方式（手动搜索只有路径，没有条目 Id）。</summary>
    /// <param name="mediaPath">Path of the video file.</param>
    /// <returns>The configured mode, defaulting to code search.</returns>
    SubtitleSearchMode ModeForPath(string? mediaPath);

    /// <summary>Builds the search target for a library item.</summary>
    /// <param name="item">The movie or episode.</param>
    /// <param name="mode">The mode configured for its library.</param>
    /// <returns>The target, or <c>null</c> when nothing usable can be derived.</returns>
    SearchTarget? FromItem(BaseItem item, SubtitleSearchMode mode);

    /// <summary>Builds the search target for Jellyfin's manual "search subtitles" dialog.</summary>
    /// <param name="request">The search request Jellyfin passed in.</param>
    /// <param name="mode">The mode configured for the item's library.</param>
    /// <returns>The target, or <c>null</c> when nothing usable can be derived.</returns>
    SearchTarget? FromRequest(SubtitleSearchRequest request, SubtitleSearchMode mode);
}

/// <summary>默认实现：影视库归属来自 <see cref="ILibraryManager"/>，设置来自插件配置。</summary>
public sealed class LibraryContext : ILibraryContext
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="LibraryContext"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    public LibraryContext(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public Guid? LibraryIdForItem(BaseItem? item)
    {
        if (item is null)
        {
            return null;
        }

        var config = Plugin.Current;

        // 一部影片可能同时挂在多个库里（少见），取第一个在配置中出现过的库。
        foreach (var library in _libraryManager.GetCollectionFolders(item))
        {
            if (config.IsLibraryConfigured(library.Id))
            {
                return library.Id;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public SubtitleSearchMode ModeForItem(BaseItem? item)
    {
        var libraryId = LibraryIdForItem(item);
        return libraryId is null
            ? SubtitleSearchMode.Code
            : Plugin.Current.ResolveSearchMode(libraryId.Value);
    }

    /// <inheritdoc />
    public SubtitleSearchMode ModeForPath(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return SubtitleSearchMode.Code;
        }

        var item = _libraryManager.FindByPath(mediaPath, isFolder: false);
        if (item is not null)
        {
            return ModeForItem(item);
        }

        // 条目还没入库（例如刚扫描到一半）时退回路径前缀匹配。
        var config = Plugin.Current;
        foreach (var libraryId in config.ResolveLibraryIds())
        {
            var library = _libraryManager.GetItemById(libraryId);
            if (library?.Path is { Length: > 0 } root
                && mediaPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return config.ResolveSearchMode(libraryId);
            }
        }

        return SubtitleSearchMode.Code;
    }

    /// <inheritdoc />
    public SearchTarget? FromItem(BaseItem item, SubtitleSearchMode mode)
    {
        return mode == SubtitleSearchMode.Code
            ? FromCode(ReleaseCode.FromPath(item.Path) ?? ReleaseCode.FromText(item.Name))
            : FromEpisodeOrMovie(item);
    }

    /// <inheritdoc />
    public SearchTarget? FromRequest(SubtitleSearchRequest request, SubtitleSearchMode mode)
    {
        if (mode == SubtitleSearchMode.Code)
        {
            return FromCode(ReleaseCode.FromPath(request.MediaPath) ?? ReleaseCode.FromText(request.Name));
        }

        if (request.ContentType == VideoContentType.Episode)
        {
            return SearchTarget.ForTitle(
                request.SeriesName ?? request.Name,
                year: null,
                season: request.ParentIndexNumber,
                episode: request.IndexNumber);
        }

        return SearchTarget.ForTitle(request.Name, request.ProductionYear);
    }

    private static SearchTarget? FromCode(string? code)
    {
        return string.IsNullOrWhiteSpace(code) ? null : SearchTarget.ForCode(code);
    }

    private static SearchTarget? FromEpisodeOrMovie(BaseItem item)
    {
        if (item is Episode episode)
        {
            // 剧集用「剧名 + SxxExx」查：实测中文剧名也能查到英文命名的候选。
            var series = string.IsNullOrWhiteSpace(episode.SeriesName) ? episode.Name : episode.SeriesName;
            return SearchTarget.ForTitle(series, year: null, season: episode.ParentIndexNumber, episode: episode.IndexNumber);
        }

        var title = string.IsNullOrWhiteSpace(item.Name) ? item.OriginalTitle : item.Name;
        return SearchTarget.ForTitle(title, item.ProductionYear);
    }
}
