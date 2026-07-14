import Alpine from "alpinejs";

// True only when the element is laid out and not visibility:hidden, so it can actually take focus.
// offsetParent is null for display:none or a detached node; visibility:hidden keeps an offsetParent but
// is unfocusable, so it is checked separately. Used to avoid restoring focus to a hidden opener.
function isVisible(element) {
    if (!element || !element.isConnected || element.offsetParent === null) {
        return false;
    }

    return getComputedStyle(element).visibility !== "hidden";
}

// Reusable focus-overlay behaviour for modal surfaces, composed by spreading it into an Alpine data
// object. It owns only: capturing the opener, moving focus in on open, holding caller-named background
// nodes inert while open, and closing on Escape with focus restore. It deliberately stops the Escape it
// handles from propagating, so one keypress closes only the topmost overlay and never also dismisses an
// overlay behind it (e.g. the mobile nav drawer), which would restore focus to a hidden control. It owns
// no result/filter/arrow/active-descendant logic - the composing component layers that on top - and it
// captures each inert node's prior state so releasing it never clobbers an overlay still holding it
// inert (the drawer already inerts the stage below the desktop breakpoint). The composing component must
// supply a close() method, called from the Escape handler. On close it restores focus to the captured
// opener when that opener is still visible; otherwise to the caller's fallback control when the fallback
// is itself visible; failing both, to the main landmark - so focus never lands on a hidden control or body.
function overlayFocus({ inert = [], focus, fallback }) {
    return {
        overlayOpener: null,
        overlayInert: null,
        enterOverlay(opener) {
            this.overlayOpener = opener || null;
            this.overlayInert = inert.map((selector) => {
                const node = document.querySelector(selector);
                return { node, was: node ? node.inert : false };
            });
            for (const state of this.overlayInert) {
                if (state.node) {
                    state.node.inert = true;
                }
            }

            this.$nextTick(() => {
                const target = focus ? this.$root.querySelector(focus) : null;
                if (target) {
                    target.focus();
                }
            });
        },
        exitOverlay() {
            for (const state of this.overlayInert || []) {
                if (state.node) {
                    state.node.inert = state.was;
                }
            }

            this.overlayInert = null;
            this.$nextTick(() => {
                // Restore to the opener when it is still visible, else to the caller's fallback control.
                // Guard the fallback's own visibility too: a hidden fallback must not strand focus on
                // <body>, so drop to the always-present main landmark as a last resort.
                const fallbackNode = fallback ? document.querySelector(fallback) : null;
                const target = isVisible(this.overlayOpener)
                    ? this.overlayOpener
                    : isVisible(fallbackNode)
                        ? fallbackNode
                        : document.querySelector("main.fb-main");
                if (target) {
                    target.focus();
                }
            });
        },
        overlayEscape(event) {
            // Consume the Escape so it closes only this overlay, not one behind it.
            event.stopPropagation();
            event.preventDefault();
            this.close();
        },
    };
}

// Theme toggle. Reads and flips the shared "theme" store, so a palette-driven theme change keeps the
// topbar icon/label/aria-pressed in sync (both surfaces observe the one store).
Alpine.data("themeToggle", () => ({
    get dark() {
        return this.$store.theme.dark;
    },
    toggle() {
        this.$store.theme.toggle();
    },
}));

// The mobile nav drawer (A5). While open the layout holds the background stage inert, so focus cannot
// land on the obscured page; Escape (bound at window scope on the frame) closes it and restores focus
// to the opener. The slide is a CSS transform transition, neutralised by the global reduced-motion rule.
Alpine.data("railDrawer", () => ({
    open: false,
    opener: null,
    desktop: null,
    onDesktop: null,
    init() {
        // At the desktop breakpoint both drawer controls are hidden by CSS. If the viewport grows past
        // it while the drawer is open, force it closed so the stage cannot be left inert (:inert="open")
        // with no control to dismiss it.
        this.desktop = window.matchMedia("(min-width: 1024px)");
        this.onDesktop = (event) => {
            if (event.matches) {
                this.open = false;
            }
        };
        this.desktop.addEventListener("change", this.onDesktop);
    },
    destroy() {
        if (this.desktop && this.onDesktop) {
            this.desktop.removeEventListener("change", this.onDesktop);
        }
    },
    show(event) {
        this.opener = (event && event.currentTarget) || null;
        this.open = true;
        this.$nextTick(() => {
            const first = this.$refs.rail && this.$refs.rail.querySelector("a, button");
            if (first) {
                first.focus();
            }
        });
    },
    hide() {
        if (!this.open) {
            return;
        }

        this.open = false;
        this.$nextTick(() => {
            if (this.opener) {
                this.opener.focus();
            }
        });
    },
}));

// The account popover. A menu, not a drawer: Escape and outside-click close it and restore focus to the
// avatar, but it needs no focus trap (A5 governs drawers/dialogs, not menus).
Alpine.data("accountMenu", () => ({
    open: false,
    toggle() {
        this.open = !this.open;
    },
    close() {
        if (!this.open) {
            return;
        }

        this.open = false;
        this.$nextTick(() => {
            if (this.$refs.opener) {
                this.$refs.opener.focus();
            }
        });
    },
}));

