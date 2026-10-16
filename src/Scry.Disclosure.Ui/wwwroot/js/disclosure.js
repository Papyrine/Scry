// The few things the page needs the browser for directly. A classic script that publishes one
// object, loaded before Blazor starts: the page reads its address and the saved theme from it
// synchronously as it boots, and a browser test replaces `save` to see what a download would have
// written. It holds no logic of the explorer's own — nothing here asks the server anything.
window.disclosure = {
    // Where the page is: the fragment of its address, which is the question it is asking.
    address: function () {
        return location.hash;
    },
    // Asks another question by going to its address. Set on the location rather than pushed onto
    // the history, because that is a navigation the browser itself reports: the same event then
    // follows a link, Back, and this.
    go: function (fragment) {
        location.hash = fragment;
    },
    // Tells the page each time its fragment changes. The page has no routes, only this, and the
    // framework's own navigation says nothing when all that moved is the fragment.
    watch: function (page) {
        window.addEventListener('hashchange', function () {
            page.invokeMethodAsync('Moved', location.hash);
        });
    },
    // Save bytes as a file. They arrive as a Uint8Array, which is what a byte[] crosses the interop
    // boundary as.
    download: function (name, type, bytes) {
        try {
            window.disclosure.save(name, new Blob([bytes], { type: type }));
        } catch (e) {
            console.warn('disclosure: download failed', e);
        }
    },
    // The click a browser turns into a save.
    save: function (name, blob) {
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = name;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    },
    // The theme: "system", "light" or "dark". Kept under the key the query explorer uses, so the two
    // pages of one host agree. Best-effort: a browser that refuses storage shows the system's.
    theme: function () {
        try {
            return localStorage.getItem('scry-theme') || 'system';
        } catch (e) {
            return 'system';
        }
    },
    setTheme: function (theme) {
        document.documentElement.dataset.theme = theme;
        try {
            localStorage.setItem('scry-theme', theme);
        } catch (e) {
        }
    }
};
