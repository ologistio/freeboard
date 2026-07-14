import Alpine from "alpinejs";

// Theme toggle. Writes exactly the key, values, and attribute the pre-paint reader in _Head.cshtml
// consumes: localStorage "fb-theme" of "light" or "dark", applied to <html data-theme>. It writes only
// an explicit theme (never a prefers-color-scheme activation), so the dark-staging contract holds.
Alpine.data("themeToggle", () => ({
    dark: document.documentElement.dataset.theme === "dark",
    toggle() {
        this.dark = !this.dark;
        const theme = this.dark ? "dark" : "light";
        document.documentElement.dataset.theme = theme;
        try {
            localStorage.setItem("fb-theme", theme);
        } catch (e) {
            // Private-mode or storage-disabled: the toggle still themes this page; it just does not persist.
        }
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

window.Alpine = Alpine;
Alpine.start();
