/*
 * Injected into jellyfin-web's index.html at request time.
 *
 * Jellyfin's "修改字幕 → 搜索字幕" dialog is compiled into the web client, so a plugin cannot
 * add a row action from the server side. This script waits for the dialog to render, puts
 * preview / 繁转简 buttons on the rows, and shows the whole subtitle file in an overlay.
 * Nothing is written next to the video: the text is fetched and decoded in memory.
 *
 * 拆成三个文件由服务端按顺序拼接（核心 → 预览/行增强 → 附加提示词），
 * 每个文件都是独立 IIFE，通过 window.__ts 共享工具函数：
 * 单文件已经超过 450 行，再往里塞「我的字幕」行与紫色标签会破 600 行红线。
 */
(function (TS) {
    'use strict';

    if (TS.started) {
        return;
    }

    TS.started = true;

    // 字幕 id 的字形前缀：t + 字形(s/t/m) + 1(原样)/2(转简) + '-'
    // 与 Providers/ThunderSubtitleProvider.cs 的 ScriptMarkers 一一对应。
    TS.SCRIPT_MARKERS = ['ts1-', 'ts2-', 'tt1-', 'tt2-', 'tm1-', 'tm2-'];

    var BUTTON_CLASS = 'btnThunderPreview';
    var STYLE_ID = 'thunderSubtitlePreviewStyle';

    // document.currentScript is only available while this file executes, so capture it now:
    // it is the fallback for the API root when window.ApiClient is not exposed.
    var SCRIPT_SRC = (document.currentScript && document.currentScript.src) || '';
    var overlay = null;
    var wrapEnabled = true;

    TS.apiRoot = function () {
        var index = SCRIPT_SRC.indexOf('/ThunderSubtitle/');
        return index >= 0 ? SCRIPT_SRC.substring(0, index) : '';
    };

    TS.queryString = function (params) {
        var parts = [];
        for (var key in params) {
            if (Object.prototype.hasOwnProperty.call(params, key) && params[key] != null) {
                parts.push(encodeURIComponent(key) + '=' + encodeURIComponent(params[key]));
            }
        }

        return parts.length ? '?' + parts.join('&') : '';
    };

    TS.apiUrl = function (name, params) {
        var client = window.ApiClient;
        if (client && typeof client.getUrl === 'function') {
            try {
                return client.getUrl('ThunderSubtitle/' + name, params);
            } catch (error) {
                // Fall through to the path derived from the script tag.
            }
        }

        var root = TS.apiRoot();
        return root ? root + '/ThunderSubtitle/' + name + TS.queryString(params) : null;
    };

    TS.accessToken = function () {
        var client = window.ApiClient;
        try {
            if (client && typeof client.accessToken === 'function') {
                var token = client.accessToken();
                if (token) {
                    return token;
                }
            }
        } catch (error) {
            // Older/newer clients differ; the credential store below is the stable fallback.
        }

        try {
            var raw = window.localStorage.getItem('jellyfin_credentials');
            if (!raw) {
                return null;
            }

            var parsed = JSON.parse(raw);
            var servers = (parsed && parsed.Data && parsed.Data.Servers) || (parsed && parsed.Servers) || [];
            for (var i = 0; i < servers.length; i++) {
                if (servers[i] && servers[i].AccessToken) {
                    return servers[i].AccessToken;
                }
            }
        } catch (error) {
            // Ignore: the caller reports "please sign in again".
        }

        return null;
    }

    /// Jellyfin prefixes the provider id with an md5 of the provider name and an underscore.
    /// Returns the marker info (字形 + 是否转简) or null for other providers' rows.
    TS.parseRow = function (raw) {
        if (!raw) {
            return null;
        }

        // id 有两种可能的形态，都试一次：
        //   1. 带 md5 前缀：{32 位 md5}_{marker}{base64url}  ← Jellyfin 实际返回的就是这种
        //   2. 不带前缀：{marker}{base64url}
        // 之所以要试第二种，是因为 base64url 用 '_' 代替 '/'，id 自己就含下划线。
        // 早先只认「第一个下划线之后」，一旦遇到没有前缀的 id 就会切在 base64 中间，
        // 整行认不出来 —— 表现为既没有紫色标签、也没有「简」按钮。
        // 只试这两个固定位置，不遍历所有下划线：base64 里偶然出现 "tm1-" 之类的
        // 四字符模式并非不可能，无差别扫描会把别的 provider 的行也误认成我们的。
        var separator = raw.indexOf('_');
        var candidates = separator > 0 ? [raw.substring(separator + 1), raw] : [raw];

        for (var c = 0; c < candidates.length; c++) {
            var payload = candidates[c];
            for (var i = 0; i < TS.SCRIPT_MARKERS.length; i++) {
                var marker = TS.SCRIPT_MARKERS[i];
                if (payload.indexOf(marker) === 0) {
                    return {
                        raw: raw,
                        marker: marker,
                        // s = 简体（不显示标签）, t = 繁体, m = 简繁混用
                        script: marker.charAt(1),
                        simplify: marker.charAt(2) === '2'
                    };
                }
            }
        }

        return null;
    };

    /// 这一行是否已经挂过预览按钮。用 data 属性查，与 clientScript.Rows.js 保持一致：
    /// 判断「这一行处理过没有」绝不能依赖 row 自身的 class（querySelector 看不到自身）。
    var PREVIEW_ATTR = 'data-ts-preview';

    function enhance(row) {
        if (!row || row.tagName === 'BUTTON') {
            return;
        }

        var info = TS.parseRow(row.getAttribute('data-subid'));
        if (!info) {
            return;
        }

        var download = row.querySelector('.btnDownload');
        if (!download) {
            return;
        }

        // 字形标签与「简」按钮（clientScript.Rows.js）。
        //
        // 这里必须调用，不能只在预览按钮的点击回调里做装饰：
        // 早先拆文件时漏了这一步，结果是眼睛按钮出来了、紫色标签和「简」按钮永远不出来，
        // 正是 0.2 节那个「只有眼睛和下载」的现象。
        // decorateResultRow 自己按 data-ts / .tsScriptPillow 做幂等，重复扫描不会叠加。
        if (TS.decorateResultRow) {
            TS.decorateResultRow(row, info);
        }

        // 预览按钮单独判重：标签可能已经挂上，但眼睛按钮还没插（例如上一次插入失败）。
        if (row.querySelector('[' + PREVIEW_ATTR + ']')) {
            return;
        }

        // Inserted as markup so the parser can upgrade the is="paper-icon-button-light"
        // customized built-in, which is what gives it the same look as Jellyfin's buttons.
        download.insertAdjacentHTML(
            'beforebegin',
            '<button type="button" is="paper-icon-button-light" ' + PREVIEW_ATTR + '="1" class="'
            + BUTTON_CLASS + ' listItemButton" title="预览字幕全文（不会下载到影片文件夹）">'
            + '<span class="material-icons visibility" aria-hidden="true"></span></button>');

        var button = download.previousElementSibling;
        if (!button) {
            return;
        }

        button.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            TS.previewResultRow(row, info.raw);
            return false;
        });
    };

    /// 预览按钮的 id 必须现读，不能用增强时抓到的 info.raw：
    /// 用户点过「简」之后，行上的 data-subid 已经换成转简版，
    /// 沿用旧值会预览到没转的那一份 —— 与用户「先转、再预览」的预期正好相反。
    TS.previewResultRow = function (row, fallback) {
        var download = row.querySelector('.btnDownload');
        var raw = download ? download.getAttribute('data-subid') : null;
        TS.openPreview(raw || fallback, row);
    }

    /// 扫描搜索结果里的行（本插件生成的候选）。
    TS.scanResults = function () {
        var rows = document.querySelectorAll('.subtitleResults [data-subid]');
        for (var i = 0; i < rows.length; i++) {
            enhance(rows[i]);
        }
    };

    function scheduleScan() {
        if (scheduleScan.pending) {
            return;
        }

        scheduleScan.pending = true;
        window.requestAnimationFrame(function () {
            scheduleScan.pending = false;
            TS.scanAll();
        });
    }

    TS.ensureStyle = function () {
        if (document.getElementById(STYLE_ID)) {
            return;
        }

        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = [
            '.tsPreviewOverlay{position:fixed;inset:0;z-index:2147483000;background:rgba(0,0,0,.82);',
            'display:flex;align-items:center;justify-content:center;padding:2vh 2vw;}',
            '.tsPreviewDialog{display:flex;flex-direction:column;width:min(1200px,96vw);height:92vh;',
            'background:#101010;color:#eee;border-radius:.35em;box-shadow:0 18px 60px rgba(0,0,0,.7);overflow:hidden;}',
            '.tsPreviewHead{display:flex;flex-wrap:wrap;gap:.6rem 1rem;align-items:center;justify-content:space-between;',
            'padding:.9rem 1.1rem;border-bottom:1px solid rgba(255,255,255,.12);background:rgba(255,255,255,.04);}',
            '.tsPreviewTitle{font-size:1.05rem;font-weight:600;word-break:break-all;}',
            '.tsPreviewMeta{margin-top:.2rem;font-size:.85em;color:#9aa0a6;}',
            '.tsPreviewActions{display:flex;gap:.45rem;flex-wrap:wrap;}',
            '.tsPreviewButton{padding:.4em .9em;border-radius:.25em;border:1px solid rgba(255,255,255,.22);',
            'background:rgba(255,255,255,.08);color:#eee;cursor:pointer;font-size:.9rem;}',
            '.tsPreviewButton:hover{background:rgba(255,255,255,.16);}',
            '.tsPreviewPrimary{border-color:#00a4dc;background:#00a4dc;color:#03151d;font-weight:600;}',
            '.tsPreviewPrimary:hover{background:#2bb8e8;}',
            '.tsPreviewBody{flex:1;margin:0;padding:1rem 1.2rem;overflow:auto;background:#0b0b0b;',
            'font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:.92rem;line-height:1.55;',
            'white-space:pre;tab-size:4;}',
            '.tsPreviewBody.tsPreviewWrap{white-space:pre-wrap;word-break:break-word;}',
            '.tsPreviewEmpty{padding:2rem;color:#9aa0a6;}',
            '.tsHintBox{padding:.6rem .2rem 1rem;}',
            '.tsHintLabel{font-size:.85em;color:#9aa0a6;margin-bottom:.35rem;}',
            '.tsHintInput{width:100%;box-sizing:border-box;resize:vertical;min-height:2.6em;',
            'padding:.45em .6em;border-radius:.25em;border:1px solid rgba(255,255,255,.22);',
            'background:rgba(255,255,255,.06);color:inherit;font:inherit;}',
            '.tsHintState{margin-top:.3rem;font-size:.8em;color:#9aa0a6;min-height:1.1em;}',
            // 标签配色统一成「好绿 / 一般橙 / 差红」：
            // 机器翻译、繁体是红色；简繁混用是橙色；人工翻译、简体 / 简英双语是绿色。
            // 复用 Jellyfin 原生的 .subtitleFeaturePillow 排版，只换底色，
            // 尺寸、圆角、对齐都与原生「机器翻译」一致。
            '.subtitleFeaturePillow.tsPill{background:#546e7a;color:#fff;border:1px solid transparent;',
            'margin-right:.45em;cursor:default;}',
            '.subtitleFeaturePillow.tsPillGood{background:#2e7d32;color:#fff;border-color:#2e7d32;}',
            '.subtitleFeaturePillow.tsPillWarn{background:#d84315;color:#fff;border-color:#d84315;}',
            '.subtitleFeaturePillow.tsPillBad{background:#c62828;color:#fff;border-color:#c62828;}',
            // 评分胶囊加粗一点：同一行里它和语言标签含义不同，扫一眼要能分开。
            '.subtitleFeaturePillow.tsPillScore{font-weight:600;}',
            // 第 1 行文件名后面的「格式 / 字数 / 条数 / 大小」。
            '.tsRowStats{margin-left:.6em;font-size:.85em;font-weight:400;color:#9aa0a6;}',
            // 第 2 行的胶囊容器：标签多时允许换行，不能撑破卡片。
            '.tsRowBadges{display:flex;flex-wrap:wrap;align-items:center;}',
            // 「简」按钮用文字图标：material-icons 里没有对应汉字，
            // 而用户明确要求图标内容就是「简」字。
            '.tsGlyphIcon{font-family:inherit;font-size:.95rem;font-weight:600;line-height:1;}',
            // 开启转简后给按钮上紫底：这是唯一能告诉用户「这一行下载时会转简」的信号，
            // 只靠 title 提示在触屏上根本看不到。
            '.tsGlyphButton.tsGlyphActive{background:#7c4dff;color:#fff;}',
            '.tsGlyphButton.tsGlyphActive:hover{background:#8f66ff;}'
        ].join('');
        document.head.appendChild(style);
    };

    TS.formatBytes = function (bytes) {
        if (!bytes) {
            return '0 B';
        }

        return bytes >= 1024 ? (bytes / 1024).toFixed(1) + ' KB' : bytes + ' B';
    };

    /// 当前详情页 / 播放页的条目 Id。
    /// 地址形如 <c>#/details?id=&lt;guid&gt;</c>，播放页是 <c>#/video?id=&lt;guid&gt;</c>。
    TS.currentItemId = function () {
        var hash = window.location.hash || '';
        var match = hash.match(/[?&]id=([0-9a-fA-F-]{32,36})/);
        return match ? match[1] : null;
    };

    function closePreview() {
        if (overlay && overlay.parentNode) {
            overlay.parentNode.removeChild(overlay);
        }

        overlay = null;
        document.removeEventListener('keydown', onKeyDown, true);
    }

    function onKeyDown(event) {
        if (event.key === 'Escape') {
            closePreview();
        }
    }

    /// 打开预览。
    /// <param name="source">候选 id（`ts1-…`），或 `{ itemId, index }` 形式的本地字幕流。</param>
    /// <param name="row">触发预览的行，用于「使用这份字幕」回点下载按钮。</param>
    /// <param name="options">`{ showUse: false }` 用于「我的字幕」——那是本地已有文件，没有下载按钮。</param>
    TS.openPreview = function (source, row, options) {
        TS.ensureStyle();
        closePreview();

        var opts = options || {};
        overlay = document.createElement('div');
        overlay.className = 'tsPreviewOverlay';
        overlay.innerHTML = [
            '<div class="tsPreviewDialog" role="dialog" aria-modal="true">',
            '  <div class="tsPreviewHead">',
            '    <div>',
            '      <div class="tsPreviewTitle">读取中…</div>',
            '      <div class="tsPreviewMeta"></div>',
            '    </div>',
            '    <div class="tsPreviewActions">',
            '      <button type="button" class="tsPreviewButton" data-act="wrap">自动换行</button>',
            opts.showUse === false
                ? ''
                : '      <button type="button" class="tsPreviewButton tsPreviewPrimary" data-act="use">使用这份字幕</button>',
            '      <button type="button" class="tsPreviewButton" data-act="close" title="关闭（Esc）">✕</button>',
            '    </div>',
            '  </div>',
            '  <pre class="tsPreviewBody tsPreviewWrap">读取中…</pre>',
            '</div>'
        ].join('');

        document.body.appendChild(overlay);
        document.addEventListener('keydown', onKeyDown, true);

        var title = overlay.querySelector('.tsPreviewTitle');
        var meta = overlay.querySelector('.tsPreviewMeta');
        var body = overlay.querySelector('.tsPreviewBody');
        var download = row ? row.querySelector('.btnDownload') : null;

        overlay.addEventListener('click', function (event) {
            var act = event.target.getAttribute && event.target.getAttribute('data-act');
            if (act === 'close' || event.target === overlay) {
                closePreview();
                return;
            }

            if (act === 'wrap') {
                wrapEnabled = !wrapEnabled;
                body.classList.toggle('tsPreviewWrap', wrapEnabled);
                event.target.textContent = wrapEnabled ? '自动换行' : '不换行';
                return;
            }

            if (act === 'use') {
                closePreview();
                if (download) {
                    download.click();
                }
            }
        });

        var url = typeof source === 'string'
            ? TS.apiUrl('Text', { id: source })
            : TS.apiUrl('Stream', { itemId: source.itemId, index: source.index });
        var headers = {};
        var token = TS.accessToken();
        if (token) {
            headers.Authorization = 'MediaBrowser Token="' + token + '"';
        }

        if (!url) {
            title.textContent = '无法预览';
            body.textContent = '拿不到服务器的 API 地址，请刷新页面后重试。';
            return;
        }

        if (!token) {
            title.textContent = '无法预览';
            body.textContent = '没有读到登录凭据，请退出并重新登录 Jellyfin 后再试。';
            return;
        }

        fetch(url, { credentials: 'same-origin', headers: headers })
            .then(function (response) {
                return response.json().catch(function () {
                    return {};
                }).then(function (payload) {
                    if (!response.ok) {
                        throw new Error(payload && payload.error ? payload.error : 'HTTP ' + response.status);
                    }

                    return payload;
                });
            })
            .then(function (data) {
                title.textContent = data.name || '字幕预览';
                meta.textContent = [
                    data.language,
                    data.cueCount + ' 条',
                    data.contentChars + ' 字',
                    TS.formatBytes(data.bytes),
                    (data.extension || '').toUpperCase(),
                    data.encodingName
                ].filter(Boolean).join(' · ');
                body.textContent = data.text || '';
            })
            .catch(function (error) {
                title.textContent = '无法预览';
                body.textContent = '读取字幕失败：' + (error && error.message ? error.message : error);
            });
    };

    /// 扫描全部三类行：搜索结果、我的字幕、附加提示词输入框。
    TS.scanAll = function () {
        TS.scanResults();

        if (TS.scanExisting) {
            TS.scanExisting();
        }

        if (TS.ensureHintBox) {
            TS.ensureHintBox();
        }
    };

    function start() {
        TS.scanAll();

        var observer = new MutationObserver(scheduleScan);
        observer.observe(document.body, { childList: true, subtree: true });

        // 双保险：对话框在屏幕上时每秒重扫一次，
        // 覆盖「行被重渲染但没有 childList 变动」这种情况。
        window.setInterval(function () {
            if (document.querySelector(".subtitleEditorDialog")) {
                TS.scanAll();
            }
        }, 1000);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", start);
    } else {
        start();
    }
}) (window.__ts = window.__ts || {});

