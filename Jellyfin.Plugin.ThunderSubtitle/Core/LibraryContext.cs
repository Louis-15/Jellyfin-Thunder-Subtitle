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
    /// <returns>The configured mode, defaulting to title search when the item belongs to no configured library.</returns>
    SubtitleSearchMode ModeForItem(BaseItem? item);

    /// <summary>按媒体路径推断检索方式（手动搜索只有路径，没有条目 Id）。</summary>
    /// <param name="mediaPath">Path of the video file.</param>
    /// <returns>The configured mode, defaulting to title search when no configured library matches.</returns>
    SubtitleSearchMode ModeForPath(string? mediaPath);

    /// <summary>
    /// 取条目所属影片的类型：番号库一律是日本AV，其余靠 Jellyfin 刮削的语言与地区判断。
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The media kind.</returns>
    MediaKind MediaKindForItem(BaseItem? item);

    /// <summary>
    /// 按媒体路径推断影片类型（手动搜索只有路径，没有条目 Id）。
    /// 条目还没入库时，退化为「按库的检索方式」判断。
    /// </summary>
    /// <param name="mediaPath">Path of the video file.</param>
    /// <returns>The media kind, defaulting to non-Chinese film.</returns>
    MediaKind MediaKindForPath(string? mediaPath);

    /// <summary>取条目的影片元数据卡（片名 / 年份 / 题材 / 地区 / 简介），用于拼 AI 提示词。</summary>
    /// <param name="item">The item.</param>
    /// <returns>The brief, never null.</returns>
    MediaBrief BriefForItem(BaseItem? item);

    /// <summary>按媒体路径取影片元数据卡。</summary>
    /// <param name="mediaPath">Path of the video file.</param>
    /// <returns>The brief; empty when the item cannot be resolved.</returns>
    MediaBrief BriefForPath(string? mediaPath);

    /// <summary>
    /// 按媒体路径取条目 Id，用来把前端暂存的附加提示词和这次搜索对上。
    /// 找不到条目时返回 <c>null</c>，调用方就此降级为「本次没有提示词」。
    /// </summary>
    /// <param name="mediaPath">Path of the video file.</param>
    /// <returns>The item id, or <c>null</c>.</returns>
    Guid? ItemIdForPath(string? mediaPath);

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
        // 找不到库归属时退回「片名搜索」而不是「番号搜索」。
        //
        // 番号搜索现在等于「这个条目是日本AV」的唯一判据（见 MediaKindResolver），
        // 所以绝不能把「不知道属于哪个库」当成番号库 —— 那样普通影视会被按假番号
        // 硬过滤，候选全丢。片名搜索对任何内容都是安全的退化方向。
        var libraryId = LibraryIdForItem(item);
        return libraryId is null
            ? SubtitleSearchMode.Title
            : Plugin.Current.ResolveSearchMode(libraryId.Value);
    }

    /// <inheritdoc />
    public SubtitleSearchMode ModeForPath(string? mediaPath)
    {
        return ModeForPathCore(
            mediaPath,
            string.IsNullOrWhiteSpace(mediaPath) ? null : _libraryManager.FindByPath(mediaPath, isFolder: false));
    }

    /// <inheritdoc />
    public MediaKind MediaKindForItem(BaseItem? item)
    {
        // 影片类型只看**显式配置**：库没保存过「番号 / 片名」时传 null，
        // 让 MediaKindResolver 继续按语言和地区判断，而不是猜成 AV。
        var libraryId = LibraryIdForItem(item);
        SubtitleSearchMode? explicitMode = libraryId is not null
            && Plugin.Current.TryResolveSearchMode(libraryId.Value, out var mode)
            ? mode
            : null;

        return MediaKindResolver.Resolve(
            explicitMode,
            item?.OriginalLanguage,
            item?.ProductionLocations);
    }

    /// <inheritdoc />
    public MediaKind MediaKindForPath(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return MediaKind.Foreign;
        }

        var item = _libraryManager.FindByPath(mediaPath, isFolder: false);
        if (item is not null)
        {
            return MediaKindForItem(item);
        }

        // 条目还没入库时按路径前缀找所属库，同样要求该库显式配置过检索方式。
        var config = Plugin.Current;
        foreach (var libraryId in config.ResolveLibraryIds())
        {
            var library = _libraryManager.GetItemById(libraryId);
            if (library?.Path is { Length: > 0 } root
                && mediaPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && config.TryResolveSearchMode(libraryId, out var mode))
            {
                return MediaKindResolver.Resolve(mode, null, null);
            }
        }

        return MediaKind.Foreign;
    }

    /// <inheritdoc />
    public MediaBrief BriefForItem(BaseItem? item) => MediaBriefFactory.FromItem(item);

    /// <inheritdoc />
    public MediaBrief BriefForPath(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return MediaBrief.Empty;
        }

        return MediaBriefFactory.FromItem(_libraryManager.FindByPath(mediaPath, isFolder: false));
    }

    /// <inheritdoc />
    public Guid? ItemIdForPath(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return null;
        }

        var item = _libraryManager.FindByPath(mediaPath, isFolder: false);
        return item?.Id;
    }

    private SubtitleSearchMode ModeForPathCore(string? mediaPath, BaseItem? item)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return SubtitleSearchMode.Title;
        }

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

        // 同样不能默认番号：路径对不上任何已配置的库，说明插件本来就不管它。
        return SubtitleSearchMode.Title;
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
