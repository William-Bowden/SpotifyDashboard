
const TOKEN_KEY = "spotify_dashboard_tokens";
const VERIFIER_KEY = "spotify_code_verifier";
const STATE_KEY = "spotify_oauth_state";

function randomBase64Url(byteLength) {
    const bytes = crypto.getRandomValues(
        new Uint8Array(byteLength)
    );

    let binary = "";

    for (const byte of bytes) {
        binary += String.fromCharCode(byte);
    }

    return btoa(binary)
        .replace(/\+/g, "-")
        .replace(/\//g, "_")
        .replace(/=+$/, "");
}

async function createCodeChallenge(verifier) {
    const data = new TextEncoder().encode(verifier);

    const digest = await crypto.subtle.digest(
        "SHA-256",
        data
    );

    const bytes = new Uint8Array(digest);

    let binary = "";

    for (const byte of bytes) {
        binary += String.fromCharCode(byte);
    }

    return btoa(binary)
        .replace(/\+/g, "-")
        .replace(/\//g, "_")
        .replace(/=+$/, "");
}

export async function beginLogin(
    clientId,
    redirectUri,
    scopes
) {
    const verifier = randomBase64Url(64);
    const challenge = await createCodeChallenge(verifier);
    const state = randomBase64Url(32);

    sessionStorage.setItem(VERIFIER_KEY, verifier);
    sessionStorage.setItem(STATE_KEY, state);

    const params = new URLSearchParams({
        response_type: "code",
        client_id: clientId,
        redirect_uri: redirectUri,
        scope: scopes,
        state: state,
        code_challenge_method: "S256",
        code_challenge: challenge
    });

    window.location.assign(
        `https://accounts.spotify.com/authorize?${params}`
    );
}

export async function completeLogin(
    clientId,
    redirectUri
) {
    const params = new URLSearchParams(
        window.location.search
    );

    const error = params.get("error");

    if (error) {
        window.history.replaceState(
            {},
            document.title,
            window.location.pathname
        );

        throw new Error(
            params.get("error_description") || error
        );
    }

    const code = params.get("code");
    const returnedState = params.get("state");

    if (!code) {
        throw new Error("No authorization code received.");
    }

    const expectedState = sessionStorage.getItem(STATE_KEY);
    const verifier = sessionStorage.getItem(VERIFIER_KEY);

    if (!expectedState || returnedState !== expectedState) {
        throw new Error("OAuth state validation failed.");
    }

    if (!verifier) {
        throw new Error("Missing PKCE code verifier.");
    }

    const response = await fetch(
        "https://accounts.spotify.com/api/token",
        {
            method: "POST",
            headers: {
                "Content-Type": "application/x-www-form-urlencoded"
            },
            body: new URLSearchParams({
                client_id: clientId,
                grant_type: "authorization_code",
                code: code,
                redirect_uri: redirectUri,
                code_verifier: verifier
            })
        }
    );

    const data = await response.json();

    if (!response.ok) {
        throw new Error(
            data.error_description ||
            data.error ||
            "Spotify token exchange failed."
        );
    }

    saveTokens(data);

    sessionStorage.removeItem(VERIFIER_KEY);
    sessionStorage.removeItem(STATE_KEY);

    window.history.replaceState(
        {},
        document.title,
        window.location.pathname
    );
}

function saveTokens(data) {
    const existing = JSON.parse(
        localStorage.getItem(TOKEN_KEY) || "null"
    );

    const tokens = {
        accessToken: data.access_token,
        refreshToken: data.refresh_token ||
            existing?.refreshToken,
        expiresAt: Date.now() + data.expires_in * 1000
    };

    localStorage.setItem(
        TOKEN_KEY,
        JSON.stringify(tokens)
    );
}

export async function getAccessToken(clientId) {
    const stored = JSON.parse(
        localStorage.getItem(TOKEN_KEY) || "null"
    );

    if (!stored) {
        return null;
    }

    if (Date.now() < stored.expiresAt - 60000) {
        return stored.accessToken;
    }

    if (!stored.refreshToken) {
        localStorage.removeItem(TOKEN_KEY);
        return null;
    }

    const response = await fetch(
        "https://accounts.spotify.com/api/token",
        {
            method: "POST",
            headers: {
                "Content-Type": "application/x-www-form-urlencoded"
            },
            body: new URLSearchParams({
                client_id: clientId,
                grant_type: "refresh_token",
                refresh_token: stored.refreshToken
            })
        }
    );

    const data = await response.json();

    if (!response.ok) {
        if (data.error === "invalid_grant") {
            localStorage.removeItem(TOKEN_KEY);
            return null;
        }

        throw new Error(
            data.error_description ||
            "Spotify token refresh failed."
        );
    }

    saveTokens(data);

    const updated = JSON.parse(
        localStorage.getItem(TOKEN_KEY)
    );

    return updated.accessToken;
}

export function logout() {
    localStorage.removeItem(TOKEN_KEY);
    sessionStorage.removeItem(VERIFIER_KEY);
    sessionStorage.removeItem(STATE_KEY);
}
