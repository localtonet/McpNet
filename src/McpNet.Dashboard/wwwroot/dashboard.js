'use strict';
const BASE = '';
let servers = [], tools = [], groups = [], clients = [], auditLogs = [];
let _toolsStdioEnabledCount = 0;
let _toolDiagnostics = [];
const TOKEN_STORAGE_KEY = 'mcpnet.adminToken.enc.v1';
const TOKEN_SALT_KEY = 'mcpnet.adminToken.salt.v1';
let _tokenSaveTimer = null;

function bytesToB64(bytes) {
  let binary = '';
  for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
  return btoa(binary);
}

function b64ToBytes(base64) {
  const binary = atob(base64);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
  return out;
}

function getOrCreateTokenSalt() {
  let salt = localStorage.getItem(TOKEN_SALT_KEY);
  if (!salt) {
    const raw = new Uint8Array(16);
    crypto.getRandomValues(raw);
    salt = bytesToB64(raw);
    localStorage.setItem(TOKEN_SALT_KEY, salt);
  }
  return b64ToBytes(salt);
}

async function deriveTokenKey() {
  if (!window.crypto || !window.crypto.subtle) return null;
  const seed = `${location.origin}|${navigator.userAgent}|${navigator.language}`;
  const enc = new TextEncoder();
  const baseKey = await crypto.subtle.importKey('raw', enc.encode(seed), 'PBKDF2', false, ['deriveKey']);
  return crypto.subtle.deriveKey(
    {
      name: 'PBKDF2',
      salt: getOrCreateTokenSalt(),
      iterations: 150000,
      hash: 'SHA-256'
    },
    baseKey,
    { name: 'AES-GCM', length: 256 },
    false,
    ['encrypt', 'decrypt']
  );
}

async function saveAdminTokenEncrypted(token) {
  if (!token) {
    localStorage.removeItem(TOKEN_STORAGE_KEY);
    return;
  }
  const key = await deriveTokenKey();
  if (!key) return;
  const enc = new TextEncoder();
  const iv = new Uint8Array(12);
  crypto.getRandomValues(iv);
  const cipher = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, enc.encode(token));
  localStorage.setItem(TOKEN_STORAGE_KEY, JSON.stringify({
    iv: bytesToB64(iv),
    data: bytesToB64(new Uint8Array(cipher))
  }));
}

async function restoreAdminTokenEncrypted() {
  const raw = localStorage.getItem(TOKEN_STORAGE_KEY);
  if (!raw) return;
  try {
    const parsed = JSON.parse(raw);
    const key = await deriveTokenKey();
    if (!key) return;
    const plain = await crypto.subtle.decrypt(
      { name: 'AES-GCM', iv: b64ToBytes(parsed.iv) },
      key,
      b64ToBytes(parsed.data)
    );
    const token = new TextDecoder().decode(plain);
    const input = document.getElementById('adminToken');
    if (input) input.value = token;
  } catch {
    localStorage.removeItem(TOKEN_STORAGE_KEY);
  }
}

function scheduleTokenSave() {
  if (_tokenSaveTimer) clearTimeout(_tokenSaveTimer);
  _tokenSaveTimer = setTimeout(() => {
    const input = document.getElementById('adminToken');
    const token = input ? input.value.trim() : '';
    saveAdminTokenEncrypted(token).catch(() => { /* ignore */ });
  }, 180);
}

const PAGE_META = {
  overview: ['Dashboard', 'Gateway overview & activity'],
  servers:  ['Servers', 'Manage upstream MCP servers'],
  catalog:  ['Catalog', 'Browse & install ready-made MCP servers'],
  tools:    ['Tools', 'Aggregated tools across all servers'],
  groups:   ['Groups', 'Logical groupings of tools'],
  clients:  ['Clients', 'API clients & access tokens (enterprise)'],
  audit:    ['Audit Log', 'Recent gateway request history'],
  settings: ['Settings', 'Gateway configuration, backup & restore']
};

function headers() {
  const t = document.getElementById('adminToken').value.trim();
  const h = { 'Content-Type': 'application/json' };
  if (t) h['X-Admin-Token'] = t;
  return h;
}

function setConn(ok, text) {
  const dot = document.getElementById('connDot');
  dot.className = 'status-dot ' + (ok ? 'ok' : 'err');
  document.getElementById('connText').textContent = text;
}

let gatewayMode = 'Dev';
async function loadInfo() {
  try {
    const r = await fetch(BASE + '/api/info');
    if (!r.ok) return;
    const info = await r.json();
    gatewayMode = info.mode || 'Dev';
    const badge = document.getElementById('modeBadge');
    badge.textContent = (gatewayMode || 'Dev').toUpperCase();
    const tokenInput = document.getElementById('adminToken');
    if (info.requiresAuth) {
      badge.style.background = 'linear-gradient(135deg, #f59e0b, #d97706)';
      tokenInput.placeholder = 'Admin token (required)';
    } else {
      tokenInput.placeholder = 'Admin token (dev: optional)';
    }
  } catch { /* ignore */ }
}

async function api(method, path, body) {
  try {
    const r = await fetch(BASE + '/api' + path, {
      method,
      headers: headers(),
      body: body ? JSON.stringify(body) : undefined
    });
    if (r.status === 401) { setConn(false, 'Unauthorized'); toast('Unauthorized', 'Check your admin token.', 'danger'); return null; }
    if (r.status === 204) { setConn(true, 'Connected'); return null; }
    if (!r.ok) { setConn(false, 'Error ' + r.status); toast('Request failed', r.status + ' ' + r.statusText, 'danger'); return null; }
    setConn(true, 'Connected');
    return await r.json();
  } catch (e) { setConn(false, 'Offline'); toast('Network error', e.message, 'danger'); return null; }
}

// ── Toast ────────────────────────────────────────────────────
const TOAST_ICONS = {
  success: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg>',
  danger:  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M15 9l-6 6M9 9l6 6"/></svg>',
  warning: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 9v4M12 17h.01"/><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/></svg>',
  info:    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/></svg>'
};
const TOAST_TITLES = { success: 'Success', danger: 'Error', warning: 'Warning', info: 'Info' };

function toast(a, b, c) {
  let title, msg, type;
  if (c !== undefined || (b !== undefined && !['success','danger','warning','info'].includes(b))) {
    title = a; msg = b || ''; type = c || 'info';
  } else { type = b || 'info'; title = TOAST_TITLES[type]; msg = a; }
  const host = document.getElementById('toastHost');
  const el = document.createElement('div');
  el.className = 'toast ' + type;
  const dur = 4200;
  el.innerHTML = `
    <span class="t-icon">${TOAST_ICONS[type] || TOAST_ICONS.info}</span>
    <div class="t-body"><div class="t-title">${esc(title)}</div>${msg ? `<div class="t-msg">${esc(msg)}</div>` : ''}</div>
    <button class="t-close" aria-label="close"><svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 6l12 12M18 6 6 18"/></svg></button>
    <span class="t-bar" style="animation-duration:${dur}ms"></span>`;
  const remove = () => { el.classList.add('out'); setTimeout(() => el.remove(), 250); };
  el.querySelector('.t-close').onclick = remove;
  host.appendChild(el);
  setTimeout(remove, dur);
}

