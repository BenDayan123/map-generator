import { dotnet } from './_framework/dotnet.js';

// Browser helpers the .NET side calls through [JSImport] (BrowserPlatformServices).
// Defined before the runtime starts so they exist when the app first loads its settings.
globalThis.gmapPlanner = {
    getItem: (key) => {
        try { return localStorage.getItem(key); } catch { return null; }
    },
    setItem: (key, value) => {
        try { localStorage.setItem(key, value); } catch { /* private mode or quota: settings just don't persist */ }
    },
    openUrl: (url) => {
        window.open(url, '_blank', 'noopener');
    },
    downloadText: (fileName, content, mimeType) => {
        const url = URL.createObjectURL(new Blob([content], { type: mimeType }));
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    },
};

const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

const config = dotnetRuntime.getConfig();
try {
    await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
} catch (e) {
    // A startup crash otherwise vanishes behind the loading splash.
    const msg = e && (e.stack || e.message) ? String(e.stack || e.message) : String(e);
    document.body.innerHTML =
        '<pre style="color:#f88;white-space:pre-wrap;padding:16px;font:12px/1.5 monospace">BOOT ERROR:\n' +
        msg.replace(/[<&]/g, c => (c === '<' ? '&lt;' : '&amp;')) + '</pre>';
    console.error(e);
}
