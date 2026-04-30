// site.js

// Load Prism.js for syntax highlighting
import 'https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-core.min.js';
import 'https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/plugins/autoloader/prism-autoloader.min.js';

// Initialize Prism
document.addEventListener('DOMContentLoaded', function () {
    Prism.highlightAll();
});

// Re-highlight when content changes
window.highlightCode = function() {
    Prism.highlightAll();
};