// ── Confirm dialog ───────────────────────────────────────────
function confirmDialog({ title = 'Are you sure?', message = 'This action cannot be undone.', okText = 'Delete', danger = true } = {}) {
  return new Promise(resolve => {
    document.getElementById('confirmTitle').textContent = title;
    document.getElementById('confirmMsg').textContent = message;
    const ok = document.getElementById('confirmOk');
    const cancel = document.getElementById('confirmCancel');
    ok.textContent = okText;
    ok.className = 'btn ' + (danger ? 'danger' : '');
    const done = (val) => { closeModal('confirmModal'); ok.onclick = null; cancel.onclick = null; resolve(val); };
    ok.onclick = () => done(true);
    cancel.onclick = () => done(false);
    openModal('confirmModal');
  });
}

// ── Path Input dialog (for catalog installs that need a path) ─
let _pathResolve = null, _pathReject = null;
function pathInputDialog({ label = 'Directory Path', description = '', placeholder = '.', defaultValue = '' } = {}) {
  return new Promise((resolve, reject) => {
    _pathResolve = () => { closeModal('pathInputModal'); resolve(document.getElementById('pathModal_input').value.trim() || placeholder); };
    _pathReject  = () => { closeModal('pathInputModal'); reject(null); };
    document.getElementById('pathModal_desc').textContent = description;
    document.getElementById('pathModal_label').textContent = label;
    const inp = document.getElementById('pathModal_input');
    inp.value = defaultValue;
    inp.placeholder = placeholder;
    openModal('pathInputModal');
    setTimeout(() => inp.focus(), 100);
  });
}
function pathModalResolve() { if (_pathResolve) _pathResolve(); }
function pathModalReject()  { if (_pathReject)  _pathReject(); }

// ── Env Var dialog (for catalog installs that need API keys) ─
let _envResolve = null, _envReject = null;
function envVarDialog(requiredEnvs) {
  return new Promise((resolve, reject) => {
    _envResolve = () => {
      closeModal('envVarModal');
      const result = {};
      requiredEnvs.forEach(k => {
        const val = (document.getElementById('envModal_' + k) || {}).value || '';
        if (val.trim()) result[k] = val.trim();
      });
      resolve(result);
    };
    _envReject = () => { closeModal('envVarModal'); reject(null); };

    const body = document.getElementById('envModal_body');
    body.innerHTML = requiredEnvs.map(k => `
      <div class="form-row">
        <label class="form-label">${esc(k)}</label>
        <input id="envModal_${esc(k)}" class="form-input" type="password" placeholder="Paste your ${esc(k)} here" autocomplete="off" />
      </div>`).join('');
    document.getElementById('envModal_note').textContent =
      'These values are stored only in the gateway process environment and are never sent to the browser again.';
    openModal('envVarModal');
    const first = document.getElementById('envModal_' + requiredEnvs[0]);
    if (first) setTimeout(() => first.focus(), 100);
  });
}
function envModalResolve() { if (_envResolve) _envResolve(); }
function envModalReject()  { if (_envReject)  _envReject(); }

function onTokenChange() {
  scheduleTokenSave();
  reloadActive();
}
function reloadActive() {
  const active = document.querySelector('.tab.active');
  const name = active ? active.id.replace('tab-', '') : 'overview';
  loadTab(name);
}

function loadTab(name) {
  if (name === 'overview') loadOverview();
  if (name === 'servers')  loadServers();
  if (name === 'catalog')  loadCatalog();
  if (name === 'tools')    loadTools();
  if (name === 'groups')   loadGroups();
  if (name === 'clients')  loadClients();
  if (name === 'audit')    loadAudit();
  if (name === 'settings') loadSettings();
}

function switchTab(name, btn) {
  document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
  document.querySelectorAll('.side-nav button').forEach(b => b.classList.remove('active'));
  document.getElementById('tab-' + name).classList.add('active');
  if (btn) btn.classList.add('active');
  const meta = PAGE_META[name] || ['', ''];
  document.getElementById('pageTitle').textContent = meta[0];
  document.getElementById('pageSub').textContent = meta[1];
  loadTab(name);
}

function openModal(id)  { document.getElementById(id).classList.add('open'); }
function closeModal(id) { document.getElementById(id).classList.remove('open'); }
document.addEventListener('click',   e => { if (e.target.classList && e.target.classList.contains('modal-backdrop')) e.target.classList.remove('open'); });
document.addEventListener('keydown', e => { if (e.key === 'Escape') document.querySelectorAll('.modal-backdrop.open').forEach(m => m.classList.remove('open')); });

function setCount(name, n) { const el = document.getElementById('navCount-' + name); if (el) el.textContent = n; }

// ── Overview ─────────────────────────────────────────────────
async function loadOverview() {
  const [s, t, g, c, a] = await Promise.all([
    api('GET', '/servers'), api('GET', '/tools'), api('GET', '/groups'), api('GET', '/clients'), api('GET', '/audit')
  ]);
  servers = s || servers; tools = t || tools; groups = g || groups; clients = c || clients; auditLogs = a || auditLogs;
  document.getElementById('mServers').textContent = servers.length;
  document.getElementById('mTools').textContent = tools.length;
  document.getElementById('mToolsEnabled').textContent = tools.filter(x => x.enabled).length;
  document.getElementById('mGroups').textContent = groups.length;
  document.getElementById('mClients').textContent = clients.length;
  setCount('servers', servers.length); setCount('tools', tools.length); setCount('groups', groups.length); setCount('clients', clients.length);
  const tbody = document.getElementById('overviewAuditBody');
  const recent = auditLogs.slice().reverse().slice(0, 8);
  if (!recent.length) { tbody.innerHTML = emptyRow(5, 'No activity yet'); return; }
  tbody.innerHTML = recent.map(x => `
    <tr>
      <td style="white-space:nowrap;font-size:12px">${new Date(x.timestamp).toLocaleString()}</td>
      <td><code>${esc(x.method || '')}</code></td>
      <td>${esc(x.clientName || '-')}</td>
      <td>${esc(x.toolName || '-')}</td>
      <td>${x.success ? '<span class="chip green">OK</span>' : '<span class="chip red">Error</span>'}</td>
    </tr>`).join('');
}

function emptyRow(cols, title, sub) {
  return `<tr><td colspan="${cols}" class="empty">
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7l9-4 9 4v10l-9 4-9-4z"/><path d="M3 7l9 4 9-4M12 11v10"/></svg>
    <div class="empty-title">${esc(title)}</div>${sub ? `<div>${esc(sub)}</div>` : ''}</td></tr>`;
}

function switchTabByName(name) {
  const btn = document.querySelector(`.side-nav button[data-tab="${name}"]`);
  switchTab(name, btn);
}

// ── Servers ───────────────────────────────────────────────────
async function loadServers() {
  const data = await api('GET', '/servers');
  servers = data || [];
  renderServers();
}

