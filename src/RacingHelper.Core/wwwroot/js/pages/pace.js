import { api, onLive, lapTime, secTime, delta, deltaClass, fixed, esc, isNum, speedKmh, speedUnit, $ } from '../core.js';
import { drawPace, handlingGrid } from './session.js';
import { suggestionsHtml } from './setup.js';

// Live pace: your session analysis while you drive. The "this lap" strip follows the live feed; everything else is the
// session report, fetched again a moment after each lap is saved.

let off = null, timer = null, refresh = null;
export function destroy() { off?.(); off = null; clearTimeout(timer); clearInterval(refresh); }

export async function render(root) {
  root.innerHTML = `
    <div class="page-head"><div class="grow"><h1>Live pace</h1><p id="pc-head" class="muted">Waiting for a session…</p></div>
      <span id="pc-badge"></span><a id="pc-open" class="small" style="display:none">Open full session →</a></div>
    <div class="grid g4" id="pc-stats"></div>
    <div class="card" style="margin-top:14px"><div class="card-head"><h2 class="grow">This lap</h2><span class="small muted" id="pc-lapno"></span></div><div id="pc-now" class="muted small">Not on track.</div></div>
    <div class="grid g2" style="margin-top:14px">
      <div class="card"><div class="card-head"><h2 class="grow">Where your time is</h2><span class="small muted">average loss to your best in each corner</span></div><div id="pc-corners" class="muted small">After your first clean laps.</div></div>
      <div class="card"><div class="card-head"><h2 class="grow">Pace through the session</h2></div><canvas id="pc-chart" style="width:100%;height:220px"></canvas><div class="legend" id="pc-legend" style="margin-top:6px"></div></div>
    </div>
    <div class="grid g2" style="margin-top:14px">
      <div class="card"><div class="card-head"><h2 class="grow">What to work on</h2><span class="small muted">updates every lap</span></div><div class="insights" id="pc-insights"><div class="muted small">After your first clean laps.</div></div></div>
      <div class="card"><div class="card-head"><h2 class="grow">Handling</h2><a class="small" id="pc-setup">Fix it in Setup →</a></div><div id="pc-handling" class="muted small">Needs a few clean laps at the limit.</div></div>
    </div>
    <div class="grid g2" style="margin-top:14px">
      <div class="card"><div class="card-head"><h2 class="grow">Setup: change in the car now</h2><span class="small muted">live</span></div><div id="pc-incar" class="small muted">Nothing to change right now.</div></div>
      <div class="card"><div class="card-head"><h2 class="grow">Setup: change in the garage</h2><span class="small muted">from this session, updates every lap</span></div><div id="pc-garage" class="small muted">Needs a few clean laps at the limit.</div><p class="small muted" id="pc-src" style="margin:10px 0 0"></p></div>
    </div>
    <div class="card" style="margin-top:14px"><div class="card-head"><h2 class="grow">Laps</h2><span class="small muted">newest first</span></div><div class="table-wrap" id="pc-laps"></div></div>`;

  let sid = 0, key = '', isLive = false, busy = false, again = false, livePace = null, tries = 0, lastCar = '';
  let report = null, laps = [], setup = null;

  async function load() {
    if (busy) { again = true; return; }
    busy = true;
    try {
      let id = sid;
      if (!id) {   // not in a session: show the latest one
        const list = await api('/api/sessions').catch(() => []);
        id = list.slice().sort((a, b) => new Date(b.startedAt) - new Date(a.startedAt))[0]?.id || 0;
      }
      if (!id) { $('#pc-head', root).textContent = 'No sessions yet. Get on track and this page fills in after your first laps.'; return; }
      const [detail, rep, su] = await Promise.all([api(`/api/sessions/${id}`), api(`/api/sessions/${id}/report`).catch(() => null), api(`/api/setup/auto?session=${id}`).catch(() => null)]);
      if (!root.isConnected) return;
      report = rep; laps = detail.laps || []; setup = su;
      // the lap that was just completed may not be saved yet: look again shortly
      if (isLive && livePace && livePace.laps > Math.max(0, ...laps.map(l => l.lapNumber)) && tries++ < 4) { clearTimeout(timer); timer = setTimeout(load, 3000); }
      else if (!isLive || !livePace || livePace.laps <= Math.max(0, ...laps.map(l => l.lapNumber))) tries = 0;
      const s = detail.session;
      $('#pc-head', root).textContent = `${s.carName} · ${s.trackName} · ${s.sessionType}${isLive ? '' : ' · last session (not on track now)'}`;
      $('#pc-badge', root).innerHTML = isLive ? `<span class="tag green">LIVE · updated ${new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</span>` : '';
      const open = $('#pc-open', root); open.style.display = ''; open.href = `#/session/${id}`;
      $('#pc-setup', root).href = `#/setup?session=${id}`;
      draw();
    } finally {
      busy = false;
      if (again) { again = false; load(); }
    }
  }

  function draw() {
    const r = report, best = r?.bestLap;
    // where the time is: corners ordered by how much you lose on average vs your best there
    const corners = (r?.corners || []).filter(c => isNum(c.avgLossToBest)).sort((a, b) => b.avgLossToBest - a.avgLossToBest).slice(0, 6);
    const maxLoss = Math.max(0.05, ...corners.map(c => c.avgLossToBest));
    $('#pc-corners', root).innerHTML = corners.length ? `<table>
        <tr><th>Corner</th><th style="width:40%"></th><th class="num">Avg loss</th><th class="num">Apex best / avg (${speedUnit()})</th><th class="num">Brake ± m</th></tr>
        ${corners.map(c => `<tr><td><b>${esc(c.name)}</b>${c.lockups ? ` <span class="tag red">${c.lockups} lock</span>` : ''}</td>
          <td><div style="height:8px;border-radius:4px;background:var(--warn,#F5B942);width:${Math.max(4, c.avgLossToBest / maxLoss * 100).toFixed(0)}%"></div></td>
          <td class="num">${c.avgLossToBest.toFixed(3)}</td>
          <td class="num">${speedKmh(c.minSpeedBest)} / ${speedKmh(c.minSpeedAvg)}</td>
          <td class="num ${c.brakePointSpread > 12 ? 'warn' : ''}">${c.brakePointSpread ? '±' + c.brakePointSpread.toFixed(0) : '–'}</td></tr>`).join('')}
      </table>
      <p class="small muted" style="margin:8px 0 0">The top rows are where you're least consistent: copy your best lap there.${isNum(r?.theoreticalBest) && best ? ` All your best corners together: ${lapTime(r.theoreticalBest)}, ${Math.max(0, best - r.theoreticalBest).toFixed(2)} s quicker than your best lap.` : ''}</p>`
      : '<div class="muted small">After your first clean laps (needs the track map, built on your first clean lap).</div>';

    $('#pc-insights', root).innerHTML = r?.insights?.length ? r.insights.map(i => `
      <div class="insight s${i.severity}"><div class="cat">${esc(i.category)}</div><div><div class="ttl">${esc(i.title)}</div><div class="det">${esc(i.detail)}</div></div></div>`).join('')
      : '<div class="muted small">After your first clean laps.</div>';

    $('#pc-handling', root).innerHTML = handlingGrid(r?.handling);
    $('#pc-garage', root).innerHTML = suggestionsHtml(setup);
    $('#pc-src', root).textContent = setup?.source ? 'Setup: ' + setup.source : '';

    const nSec = Math.max(0, ...laps.map(l => l.sectorTimes.length));
    $('#pc-laps', root).innerHTML = laps.length ? `<table>
      <tr><th>Lap</th><th class="num">Time</th><th class="num">Δ best</th>${[...Array(nSec)].map((_, i) => `<th class="num">S${i + 1}</th>`).join('')}<th>Status</th></tr>
      ${laps.slice().reverse().slice(0, 15).map(l => `<tr class="${l.valid ? '' : 'invalid'}"><td>${l.lapNumber}</td>
        <td class="num ${l.valid && l.lapTime === best ? 'purple' : ''}">${lapTime(l.lapTime)}</td>
        <td class="num">${l.valid && best ? delta(l.lapTime - best, 3) : ''}</td>
        ${[...Array(nSec)].map((_, i) => `<td class="num">${isNum(l.sectorTimes[i]) ? l.sectorTimes[i].toFixed(3) : '–'}</td>`).join('')}
        <td>${l.valid ? '<span class="tag green">clean</span>' : `<span class="tag">${esc(l.invalidReason || (l.outLap ? 'out lap' : l.inLap ? 'in lap' : 'invalid'))}</span>`}</td></tr>`).join('')}</table>`
      : '<div class="muted small">No laps yet.</div>';

    if (r) drawPace($('#pc-chart', root), $('#pc-legend', root), r, laps);
    stats(null);
  }

  // headline numbers: the live pace summary when driving, the report otherwise
  function stats(p) {
    const r = report;
    const best = p && isNum(p.best) ? p.best : r?.bestLap;
    const last = p && isNum(p.lastLap) ? p.lastLap : laps.at(-1)?.lapTime;
    const theo = isNum(r?.theoreticalBest) ? r.theoreticalBest : p?.theoreticalBest;
    const avg = p && isNum(p.average) ? p.average : null;
    const trend = p && isNum(p.trend) ? p.trend : null;
    const card = (label, value, sub, cls = '') => `<div class="card"><div class="stat"><span class="label">${label}</span><span class="value ${cls}">${value}</span><span class="sub">${sub}</span></div></div>`;
    $('#pc-stats', root).innerHTML =
      card('Last lap', lapTime(last), isNum(last) && isNum(best) ? `${delta(last - best, 3)} to your best${p && !p.lastValid ? ' · not clean' : ''}` : '', isNum(last) && isNum(best) && last <= best + 1e-6 ? 'purple' : '') +
      card('Best lap', lapTime(best), r?.bestLapNumber && isNum(r?.bestLap) && Math.abs(r.bestLap - best) < 0.0005 ? 'lap ' + r.bestLapNumber : '') +
      card('Theoretical best', lapTime(theo), isNum(theo) && isNum(best) ? `${Math.max(0, best - theo).toFixed(2)} s in your best corners${p?.focus ? ' · ' + esc(p.focus) : ''}` : 'best of each corner') +
      card(trend != null ? 'Trend' : 'Consistency',
        trend != null ? `${trend > 0 ? '+' : '−'}${Math.abs(trend).toFixed(2)} s/lap` : isNum(r?.stdDev) ? '±' + r.stdDev.toFixed(2) + 's' : '–',
        trend != null ? `${Math.abs(trend) < 0.03 ? 'steady' : trend < 0 ? 'getting faster' : 'getting slower'}${avg != null ? ` · last 5 avg ${lapTime(avg)} (spread ${p.spread.toFixed(2)})` : ''}`
          : `${r?.validLaps ?? 0} clean laps${avg != null ? ` · last 5 avg ${lapTime(avg)}` : ''}`,
        trend == null ? '' : trend < -0.03 ? 'good' : trend > 0.03 ? 'bad' : '');
  }

  let lastStats = '';
  off = onLive(s => {
    const live = s.status !== 'waiting' && (s.sessionDbId || 0) > 0;
    // this lap, straight from the live feed
    if (s.status === 'driving' || s.status === 'replay') {
      const secs = (s.sectors || []).map(x => `<span class="tag ${x.color === 'purple' ? 'purple' : x.color === 'green' ? 'green' : x.color === 'yellow' ? 'yellow' : ''}" style="margin-right:4px">S${x.index + 1} ${x.state === 'pending' ? '–' : delta(x.delta, 2)}</span>`).join('');
      $('#pc-now', root).innerHTML = `<div class="row" style="gap:22px;flex-wrap:wrap;align-items:baseline">
          <span>Delta <b class="num ${deltaClass(s.delta)}" style="font-size:22px">${delta(s.delta, 2)}</b> <span class="muted small">to ${esc(s.referenceLabel || 'reference')}</span></span>
          <span>Predicted <b class="num">${lapTime(s.predictedLap)}</b></span>
          <span>Current <b class="num">${lapTime(s.currentLapTime)}</b></span>
          ${s.lastCorner ? `<span class="small">Last corner <b>${esc(s.lastCorner.name)}</b> <span class="${deltaClass(s.lastCorner.timeDelta)}">${delta(s.lastCorner.timeDelta, 2)}</span> ${esc(s.lastCorner.advice || s.lastCorner.verdict || '')}</span>` : ''}
        </div><div style="margin-top:8px">${secs}</div>`;
      $('#pc-lapno', root).textContent = s.outLap ? 'out lap' : s.lap ? 'lap ' + s.lap : '';
    }
    livePace = s.pace || null;
    // in-car changes and the setup session, straight from the live feed
    const car = JSON.stringify([s.carAdvice, s.setupInstruction, s.setupStatus, s.setupSheet]);
    if (car !== lastCar) {
      lastCar = car;
      const items = [];
      if (s.setupInstruction) items.push(`<div class="warn" style="font-weight:600">To do: ${esc(s.setupInstruction)}</div>`);
      if (s.carAdvice) items.push(`<div style="font-weight:600">${esc(s.carAdvice)}</div>`);
      if (s.setupStatus) items.push(`<div class="small">Setup session: ${esc(s.setupStatus)}</div>`);
      if (s.setupSheet?.length) items.push(`<div class="small"><b>Setup sheet</b><ul style="margin:4px 0 0 18px;padding:0">${s.setupSheet.map(l => `<li>${esc(l)}</li>`).join('')}</ul></div>`);
      $('#pc-incar', root).innerHTML = items.length ? items.join('<div style="height:8px"></div>') : '<span class="muted">Nothing to change right now. The engineer says it here (and on the radio) when brake bias, TC, ABS or an in-car bar would help.</span>';
    }
    const st = JSON.stringify(s.pace || null);
    if (st !== lastStats) { lastStats = st; if (s.pace) stats(s.pace); }

    const k = `${live ? s.sessionDbId : 0}:${s.pace?.laps ?? ''}:${s.pace?.lastLap ?? ''}`;
    if (k !== key) {
      const first = key === '';
      key = k; sid = live ? s.sessionDbId : 0; isLive = live;
      clearTimeout(timer);
      // the lap is written to the database just after it's completed: give it a moment
      timer = setTimeout(load, first ? 0 : 2500);
    }
  });
  if (key === '') load();
  // safety net while live (e.g. a lap saved late)
  refresh = setInterval(() => { if (isLive) load(); }, 30000);
}
