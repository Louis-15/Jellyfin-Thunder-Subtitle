using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>One search kept in memory so the preview page can serve whole files.</summary>
public sealed class PreviewSession
{
    /// <summary>Initializes a new instance of the <see cref="PreviewSession"/> class.</summary>
    /// <param name="key">Opaque handle the page sends back.</param>
    /// <param name="code">The code that was searched.</param>
    /// <param name="itemId">The movie the search was started for, when known.</param>
    /// <param name="mediaPath">Path of that movie, when known.</param>
    /// <param name="outcome">What the pipeline produced.</param>
    /// <param name="createdAt">When the search finished.</param>
    public PreviewSession(
        string key,
        string code,
        Guid? itemId,
        string? mediaPath,
        FindOutcome outcome,
        DateTimeOffset createdAt)
    {
        Key = key;
        Code = code;
        ItemId = itemId;
        MediaPath = mediaPath;
        Outcome = outcome;
        CreatedAt = createdAt;
    }

    /// <summary>Gets the opaque handle.</summary>
    public string Key { get; }

    /// <summary>Gets the code that was searched.</summary>
    public string Code { get; }

    /// <summary>Gets the movie the search was started for, when known.</summary>
    public Guid? ItemId { get; }

    /// <summary>Gets the path of that movie, when known.</summary>
    public string? MediaPath { get; }

    /// <summary>Gets what the pipeline produced.</summary>
    public FindOutcome Outcome { get; }

    /// <summary>Gets when the search finished.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets one candidate by its 1-based position in the result list.</summary>
    /// <param name="index">1-based index.</param>
    /// <returns>The candidate, or <c>null</c> when the index is out of range.</returns>
    public SubtitlePick? Pick(int index)
    {
        return index >= 1 && index <= Outcome.Ranked.Count ? Outcome.Ranked[index - 1] : null;
    }
}

/// <summary>
/// Keeps the candidates of recent searches in process memory, so the preview page can show a
/// whole subtitle without writing anything next to the video. Nothing touches the disk, and the
/// entry for a movie is dropped the moment the user applies one of its candidates, so the cache
/// never accumulates files the user has already chosen from.
/// </summary>
public sealed class PreviewCache
{
    /// <summary>How long an untouched search stays available.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, PreviewSession> _sessions = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _maxSessions;

    /// <summary>Initializes a new instance of the <see cref="PreviewCache"/> class.</summary>
    public PreviewCache()
        : this(() => DateTimeOffset.UtcNow, 20)
    {
    }

    internal PreviewCache(Func<DateTimeOffset> clock, int maxSessions = 20)
    {
        _clock = clock;
        _maxSessions = Math.Max(1, maxSessions);
    }

    /// <summary>Gets how many searches are held right now.</summary>
    public int Count => _sessions.Count;

    /// <summary>Stores a finished search, replacing any older copy for the same movie.</summary>
    /// <param name="code">The code that was searched.</param>
    /// <param name="itemId">The movie, when known.</param>
    /// <param name="mediaPath">Path of that movie, when known.</param>
    /// <param name="outcome">What the pipeline produced.</param>
    /// <returns>The session, whose <see cref="PreviewSession.Key"/> the page sends back.</returns>
    public PreviewSession Add(string code, Guid? itemId, string? mediaPath, FindOutcome outcome)
    {
        Prune();

        var session = new PreviewSession(
            Guid.NewGuid().ToString("N"),
            code,
            itemId,
            mediaPath,
            outcome,
            _clock());

        RemoveMovie(session.Code, session.MediaPath);
        _sessions[session.Key] = session;
        TrimToLimit();
        return session;
    }

    /// <summary>Looks a session up, dropping it when it is past its lifetime.</summary>
    /// <param name="key">The handle the page sent back.</param>
    /// <returns>The session, or <c>null</c> when it is gone.</returns>
    public PreviewSession? Get(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !_sessions.TryGetValue(key, out var session))
        {
            return null;
        }

        if (_clock() - session.CreatedAt > Lifetime)
        {
            _sessions.TryRemove(key, out _);
            return null;
        }

        return session;
    }

    /// <summary>
    /// Drops every search for one movie. Called right after a subtitle is written, which is
    /// what keeps the cache from growing with copies of a movie the user already picked from.
    /// </summary>
    /// <param name="code">The code of the movie.</param>
    /// <param name="mediaPath">Path of the movie, when known.</param>
    /// <returns>How many sessions were dropped.</returns>
    public int Purge(string? code, string? mediaPath)
    {
        var dropped = 0;
        foreach (var session in _sessions.Values)
        {
            if (Matches(session, code, mediaPath) && _sessions.TryRemove(session.Key, out _))
            {
                dropped++;
            }
        }

        return dropped;
    }

    /// <summary>Drops everything.</summary>
    /// <returns>How many sessions were dropped.</returns>
    public int Clear()
    {
        var count = _sessions.Count;
        _sessions.Clear();
        return count;
    }

    private void RemoveMovie(string? code, string? mediaPath)
    {
        Purge(code, mediaPath);
    }

    private static bool Matches(PreviewSession session, string? code, string? mediaPath)
    {
        if (!string.IsNullOrWhiteSpace(code)
            && string.Equals(session.Code, code, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(mediaPath)
               && string.Equals(session.MediaPath, mediaPath, StringComparison.Ordinal);
    }

    private void Prune()
    {
        var now = _clock();
        foreach (var session in _sessions.Values)
        {
            if (now - session.CreatedAt > Lifetime)
            {
                _sessions.TryRemove(session.Key, out _);
            }
        }
    }

    private void TrimToLimit()
    {
        if (_sessions.Count <= _maxSessions)
        {
            return;
        }

        foreach (var session in _sessions.Values.OrderBy(value => value.CreatedAt).ToList())
        {
            if (_sessions.Count <= _maxSessions)
            {
                break;
            }

            _sessions.TryRemove(session.Key, out _);
        }
    }
}
