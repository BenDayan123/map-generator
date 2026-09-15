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
await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
