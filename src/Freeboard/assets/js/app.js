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
// releases only the inert it applied itself, so a node another overlay still holds inert (the drawer
// inerts the stage below the desktop breakpoint) is left to its owner. The composing component must
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
                // Release only the inert this overlay applied. If a node was already inert when we
                // opened (the mobile drawer inerts the stage), leave it to its owner: writing the
                // captured "true" back could re-inert a node the drawer has since released when the
                // viewport crossed the desktop breakpoint, stranding the topbar and main inert.
                if (state.node && !state.was) {
                    state.node.inert = false;
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
        // One top-level overlay at a time: never open the palette over an open object drawer. The
        // palette inerts only .fb-rail and .fb-stage, not the drawer (the fourth .fb-app sibling), so
        // stacking would leave the drawer focusable behind the palette. This is the single choke point
        // both the document Ctrl-K / "/" listener and the store request() funnel through.
        if (this.$store.palette.open || this.$store.drawer.open) {
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

// The object-detail drawer (O4/A5): the single right-anchored ARIA dialog every list opens its records
// into. It composes overlayFocus for the open/close focus mechanics (capture opener, move focus in, hold
// .fb-rail and .fb-stage inert while open, Escape-to-close with focus restore) and layers only the
// clone-on-open behaviour on top - it re-authors no focus-trap, Escape, or inert code. The record markup
// is server-rendered into a <template> next to each list row; on open the drawer clones the row's template
// into its content slot, so no fetch and no client-built HTML is needed. The list openers live in the
// page's own Alpine scope, so they reach this shell-mounted drawer through the "drawer" store.
Alpine.data("objectDrawer", () => ({
    ...overlayFocus({
        inert: [".fb-rail", ".fb-stage"],
        focus: ".fb-drawer",
        fallback: "main.fb-main",
    }),
    init() {
        // The list opener (a different scope) asks the store to open us; register how.
        this.$store.drawer.onOpen((opener) => this.open(opener));
    },
    open(opener) {
        if (this.$store.drawer.open) {
            return;
        }

        // Clone the opener's server-rendered anatomy template into the content slot. The template is
        // inert markup already in the page GET, so this is a DOM copy, never a fetch or JSON->HTML build.
        const templateId = opener && opener.dataset ? opener.dataset.detailTemplate : null;
        const template = templateId ? document.getElementById(templateId) : null;
        if (!template || !("content" in template)) {
            // No anatomy to clone: never open an empty inert dialog. Fall back to the opener's full-page
            // href (the same target the no-JavaScript path uses); if there is none, abort without inerting.
            if (opener && opener.href) {
                window.location.assign(opener.href);
            }
            return;
        }

        this.$refs.content.replaceChildren();
        this.$refs.content.appendChild(template.content.cloneNode(true));

        this.$store.drawer.open = true;
        this.enterOverlay(opener);
    },
    close() {
        if (!this.$store.drawer.open) {
            return;
        }

        this.$store.drawer.open = false;
        this.exitOverlay();
    },
}));

// Input types that take typed text, where a bare "/" is a character the user means literally.
// Checkboxes, radios, buttons, and selects are not text entry, so the shortcut still opens there.
const TEXT_INPUT_TYPES = new Set([
    "text", "search", "email", "url", "tel", "password", "number",
]);

function isEditable(element) {
    if (!element) {
        return false;
    }

    if (element.isContentEditable) {
        return true;
    }

    const tag = element.tagName;
    if (tag === "TEXTAREA") {
        return true;
    }
    if (tag === "INPUT") {
        const type = (element.getAttribute("type") || "text").toLowerCase();
        return TEXT_INPUT_TYPES.has(type);
    }
    return false;
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

// Couples each list's row openers (in the page scope) to the single shell-mounted object drawer without
// a shared x-data spanning both regions: a row calls request(opener) with its anchor, and the drawer
// registers its open handler here on init. Same coupling pattern as the "palette" store.
Alpine.store("drawer", {
    open: false,
    openHandler: null,
    onOpen(handler) {
        this.openHandler = handler;
    },
    request(opener) {
        if (this.openHandler) {
            this.openHandler(opener);
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
