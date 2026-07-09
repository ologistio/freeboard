export default {
    title: "Components/Form controls",
    parameters: { layout: "padded" },
};

export const TextField = {
    render: () => `
        <div class="w-80">
            <label class="form-label" for="email">Email</label>
            <input class="form-input" id="email" type="email" placeholder="you@example.com" />
        </div>`,
};

export const Notice = {
    render: () => `
        <div class="w-80 notice">We sent you a sign-in link. Check your inbox.</div>`,
};

export const NoticeError = {
    render: () => `
        <div class="w-80 notice-error">That link has expired. Request a new one.</div>`,
};
