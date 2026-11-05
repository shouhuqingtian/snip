const { app, BrowserWindow } = require('electron');
app.whenReady().then(() => {
  const window = new BrowserWindow({ width: 800, height: 650,
    webPreferences: { nodeIntegration: false, contextIsolation: true, sandbox: true } });
  window.loadFile(process.argv[2]);
});
app.on('window-all-closed', () => app.quit());
