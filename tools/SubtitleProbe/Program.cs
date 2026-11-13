using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ThunderSubtitle.Core;

// Offline sanity check for the parts of the plugin that talk to the Xunlei API:
//   dotnet run --project tools/SubtitleProbe -- ABF-363 MIUM-1457 FSDSS-272
//   dotnet run --project tools/SubtitleProbe -- --kind Chinese "流浪地球 2019"
// It prints the same ranking the scheduled task uses and writes the winner to a temp
// folder, so decoding and format sniffing can be verified without a Jellyfin server.
//
// --kind 决定语言档位：JapaneseAv（默认）/ Foreign / Chinese。
//
// Measuring local files:
//   dotnet run --project tools/SubtitleProbe -- --measure a.srt b.ass
if (args.Length >= 2 && args[0] == "--measure")
{
    foreach (var path in args.Skip(1))
    {
        var text = File.ReadAllText(path);
        var extension = Path.GetExtension(path);
        var stats = SubtitleContent.Measure(text, extension);
        var cues = SubtitleContent.Parse(text, extension);
        var language = SubtitleLanguageDetector.Detect(cues, SubtitleContent.StripMarkup(text));

        Console.WriteLine($"{Path.GetFileName(path)}  {new FileInfo(path).Length} B  ext={extension}");
        Console.WriteLine($"  language={language} ({SubtitleLanguageDetector.DisplayName(language)})  cues={stats.CueCount}  contentChars={stats.ContentChars}  charsPerCue={(stats.CueCount == 0 ? 0 : (double)stats.ContentChars / stats.CueCount):F1}");
        foreach (var cue in cues.Take(3))
        {
            Console.WriteLine($"  first: [{cue.Start:hh\\:mm\\:ss}] {cue.Text}");
        }

        if (cues.Count > 0)
        {
            var longest = cues.OrderByDescending(c => c.Text.Length).First();
            Console.WriteLine($"  longest cue ({longest.Text.Length} chars): {longest.Text[..Math.Min(80, longest.Text.Length)]}");
        }
    }

    return 0;
}

// Ranking a folder of candidates offline, without touching Jellyfin or the search API:
//   dotnet run --project tools/SubtitleProbe -- --rank /tmp/pirates --kind Foreign
// Each *.json in the folder is { "name": ..., "extension": ..., "text": ... } — exactly what
// GET /ThunderSubtitle/Text returns. This runs the same drop → rank → shortlist chain the
// scheduled task runs, so the effect of a ranking change can be checked against real
// candidates before deploying.
if (args.Length >= 2 && args[0] == "--rank")
{
    var rankKind = MediaKind.Foreign;
    for (var i = 2; i < args.Length - 1; i++)
    {
        if (args[i] == "--kind")
        {
            rankKind = Enum.Parse<MediaKind>(args[i + 1], ignoreCase: true);
            i++;
        }
    }

    var files = Directory.GetFiles(args[1], "*.json")
        .OrderBy(path => int.TryParse(Path.GetFileNameWithoutExtension(path), out var order) ? order : int.MaxValue)
        .ToList();

    var loaded = new List<(string Label, SubtitlePick Pick)>();
    foreach (var path in files)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var label = Path.GetFileNameWithoutExtension(path);
        var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? label : label;
        var extension = root.TryGetProperty("extension", out var extElement) ? extElement.GetString() ?? "srt" : "srt";
        var text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;

        loaded.Add((label, new SubtitlePick(name, "." + extension.TrimStart('.'), text, "json")));
    }

    Console.WriteLine($"读入 {loaded.Count} 份候选，类型 {rankKind}");
    Console.WriteLine();

    var labelOf = new Dictionary<SubtitlePick, string>(ReferenceEqualityComparer.Instance);
    foreach (var (label, pick) in loaded)
    {
        labelOf[pick] = label;
    }

    var health = SubtitleRanker.DropUnusable(loaded.Select(entry => entry.Pick).ToList());
    var dropped = new HashSet<SubtitlePick>(health.Dropped.Select(entry => entry.Pick), ReferenceEqualityComparer.Instance);

    Console.WriteLine("健康度扫描：");
    foreach (var (label, pick) in loaded)
    {
        var verdict = dropped.Contains(pick) ? "淘汰" : "保留";
        var reason = pick.Health.DefectReason.Length > 0 ? $"，{pick.Health.DefectReason}" : string.Empty;
        Console.WriteLine(
            $"  #{label,-3} {verdict}  {SubtitleLanguageDetector.DisplayName(pick.Language),-8}"
            + $" {pick.CueCount,5} 条  含中文 {pick.Health.ChineseRatio:P0}"
            + $"  半角标点 {pick.Health.HalfWidthPunctuation}"
            + $"  缺陷 {pick.Health.Defects}{reason}");
    }

    Console.WriteLine();

    IReadOnlyList<SubtitlePick> ranked = SubtitleRanker.Rank(health.Kept, rankKind);

    // 真实管线在排序之后还有一步近似去重：同一份字幕被不同站点转码、改名后
    // 内容哈希不同，精确去重拦不住，不合并的话它们会一起占掉送审名额。
    // 用默认阈值 0.95 跑一遍，保证 probe 看到的名额和线上一致。
    var collapse = NearDuplicate.Collapse(ranked, 0.95);
    if (collapse.Merged > 0)
    {
        Console.WriteLine($"近似去重：{ranked.Count} 份合并为 {collapse.Kept.Count} 份（{collapse.Merged} 份与已有候选相似度 ≥ 95%）。");
        Console.WriteLine();
        ranked = collapse.Kept;
    }

    var median = SubtitleRanker.MedianCueCount(ranked);

    Console.WriteLine($"条数中位数 {median:F0}，排序结果（共 {ranked.Count} 份）：");
    for (var i = 0; i < ranked.Count; i++)
    {
        var pick = ranked[i];
        Console.WriteLine(
            $"  {i + 1,2}. #{labelOf[pick],-3} 偏离档 {SubtitleRanker.DeviationBucket(pick, median),-2}"
            + $" 关键词 {SubtitleRanker.CountQualityKeywords(pick.CandidateName)}"
            + $"  {pick.CandidateName}");
    }

    Console.WriteLine();

    var shortlist = ShortlistSelector.Select(ranked, rankKind, 5);
    Console.WriteLine($"送审名单（{shortlist.Count} 份）：");
    foreach (var pick in shortlist)
    {
        Console.WriteLine(
            $"  Tier{LanguagePolicy.TierOf(rankKind, pick.Language)}"
            + $"（{LanguagePolicy.TierName(rankKind, LanguagePolicy.TierOf(rankKind, pick.Language))}）"
            + $" #{labelOf[pick]}  {pick.CandidateName}");
    }

    return 0;
}

