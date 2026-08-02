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
    },
    // The URL fragment, which is where a shared query is carried. A fragment is never sent to the
    // server, so sharing a link cannot log the query into an access log on the way.
    hash: function () {
        return window.location.hash;
    },
    // Rewrite the fragment without navigating and without pushing a history entry — sharing the
    // current query should not put a back-button step between the user and their previous one.
    setHash: function (value) {
        history.replaceState(null, '', value);
        return window.location.href;
    },
    // Save text as a file. The BOM is per-format rather than always on: CSV wants it, because it is
    // what makes Excel read the UTF-8 as UTF-8 rather than as the local codepage (which otherwise
    // mangles any non-ASCII value in an exported column), while a leading U+FEFF is not valid JSON.
    download: function (name, text, type, bom) {
        try {
            const blob = new Blob([bom ? '\ufeff' + text : text], { type: type });
            const url = URL.createObjectURL(blob);
            const link = document.createElement('a');
            link.href = url;
            link.download = name;
            document.body.appendChild(link);
            link.click();
            link.remove();
            URL.revokeObjectURL(url);
        } catch (e) {
            console.warn('scry: download failed', e);
        }
    }
};
