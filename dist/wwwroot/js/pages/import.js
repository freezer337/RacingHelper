import { api, onLive, esc, bytes, dateTime, toast, $, $$ } from '../core.js';

let timer = null;
export function destroy() { clearInterval(timer); }

export async function render(root) {
  root.innerHTML = `
    <div class="page-head"><div class="grow"><h1>Import & replay</h1><p>iRacing writes a telemetry (.ibt) file every time you drive with logging on. New files are imported automatically after each session; bring in older history here.</p></div>
      <button data-open="telemetry">Open telemetry folder</button></div>
    <div class="card" style="margin-bottom:14px" id="job"></div>
    <div class="card"><div class="card-head"><div class="row grow"><input id="q" placeholder="Filter by car or track…" style="min-width:260px"><label class="check small"><input type="checkbox" id="hide-imported" checked> Hide imported</label><label class="check small"><input type="checkbox" id="hide-small" checked> Hide tiny files</label></div>
      <button id="sel-all" class="small">Select shown</button><button id="imp" class="primary">Import selected</button></div>
      <div id="files" class="table-wrap"><span class="spinner"></span></div></div>`;
  $('[data-open]', root).onclick = () => api('/api/open-folder', { body: { which: 'telemetry' } });
  const data = await api('/api/import/files');
  const selected = new Set();
  const shown = () => {
    const q = $('#q', root).value.toLowerCase();
    return data.files.filter(f => (!$('#hide-imported', root).checked || !f.imported) && (!$('#hide-small', root).checked || f.size > 1_500_000)
      && (!q || (f.car + ' ' + f.track).toLowerCase().includes(q)));
  };
  const draw = () => {
    const list = shown().slice(0, 400);
    $('#files', root).innerHTML = list.length ? `<table><tr><th></th><th>Date</th><th>Car</th><th>Track</th><th class="num">Size</th><th>Status</th><th></th></tr>
      ${list.map(f => `<tr class="${selected.has(f.path) ? 'sel' : ''}" data-p="${esc(f.path)}"><td><input type="checkbox" ${selected.has(f.path) ? 'checked' : ''}></td>
        <td class="nowrap">${dateTime(f.modified)}</td><td>${esc(f.car)}</td><td>${esc(f.track)}</td><td class="num">${bytes(f.size)}</td>
        <td>${f.imported ? '<span class="tag green">imported</span>' : ''}</td>
        <td class="right nowrap"><button class="small ghost replay" title="Replay as if live (overlays, delta, engineer)">▶ Replay</button></td></tr>`).join('')}</table>
      ${shown().length > 400 ? '<p class="small muted">Showing the newest 400 — use the filter to narrow down.</p>' : ''}`
      : `<div class="empty">No files ${data.files.length ? 'match' : `in ${esc(data.folder)}`}.</div>`;
    for (const tr of $$('tr[data-p]', root)) {
      tr.querySelector('input').onchange = e => { e.target.checked ? selected.add(tr.dataset.p) : selected.delete(tr.dataset.p); tr.classList.toggle('sel', e.target.checked); };
      tr.querySelector('.replay').onclick = async () => {
        const r = await api('/api/replay', { body: { file: tr.dataset.p, speed: 1 } });
        toast(r.ok ? 'Replay started — open Live or watch the overlays.' : r.error, !r.ok);
        if (r.ok) location.hash = '#/live';
      };
    }
  };
  for (const id of ['q', 'hide-imported', 'hide-small']) $('#' + id, root).oninput = draw;
  $('#sel-all', root).onclick = () => { shown().slice(0, 400).forEach(f => selected.add(f.path)); draw(); };
  $('#imp', root).onclick = async () => {
    if (!selected.size) return toast('Select files first');
    const r = await api('/api/import', { body: [...selected] });
    if (!r.ok) return toast(r.error, true);
    toast(`Importing ${selected.size} file(s)…`);
    selected.clear();
  };
  draw();

  const job = $('#job', root);
  const poll = async () => {
    const s = await api('/api/status').catch(() => null);
    if (!s) return;
    const j = s.importJob;
    job.innerHTML = j.status === 'running'
      ? `<div class="row"><span class="spinner"></span><b>Importing ${j.done} / ${j.total}</b><span class="small muted">${esc(s.importer)}</span></div><div class="progress" style="margin-top:10px"><div style="width:${(j.done / Math.max(1, j.total) * 100).toFixed(0)}%"></div></div>`
      : `<div class="row"><b>Auto-import is ${(await api('/api/settings')).autoImportIbt ? 'on' : 'off'}</b><span class="small muted">${s.importer === 'idle' ? 'New sessions are imported within a minute of leaving the car.' : esc(s.importer)}</span><button class="small ghost" id="stop-replay" ${s.replay ? '' : 'disabled'}>Stop replay</button></div>`;
    const sr = $('#stop-replay', job);
    if (sr) sr.onclick = () => api('/api/replay/stop', { body: {} }).then(() => toast('Replay stopped'));
    if (j.status !== 'running' && job.dataset.was === 'running') {
      const fresh = await api('/api/import/files');
      data.files = fresh.files; draw();
    }
    job.dataset.was = j.status;
  };
  poll();
  timer = setInterval(poll, 1500);
}
