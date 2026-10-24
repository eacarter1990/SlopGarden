"use strict";

const $ = (sel, root = document) => root.querySelector(sel);
const sparkHistory = new Map();
const sensorCards = new Map();
const actuatorCards = new Map();
const cameraCards = new Map();
let lastScale = null;

const ICONS = {
  valve: '<path d="M12 3c3 4 6 7.5 6 11a6 6 0 0 1-12 0c0-3.5 3-7 6-11Z"/>',
  pump: '<circle cx="12" cy="12" r="8"/><path d="M12 4v4M12 16v4M4 12h4M16 12h4"/><circle cx="12" cy="12" r="2"/>',
  light: '<path d="M9 18h6M10 21h4M12 3a6 6 0 0 0-3.5 10.9c.6.5 1 1.2 1 2.1h5c0-.9.4-1.6 1-2.1A6 6 0 0 0 12 3Z"/>',
  heater: '<path d="M12 3c1 3 5 5 5 10a5 5 0 0 1-10 0c0-2.5 1.5-4 2.5-5 .3 1.6 1 2.6 2 3 0-3 0-5.5.5-8Z"/>',
  fertilizer_doser: '<path d="M9 3h6M10 3v6l-5 9a2 2 0 0 0 1.8 3h10.4A2 2 0 0 0 19 18l-5-9V3"/><path d="M7.5 15h9"/>',
  fan: '<circle cx="12" cy="12" r="2"/><path d="M12 10c0-4 1-7 4-7 2 0 2 3 0 5M14 12c4 0 7 1 7 4 0 2-3 2-5 0M12 14c0 4-1 7-4 7-2 0-2-3 0-5M10 12c-4 0-7-1-7-4 0-2 3-2 5 0"/>',
  mister: '<path d="M4 14h16M7 18v2M12 18v3M17 18v2M8 14V8a4 4 0 0 1 8 0v6"/>',
  other: '<circle cx="12" cy="12" r="8"/><path d="M12 8v8M8 12h8"/>',
};

async function api(path, options = {}) {
  const res = await fetch(path, {
    headers: options.body && !(options.body instanceof Blob) ? { "Content-Type": "application/json" } : undefined,
    ...options,
  });
  if (!res.ok) {
    let msg = res.statusText;
    try { msg = (await res.json()).error ?? msg; } catch { /* not json */ }
    throw new Error(msg);
  }
  return res.status === 204 ? null : res.json();
}

