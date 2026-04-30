(function () {
    var loaded = false;
    function loadBlazor() {
        if (loaded) return;
        loaded = true;
        var s = document.createElement('script');
        s.src = '_framework/blazor.webview.js';
        document.head.appendChild(s);
    }
    function waitForBridge(attempt) {
        if (window.external && typeof window.external.receiveMessage === 'function') {
            loadBlazor();
        } else if (attempt < 200) {
            setTimeout(function () { waitForBridge(attempt + 1); }, 50);
        } else {
            loadBlazor();
        }
    }
    waitForBridge(0);
})();