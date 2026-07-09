export default {
    title: "Components/Badges",
    parameters: { layout: "padded" },
};

export const AllVariants = {
    render: () => `
        <div class="flex flex-wrap items-center gap-2">
            <span class="badge badge-brand">Brand</span>
            <span class="badge badge-neutral">Neutral</span>
            <span class="badge badge-success">Success</span>
            <span class="badge badge-warn">Warning</span>
            <span class="badge badge-danger">Danger</span>
        </div>`,
};
