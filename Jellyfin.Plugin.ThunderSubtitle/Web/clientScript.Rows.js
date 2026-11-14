/*
 * 行增强：搜索结果行与「我的字幕」行的字形标签、繁转简按钮。
 *
 * 由服务端按顺序拼接在 clientScript.Card.js 之后，共享 window.__ts 上的工具函数。
 *
 * 为什么单独一个文件：clientScript.js 已经 400 多行，再塞进这两类行会破 600 行红线。
 * 职责也确实是分开的：那边管「怎么预览一份字幕」，Card.js 管「卡片长什么样、标签什么颜色」，
 * 这边只管「往 Jellyfin 的行上挂什么按钮、开关怎么切」。
 */
(function (TS) {
    'use strict';

    // 行上是否已经挂过我们东西的标记。用 data 属性而不是 class：
    // 判断「这一行处理过没有」必须查元素自身，而 querySelector 只匹配后代 ——
    // 早先用 row.querySelector('.tsExistingRow') 判断，永远为 null，
    // 每秒的重扫就会再插一个按钮，界面上眼睛图标越冒越多。
    var MARK = 'data-ts';
    var PREVIEW_ACTION = 'preview';
    var CONVERT_ACTION = 'convert';

    /// 字形字母 → 标签文案。只在没有服务端结构化评论时兜底；
    /// 正常路径的文案与配色由 clientScript.Card.js 负责。
    var LABELS = {
        t: '繁体',
        m: '简繁混用'
    };

    /// 这一行里有没有某个动作的按钮。查后代是对的 —— 按钮确实插在行内部。
    function hasButton(row, action) {
        return !!row.querySelector('[' + MARK + '="' + action + '"]');
    }

    /// 「我的字幕」那类行只有文案，没有服务端结构化评论，按文案本身判配色。
    /// 与 Card.js 的规则保持一致：混用橙、繁体红、简体绿。
    function labelTone(label) {
        if (label.indexOf('混用') >= 0) {
            return 'tsPillWarn';
        }

        if (label.indexOf('繁') >= 0) {
            return 'tsPillBad';
        }

        return label.indexOf('简') >= 0 ? 'tsPillGood' : '';
    }

    /// 给搜索结果行加「简」按钮与字形标签。
    ///
    /// 排版（三行、评分胶囊、统计搬到第一行）由 clientScript.Card.js 负责；
    /// 这里只负责「繁体 / 混用才给转换按钮」，简体没有可转的东西。
    /// 「简」不是「下载并转简」，而是一个开关：点亮后这一行后续的预览与下载都会走转简版。
    TS.decorateResultRow = function (row, info) {
        if (!row || !info) {
            return;
        }

        // 正常路径：Card.js 把卡片重排成三行，并画出评分 / 翻译 / 字形胶囊。
        // 返回 false 表示服务端评论不是新格式（旧 dll 或评语为空），退回到只补字形标签。
        var restructured = TS.decorateResultCard ? TS.decorateResultCard(row, info) : false;
        if (!restructured) {
            var label = LABELS[info.script];
            if (label) {
                TS.addScriptPillow(row, label, labelTone(label));
            }
        }

        if (info.script === 's' || hasButton(row, CONVERT_ACTION)) {
            return;
        }

        var download = row.querySelector('.btnDownload');
        if (!download) {
            return;
        }

        // 图标就是汉字「简」：material-icons 里没有对应字形，
        // 用 span 直接排一个字比塞 SVG 更清晰，也不会在高 DPI 下发虚。
        download.insertAdjacentHTML(
            'beforebegin',
            '<button type="button" is="paper-icon-button-light" ' + MARK + '="' + CONVERT_ACTION + '"'
            + ' class="listItemButton tsGlyphButton" title="把这份字幕转成简体（只切换，不会下载；可用眼睛预览）">'
            + '<span class="tsGlyphIcon" aria-hidden="true">简</span></button>');

        var button = download.previousElementSibling;
        if (!button) {
            return;
        }

        button.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            TS.toggleSimplify(row);
            return false;
        });
    };

    /// 点「简」：只切换「下载时转简」这个开关，<b>不触发任何下载</b>。
    ///
    /// 用户的原话是「繁体转简体按钮只做转换，不包括下载」：
    /// 点了之后可以先点眼睛预览转简后的效果，满意了再自己点下载。
    /// 所以这里做的唯一一件事，是把该行下载按钮的 data-subid 换成转简版前缀，
    /// 之后无论预览还是下载，都会带上这个标志。
    ///
    /// 早先的实现是「改 id → 伪造一次 click」，实测无效：按钮点下去 Jellyfin 拿到的仍是旧 id。
    /// 现在改成「永久改掉 id，让用户自己点」，既绕开了那次伪造，也正好对上新的交互。
    TS.toggleSimplify = function (row) {
        var download = row.querySelector('.btnDownload');
        if (!download) {
            return;
        }

        // 以按钮上「当前」的 id 为准，不用增强时抓到的 info：
        // 用户可能已经点过一次又点回来，info 里的是最初那版。
        var current = TS.parseRow(download.getAttribute('data-subid'));
        if (!current) {
            return;
        }

        var marker = current.simplify
            ? unsimplifyMarkerOf(current.marker)
            : simplifyMarkerOf(current.marker);

        download.setAttribute('data-subid', current.raw.replace(current.marker, marker));

        // 开关的当前状态就是下载按钮上的 id，所以不需要另存一份状态。
        var on = !current.simplify;

        var button = row.querySelector('[' + MARK + '="' + CONVERT_ACTION + '"]');
        if (button) {
            button.classList.toggle('tsGlyphActive', on);
            button.title = on
                ? '已开启转简：预览与下载都会是简体，再点一次取消'
                : '下载这份字幕前先转成简体（点了不影响下载，可以先用眼睛预览）';
        }

        var pill = row.querySelector('.tsScriptPillow');
        if (pill) {
            // base 是 Card.js 写进 data-ts-base 的原文案（可能是「繁英双语」这类带后缀的写法），
            // 不能再用 LABELS[current.script] 猜：那样会把后缀丢掉。
            var base = pill.getAttribute('data-ts-base') || LABELS[current.script] || '字幕';
            // 「繁体」→「繁体转简体」，「简繁混用」→「简繁混用转简体」。
            // 保留原标签而不是换成固定文案，是因为混用和纯繁体对用户是两件事。
            pill.textContent = on ? base + '转简体' : base;

            // 打开开关后实际会以简体落盘，标签必须变绿；关掉时恢复原字形颜色。
            // 原色存在 data-ts-tone 里，不在这里重新推断，避免两处规则漂移。
            var baseTone = pill.getAttribute('data-ts-tone') || (TS.scriptTone
                ? TS.scriptTone(base, current.script)
                : '');
            pill.classList.remove('tsPillGood', 'tsPillWarn', 'tsPillBad');
            if (on) {
                pill.classList.add('tsPillGood');
            } else if (baseTone) {
                pill.classList.add(baseTone);
            }
        }
    };

    /// 转简前缀：第三位 1→2（ts1- → ts2-）。
    function simplifyMarkerOf(marker) {
        return marker.charAt(0) + marker.charAt(1) + '2-';
    }

    /// 反向：第三位 2→1，用于再点一次取消。
    function unsimplifyMarkerOf(marker) {
        return marker.charAt(0) + marker.charAt(1) + '1-';
    }

    /// 扫描「我的字幕」里的每一行，加预览按钮、简按钮与字形标签。
    TS.scanExisting = function () {
        var list = document.querySelector('.subtitleList');
        if (!list) {
            return;
        }

        var rows = list.querySelectorAll('[data-index]');
        for (var i = 0; i < rows.length; i++) {
            enhanceExisting(rows[i]);
        }
    };

    function enhanceExisting(row) {
        if (!row) {
            return;
        }

        // 必须查元素自身。旧代码写的是 row.querySelector('.tsExistingRow') ——
        // querySelector 只匹配后代，元素自己的 class 永远查不到，
        // 于是每秒的重扫都会再插一个预览按钮，界面上眼睛越冒越多。
        if (row.getAttribute(MARK) === 'existing') {
            return;
        }

        var index = row.getAttribute('data-index');
        var itemId = TS.currentItemId();
        if (index == null || !itemId) {
            return;
        }

        var deleteButton = row.querySelector('.btnDelete');
        if (!deleteButton) {
            // 内嵌字幕没有删除按钮，也就没有独立文件 —— 预览和转换都无从谈起。
            return;
        }

        row.setAttribute(MARK, 'existing');

        deleteButton.insertAdjacentHTML(
            'beforebegin',
            '<button type="button" is="paper-icon-button-light" ' + MARK + '="' + PREVIEW_ACTION + '"'
            + ' class="listItemButton" title="预览这份字幕（不会修改文件）" style="display:none">'
            + '<span class="material-icons visibility" aria-hidden="true"></span></button>');

        var preview = deleteButton.previousElementSibling;
        if (preview) {
            preview.addEventListener('click', function (event) {
                event.preventDefault();
                event.stopPropagation();
                TS.openPreview({ itemId: itemId, index: parseInt(index, 10) }, null, { showUse: false });
                return false;
            });
        }

        // 字形与「能不能转」都要问服务端：这里是磁盘上的文件，
        // 前端手上没有任何关于它的信息。
        fetchScript(itemId, index, function (info) {
            if (preview) {
                preview.style.display = '';
            }

            if (!info || info.available === false) {
                return;
            }

            // 新格式：和候选卡片一样的三行布局（文件名+统计 / 胶囊 / AI 评语）。
            // 旧服务端只有 label，退回最小增强。
            if (TS.decorateExistingCard) {
                TS.decorateExistingCard(row, info);
            } else if (info.label) {
                TS.addScriptPillow(row, info.label, labelTone(info.label));
            }

            if (!info.convertible || hasButton(row, CONVERT_ACTION)) {
                return;
            }

            deleteButton.insertAdjacentHTML(
                'beforebegin',
                '<button type="button" is="paper-icon-button-light" ' + MARK + '="' + CONVERT_ACTION + '"'
                + ' class="listItemButton" title="把这份字幕转成简体（原文件备份为 .bak）">'
                + '<span class="tsGlyphIcon" aria-hidden="true">简</span></button>');

            var convert = deleteButton.previousElementSibling;
            if (convert) {
                convert.addEventListener('click', function (event) {
                    event.preventDefault();
                    event.stopPropagation();
                    convertExisting(itemId, index, row);
                    return false;
                });
            }
        });
    }

    /// 查一条本地字幕流的字形。每行一次请求，但只在对话框打开时发生，
    /// 且结果缓存在行上（增强过的行不会重复查）。
    function fetchScript(itemId, index, callback) {
        var url = TS.apiUrl('Script', { itemId: itemId, index: index });
        var token = TS.accessToken();
        if (!url || !token) {
            callback(null);
            return;
        }

        fetch(url, {
            credentials: 'same-origin',
            headers: { Authorization: 'MediaBrowser Token="' + token + '"' }
        }).then(function (response) {
            return response.ok ? response.json() : null;
        }).then(function (data) {
            callback(data);
        }).catch(function () {
            callback(null);
        });
    }

    function convertExisting(itemId, index, row) {
        if (!window.confirm('把这份字幕转成简体？\n\n原文件会备份为 .bak，可以随时还原。')) {
            return;
        }

        var url = TS.apiUrl('Convert', { itemId: itemId, index: index });
        var token = TS.accessToken();
        if (!url || !token) {
            return;
        }

        fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { Authorization: 'MediaBrowser Token="' + token + '"' }
        }).then(function (response) {
            return response.json();
        }).then(function (data) {
            if (data && data.ok) {
                // 转换后文件已经是简体：文案保留来源（「简繁混用转简体」），
                // 但颜色必须变绿 —— 文件现在就是简体，继续用红色 / 橙色会误导。
                var pill = row.querySelector('.tsScriptPillow');
                if (pill) {
                    var base = pill.getAttribute('data-ts-base') || '字幕';
                    pill.textContent = base + '转简体';
                    pill.setAttribute('data-ts-base', base);
                    pill.setAttribute('data-ts-tone', 'tsPillGood');
                    pill.classList.remove('tsPillBad', 'tsPillWarn');
                    pill.classList.add('tsPillGood');
                }

                // 转完就没有可转的东西了，按钮该消失。
                // 按 data 属性精确取，不用 :last-of-type —— 那个伪类按「同类型兄弟」算，
                // 行里还有预览按钮与删除按钮，取不准。
                var convert = row.querySelector('[' + MARK + '="' + CONVERT_ACTION + '"]');
                if (convert) {
                    convert.remove();
                }
            }

            window.alert((data && data.message) || '转换完成。');
        }).catch(function (error) {
            window.alert('转换失败：' + (error && error.message ? error.message : error));
        });
    }
})(window.__ts = window.__ts || {});
