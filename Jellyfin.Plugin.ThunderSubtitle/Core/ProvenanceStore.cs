using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>What the plugin knows about a subtitle it wrote.</summary>
/// <param name="ItemId">Jellyfin item id.</param>
/// <param name="Code">The release code the subtitle was searched with.</param>
/// <param name="File">Full path of the sidecar.</param>
/// <param name="CandidateName">The name the API reported.</param>
/// <param name="Bytes">Size written.</param>
/// <param name="AiMarkers">Machine-translation markers in the candidate name.</param>
/// <param name="WrittenAt">When it was written (UTC).</param>
/// <param name="MediaKind">写入时判定的影片类型，自动择优要按同一套规则比较。</param>
/// <param name="Language">语言形式，例如 <c>SimplifiedChinese</c>。</param>
/// <param name="AiScore">这份文件的 AI 分数，评审过才有。</param>
/// <param name="AiSource">human / ai / asr / mixed。</param>
/// <param name="TypoCount">中文影视的错别字数量。</param>
/// <param name="PromptVersion">评分时使用的提示词版本，版本变了旧分数就不能比。</param>
public sealed record SubtitleProvenance(
    string ItemId,
    string Code,
    string File,
    string CandidateName,
    long Bytes,
    int AiMarkers,
    DateTimeOffset WrittenAt,
    string MediaKind = "",
    string Language = "",
    double? AiScore = null,
    string AiSource = "",
    int TypoCount = 0,
    int PromptVersion = 0);

/// <summary>
/// Small JSON ledger of the subtitles this plugin wrote. It lets the re-pick task tell
/// "my own 13 KB machine translation" from "a 63 KB human subtitle someone else put
/// there", which size alone cannot.
/// </summary>
public sealed class ProvenanceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, SubtitleProvenance>? _entries;

    /// <summary>Initializes a new instance of the <see cref="ProvenanceStore"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin application paths; the ledger lives under the data folder.</param>
    public ProvenanceStore(IApplicationPaths applicationPaths)
        : this(System.IO.Path.Combine(applicationPaths.DataPath, "thunder-subtitle", "provenance.json"))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProvenanceStore"/> class.</summary>
    /// <param name="path">Full path of the ledger file.</param>
    public ProvenanceStore(string path)
    {
        _path = path;
    }

    /// <summary>Gets the ledger file path.</summary>
    public string StorePath => _path;

    /// <summary>Looks up the record for an item.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <returns>The record, or <c>null</c>.</returns>
    public SubtitleProvenance? Get(string itemId)
    {
        lock (_gate)
        {
            Load();
            return _entries!.TryGetValue(itemId, out var entry) ? entry : null;
        }
    }

    /// <summary>Records what was written for an item.</summary>
    /// <param name="entry">The record.</param>
    public void Set(SubtitleProvenance entry)
    {
        lock (_gate)
        {
            Load();
            _entries![entry.ItemId] = entry;
            Save();
        }
    }

    /// <summary>Forgets an item.</summary>
    /// <param name="itemId">Jellyfin item id.</param>
    public void Remove(string itemId)
    {
        lock (_gate)
        {
            Load();
            if (_entries!.Remove(itemId))
            {
                Save();
            }
        }
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
                var stored = JsonSerializer.Deserialize<Dictionary<string, SubtitleProvenance>>(json);
                if (stored is not null)
                {
                    _entries = new Dictionary<string, SubtitleProvenance>(stored, StringComparer.Ordinal);
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

        _entries = new Dictionary<string, SubtitleProvenance>(StringComparer.Ordinal);
    }

    private void Save()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
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
}
