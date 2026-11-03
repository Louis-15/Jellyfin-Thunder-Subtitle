using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 实时入库监听：发现「刚入库且已刮削完成」的影片就立刻补一次字幕。
///
/// 为什么做成插件内的后台任务，而不是外部脚本 + 定时器：
/// 1. 触发点在 Jellyfin 进程内，不存在容器内外路径 / 权限差异；
/// 2. 插件停用即停止，不会留下孤儿进程或残留 timer；
/// 3. 能直接读媒体库索引，不需要为了一次判断去跑 REST。
///
/// 为什么用轮询而不是监听库事件：
/// Jellyfin 的库事件在不同版本上语义并不完全一致（新增 vs 更新、刮削前后各触发几次），
/// 而「最近几小时有没有新条目」这个判断用轮询做只有几十毫秒成本，却稳定得多。
///
/// 为什么必须等刮削完成（ProviderIds 非空）才动手：
/// 文件刚落地时文件名可能还没被识别、番号也取不到，
/// 这时候去找字幕必然一无所获，还会把条目记进「已处理」，白白漏掉。
/// </summary>
public sealed class ItemWatchService : IHostedService, IDisposable
{
    /// <summary>两次检查之间的间隔。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>只关心最近这段时间内入库的条目。</summary>
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(6);

    /// <summary>
    /// 启动后先等一会儿，避开 Jellyfin 开机时的库扫描高峰。
    /// 比 AV Collection 的同类服务晚 30 秒，避免两个插件同时抢 IO。
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(75);

    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleJob _job;
    private readonly PluginLog _log;

    /// <summary>
    /// 已经处理过的条目（值 = 处理时刻，用来做过期清理）。
    /// 只在监听线程里访问，不需要加锁。
    /// </summary>
    private readonly Dictionary<Guid, DateTime> _handled = [];

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Initializes a new instance of the <see cref="ItemWatchService"/> class.</summary>
    /// <param name="libraryManager">Jellyfin 媒体库服务。</param>
    /// <param name="job">字幕任务（提供单条目处理）。</param>
    /// <param name="logger">日志。</param>
    public ItemWatchService(ILibraryManager libraryManager, SubtitleJob job, ILogger<ItemWatchService> logger)
    {
        _libraryManager = libraryManager;
        _job = job;
        _log = new PluginLog(logger);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停机。
            }
        }

        _cts.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(StartupDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 一次失败不能让监听循环死掉：下一轮还会再来。
                _log.Error("实时入库检查失败：" + ex.Message);
            }

            try
            {
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Current;
        config.Normalize();

        // 总开关或实时开关关掉时什么都不做。
        if (!config.Enabled || !config.RealtimeFill)
        {
            return;
        }

        var libraries = config.ResolveLibraryIds();
        if (libraries.Count == 0)
        {
            return;
        }

        var since = DateTime.UtcNow - Lookback;

        // 过期清理：只保留窗口内的记录，否则字典会随运行时间无限增长。
        if (_handled.Count > 0)
        {
            foreach (var stale in _handled.Where(kv => kv.Value < since).Select(kv => kv.Key).ToList())
            {
                _handled.Remove(stale);
            }
        }

        var pending = new List<Guid>();

        foreach (var libraryId in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = libraryId,
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Movie],

                // 合集里的影片也要能被看到，否则进了合集的片子会从查询里消失。
                CollapseBoxSetItems = false,
                IsVirtualItem = false,

                // 只取「入库时间在窗口内」的条目：数据库直接按 DateCreated 过滤，
                // 不会把整个库的影片都拉回来。
                MinDateCreated = since
            });

            foreach (var item in items)
            {
                if (_handled.ContainsKey(item.Id))
                {
                    continue;
                }

                // ProviderIds 为空 = 还没刮削完（或根本没识别出来），下一轮再看。
                if (item.ProviderIds is not { Count: > 0 })
                {
                    continue;
                }

                pending.Add(item.Id);
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        _log.Info($"实时入库：{pending.Count} 部新片，交给字幕任务。");

        // 只有真正跑过才记为已处理：任务被锁挡住时要在下一轮重试。
        var done = await _job.ProcessItemsAsync(pending, cancellationToken).ConfigureAwait(false);
        if (!done)
        {
            _log.Warn("实时补字幕本次未执行（字幕任务占用中或插件已停用），下一轮重试。");
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var id in pending)
        {
            _handled[id] = now;
        }
    }
}
