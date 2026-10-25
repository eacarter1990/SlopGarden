const { app, BrowserWindow, ipcMain } = require('electron')
const { spawn } = require('child_process')
const fs = require('fs')
const path = require('path')

function helper() {
  const bundledDir = path.join(process.resourcesPath, 'helper')
  const bundled = path.join(bundledDir, process.platform === 'win32' ? 'slopgarden-setup.exe' : 'slopgarden-setup')
  if (fs.existsSync(bundled)) return { command: bundled, prefix: [] }
  const project = path.join(__dirname, '..', 'tools', 'SlopGarden.Setup', 'SlopGarden.Setup.csproj')
  return { command: 'dotnet', prefix: ['run', '--project', project, '--'] }
}

function run(args) {
  const { command, prefix } = helper()
  return new Promise((resolve, reject) => {
    const child = spawn(command, [...prefix, ...args], { windowsHide: true })
    let out = ''
    let err = ''
    child.stdout.on('data', (chunk) => { out += chunk })
    child.stderr.on('data', (chunk) => { err += chunk })
    child.on('error', (error) => reject(error))
    child.on('close', (code) => {
      const line = out.trim().split('\n').filter(Boolean).pop() || ''
      let body
      try { body = JSON.parse(line) } catch { body = null }
      if (body?.error) reject(new Error(body.error))
      else if (code !== 0 || !body) reject(new Error(err.trim() || out.trim() || 'Setup failed'))
      else resolve(body)
    })
  })
}

function arg(name, value) {
  return value ? [name, value] : []
}

ipcMain.handle('docker', () => run(['docker']))
ipcMain.handle('discover', () => run(['discover']))
ipcMain.handle('server', (_event, body) => run([
  'server',
  ...arg('--directory', body.directory),
  ...arg('--public-url', body.publicUrl),
  ...arg('--password', body.postgresPassword),
]))
ipcMain.handle('client', (_event, body) => run([
  'client',
  '--name', body.name || '',
  ...arg('--directory', body.directory),
  ...arg('--server-url', body.serverUrl),
]))

function createWindow() {
  const window = new BrowserWindow({
    width: 720,
    height: 760,
    minWidth: 420,
    show: false,
    autoHideMenuBar: true,
    backgroundColor: '#121a16',
    title: 'SlopGarden Setup',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
    },
  })
  window.once('ready-to-show', () => window.show())
  window.loadFile(path.join(__dirname, 'index.html'))
}

app.whenReady().then(createWindow)
app.on('window-all-closed', () => app.quit())
