// site.js
// Utilities for offline text formatting support with local Prism.js

// Load language support dynamically
window.loadPrismLanguage = function(language) {
    if (!language || Prism.languages[language]) return;
    var script = document.createElement('script');
    script.src = '/lib/prism/js/languages/prism-' + language + '.min.js';
    document.head.appendChild(script);
};

// Initialize Prism on page load
window.addEventListener('load', function() {
    if (typeof Prism !== 'undefined') {
        Prism.highlightAll();
    }
});

// Re-highlight when content changes (e.g., Blazor component updates)
window.highlightCode = function() {
    if (typeof Prism !== 'undefined') {
        Prism.highlightAll();
    }
};

// Observe DOM changes for Blazor and highlight new code blocks
if (typeof MutationObserver !== 'undefined') {
    var observer = new MutationObserver(function(mutations) {
        mutations.forEach(function(mutation) {
            if (mutation.addedNodes.length > 0) {
                var hasCodeBlock = false;
                mutation.addedNodes.forEach(function(node) {
                    if (node.querySelector && (node.querySelector('pre code') || node.tagName === 'PRE')) {
                        hasCodeBlock = true;
                    }
                });
                if (hasCodeBlock && typeof Prism !== 'undefined') {
                    setTimeout(function() { Prism.highlightAll(); }, 100);
                }
            }
        });
    });
    
    var config = { childList: true, subtree: true };
    document.addEventListener('DOMContentLoaded', function() {
        observer.observe(document.body, config);
    });
}