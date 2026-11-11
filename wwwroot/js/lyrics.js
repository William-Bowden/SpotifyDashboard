// Paginated lyrics.
//
// All lines are laid out in one column inside an overflow:hidden container. The
// column is split into pages that each fit the container's height (lines wrap
// differently per screen, so this is measured, not counted), and one page is shown
// at a time by setting scrollTop. Lines outside the page get [data-off] so a line
// that only partly fits never peeks in at the bottom.
//
// The page holding the current synced line is shown automatically, except for a
// while after the user flips pages themselves (swipe, mouse wheel or pager buttons).
//
// Every export returns [page, pageCount] for the .NET side's page indicator.

const USER_FLIP_PAUSE_MS = 8000;
const SWIPE_MIN_PX = 40;
const WHEEL_COOLDOWN_MS = 450;
const FLIP_FADE_MS = 140;

const states = new WeakMap();
let currentContainer = null;

function getState(container, dotnetRef) {
    let st = states.get(container);

    if (st) {
        if (dotnetRef) {
            st.ref = dotnetRef;
        }
        return st;
    }

    st = {
        pages: [{ start: 0, top: 0 }],
        page: 0,
        anchor: -1,      // line the current page is chosen by
        userAt: 0,       // last manual flip
        wheelAt: 0,
        width: 0,
        height: 0,
        flipToken: 0,
        ref: dotnetRef ?? null,
        ro: null
    };

    states.set(container, st);
    attachGestures(container, st);

    if ("ResizeObserver" in window) {
        st.ro = new ResizeObserver(() => {
            if (container.clientWidth === st.width && container.clientHeight === st.height) {
                return;
            }
            relayout(container, st);
            notify(st);
        });
        st.ro.observe(container);
    }

    return st;
}

function attachGestures(container, st) {
    let startX = 0;
    let startY = 0;
    let tracking = false;

    container.addEventListener("touchstart", (e) => {
        tracking = e.touches.length === 1;
        if (tracking) {
            startX = e.touches[0].clientX;
            startY = e.touches[0].clientY;
        }
    }, { passive: true });

    container.addEventListener("touchend", (e) => {
        if (!tracking) {
            return;
        }
        tracking = false;

        const t = e.changedTouches[0];
        const dx = t.clientX - startX;
        const dy = t.clientY - startY;
        const d = Math.abs(dx) > Math.abs(dy) ? dx : dy;

        if (Math.abs(d) >= SWIPE_MIN_PX) {
            // Swipe left or up = next page, like turning a page.
            userFlip(container, st, d < 0 ? 1 : -1, true);
        }
    }, { passive: true });

    container.addEventListener("wheel", (e) => {
        const now = Date.now();
        if (Math.abs(e.deltaY) < 10 || now - st.wheelAt < WHEEL_COOLDOWN_MS) {
            return;
        }
        st.wheelAt = now;
        userFlip(container, st, e.deltaY > 0 ? 1 : -1, true);
    }, { passive: true });
}

function computePages(container, height) {
    const pages = [];
    let pageTop = null;

    for (const line of container.querySelectorAll("[data-index]")) {
        const top = line.offsetTop;
        const bottom = top + line.offsetHeight;

        // Start a new page when this line would overflow the current one.
        // (A single line taller than the container still gets a page to itself.)
        if (pageTop === null || (bottom - pageTop > height && top > pageTop)) {
            pages.push({ start: Number(line.dataset.index), top });
            pageTop = top;
        }
    }

    return pages.length ? pages : [{ start: 0, top: 0 }];
}

function pageOf(pages, lineIndex) {
    let page = 0;
    for (let i = 0; i < pages.length; i++) {
        if (pages[i].start <= lineIndex) {
            page = i;
        } else {
            break;
        }
    }
    return page;
}

function relayout(container, st) {
    st.width = container.clientWidth;
    st.height = container.clientHeight;

    // Extra room after the last line so the last page can be scrolled to its top.
    const spacer = container.querySelector(".lyrics-spacer");
    if (spacer) {
        spacer.style.height = `${st.height}px`;
    }

    st.pages = st.height > 0 ? computePages(container, st.height) : [{ start: 0, top: 0 }];
    st.page = pageOf(st.pages, st.anchor);
    show(container, st, false);
}

function apply(container, st) {
    const page = st.pages[st.page] ?? st.pages[0];
    const next = st.pages[st.page + 1];
    const end = next ? next.start : Infinity;

    container.scrollTop = page.top;

    for (const line of container.querySelectorAll("[data-index]")) {
        const i = Number(line.dataset.index);
        line.toggleAttribute("data-off", i < page.start || i >= end);
    }
}

function show(container, st, animate) {
    const token = ++st.flipToken;

    if (!animate) {
        container.removeAttribute("data-flip");
        apply(container, st);
        return;
    }

    // Quick fade out, swap pages, fade back in (the CSS transition does the fading).
    container.setAttribute("data-flip", "");
    setTimeout(() => {
        if (token !== st.flipToken) {
            return;
        }
        apply(container, st);
        container.removeAttribute("data-flip");
    }, FLIP_FADE_MS);
}

function userFlip(container, st, delta, fromGesture) {
    st.userAt = Date.now();

    const page = Math.min(Math.max(st.page + delta, 0), st.pages.length - 1);
    if (page === st.page) {
        return;
    }

    st.page = page;
    st.anchor = st.pages[page].start;
    show(container, st, true);

    if (fromGesture) {
        notify(st);
    }
}

function info(st) {
    return [st.page, st.pages.length];
}

function notify(st) {
    if (st.ref) {
        st.ref.invokeMethodAsync("OnLyricsPageChanged", st.page, st.pages.length).catch(() => { });
    }
}

/** New lines were rendered: measure pages and show the one holding `lineIndex`. */
export function layout(container, lineIndex, dotnetRef) {
    if (!container) {
        return null;
    }

    // A new track renders a new container; stop observing the old one.
    if (currentContainer && currentContainer !== container) {
        detach(currentContainer);
    }
    currentContainer = container;

    const st = getState(container, dotnetRef);
    st.anchor = lineIndex;
    st.userAt = 0;
    relayout(container, st);
    return info(st);
}

/** The current synced line changed: turn to its page unless the user is browsing. */
export function showLine(container, lineIndex, force) {
    if (!container) {
        return null;
    }

    const st = getState(container);

    if (!force && Date.now() - st.userAt < USER_FLIP_PAUSE_MS) {
        return info(st);
    }

    st.anchor = lineIndex;

    const page = pageOf(st.pages, lineIndex);
    if (page !== st.page) {
        st.page = page;
        show(container, st, true);
    }

    return info(st);
}

/** Pager buttons. */
export function flip(container, delta) {
    if (!container) {
        return null;
    }

    const st = getState(container);
    userFlip(container, st, delta, false);
    return info(st);
}

export function detach(container) {
    const st = container ? states.get(container) : null;
    if (!st) {
        return;
    }

    st.ro?.disconnect();
    st.ref = null;
    states.delete(container);

    if (currentContainer === container) {
        currentContainer = null;
    }
}
