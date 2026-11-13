using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 手动搜索时用户填的「本次额外要求」暂存。
///
/// 为什么要绕服务器一圈：AI 评分发生在 <c>ThunderSubtitleProvider.Search</c> 里，
/// 而浏览器没法把这个字符串直接交给它 —— Jellyfin 的搜索接口没有自定义参数。
/// 所以前端先 POST 到这里，Search 再按条目 Id 取回来。
///
/// 存在内存里、10 分钟过期：这是一次性输入，重启后不该留下任何痕迹。
/// </summary>
public sealed class HintStore
{
    /// <summary>提示词长度上限。再长会挤掉真正重要的台词抽样。</summary>
    public const int MaxChars = 500;

    /// <summary>最多同时保留多少条。手动搜索是低频操作，32 条足够覆盖多个用户的并发操作。</summary>
    private const int Capacity = 32;

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Initializes a new instance of the <see cref="HintStore"/> class.</summary>
    public HintStore()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HintStore"/> class with a controllable clock.</summary>
    /// <param name="now">Clock, for tests.</param>
    internal HintStore(Func<DateTimeOffset> now)
    {
        _now = now;
    }

    /// <summary>Gets how many hints are currently held. Exposed for tests.</summary>
    internal int Count => _entries.Count;

    /// <summary>保存一条提示词。空内容等同于清除。</summary>
    /// <param name="itemId">条目 Id。</param>
    /// <param name="hint">用户输入。</param>
    public void Set(Guid itemId, string? hint)
    {
        Set(Key(itemId), hint);
    }

    /// <summary>保存一条提示词。空内容等同于清除。</summary>
    /// <param name="key">条目 Id 的字符串形式。</param>
    /// <param name="hint">用户输入。</param>
    public void Set(string? key, string? hint)
    {
        var normalizedKey = NormalizeKey(key);
        if (normalizedKey is null)
        {
            return;
        }

        var text = Normalize(hint);
        if (text.Length == 0)
        {
            _entries.TryRemove(normalizedKey, out _);
            return;
        }

        Prune();
        _entries[normalizedKey] = new Entry(text, _now() + Lifetime);
    }

    /// <summary>
    /// 取一条提示词。
    ///
    /// 读完不删除：用户可能连点几次搜索，或者先搜一次看看再改输入再来一次。
    /// 靠 TTL 而不是靠「一次性消费」来清理。
    /// </summary>
    /// <param name="itemId">条目 Id。</param>
    /// <returns>The hint, or <c>null</c> when there is none or it expired.</returns>
    public string? Take(Guid itemId)
    {
        return Take(Key(itemId));
    }

    /// <summary>取一条提示词。</summary>
    /// <param name="key">条目 Id 的字符串形式。</param>
    /// <returns>The hint, or <c>null</c> when there is none or it expired.</returns>
    public string? Take(string? key)
    {
        var normalizedKey = NormalizeKey(key);
        if (normalizedKey is null || !_entries.TryGetValue(normalizedKey, out var entry))
        {
            return null;
        }

        if (entry.ExpiresAt <= _now())
        {
            _entries.TryRemove(normalizedKey, out _);
            return null;
        }

        return entry.Text;
    }

    /// <summary>
    /// 把任意 Guid 写法归一成 <c>N</c> 格式。
    /// 前端传的是带连字符的 <c>D</c> 格式，后端从 <see cref="Guid"/> 直接拿的是 <c>N</c> 格式，
    /// 不归一就会两边对不上，提示词静默失效。
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The normalized key.</returns>
    internal static string Key(Guid itemId) => itemId.ToString("N");

    /// <summary>
    /// 归一化外部传进来的键：能解析成 Guid 就统一成 <c>N</c> 格式，
    /// 否则原样（去首尾空白）使用 —— 这样将来换成非 Guid 的键也不会坏。
    /// </summary>
    private static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return Guid.TryParse(key, out var guid) ? guid.ToString("N") : key.Trim();
    }

    /// <summary>
    /// 清理过期项，并在超容量时丢掉最早过期的那些。
    ///
    /// 只在 <see cref="Set"/> 时调用：<see cref="Take"/> 已经能识别过期，
    /// 让它也做清理只会让读路径变慢（用户等待的正是这条路径）。
    /// </summary>
    private void Prune()
    {
        var now = _now();

        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }

        if (_entries.Count < Capacity)
        {
            return;
        }

        // 超容量：按过期时间排序，丢掉最快过期的，保证留下的都是「更近写的」。
        foreach (var pair in _entries.OrderBy(entry => entry.Value.ExpiresAt).Take(_entries.Count - Capacity + 1))
        {
            _entries.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>
    /// 归一化用户输入：去掉控制字符（用户可能粘贴带换行的内容），
    /// 折叠连续空白，再按 <see cref="MaxChars"/> 截断。
    /// </summary>
    private static string Normalize(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(hint.Length);
        var lastWasSpace = false;

        foreach (var ch in hint)
        {
            // 先判空白再判控制字符：换行和制表符既是空白也是控制字符，
            // 按空白折叠才不会把「1970年代\r\n黑帮片」粘成「1970年代黑帮片」。
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            if (char.IsControl(ch))
            {
                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        var text = builder.ToString().Trim();
        return text.Length <= MaxChars ? text : text[..MaxChars];
    }

    private sealed record Entry(string Text, DateTimeOffset ExpiresAt);
}
