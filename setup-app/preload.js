const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('setup', {
  docker: () => ipcRenderer.invoke('docker'),
  discover: () => ipcRenderer.invoke('discover'),
  server: (body) => ipcRenderer.invoke('server', body),
  client: (body) => ipcRenderer.invoke('client', body),
  remove: (body) => ipcRenderer.invoke('remove', body),
})
