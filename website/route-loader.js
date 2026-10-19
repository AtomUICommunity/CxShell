(async function () {
    const rootUrl = new URL('../index.html', window.location.href);
    const siteRootUrl = new URL('../', window.location.href);

    try {
        const response = await fetch(rootUrl);
        if (!response.ok) throw new Error(`HTTP ${response.status}`);

        let html = await response.text();
        html = html.replace(/((?:href|src)=["'])\.\/([^"']+)/gi, (_, attribute, path) => {
            return `${attribute}${new URL(path, siteRootUrl).href}`;
        });

        document.open();
        document.write(html);
        document.close();
    } catch {
        document.body.textContent = 'The CxShell page could not be loaded. Please return to the site home page and try again.';
    }
})();
