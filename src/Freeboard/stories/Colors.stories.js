export default {
    title: "Brand/Colors",
    parameters: { layout: "fullscreen" },
};

const swatch = (name, bgClass, hex) => `
    <div class="flex items-center gap-3">
        <div class="h-12 w-12 rounded-md border border-neutral-200 ${bgClass}"></div>
        <div>
            <div class="font-semibold text-neutral-900">${name}</div>
            <div class="text-sm text-neutral-500">${hex}</div>
        </div>
    </div>`;

export const Palette = {
    render: () => `
        <div class="grid grid-cols-1 gap-4 p-6 sm:grid-cols-2">
            ${swatch("brand", "bg-brand", "#215091")}
            ${swatch("brand-hover", "bg-brand-hover", "#1b436f")}
            ${swatch("danger", "bg-danger", "#b00020")}
        </div>`,
};
