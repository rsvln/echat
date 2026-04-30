window.getChatScrollTop = function () {
    var el = document.querySelector('.messages-container');
    return el ? el.scrollTop : 0;
};

window.setChatScrollTop = function (pos) {
    requestAnimationFrame(function () {
        var el = document.querySelector('.messages-container');
        if (el) el.scrollTop = pos;
    });
};

window.scrollChatToBottom = function () {
    requestAnimationFrame(function () {
        var el = document.querySelector('.messages-container');
        if (el) el.scrollTop = el.scrollHeight;
    });
};

window.isChatAtBottom = function () {
    var el = document.querySelector('.messages-container');
    if (!el) return true;
    return el.scrollHeight - el.scrollTop - el.clientHeight < 80;
};