// Notifies .NET when the page is hidden or shown (screen off, app switched,
// tab backgrounded) so Spotify polling can pause while nobody can see it.

let handler = null;

export function subscribe(dotNetRef) {
    unsubscribe();

    handler = () => {
        dotNetRef.invokeMethodAsync(
            "OnVisibilityChanged",
            document.visibilityState === "visible"
        );
    };

    document.addEventListener("visibilitychange", handler);

    return document.visibilityState === "visible";
}

export function unsubscribe() {
    if (handler) {
        document.removeEventListener("visibilitychange", handler);
        handler = null;
    }
}
