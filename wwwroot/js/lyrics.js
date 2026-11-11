// Keeps the current synced-lyrics line centred in its scroll container.
// Auto-scroll pauses for a few seconds after the user scrolls or touches the lyrics,
// so it doesn't fight them.

const USER_SCROLL_PAUSE_MS = 4000;
const lastUserInteraction = new WeakMap();

function attach(container) {
    if (lastUserInteraction.has(container)) {
        return;
    }

    lastUserInteraction.set(container, 0);

    const mark = () => lastUserInteraction.set(container, Date.now());

    container.addEventListener("wheel", mark, { passive: true });
    container.addEventListener("touchstart", mark, { passive: true });
    container.addEventListener("touchmove", mark, { passive: true });
    container.addEventListener("mousedown", mark);
}

export function scrollToLine(container, index, force) {
    if (!container) {
        return;
    }

    attach(container);

    // Room above the first and below the last line so either can reach the centre.
    // (CSS % padding follows the width, not the height, so size it here.)
    const pad = `${Math.round(container.clientHeight * 0.42)}px`;
    if (container.style.paddingTop !== pad) {
        container.style.paddingTop = pad;
        container.style.paddingBottom = pad;
    }

    if (!force && Date.now() - lastUserInteraction.get(container) < USER_SCROLL_PAUSE_MS) {
        return;
    }

    const behavior = force ? "auto" : "smooth";
    const line = container.querySelector(`[data-index="${index}"]`);

    if (!line) {
        container.scrollTo({ top: 0, behavior });
        return;
    }

    // The container is position: relative, so offsetTop is relative to it.
    const top = line.offsetTop - container.clientHeight / 2 + line.clientHeight / 2;

    container.scrollTo({ top: Math.max(0, top), behavior });
}
