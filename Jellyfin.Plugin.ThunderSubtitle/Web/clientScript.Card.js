/*
 * 候选字幕卡片的三行布局。
 *
 * 由服务端按顺序拼接在 clientScript.js 之后，共享 window.__ts。
 *
 * 为什么要单独一个文件：
 *   clientScript.js 已经 400 多行，clientScript.Rows.js 也在 300 行上下；
 *   把「卡片长什么样」抽出来，行增强那边只剩「挂什么按钮、怎么切换」，
 *   加一种标签或调一次配色都不必再碰 400 行的大文件。
 *
 * 卡片目标结构（服务端 Comment 的行序就是这里的输入约定）：
 *   第 1 行：1. IPX-811.srt   格式: srt · 2694 字 / 486 条 · 24.9 KB
 *   第 2 行：[AI 评分：74 分] [人工翻译] [简繁混用]
 *   第 3 行：AI 评语
 *
 * 服务端 Comment 的约定（见 Providers/ThunderSubtitleProvider.Search.cs）：
 *   AI 评分：74 分                  → 第 2 行的评分胶囊（没有 AI 评审时省略）
 *   简英双语 · 人工翻译             → 第 2 行的两个标签
 *   2694 字 / 486 条 · 24.9 KB      → 搬到第 1 行
 *   评语正文                        → 第 3 行
 * 保持纯文本可读：脚本万一没加载，这些行本身仍然是有意义的说明。
 */
