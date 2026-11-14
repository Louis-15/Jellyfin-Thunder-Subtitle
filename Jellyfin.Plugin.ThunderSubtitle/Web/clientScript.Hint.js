/*
 * 附加提示词：手动搜索对话框里的一行输入框。
 *
 * 为什么不拦截「搜索」按钮：Jellyfin 各版本的按钮选择器一直在变，
 * 拦截 + preventDefault + 重放 click 只要猜错一次，用户就点不动搜索。
 * 改成「输入即保存」（debounce 500ms）：无论用户点哪个按钮、什么时候点，
 * 服务器手上都已经有最新的提示词了，前端完全不介入搜索流程。
 *
 * 从 clientScript.js 拆出来，因为它与「行增强」没有任何共同状态。
 */
(function (TS) {
    'use strict';

    var HINT_CLASS = 'tsHintBox';

    function postHint(itemId, hint) {
        var url = TS.apiUrl('Hint', null);
        if (!url) {
            return Promise.reject(new Error('no api url'));
        }

        var token = TS.accessToken();
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

    TS.ensureHintBox = function () {
        var dialog = document.querySelector('.subtitleEditorDialog');
        if (!dialog || dialog.querySelector('.' + HINT_CLASS)) {
            return;
        }

        var itemId = TS.currentItemId();
        if (!itemId) {
            // 拿不到条目 Id，后端就没法把提示词和这次搜索对上。
            // 宁可不显示输入框，也不要让用户填了却静默失效。
            return;
        }

        TS.ensureStyle();

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
    };
})(window.__ts = window.__ts || {});
