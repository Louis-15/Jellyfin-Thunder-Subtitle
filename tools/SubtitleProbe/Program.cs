using System.Text;
using Jellyfin.Plugin.ThunderSubtitle.Core;

// Offline sanity check for the parts of the plugin that talk to the Xunlei API:
//   dotnet run --project tools/SubtitleProbe -- ABF-363 MIUM-1457 FSDSS-272
// It prints the same ranking the scheduled task uses and writes the winner to a temp
// folder, so decoding and format sniffing can be verified without a Jellyfin server.
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

        Console.WriteLine($"{Path.GetFileName(path)}  {new FileInfo(path).Length} B  ext={extension}");
        Console.WriteLine($"  language={SubtitleLanguageDetector.Detect(SubtitleContent.StripMarkup(text))}  cues={stats.CueCount}  contentChars={stats.ContentChars}  charsPerCue={(stats.CueCount == 0 ? 0 : (double)stats.ContentChars / stats.CueCount):F1}");
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

var codes = args.Length > 0 ? args : ["ABF-363", "MIUM-1457", "FSDSS-272"];
var outputDirectory = Path.Combine(Path.GetTempPath(), "thunder-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(outputDirectory);

var client = new ThunderClient();
var exitCode = 0;

foreach (var raw in codes)
{
    var code = JavCode.FromText(raw) ?? raw;
    Console.WriteLine($"=== {raw} -> {code}");

    IReadOnlyList<ThunderCandidate> candidates;
    try
    {
        var search = await client.SearchAsync(code, CancellationToken.None);
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

    var alternative = JavCode.AlternativeSpelling(code);
    if (alternative is not null && !candidates.Any(c => SubtitleRanker.HasCode(c.Name, code)))
    {
        Console.WriteLine($"    没有番号匹配的候选，改用 {alternative} 补查");
        var secondary = (await client.SearchAsync(alternative, CancellationToken.None)).Candidates;
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

            picks.Add(new SubtitlePick(candidate.Name, extension, decoded.Text, decoded.EncodingName));
            Console.WriteLine($"    + {candidate.Name,-52} {bytes.Length,7} B  {decoded.EncodingName,-10} {extension}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    - 下载失败：{candidate.Name} - {ex.Message}");
        }
    }

    var ranked = SubtitleRanker.Rank(picks);
    var best = SubtitleRanker.SelectBest(picks, code);
    if (best is null)
    {
        Console.WriteLine($"    → 没有可用字幕（{picks.Count} 条候选全部属于其它番号）");
        Console.WriteLine();
        continue;
    }
    var target = Path.Combine(outputDirectory, $"{code}.chi{best.Extension}");
    await File.WriteAllBytesAsync(target, new UTF8Encoding(false).GetBytes(SubtitleText.Normalize(best.Text) + "\n"));

    Console.WriteLine($"    → 去重后 {ranked.Count} 条，最优：{best.CandidateName}");
    Console.WriteLine($"      {best.Bytes} B  {best.Extension}  {best.EncodingName}  AI标记 {best.AiMarkers}  -> {target}");
    Console.WriteLine();
}

Console.WriteLine($"输出目录：{outputDirectory}");
return exitCode;