(function (TS) {
    'use strict';

    // 行级幂等标记。必须是 data 属性并查元素自身：
    // querySelector 只匹配后代，查 row 自己的 class 永远为 null（踩过这个坑）。
    var ROW_MARK = 'data-ts-row';

    // 与服务端 ToInfo 一一对应的行格式。改服务端文案时必须同步改这里。
    var RE_SCORE = /^AI 评分：([0-9]+(?:\.[0-9]+)?) 分$/;
    var RE_LANG = /^(.+?) · (机器翻译|人工翻译)$/;
    var RE_STATS = /^([0-9]+) 字 \/ ([0-9]+) 条 · (.+)$/;

    /// 解析服务端 Comment。返回固定字段，调用方不必关心中间顺序。
    TS.parseComment = function (text) {
        var meta = { score: null, scoreText: '', language: '', translation: '', stats: '', comment: '' };
        var rest = [];
        var lines = String(text || '').split('\n');

        for (var i = 0; i < lines.length; i++) {
            var line = lines[i].replace(/^\s+|\s+$/g, '');
            if (!line) {
                continue;
            }

            var match = RE_SCORE.exec(line);
            if (match) {
                meta.score = parseFloat(match[1]);
                // 原样保留服务端的小数格式：F0 的舍入规则和 JS 的 Math.round 不完全一样，
                // 重新取整会让界面分数与日志 / 缓存里的分数差一分。
                meta.scoreText = match[1];
                continue;
            }

            match = RE_LANG.exec(line);
            if (match) {
                meta.language = match[1];
                meta.translation = match[2];
                continue;
            }

            match = RE_STATS.exec(line);
            if (match) {
                // 原样保留整行，避免重新拼装时把「字 / 条 / KB」的单位改错。
                meta.stats = line;
                continue;
            }

            rest.push(line);
        }

        meta.comment = rest.join('\n');
        return meta;
    };

    /// 评分 → 胶囊配色。
    /// 80 分以上是「好」，60-79 是「一般」，60 以下按「差」处理；
    /// 与语言标签的绿 / 橙 / 红保持同一套视觉语言，用户不用记两套含义。
    TS.scoreTone = function (score) {
        if (score >= 80) {
            return 'tsPillGood';
        }

        return score >= 60 ? 'tsPillWarn' : 'tsPillBad';
    };

    /// 语言标签的底色：混用橙、繁体红、简体绿。
    /// 同时看脚本给的字母和文案，是为了兼容「繁英双语」这种带后缀的写法。
    /// 注意：`script` 既可能是 id 前缀里的单字母（s/t/m），
    /// 也可能是 Script 接口返回的英文单词（simplified/traditional/mixed）。
    function scriptLetter(script) {
        if (script === 'm' || script === 'mixed') {
            return 'm';
        }

        return script === 't' || script === 'traditional' ? 't' : 's';
    }

    function scriptTone(label, script) {
        var letter = scriptLetter(script);
        if (letter === 'm' || label.indexOf('混用') >= 0) {
            return 'tsPillWarn';
        }

        if (letter === 't' || label.indexOf('繁') >= 0) {
            return 'tsPillBad';
        }

        return label.indexOf('简') >= 0 ? 'tsPillGood' : '';
    }

    TS.scriptTone = scriptTone;

    /// 生成一个胶囊。复用 Jellyfin 的 .subtitleFeaturePillow，
    /// 这样内边距、圆角、垂直对齐都与原生「机器翻译」一致，只换底色。
    function makePill(text, tone, title) {
        var pill = document.createElement('span');
        pill.className = 'inline-flex align-items-center justify-content-center subtitleFeaturePillow tsPill'
            + (tone ? ' ' + tone : '');
        pill.textContent = text;

        if (title) {
            pill.title = title;
        }

        return pill;
    }

    /// 把 Jellyfin 原生标签行（格式 / 下载数 / 帧率）拍平成一段文字。
    /// 直接读 textContent 会把相邻 span 粘在一起（「格式: srt下载: 3」），
    /// 所以有 span 时逐个取再自己加分隔符。
    function flattenMeta(element) {
        var spans = element.querySelectorAll('span');
        var parts = [];
        var i;

        if (spans.length === 0) {
            return (element.textContent || '').replace(/^\s+|\s+$/g, '');
        }

        for (i = 0; i < spans.length; i++) {
            var text = (spans[i].textContent || '').replace(/^\s+|\s+$/g, '');
            if (text) {
                parts.push(text);
            }
        }

        return parts.join(' · ');
    }

    /// 把 listItemBody 的直接子元素分成四类。
    /// 不能按固定下标取：Jellyfin 会在「有评论 / 有标签 / 有格式」时增删 div。
    function findParts(body) {
        var parts = { name: null, meta: null, comment: null, flags: null };
        var children = body.children;

        for (var i = 0; i < children.length; i++) {
            var element = children[i];
            var isText = element.classList && element.classList.contains('listItemBodyText');

            if (!isText) {
                if (!parts.name) {
                    parts.name = element;
                }

                continue;
            }

            if (element.querySelector('.subtitleFeaturePillow')) {
                parts.flags = element;
                continue;
            }

            // Jellyfin 只给评语那一行加 style="white-space:pre-line;"，
            // 这是区分「评语行」与「格式行」唯一稳定的特征。
            if ((element.getAttribute('style') || '').toLowerCase().indexOf('pre-line') >= 0) {
                parts.comment = element;
                continue;
            }

            if (!parts.meta) {
                parts.meta = element;
            }
        }

        return parts;
    }

    /// 语言标签的文案。
    /// 混用必须显示「简繁混用」而不是多数派的「简体」，否则标签会误导；
    /// 其余情况优先用服务端的语言名，以保留「简英双语 / 繁英双语」。
    function scriptLabel(language, info) {
        var letter = scriptLetter(info.script);
        if (letter === 'm') {
            return '简繁混用';
        }

        var expected = letter === 't' ? '繁' : '简';
        if (language && language.indexOf(expected) === 0) {
            return language;
        }

        return letter === 't' ? '繁体' : '简体';
    }

    /// 重排一张候选卡片。返回 true 表示已经按新结构处理过。
    /// 解析不到服务端的结构化行时返回 false，由调用方退回到旧的最小增强。
    TS.decorateResultCard = function (row, info) {
        if (!row || !info || row.getAttribute(ROW_MARK) === '1') {
            return true;
        }

        var body = row.querySelector('.listItemBody');
        if (!body) {
            return false;
        }

        var parts = findParts(body);
        var meta = TS.parseComment(parts.comment ? parts.comment.textContent : '');

        // 没有语言/统计行就不是本插件当前版本的评论格式，别动用户可见的排版。
        if (!meta.language && !meta.stats) {
            return false;
        }

        if (!parts.name) {
            parts.name = document.createElement('div');
            body.insertBefore(parts.name, body.firstChild);
        }

        // 第 1 行：把「格式」和「字数 / 条数 / 大小」接在文件名后面。
        var statsParts = [];
        if (parts.meta) {
            var metaText = flattenMeta(parts.meta);
            if (metaText) {
                statsParts.push(metaText);
            }
        }

        if (meta.stats) {
            statsParts.push(meta.stats);
        }

        if (statsParts.length > 0) {
            var stats = document.createElement('span');
            stats.className = 'tsRowStats';
            stats.textContent = statsParts.join(' · ');
            parts.name.appendChild(stats);
        }

        // 第 2 行：评分 + 翻译方式 + 语言。顺序与用户要求一致：评分在最前。
        var badges = document.createElement('div');
        badges.className = 'secondary listItemBodyText tsRowBadges';

        if (meta.score !== null) {
            badges.appendChild(makePill(
                'AI 评分：' + meta.scoreText + ' 分',
                TS.scoreTone(meta.score) + ' tsPillScore',
                'AI 综合评分（满分 100）'));
        }

        if (meta.translation) {
            var badTranslation = meta.translation.indexOf('机器') >= 0;
            badges.appendChild(makePill(
                meta.translation,
                badTranslation ? 'tsPillBad' : 'tsPillGood',
                badTranslation ? '机器翻译 / 自动生成' : '人工翻译'));
        }

        var base = scriptLabel(meta.language, info);
        var tone = scriptTone(base, info.script);
        var scriptPill = makePill(base, tone, '字幕使用的字形');
        // clientScript.Rows.js 的「简」开关靠这个 class 找标签，靠 data-ts-base 还原原文案，
        // 靠 data-ts-tone 在关掉开关时恢复原色（打开时统一变绿）。
        scriptPill.classList.add('tsScriptPillow');
        scriptPill.setAttribute('data-ts-base', base);
        scriptPill.setAttribute('data-ts-tone', tone);
        badges.appendChild(scriptPill);

        // 插在评语行前面；评语行不存在时（没有 AI 评语）追加到末尾。
        var anchor = parts.comment || parts.flags || null;
        body.insertBefore(badges, anchor);

        // 第 3 行：只留评语正文。评分和统计已经搬到上面两行。
        if (parts.comment) {
            if (meta.comment) {
                parts.comment.textContent = meta.comment;
            } else {
                parts.comment.remove();
            }
        }

        // 原生标签行只承载 MachineTranslated（本插件不会给别的标记），
        // 已经重画成配色统一的胶囊，留着会重复且颜色不一致。
        if (parts.flags) {
            parts.flags.remove();
        }

        // 格式行搬空后删掉，否则会多出一行空白。
        if (parts.meta && parts.meta.parentNode) {
            parts.meta.remove();
        }

        row.setAttribute(ROW_MARK, '1');
        return true;
    };

    /// 重排「我的字幕」里的一行：与候选卡片相同的三行布局。
    ///
    /// 输入是 GET /ThunderSubtitle/Script 的返回：
    ///   file / format / chars / cues / bytes    → 第 1 行
    ///   aiScore / machine / script / convertedFrom → 第 2 行
    ///   aiComment                               → 第 3 行
    ///
    /// 这一行原本还有一条完整路径（第 2 行）。为了压到三行，路径移到 title 提示里，
    /// 第 1 行只保留文件名 —— 用户仍然能悬停看到完整路径。
    TS.decorateExistingCard = function (row, info) {
        if (!row || !info || info.available === false) {
            return false;
        }

        if (row.getAttribute(ROW_MARK) === '1') {
            return true;
        }

        var body = row.querySelector('.listItemBody');
        if (!body) {
            return false;
        }

        var name = null;
        var pathLine = null;
        for (var i = 0; i < body.children.length; i++) {
            var element = body.children[i];
            if (element.classList && element.classList.contains('listItemBodyText')) {
                if (!pathLine) {
                    pathLine = element;
                }
            } else if (!name) {
                name = element;
            }
        }

        if (!name) {
            return false;
        }

        if (pathLine) {
            var pathText = (pathLine.textContent || '').replace(/^\s+|\s+$/g, '');
            if (pathText) {
                row.title = pathText;
                name.title = pathText;
            }

            pathLine.remove();
        }

        // 第 1 行：文件名 + 格式 + 字数 / 条数 / 大小。
        var statsParts = [];
        if (info.file) {
            statsParts.push(info.file);
        }

        if (info.format) {
            statsParts.push('格式: ' + info.format);
        }

        if (info.chars > 0 || info.cues > 0) {
            statsParts.push(info.chars + ' 字 / ' + info.cues + ' 条'
                + (info.bytes > 0 ? ' · ' + TS.formatBytes(info.bytes) : ''));
        } else if (info.bytes > 0) {
            statsParts.push(TS.formatBytes(info.bytes));
        }

        if (statsParts.length > 0) {
            var stats = document.createElement('span');
            stats.className = 'tsRowStats';
            stats.textContent = statsParts.join(' · ');
            name.appendChild(stats);
        }

        // 第 2 行：评分 + 翻译方式 + 字形。没有 AI 记录时只画字形。
        var badges = document.createElement('div');
        badges.className = 'secondary listItemBodyText tsRowBadges';

        if (typeof info.aiScore === 'number') {
            badges.appendChild(makePill(
                'AI 评分：' + (info.aiScoreText || Math.round(info.aiScore)) + ' 分',
                TS.scoreTone(info.aiScore) + ' tsPillScore',
                'AI 综合评分（满分 100）'));
        }

        if (typeof info.machine === 'boolean') {
            badges.appendChild(makePill(
                info.machine ? '机器翻译' : '人工翻译',
                info.machine ? 'tsPillBad' : 'tsPillGood',
                info.machine ? '机器翻译 / 自动生成' : '人工翻译'));
        }

        // 转换过的文件：文案保留来源（「简繁混用转简体」），但颜色必须是绿色 ——
        // 文件现在就是简体，继续用红色 / 橙色会让用户以为还有问题。
        var base = info.convertedFrom
            ? info.convertedFrom
            : scriptLabel(info.language, info);
        var tone = info.convertedFrom ? 'tsPillGood' : scriptTone(base, info.script);
        var pill = makePill(info.convertedFrom ? base + '转简体' : base, tone, '字幕使用的字形');
        pill.classList.add('tsScriptPillow');
        pill.setAttribute('data-ts-base', base);
        pill.setAttribute('data-ts-tone', tone);
        badges.appendChild(pill);

        body.insertBefore(badges, name.nextSibling);

        // 第 3 行：AI 评语。没有记录就整行不出现（旧台账只有分数，没有评语）。
        if (info.aiComment) {
            var comment = document.createElement('div');
            comment.className = 'secondary listItemBodyText';
            comment.style.whiteSpace = 'pre-line';
            comment.textContent = info.aiComment;
            body.appendChild(comment);
        }

        row.setAttribute(ROW_MARK, '1');
        return true;
    };

    /// 给「我的字幕」这类没有结构化评论的行补一个语言胶囊。
    /// 返回 false 表示文案不是已知字形，调用方可以跳过。
    TS.addScriptPillow = function (row, text, tone) {
        if (!row || !text || row.querySelector('.tsScriptPillow')) {
            return false;
        }

        var body = row.querySelector('.listItemBody');
        if (!body) {
            return false;
        }

        var container = null;
        var pills = body.querySelectorAll('.subtitleFeaturePillow');
        if (pills.length > 0 && pills[0].parentNode) {
            container = pills[0].parentNode;
        } else {
            container = document.createElement('div');
            container.className = 'secondary listItemBodyText tsRowBadges';
            body.appendChild(container);
        }

        var pill = makePill(text, tone || '', text);
        // 「我的字幕」转完字后要改这一行的文案（convertExisting），
        // 它与搜索结果行共用同一个查找约定。
        pill.classList.add('tsScriptPillow');
        pill.setAttribute('data-ts-base', text);
        pill.setAttribute('data-ts-tone', tone || '');
        container.appendChild(pill);
        return true;
    };
})(window.__ts = window.__ts || {});
