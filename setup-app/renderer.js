const app = document.getElementById('app')
const lead = document.getElementById('lead')

function showError(error) {
  return `<div class="error">${error.message || error}</div>`
}

function actions(primary) {
  return `<div class="actions"><button class="quiet" id="back" type="button">Back</button><button class="quiet" id="remove" type="button">Remove service</button><button class="primary" id="go" type="button">${primary}</button></div><div id="out"></div>`
}

function removeService(role, directory) {
  const button = document.getElementById('remove')
  const out = document.getElementById('out')
  button.disabled = true
  out.textContent = ''
  window.setup.remove({ role, directory }).then((body) => {
    out.innerHTML = `<div class="ok">${body.log || 'Service removed.'}</div>`
    button.disabled = false
  }).catch((error) => {
    out.innerHTML = showError(error)
    button.disabled = false
  })
}

async function run(button, work) {
  const out = document.getElementById('out')
  const label = button.textContent
  button.disabled = true
  button.textContent = 'Starting…'
  out.textContent = ''
  try {
    await work(out)
    button.textContent = label
  } catch (error) {
    out.innerHTML = showError(error)
    button.disabled = false
    button.textContent = label
  }
}

const home = () => {
  lead.textContent = 'What is this machine?'
  app.innerHTML = `
    <div class="choices">
      <button class="choice" id="server" type="button">
        <span class="choice-kicker">This machine</span>
        <span class="choice-title">Server</span>
        <span class="choice-body">The one garden server on this LAN. It starts when this machine boots.</span>
      </button>
      <button class="choice" id="client" type="button">
        <span class="choice-kicker">This machine</span>
        <span class="choice-title">Client</span>
        <span class="choice-body">A Pi or computer with sensors and relays. It starts when this machine boots and does not run the server.</span>
      </button>
    </div>`
  document.getElementById('server').onclick = serverForm
  document.getElementById('client').onclick = clientForm
}

function serverForm() {
  lead.textContent = 'This machine will be the server.'
  app.innerHTML = `
    <div class="logbook">
      <div class="row"><label for="dir">Install folder</label><input id="dir" placeholder="~/slopgarden" /></div>
      <div class="row"><label for="url">Address</label><input id="url" placeholder="http://192.168.1.20:5080" /></div>
      <div class="row"><label for="pw">Database password</label><input id="pw" type="password" placeholder="Leave blank to generate one" /></div>
      <p class="note">This registers the server service and starts it at boot. PostgreSQL needs to be running already. The address is what other machines use. Then open the web app and create the Admin account. No demo user is created.</p>
      ${actions('Start the server')}
    </div>`
  document.getElementById('back').onclick = home
  document.getElementById('remove').onclick = () => removeService('server', document.getElementById('dir').value)
  document.getElementById('go').onclick = () => run(document.getElementById('go'), async (out) => {
    const body = await window.setup.server({
      directory: document.getElementById('dir').value,
      publicUrl: document.getElementById('url').value,
      postgresPassword: document.getElementById('pw').value,
    })
    const password = body.databasePassword ? ` Database password: ${body.databasePassword}` : ''
    out.innerHTML = `<div class="ok">${body.log || 'Server service started.'} Create the Admin at http://127.0.0.1:5080.${password}</div>`
  })
}

async function clientForm() {
  lead.textContent = 'This machine will be a client.'
  app.innerHTML = '<p class="note">Looking for a SlopGarden server on this network…</p>'
  let detected = ''
  try {
    const found = await window.setup.discover()
    if (found.found) detected = found.serverUrl
  } catch { detected = '' }
  lead.textContent = detected ? 'Server found on this network.' : 'No server answered automatically.'
  app.innerHTML = `
    <div class="logbook">
      <div class="row"><label for="name">Display name</label><input id="name" placeholder="Greenhouse Pi" /></div>
      <div class="row"><label for="dir">Install folder</label><input id="dir" placeholder="~/slopgarden" /></div>
      ${detected
        ? ''
        : '<div class="row"><label for="url">Server address</label><input id="url" placeholder="http://192.168.1.20:5080" /></div>'}
      <p class="note" id="client-note"></p>
      ${actions('Start the client')}
    </div>`
  const note = document.getElementById('client-note')
  if (detected) {
    note.append('Using ')
    const found = document.createElement('b')
    found.id = 'found'
    found.textContent = detected
    note.append(found)
    note.append('. This machine will not run the server. It stays pending until an Admin approves it.')
  } else {
    note.textContent = 'mDNS and UDP did not find a server. Enter its address. This machine will not run the server, and it stays pending until an Admin approves it.'
  }
  document.getElementById('back').onclick = home
  document.getElementById('remove').onclick = () => removeService('client', document.getElementById('dir').value)
  document.getElementById('go').onclick = () => run(document.getElementById('go'), async (out) => {
    const typed = document.getElementById('url')
    const body = await window.setup.client({
      name: document.getElementById('name').value,
      directory: document.getElementById('dir').value,
      serverUrl: detected || (typed ? typed.value : ''),
    })
    out.innerHTML = `<div class="ok">${body.log || 'Client service started.'} Approve it on the server under Devices.</div>`
  })
}

home()
