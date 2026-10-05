(function() {
    "use strict";

    var config = null;
    var pollTimer = null;
    var devicePollTimer = null;
    var statusSource = null;

    /* ── DOM helpers ─────────────────────────────────────── */

    function show(id) {
        var steps = document.querySelectorAll(".step");
        for (var i = 0; i < steps.length; i++) steps[i].classList.remove("active");
        document.getElementById(id).classList.add("active");
    }

    function showError(msg) {
        var box = document.getElementById("error-box");
        box.textContent = msg;
        box.classList.add("visible");
    }

    function el(id) { return document.getElementById(id); }

    function drawQrCode(container, url) {
        var img = document.createElement("img");
        img.src = "/setup/qr?data=" + encodeURIComponent(url);
        img.width = 180;
        img.height = 180;
        img.alt = "QR Code";
        while (container.firstChild) container.removeChild(container.firstChild);
        container.appendChild(img);
    }

    function buildDeviceLink(linkEl, verificationUri, verificationUriComplete) {
        while (linkEl.firstChild) linkEl.removeChild(linkEl.firstChild);
        var anchor = document.createElement("a");
        anchor.href = verificationUriComplete;
        anchor.target = "_blank";
        anchor.rel = "noopener noreferrer";
        anchor.textContent = verificationUri;
        linkEl.appendChild(anchor);
    }

    function buildAuthUrl() {
        var baseUrl = config.auth_base_url.replace(/\/+$/, "")
            + "/protocol/openid-connect/auth";
        var redirectUri = window.location.protocol + "//"
            + window.location.host + "/sso-callback";
        var params = [
            "client_id="              + encodeURIComponent(config.client_id),
            "redirect_uri="           + encodeURIComponent(redirectUri),
            "response_type=code",
            "scope="                  + encodeURIComponent("openid offline_access email profile"),
            "code_challenge="         + encodeURIComponent(config.code_challenge),
            "code_challenge_method=S256",
            "state="                  + encodeURIComponent(config.pkce_state)
        ];
        return baseUrl + "?" + params.join("&");
    }

    /* ── Silent SSO ──────────────────────────────────────── */

    function buildSilentAuthUrl(silentRedirectUri) {
        var baseUrl = config.auth_base_url.replace(/\/+$/, "")
            + "/protocol/openid-connect/auth";
        var params = [
            "client_id="              + encodeURIComponent(config.client_id),
            "redirect_uri="           + encodeURIComponent(silentRedirectUri),
            "response_type=code",
            "scope="                  + encodeURIComponent("openid offline_access email profile"),
            "code_challenge="         + encodeURIComponent(config.code_challenge),
            "code_challenge_method=S256",
            "state="                  + encodeURIComponent(config.pkce_state),
            "prompt=none"
        ];
        return baseUrl + "?" + params.join("&");
    }

    function exchangeCodeSilently(code, state) {
        return fetch("/setup/exchange", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ code: code, state: state })
        }).then(function(r) {
            if (!r.ok) throw new Error("Exchange failed: " + r.status);
            return r.json();
        });
    }

    function trySilentSso() {
        return new Promise(function(resolve) {
            var iframe = document.createElement("iframe");
            iframe.style.display = "none";

            var silentRedirectUri = window.location.protocol + "//"
                + window.location.host + "/setup/silent-sso";
            var authUrl = buildSilentAuthUrl(silentRedirectUri);

            var timeout = setTimeout(function() {
                cleanup();
                resolve({ success: false, reason: "timeout" });
            }, 10000);

            window.addEventListener("message", function handler(event) {
                if (event.origin !== window.location.origin) return;
                if (event.source !== iframe.contentWindow) return;
                window.removeEventListener("message", handler);
                cleanup();

                var url = new URL(event.data);
                var code = url.searchParams.get("code");
                var error = url.searchParams.get("error");

                if (code) {
                    exchangeCodeSilently(code, url.searchParams.get("state"))
                        .then(function() { resolve({ success: true }); })
                        .catch(function() { resolve({ success: false, reason: "exchange_failed" }); });
                } else {
                    resolve({ success: false, reason: error || "no_code" });
                }
            });

            function cleanup() {
                clearTimeout(timeout);
                if (iframe.parentNode) iframe.parentNode.removeChild(iframe);
            }

            document.body.appendChild(iframe);
            iframe.src = authUrl;
        });
    }

    /* ── QR / device code ────────────────────────────────── */

    function showQrReady(data) {
        el("qr-loading").style.display = "none";
        el("qr-error").classList.add("qr-hidden");

        drawQrCode(el("qr-container"), data.verification_uri_complete);
        buildDeviceLink(
            el("device-link"),
            data.verification_uri,
            data.verification_uri_complete
        );
        el("device-code").textContent = data.user_code;
        el("qr-ready").classList.remove("qr-hidden");
    }

    function showQrFailed() {
        el("qr-loading").style.display = "none";
        el("qr-ready").classList.add("qr-hidden");
        el("qr-error").classList.remove("qr-hidden");
    }

    var deviceRefreshTimer = null;

    function startDeviceGrant() {
        if (deviceRefreshTimer) {
            clearTimeout(deviceRefreshTimer);
            deviceRefreshTimer = null;
        }
        if (devicePollTimer) {
            clearInterval(devicePollTimer);
            devicePollTimer = null;
        }

        fetch("/setup/device-code", { method: "POST" })
            .then(function(r) { return r.json(); })
            .then(function(data) {
                if (data.error) {
                    showQrFailed();
                    return;
                }

                showQrReady(data);

                // A device code expires (typically 10 minutes) and a stale QR
                // scans into nothing — mint a fresh one shortly before expiry
                // so the code on screen is always redeemable.
                var expiresIn = Number(data.expires_in) || 600;
                var refreshInMs = Math.max((expiresIn - 30) * 1000, 30000);
                deviceRefreshTimer = setTimeout(startDeviceGrant, refreshInMs);

                // Poll until auth transitions away from Unauthenticated
                devicePollTimer = setInterval(function() {
                    fetch("/setup/status")
                        .then(function(r) { return r.json(); })
                        .then(function(status) {
                            if (status.is_authenticated ||
                                    status.phase !== "Unauthenticated") {
                                clearInterval(devicePollTimer);
                                devicePollTimer = null;
                                if (deviceRefreshTimer) {
                                    clearTimeout(deviceRefreshTimer);
                                    deviceRefreshTimer = null;
                                }
                                show("step-progress");
                                startStatusStream();
                            }
                        })
                        .catch(function() {});
                }, 3000);
            })
            .catch(function() {
                showQrFailed();
            });
    }

    /* ── Status stream ───────────────────────────────────── */

    function startStatusStream() {
        stopStatusStream();

        if (typeof EventSource !== "undefined") {
            statusSource = new EventSource("/setup/status");
            statusSource.onmessage = function(e) {
                try {
                    handleStatusData(JSON.parse(e.data));
                } catch (err) { /* ignore parse errors */ }
            };
            statusSource.onerror = function() {
                statusSource.close();
                statusSource = null;
                pollTimer = setInterval(pollStatus, 2000);
                pollStatus();
            };
        } else {
            pollTimer = setInterval(pollStatus, 2000);
            pollStatus();
        }
    }

    function stopStatusStream() {
        if (statusSource) {
            statusSource.close();
            statusSource = null;
        }
        if (pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    function pollStatus() {
        fetch("/setup/status")
            .then(function(r) { return r.json(); })
            .then(function(data) { handleStatusData(data); })
            .catch(function() { /* network error — keep polling */ });
    }

    function handleStatusData(data) {
        updateProgress(data);

        if (data.phase === "Complete") {
            stopStatusStream();
            show("step-complete");

            // Hand off to the app, never to server_url: the server's own origin
            // serves the API, so sending the user there ends setup on Swagger.
            var appUrl = data.app_url || el("server-url").href;

            if (data.server_url) {
                el("server-url-display").textContent = data.server_url;
            }

            el("server-url").href = appUrl;
            el("redirect-msg").classList.remove("redirect-msg-hidden");
            setTimeout(function() {
                window.location.href = appUrl;
            }, 5000);

        } else if (data.error) {
            el("progress-error").textContent = data.error;
            el("progress-error").classList.add("visible");
            if (data.phase !== "Unauthenticated") {
                el("btn-retry").classList.remove("btn-retry-hidden");
            }
            var spinner = el("step-progress").querySelector(".spinner");
            if (spinner) spinner.style.display = "none";

        } else {
            el("progress-error").classList.remove("visible");
            el("btn-retry").classList.add("btn-retry-hidden");
            var spinner = el("step-progress").querySelector(".spinner");
            if (spinner) spinner.style.display = "inline-block";
        }
    }

    var phases = {
        "Unauthenticated":     ["Waiting for login...",        "Sign in to continue"],
        "Authenticating":      ["Authenticating...",           "Verifying your credentials"],
        "Authenticated":       ["Authenticated",               "Registering server..."],
        "Registering":         ["Registering server...",       "Connecting to NoMercy"],
        "Registered":          ["Server registered",           "Acquiring SSL certificate..."],
        "CertificateAcquired": ["Certificate acquired",        "Finalizing setup..."],
        "Failed":              ["Setup could not finish",      "Something went wrong — you can retry"],
        "Complete":            ["Setup complete!",             "Your server is ready"]
    };

    function updateProgress(data) {
        var info = phases[data.phase] || ["Processing...", "Please wait"];
        el("progress-label").textContent = info[0];
        el("progress-detail").textContent = data.detail || info[1] || "Please wait";
    }

    /* ── Init ────────────────────────────────────────────── */

    // Keycloak only accepts a redirect_uri on *.nomercy.tv, localhost or loopback.
    // The server decides per request host (browser_login_allowed); on any other
    // host the page offers only the device code so it never sends a redirect
    // to an address it does not own.
    function applyLoginMode() {
        var browserLoginAllowed = config.browser_login_allowed !== false;
        el("btn-login").classList.toggle("qr-hidden", !browserLoginAllowed);
        el("login-divider").classList.toggle("qr-hidden", !browserLoginAllowed);
        if (browserLoginAllowed) {
            el("btn-login").href = buildAuthUrl();
        } else {
            el("btn-login").removeAttribute("href");
        }
    }

    function showLoginStep() {
        applyLoginMode();
        show("step-login");
        startDeviceGrant();
    }

    function init() {
        fetch("/setup/config")
            .then(function(r) { return r.json(); })
            .then(function(data) {
                config = data;

                if (data.phase === "Complete") {
                    show("step-complete");
                    return;
                }

                if (data.phase !== "Unauthenticated") {
                    show("step-progress");
                    startStatusStream();
                    return;
                }

                // Unauthenticated: try silent SSO on first boot before showing UI
                if (data.is_first_boot && data.browser_login_allowed !== false) {
                    trySilentSso().then(function(result) {
                        if (result.success) {
                            show("step-progress");
                            startStatusStream();
                        } else {
                            // Re-fetch config to get fresh PKCE params after
                            // the silent attempt consumed the current challenge
                            fetch("/setup/config")
                                .then(function(r) { return r.json(); })
                                .then(function(freshConfig) {
                                    config = freshConfig;
                                    showLoginStep();
                                })
                                .catch(function() { showLoginStep(); });
                        }
                    });
                } else {
                    // If a device code poll is already in flight on the server
                    // (phase would be Authenticating), reuse it via status polling
                    // instead of creating a new device code — prevents Keycloak
                    // rate limiting on page refresh / multiple tabs.
                    if (data.phase === "Authenticating") {
                        el("qr-loading").style.display = "none";
                        el("qr-error").classList.remove("qr-hidden");
                        show("step-progress");
                        startStatusStream();
                    } else {
                        showLoginStep();
                    }
                }
            })
            .catch(function() {
                showError("Failed to load setup configuration");
            });

        // Retry button
        el("btn-retry").addEventListener("click", function() {
            el("btn-retry").classList.add("btn-retry-hidden");
            el("progress-error").classList.remove("visible");
            var spinner = el("step-progress").querySelector(".spinner");
            if (spinner) spinner.style.display = "inline-block";

            fetch("/setup/config")
                .then(function(r) { return r.json(); })
                .then(function(data) {
                    config = data;

                    if (data.phase === "Unauthenticated") {
                        applyLoginMode();
                        show("step-login");
                        return;
                    }

                    show("step-progress");

                    fetch("/setup/retry", { method: "POST" })
                        .then(function(r) { return r.json(); })
                        .then(function(retryData) {
                            if (retryData.status === "unauthenticated") {
                                applyLoginMode();
                                show("step-login");
                                return;
                            }
                            startStatusStream();
                        })
                        .catch(function() {
                            startStatusStream();
                        });
                })
                .catch(function() {
                    show("step-progress");
                    startStatusStream();
                });
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
