(() => {
    const topbar = document.querySelector('.player-topbar');
    const view = topbar?.parentElement;
    if (!topbar || !view) return;

    let frame = 0;
    const syncReservedSpace = () => {
        cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => {
            const height = Math.ceil(topbar.getBoundingClientRect().height);
            if (height > 0) view.style.paddingTop = `${height}px`;
        });
    };

    if ('ResizeObserver' in window) {
        new ResizeObserver(syncReservedSpace).observe(topbar);
    }

    // 登录后玩家视图会由 hidden 切为可见；此时重新测量固定顶栏。
    new MutationObserver(syncReservedSpace).observe(view, {
        attributes: true,
        attributeFilter: ['class'],
    });
    window.addEventListener('resize', syncReservedSpace, { passive: true });
    window.addEventListener('load', syncReservedSpace, { once: true });
    syncReservedSpace();
})();
