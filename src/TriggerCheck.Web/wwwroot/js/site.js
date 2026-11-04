// Light/dark toggle (initial theme is applied in the <head> by _ThemeScript).
(function () {
    var root = document.documentElement;
    document.addEventListener('click', function (e) {
        var btn = e.target.closest && e.target.closest('[data-theme-toggle]');
        if (!btn) return;
        var next = root.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
        root.setAttribute('data-bs-theme', next);
        try { localStorage.setItem('tc-theme', next); } catch (err) { }
        document.dispatchEvent(new CustomEvent('tc:theme', { detail: next }));
    });

    // Marketing nav gets a border once the page is scrolled.
    var nav = document.querySelector('.site-nav');
    if (nav) {
        var onScroll = function () { nav.classList.toggle('scrolled', window.scrollY > 8); };
        window.addEventListener('scroll', onScroll, { passive: true });
        onScroll();
    }
})();
