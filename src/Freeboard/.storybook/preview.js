import Alpine from "alpinejs";
import "./tailwind.css";

// Mirror the app runtime so Alpine-driven component stories behave as they do in
// the web UI. Alpine's MutationObserver initialises story DOM as Storybook mounts it.
window.Alpine = Alpine;
Alpine.start();

/** @type { import('@storybook/html-vite').Preview } */
const preview = {
    parameters: {
        layout: "centered",
        controls: { expanded: true },
    },
};

export default preview;
