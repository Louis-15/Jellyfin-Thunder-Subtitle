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
    var overlay = null;
    var wrapEnabled = true;

    function apiUrl(name, params) {
        var client = window.ApiClient;
        if (!client || typeof client.getUrl !== 'function') {
            return null;
        }

        return client.getUrl('ThunderSubtitle/' + name, params);
    }

    function authorizationHeader() {
        var client = window.ApiClient;
        var token = client && typeof client.accessToken === 'function' ? client.accessToken() : null;
        return token ? 'MediaBrowser Token="' + token + '"' : null;
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
            '.tsPreviewEmpty{padding:2rem;color:#9aa0a6;}'
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
        var auth = authorizationHeader();
        if (auth) {
            headers.Authorization = auth;
        }

        if (!url) {
            body.textContent = '页面还没准备好，请稍后重试。';
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
                    data.cues + ' 条',
                    data.chars + ' 字',
                    formatBytes(data.bytes),
                    (data.extension || '').toUpperCase(),
                    data.encoding
                ].filter(Boolean).join(' · ');
                body.textContent = data.text || '';
            })
            .catch(function (error) {
                title.textContent = '无法预览';
                body.textContent = '读取字幕失败：' + (error && error.message ? error.message : error);
            });
    }

    function start() {
        scan();

        var observer = new MutationObserver(scheduleScan);
        observer.observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
