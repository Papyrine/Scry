// Small helpers the explorer calls from C#. Kept tiny and dependency-free.
window.scry = {
    // Whether the OS currently prefers a dark color scheme (used to resolve the "system" theme).
    systemDark: function () {
        try {
            return window.matchMedia('(prefers-color-scheme: dark)').matches;
        } catch (e) {
            return false;
        }
    },
    // Reflect the chosen theme onto <html data-theme> so CSS color-scheme + light-dark() apply.
    setDataTheme: function (mode) {
        document.documentElement.dataset.theme = mode;
    },
    // Copy text to the clipboard (best-effort; the UI shows its own confirmation).
    copy: function (text) {
        try {
            navigator.clipboard.writeText(text);
        } catch (e) {
            console.warn('scry: copy failed', e);
        }
    }
};