function fmt(v, decimals) {
  if (v === null || v === undefined || Number.isNaN(v)) return "--";
  return Number(v).toLocaleString(undefined, { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
}

function sliderRange(sensor) {
  const m = sensor.model;
  switch (sensor.unit) {
    case "%": return [0, 100, 0.5];
    case "°C": return [-10, 50, 0.1];
    case "lx": return [0, 100000, 100];
    case "hPa": return [900, 1100, 0.5];
    case "pH": return [0, 14, 0.05];
  }
  const lo = m.min ?? m.floor ?? 0;
  const hi = m.max ?? m.ceiling ?? Math.max(10, Math.abs(sensor.value) * 2);
  return [lo, hi, (hi - lo) / 200];
}

function modelSummary(m) {
  switch (m.type) {
    case "drying": return `drying ${m.ratePerHour ?? 0}/h`;
    case "sine": return `sine ${m.min}…${m.max}`;
    case "random_walk": return `random walk ${m.min}…${m.max}`;
    default: return `constant ${m.value ?? 0}`;
  }
}

function sparkline(card, id, value) {
  const values = sparkHistory.get(id) ?? [];
  values.push(value);
  if (values.length > 90) values.shift();
  sparkHistory.set(id, values);
  const min = Math.min(...values), max = Math.max(...values);
  const span = max - min || 1;
  const pts = values.map((v, i) => `${(i / 89) * 100},${26 - ((v - min) / span) * 24}`).join(" ");
  $("polyline", card).setAttribute("points", pts);
}

function setModelVisibility(form) {
  const type = form.elements.type.value;
  form.querySelectorAll("label[data-for]").forEach(l => l.classList.toggle("hidden", !l.dataset.for.split(" ").includes(type)));
}

function fillModelForm(form, s) {
  form.elements.name.value = s.name;
  form.elements.type.value = s.model.type;
  for (const key of ["value", "min", "max", "periodSec", "phase", "step", "start", "ratePerHour", "floor", "ceiling"]) {
    form.elements[key].value = s.model[key] ?? "";
  }
  form.elements.noise.value = s.noise;
  setModelVisibility(form);
}

function readModelForm(form) {
  const num = name => form.elements[name].value === "" ? null : Number(form.elements[name].value);
  const model = { type: form.elements.type.value };
  for (const key of ["value", "min", "max", "periodSec", "phase", "step", "start", "ratePerHour", "floor", "ceiling"]) {
    const v = num(key);
    if (v !== null) model[key] = v;
  }
  return { name: form.elements.name.value, model, noise: num("noise") ?? 0 };
}

function createSensorCard(s) {
  const card = $("#sensor-template").content.firstElementChild.cloneNode(true);
  card.dataset.id = s.id;
  const slider = $(".ov-slider", card), valueInput = $(".ov-value", card), form = $(".model-editor", card);
  const [lo, hi, step] = sliderRange(s);
  Object.assign(slider, { min: lo, max: hi, step, value: s.value });
  slider.addEventListener("input", () => { valueInput.value = slider.value; });
  slider.addEventListener("change", () => applyOverride());
  valueInput.addEventListener("keydown", e => { if (e.key === "Enter") applyOverride(); });

  async function applyOverride() {
    const value = Number(valueInput.value || slider.value);
    const duration = $(".ov-duration", card).value;
    await guard(api(`/api/sensors/${encodeURIComponent(s.id)}/override`, {
      method: "POST", body: JSON.stringify({ value, durationSec: duration ? Number(duration) : null }),
    }));
  }
  $(".ov-apply", card).addEventListener("click", applyOverride);
  $(".ov-clear", card).addEventListener("click", () => guard(api(`/api/sensors/${encodeURIComponent(s.id)}/override`, { method: "DELETE" })));

  $(".edit", card).addEventListener("click", () => {
    const open = form.classList.toggle("hidden") === false;
    if (open) fillModelForm(form, card._sensor);
  });
  form.elements.type.addEventListener("change", () => setModelVisibility(form));
  $(".cancel", form).addEventListener("click", () => form.classList.add("hidden"));
  $(".delete", form).addEventListener("click", async () => {
    if (!confirm(`Delete sensor ${s.id}?`)) return;
    await guard(api(`/api/sensors/${encodeURIComponent(s.id)}`, { method: "DELETE" }));
    refresh();
  });
  form.addEventListener("submit", async e => {
    e.preventDefault();
    await guard(api(`/api/sensors/${encodeURIComponent(s.id)}`, { method: "PUT", body: JSON.stringify(readModelForm(form)) }));
    form.classList.add("hidden");
    sparkHistory.delete(s.id);
  });
  return card;
}

function updateSensorCard(card, s) {
  card._sensor = s;
  $(".name", card).textContent = s.name;
  $(".metric", card).textContent = s.metric;
  $(".model", card).textContent = modelSummary(s.model);
  $(".value", card).textContent = fmt(s.value, s.decimals);
  $(".unit", card).textContent = s.unit;
  const overridden = !!s.override;
  card.classList.toggle("overridden", overridden);
  const badge = $(".override-badge", card);
  badge.classList.toggle("hidden", !overridden);
  badge.textContent = overridden && s.override.until ? `override until ${new Date(s.override.until).toLocaleTimeString()}` : "override";
  const slider = $(".ov-slider", card);
  if (document.activeElement !== slider && document.activeElement !== $(".ov-value", card)) slider.value = s.value;
  sparkline(card, s.id, s.value);
}

function createActuatorCard(a) {
  const card = document.createElement("article");
  card.className = "card actuator";
  card.innerHTML = `
    <div class="top">
      <div class="icon"><svg viewBox="0 0 24 24">${ICONS[a.kind] ?? ICONS.other}</svg></div>
      <div><h3></h3><div class="kind"></div></div>
    </div>
    <div class="bottom">
      <span class="state"></span>
      <label class="switch"><input type="checkbox"><span></span></label>
    </div>`;
  $("input", card).addEventListener("change", e =>
    guard(api(`/api/actuators/${encodeURIComponent(a.id)}/state`, { method: "PUT", body: JSON.stringify({ on: e.target.checked, source: "emulator-ui" }) })));
  return card;
}

function updateActuatorCard(card, a) {
  $("h3", card).textContent = a.name;
  $(".kind", card).textContent = a.kind.replace("_", " ");
  card.classList.toggle("on", a.on);
  $("input", card).checked = a.on;
  const since = new Date(a.since);
  const level = a.on && a.level != null && a.level < 1 ? ` · ${Math.round(a.level * 100)}%` : "";
  $(".state", card).textContent = `${a.on ? "ON" : "Off"}${level} · since ${since.toLocaleTimeString()}`;
}

function createCameraCard(c) {
  const card = document.createElement("article");
  card.className = "card camera";
  card.innerHTML = `
    <img alt="">
    <div class="meta">
      <h3></h3>
      <label class="upload">Replace image<input type="file" accept="image/jpeg,image/png,image/webp"></label>
    </div>`;
  $("input", card).addEventListener("change", async e => {
    const file = e.target.files[0];
    if (!file) return;
    await guard(api(`/api/cameras/${encodeURIComponent(c.id)}/image`, { method: "PUT", body: file, headers: { "Content-Type": file.type } }));
    card._loadedAt = 0;
  });
  return card;
}

function updateCameraCard(card, c) {
  $("h3", card).textContent = c.name;
  const now = Date.now();
  if (!card._loadedAt || now - card._loadedAt > 5000) {
    card._loadedAt = now;
    const img = $("img", card);
    img.alt = c.name;
    img.src = `${c.snapshotUrl}?width=760&height=428&t=${now}`;
  }
}

function sync(container, map, items, create, update) {
  const ids = new Set(items.map(i => i.id));
  for (const [id, card] of map) {
    if (!ids.has(id)) { card.remove(); map.delete(id); }
  }
  for (const item of items) {
    let card = map.get(item.id);
    if (!card) {
      card = create(item);
      map.set(item.id, card);
      container.appendChild(card);
    }
    update(card, item);
  }
}

function updateScale(scale) {
  if (scale === lastScale) return;
  lastScale = scale;
  document.querySelectorAll("#scale-buttons button").forEach(b => b.classList.toggle("active", Number(b.dataset.scale) === scale));
  if (document.activeElement !== $("#scale-input")) $("#scale-input").value = scale;
}

async function refresh() {
  try {
    const s = await api("/api/scenario");
    $("#scenario-name").textContent = `${s.name} · ${s.sensors.length} sensors · ${s.actuators.length} actuators · ${s.cameras.length} cameras`;
    $("#sim-time").textContent = new Date(s.simulatedTime).toLocaleString();
    updateScale(s.timeScale);
    sync($("#sensors"), sensorCards, s.sensors, createSensorCard, updateSensorCard);
    sync($("#actuators"), actuatorCards, s.actuators, createActuatorCard, updateActuatorCard);
    sync($("#cameras"), cameraCards, s.cameras, createCameraCard, updateCameraCard);
    setStatus(`live · ${new Date().toLocaleTimeString()}`);
  } catch (e) {
    setStatus(`offline: ${e.message}`, true);
  }
}

function setStatus(text, error = false) {
  const el = $("#status");
  el.textContent = text;
  el.classList.toggle("error", error);
}

async function guard(promise) {
  try {
    const result = await promise;
    refresh();
    return result;
  } catch (e) {
    setStatus(e.message, true);
    alert(e.message);
  }
}

async function setScale(factor) {
  if (!(factor > 0)) return;
  await guard(api("/api/scenario/time-scale", { method: "POST", body: JSON.stringify({ factor }) }));
}

document.querySelectorAll("#scale-buttons button").forEach(b => b.addEventListener("click", () => setScale(Number(b.dataset.scale))));
$("#scale-input").addEventListener("change", e => setScale(Number(e.target.value)));
$("#reset").addEventListener("click", async () => {
  if (!confirm("Reset to the default demo garden? Custom sensors and overrides are lost.")) return;
  sparkHistory.clear();
  await guard(api("/api/scenario/reset", { method: "POST" }));
});
$("#add-sensor-toggle").addEventListener("click", () => $("#add-sensor").classList.toggle("hidden"));
$("#add-sensor").addEventListener("submit", async e => {
  e.preventDefault();
  const f = e.target.elements;
  const type = f.type.value;
  const model = { type };
  if (type === "drying") Object.assign(model, { start: 50, ratePerHour: 0.5, floor: 5, ceiling: 100 });
  if (type === "constant") model.value = 50;
  if (type === "sine") Object.assign(model, { min: 10, max: 30, periodSec: 86400, phase: 28800 });
  if (type === "random_walk") Object.assign(model, { min: 0, max: 100, step: 0.1, start: 50 });
  const created = await guard(api("/api/sensors", {
    method: "POST",
    body: JSON.stringify({ id: f.id.value, name: f.name.value || f.id.value, metric: f.metric.value, unit: f.unit.value, noise: Number(f.noise.value || 0), model }),
  }));
  if (created) { e.target.reset(); e.target.classList.add("hidden"); }
});

refresh();
setInterval(refresh, 1000);