// --kind 可选，默认 JapaneseAv：probe 的典型用途是验证番号库。
// --dump 会把每份下载成功的候选原始字节保存到输出目录，便于分析解析失败的文件。
// --prompt 会把「真正要发给 AI 的 prompt」写进输出目录，可以脱机核对抽样与措辞。
var kind = MediaKind.JapaneseAv;
var dumpAll = false;
var dumpPrompt = false;
var codeArgs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--kind" && i + 1 < args.Length)
    {
        kind = Enum.Parse<MediaKind>(args[i + 1], ignoreCase: true);
        i++;
        continue;
    }

    if (args[i] == "--dump")
    {
        dumpAll = true;
        continue;
    }

    if (args[i] == "--prompt")
    {
        dumpPrompt = true;
        continue;
    }

    codeArgs.Add(args[i]);
}

var codes = codeArgs.Count > 0 ? codeArgs.ToArray() : ["ABF-363", "MIUM-1457", "FSDSS-272"];
Console.WriteLine($"影片类型：{kind}（{MediaKindResolver.DisplayName(kind)}），首选档 {LanguagePolicy.TierName(kind, 0)}");
var outputDirectory = Path.Combine(Path.GetTempPath(), "thunder-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(outputDirectory);

var client = new ThunderClient();
var exitCode = 0;

foreach (var raw in codes)
{
    // 先判断是不是番号：是番号就走番号模式（补查前导零变体），否则走片名模式
    // （按片名 / 年份 / 季集证据收窄）。probe 之前一律按番号处理，
    // 片名模式下的证据分档和年份排除根本没被测到。
    var parsedCode = ReleaseCode.FromText(raw);
    var year = parsedCode is null ? ParseYear(raw) : null;

    // 片名模式的查询词由 SearchTarget 拼「片名 + 年份」，所以传进去的片名要先去掉年份，
    // 否则会变成「Inception 2010 2010」。
    var title = year is int y
        ? System.Text.RegularExpressions.Regex.Replace(raw, $@"(?<!\d){y}(?!\d)", string.Empty).Trim()
        : raw;

    var target = parsedCode is not null
        ? SearchTarget.ForCode(parsedCode)
        : SearchTarget.ForTitle(title, year);

    if (target is null)
    {
        Console.WriteLine($"=== {raw} -> 无法构造检索目标");
        continue;
    }

    Console.WriteLine($"=== {raw} -> {target.Mode}「{target.Query}」"
                      + (target.Year is int targetYear ? $"（年份 {targetYear}）" : string.Empty));

    IReadOnlyList<ThunderCandidate> candidates;
    try
    {
        var search = await client.SearchAsync(target.Query, CancellationToken.None);
        candidates = search.Candidates;
        if (search.Attempts > 1)
        {
            Console.WriteLine($"    前 {search.Attempts - 1} 次为空，第 {search.Attempts} 次返回 {search.Candidates.Count} 条");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    查询失败：{ex.Message}");
        exitCode = 1;
        continue;
    }

    Console.WriteLine($"    候选 {candidates.Count} 条");

    // 番号模式补查前导零变体；片名模式在证据全不匹配时退化为纯片名再查一次。
    if (target.Mode == SubtitleSearchMode.Code)
    {
        var alternative = ReleaseCode.AlternativeSpelling(target.Code!);
        if (alternative is not null && !candidates.Any(c => SubtitleRanker.HasCode(c.Name, target.Code!)))
        {
            Console.WriteLine($"    没有番号匹配的候选，改用 {alternative} 补查");
            var secondary = (await client.SearchAsync(alternative, CancellationToken.None)).Candidates;
            candidates = candidates.Concat(secondary)
                .GroupBy(c => c.Uri.AbsoluteUri, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            Console.WriteLine($"    合并后候选 {candidates.Count} 条");
        }
    }
    else if (candidates.Count > 0
             && candidates.All(c => TitleMatch.Level(c.Name, target) == TitleMatchLevel.None)
             && !string.Equals(target.Query, target.Title, StringComparison.Ordinal))
    {
        Console.WriteLine($"    没有片名/年份匹配的候选，改用「{target.Title}」补查");
        var secondary = (await client.SearchAsync(target.Title!, CancellationToken.None)).Candidates;
        candidates = candidates.Concat(secondary)
            .GroupBy(c => c.Uri.AbsoluteUri, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        Console.WriteLine($"    合并后候选 {candidates.Count} 条");
    }

    var picks = new List<SubtitlePick>();
    foreach (var candidate in candidates)
    {
        if (!SubtitleText.AcceptedExtensions.Contains("." + candidate.Extension, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"    - 跳过（格式 {candidate.Extension}）：{candidate.Name}");
            continue;
        }

        try
        {
            var bytes = await client.DownloadAsync(candidate.Uri, CancellationToken.None);
            var decoded = SubtitleText.TryDecode(bytes);
            if (decoded is null)
            {
                Console.WriteLine($"    - 丢弃（无法解码 {bytes.Length} B）：{candidate.Name}");
                continue;
            }

            var extension = SubtitleText.DetectExtension(decoded.Text, candidate.Extension);
            if (extension is null)
            {
                Console.WriteLine($"    - 丢弃（内容不是字幕，{bytes.Length} B，{decoded.EncodingName}）：{candidate.Name}");
                continue;
            }

            if (dumpAll)
            {
                // 用原始字节保存：分析解析失败时，转码后的文本可能掩盖真正的问题。
                var safeName = string.Concat(candidate.Name.Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    safeName = Guid.NewGuid().ToString("N");
                }

                await File.WriteAllBytesAsync(Path.Combine(outputDirectory, safeName + "_raw" + extension), bytes);
            }

            var pick = new SubtitlePick(candidate.Name, extension, decoded.Text, decoded.EncodingName);
            picks.Add(pick);
            Console.WriteLine($"    + {candidate.Name,-52} {bytes.Length,7} B  {decoded.EncodingName,-10} {extension}  "
                              + $"{SubtitleLanguageDetector.DisplayName(pick.Language)} 条{pick.CueCount} 字{pick.ContentChars} 标记{pick.AiMarkers}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    - 下载失败：{candidate.Name} - {ex.Message}");
        }
    }

    var acceptable = SubtitleRanker.KeepAcceptable(kind, picks);
    if (acceptable.Dropped.Count > 0)
    {
        Console.WriteLine($"    语言过滤：丢弃 " + string.Join(" / ", acceptable.Dropped.Select(pair => $"{SubtitleLanguageDetector.DisplayName(pair.Key)} {pair.Value}")));
    }

    var ranked = target.Mode == SubtitleSearchMode.Code
        ? SubtitleRanker.Rank(acceptable.Kept, kind, target.Code)
        : SubtitleRanker.RankForTitle(acceptable.Kept, target, kind);

    var best = ranked.FirstOrDefault();
    if (best is null)
    {
        Console.WriteLine($"    → 没有可用字幕（{picks.Count} 条候选全部被过滤）");
        Console.WriteLine();
        continue;
    }

    Console.WriteLine($"    → 去重后 {ranked.Count} 条，按档位排序：");
    foreach (var pick in ranked.Take(10))
    {
        var tier = LanguagePolicy.TierOf(kind, pick.Language);
        var marker = ReferenceEquals(pick, best) ? " ←最优" : string.Empty;
        Console.WriteLine($"      [Tier{tier} {LanguagePolicy.TierName(kind, tier)}] {pick.CandidateName,-48} "
                          + $"{SubtitleLanguageDetector.DisplayName(pick.Language)} {pick.CueCount} 条 {pick.ContentChars} 字 标记{pick.AiMarkers}{marker}");
    }
    var outputPath = Path.Combine(outputDirectory, $"{target.Label}.chi{best.Extension}");
    await File.WriteAllBytesAsync(outputPath, new UTF8Encoding(false).GetBytes(SubtitleText.Normalize(best.Text) + "\n"));

    Console.WriteLine($"    → 去重后 {ranked.Count} 条，最优：{best.CandidateName}");
    Console.WriteLine($"      {best.Bytes} B  {best.Extension}  {best.EncodingName}  AI标记 {best.AiMarkers}  -> {outputPath}");

    if (dumpPrompt)
    {
        DumpPrompt(kind, ranked, outputDirectory, target.Label);
    }

    Console.WriteLine();
}

Console.WriteLine($"输出目录：{outputDirectory}");
return exitCode;

/// <summary>从「流浪地球 2019」这类参数里取出 4 位年份，供片名模式使用。</summary>
static int? ParseYear(string text)
{
    var match = System.Text.RegularExpressions.Regex.Match(text, @"(?<!\d)(19|20)\d{2}(?!\d)");
    return match.Success ? int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture) : null;
}

/// <summary>
/// 走真实管线生成 AI 送审 prompt 并落盘（不调用 API）。
///
/// 用 <see cref="ShortlistSelector"/> 和 <see cref="AiPromptBuilder.Compose"/> 而不是
/// probe 自己拼一份简化版：差异就在这里 —— 简化版永远比真实版好看。
/// </summary>
static void DumpPrompt(MediaKind kind, IReadOnlyList<SubtitlePick> ranked, string outputDirectory, string label)
{
    var shortlist = ShortlistSelector.Select(ranked, kind, perTier: 5);
    if (shortlist.Count < 2)
    {
        Console.WriteLine($"    [AI] 短名单只有 {shortlist.Count} 份，实际不会发起评审");
        return;
    }

    // 手写默认值而不是读 PluginConfiguration：probe 不编译那一半，
    // 数值和 PluginConfiguration 的默认值保持一致（改配置默认值时要同步这里）。
    var settings = new AiSettings(
        BaseUrl: string.Empty,
        ApiKey: string.Empty,
        Model: "probe",
        SegmentCount: 6,
        CuesPerSegment: 8,
        MaxCharsPerCandidate: 3000,
        TimeoutSeconds: 30,
        CjkCuesPerSegment: 10,
        CjkMaxChars: 6000,
        MaxPromptChars: 24000);

    var composed = AiPromptBuilder.Compose(kind, null, shortlist, settings, null, TimeSpan.FromSeconds(60));
    if (composed is null)
    {
        Console.WriteLine("    [AI] 抽不出可用片段（候选没有可解析的字幕结构）");
        return;
    }

    Console.WriteLine($"    [AI] 送审 {shortlist.Count} 份，prompt {composed.UserPrompt.Length} 字符 / {composed.Segments.Count} 段"
                      + $"（上限 {settings.MaxPromptChars}）");

    foreach (var segment in composed.Segments)
    {
        var landing = string.Join(
            "  ",
            segment.Candidates.Select(candidate => candidate.Lines.Count == 0
                ? $"#{candidate.PickIndex}—"
                : $"#{candidate.PickIndex}{(candidate.OffsetSeconds >= 0 ? "+" : string.Empty)}{candidate.OffsetSeconds:F0}s"));
        Console.WriteLine($"      片段 {segment.Index} 锚点 {segment.Anchor:hh\\:mm\\:ss} → {landing}");
    }

    var path = Path.Combine(outputDirectory, SafeFileName(label) + "-prompt.txt");
    File.WriteAllText(
        path,
        "# SYSTEM\n" + AiPrompts.System(kind) + "\n\n# USER\n" + composed.UserPrompt,
        new UTF8Encoding(false));
    Console.WriteLine($"      prompt -> {path}");
}

/// <summary>把任意字符串转成可用的文件名（候选名里常带 <c>/</c> 和引号）。</summary>
static string SafeFileName(string value)
{
    var safe = string.Concat(value.Split(Path.GetInvalidFileNameChars())).Trim();
    return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") : safe;
}
