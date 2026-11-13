/*
 * Injected into jellyfin-web's index.html at request time.
 *
 * Jellyfin's "修改字幕 → 搜索字幕" dialog is compiled into the web client, so a plugin cannot
 * add a row action from the server side. This script waits for the dialog to render, puts a
 * preview button next to each result's download button, and shows the whole subtitle file in an
 * overlay. Nothing is written next to the video: the text is fetched and decoded in memory.
 */
(function () {
    'use strict';

    if (window.__thunderSubtitleClient) {
        return;
    }

    window.__thunderSubtitleClient = true;

    var ID_MARKER = 'ts1-';
    var BUTTON_CLASS = 'btnThunderPreview';
    var STYLE_ID = 'thunderSubtitlePreviewStyle';
    var HINT_CLASS = 'tsHintBox';

    // document.currentScript is only available while this file executes, so capture it now:
    // it is the fallback for the API root when window.ApiClient is not exposed.
    var SCRIPT_SRC = (document.currentScript && document.currentScript.src) || '';
    var overlay = null;
    var wrapEnabled = true;

    function apiRoot() {
        var index = SCRIPT_SRC.indexOf('/ThunderSubtitle/');
        return index >= 0 ? SCRIPT_SRC.substring(0, index) : '';
    }

    function queryString(params) {
        var parts = [];
        for (var key in params) {
            if (Object.prototype.hasOwnProperty.call(params, key)) {
                parts.push(encodeURIComponent(key) + '=' + encodeURIComponent(params[key]));
            }
        }

        return parts.length ? '?' + parts.join('&') : '';
    }

    function apiUrl(name, params) {
        var client = window.ApiClient;
        if (client && typeof client.getUrl === 'function') {
            try {
                return client.getUrl('ThunderSubtitle/' + name, params);
            } catch (error) {
                // Fall through to the path derived from the script tag.
            }
        }

        var root = apiRoot();
        return root ? root + '/ThunderSubtitle/' + name + queryString(params) : null;
    }

    function accessToken() {
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

    // Jellyfin prefixes the provider id with an md5 of the provider name and an underscore.
    function ourSubtitleId(raw) {
        if (!raw) {
            return null;
        }

        var separator = raw.indexOf('_');
        var payload = separator >= 0 ? raw.substring(separator + 1) : raw;
        return payload.indexOf(ID_MARKER) === 0 ? raw : null;
    }

    function enhance(row) {
        if (!row || row.tagName === 'BUTTON') {
            return;
        }

        if (row.querySelector('.' + BUTTON_CLASS)) {
            return;
        }

        var subtitleId = ourSubtitleId(row.getAttribute('data-subid'));
        if (!subtitleId) {
            return;
        }

        var download = row.querySelector('.btnDownload');
        if (!download) {
            return;
        }

        // Inserted as markup so the parser can upgrade the is="paper-icon-button-light"
        // customized built-in, which is what gives it the same look as Jellyfin's buttons.
        download.insertAdjacentHTML(
            'beforebegin',
            '<button type="button" is="paper-icon-button-light" class="' + BUTTON_CLASS
            + ' listItemButton" title="预览字幕全文（不会下载到影片文件夹）">'
            + '<span class="material-icons visibility" aria-hidden="true"></span></button>');

        var button = download.previousElementSibling;
        if (!button) {
            return;
        }

        button.addEventListener('click', function (event) {
            event.preventDefault();
            event.stopPropagation();
            openPreview(subtitleId, row);
            return false;
        });
    }

    function scan() {
        var rows = document.querySelectorAll('.subtitleResults [data-subid]');
        for (var i = 0; i < rows.length; i++) {
            enhance(rows[i]);
        }
    }

    function scheduleScan() {
        if (scheduleScan.pending) {
            return;
        }

        scheduleScan.pending = true;
        window.requestAnimationFrame(function () {
            scheduleScan.pending = false;
            scan();
        });
    }

    function ensureStyle() {
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
            '.tsHintState{margin-top:.3rem;font-size:.8em;color:#9aa0a6;min-height:1.1em;}'
        ].join('');
        document.head.appendChild(style);
    }

    function formatBytes(bytes) {
        if (!bytes) {
            return '0 B';
        }

        return bytes >= 1024 ? (bytes / 1024).toFixed(1) + ' KB' : bytes + ' B';
    }

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

    function openPreview(subtitleId, row) {
        ensureStyle();
        closePreview();

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
            '      <button type="button" class="tsPreviewButton tsPreviewPrimary" data-act="use">使用这份字幕</button>',
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

        var url = apiUrl('Text', { id: subtitleId });
        var headers = {};
        var token = accessToken();
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
                    formatBytes(data.bytes),
                    (data.extension || '').toUpperCase(),
                    data.encodingName
                ].filter(Boolean).join(' · ');
                body.textContent = data.text || '';
            })
            .catch(function (error) {
                title.textContent = '无法预览';
                body.textContent = '读取字幕失败：' + (error && error.message ? error.message : error);
            });
    }

    // ---------------------------------------------------------------------
    // 附加提示词：手动搜索对话框里的一行输入框
    //
    // 为什么不拦截「搜索」按钮：Jellyfin 各版本的按钮选择器一直在变，
    // 拦截 + preventDefault + 重放 click 只要猜错一次，用户就点不动搜索。
    // 改成「输入即保存」（debounce 500ms）：无论用户点哪个按钮、什么时候点，
    // 服务器手上都已经有最新的提示词了，前端完全不介入搜索流程。
    // ---------------------------------------------------------------------

    function currentItemId() {
        // 详情页与播放页的地址形如 #/details?id=<guid>，播放页是 #/video?id=<guid>。
        var hash = window.location.hash || '';
        var match = hash.match(/[?&]id=([0-9a-fA-F-]{32,36})/);
        return match ? match[1] : null;
    }

    function postHint(itemId, hint) {
        var url = apiUrl('Hint', null);
        if (!url) {
            return Promise.reject(new Error('no api url'));
        }

        var token = accessToken();
        var headers = { 'Content-Type': 'application/json' };
        if (token) {
            headers.Authorization = 'MediaBrowser Token="' + token + '"';
        }

        return fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: headers,
            body: JSON.stringify({ itemId: itemId, hint: hint || '' })
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status);
            }

            return response.json().catch(function () {
                return {};
            });
        });
    }

    function saveHintSoon(itemId, box) {
        var textarea = box.querySelector('.tsHintInput');
        var state = box.querySelector('.tsHintState');
        if (!textarea || !state) {
            return;
        }

        if (saveHintSoon.timer) {
            window.clearTimeout(saveHintSoon.timer);
        }

        state.textContent = '正在保存…';
        saveHintSoon.timer = window.setTimeout(function () {
            postHint(itemId, textarea.value)
                .then(function (data) {
                    var length = data && typeof data.length === 'number' ? data.length : 0;
                    state.textContent = length > 0
                        ? '已应用（' + length + ' 字），只影响本次搜索'
                        : '';
                })
                .catch(function () {
                    // 降级：提示词没送到，搜索照常进行，只是按默认尺度评审。
                    state.textContent = '提示词没有送到服务器，本次搜索按默认尺度评审';
                });
        }, 500);
    }

    function ensureHintBox() {
        var dialog = document.querySelector('.subtitleEditorDialog');
        if (!dialog || dialog.querySelector('.' + HINT_CLASS)) {
            return;
        }

        var itemId = currentItemId();
        if (!itemId) {
            // 拿不到条目 Id，后端就没法把提示词和这次搜索对上。
            // 宁可不显示输入框，也不要让用户填了却静默失效。
            return;
        }

        ensureStyle();

        var box = document.createElement('div');
        box.className = HINT_CLASS;
        box.innerHTML = [
            '<div class="tsHintLabel">本次搜索的额外要求（可选，10 分钟内有效）</div>',
            '<textarea class="tsHintInput" rows="2" maxlength="500" placeholder="例如：这是 1970 年代的黑帮片，用词粗粝一点，不要用网络语"></textarea>',
            '<div class="tsHintState"></div>'
        ].join('');

        // 插在搜索结果上方；对话框还没渲染结果时退化为追加到末尾。
        var results = dialog.querySelector('.subtitleResults');
        if (results && results.parentNode) {
            results.parentNode.insertBefore(box, results);
        } else {
            dialog.appendChild(box);
        }

        var textarea = box.querySelector('.tsHintInput');
        if (textarea) {
            textarea.addEventListener('input', function () {
                saveHintSoon(itemId, box);
            });
        }
    }

    function start() {
        scan();
        ensureHintBox();

        var observer = new MutationObserver(scheduleScan);
        observer.observe(document.body, { childList: true, subtree: true });

        // Belt and braces: re-scan once a second while a subtitle dialog is on screen, so a row
        // that is re-rendered without a childList mutation still gets its button.
        window.setInterval(function () {
            if (document.querySelector('.subtitleEditorDialog')) {
                scan();
                ensureHintBox();
            }
        }, 1000);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
