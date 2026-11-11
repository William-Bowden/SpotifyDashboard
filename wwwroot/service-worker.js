// Service worker: makes the app installable and lets it open without a network.
//
// Strategy: network first, cache as fallback, for the app's own files only.
// - Online, every load gets the latest deploy (no "stuck on an old version" problem,
//   which matters for a phone that leaves the app open for days).
// - Offline, the last good copy of each file is served from the cache.
// - Requests to other origins (Spotify's API, sign-in, album art, lyrics) are never
//   touched: they go straight to the network as if there were no service worker.
//
// Blazor keeps its own integrity-checked cache of the .NET runtime and app assemblies,
// so the large files aren't downloaded again on every load anyway.

const CACHE = "spotify-remote-v1";

// The app shell, cached at install so the app opens offline even before it's been used.
const SHELL = [
    "./",
    "manifest.webmanifest",
    "css/dashboard.css",
    "css/app.css",
    "lib/bootstrap/dist/css/bootstrap.min.css",
    "icon-192.png",
    "icon-512.png",
    "favicon.png"
];

self.addEventListener("install", (event) => {
    event.waitUntil((async () => {
        const cache = await caches.open(CACHE);
        // One missing file shouldn't stop installation.
        await Promise.all(SHELL.map((url) => cache.add(new Request(url, { cache: "reload" })).catch(() => { })));
        await self.skipWaiting();
    })());
});

self.addEventListener("activate", (event) => {
    event.waitUntil((async () => {
        const names = await caches.keys();
        await Promise.all(names.filter((n) => n.startsWith("spotify-remote-") && n !== CACHE).map((n) => caches.delete(n)));
        await self.clients.claim();
    })());
});

self.addEventListener("fetch", (event) => {
    const request = event.request;

    if (request.method !== "GET") {
        return;
    }

    const url = new URL(request.url);
    if (url.origin !== self.location.origin) {
        return; // Spotify, LRCLIB, album art: no interception.
    }

    if (request.mode === "navigate") {
        event.respondWith(handleNavigation(request));
    } else {
        event.respondWith(networkFirst(request));
    }
});

// Every route (/, /callback?code=...) is the same single-page app, so the shell is
// cached under one key instead of one entry per URL.
async function handleNavigation(request) {
    const cache = await caches.open(CACHE);

    try {
        const response = await fetch(request);
        if (response.ok && response.type === "basic") {
            cache.put("./", response.clone());
        }
        return response;
    } catch {
        return (await cache.match("./")) || offlineResponse();
    }
}

async function networkFirst(request) {
    const cache = await caches.open(CACHE);

    try {
        const response = await fetch(request);
        if (response.ok && response.type === "basic") {
            cache.put(request, response.clone());
        }
        return response;
    } catch {
        return (await cache.match(request, { ignoreVary: true })) || offlineResponse();
    }
}

function offlineResponse() {
    return new Response("Offline", { status: 503, headers: { "Content-Type": "text/plain" } });
}
