// WebAuthn shim, loaded only on the passkey pages. It marshals the public ceremony bytes between the
// browser's navigator.credentials API and the server: the server renders the options JSON and an
// antiforgery token into the page, this script runs the create/get ceremony, then POSTs the resulting
// public-key credential back. The antiforgery token rides the RequestVerificationToken header because
// the submit is a fetch, not a <form>, so the framework's hidden field is absent.
//
// No private key material is ever handled here; the authenticator keeps it. Only the public
// attestation/assertion the browser returns is sent to the server.
(function () {
    "use strict";

    function base64UrlToBytes(value) {
        const padded = value.replace(/-/g, "+").replace(/_/g, "/");
        const binary = atob(padded + "===".slice((padded.length + 3) % 4));
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) {
            bytes[i] = binary.charCodeAt(i);
        }
        return bytes.buffer;
    }

    function bytesToBase64Url(buffer) {
        const bytes = new Uint8Array(buffer);
        let binary = "";
        for (let i = 0; i < bytes.length; i++) {
            binary += String.fromCharCode(bytes[i]);
        }
        return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    }

    // The WebAuthn API needs ArrayBuffers where the JSON options carry base64url strings. Walk the
    // known binary fields and decode them in place.
    function decodeRequestOptions(options, isCreate) {
        options.challenge = base64UrlToBytes(options.challenge);
        if (isCreate && options.user && typeof options.user.id === "string") {
            options.user.id = base64UrlToBytes(options.user.id);
        }
        const list = options.allowCredentials || options.excludeCredentials;
        if (Array.isArray(list)) {
            for (const cred of list) {
                cred.id = base64UrlToBytes(cred.id);
            }
        }
        return options;
    }

    function encodeRegistration(credential) {
        const response = credential.response;
        return {
            id: credential.id,
            rawId: bytesToBase64Url(credential.rawId),
            type: credential.type,
            response: {
                clientDataJSON: bytesToBase64Url(response.clientDataJSON),
                attestationObject: bytesToBase64Url(response.attestationObject)
            }
        };
    }

    function encodeAssertion(credential) {
        const response = credential.response;
        const out = {
            id: credential.id,
            rawId: bytesToBase64Url(credential.rawId),
            type: credential.type,
            response: {
                clientDataJSON: bytesToBase64Url(response.clientDataJSON),
                authenticatorData: bytesToBase64Url(response.authenticatorData),
                signature: bytesToBase64Url(response.signature),
                userHandle: response.userHandle ? bytesToBase64Url(response.userHandle) : null
            }
        };
        return out;
    }

    async function postCredential(action, antiforgeryToken, body) {
        const response = await fetch(action, {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "RequestVerificationToken": antiforgeryToken
            },
            body: JSON.stringify(body)
        });
        // A transparently-followed 302 lands here with redirected=true; follow it.
        if (response.redirected) {
            window.location.assign(response.url);
            return;
        }
        const contentType = response.headers.get("Content-Type") || "";
        if (contentType.indexOf("application/json") !== -1) {
            // Success path: the server returns a JSON { redirect } target to navigate to. This carries
            // any one-time state (e.g. recovery codes) the server stashed and set a cookie for; never
            // reload past it, or the body that mattered is lost on a fresh GET.
            const data = await response.json().catch(() => null);
            if (data && data.redirect) {
                window.location.assign(data.redirect);
                return;
            }
            return;
        }
        // An HTML 200 is the re-rendered page carrying an error to show. Swap the document in place so
        // the user sees that body instead of a blank reload that would discard it.
        if (contentType.indexOf("text/html") !== -1) {
            const html = await response.text();
            document.open();
            document.write(html);
            document.close();
        }
    }

    async function run(root, isCreate) {
        const action = root.getAttribute("data-action");
        const antiforgeryToken = root.getAttribute("data-antiforgery");
        const optionsJson = root.getAttribute("data-options");
        const correlation = root.getAttribute("data-correlation");
        const nicknameInput = root.querySelector("[data-nickname]");

        const options = decodeRequestOptions(JSON.parse(optionsJson), isCreate);
        let credential;
        try {
            credential = isCreate
                ? await navigator.credentials.create({ publicKey: options })
                : await navigator.credentials.get({ publicKey: options });
        } catch (err) {
            // A cancelled or failed ceremony leaves the page as-is so the user can retry.
            return;
        }

        const body = isCreate ? encodeRegistration(credential) : encodeAssertion(credential);
        const payload = { correlation: correlation };
        payload[isCreate ? "attestation" : "assertion"] = JSON.stringify(body);
        if (isCreate && nicknameInput) {
            payload.nickname = nicknameInput.value;
        }
        await postCredential(action, antiforgeryToken, payload);
    }

    function wire() {
        const create = document.querySelector("[data-passkey-register]");
        if (create) {
            const button = create.querySelector("[data-passkey-go]");
            button.addEventListener("click", () => run(create, true));
        }
        const get = document.querySelector("[data-passkey-assert]");
        if (get) {
            const button = get.querySelector("[data-passkey-go]");
            button.addEventListener("click", () => run(get, false));
        }
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", wire);
    } else {
        wire();
    }
})();