function renderServers() {
  const tbody = document.getElementById('serverTableBody');
  setCount('servers', servers.length);
  if (!servers.length) { tbody.innerHTML = emptyRow(7, 'No servers registered', 'Add an upstream MCP server to get started.'); return; }
  tbody.innerHTML = servers.map(s => `
    <tr>
      <td><strong>${esc(s.name)}</strong></td>
      <td><code>${esc(s.url || s.stdioCommand || '')}</code></td>
      <td><span class="chip blue plain">${esc(s.transportType)}</span></td>
      <td>${s.hasAuth ? '<span class="chip green">Auth</span>' : '<span class="chip gray plain">None</span>'}</td>
      <td>
        <span id="health-${s.id}" class="chip gray plain">—</span>
        <button class="btn ghost sm" onclick="checkHealth('${s.id}')" style="padding:3px 8px;margin-left:4px">Ping</button>
      </td>
      <td>
        <label class="toggle-switch" title="${s.enabled ? 'Disable' : 'Enable'} server">
          <input type="checkbox" ${s.enabled ? 'checked' : ''} onchange="toggleServer('${s.id}', this.checked)" />
          <span class="toggle-slider"></span>
        </label>
      </td>
      <td><div class="row-actions">
        <button class="icon-btn" title="Edit server" onclick="openEditServer('${s.id}')"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/></svg></button>
        <button class="icon-btn danger" title="Delete" onclick="deleteServer('${s.id}', '${esc(s.name)}')"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg></button>
      </div></td>
    </tr>
  `).join('');
}

// ── Server Health Check ────────────────────────────────────────
async function checkHealth(id) {
  const server = servers.find(s => s.id === id);
  // Show modal immediately with loading state
  document.getElementById('hm_name').textContent = server ? server.name : id;
  document.getElementById('hm_status').className = 'chip gray plain';
  document.getElementById('hm_status').textContent = 'Checking…';
  document.getElementById('hm_latency').textContent = '—';
  document.getElementById('hm_tools').textContent = '—';
  document.getElementById('hm_log').textContent = 'Connecting to server…';
  openModal('healthModal');

  // Also update inline chip to spinner
  const chipEl = document.getElementById('health-' + id);
  if (chipEl) { chipEl.innerHTML = '<span class="spinner" style="width:12px;height:12px;vertical-align:middle"></span>'; chipEl.className = 'chip gray plain'; }

  const r = await api('GET', '/servers/' + id + '/health');

  if (!r) {
    document.getElementById('hm_status').textContent = 'Failed';
    document.getElementById('hm_log').textContent = 'Request failed. Check gateway logs.';
    if (chipEl) { chipEl.className = 'chip gray plain'; chipEl.textContent = '—'; }
    return;
  }

  // Update modal fields
  document.getElementById('hm_latency').textContent = r.latencyMs != null ? r.latencyMs + ' ms' : '—';
  document.getElementById('hm_tools').textContent = r.toolCount != null ? r.toolCount + ' tools' : '—';

  if (r.status === 'healthy') {
    document.getElementById('hm_status').className = 'chip green';
    document.getElementById('hm_status').textContent = 'Healthy';
    let logMsg = 'Connection established. Tool list retrieved successfully.';
    if (r.runtimeInfo) logMsg += '\n\nRuntime: ' + r.runtimeInfo;
    if (r.command)     logMsg += '\nCommand: ' + r.command;
    document.getElementById('hm_log').textContent = logMsg;
    if (chipEl) { chipEl.className = 'chip green'; chipEl.textContent = r.latencyMs + 'ms · ' + r.toolCount + ' tools'; }
  } else if (r.status === 'warning') {
    document.getElementById('hm_status').className = 'chip yellow';
    document.getElementById('hm_status').textContent = 'Warning';
    let logMsg = r.note || 'Connected but returned 0 tools.';
    if (r.runtimeInfo) logMsg += '\n\nRuntime: ' + r.runtimeInfo;
    if (r.command)     logMsg += '\nCommand: ' + r.command;
    document.getElementById('hm_log').textContent = logMsg;
    if (chipEl) { chipEl.className = 'chip yellow'; chipEl.textContent = '0 tools'; }
  } else if (r.status === 'stdio') {
    // Legacy fallback (should not occur after the endpoint update)
    document.getElementById('hm_status').className = 'chip blue plain';
    document.getElementById('hm_status').textContent = 'Stdio';
    const note = r.note || 'Stdio server — started as subprocess on first tool call.';
    const cmd = r.command ? '\n\nCommand: ' + r.command : '';
    document.getElementById('hm_log').textContent = note + cmd;
    if (chipEl) { chipEl.className = 'chip blue plain'; chipEl.textContent = 'Local'; }
  } else {
    document.getElementById('hm_status').className = 'chip red';
    document.getElementById('hm_status').textContent = 'Unhealthy';
    let logMsg = r.error || 'Connection failed. Server may be offline or unreachable.';
    if (r.runtimeInfo) logMsg += '\n\nRuntime: ' + r.runtimeInfo;
    if (r.command)     logMsg += '\nCommand: ' + r.command;
    document.getElementById('hm_log').textContent = logMsg;
    if (chipEl) { chipEl.className = 'chip red'; chipEl.textContent = 'Unhealthy'; }
  }
}

function openAddServer() {
  ['s_name','s_url','s_token','s_command','s_args','s_workdir','s_headers','s_oauth_url','s_oauth_id','s_oauth_secret','s_oauth_scopes'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.value = '';
  });
  document.getElementById('s_oauth_enabled').checked = false;
  openModal('addServerModal');
}

function parseHeaders(text) {
  const h = {};
  (text || '').split('\n').map(l => l.trim()).filter(Boolean).forEach(line => {
    const idx = line.indexOf(':');
    if (idx > 0) h[line.slice(0, idx).trim()] = line.slice(idx + 1).trim();
  });
  return h;
}

function buildOAuth(prefix) {
  const enabled = document.getElementById(prefix + '_enabled').checked;
  if (!enabled) return null;
  return {
    enabled: true,
    tokenUrl:     document.getElementById(prefix + '_url').value.trim(),
    clientId:     document.getElementById(prefix + '_id').value.trim(),
    clientSecret: document.getElementById(prefix + '_secret').value.trim(),
    scopes: document.getElementById(prefix + '_scopes').value.split(/\s+/).map(s => s.trim()).filter(Boolean)
  };
}

function updateServerForm() {
  const t = document.getElementById('s_transport').value;
  document.getElementById('s_httpFields').style.display = t !== 'Stdio' ? '' : 'none';
  document.getElementById('s_stdioFields').style.display = t === 'Stdio' ? '' : 'none';
}

async function addServer() {
  const transport = document.getElementById('s_transport').value;
  const hdrs = parseHeaders(document.getElementById('s_headers').value);
  const body = {
    name:         document.getElementById('s_name').value.trim(),
    transportType: transport,
    url:          transport !== 'Stdio' ? document.getElementById('s_url').value.trim() : null,
    bearerToken:  transport !== 'Stdio' ? (document.getElementById('s_token').value.trim() || null) : null,
    stdioCommand: transport === 'Stdio' ? document.getElementById('s_command').value.trim() : null,
    stdioArgs:    transport === 'Stdio' ? document.getElementById('s_args').value.split('\n').map(l => l.trim()).filter(Boolean) : [],
    stdioWorkingDirectory: transport === 'Stdio' ? (document.getElementById('s_workdir').value.trim() || null) : null,
    customHeaders: Object.keys(hdrs).length ? hdrs : null,
    oAuth: buildOAuth('s_oauth')
  };
  if (!body.name) { toast('Name is required', 'danger'); return; }
  const result = await api('POST', '/servers', body);
  if (result) { closeModal('addServerModal'); await loadServers(); toast('Server added', body.name + ' is now registered.', 'success'); }
}

