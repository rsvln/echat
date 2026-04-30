window._textareaSetup = false;

window.setupTextareaResize = function () {
    if (window._textareaSetup) return;
    var t = document.getElementById('messageInput');
    if (!t) return;
    window._textareaSetup = true;
    t.addEventListener('input', function () {
        this.style.height = '1px';
        void this.offsetHeight;
        var sh = this.scrollHeight;
        var maxH = 12 * 21;
        var newH = Math.max(44, Math.min(sh, maxH));
        this.style.height = newH + 'px';
        this.style.overflowY = sh > maxH ? 'auto' : 'hidden';
    });
};

window.autoResizeTextarea = function () {
    window.setupTextareaResize();
    var t = document.getElementById('messageInput');
    if (t) {
        t.style.height = '1px';
        void t.offsetHeight;
        var sh = t.scrollHeight;
        var maxH = 12 * 21;
        var newH = Math.max(44, Math.min(sh, maxH));
        t.style.height = newH + 'px';
        t.style.overflowY = sh > maxH ? 'auto' : 'hidden';
    }
};

window.resetTextareaHeight = function () {
    var t = document.getElementById('messageInput');
    if (t) {
        t.style.height = '44px';
        t.style.overflowY = 'hidden';
    } else {
        var el = document.querySelector('.message-input textarea');
        if (el) { el.style.height = 'auto'; el.style.height = el.scrollHeight + 'px'; }
    }
};

window.getMessageInputValue = function () {
    var t = document.getElementById('messageInput');
    return t ? t.value : '';
};

window.clearMessageInput = function () {
    var t = document.getElementById('messageInput');
    if (!t) return;
    t.value = '';
    t.style.height = '44px';
    t.style.overflowY = 'hidden';
};

window.setMessageInputValue = function (text) {
    var t = document.getElementById('messageInput');
    if (!t) return;
    t.value = text;
    t.style.height = 'auto';
    var sh = t.scrollHeight;
    var maxH = 12 * 21;
    t.style.height = Math.max(44, Math.min(sh, maxH)) + 'px';
    t.style.overflowY = sh > maxH ? 'auto' : 'hidden';
};

window.handleMessageInputKey = function (event, sendOnEnter) {
    if (event.key === 'Enter') {
        if (event.shiftKey || (!sendOnEnter && !event.ctrlKey) || (sendOnEnter && event.ctrlKey)) {
            return null;
        }
        event.preventDefault();
        var t = document.getElementById('messageInput');
        var text = t ? t.value : '';
        if (t) { t.value = ''; t.style.height = '44px'; t.style.overflowY = 'hidden'; }
        return text;
    }
    return null;
};