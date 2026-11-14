/*
 * 行增强：搜索结果行与「我的字幕」行的紫色字形标签、繁转简按钮。
 *
 * 由服务端按顺序拼接在 clientScript.js 之后，共享 window.__ts 上的工具函数。
 *
 * 为什么单独一个文件：clientScript.js 已经 400 行，再塞进这两类行会破 600 行红线。
 * 职责也确实是分开的：那边管「怎么预览一份字幕」，这边管「往 Jellyfin 的行上挂什么」。
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

    /// 字形字母 → 标签文案。简体不显示标签（用户看惯简体，没必要每个都标一遍）。
    var LABELS = {
        t: '繁体',
        m: '简繁混用'
    };

    /// 这一行里有没有某个动作的按钮。查后代是对的 —— 按钮确实插在行内部。
    function hasButton(row, action) {
        return !!row.querySelector('[' + MARK + '="' + action + '"]');
    }

    /// 往一行的标签区插入紫色 pill。
    ///
    /// 复用 Jellyfin 原生的 .subtitleFeaturePillow，只加一个 tsScriptPillow 换配色 ——
    /// 这样尺寸、圆角、对齐全都跟原生「机器翻译 / AI 翻译」一致，不用自己去猜样式。
    /// 没有标签容器时（Jellyfin 只在有 pill 时才渲染那个 div），自己造一个。
    TS.addScriptPillow = function (row, text) {
        if (!row || !text || row.querySelector('.tsScriptPillow')) {
            return;
        }

        var body = row.querySelector('.listItemBody');
        if (!body) {
            return;
        }

        var container = null;
        var pills = body.querySelectorAll('.subtitleFeaturePillow');
        if (pills.length > 0 && pills[0].parentNode) {
            container = pills[0].parentNode;
        } else {
            container = document.createElement('div');
            container.className = 'secondary listItemBodyText';
            body.appendChild(container);
        }

        var pill = document.createElement('span');
        pill.className = 'inline-flex align-items-center justify-content-center subtitleFeaturePillow tsScriptPillow';
        pill.textContent = text;
        container.appendChild(pill);
    };

    /// 给搜索结果行加「简」按钮与紫色标签。
    ///
    /// 只在繁体 / 混用时加按钮：简体没有可转的东西，多一个按钮只会让行更挤。
    /// 「简」不是「下载并转简」，而是一个开关：点亮后这一行后续的预览与下载都会走转简版。
    TS.decorateResultRow = function (row, info) {
        var label = LABELS[info.script];
        if (!label) {
            return;
        }

        TS.addScriptPillow(row, label);

        if (hasButton(row, CONVERT_ACTION)) {
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
            // 「繁体」→「繁体转简体」，「简繁混用」→「简繁混用转简体」。
            // 保留原标签而不是换成固定文案，是因为混用和纯繁体对用户是两件事。
            pill.textContent = on
                ? LABELS[current.script] + '转简体'
                : LABELS[current.script];
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

    /// 扫描「我的字幕」里的每一行，加预览按钮、简按钮与紫色标签。
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

            if (!info || !info.label || info.script === 'simplified') {
                return;
            }

            TS.addScriptPillow(row, info.label);

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
                    convertExisting(itemId, index, info, row);
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

    function convertExisting(itemId, index, info, row) {
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
                // 转换后字形变了，标签要跟着改：把「繁体 / 简繁混用」换成「繁体转简体」，
                // 与候选列表里那份的文案保持一致。
                var pill = row.querySelector('.tsScriptPillow');
                if (pill) {
                    pill.textContent = info.label + '转简体';
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
