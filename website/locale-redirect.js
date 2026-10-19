(function () {
    if (window.location.protocol === 'file:') return;

    const path = window.location.pathname;
    if (/(?:^|\/)(?:en|zh)(?:\/index\.html)?\/?$/i.test(path)) return;

    const segments = path.split('/').filter(Boolean);
    if (segments.at(-1)?.toLowerCase() === 'index.html') segments.pop();
    const basePath = `/${segments.length ? `${segments.join('/')}/` : ''}`;

    window.location.replace(`${basePath}en/${window.location.search}${window.location.hash}`);
})();
