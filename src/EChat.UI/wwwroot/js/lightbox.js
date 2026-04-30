window.initLightboxZoom = function () {
    var img = document.getElementById('echat-lightbox-img');
    if (!img || img._lbInit) return;
    img._lbInit = true;
    var scale = 1, panX = 0, panY = 0;
    var lastDist = 0;
    var startPanX = 0, startPanY = 0, startTouchX = 0, startTouchY = 0;
    var lastTap = 0;
    var isPinching = false;

    function dist(t) {
        var dx = t[0].clientX - t[1].clientX, dy = t[0].clientY - t[1].clientY;
        return Math.sqrt(dx * dx + dy * dy);
    }
    function apply() {
        img.style.transform = 'translate(' + panX + 'px,' + panY + 'px) scale(' + scale + ')';
        img.style.maxWidth = scale > 1 ? 'none' : '';
        img.style.maxHeight = scale > 1 ? 'none' : '';
        img.style.cursor = scale > 1 ? 'grab' : 'default';
    }

    img.addEventListener('touchstart', function (e) {
        if (e.touches.length === 2) {
            isPinching = true;
            lastDist = dist(e.touches);
        } else if (e.touches.length === 1) {
            isPinching = false;
            var now = Date.now();
            if (now - lastTap < 300) {
                if (scale > 1.05) { scale = 1; panX = 0; panY = 0; }
                else { scale = 1.5; panX = 0; panY = 0; }
                apply();
                lastTap = 0;
                return;
            }
            lastTap = now;
            if (scale > 1) {
                startTouchX = e.touches[0].clientX;
                startTouchY = e.touches[0].clientY;
                startPanX = panX;
                startPanY = panY;
            }
        }
    }, { passive: true });

    img.addEventListener('touchmove', function (e) {
        e.stopPropagation();
        if (e.touches.length === 2) {
            e.preventDefault();
            var d = dist(e.touches);
            if (lastDist > 0) {
                var ratio = d / lastDist;
                var newScale = scale * ratio;
                var change = (newScale - scale) / scale;
                var maxStep = 0.08;
                change = Math.max(-maxStep, Math.min(maxStep, change));
                newScale = scale * (1 + change);
                newScale = Math.min(Math.max(newScale, 1), 3);
                scale = newScale;
            }
            lastDist = d;
            apply();
        } else if (e.touches.length === 1 && scale > 1 && !isPinching) {
            e.preventDefault();
            panX = startPanX + (e.touches[0].clientX - startTouchX);
            panY = startPanY + (e.touches[0].clientY - startTouchY);
            apply();
        }
    }, { passive: false });

    img.addEventListener('touchend', function (e) {
        if (e.touches.length < 2) {
            isPinching = false;
            lastDist = 0;
        }
        if (e.touches.length === 0 && scale < 1.05) {
            scale = 1; panX = 0; panY = 0; apply();
        }
    }, { passive: true });

    img._lbReset = function () {
        scale = 1; panX = 0; panY = 0; img._lbInit = false; apply();
    };
};

window.resetLightboxZoom = function () {
    var img = document.getElementById('echat-lightbox-img');
    if (img && img._lbReset) { img._lbReset(); return true; }
    return false;
};

window.isLightboxZoomed = function () {
    var img = document.getElementById('echat-lightbox-img');
    if (!img || !img._lbInit) return false;
    var t = img.style.transform;
    return t && t !== '' && t !== 'translate(0px,0px) scale(1)';
};