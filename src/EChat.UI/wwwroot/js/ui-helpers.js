window.getSelectedText = function () { return window.getSelection()?.toString() ?? ''; };

window.wrapSelectedText = function (textareaId, prefix, suffix) {
    var textarea = document.getElementById(textareaId);
    if (!textarea) return;
    var start = textarea.selectionStart;
    var end = textarea.selectionEnd;
    var text = textarea.value;
    var selected = text.substring(start, end);
    var wrapped = prefix + selected + suffix;
    textarea.value = text.substring(0, start) + wrapped + text.substring(end);
    textarea.selectionStart = textarea.selectionEnd = start + wrapped.length;
    textarea.focus();
};

window.positionMenu = function (id, x, y) {
    var el = document.getElementById(id);
    if (!el) return;
    var vw = window.innerWidth, vh = window.innerHeight;
    var w = el.offsetWidth || 220, h = el.offsetHeight || 100;
    var left = x + w + 8 > vw ? vw - w - 8 : x;
    var top = y + h + 8 > vh ? vh - h - 8 : y;
    if (left < 8) left = 8;
    if (top < 8) top = 8;
    el.style.left = left + 'px';
    el.style.top = top + 'px';
    el.style.visibility = 'visible';
};

window.copyToClipboard = function (text) { return navigator.clipboard.writeText(text); };

window.lockBodyScroll = function () {
    document.body.style.overflow = 'hidden';
    document.documentElement.style.overflow = 'hidden';
};
window.unlockBodyScroll = function () {
    document.body.style.overflow = '';
    document.documentElement.style.overflow = '';
};

window.closeAttachMenu = function () { };
window.closeEmojiPicker = function () { };

document.addEventListener('click', function (e) {
    var attachWrap = document.querySelector('.attach-wrap');
    if (attachWrap && !attachWrap.contains(e.target)) {
        if (window.closeAttachMenu) window.closeAttachMenu();
    }
    var emojiPopup = document.querySelector('.emoji-picker-popup');
    if (emojiPopup && !emojiPopup.contains(e.target)) {
        var emojiBtn = document.querySelector('.emoji-btn') || document.querySelector('.message-input-bar .input-btn:last-of-type');
        if (emojiBtn && !emojiBtn.contains(e.target) && !emojiPopup.contains(e.target)) {
            if (window.closeEmojiPicker) window.closeEmojiPicker();
        }
    }
});

(function () {
    var isDark = localStorage.getItem('darkMode') === 'true';
    if (isDark) {
        document.documentElement.setAttribute('data-theme', 'dark');
    }
})();

window.applyDarkThemeFromBlazor = function (isDark) {
    if (isDark) {
        document.documentElement.setAttribute('data-theme', 'dark');
        localStorage.setItem('darkMode', 'true');
    } else {
        document.documentElement.removeAttribute('data-theme');
        localStorage.setItem('darkMode', 'false');
    }
};

window.requestNotificationPermission = async function () {
    if (!('Notification' in window)) return 'unsupported';
    return await Notification.requestPermission();
};
window.getNotificationPermission = function () {
    if (!('Notification' in window)) return 'unsupported';
    return Notification.permission;
};
window.showWebNotification = function (title, body) {
    if (!('Notification' in window) || Notification.permission !== 'granted') return;
    if (document.visibilityState === 'visible') return;
    var n = new Notification(title, { body: body, icon: '/favicon.ico', tag: 'echat-msg' });
    n.onclick = function () { window.focus(); n.close(); };
    setTimeout(function () { n.close(); }, 8000);
};

window.downloadBytes = function (filename, bytes) {
    var blob = new Blob([new Uint8Array(bytes)], { type: 'application/zip' });
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url; a.download = filename;
    document.body.appendChild(a); a.click();
    document.body.removeChild(a);
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
};