(function () {
    var modeOnline = document.getElementById('modeOnline');
    var modeOffline = document.getElementById('modeOffline');
    var onlinePanel = document.getElementById('onlinePanel');
    var offlinePanel = document.getElementById('offlinePanel');
    var serverProvider = document.getElementById('serverProvider');
    var serverBaseUrlField = document.getElementById('serverBaseUrlField');
    var serverBaseUrl = document.getElementById('serverBaseUrl');
    var pat = document.getElementById('pat');
    var startBtn = document.getElementById('startBtn');

    var mode = 'online';

    function updateServerProvider() {
        var isOther = serverProvider.value === 'other';
        serverBaseUrlField.hidden = !isOther;
        if (isOther) {
            serverBaseUrl.value = ''; // 切到「其他」时清空，让用户填写自有域名
        } else {
            serverBaseUrl.value = 'https://www1.baaaize.site';
        }
    }
    serverProvider.addEventListener('change', updateServerProvider);

    function setMode(next) {
        mode = next;
        modeOnline.classList.toggle('active', next === 'online');
        modeOffline.classList.toggle('active', next === 'offline');
        onlinePanel.hidden = next !== 'online';
        offlinePanel.hidden = next !== 'offline';
    }

    modeOnline.addEventListener('click', function () { setMode('online'); });
    modeOffline.addEventListener('click', function () { setMode('offline'); });

    function markInvalid(el, invalid) { el.classList.toggle('invalid', invalid); }

    function post(message) {
        if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
            window.chrome.webview.postMessage(message);
        }
    }

    function showError(text) {
        var banner = document.getElementById('errorBanner');
        var label = document.getElementById('errorText');
        label.textContent = text || '保存失败，请重试';
        banner.hidden = false;
    }

    function hideError() {
        document.getElementById('errorBanner').hidden = true;
    }

    // 接收 C# 回传的校验失败信息并展示
    if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener) {
        window.chrome.webview.addEventListener('message', function (e) {
            var msg = e.data;
            if (msg && msg.type === 'error') {
                showError(msg.message);
            }
        });
    }

    startBtn.addEventListener('click', function () {
        hideError();
        if (mode === 'online') {
            var url = serverBaseUrl.value.trim();
            var token = pat.value.trim();
            var ok = true;
            if (!url) { markInvalid(serverBaseUrl, true); ok = false; } else { markInvalid(serverBaseUrl, false); }
            if (!token) { markInvalid(pat, true); ok = false; } else { markInvalid(pat, false); }
            if (!ok) { return; }
            post({ type: 'save', mode: 'online', serverBaseUrl: url, pat: token });
        } else {
            post({ type: 'save', mode: 'offline', serverBaseUrl: '', pat: '' });
        }
    });

    serverBaseUrl.addEventListener('input', function () { markInvalid(serverBaseUrl, false); });
    pat.addEventListener('input', function () { markInvalid(pat, false); });

    setMode('online');
    updateServerProvider();
})();