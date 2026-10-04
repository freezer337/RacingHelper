// Shared helpers: API, live state stream, formatting, small DOM utilities.

export async function api(path, opts = {}) {
  const init = { method: opts.method || (opts.body !== undefined ? 'POST' : 'GET'), headers: {} };
  if (opts.body !== undefined) { init.body = JSON.stringify(opts.body); init.headers['Content-Type'] = 'application/json'; }
  const res = await fetch(path, init);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}

// ---------------- live state (WebSocket with polling fallback) ----------------
const listeners = new Set();
export let live = null;
export function onLive(fn) { listeners.add(fn); if (live) fn(live); return () => listeners.delete(fn); }
function emit(s) { live = s; for (const fn of listeners) { try { fn(s); } catch (e) { console.error(e); } } }

export function startLive() {
  let ws, pollTimer;
  const poll = async () => { try { emit(await api('/api/live')); } catch { } };
  const connect = () => {
    try { ws = new WebSocket(`ws://${location.host}/ws/live`); } catch { pollTimer = setInterval(poll, 500); return; }
    ws.onmessage = e => emit(JSON.parse(e.data));
    ws.onclose = () => { setTimeout(connect, 1500); };
    ws.onerror = () => ws.close();
  };
  connect();
}

// ---------------- settings cache ----------------
export let settings = null;
export async function loadSettings() { settings = await api('/api/settings'); return settings; }
export async function saveSettings(patch) {
  settings = { ...settings, ...patch };
  await api('/api/settings', { body: settings });
  return settings;
}

// ---------------- formatting ----------------
export const isNum = v => typeof v === 'number' && isFinite(v);
export function lapTime(s) {
  if (!isNum(s) || s <= 0) return '–:––.–––';
  const m = Math.floor(s / 60), r = s - m * 60;
  return `${m}:${r.toFixed(3).padStart(6, '0')}`;
}
export function secTime(s) { return !isNum(s) ? '–' : s >= 60 ? lapTime(s) : s.toFixed(3); }
export function delta(d, digits = 3) { return !isNum(d) ? '–' : (d >= 0 ? '+' : '−') + Math.abs(d).toFixed(digits); }
export function deltaClass(d) { return !isNum(d) ? 'muted' : d <= 0 ? 'good' : 'bad'; }
export function fixed(v, digits = 1, suffix = '') { return isNum(v) ? v.toFixed(digits) + suffix : '–'; }
export function speed(ms) {
  if (!isNum(ms)) return '–';
  return settings?.speedUnit === 'mph' ? (ms * 2.23694).toFixed(0) : (ms * 3.6).toFixed(0);
}
export function speedKmh(kmh, digits = 0) { if (!isNum(kmh)) return '–'; return settings?.speedUnit === 'mph' ? (kmh / 1.609344).toFixed(digits) : kmh.toFixed(digits); }
export const speedUnit = () => settings?.speedUnit === 'mph' ? 'mph' : 'km/h';
export function pressure(kpa) {
  if (!isNum(kpa) || kpa <= 0) return '–';
  switch (settings?.pressureUnit) { case 'psi': return (kpa * 0.1450377).toFixed(1); case 'bar': return (kpa / 100).toFixed(2); default: return kpa.toFixed(0); }
}
export const pressureUnit = () => settings?.pressureUnit || 'kPa';
export function temp(c) { if (!isNum(c)) return '–'; return settings?.tempUnit === 'F' ? (c * 9 / 5 + 32).toFixed(0) + '°' : c.toFixed(0) + '°'; }
export function date(d) { const x = new Date(d); return x.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' }); }
export function dateTime(d) { const x = new Date(d); return x.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }); }
export function ago(d) {
  const s = (Date.now() - new Date(d).getTime()) / 1000;
  if (s < 60) return `${s.toFixed(0)}s ago`; if (s < 3600) return `${(s / 60).toFixed(0)}m ago`;
  if (s < 86400) return `${(s / 3600).toFixed(0)}h ago`; return `${(s / 86400).toFixed(0)}d ago`;
}
export function bytes(n) { return n > 1e9 ? (n / 1e9).toFixed(1) + ' GB' : n > 1e6 ? (n / 1e6).toFixed(0) + ' MB' : (n / 1e3).toFixed(0) + ' KB'; }
export function esc(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
export function trackName(key) { return (key || '').split(':').slice(1).join(':'); }

// ---------------- DOM ----------------
export function h(html) { const t = document.createElement('template'); t.innerHTML = html.trim(); return t.content.firstElementChild; }
export function $(sel, root = document) { return root.querySelector(sel); }
export function $$(sel, root = document) { return [...root.querySelectorAll(sel)]; }
export function toast(msg, err = false) {
  const el = h(`<div class="${err ? 'err' : ''}">${esc(msg)}</div>`);
  $('#toast').appendChild(el);
  setTimeout(() => el.remove(), 4200);
}

export const icons = {
  live: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 13l4-4"/><path d="M3.5 17a9 9 0 1 1 17 0"/></svg>',
  sessions: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="4" width="18" height="17" rx="2"/><path d="M3 9h18M8 2v4M16 2v4"/></svg>',
  telemetry: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M3 17l5-6 4 3 4-7 5 6"/><path d="M3 21h18"/></svg>',
  pace: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3v18h18"/><path d="M7 15l4-4 3 3 5-6"/></svg>',
  trophy: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M8 21h8M12 17v4M7 4h10v5a5 5 0 0 1-10 0z"/><path d="M17 5h3v2a3 3 0 0 1-3 3M7 5H4v2a3 3 0 0 0 3 3"/></svg>',
  tyre: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="4"/></svg>',
  fuel: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M4 21V5a2 2 0 0 1 2-2h7a2 2 0 0 1 2 2v16M3 21h13M4 10h11"/><path d="M15 8l3 3v7a2 2 0 0 0 4 0V9l-3-3"/></svg>',
  setup: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0"/><circle cx="16" cy="6" r="2"/><circle cx="10" cy="12" r="2"/><circle cx="18" cy="18" r="2"/></svg>',
  overlays: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="2" y="4" width="20" height="14" rx="2"/><rect x="5" y="7" width="6" height="4" rx="1"/><rect x="14" y="12" width="5" height="3" rx="1"/></svg>',
  import: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 3v12M7 10l5 5 5-5M4 21h16"/></svg>',
  settings: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.6 1.6 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.6 1.6 0 0 0-1.8-.3 1.6 1.6 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.6 1.6 0 0 0-1-1.5 1.6 1.6 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.6 1.6 0 0 0 .3-1.8 1.6 1.6 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.6 1.6 0 0 0 1.5-1 1.6 1.6 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.6 1.6 0 0 0 1.8.3H9a1.6 1.6 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.6 1.6 0 0 0 1 1.5 1.6 1.6 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.6 1.6 0 0 0-.3 1.8V9a1.6 1.6 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.6 1.6 0 0 0-1.5 1z"/></svg>',
};

export const lapColors = ['#B26BFF', '#22D37E', '#FF9640', '#4DA3FF', '#FF6FB5', '#F5C542'];
