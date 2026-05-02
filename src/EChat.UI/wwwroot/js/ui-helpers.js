window.getSelectedText = function() { 
    var sel = window.getSelection();
    if (sel && sel.toString()) return sel.toString();
    var ta = document.getElementById('messageInput');
    if (ta && ta.selectionStart !== ta.selectionEnd) {
        return ta.value.substring(ta.selectionStart, ta.selectionEnd);
    }
    return '';
};

window.getCursorPosition = function(id) {
    var el = document.getElementById(id);
    return el ? {x: el.getBoundingClientRect().left, y: el.getBoundingClientRect().top} : {x:0, y:0};
};

window.insertTextAtCursor = function(id, text) {
    var ta = document.getElementById(id);
    if (!ta) return;
    var s = ta.selectionStart;
    var val = ta.value;
    ta.value = val.substring(0, s) + text + val.substring(s);
    ta.selectionStart = ta.selectionEnd = s + text.length;
    ta.focus();
};

window.getTextareaValue = function(id) {
    var ta = document.getElementById(id);
    return ta ? ta.value : '';
};

window.wrapSelectedText = function(id, prefix, suffix) {
    var ta = document.getElementById(id);
    if (!ta) return;
    var s = ta.selectionStart, e = ta.selectionEnd;
    var val = ta.value;
    var sel = val.substring(s, e);
    ta.value = val.substring(0, s) + prefix + sel + suffix + val.substring(e);
    ta.selectionStart = ta.selectionEnd = s + prefix.length + sel.length + suffix.length;
    ta.focus();
};

window._lastCtxX = 0;
window._lastCtxY = 0;
document.addEventListener('contextmenu', function(e) { window._lastCtxX = e.clientX; window._lastCtxY = e.clientY; });

window.getLastCtxPos = function() { return {x: window._lastCtxX, y: window._lastCtxY}; };
window.getLastContextMenuPosition = function() { return {x: window._lastCtxX, y: window._lastCtxY}; };

window.positionMenu = function(id, x, y) {
    var el = document.getElementById(id);
    if (!el) return;
    var vw = window.innerWidth, vh = window.innerHeight;
    var w = el.offsetWidth || 220, h = el.offsetHeight || 100;
    el.style.left = (x + w + 8 > vw ? vw - w - 8 : x) + 'px';
    el.style.top = (y + h + 8 > vh ? vh - h - 8 : y) + 'px';
    el.style.visibility = 'visible';
};

window.copyToClipboard = function(t) { navigator.clipboard.writeText(t); };
window.lockBodyScroll = function() { document.body.style.overflow = 'hidden'; };
window.unlockBodyScroll = function() { document.body.style.overflow = ''; };
window.closeAttachMenu = function() {};
window.closeEmojiPicker = function() {};

document.addEventListener('click', function(e) {
    var aw = document.querySelector('.attach-wrap');
    if (aw && !aw.contains(e.target)) { if (window.closeAttachMenu) window.closeAttachMenu(); }
    var ep = document.querySelector('.emoji-picker-popup');
    if (ep && !ep.contains(e.target)) { if (window.closeEmojiPicker) window.closeEmojiPicker(); }
});

(function() {
    if (localStorage.getItem('darkMode') === 'true') document.documentElement.setAttribute('data-theme', 'dark');
})();

window.applyDarkThemeFromBlazor = function(isDark) {
    if (isDark) { document.documentElement.setAttribute('data-theme', 'dark'); localStorage.setItem('darkMode', 'true'); }
    else { document.documentElement.removeAttribute('data-theme'); localStorage.setItem('darkMode', 'false'); }
};

window.requestNotificationPermission = async function() {
    if (!('Notification' in window)) return 'unsupported';
    return await Notification.requestPermission();
};
window.getNotificationPermission = function() { return ('Notification' in window) ? Notification.permission : 'unsupported'; };
window.showWebNotification = function(title, body) {
    if (!('Notification' in window) || Notification.permission !== 'granted') return;
    if (document.visibilityState === 'visible') return;
    var n = new Notification(title, { body: body, icon: '/favicon.ico', tag: 'echat-msg' });
    n.onclick = function() { window.focus(); n.close(); };
    setTimeout(function() { n.close(); }, 8000);
};

// Shows/hides the mobile format bar based on textarea selection.
// Fires only when the has-selection state actually changes to avoid hammering Blazor interop.
window.setupMobileFormatBar = function() {
    var lastHasSelection = false;
    document.addEventListener('selectionchange', function() {
        var ta = document.getElementById('messageInput');
        if (!ta || document.activeElement !== ta) {
            if (lastHasSelection) {
                lastHasSelection = false;
                DotNet.invokeMethodAsync('EChat.UI', 'SetFormatBarVisible', false);
            }
            return;
        }
        var hasSelection = ta.selectionStart !== ta.selectionEnd;
        if (hasSelection !== lastHasSelection) {
            lastHasSelection = hasSelection;
            DotNet.invokeMethodAsync('EChat.UI', 'SetFormatBarVisible', hasSelection);
        }
    });
};

window.downloadBytes = function(filename, bytes) {
    var blob = new Blob([new Uint8Array(bytes)], { type: 'application/zip' });
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url; a.download = filename;
    document.body.appendChild(a); a.click();
    document.body.removeChild(a);
    setTimeout(function() { URL.revokeObjectURL(url); }, 1000);
};
