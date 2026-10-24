using System.Diagnostics;
using SlopGarden.Contracts;

// Local setup wizard. It binds to loopback only and asks whether this machine is the server or a client.
var builder = WebApplication.CreateBuilder(args);
var port = MachineSetup.SetupWizardPort;
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();

app.MapGet("/", () => Results.Content(WizardPage.Html, "text/html"));
app.MapGet("/api/status", () => Results.Ok(new { docker = Docker.Available(), port }));

app.MapPost("/api/server", async (ServerChoice body, CancellationToken ct) =>
{
    try
    {
        var compose = MachineSetup.ServerCompose(body.PostgresPassword ?? "", body.PublicUrl);
        var dir = DirectoryFor(body.Directory);
        var file = Path.Combine(dir, "docker-compose.yml");
        if (body.DryRun) return Results.Ok(new { directory = dir, file, compose, started = false });
        Docker.Require();
        await File.WriteAllTextAsync(file, compose, ct);
        var log = await Docker.ComposeUpAsync(dir, ct);
        return Results.Ok(new { directory = dir, url = "http://127.0.0.1:5080", log, started = true });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/client", async (ClientChoice body, CancellationToken ct) =>
{
    try
    {
        var compose = MachineSetup.ClientCompose(body.ServerUrl, body.Name, body.Image);
        var dir = DirectoryFor(body.Directory);
        var file = Path.Combine(dir, "docker-compose.yml");
        if (body.DryRun) return Results.Ok(new { directory = dir, file, compose, started = false });
        Docker.Require();
        await File.WriteAllTextAsync(file, compose, ct);
        var log = await Docker.ComposeUpAsync(dir, ct);
        return Results.Ok(new { directory = dir, log, started = true, pending = true });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = $"http://127.0.0.1:{port}";
    Console.WriteLine($"SlopGarden setup is at {url}");
    if (!args.Contains("--no-browser")) OpenBrowser(url);
});

app.Run();

static string DirectoryFor(string? requested)
{
    var dir = string.IsNullOrWhiteSpace(requested)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "slopgarden")
        : requested;
    Directory.CreateDirectory(dir);
    return Path.GetFullPath(dir);
}

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url);
    }
    catch (Exception)
    {
        // The URL is already printed. A machine without a browser still works.
    }
}

sealed record ServerChoice(string? Directory, string? PublicUrl, string? PostgresPassword, bool DryRun);
sealed record ClientChoice(string Name, string ServerUrl, string? Image, string? Directory, bool DryRun);

static class Docker
{
    public static bool Available()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("docker", "version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            return proc is not null && proc.WaitForExit(4000) && proc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static void Require()
    {
        if (!Available()) throw new InvalidOperationException("Docker is not installed or not running. Install Docker, then run slopgarden-setup again.");
    }

    public static async Task<string> ComposeUpAsync(string directory, CancellationToken ct)
    {
        var start = new ProcessStartInfo("docker", "compose up -d")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(start) ?? throw new InvalidOperationException("Could not start docker compose.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var log = (stdout + "\n" + stderr).Trim();
        if (proc.ExitCode != 0) throw new InvalidOperationException(log.Length > 0 ? log : "docker compose up failed.");
        return log;
    }
}

static class WizardPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>SlopGarden setup</title>
<style>
  :root { color-scheme: light; }
  body { margin: 0; font: 16px/1.45 system-ui, sans-serif; background: linear-gradient(135deg, #1b4332, #2d6a4f); min-height: 100vh; color: #1b1b1b; }
  main { max-width: 520px; margin: 48px auto; background: #fff; border-radius: 16px; padding: 28px; box-shadow: 0 16px 40px rgba(0,0,0,.2); }
  h1 { margin: 0 0 4px; font-size: 1.6rem; }
  p.lead { color: #526; margin-top: 0; }
  .choices { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
  button, .choice { font: inherit; border-radius: 12px; border: 1px solid #c8d5cc; background: #f6faf7; padding: 16px; text-align: left; cursor: pointer; }
  button.primary { background: #2d6a4f; color: white; border: 0; width: 100%; margin-top: 12px; }
  button.primary:disabled { opacity: .6; }
  label { display: block; font-size: .85rem; margin: 12px 0 4px; font-weight: 650; }
  input { width: 100%; box-sizing: border-box; padding: 8px 10px; border-radius: 8px; border: 1px solid #c8d5cc; font: inherit; }
  .error { background: #fde8e8; color: #8a1f1f; padding: 10px 12px; border-radius: 8px; }
  .ok { background: #e7f6ec; padding: 10px 12px; border-radius: 8px; }
  pre { white-space: pre-wrap; background: #f4f4f4; padding: 10px; border-radius: 8px; font-size: .8rem; }
  a { color: #1b4332; }
</style>
</head>
<body>
<main>
  <h1>SlopGarden setup</h1>
  <p class="lead" id="lead">What is this machine?</p>
  <div id="app"></div>
</main>
<script>
const app = document.getElementById('app')
const lead = document.getElementById('lead')
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
    <label>PostgreSQL password</label><input id="pw" type="password" value="" placeholder="generated if empty" />
    <p>After it starts, open the web app and create the Admin account. No demo user is created.</p>
    <button class="primary" id="go">Start the server</button>
    <p><a href="#" id="back">Back</a></p>
    <div id="out"></div>`
  document.getElementById('back').onclick = (e) => { e.preventDefault(); home() }
  document.getElementById('go').onclick = async () => {
    const out = document.getElementById('out')
    out.innerHTML = 'Starting…'
    const res = await fetch('/api/server', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
      directory: document.getElementById('dir').value,
      publicUrl: document.getElementById('url').value,
      postgresPassword: document.getElementById('pw').value
    })})
    const body = await res.json()
    out.innerHTML = res.ok
      ? `<div class="ok">Server is starting. Create the Admin at <a href="http://127.0.0.1:5080">http://127.0.0.1:5080</a>.<pre>${body.log || ''}</pre></div>`
      : `<div class="error">${body.error}</div>`
  }
}
function clientForm() {
  lead.textContent = 'This machine will be a client.'
  app.innerHTML = `
    <label>Name</label><input id="name" placeholder="Greenhouse Pi" />
    <label>Server address</label><input id="url" placeholder="Leave blank — the client finds the server on this network" />
    <label>Install folder</label><input id="dir" placeholder="~/slopgarden" />
    <p>Leave the address blank and the client finds the server with mDNS and UDP. Type one only if this machine is outside that LAN. It shows up as pending until an Admin approves it. This does not start a server or a database.</p>
    <button class="primary" id="go">Start the client</button>
    <p><a href="#" id="back">Back</a></p>
    <div id="out"></div>`
  document.getElementById('back').onclick = (e) => { e.preventDefault(); home() }
  document.getElementById('go').onclick = async () => {
    const out = document.getElementById('out')
    out.innerHTML = 'Starting…'
    const res = await fetch('/api/client', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
      name: document.getElementById('name').value,
      serverUrl: document.getElementById('url').value,
      directory: document.getElementById('dir').value
    })})
    const body = await res.json()
    out.innerHTML = res.ok
      ? `<div class="ok">Client started. Approve it on the server under Devices.<pre>${body.log || ''}</pre></div>`
      : `<div class="error">${body.error}</div>`
  }
}
home()
</script>
</body>
</html>
""";
}

public partial class Program;
