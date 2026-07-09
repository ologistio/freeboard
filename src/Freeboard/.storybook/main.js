import tailwindcss from "@tailwindcss/vite";

/** @type { import('@storybook/html-vite').StorybookConfig } */
const config = {
    framework: "@storybook/html-vite",
    stories: ["../stories/**/*.mdx", "../stories/**/*.stories.@(js|ts)"],
    addons: ["@storybook/addon-docs"],
    // Compile the design-system CSS with the same Tailwind v4 pipeline the app uses.
    async viteFinal(config) {
        const { mergeConfig } = await import("vite");
        return mergeConfig(config, { plugins: [tailwindcss()] });
    },
};

export default config;
