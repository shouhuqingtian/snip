const { app, BrowserWindow } = require('electron');
let window;
app.whenReady().then(() => {
  window = new BrowserWindow({ width: 800, height: 650,
    webPreferences: { nodeIntegration: false, contextIsolation: true, sandbox: true } });
  window.loadFile(process.argv[2]);
});
app.on('window-all-closed', () => app.quit());
