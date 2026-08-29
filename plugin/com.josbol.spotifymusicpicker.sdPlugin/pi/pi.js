// Minimal property-inspector runtime for OpenDeck (same connectElgatoStreamDeckSocket signature as Elgato's).
let ws = null, uuid = null, actionInfo = null, settings = {}, globalSettings = {}, status = null;
window.connectElgatoStreamDeckSocket = function (port, inUuid, registerEvent, info, inActionInfo) {
  uuid = inUuid;
  try { actionInfo = JSON.parse(inActionInfo); settings = (actionInfo.payload && actionInfo.payload.settings) || {}; } catch (e) { actionInfo = {}; }
  ws = new WebSocket("ws://127.0.0.1:" + port);
  ws.onopen = () => {
    ws.send(JSON.stringify({ event: registerEvent, uuid }));
    ws.send(JSON.stringify({ event: "getGlobalSettings", context: uuid }));
    command("status");
    render();
  };
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    if (msg.event === "didReceiveSettings") { settings = msg.payload.settings || {}; render(); }
    if (msg.event === "didReceiveGlobalSettings") { globalSettings = msg.payload.settings || {}; render(); }
    if (msg.event === "sendToPropertyInspector" && msg.payload) {
      if (msg.payload.globalSettings) globalSettings = Object.assign({}, msg.payload.globalSettings);
      if (msg.payload.status) status = msg.payload.status;
      render();
    }
  };
};
function send(o) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(o)); }
function command(name, extra) { send({ event: "sendToPlugin", action: actionInfo && actionInfo.action, context: uuid, payload: Object.assign({ command: name }, extra || {}) }); }
function saveSettings(patch) {
  settings = Object.assign({}, settings, patch);
  send({ event: "setSettings", context: uuid, payload: settings });
}
function saveGlobal(patch) {
  globalSettings = Object.assign({}, globalSettings, patch);
  send({ event: "setGlobalSettings", context: uuid, payload: globalSettings });
  command("setGlobalSettings", { settings: globalSettings });
}
function bind(id, key, opts) {
  const el = document.getElementById(id); if (!el) return;
  opts = opts || {};
  const store = opts.global ? globalSettings : settings;
  const v = store[key];
  if (document.activeElement !== el) {
    if (el.type === "checkbox") el.checked = v === undefined ? !!opts.def : !!v;
    else el.value = v === undefined || v === null ? (opts.def === undefined ? "" : opts.def) : v;
  }
  if (!el.dataset.bound) {
    el.dataset.bound = "1";
    el.addEventListener("change", () => {
      let val = el.type === "checkbox" ? el.checked : el.value;
      if (opts.num) val = Number(val);
      (opts.global ? saveGlobal : saveSettings)({ [key]: val });
    });
  }
}
const GLOBAL_HTML = `
<h3>Spotify account</h3>
<div class="row"><label for="g-clientId">Client id</label><input type="text" id="g-clientId" placeholder="from developer.spotify.com/dashboard"></div>
<div class="hint" id="clientHint"></div>
<div class="row"><label for="g-redirectPort">Callback port</label><input type="number" id="g-redirectPort" min="1024" max="65535"></div>
<div class="hint">Add <code id="redirectUri">http://127.0.0.1:43118/callback</code> as a Redirect URI of your Spotify app (Spotify no longer accepts "localhost"). The app needs a Premium account; Web API, Development mode.</div>
<div class="row"><button id="login" class="primary" type="button">Connect to Spotify</button><button id="logout" type="button">Disconnect</button><button id="refresh" type="button">Refresh</button><button id="reroll" type="button">New suggestions</button></div>
<div id="auth" class="hint"></div>
<h3>Layouts</h3>
<div class="row"><label for="g-pickerProfile">Spotify profile</label><input type="text" id="g-pickerProfile"></div>
<div class="row"><label for="g-mainProfile">Main profile</label><input type="text" id="g-mainProfile"></div>
<div class="hint">Names of the OpenDeck profiles the volume dial switches between.</div>
<h3>Rows</h3>
<div class="row"><label for="g-madeForYouLinks">Made-for-you links</label></div>
<textarea id="g-madeForYouLinks" placeholder="https://open.spotify.com/playlist/37i9dQZF1E3… | Daily Mix 1&#10;spotify:playlist:37i9dQZF1E… | Discover Weekly"></textarea>
<div class="hint">Spotify hides its own playlists (Daily Mix, Discover Weekly…) from apps created after Nov 2024 — in your library and in search. Paste their share links here (Spotify app → playlist → ⋯ → Share → Copy link), one per line; the name and today's cover come from Spotify's public embed endpoint (add "| name" to override the name). Mixes you play in the Spotify app appear on the row by themselves.</div>
<div class="row"><label for="g-historyDays">Most played: days</label><input type="number" id="g-historyDays" min="1" max="180"></div>
<div class="row"><label for="g-showLabels">Names on covers</label><input type="checkbox" id="g-showLabels"></div>
<h3>Playback</h3>
<div class="row"><label for="g-preferredDevice">Preferred device</label><input type="text" id="g-preferredDevice" placeholder="(this computer)"></div>
<div class="hint">Where to start playback when nothing is active (part of the device name as Spotify shows it).</div>
<div class="row"><label for="g-volumeStep">Volume step (%)</label><input type="number" id="g-volumeStep" min="1" max="50"></div>
<div class="row"><label for="g-playbackSeconds">Now-playing poll (s)</label><input type="number" id="g-playbackSeconds" min="2" max="60"></div>
<div class="row"><label for="g-refreshMinutes">Rows refresh (min)</label><input type="number" id="g-refreshMinutes" min="1" max="240"></div>
<h3>Live</h3><pre id="status">…</pre>`;
function renderGlobal() {
  const host = document.getElementById("global");
  if (host && !host.dataset.filled) {
    host.dataset.filled = "1"; host.innerHTML = GLOBAL_HTML;
    document.getElementById("login").addEventListener("click", () => command("login"));
    document.getElementById("logout").addEventListener("click", () => command("logout"));
    document.getElementById("refresh").addEventListener("click", () => command("refresh"));
    document.getElementById("reroll").addEventListener("click", () => command("reroll"));
  }
  bind("g-clientId", "clientId", { global: true });
  bind("g-redirectPort", "redirectPort", { global: true, num: true, def: 43118 });
  bind("g-pickerProfile", "pickerProfile", { global: true, def: "Spotify" });
  bind("g-mainProfile", "mainProfile", { global: true, def: "Default" });
  bind("g-madeForYouLinks", "madeForYouLinks", { global: true });
  bind("g-historyDays", "historyDays", { global: true, num: true, def: 30 });
  bind("g-showLabels", "showLabels", { global: true, def: true });
  bind("g-preferredDevice", "preferredDevice", { global: true });
  bind("g-volumeStep", "volumeStep", { global: true, num: true, def: 5 });
  bind("g-playbackSeconds", "playbackSeconds", { global: true, num: true, def: 5 });
  bind("g-refreshMinutes", "refreshMinutes", { global: true, num: true, def: 15 });
  const port = globalSettings.redirectPort || 43118;
  const ru = document.getElementById("redirectUri"); if (ru) ru.textContent = "http://127.0.0.1:" + port + "/callback";
  const auth = document.getElementById("auth"), st = document.getElementById("status"), ch = document.getElementById("clientHint");
  if (!status) { if (auth) auth.textContent = "…"; return; }
  if (ch) ch.textContent = status.clientIdSource === "Essentials for Spotify" ? "Using the client id of the Essentials for Spotify plugin (" + status.clientIdPreview + ") — add the redirect URI below to that app, or enter your own." : (status.clientIdSource === "none" ? "No client id yet: create an app at developer.spotify.com/dashboard and paste its Client ID." : "");
  if (auth) {
    let html = status.connected ? `<span class="ok">Connected</span> as ${esc(status.user || "?")}` : `<span class="bad">Not connected</span>`;
    if (status.error) html += ` — <span class="bad">${esc(status.error)}</span>`;
    if (status.authorizeUrl) html += `<br>If no browser opened: <a href="${status.authorizeUrl}" target="_blank">open the Spotify login</a>`;
    auth.innerHTML = html;
  }
  if (st) {
    const l = status.lists || {};
    const names = (arr) => (arr || []).map(i => i.name + (i.subtitle && i.subtitle !== "Made for you" ? " — " + i.subtitle : "") + (i.missing ? " (no metadata)" : "")).join("\n  ") || "—";
    st.textContent = `Made for you (${(l.madeForYou || []).length}${l.hidden ? ", " + l.hidden + " hidden by Spotify" : ""}):\n  ${names(l.madeForYou)}\nMost played (${l.plays || 0} plays in history):\n  ${names(l.frequent)}\nSuggested:\n  ${names(l.recommended)}\n` +
      (l.playback ? `Playing: ${l.playback.trackName} — ${l.playback.artists} on ${l.playback.deviceName || "?"} (${l.playback.isPlaying ? "playing" : "paused"}, vol ${l.playback.volumePercent}%)` : "Nothing playing") +
      (l.listsAt ? `\nRows refreshed ${l.listsAt}, ${l.requests} API requests since start` : "");
  }
}
function esc(s) { return String(s).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c])); }
function render() {
  if (typeof window.bindFields === "function") window.bindFields();
  renderGlobal();
}
