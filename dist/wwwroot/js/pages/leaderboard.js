import { api, lapTime, delta, deltaClass, esc, isNum, date, toast, $, $$ } from '../core.js';

export async function render(root, params) {
  root.innerHTML = `
    <div class="page-head"><div class="grow"><h1>Leaderboards</h1><p>Your personal bests everywhere, your fastest laps per combo, and laps from other drivers you've imported (friends, pros, setup shops).</p></div></div>
    <div class="card" style="margin-bottom:14px"><div class="card-head"><h2 class="grow">Personal bests</h2></div><div id="pbs" class="table-wrap"><span class="spinner"></span></div></div>
    <div class="card" style="margin-bottom:14px"><div class="row"><select id="combo" style="min-width:320px"></select><span id="theo" class="muted small"></span></div></div>
    <div class="grid g2">
      <div class="card"><div class="card-head"><h2 class="grow">Your laps</h2></div><div id="mine" class="scroll-y"></div></div>
      <div class="card"><div class="card-head"><h2 class="grow">Rivals</h2><span class="small muted">best lap per driver</span></div><div id="rivals" class="scroll-y"></div>
        <p class="small muted">Got a friend's or a pro's .ibt file? Drop it into your iRacing telemetry folder (or import it on the <a href="#/import">Import</a> page) and it shows up here as a rival lap you can race against.</p></div>
    </div>`;
  const [pbs, combos] = await Promise.all([api('/api/personal-bests'), api('/api/combos')]);
  $('#pbs', root).innerHTML = pbs.length ? `<table><tr><th>Track</th><th>Car</th><th class="num">Best</th><th>Set</th><th></th></tr>
    ${pbs.map(p => `<tr><td>${esc(p.session?.trackName || p.trackKey)}${p.session?.trackConfig ? ' <span class="muted">· ' + esc(p.session.trackConfig) + '</span>' : ''}</td><td>${esc(p.session?.carName || p.carPath)}</td>
      <td class="num purple">${lapTime(p.lapTime)}</td><td class="small muted">${date(p.startedAt)} · ${esc(p.session?.sessionType || '')}</td>
      <td class="right"><button class="small" data-ref="${p.id}">Use as delta reference</button> <a class="btn small" href="#/telemetry?laps=${p.id}">Telemetry</a></td></tr>`).join('')}</table>`
    : '<div class="muted">No clean laps yet.</div>';
  wireRef(root);

  const sel = $('#combo', root);
  if (!combos.length) { sel.outerHTML = '<span class="muted">No laps yet.</span>'; return; }
  sel.innerHTML = combos.map(c => `<option value="${esc(c.carPath)}|${esc(c.trackKey)}">${esc(c.carName)} · ${esc(c.trackName)}${c.trackConfig ? ' – ' + esc(c.trackConfig) : ''}</option>`).join('');
  if (params.car && params.track) sel.value = `${params.car}|${params.track}`;
  const load = async () => {
    const [car, track] = sel.value.split('|');
    const lb = await api(`/api/leaderboard?car=${encodeURIComponent(car)}&track=${encodeURIComponent(track)}`);
    const best = lb.mine[0]?.lapTime;
    $('#theo', root).textContent = isNum(lb.theoretical) && best ? `Theoretical best from your sectors: ${lapTime(lb.theoretical)} (${delta(lb.theoretical - best)})` : '';
    const rows = (list, mine) => list.length ? `<table><tr><th>#</th><th class="num">Time</th><th class="num">Gap</th>${mine ? '' : '<th>Driver</th>'}<th>When</th><th></th></tr>
      ${list.map((l, i) => `<tr><td>${i + 1}</td><td class="num ${i === 0 && mine ? 'purple' : ''}">${lapTime(l.lapTime)}</td><td class="num ${deltaClass(l.lapTime - best)}">${i === 0 && mine ? '' : isNum(best) ? delta(l.lapTime - best) : ''}</td>
        ${mine ? '' : `<td>${esc(l.driverName)}</td>`}<td class="small muted">${esc(l.sessionLabel)}${l.wetness >= 3 ? ' <span class="tag blue">wet</span>' : ''}</td>
        <td class="right nowrap"><button class="small ghost" data-ref="${l.id}" title="Use as live delta reference">◎ Ref</button>${lb.mine[0] ? ` <a class="btn small" href="#/telemetry?laps=${mine ? lb.mine[0].id + ',' + l.id : l.id + ',' + lb.mine[0].id}">Compare</a>` : ''}</td></tr>`).join('')}</table>`
      : `<div class="muted small">${mine ? 'No clean laps here yet.' : 'No laps from other drivers for this combo.'}</div>`;
    $('#mine', root).innerHTML = rows(lb.mine, true);
    $('#rivals', root).innerHTML = rows(lb.rivals, false);
    wireRef(root);
  };
  sel.onchange = load;
  load();
}

function wireRef(root) {
  for (const b of $$('[data-ref]', root)) b.onclick = async () => {
    await api('/api/reference', { body: { mode: 'lap', lapId: +b.dataset.ref } });
    toast('Live delta reference set — used next time you drive this combo.');
  };
}
