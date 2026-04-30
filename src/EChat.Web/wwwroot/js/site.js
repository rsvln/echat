// site.js
// Utilities for offline text formatting support with local Prism.js

// Language mapping for Prism
var prismLangMap = {
    'csharp': 'csharp', 'cs': 'csharp', 'c#': 'csharp',
    'js': 'javascript', 'javascript': 'javascript',
    'py': 'python', 'python': 'python',
    'java': 'java',
    'html': 'markup', 'xml': 'markup',
    'css': 'css',
    'sql': 'sql',
    'json': 'json',
    'bash': 'bash', 'shell': 'bash',
    'powershell': 'powershell', 'ps1': 'powershell'
};

// Load language support dynamically  
window.loadPrismLanguage = function(language) {
    if (!language) return;
    var prismLang = prismLangMap[language.toLowerCase()] || language;
    if (Prism.languages[prismLang]) return;
    
    var script = document.createElement('script');
    script.src = '/lib/prism/js/languages/prism-' + prismLang + '.min.js';
    script.onload = function() { 
        Prism.highlightAll(); 
    };
    document.head.appendChild(script);
};

// Highlight all code blocks
window.highlightCode = function() {
    if (typeof Prism === 'undefined') return;
    
    // Find all code blocks and load languages
    document.querySelectorAll('pre code[class*="language-"]').forEach(function(code) {
        var cls = code.className || '';
        var langMatch = cls.match(/language-(.+)/);
        if (langMatch) {
            window.loadPrismLanguage(langMatch[1]);
        }
    });
    
    Prism.highlightAll();
};

// Initial highlight on load
window.addEventListener('load', function() {
    setTimeout(window.highlightCode, 500);
});

// Also try after a bit more time
setTimeout(window.highlightCode, 2000);

// Observe DOM changes for code blocks
if (typeof MutationObserver !== 'undefined') {
    var observer = new MutationObserver(function(mutations) {
        var hasCode = false;
        mutations.forEach(function(m) {
            m.addedNodes.forEach(function(n) {
                if (n.querySelectorAll) {
                    var codes = n.querySelectorAll('pre code, code[class*="language-"]');
                    if (codes.length > 0) hasCode = true;
                }
            });
        });
        if (hasCode) {
            setTimeout(window.highlightCode, 200);
        }
    });
    
    document.addEventListener('DOMContentLoaded', function() {
        observer.observe(document.body, { childList: true, subtree: true });
    });
}