async function deleteServer(id, name) {
  if (!(await confirmDialog({ title: 'Delete server', message: `"${name}" and its tools will be removed.`, okText: 'Delete' }))) return;
  await api('DELETE', '/servers/' + id);
  await loadServers();
  toast('Server deleted', name + ' was removed.', 'success');
}

// ── Catalog ───────────────────────────────────────────────────
let catalog = [];
const VERIFIED_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M9 12l2 2 4-4"/><circle cx="12" cy="12" r="9"/></svg>';

async function loadCatalog() {
  const grid = document.getElementById('catalogGrid');
  grid.innerHTML = '<div class="empty"><span class="spinner"></span> Loading catalog…</div>';
  try {
    const r = await fetch(BASE + '/api/catalog', {
      cache: 'no-store',
      headers: headers()
    });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const data = await r.json();
    catalog = data.servers || [];
    const upd = data.updatedAt ? new Date(data.updatedAt).toLocaleString() : '';
    const meta = document.getElementById('catalogMeta');
    if (meta) meta.textContent = `${catalog.length} servers • Updated: ${upd}`;
    renderCatalog();
  } catch (e) {
    catalog = [];
    grid.innerHTML = `<div class="empty">Failed to load catalog (${esc(e.message)}).</div>`;
  }
}

function onCatalogSearch() {
  renderCatalog();
}

function catalogCommand(c) {
  return c.transport === 'Stdio' ? [c.command].concat(c.args || []).join(' ') : (c.url || '');
}

function renderCatalog() {
  const grid = document.getElementById('catalogGrid');
  const q = (document.getElementById('catalogSearch').value || '').trim().toLowerCase();
  const installed = new Set(servers.map(s => (s.name || '').toLowerCase()));
  const list = !q ? catalog : catalog.filter(c =>
    (c.title + ' ' + (c.name||'') + ' ' + (c.description||'') + ' ' + (c.category||'')).toLowerCase().includes(q));
  const meta = document.getElementById('catalogMeta');
  if (meta) meta.textContent = `${list.length} / ${catalog.length} servers`;
  if (!list.length) { grid.innerHTML = '<div class="empty">No catalog entries match your search.</div>'; return; }
  grid.innerHTML = list.map(c => {
    const i = catalog.indexOf(c);
    const isInstalled = installed.has((c.name || '').toLowerCase());
    const isCustom = c.source === 'custom';
    const logo = (c.title || c.name || '?').trim().charAt(0).toUpperCase();
    return `
    <div class="cat-card">
      <div class="cat-head">
        <div class="cat-logo">${esc(logo)}</div>
        <div>
          <div class="cat-title">${esc(c.title || c.name)}${c.verified ? `<span class="verified-badge" title="Verified">${VERIFIED_ICON}</span>` : ''}</div>
          <div class="cat-cat">${esc(c.category || 'general')}</div>
        </div>
      </div>
      <div class="cat-desc">${esc(c.description || '')}</div>
      <div class="cat-meta">
        <span class="chip blue plain">${esc(c.transport)}</span>
        ${c.requiresNode ? '<span class="chip gray plain">needs Node/npx</span>' : ''}
        ${c.requiresPython ? '<span class="chip gray plain">needs Python/uvx</span>' : ''}
        ${c.requiresDirectory ? '<span class="chip orange plain">needs path</span>' : ''}
        ${(c.requiresEnv && c.requiresEnv.length > 0) ? `<span class="chip yellow plain" title="Needs: ${esc(c.requiresEnv.join(', '))}">needs API key</span>` : ''}
        ${isCustom ? '<span class="chip green plain" title="Added by you">custom</span>' : ''}
      </div>
      <div class="cmd-preview">${esc(catalogCommand(c))}</div>
      <div class="cat-foot">
        ${c.homepage ? `<a class="btn ghost" href="${esc(c.homepage)}" target="_blank" rel="noopener">Docs</a>` : ''}
        ${isCustom ? `<button class="icon-btn danger" title="Remove" onclick="removeCustomCatalog('${esc(c.name)}')">✕</button>` : ''}
        <span class="spacer"></span>
        ${isInstalled
          ? '<button class="btn ghost" disabled>Installed</button>'
          : `<button class="btn" onclick="installFromCatalog(${i})">
               <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12M7 10l5 5 5-5M5 21h14"/></svg>
               Install
             </button>`}
      </div>
    </div>`;
  }).join('');
}

async function installFromCatalog(i) {
  const c = catalog[i];
  if (!c) return;
  const isStdio = c.transport === 'Stdio';
  let finalArgs = [...(c.args || [])];
  let envVars = {};

  // For servers that require a directory path (e.g. filesystem), prompt the user
  if (c.requiresDirectory) {
    let dir;
    try {
      dir = await pathInputDialog({
        label: 'Allowed Directory Path',
        description: `"${c.title}" needs a directory path that clients can access. Enter the full path (e.g. C:\\Users\\me\\Documents or /home/user/docs).`,
        placeholder: '.',
        defaultValue: '.'
      });
    } catch { return; } // user cancelled
    if (finalArgs.length > 0) finalArgs[finalArgs.length - 1] = dir;
    else finalArgs.push(dir);
  }

  // For servers that require API keys or other secrets, prompt for env vars
  if (isStdio && c.requiresEnv && c.requiresEnv.length > 0) {
    try {
      envVars = await envVarDialog(c.requiresEnv);
    } catch { return; } // user cancelled
  }

  const cmd = isStdio ? [c.command].concat(finalArgs).join(' ') : (c.url || '');
  const envNote = Object.keys(envVars).length > 0
    ? `\n\nEnvironment variables will be set: ${Object.keys(envVars).join(', ')}`
    : (c.requiresEnv && c.requiresEnv.length > 0 ? `\n\n⚠️ No API keys provided — server may fail to start.` : '');
  const message = isStdio
    ? `This will register "${c.title}" and run a local process:\n\n${cmd}${envNote}\n\nOnly install commands you trust. Requires Node/npx on the host.`
    : `This will register "${c.title}" and connect to:\n\n${cmd}`;

  if (!(await confirmDialog({ title: 'Install ' + (c.title || c.name), message, okText: 'Install', danger: isStdio }))) return;

  const body = {
    name: c.name,
    transportType: c.transport,
    url:           !isStdio ? c.url : null,
    bearerToken:   null,
    stdioCommand:  isStdio ? c.command : null,
    stdioArgs:     isStdio ? finalArgs : [],
    stdioEnvVars:  Object.keys(envVars).length > 0 ? envVars : undefined
  };
  const result = await api('POST', '/servers', body);
  if (result) {
    toast('Installed', (c.title || c.name) + ' is now registered.', 'success');
    await loadServers();
    renderCatalog();
  }
}

function showAddCatalogModal() {
  document.getElementById('ac_title').value = '';
  document.getElementById('ac_command').value = '';
  document.getElementById('ac_desc').value = '';
  document.getElementById('ac_category').value = 'Development';
  openModal('addCatalogModal');
}

