// Small helpers the explorer calls from C#. Kept tiny and dependency-free. Nothing here touches
// Monaco or the language services: the editor is a BlazorMonaco component and every language feature
// runs in C#, so this file stays browser plumbing only.
let dotNet = null;
let shortcutListener = null;
// Detachers for the pane-resizer pointerdown listeners.
const pointerTrackers = [];

window.scry = {
    // The callback hub every document-level event is routed back through.
    init: function (dotNetRef) {
        dotNet = dotNetRef;
        // What the explorer remembers is written on a debounce, and a page that closed inside that
        // window lost its last edit. pagehide is the last event a closing, reloading, or navigating
        // page reliably sees, and the call is synchronous: the runtime is single-threaded, so it has
        // finished writing before the page goes.
        window.addEventListener('pagehide', () => {
            try {
                dotNet.invokeMethod('OnFlush');
            } catch (e) {
                console.warn('scry: flush failed', e);
            }
        });
    },
    // Turn off Monaco's word-based suggestions, so the completion dropdown offers the allow-listed
    // schema or nothing rather than mixing in the words already sitting in the editor. Set from here
    // rather than through the editor's construction options because Monaco reads a string enum
    // ('off' | 'currentDocument' | ...) and BlazorMonaco types the option as a bool, which Monaco's
    // validator discards in favour of the default — leaving the provider quietly on.
    disableWordSuggestions: function () {
        for (const editor of monaco.editor.getEditors()) {
            editor.updateOptions({ wordBasedSuggestions: 'off' });
        }
    },
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
    // Copy text to the clipboard, answering whether it landed. writeText returns a promise, and its
    // rejection — a document without focus, a permission refused — is the failure a try/catch never
    // saw: C# showed "Copied" for text the clipboard did not hold.
    copy: function (text) {
        const failed = e => {
            console.warn('scry: copy failed', e);
            return false;
        };
        try {
            return navigator.clipboard.writeText(text).then(() => true, failed);
        } catch (e) {
            return Promise.resolve(failed(e));
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
            window.scry.save(name, new Blob([bom ? '\ufeff' + text : text], { type: type }));
        } catch (e) {
            console.warn('scry: download failed', e);
        }
    },
    // Save an attachment's bytes as a file. Handed over as base64 rather than as a byte array: that
    // is what crosses the interop boundary without a copy per element, and an attachment is arbitrary
    // binary that no text encoding would survive.
    downloadBytes: function (name, base64, type) {
        try {
            const binary = atob(base64);
            const bytes = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++) {
                bytes[i] = binary.charCodeAt(i);
            }

            window.scry.save(name, new Blob([bytes], { type: type }));
        } catch (e) {
            console.warn('scry: download failed', e);
        }
    },
    // The click a browser turns into a save. Shared by both downloads so a file arrives the same way
    // whatever produced it.
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
    // localStorage behind the module seam, so C# owns the namespacing and the policy. Every read is
    // best-effort: a browser with storage disabled, or a private window that refuses it, degrades to
    // an explorer that simply remembers nothing.
    storageGet: function (key) {
        try {
            return localStorage.getItem(key);
        } catch {
            return null;
        }
    },
    // Returns {ok, error?} as JSON: a full quota (and a privacy mode that refuses writes) surfaces as
    // a result C# can report as a boolean rather than as an interop exception.
    storageSet: function (key, value) {
        try {
            localStorage.setItem(key, value);
            return JSON.stringify({ ok: true });
        } catch (e) {
            return JSON.stringify({ ok: false, error: String(e?.message ?? e) });
        }
    },
    storageRemove: function (key) {
        try {
            localStorage.removeItem(key);
        } catch {
            // Nothing to remove when storage itself is unavailable.
        }
    },
    storageKeys: function (prefix) {
        try {
            return Object.keys(localStorage).filter(key => key.startsWith(prefix));
        } catch {
            return [];
        }
    },
    focusElement: function (selector) {
        document.querySelector(selector)?.focus();
    },
    // Document-level shortcuts for the commands that live outside the editor, which Monaco's own
    // keybindings therefore cannot carry. Entries are {id, key, ctrl, shift, alt, meta}, matched on
    // event.key case-insensitively with an exact modifier match so Ctrl+K does not answer Ctrl+Shift+K.
    registerGlobalShortcuts: function (jsonArray) {
        const shortcuts = JSON.parse(jsonArray);
        if (shortcutListener) {
            document.removeEventListener('keydown', shortcutListener);
        }

        shortcutListener = event => {
            for (const shortcut of shortcuts) {
                if (event.key?.toLowerCase() === shortcut.key.toLowerCase() &&
                    event.ctrlKey === shortcut.ctrl &&
                    event.shiftKey === shortcut.shift &&
                    event.altKey === shortcut.alt &&
                    event.metaKey === shortcut.meta) {
                    event.preventDefault();
                    dotNet.invokeMethodAsync('OnGlobalShortcut', shortcut.id);
                    return;
                }
            }
        };
        document.addEventListener('keydown', shortcutListener);
    },
    // Pane-resize dragging on a drag-bar element. While a pointer is captured, every move reports the
    // pointer's fractional position within the bar's parent (and the parent's size on that axis, so C#
    // can apply pixel thresholds) through the callback hub.
    trackPointer: function (elementId, resizerId, direction) {
        const element = document.getElementById(elementId);
        if (!element) {
            return;
        }

        const onDown = down => {
            down.preventDefault();
            element.setPointerCapture(down.pointerId);

            // Pointer moves arrive faster than frames, and every one that reaches .NET re-renders the
            // whole explorer. So only the last position of a frame is sent, and none is sent while a
            // previous call is still out. The layout read moves in here with it, once per frame
            // instead of once per move.
            let position = null;
            let frame = 0;
            let pending = false;

            const send = () => {
                frame = 0;
                if (pending || position === null) {
                    return;
                }

                const rect = element.parentElement.getBoundingClientRect();
                const size = direction === 'x' ? rect.width : rect.height;
                const origin = direction === 'x' ? rect.left : rect.top;
                const offset = position - origin;
                position = null;
                if (size <= 0) {
                    return;
                }

                pending = true;
                dotNet.invokeMethodAsync('OnPaneResize', resizerId, Math.min(Math.max(offset / size, 0), 1), size)
                    .finally(() => {
                        pending = false;
                        // A move that arrived while the call was out still has to land.
                        if (position !== null && frame === 0) {
                            frame = requestAnimationFrame(send);
                        }
                    });
            };

            const onMove = move => {
                position = direction === 'x' ? move.clientX : move.clientY;
                if (frame === 0) {
                    frame = requestAnimationFrame(send);
                }
            };
            const stop = up => {
                element.releasePointerCapture(up.pointerId);
                element.removeEventListener('pointermove', onMove);
                element.removeEventListener('pointerup', stop);
                element.removeEventListener('pointercancel', stop);
                if (frame !== 0) {
                    cancelAnimationFrame(frame);
                    frame = 0;
                }

                // Where the drag ended is the one position that must not be lost to the coalescing.
                send();
            };
            element.addEventListener('pointermove', onMove);
            element.addEventListener('pointerup', stop);
            element.addEventListener('pointercancel', stop);
        };
        element.addEventListener('pointerdown', onDown);
        pointerTrackers.push(() => element.removeEventListener('pointerdown', onDown));
    }
};
