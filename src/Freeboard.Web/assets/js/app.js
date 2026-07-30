import Alpine from "alpinejs";

// Tab groups on a documentation page. Selection is held per group name, not per widget, so
// every tab strip in a group moves together: pick GitOps in one step and the rest of the page
// follows. Markup comes from the tabs custom container (see Docs/DocsTabs.cs).
Alpine.store("docTabs", {
    picked: {},
    current(group, keys) {
        const key = this.picked[group];
        return keys.includes(key) ? key : keys[0];
    },
});

Alpine.data("fbTabs", (group, keys) => ({
    on(key) {
        return this.$store.docTabs.current(group, keys) === key;
    },
    pick(key) {
        this.$store.docTabs.picked[group] = key;
    },
    // Arrow, Home and End keys move the selection and the focus together, which is how a
    // tablist is expected to behave once one of its tabs holds the focus.
    step(event) {
        const move = { ArrowRight: 1, ArrowLeft: -1, Home: "first", End: "last" }[event.key];
        if (move === undefined) {
            return;
        }

        event.preventDefault();
        const from = keys.indexOf(this.$store.docTabs.current(group, keys));
        const to = move === "first" ? 0
            : move === "last" ? keys.length - 1
                : (from + move + keys.length) % keys.length;
        this.pick(keys[to]);
        event.target.parentElement.children[to].focus();
    },
}));

window.Alpine = Alpine;
Alpine.start();
