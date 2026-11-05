// Light/dark toggle (initial theme is applied in the <head> by _ThemeScript).
(function () {
    var root = document.documentElement;
    // The toggle is a "dark mode" switch: aria-pressed tells screen readers whether dark mode is on.
    var syncThemeButtons = function () {
        var dark = root.getAttribute('data-bs-theme') === 'dark';
        document.querySelectorAll('[data-theme-toggle]').forEach(function (b) { b.setAttribute('aria-pressed', dark ? 'true' : 'false'); });
    };
    syncThemeButtons();
    document.addEventListener('click', function (e) {
        var btn = e.target.closest && e.target.closest('[data-theme-toggle]');
        if (!btn) return;
        var next = root.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
        root.setAttribute('data-bs-theme', next);
        try { localStorage.setItem('tc-theme', next); } catch (err) { }
        syncThemeButtons();
        document.dispatchEvent(new CustomEvent('tc:theme', { detail: next }));
    });

    // Off-canvas sidebar (small screens): keep aria-expanded of its toggle in step (Bootstrap does not).
    document.addEventListener('show.bs.offcanvas', function (e) {
        document.querySelectorAll('[data-bs-toggle="offcanvas"][aria-controls="' + e.target.id + '"]').forEach(function (b) { b.setAttribute('aria-expanded', 'true'); });
    });
    document.addEventListener('hidden.bs.offcanvas', function (e) {
        document.querySelectorAll('[data-bs-toggle="offcanvas"][aria-controls="' + e.target.id + '"]').forEach(function (b) { b.setAttribute('aria-expanded', 'false'); });
    });

    // Forms with data-confirm ask before submitting (no inline handlers: the CSP forbids them).
    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (form.matches && form.matches('form[data-confirm]') && !window.confirm(form.getAttribute('data-confirm'))) {
            e.preventDefault();
        }
    });

    // Monitor form: show the fields of the selected type. Hidden fieldsets/options are also disabled, so the browser
    // does not post them (e.g. the two interval selects share one name). The server renders the initial state.
    var typeSelect = document.querySelector('[data-monitor-type]');
    if (typeSelect && typeSelect.form) {
        var form = typeSelect.form;
        var help = document.getElementById('target-help');
        var target = document.getElementById('monitor-target');
        var updateType = function () {
            var t = typeSelect.value;
            form.querySelectorAll('[data-types]').forEach(function (el) {
                var on = el.getAttribute('data-types').split(' ').indexOf(t) >= 0;
                el.hidden = !on;
                if (el.tagName === 'FIELDSET' || el.tagName === 'OPTION') el.disabled = !on;
            });
            // A select whose chosen option was just disabled falls back to its first enabled option.
            form.querySelectorAll('select').forEach(function (s) {
                var chosen = s.options[s.selectedIndex];
                if (chosen && chosen.disabled) {
                    for (var i = 0; i < s.options.length; i++) {
                        if (!s.options[i].disabled) { s.selectedIndex = i; break; }
                    }
                }
            });
            var key = t.toLowerCase();
            if (help) help.textContent = help.getAttribute('data-help-' + key) || help.getAttribute('data-help-default');
            if (target) target.placeholder = target.getAttribute('data-placeholder-' + key) || 'example.com';
        };
        typeSelect.addEventListener('change', updateType);
        updateType();
    }

    // Marketing nav gets a border once the page is scrolled.
    var nav = document.querySelector('.site-nav');
    if (nav) {
        var onScroll = function () { nav.classList.toggle('scrolled', window.scrollY > 8); };
        window.addEventListener('scroll', onScroll, { passive: true });
        onScroll();
    }
})();
