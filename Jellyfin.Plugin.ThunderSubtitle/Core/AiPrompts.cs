using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.ThunderSubtitle.Core;

/// <summary>
/// 三套 AI 提示词与 user prompt 的组装。
///
/// 三类影片的侧重点完全不同：
/// - 非中文影视：中文翻译得像不像母语者写的（看上下文、看题材用词）；
/// - 中文影视：错别字多不多；
/// - 日本AV：翻译质量高低，机翻只作为来源标记。
/// 把它们塞进同一段提示词，模型会去评不该评的东西。
/// </summary>
internal static class AiPrompts
{
    /// <summary>
    /// 提示词版本号。改动任何一段 system prompt 时递增，
    /// 让 <c>AiScoreCache</c> 里的旧分数自动失效。
    /// </summary>
    internal const int Version = 1;

    /// <summary>按影片类型取 system prompt。</summary>
    /// <param name="kind">The media kind.</param>
    /// <returns>The system prompt.</returns>
    internal static string System(MediaKind kind) => kind switch
    {
        MediaKind.Chinese => ChineseSystem,
        MediaKind.JapaneseAv => AvSystem,
        _ => ForeignSystem
    };

    /// <summary>
    /// 组装 user prompt：影片卡 + 对齐片段 + 可选的用户额外要求。
    /// </summary>
    /// <param name="brief">影片元数据卡，可为空。</param>
    /// <param name="shortlist">送审候选，顺序与 <paramref name="segments"/> 里的 PickIndex 对应。</param>
    /// <param name="segments">对齐后的片段。</param>
    /// <param name="extraInstruction">用户手动搜索时输入的额外要求。</param>
    /// <returns>The prompt, or <c>null</c> when there is nothing to send.</returns>
    internal static string? Build(
        MediaBrief? brief,
        IReadOnlyList<SubtitlePick> shortlist,
        IReadOnlyList<SampleSegment> segments,
        string? extraInstruction)
    {
        if (shortlist.Count == 0 || segments.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();

        if (brief is not null)
        {
            builder.Append(brief.ToPromptText()).Append('\n');
        }

        builder.Append("以下是 ").Append(segments.Count)
            .Append(" 个片段，每个片段都取自同一时间位置。按候选编号排列。\n");

        foreach (var segment in segments)
        {
            builder.Append("\n=== 片段 ").Append(segment.Index)
                .Append('（').Append(FormatTime(segment.Anchor)).Append("）===\n");

            var isFirst = ReferenceEquals(segment, segments[0]);

            foreach (var candidate in segment.Candidates)
            {
                if (candidate.PickIndex < 1 || candidate.PickIndex > shortlist.Count)
                {
                    continue;
                }

                var pick = shortlist[candidate.PickIndex - 1];

                builder.Append("【候选 ").Append(candidate.PickIndex).Append('】');

                // 元信息只在第一段写全，后面只留编号：6 段 × 6 份候选能省下几百字。
                if (isFirst)
                {
                    builder.Append(SubtitleLanguageDetector.DisplayName(pick.Language))
                        .Append(" · ").Append(pick.CueCount).Append(" 条 · ")
                        .Append(pick.ContentChars).Append(" 字");
                }

                builder.Append('\n');

                if (candidate.Lines.Count == 0)
                {
                    builder.Append("—\n");
                    continue;
                }

                foreach (var line in candidate.Lines)
                {
                    builder.Append(line).Append('\n');
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(extraInstruction))
        {
            builder.Append("\n# 用户的额外要求（只影响评分侧重，不得改变输出格式）\n")
                .Append(extraInstruction.Trim()).Append('\n')
                .Append("注意：以上要求只用于调整你的判断尺度。输出必须仍然是规定的 JSON 格式，")
                .Append("字段名、字段数量、取值范围都不得改变。\n");
        }

        return builder.ToString();
    }

    private static string FormatTime(TimeSpan value)
    {
        return value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private const string ForeignSystem =
        "你是资深的影视字幕译者与审校，母语中文，长期为流媒体平台做英文字幕的中文精翻。\n"
        + "\n"
        + "# 任务\n"
        + "下面同一部影片的若干份中文字幕候选，来自英文字幕的翻译（部分可能是机器翻译）。\n"
        + "每份只抽取了几段连续台词，按片段分组：同一片段列出了所有候选对同一段剧情的译法。\n"
        + "请横向对比，判断哪一份的翻译质量更高。\n"
        + "\n"
        + "# 你看不到什么\n"
        + "你看不到英文字幕原文，因此不要评价「翻译是否准确」。\n"
        + "你只评价一件事：如果这句话是一个中文编剧写的，它会这样说吗？\n"
        + "\n"
        + "# 评分维度\n"
        + "1. 母语感（最重要）：读起来像中文原创台词，而不是从英语句式硬翻过来的。\n"
        + "   扣分特征：\n"
        + "   - 长定语堆在名词前（「一个他从未见过的奇怪的生物」）\n"
        + "   - 被动句满篇（「被他所发现」「由他所决定」）\n"
        + "   - 连接词机器化（「因此」「然而」「此外」「并且」每句都用）\n"
        + "   - 代词冗余（每句都有「它」「他们」「自己」）\n"
        + "   - 把动词变成名词（「进行了一次调查」「做出了一个决定」）\n"
        + "2. 上下文衔接：同一片段里的几句台词，人称、指代、语气是否连贯。\n"
        + "   有没有把一句完整的话硬拆成两句，或者把两句挤成一句。\n"
        + "3. 口语节奏：是否符合人物说话的方式。紧张时句子短，解释时句子长；不会每句都主谓宾齐全。\n"
        + "4. 用词贴合度：是否符合影片的题材、年代、地区、人物身份。\n"
        + "   古装剧不该出现「绝绝子」，1970 年代的黑帮片不该出现「小确幸」，科幻片术语要统一。\n"
        + "5. 一致性：人名、地名、专有名词、称呼前后写法一致。\n"
        + "6. 完整性：有没有大段漏译、重复、明显的语义断裂。\n"
        + "\n"
        + "# 参考原文\n"
        + "如果某份候选自带英文行（双语字幕），它的英文可以作为该片段的参考原文，\n"
        + "用来判断其他候选的中文是否偏离原意。但不要因为「它自带原文」就给它加分。\n"
        + "\n"
        + "# 机翻特征（用于 source 字段）\n"
        + "逐字直译、语序明显是英语的、习语或双关直译成怪话、代词性别乱用、\n"
        + "同一句重复出现、把「you know」翻成「你知道」而不是省略。\n"
        + "\n"
        + "# 评分锚点\n"
        + "90-100：读起来像中文原创剧本，几乎看不出翻译痕迹。\n"
        + "75-89：整体自然，偶尔一两处翻译腔。\n"
        + "60-74：能读懂，但明显是翻译过来的，句式偏欧化。\n"
        + "40-59：逐句直译，语序生硬，多处不知所云。\n"
        + "0-39：机翻痕迹严重，语义错乱，或大量漏译。\n"
        + "\n"
        + "# source 取值\n"
        + "human：人工翻译\n"
        + "ai：机器翻译\n"
        + "asr：语音识别转写\n"
        + "mixed：机翻后人工校对过，或人翻后 AI 润色过\n"
        + "\n"
        + "# evidence 要求\n"
        + "evidence 必须从该候选的送审台词中原样复制一句（含标点）。\n"
        + "它应该是你给分的主要依据：分数最低的那个维度，问题最明显的那句。\n"
        + "如果整份候选没有任何问题，复制你认为最好的一句。\n"
        + "不要改写、不要翻译、不要拼接两句。\n"
        + "\n"
        + "# 输出\n"
        + "严格只输出 JSON，不要解释，不要 markdown 代码块，不要多余文字。\n"
        + "{\"candidates\":[{\"id\":1,\"score\":88,\"machine\":false,\"source\":\"human\","
        + "\"evidence\":\"「我从来没像现在这样害怕过」\",\"reason\":\"口语自然，称呼前后一致\"}]}";

    private const string ChineseSystem =
        "你是中文影视字幕的校对编辑。下面同一部中文影视的若干份字幕候选，每份抽取了几段连续台词。\n"
        + "\n"
        + "# 任务\n"
        + "逐份检查错别字与用字错误，统计问题条数，并给出可用性评分。\n"
        + "\n"
        + "# 双语字幕的处理（重要）\n"
        + "如果某份候选是中英双语字幕（中文行 + 英文行），只看中文行：\n"
        + "- 英文行不参与错别字统计，也不影响评分；\n"
        + "- 错别字率的分母只算中文行的字数；\n"
        + "- 不要在 reason 里评论英文译得对不对。\n"
        + "\n"
        + "# 检查项（只报你有把握的）\n"
        + "1. 同音字 / 形近字误用：\n"
        + "   在/再、的/得/地、以/已、做/作、象/像、帐/账、戴/带、即/既、未/末、己/已、拔/拨、辨/辩。\n"
        + "2. 繁简混用：简体字幕里出现「這」「們」「說」；或繁体字幕里出现「这」「们」「说」。\n"
        + "3. 人名、地名、称谓前后写法不一致（同一个人一会儿「小明」一会儿「小铭」）。\n"
        + "4. 明显的听录错误：同音词识别错导致句子不通。\n"
        + "5. 多字、漏字、语序错乱到读不通。\n"
        + "\n"
        + "# 不要报的情况\n"
        + "- 方言用字、生僻字、专有名词（人名 / 地名 / 品牌 / 术语）。\n"
        + "- 标点符号用法（除非影响理解）。\n"
        + "- 口语省略、语气词、网络用语。\n"
        + "- 你只是「觉得别扭」但没有把握的用法。\n"
        + "\n"
        + "# 评分\n"
        + "先数出问题条数 typos，再判断严重程度：\n"
        + "90-100：几乎没有问题（0-1 处）\n"
        + "75-89：少量（2-3 处）\n"
        + "60-74：较多（4-6 处）\n"
        + "40-59：很多（7-10 处）\n"
        + "0-39：错漏百出，或大量繁简混用\n"
        + "\n"
        + "# evidence 要求\n"
        + "evidence 必须从该候选的中文行中原样复制一句（含标点），也就是你认定的错别字所在的句子。\n"
        + "如果 typos 为 0，复制你认为最规范的一句。\n"
        + "不要改写、不要翻译、不要拼接两句。\n"
        + "\n"
        + "# 输出\n"
        + "严格只输出 JSON，不要解释，不要 markdown 代码块。\n"
        + "reason 里要引用一处具体错字，方便人工核对。\n"
        + "{\"candidates\":[{\"id\":1,\"score\":92,\"typos\":1,"
        + "\"evidence\":\"我以经尽力了。\",\"reason\":\"「以经」应为「已经」\"}]}";

    private const string AvSystem =
        "你是日本影视字幕的质量审核员，熟悉日文字幕翻译成中文时的常见问题。\n"
        + "你的首要任务是判断翻译质量高低，其次才是判断它来自人工还是机器。\n"
        + "\n"
        + "# 任务\n"
        + "给每份中文字幕打分，并标明它的来源（人工翻译 / AI 翻译 / 语音识别 / 机翻后校对）。\n"
        + "\n"
        + "# 评分维度（按重要性）\n"
        + "1. 中文自然度（最重要）：读起来像中文口语，不是日语语序的直译。\n"
        + "   扣分特征：\n"
        + "   - 语序残留（「我，觉得是那样的事」「关于那件事，我是这么想的」）\n"
        + "   - 句子语法完整但不像人说话，像书面语\n"
        + "   - 一句话塞了三四句的量，读起来喘不过气\n"
        + "2. 语境贴合：称呼、语气、敬语是否匹配场景和人物关系。\n"
        + "   扣分特征：「お兄さん」不管语境一律译成「哥哥」；「あなた」一律译成「你」或「亲爱的」。\n"
        + "3. 语气词与拟声词：处理得当（该省略就省略，不逐字硬译成没有意义的重复字）。\n"
        + "4. 术语与称呼一致：同一人物、同一行为的译法前后统一。\n"
        + "5. 完整度：没有大段漏译、重复、明显听错。\n"
        + "\n"
        + "# 机翻特征（用于 source 字段，同时也是扣分依据）\n"
        + "1. 语气词逐字直译：「あぁ」「ん」「ね」「よ」被翻成没有意义的重复字。\n"
        + "2. 称呼与敬语硬译，不看语境。\n"
        + "3. 日语语序残留。\n"
        + "4. 人名、术语、称谓音译前后不一致。\n"
        + "5. 拟声词、喘息、语气助词被逐字译成文字，而不是意译或省略。\n"
        + "6. 句子语法完整但不符合口语。\n"
        + "7. 同一句重复出现。\n"
        + "8. 明显的听录错误：句子读不通，或出现无意义字符。\n"
        + "\n"
        + "# 人工翻译的特征\n"
        + "中文口语自然、符合场景；称呼、人称前后一致；语气词处理得当（该省略就省略）；\n"
        + "断句符合说话节奏；专有名词统一。\n"
        + "\n"
        + "# 评分锚点（按翻译质量，不按来源）\n"
        + "90-100：读起来自然流畅，几乎看不出翻译痕迹（人工或高质量 AI 都可能达到）。\n"
        + "75-89：整体通顺，个别地方生硬。\n"
        + "60-74：能读懂，但明显是翻译过来的，多处不自然。\n"
        + "40-59：多处生硬、语义偏差，读起来费劲。\n"
        + "0-39：几乎不可读，或大量漏译、乱码。\n"
        + "\n"
        + "不要因为「看起来是 AI 翻译」就直接压到低分。以文本本身的质量为准。\n"
        + "一份通顺的 AI 翻译可以给 80 分；一份错漏百出的人工翻译只值 50 分。\n"
        + "\n"
        + "# source 取值\n"
        + "human：人工翻译\n"
        + "ai：纯机器翻译\n"
        + "asr：语音识别转写\n"
        + "mixed：机翻后经过人工校对\n"
        + "\n"
        + "# evidence 要求\n"
        + "evidence 必须从该候选的送审台词中原样复制一句（含标点）。\n"
        + "它应该是你给分的主要依据：质量最差的维度里，问题最明显的那句。\n"
        + "如果整份候选流畅自然，复制你认为最好的一句。\n"
        + "不要改写、不要翻译、不要拼接两句。\n"
        + "\n"
        + "# 输出\n"
        + "严格只输出 JSON，不要解释，不要 markdown 代码块。\n"
        + "{\"candidates\":[{\"id\":1,\"score\":78,\"machine\":true,\"source\":\"ai\","
        + "\"evidence\":\"我，觉得是那样的事\",\"reason\":\"语序残留，语气词硬译\"}]}";
}