// The command palette (N7): the single search-or-ask surface. An ARIA combobox over a listbox - the
// input keeps DOM focus the whole time and aria-activedescendant tracks the highlight; focus never moves
// onto an option. It composes overlayFocus for the open/close focus mechanics and holds both background
// siblings (.fb-rail and .fb-stage) inert while open, so the rail (its nav links and this palette's own
// opener), the topbar, and the main content are all unreachable. The Page options are server-rendered
// from the gated nav catalog; one Command option toggles the theme. The rail opener lives in another
// Alpine scope, so it and the palette are coupled through the "palette" store.
Alpine.data("commandPalette", () => ({
    ...overlayFocus({
        inert: [".fb-rail", ".fb-stage"],
        focus: ".fb-palinput",
        // Below the desktop breakpoint with the drawer closed, the rail opener is hidden; fall back to
        // the topbar drawer toggle, which is the visible control at that breakpoint.
        fallback: ".fb-topbar .fb-drawer-toggle",
    }),
    query: "",
    activeId: "",
    onDocumentKeydown: null,
    init() {
        // The rail opener (a different scope) asks the store to open us; register how.
        this.$store.palette.onOpen(() => this.open());

        // Open from anywhere on Ctrl-K / Cmd-K / "/". "/" is ignored while a text field or contenteditable
        // is focused so it stays typeable there; the combos open regardless. preventDefault stops the
        // browser default (Cmd-K focus bar, "/" quick-find).
        this.onDocumentKeydown = (event) => {
            if (this.$store.palette.open) {
                return;
            }

            const key = event.key;
            const combo = (event.ctrlKey || event.metaKey) && (key === "k" || key === "K");
            const slash = key === "/" && !isEditable(event.target);
            if (!combo && !slash) {
                return;
            }

            event.preventDefault();
            this.open();
        };
        document.addEventListener("keydown", this.onDocumentKeydown);

        this.$watch("query", () => this.refilter());
    },
    destroy() {
        if (this.onDocumentKeydown) {
            document.removeEventListener("keydown", this.onDocumentKeydown);
        }
    },
    open() {
        if (this.$store.palette.open) {
            return;
        }

        this.query = "";
        this.$store.palette.open = true;
        // Capture the rail entry as the opener whether we opened by click or key. On close, focus returns
        // to it when it is visible, and otherwise to a visible fallback (the primitive never lands focus on
        // a hidden control - the rail entry is hidden below the desktop breakpoint with the drawer closed).
        this.enterOverlay(document.querySelector(".fb-search-entry"));
        this.refilter();
    },
    close() {
        if (!this.$store.palette.open) {
            return;
        }

        this.$store.palette.open = false;
        this.exitOverlay();
    },
    options() {
        return Array.from(this.$refs.list.querySelectorAll("li[role=option]"));
    },
    visibleOptions() {
        return this.options().filter((option) => this.optionVisible(option));
    },
    optionVisible(option) {
        const q = this.query.trim().toLowerCase();
        return q === "" || option.dataset.label.includes(q);
    },
    refilter() {
        // The first match becomes the highlight; an empty result clears it.
        const visible = this.visibleOptions();
        this.activeId = visible.length ? visible[0].id : "";
    },
    move(delta) {
        const visible = this.visibleOptions();
        if (!visible.length) {
            return;
        }

        const current = visible.findIndex((option) => option.id === this.activeId);
        const next = current < 0
            ? (delta > 0 ? 0 : visible.length - 1)
            : (current + delta + visible.length) % visible.length;
        this.activeId = visible[next].id;
        visible[next].scrollIntoView({ block: "nearest" });
    },
    run(option) {
        const target = option || document.getElementById(this.activeId);
        if (!target) {
            return;
        }

        if (target.dataset.kind === "command" && target.dataset.command === "toggle-theme") {
            this.$store.theme.toggle();
            this.close();
            return;
        }

        const route = target.dataset.route;
        if (route) {
            // Close through the primitive first so focus is restored uniformly, then navigate. A
            // cross-page route unloads anyway; the close matters for a same-route result.
            this.close();
            window.location.href = route;
        }
    },
}));

function isEditable(element) {
    if (!element) {
        return false;
    }

    if (element.isContentEditable) {
        return true;
    }

    const tag = element.tagName;
    return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
}

// Couples the rail opener (in the rail scope) to the sibling palette without a shared x-data spanning
// both regions: the opener flips `open`, and the palette registers its open handler here on init.
Alpine.store("palette", {
    open: false,
    opener: null,
    onOpen(handler) {
        this.opener = handler;
    },
    request() {
        if (this.opener) {
            this.opener();
        }
    },
});

// The one theme source of truth, so the topbar toggle and the palette command never desync. set() writes
// the exact key, values, and attribute the pre-paint reader in _Head.cshtml consumes (localStorage
// "fb-theme" of "light" or "dark", applied to <html data-theme>) and never a prefers-color-scheme
// activation, so the dark-staging contract holds. dark is reactive, so anything bound to it updates.
Alpine.store("theme", {
    dark: document.documentElement.dataset.theme === "dark",
    set(theme) {
        this.dark = theme === "dark";
        document.documentElement.dataset.theme = theme;
        try {
            localStorage.setItem("fb-theme", theme);
        } catch (e) {
            // Private-mode or storage-disabled: the change still themes this page; it just does not persist.
        }
    },
    toggle() {
        this.set(this.dark ? "light" : "dark");
    },
});

window.Alpine = Alpine;
Alpine.start();
