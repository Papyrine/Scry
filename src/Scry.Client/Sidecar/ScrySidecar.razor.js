// Collocated module for the debug sidecar. Loaded by the component itself from
// _content/Scry.Client/Sidecar/ScrySidecar.razor.js, so a consuming app needs no index.html edit.
// Kept tiny and dependency-free.

let listener = null;
let styled = false;

// Parse a "+"-separated shortcut like "Alt+Q" into { alt, ctrl, shift, meta, key }.
// Unparseable input falls back to the default so a typo disables nothing.
function parseShortcut(text) {
    const fallback = { alt: true, ctrl: false, shift: false, meta: false, key: 'q' };
    if (!text) {
        return fallback;
    }

    const result = { alt: false, ctrl: false, shift: false, meta: false, key: null };
    for (const raw of text.split('+')) {
        const token = raw.trim().toLowerCase();
        if (token === 'alt') {
            result.alt = true;
        } else if (token === 'ctrl' || token === 'control') {
            result.ctrl = true;
        } else if (token === 'shift') {
            result.shift = true;
        } else if (token === 'meta' || token === 'cmd') {
            result.meta = true;
        } else if (token.length > 0 && result.key === null) {
            result.key = token;
        } else {
            console.warn('scry-sidecar: unrecognized shortcut, using Alt+Q:', text);
            return fallback;
        }
    }

    if (result.key === null) {
        console.warn('scry-sidecar: shortcut has no key, using Alt+Q:', text);
        return fallback;
    }

    return result;
}

// The stylesheet is injected here rather than shipped as scoped CSS: scoped styles land in the
// consumer's bundled {App}.styles.css, which many hosts never link, and the failure mode would be
// a silently unstyled panel. A link the module adds itself is deterministic on every host.
function ensureStyles() {
    if (styled) {
        return;
    }

    const link = document.createElement('link');
    link.rel = 'stylesheet';
    // The path rather than the full URL: same-origin always, and markup that does not bake in
    // whichever host and port this page happens to be served from.
    link.setAttribute('href', new URL('../scry-sidecar.css', import.meta.url).pathname);
    document.head.appendChild(link);
    styled = true;
}

export function init(component, shortcutText, eagerStyles) {
    // The floating toggle button is visible while the panel is closed, so when it is on the
    // stylesheet has to be there from the start; shortcut-only pages keep the lazy injection.
    if (eagerStyles) {
        ensureStyles();
    }

    const shortcut = parseShortcut(shortcutText);
    listener = event => {
        if (event.altKey !== shortcut.alt
            || event.ctrlKey !== shortcut.ctrl
            || event.shiftKey !== shortcut.shift
            || event.metaKey !== shortcut.meta) {
            return;
        }

        // Match event.key, and for a single letter also event.code — Alt mutates event.key into a
        // different character on some layouts (e.g. Alt+Q is "œ" on a French macOS layout).
        const key = (event.key || '').toLowerCase();
        const byCode = shortcut.key.length === 1 && event.code === 'Key' + shortcut.key.toUpperCase();
        if (key !== shortcut.key && !byCode) {
            return;
        }

        event.preventDefault();
        // Styles are injected on first use rather than at init, so a page whose sidecar is never
        // opened is byte-identical to one without it.
        ensureStyles();
        component.invokeMethodAsync('Toggle');
    };
    document.addEventListener('keydown', listener);
}

// Copy text to the clipboard (best-effort; the UI shows its own confirmation).
export function copy(text) {
    try {
        navigator.clipboard.writeText(text);
    } catch (e) {
        console.warn('scry-sidecar: copy failed', e);
    }
}

// Save bytes as a file. Handed over as base64: that is what crosses the interop boundary without a
// copy per element, and an attachment is arbitrary binary that no text encoding would survive.
export function downloadBytes(name, base64, type) {
    try {
        const binary = atob(base64);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
        }

        const url = URL.createObjectURL(new Blob([bytes], { type: type }));
        const link = document.createElement('a');
        link.href = url;
        link.download = name;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    } catch (e) {
        console.warn('scry-sidecar: download failed', e);
    }
}

export function dispose() {
    if (listener !== null) {
        document.removeEventListener('keydown', listener);
        listener = null;
    }
}