async function submitAddCatalog() {
  const title = document.getElementById('ac_title').value.trim();
  const rawCmd = document.getElementById('ac_command').value.trim();
  const desc = document.getElementById('ac_desc').value.trim();
  const category = document.getElementById('ac_category').value;
  if (!title) { toast('Error', 'Name is required.', 'error'); return; }
  if (!rawCmd) { toast('Error', 'Command is required.', 'error'); return; }

  const parts = rawCmd.match(/(?:[^\s"]+|"[^"]*")+/g) || rawCmd.split(/\s+/);
  const command = parts[0];
  const args = parts.slice(1);

  const isHttp = command.startsWith('http://') || command.startsWith('https://');
  const transport = isHttp ? 'StreamableHttp' : 'Stdio';

  const body = {
    title,
    description: desc,
    category,
    transport,
    command: isHttp ? '' : command,
    args: isHttp ? [] : args,
    url: isHttp ? rawCmd : null,
    requiresNode: command === 'npx',
    requiresPython: command === 'uvx' || command === 'uv',
    source: 'custom'
  };

  const r = await fetch(BASE + '/api/catalog/custom', {
    method: 'POST',
    headers: headers(),
    body: JSON.stringify(body)
  });
  if (!r.ok) {
    const msg = await r.text();
    toast('Error', msg || 'Failed to add entry.', 'error');
    return;
  }
  closeModal('addCatalogModal');
  await loadCatalog();
  toast('Added', `"${title}" added to catalog.`, 'success');
}

async function removeCustomCatalog(name) {
  if (!(await confirmDialog({ title: 'Remove', message: `Remove "${name}" from the catalog?`, okText: 'Remove', danger: true }))) return;
  const r = await fetch(BASE + '/api/catalog/custom/' + encodeURIComponent(name), {
    method: 'DELETE',
    headers: headers()
  });
  if (!r.ok) { toast('Error', 'Failed to remove entry.', 'error'); return; }
  await loadCatalog();
  toast('Removed', 'Entry removed from catalog.', 'info');
}

async function refreshAll() {
  toast('Refreshing tools…', 'Connecting to upstream servers. Stdio servers may take up to 60s on first run.', 'info');
  const result = await api('POST', '/tools/refresh');
  if (!result) { toast('Refresh failed', 'Could not reach gateway.', 'error'); return; }

  // Poll /api/tools/status.
  // Fast servers update the cache immediately — load tools as soon as any appear.
  // Keep polling until refreshing=false (all servers done) or 130s timeout.
  const deadline = Date.now() + 130000;
  let status = null;
  let lastToolCount = 0;
  while (Date.now() < deadline) {
    await new Promise(r => setTimeout(r, 2000));
    status = await api('GET', '/tools/status');
    if (!status) break;
    // Incrementally load tools as fast servers finish — don't wait for slow ones.
    if (status.totalTools > lastToolCount) {
      lastToolCount = status.totalTools;
      await loadTools();
    }
    if (!status.refreshing) break;
  }

  const diags = (status && status.diagnostics) ? Array.from(status.diagnostics) : [];
  const warnings = diags.filter(d => d.status && d.status !== 'ok');
  if (warnings.length > 0) {
    toast('Tools refreshed with warnings', `${warnings.length} server(s) reported issues. ${status.totalTools} tools total.`, 'warning');
  } else if (status) {
    toast('Tools refreshed', `${status.totalTools} tools loaded from ${diags.filter(d => d.success).length}/${diags.length} server(s).`, 'success');
  } else {
    toast('Tools refreshed', 'Refresh complete.', 'success');
  }

  // Final load to ensure we're in sync, then update health chips.
  await loadTools();
  updateHealthChipsFromDiagnostics(diags);
}

function updateHealthChipsFromDiagnostics(diags) {
  if (!diags || !diags.length) return;
  for (const d of diags) {
    const s = servers.find(x => x.id === d.serverId || x.name === d.serverName);
    if (!s) continue;
    const chip = document.getElementById('health-' + s.id);
    if (!chip) continue;
    if (d.success) {
      chip.className = 'chip green';
      chip.textContent = `${d.durationMs}ms · ${d.toolCount} tools`;
      chip.title = '';
    } else {
      chip.className = 'chip red';
      chip.textContent = 'Error';
      chip.title = d.errorMessage || 'Connection failed';
    }
  }
}

// ── Tools ─────────────────────────────────────────────────────
async function loadTools() {
  const [toolData, serverData, diagnosticsData] = await Promise.all([
    api('GET', '/tools'),
    api('GET', '/servers'),
    api('GET', '/tools/diagnostics')
  ]);
  tools = toolData || [];
  _toolDiagnostics = diagnosticsData || [];
  if (serverData) {
    servers = serverData;
    _toolsStdioEnabledCount = servers.filter(s => s.enabled && s.transportType === 'Stdio').length;
  } else {
    _toolsStdioEnabledCount = 0;
  }
  renderToolDiagnostics();
  renderTools();
}

function renderToolDiagnostics() {
  const box = document.getElementById('toolsDiag');
  if (!box) return;

  const issues = (_toolDiagnostics || []).filter(x => x.status && x.status.toLowerCase() !== 'ok');
  if (!issues.length) {
    box.style.display = 'none';
    box.innerHTML = '';
    return;
  }

  const lines = issues.slice(0, 6).map(x => {
    const status = (x.status || '').toLowerCase();
    const kind = status === 'unsupported' ? 'Unsupported' : (status === 'warning' ? 'Warning' : 'Error');
    const msg = x.errorMessage || 'Unknown issue';
    return `<div style="margin:4px 0"><strong>${esc(x.serverName)}</strong> <span class="chip orange plain" style="margin-left:6px">${esc(kind)}</span> <span style="color:var(--text-dim);margin-left:8px">${esc(msg)}</span></div>`;
  }).join('');

  const more = issues.length > 6
    ? `<div style="margin-top:6px;color:var(--muted);font-size:12px">+${issues.length - 6} more issue(s)</div>`
    : '';

  box.style.display = 'block';
  box.innerHTML = `
    <div style="font-weight:700;color:#fbbf24;margin-bottom:6px">Tool Discovery Diagnostics</div>
    <div style="font-size:12.5px;color:var(--text-dim);margin-bottom:6px">Some enabled servers could not contribute tools to Aggregated Tools.</div>
    ${lines}
    ${more}
  `;
}

function renderTools() {
  const q = (document.getElementById('toolSearch')?.value || '').toLowerCase();
  const filtered = q ? tools.filter(t => t.fullName.toLowerCase().includes(q)) : tools;
  const tbody = document.getElementById('toolTableBody');
  setCount('tools', tools.length);
  if (!filtered.length) {
    if (!q && _toolsStdioEnabledCount > 0) {
      tbody.innerHTML = emptyRow(
        3,
        'No tools found',
        'You have enabled Stdio server(s). Stdio upstream tool discovery is not supported yet, so tools from local process servers (e.g. filesystem) cannot be listed here.'
      );
      return;
    }
    tbody.innerHTML = emptyRow(3, q ? 'No matching tools' : 'No tools found', q ? '' : 'Register a server and refresh tools.');
    return;
  }
  tbody.innerHTML = filtered.map(t => `
    <tr>
      <td><code>${esc(t.fullName)}</code></td>
      <td>${esc(t.serverName)}</td>
      <td style="text-align:right">
        <button class="btn ghost sm" style="padding:3px 10px;margin-right:8px" onclick="openToolTest('${esc(t.fullName)}')">Test</button>
        <label class="toggle-switch" style="vertical-align:middle">
          <input type="checkbox" ${t.enabled ? 'checked' : ''} onchange="toggleTool('${esc(t.fullName)}', this.checked)" />
          <span class="toggle-slider"></span>
        </label>
      </td>
    </tr>
  `).join('');
}

// ── Tool Inspector ─────────────────────────────────────────────
function openToolTest(fullName) {
  document.getElementById('tt_name').textContent = fullName;
  document.getElementById('tt_args').value = '{}';
  document.getElementById('tt_result').textContent = '—';
  document.getElementById('tt_status').textContent = '';
  openModal('toolTestModal');
}

async function runToolTest() {
  const fullName = document.getElementById('tt_name').textContent;
  let args;
  try { args = JSON.parse(document.getElementById('tt_args').value || '{}'); }
  catch (e) { toast('Invalid JSON', e.message, 'danger'); return; }
  document.getElementById('tt_status').textContent = 'Running…';
  document.getElementById('tt_result').textContent = '…';
  const r = await api('POST', '/tools/call', { fullName, arguments: args });
  if (!r) { document.getElementById('tt_status').textContent = 'Failed'; return; }
  document.getElementById('tt_status').textContent = (r.success ? '✓ ' : '✗ ') + (r.durationMs != null ? r.durationMs + 'ms' : '');
  const out = r.error ? { error: r.error } : (r.content ?? r);
  document.getElementById('tt_result').textContent = JSON.stringify(out, null, 2);
}

async function toggleTool(fullName, enabled) {
  const parts = fullName.split('__');
  if (parts.length < 2) return;
  const serverId = servers.find(s => s.name === parts[0])?.id;
  if (!serverId) { toast('Server not found for tool', 'danger'); return; }
  await api('PATCH', `/servers/${serverId}/tools/${encodeURIComponent(fullName)}/toggle?enabled=${enabled}`);
  const t = tools.find(x => x.fullName === fullName);
  if (t) t.enabled = enabled;
}

// ── Groups ─────────────────────────────────────────────────────
async function loadGroups() {
  const data = await api('GET', '/groups');
  groups = data || [];
  renderGroups();
}

function renderGroups() {
  const tbody = document.getElementById('groupTableBody');
  setCount('groups', groups.length);
  if (!groups.length) { tbody.innerHTML = emptyRow(4, 'No groups defined', 'Create a group to bundle related tools.'); return; }
  tbody.innerHTML = groups.map(g => `
    <tr>
      <td><strong>${esc(g.name)}</strong></td>
      <td>${esc(g.description || '')}</td>
      <td><span class="chip blue plain">${(g.toolNames || []).length} tools</span></td>
      <td><div class="row-actions">
        <button class="btn ghost sm" onclick="openGroupTools('${g.id}', '${esc(g.name)}')">Manage Tools</button>
        <button class="icon-btn danger" title="Delete" onclick="deleteGroup('${g.id}', '${esc(g.name)}')"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg></button>
      </div></td>
    </tr>
  `).join('');
}

function openAddGroup() { document.getElementById('g_name').value = ''; document.getElementById('g_desc').value = ''; openModal('addGroupModal'); }

async function addGroup() {
  const body = { name: document.getElementById('g_name').value.trim(), description: document.getElementById('g_desc').value.trim() || null };
  if (!body.name) { toast('Name is required', 'danger'); return; }
  const result = await api('POST', '/groups', body);
  if (result) { closeModal('addGroupModal'); await loadGroups(); toast('Group created', body.name + ' is ready.', 'success'); }
}

async function deleteGroup(id, name) {
  if (!(await confirmDialog({ title: 'Delete group', message: `"${name}" will be removed.`, okText: 'Delete' }))) return;
  await api('DELETE', '/groups/' + id);
  await loadGroups();
  toast('Group deleted', name + ' was removed.', 'success');
}

// ── Clients ────────────────────────────────────────────────────
async function loadClients() {
  const data = await api('GET', '/clients');
  clients = data || [];
  renderClients();
}

function renderClients() {
  const tbody = document.getElementById('clientTableBody');
  setCount('clients', clients.length);
  if (!clients.length) { tbody.innerHTML = emptyRow(5, 'No clients', 'Clients require enterprise mode.'); return; }
  tbody.innerHTML = clients.map(c => `
    <tr>
      <td><strong>${esc(c.name)}</strong></td>
      <td>
        <label class="toggle-switch" title="${c.enabled ? 'Disable' : 'Enable'} client">
          <input type="checkbox" ${c.enabled ? 'checked' : ''} onchange="toggleClient('${c.id}', this.checked)" />
          <span class="toggle-slider"></span>
        </label>
      </td>
      <td>${c.allowedServers === 0 ? '<span class="chip gray plain">All</span>' : '<span class="chip blue plain">' + c.allowedServers + ' servers</span>'}</td>
      <td>${c.allowedGroups === 0 ? '<span class="chip gray plain">All</span>' : '<span class="chip blue plain">' + c.allowedGroups + ' groups</span>'}</td>
      <td><div class="row-actions">
        <button class="btn ghost sm" onclick="openClientPerms('${c.id}')">Permissions</button>
        <button class="icon-btn danger" title="Delete" onclick="deleteClient('${c.id}', '${esc(c.name)}')"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg></button>
      </div></td>
    </tr>
  `).join('');
}

function openAddClient() { document.getElementById('c_name').value = ''; openModal('addClientModal'); }

async function addClient() {
  const body = { name: document.getElementById('c_name').value.trim() };
  if (!body.name) { toast('Name is required', 'danger'); return; }
  const result = await api('POST', '/clients', body);
  if (result) { closeModal('addClientModal'); await loadClients(); document.getElementById('tokenDisplay').textContent = result.bearerToken; openModal('tokenModal'); }
}

async function deleteClient(id, name) {
  if (!(await confirmDialog({ title: 'Delete client', message: `"${name}" and its token will be revoked.`, okText: 'Delete' }))) return;
  await api('DELETE', '/clients/' + id);
  await loadClients();
  toast('Client deleted', name + ' was removed.', 'success');
}

function copyToken() { navigator.clipboard.writeText(document.getElementById('tokenDisplay').textContent); toast('Copied', 'Token copied to clipboard.', 'success'); }

// ── Server Edit + Toggle ────────────────────────────────────────
async function toggleServer(id, enabled) {
  await api('PATCH', `/servers/${id}/toggle?enabled=${enabled}`);
  const s = servers.find(x => x.id === id);
  if (s) s.enabled = enabled;
  toast(enabled ? 'Server enabled' : 'Server disabled', '', 'success');
}

let _editServerId = null;
async function openEditServer(id) {
  const s = await api('GET', '/servers/' + id);
  if (!s) return;
  _editServerId = id;
  document.getElementById('es_name').value  = s.name || '';
  document.getElementById('es_transport').value = s.transportType || 'StreamableHttp';
  document.getElementById('es_url').value   = s.url || '';
  document.getElementById('es_token').value = '';
  document.getElementById('es_command').value = s.stdioCommand || '';
  document.getElementById('es_args').value  = (s.stdioArgs || []).join('\n');
  document.getElementById('es_workdir').value = s.stdioWorkingDirectory || '';
  const hdrs = s.customHeaders || {};
  document.getElementById('es_headers').value = Object.keys(hdrs).map(k => k + ': ' + hdrs[k]).join('\n');
  const o = s.oAuth || s.oauth;
  document.getElementById('es_oauth_enabled').checked = !!(o && o.enabled);
  document.getElementById('es_oauth_url').value = o ? (o.tokenUrl || '') : '';
  document.getElementById('es_oauth_id').value  = o ? (o.clientId || '') : '';
  document.getElementById('es_oauth_secret').value = '';
  document.getElementById('es_oauth_scopes').value = o ? (o.scopes || []).join(' ') : '';
  updateEditServerForm();
  openModal('editServerModal');
}

function updateEditServerForm() {
  const t = document.getElementById('es_transport').value;
  document.getElementById('es_httpFields').style.display = t !== 'Stdio' ? '' : 'none';
  document.getElementById('es_stdioFields').style.display = t === 'Stdio' ? '' : 'none';
}

async function saveEditServer() {
  const transport = document.getElementById('es_transport').value;
  const hdrs = parseHeaders(document.getElementById('es_headers').value);
  const body = {
    name:         document.getElementById('es_name').value.trim(),
    transportType: transport,
    url:          transport !== 'Stdio' ? document.getElementById('es_url').value.trim() : null,
    bearerToken:  transport !== 'Stdio' ? (document.getElementById('es_token').value.trim() || null) : null,
    stdioCommand: transport === 'Stdio' ? document.getElementById('es_command').value.trim() : null,
    stdioArgs:    transport === 'Stdio' ? document.getElementById('es_args').value.split('\n').map(l => l.trim()).filter(Boolean) : [],
    stdioWorkingDirectory: transport === 'Stdio' ? (document.getElementById('es_workdir').value.trim() || null) : null,
    customHeaders: Object.keys(hdrs).length ? hdrs : null,
    oAuth: buildOAuth('es_oauth')
  };
  if (!body.name) { toast('Name is required', '', 'danger'); return; }
  const result = await api('PUT', '/servers/' + _editServerId, body);
  if (result) { closeModal('editServerModal'); await loadServers(); toast('Server updated', body.name + ' was updated.', 'success'); }
}

// ── Group Tools Management ──────────────────────────────────────
let _groupToolsId = null;
async function openGroupTools(groupId, groupName) {
  _groupToolsId = groupId;
  document.getElementById('gtModal_title').textContent = 'Tools in "' + groupName + '"';
  document.getElementById('gtModal_body').innerHTML = '<div style="padding:24px;text-align:center"><span class="spinner"></span></div>';
  openModal('groupToolsModal');
  const [allTools, allGroups] = await Promise.all([api('GET', '/tools'), api('GET', '/groups')]);
  tools = allTools || tools;
  groups = allGroups || groups;
  const groupData = groups.find(g => g.id === groupId);
  const groupToolNames = new Set(groupData?.toolNames || []);
  if (!tools.length) {
    document.getElementById('gtModal_body').innerHTML = '<div class="empty" style="padding:24px">No tools found. Register a server and refresh tools first.</div>';
    return;
  }
  const html = '<div class="form-check-list">' + tools.map(t => `
    <label class="form-check">
      <input type="checkbox" ${groupToolNames.has(t.fullName) ? 'checked' : ''} onchange="toggleGroupTool('${groupId}', '${esc(t.fullName)}', this.checked)" />
      <span class="fc-main"><code>${esc(t.fullName)}</code></span>
      <span class="fc-sub">${esc(t.serverName)}</span>
    </label>
  `).join('') + '</div>';
  document.getElementById('gtModal_body').innerHTML = html;
}

async function toggleGroupTool(groupId, toolName, add) {
  if (add) await api('POST', '/groups/' + groupId + '/tools', { toolName });
  else     await api('DELETE', '/groups/' + groupId + '/tools/' + encodeURIComponent(toolName));
  const g = groups.find(x => x.id === groupId);
  if (g) {
    if (add && !g.toolNames.includes(toolName)) g.toolNames.push(toolName);
    if (!add) g.toolNames = g.toolNames.filter(x => x !== toolName);
  }
  renderGroups();
}

// ── Client Permissions Management ────────────────────────────────
let _clientPermsId = null;

async function toggleClient(id, enabled) {
  const c = await api('GET', '/clients/' + id);
  if (!c) return;
  await api('PUT', '/clients/' + id, { enabled, allowedServerIds: c.allowedServerIds, allowedGroupIds: c.allowedGroupIds, rateLimitPerMinute: c.rateLimitPerMinute || 0 });
  const cl = clients.find(x => x.id === id);
  if (cl) cl.enabled = enabled;
  toast(enabled ? 'Client enabled' : 'Client disabled', '', 'success');
}

async function regenerateToken() {
  if (!_clientPermsId) return;
  if (!(await confirmDialog({ title: 'Regenerate token', message: 'The old token will stop working immediately. Continue?', okText: 'Regenerate', danger: true }))) return;
  const r = await api('POST', '/clients/' + _clientPermsId + '/regenerate');
  if (r && r.bearerToken) {
    // Ensure token modal is visible above client permissions modal.
    closeModal('clientPermsModal');
    document.getElementById('tokenDisplay').textContent = r.bearerToken;
    openModal('tokenModal');
  }
}

async function openClientPerms(id) {
  _clientPermsId = id;
  document.getElementById('cpModal_clientName').textContent = '…';
  document.getElementById('cpModal_body').innerHTML = '<div style="padding:24px;text-align:center"><span class="spinner"></span></div>';
  openModal('clientPermsModal');
  const [c, allServers, allGroups] = await Promise.all([api('GET', '/clients/' + id), api('GET', '/servers'), api('GET', '/groups')]);
  if (!c) { closeModal('clientPermsModal'); return; }
  servers = allServers || servers;
  groups = allGroups || groups;
  document.getElementById('cpModal_clientName').textContent = c.name;
  document.getElementById('cpModal_enabled').checked = c.enabled;
  document.getElementById('cpModal_rate').value = c.rateLimitPerMinute || 0;
  const allowedServerIds = new Set(c.allowedServerIds || []);
  const allowedGroupIds  = new Set(c.allowedGroupIds  || []);
  // Build map of existing per-server limits
  const serverRateLimits = new Map((c.serverRateLimits || []).map(r => [r.serverId, r.limitPerMinute]));

  const serversHtml = servers.length
    ? '<div class="form-check-list">' + servers.map(s => `
        <label class="form-check">
          <input type="checkbox" class="cp_server" value="${s.id}" ${allowedServerIds.has(s.id) ? 'checked' : ''} />
          <span class="fc-main"><strong>${esc(s.name)}</strong></span>
          <span class="fc-sub"><span class="chip blue plain" style="font-size:11px">${esc(s.transportType)}</span></span>
          <input type="number" class="fc-rate cp_server_rate" data-sid="${s.id}" min="0" value="${serverRateLimits.get(s.id) || 0}" placeholder="∞" title="Rate limit for this server (calls/min). 0 = use global limit." />
        </label>`).join('') +
      '<div class="fc-rate-hint">calls/min per server &nbsp;·&nbsp; 0 = use global limit</div></div>'
    : '<div style="padding:12px;color:var(--muted)">No servers registered.</div>';

  const groupsHtml = groups.length
    ? '<div class="form-check-list">' + groups.map(g => `
        <label class="form-check" style="grid-template-columns:18px 1fr auto">
          <input type="checkbox" class="cp_group" value="${g.id}" ${allowedGroupIds.has(g.id) ? 'checked' : ''} />
          <span class="fc-main"><strong>${esc(g.name)}</strong></span>
          <span class="fc-sub">${(g.toolNames||[]).length} tools</span>
        </label>`).join('') + '</div>'
    : '<div style="padding:12px;color:var(--muted)">No groups defined.</div>';

  document.getElementById('cpModal_body').innerHTML = `
    <p style="color:var(--muted);font-size:12px;margin-bottom:14px">Leave all unchecked to allow access to everything (no restriction).</p>
    <div style="margin-bottom:16px">
      <div style="font-weight:600;font-size:13px;margin-bottom:8px;color:var(--text-dim)">Servers</div>
      ${serversHtml}
    </div>
    <div>
      <div style="font-weight:600;font-size:13px;margin-bottom:8px;color:var(--text-dim)">Tool Groups</div>
      ${groupsHtml}
    </div>`;
}

async function saveClientPerms() {
  const checkedServers = [...document.querySelectorAll('.cp_server:checked')].map(x => x.value);
  const checkedGroups  = [...document.querySelectorAll('.cp_group:checked')].map(x => x.value);
  const enabled = document.getElementById('cpModal_enabled').checked;
  const rateLimitPerMinute = parseInt(document.getElementById('cpModal_rate').value, 10) || 0;
  const serverRateLimits = [...document.querySelectorAll('.cp_server_rate')]
    .filter(x => parseInt(x.value, 10) > 0)
    .map(x => ({ serverId: x.dataset.sid, limitPerMinute: parseInt(x.value, 10) }));
  const result = await api('PUT', '/clients/' + _clientPermsId, { enabled, allowedServerIds: checkedServers, allowedGroupIds: checkedGroups, rateLimitPerMinute, serverRateLimits });
  if (result) { closeModal('clientPermsModal'); await loadClients(); toast('Permissions saved', 'Client updated.', 'success'); }
}

// ── Audit ──────────────────────────────────────────────────────
async function loadAudit() { const data = await api('GET', '/audit'); auditLogs = data || []; renderAudit(); }

function renderAudit() {
  const tbody = document.getElementById('auditTableBody');
  const q = (document.getElementById('auditFilter')?.value || '').toLowerCase();
  const status = document.getElementById('auditStatus')?.value || '';
  let list = auditLogs.slice().reverse();
  if (q) list = list.filter(a => (`${a.clientName||''} ${a.serverName||''} ${a.toolName||''} ${a.method||''}`).toLowerCase().includes(q));
  if (status === 'ok')  list = list.filter(a => a.success);
  if (status === 'err') list = list.filter(a => !a.success);
  if (!list.length) { tbody.innerHTML = emptyRow(7, 'No audit entries', 'Requests will appear here as they happen.'); return; }
  tbody.innerHTML = list.map(a => `
    <tr>
      <td style="white-space:nowrap;font-size:12px">${new Date(a.timestamp).toLocaleString()}</td>
      <td><code>${esc(a.method || '')}</code></td>
      <td>${esc(a.clientName || '-')}</td>
      <td>${esc(a.serverName || '-')}</td>
      <td>${esc(a.toolName || '-')}</td>
      <td>${a.durationMs != null ? a.durationMs + 'ms' : '-'}</td>
      <td>${a.success ? '<span class="chip green">OK</span>' : '<span class="chip red">Error</span>'}</td>
    </tr>
  `).join('');
}

function exportAuditCsv() {
  if (!auditLogs.length) { toast('Nothing to export', '', 'warning'); return; }
  const rows = [['Time','Method','Client','Server','Tool','DurationMs','Success','Error']];
  auditLogs.slice().reverse().forEach(a => rows.push([new Date(a.timestamp).toISOString(), a.method||'', a.clientName||'', a.serverName||'', a.toolName||'', a.durationMs??'', a.success?'true':'false', a.errorMessage||'']));
  const csv = rows.map(r => r.map(v => `"${String(v).replace(/"/g,'""')}"`).join(',')).join('\n');
  downloadFile('audit-log-' + new Date().toISOString().slice(0,10) + '.csv', csv, 'text/csv');
  toast('Exported', 'Audit log downloaded as CSV.', 'success');
}

function downloadFile(filename, content, type) {
  const blob = new Blob([content], { type: type || 'application/octet-stream' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = filename;
  document.body.appendChild(a); a.click(); a.remove();
  URL.revokeObjectURL(url);
}

// ── Settings ───────────────────────────────────────────────────
async function loadSettings() {
  const [s, t, c] = await Promise.all([api('GET', '/servers'), api('GET', '/tools'), api('GET', '/clients')]);
  servers = s || servers; tools = t || tools; clients = c || clients;
  document.getElementById('setMode').textContent = gatewayMode || 'Dev';
  document.getElementById('setServers').textContent = servers.length;
  document.getElementById('setTools').textContent = tools.length;
  document.getElementById('setClients').textContent = clients.length;
}

async function exportConfig() {
  const cfg = await api('GET', '/export');
  if (!cfg) return;
  downloadFile('mcpnet-config-' + new Date().toISOString().slice(0,10) + '.json', JSON.stringify(cfg, null, 2), 'application/json');
  toast('Exported', 'Gateway config downloaded.', 'success');
}

async function importConfig(input) {
  const file = input.files && input.files[0];
  input.value = '';
  if (!file) return;
  let data;
  try { data = JSON.parse(await file.text()); }
  catch (e) { toast('Invalid file', e.message, 'danger'); return; }
  if (!(await confirmDialog({ title: 'Import config', message: 'New entries are added; existing names are skipped. Continue?', okText: 'Import', danger: false }))) return;
  const r = await api('POST', '/import', { servers: data.servers || [], groups: data.groups || [], clients: data.clients || [] });
  if (r) { toast('Imported', `${r.serversAdded} servers, ${r.groupsAdded} groups, ${r.clientsAdded} clients added.`, 'success'); await loadSettings(); }
}

function esc(s) {
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

// Initial load
(async () => {
  await restoreAdminTokenEncrypted();
  await loadInfo();
  await loadOverview();
  // Background poll: every 60s update tool count + health chips from diagnostics
  setInterval(async () => {
    try {
      const status = await api('GET', '/tools/status');
      if (!status || status.refreshing) return;
      if (status.totalTools !== tools.length) {
        await loadTools();
      }
      updateHealthChipsFromDiagnostics(Array.from(status.diagnostics || []));
    } catch { /* ignore background errors */ }
  }, 60000);
})();
