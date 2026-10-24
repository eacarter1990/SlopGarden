const app = document.getElementById('app')
const lead = document.getElementById('lead')

function showError(error) {
  return `<div class="error">${error.message || error}</div>`
}

const home = () => {
  lead.textContent = 'What is this machine?'
  app.innerHTML = `
    <div class="choices">
      <button class="choice" id="server"><b>Server</b><br><span>The one garden server on this LAN. Starts PostgreSQL and the web app.</span></button>
      <button class="choice" id="client"><b>Client</b><br><span>A Pi or computer with sensors and relays. Does not run the server.</span></button>
    </div>`
  document.getElementById('server').onclick = serverForm
  document.getElementById('client').onclick = clientForm
}

function serverForm() {
  lead.textContent = 'This machine will be the server.'
  app.innerHTML = `
    <label>Install folder</label><input id="dir" placeholder="~/slopgarden" />
    <label>Address other machines will use</label><input id="url" placeholder="http://192.168.1.20:5080" />
    <label>PostgreSQL password</label><input id="pw" type="password" placeholder="generated if empty" />
    <p>After it starts, open the web app and create the Admin account. No demo user is created.</p>
    <button class="primary" id="go">Start the server</button>
    <p><button id="back" type="button">Back</button></p>
    <div id="out"></div>`
  document.getElementById('back').onclick = home
  document.getElementById('go').onclick = async () => {
    const out = document.getElementById('out')
    out.textContent = 'Starting…'
    try {
      const body = await window.setup.server({
        directory: document.getElementById('dir').value,
        publicUrl: document.getElementById('url').value,
        postgresPassword: document.getElementById('pw').value,
      })
      out.innerHTML = `<div class="ok">Server is starting. Create the Admin at http://127.0.0.1:5080.<pre>${body.log || ''}</pre></div>`
    } catch (error) {
      out.innerHTML = showError(error)
    }
  }
}

async function clientForm() {
  lead.textContent = 'This machine will be a client.'
  app.innerHTML = '<p>Looking for a SlopGarden server on this network…</p>'
  let detected = ''
  try {
    const found = await window.setup.discover()
    if (found.found) detected = found.serverUrl
  } catch { detected = '' }
  lead.textContent = detected ? 'Server found on this network.' : 'No server answered automatically.'
  app.innerHTML = `
    <label>Display name</label><input id="name" placeholder="Greenhouse Pi" />
    <label>Install folder</label><input id="dir" placeholder="~/slopgarden" />
    ${detected
      ? `<p>Using <b id="found"></b>. This machine will not run the server. It stays pending until an Admin approves it.</p>`
      : `<label>Server address</label><input id="url" placeholder="http://192.168.1.20:5080" />
         <p>mDNS and UDP did not find a server. Enter its address. This machine will not run the server, and it stays pending until an Admin approves it.</p>`}
    <button class="primary" id="go">Start the client</button>
    <p><button id="back" type="button">Back</button></p>
    <div id="out"></div>`
  const foundLabel = document.getElementById('found')
  if (foundLabel) foundLabel.textContent = detected
  document.getElementById('back').onclick = home
  document.getElementById('go').onclick = async () => {
    const out = document.getElementById('out')
    const typed = document.getElementById('url')
    out.textContent = 'Starting…'
    try {
      const body = await window.setup.client({
        name: document.getElementById('name').value,
        directory: document.getElementById('dir').value,
        serverUrl: detected || (typed ? typed.value : ''),
      })
      out.innerHTML = `<div class="ok">Client started. Approve it on the server under Devices.<pre>${body.log || ''}</pre></div>`
    } catch (error) {
      out.innerHTML = showError(error)
    }
  }
}

home()
