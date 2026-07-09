import Alpine from "alpinejs";
import "./tailwind.css";
// Assignment widget behaviour (window.ua* handlers used by its inline markup).
import "../stories/_assignment.js";

// Mirror the app runtime so Alpine-driven component stories behave as they do in
// the web UI. Alpine's MutationObserver initialises story DOM as Storybook mounts it.
window.Alpine = Alpine;
Alpine.start();

/** @type { import('@storybook/html-vite').Preview } */
const preview = {
    parameters: {
        layout: "centered",
        controls: { expanded: true },
        options: {
            storySort: {
                order: [
                    "Overview",
                    "Foundations",
                    ["UX philosophy", "UX rules"],
                    "Brand",
                    "Components",
                    "Compositions",
                ],
            },
        },
    },
};

export default preview;
