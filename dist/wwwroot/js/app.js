import { api, icons, onLive, startLive, loadSettings, $, esc, h, toast } from './core.js';

const routes = [
  { path: 'live', label: 'Live', icon: 'live', module: './pages/live.js' },
  { path: 'pace', label: 'Live pace', icon: 'pace', module: './pages/pace.js' },
  { path: 'sessions', label: 'Sessions', icon: 'sessions', module: './pages/sessions.js' },
  { path: 'session', hidden: true, module: './pages/session.js' },
  { path: 'telemetry', label: 'Telemetry', icon: 'telemetry', module: './pages/telemetry.js' },
  { path: 'leaderboard', label: 'Leaderboards', icon: 'trophy', module: './pages/leaderboard.js' },
  { path: 'setup', label: 'Setup', icon: 'setup', module: './pages/setup.js' },
  { path: 'tyres', label: 'Tyres', icon: 'tyre', module: './pages/tyres.js' },
  { path: 'fuel', label: 'Fuel', icon: 'fuel', module: './pages/fuel.js' },
  { path: 'overlays', label: 'Overlays', icon: 'overlays', module: './pages/overlays.js' },
  { path: 'import', label: 'Import & replay', icon: 'import', module: './pages/import.js' },
  { path: 'settings', label: 'Settings', icon: 'settings', module: './pages/settings.js' },
];

let current = null;

function parseHash() {
  const raw = location.hash.replace(/^#\/?/, '') || 'live';
  const [pathPart, query = ''] = raw.split('?');
  const [path, ...rest] = pathPart.split('/');
  const params = Object.fromEntries(new URLSearchParams(query));
  if (rest.length) params.id = rest.join('/');
  return { path, params };
}

async function navigate() {
  const { path, params } = parseHash();
  const route = routes.find(r => r.path === path) || routes[0];
  for (const a of document.querySelectorAll('nav a')) a.classList.toggle('active', a.dataset.path === (route.path === 'session' ? 'sessions' : route.path));
  if (current?.destroy) { try { current.destroy(); } catch { } }
  const page = $('#page');
  page.innerHTML = '<div class="empty"><span class="spinner"></span></div>';
  page.scrollTop = 0;
  try {
    const mod = await import(route.module);
    current = mod;
    page.innerHTML = '';
    await mod.render(page, params);
  } catch (e) {
    console.error(e);
    page.innerHTML = `<div class="card"><h2>Something went wrong</h2><p class="muted">${esc(e.message)}</p></div>`;
  }
}

function buildNav() {
  const nav = $('#nav');
  for (const r of routes.filter(r => !r.hidden)) {
    nav.appendChild(h(`<a href="#/${r.path}" data-path="${r.path}">${icons[r.icon]}<span>${r.label}</span></a>`));
  }
}

function updateStatus(s) {
  const st = $('#status');
  st.className = 'status ' + s.status;
  const label = { waiting: 'Waiting for iRacing', connected: 'iRacing connected', driving: 'On track', replay: 'Replay' }[s.status] || s.status;
  $('#status-text').textContent = label;
  const line = s.status === 'waiting' ? '' : [s.car, s.track, s.sessionType].filter(Boolean).join('  ·  ');
  $('#session-line').textContent = line;
}

async function init() {
  buildNav();
  await loadSettings().catch(() => null);
  startLive();
  onLive(updateStatus);
  const status = await api('/api/status').catch(() => null);
  $('#sidebar-foot').innerHTML = status ? `Version <b>${esc(status.version || '1.0')}</b><br>Dashboard: <span class="num">${esc(status.url)}</span><br>VR panel: <span class="num">${esc(status.url)}kneeboard.html</span>${status.lanUrls?.length ? `<br>On your phone: <span class="num">${esc(status.lanUrls[0])}</span>` : ''}` : '';
  window.addEventListener('hashchange', navigate);
  navigate();
  // surface background import results
  let lastMsg = null;
  onLive(s => {
    const m = s.messages?.[s.messages.length - 1];
    if (!m || m.at === lastMsg) return;
    if (lastMsg !== null && m.text.startsWith('Imported')) toast(m.text);
    lastMsg = m.at;
  });
}

init().catch(e => toast(e.message, true));
