export default {
    title: "Components/Buttons",
};

export const Primary = { render: () => `<button class="btn-primary">Save changes</button>` };
export const Secondary = { render: () => `<button class="btn-secondary">Cancel</button>` };
export const Danger = { render: () => `<button class="btn-danger">Delete</button>` };
export const Small = { render: () => `<button class="btn-primary btn-sm">Add</button>` };

export const AllVariants = {
    parameters: { layout: "padded" },
    render: () => `
        <div class="flex flex-wrap items-center gap-3">
            <button class="btn-primary">Primary</button>
            <button class="btn-secondary">Secondary</button>
            <button class="btn-danger">Danger</button>
            <button class="btn-primary btn-sm">Small</button>
        </div>`,
};
