using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// AI 评分的磁盘缓存。
///
/// 为什么需要：「自动补全」和「自动择优」是两次独立任务，它们会搜到同一批候选、
/// 发两次一模一样的请求。一次评审 6 份候选约 7000 token，重复计费没有意义。
///
/// 缓存键包含模型名和提示词版本：不同模型的分数不可比，改了提示词之后旧分数也不可再用。
/// </summary>
public sealed class AiScoreCache
{
    /// <summary>最多保留多少条评分。一条约 200 字节，2000 条约 400 KB。</summary>
    internal const int MaxEntries = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;

    /// <summary>Initializes a new instance of the <see cref="AiScoreCache"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin application paths; the cache lives under the data folder.</param>
    public AiScoreCache(IApplicationPaths applicationPaths)
        : this(Path.Combine(applicationPaths.DataPath, "thunder-subtitle", "ai-scores.json"))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AiScoreCache"/> class.</summary>
    /// <param name="path">Full path of the cache file.</param>
    public AiScoreCache(string path)
        : this(path, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AiScoreCache"/> class with a controllable clock.</summary>
    /// <param name="path">Full path of the cache file.</param>
    /// <param name="now">Clock, for tests.</param>
    internal AiScoreCache(string path, Func<DateTimeOffset> now)
    {
        _path = path;
        _now = now;
    }

    /// <summary>Gets the cache file path.</summary>
    public string StorePath => _path;

    /// <summary>
    /// 生成缓存键。
    ///
    /// 内容哈希决定「是不是同一份字幕」，影片类型决定「用哪套标准评的」，
    /// 提示词版本决定「标准有没有变」，附加提示词决定「这次的尺度有没有变」。
    /// 四者任一不同都不能复用。
    /// </summary>
    /// <param name="pick">The candidate.</param>
    /// <param name="kind">The media kind.</param>
    /// <param name="extraInstruction">用户附加提示词，没有就传 <c>null</c>。</param>
    /// <param name="model">The model name.</param>
    /// <returns>A stable key.</returns>
    internal static string Key(SubtitlePick pick, MediaKind kind, string? extraInstruction, string model)
    {
        var material = string.Join(
            '\u0001',
            pick.Hash,
            kind.ToString(),
            AiPrompts.Version.ToString(CultureInfo.InvariantCulture),
            HintHash(extraInstruction),
            model.Trim().ToLowerInvariant());

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>查一条评分。过期或不存在时返回 <c>null</c>。</summary>
    /// <param name="key">缓存键。</param>
    /// <param name="maxAgeDays">有效期（天）。</param>
    /// <returns>The verdict with <see cref="AiVerdict.Id"/> set to 0.</returns>
    public AiVerdict? Get(string key, int maxAgeDays)
    {
        lock (_gate)
        {
            Load();
            if (!_entries!.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (entry.At < _now().AddDays(-Math.Max(1, maxAgeDays)))
            {
                _entries.Remove(key);
                return null;
            }

            // Id 是「本次送审的第几份」，由调用方重建。
            return entry.Verdict with { Id = 0 };
        }
    }

    /// <summary>保存一条评分。</summary>
    /// <param name="key">缓存键。</param>
    /// <param name="verdict">The verdict.</param>
    public void Set(string key, AiVerdict verdict)
    {
        lock (_gate)
        {
            Load();

            if (_entries!.Count >= MaxEntries)
            {
                // 超容量时先砍最旧的 10%：一次砍一条会在每次写入时都触发排序。
                var victims = _entries
                    .OrderBy(pair => pair.Value.At)
                    .Take(Math.Max(1, MaxEntries / 10))
                    .Select(pair => pair.Key)
                    .ToList();

                foreach (var victim in victims)
                {
                    _entries.Remove(victim);
                }
            }

            _entries[key] = new Entry(verdict, _now());
            Save();
        }
    }

    /// <summary>Gets how many entries are held. Exposed for tests.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                Load();
                return _entries!.Count;
            }
        }
    }

    private static string HintHash(string? extraInstruction)
    {
        if (string.IsNullOrWhiteSpace(extraInstruction))
        {
            return string.Empty;
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(extraInstruction.Trim())))[..16];
    }

    private void Load()
    {
        if (_entries is not null)
        {
            return;
        }

        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var stored = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json, JsonOptions);
                if (stored is not null)
                {
                    _entries = new Dictionary<string, Entry>(stored, StringComparer.Ordinal);
                    return;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_entries, JsonOptions);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Entry(AiVerdict Verdict, DateTimeOffset At);
}
