using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>A subtitle that was downloaded only to be read on screen.</summary>
/// <param name="Name">The candidate name the API reported.</param>
/// <param name="Extension">The extension matching the content, without the dot.</param>
/// <param name="EncodingName">The encoding it was decoded from.</param>
/// <param name="Text">The subtitle text.</param>
/// <param name="CueCount">Number of dialogue lines.</param>
/// <param name="ContentChars">Dialogue character count.</param>
/// <param name="Language">Detected language.</param>
public sealed record PreviewText(
    string Name,
    string Extension,
    string EncodingName,
    string Text,
    int CueCount,
    int ContentChars,
    SubtitleLanguage Language)
{
    /// <summary>Gets the size of the file as written (UTF-8, no BOM).</summary>
    public int Bytes => (int)SubtitleRanker.Utf8Length(Text);
}

/// <summary>
/// Holds the text of recently previewed candidates, keyed by subtitle id.
/// <para>
/// Previews must never reach the media folder, and re-downloading the same file every time the
/// user clicks the eye icon would hammer the CDN, so the decoded text is parked here instead.
/// It lives in memory only: nothing is written to disk, entries expire, and the oldest are
/// dropped once the cap is reached.
/// </para>
/// </summary>
public sealed class PreviewTextCache
{
    /// <summary>How long an untouched preview stays available.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _capacity;

    /// <summary>Initializes a new instance of the <see cref="PreviewTextCache"/> class.</summary>
    public PreviewTextCache()
        : this(() => DateTimeOffset.UtcNow, 24)
    {
    }

    internal PreviewTextCache(Func<DateTimeOffset> clock, int capacity = 24)
    {
        _clock = clock;
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>Gets how many previews are held right now.</summary>
    public int Count => _entries.Count;

    /// <summary>Reads a cached preview.</summary>
    /// <param name="key">The normalized subtitle id.</param>
    /// <returns>The cached preview, or <c>null</c> when it is missing or expired.</returns>
    public PreviewText? Get(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (_clock() - entry.CreatedAt > Lifetime)
        {
            _entries.TryRemove(key, out _);
            return null;
        }

        return entry.Text;
    }

    /// <summary>Stores a preview, dropping the oldest entries once the cap is reached.</summary>
    /// <param name="key">The normalized subtitle id.</param>
    /// <param name="text">The decoded subtitle.</param>
    public void Put(string key, PreviewText text)
    {
        Prune();
        _entries[key] = new Entry(text, _clock());

        if (_entries.Count <= _capacity)
        {
            return;
        }

        foreach (var entry in _entries.OrderBy(pair => pair.Value.CreatedAt).ToList())
        {
            if (_entries.Count <= _capacity)
            {
                break;
            }

            _entries.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>Drops everything, e.g. from the plugin configuration page.</summary>
    /// <returns>How many entries were dropped.</returns>
    public int Clear()
    {
        var count = _entries.Count;
        _entries.Clear();
        return count;
    }

    private void Prune()
    {
        var now = _clock();
        foreach (var entry in _entries)
        {
            if (now - entry.Value.CreatedAt > Lifetime)
            {
                _entries.TryRemove(entry.Key, out _);
            }
        }
    }

    private sealed record Entry(PreviewText Text, DateTimeOffset CreatedAt);
}
