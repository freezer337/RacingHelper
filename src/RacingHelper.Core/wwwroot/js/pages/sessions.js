import { api, lapTime, esc, date, dateTime, $ } from '../core.js';

export async function render(root) {
  root.innerHTML = `
    <div class="page-head"><div class="grow"><h1>Sessions</h1><p>Every session you've driven or imported, with a full debrief.</p></div>
      <select id="f-combo"><option value="">All cars & tracks</option></select>
      <a class="btn" href="#/import">Import</a>
    </div>
    <div id="list"><div class="empty"><span class="spinner"></span></div></div>`;
  const [sessions, combos] = await Promise.all([api('/api/sessions'), api('/api/combos')]);
  const sel = $('#f-combo', root);
  for (const c of combos) sel.appendChild(Object.assign(document.createElement('option'), { value: c.carPath + '|' + c.trackKey, textContent: `${c.carName} · ${c.trackName}${c.trackConfig ? ' – ' + c.trackConfig : ''} (${c.laps} laps)` }));
  const draw = () => {
    const f = sel.value;
    const list = sessions.filter(s => !f || s.carPath + '|' + s.trackKey === f);
    const el = $('#list', root);
    if (!list.length) { el.innerHTML = `<div class="card empty">No sessions yet. Drive in iRacing or <a href="#/import">import your telemetry</a>.</div>`; return; }
    const byDay = new Map();
    for (const s of list) { const d = date(s.startedAt); if (!byDay.has(d)) byDay.set(d, []); byDay.get(d).push(s); }
    el.innerHTML = [...byDay].map(([d, items]) => `
      <h3 style="margin:18px 0 8px">${d}</h3>
      <div class="card" style="padding:4px 8px"><table>
        <tr><th>Time</th><th>Car</th><th>Track</th><th>Session</th><th class="num">Clean / laps</th><th class="num">Best</th><th></th></tr>
        ${items.map(s => `<tr class="click" data-id="${s.id}">
          <td class="nowrap">${new Date(s.startedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</td>
          <td>${esc(s.carName)}</td>
          <td>${esc(s.trackName)}${s.trackConfig ? `<span class="muted"> · ${esc(s.trackConfig)}</span>` : ''}</td>
          <td>${esc(s.sessionType)} ${s.official ? '<span class="tag blue">official</span>' : ''} ${s.source === 'live' ? '<span class="tag green">live</span>' : ''}</td>
          <td class="num">${s.validLaps} / ${s.lapCount}</td>
          <td class="num">${lapTime(s.bestLap)}</td>
          <td class="right"><span class="muted">›</span></td></tr>`).join('')}
      </table></div>`).join('');
    for (const tr of el.querySelectorAll('tr[data-id]')) tr.onclick = () => location.hash = '#/session/' + tr.dataset.id;
  };
  sel.onchange = draw;
  draw();
}
