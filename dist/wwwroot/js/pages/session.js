import { api, lapTime, secTime, delta, deltaClass, fixed, esc, dateTime, isNum, toast, temp, pressure, pressureUnit, speedKmh, speedUnit, $, $$ } from '../core.js';

export async function render(root, params) {
  const id = params.id;
  root.innerHTML = `<div class="empty"><span class="spinner"></span> Analysing session…</div>`;
  const [detail, report] = await Promise.all([api(`/api/sessions/${id}`), api(`/api/sessions/${id}/report`)]);
  const s = detail.session, laps = detail.laps;
  const best = report?.bestLap;
  const selected = new Set();
  if (report?.bestLapId) selected.add(report.bestLapId);

  root.innerHTML = `
    <div class="page-head">
      <div class="grow"><a href="#/sessions" class="small">← Sessions</a><h1>${esc(s.carName)} · ${esc(s.trackName)}</h1>
        <p>${esc(s.sessionType)} · ${dateTime(s.startedAt)}${s.official ? ' · official' : ''} · ${esc(s.driverName)}</p></div>
      <button id="cmp" class="primary">Compare selected laps</button>
      <button id="del" class="ghost danger">Delete</button>
    </div>
    <div class="grid g4">
      ${stat('Best lap', lapTime(best), report?.bestLapNumber ? 'lap ' + report.bestLapNumber : '')}
      ${stat('Theoretical best', lapTime(report?.theoreticalBest), isNum(report?.theoreticalBest) && best ? delta(report.theoreticalBest - best, 2) + ' available' : 'best of each corner')}
      ${stat('Consistency', isNum(report?.stdDev) ? '±' + report.stdDev.toFixed(2) + 's' : '–', 'avg ' + lapTime(report?.averageLap))}
      ${stat('Clean laps', `${report?.validLaps ?? 0} / ${report?.laps ?? laps.length}`, report?.referenceLabel ? 'compared to ' + report.referenceLabel : 'no comparable PB from other sessions')}
    </div>
    <div class="grid g2" style="margin-top:14px">
      <div class="card"><div class="card-head"><h2 class="grow">Debrief</h2><span class="small muted">what to work on</span></div><div class="insights" id="insights"></div></div>
      <div class="grid" style="align-content:start">
        <div class="card"><div class="card-head"><h2 class="grow">Pace through the session</h2></div><canvas id="pace" style="width:100%;height:220px"></canvas><div class="legend" id="pace-legend" style="margin-top:6px"></div></div>
        <div class="card"><div class="card-head"><h2 class="grow">Driving style</h2></div><div id="style"></div></div>
      </div>
    </div>
    <div class="card" style="margin-top:14px"><div class="card-head"><h2 class="grow">Stints</h2><span class="small muted">a new stint starts after each pit stop / garage visit</span></div><div class="table-wrap" id="stints"></div></div>
    <div class="card" style="margin-top:14px"><div class="card-head"><h2 class="grow">Corners</h2><span class="small muted">best / average segment time, apex speed, and comparison with ${esc(report?.referenceLabel || 'your best lap')}</span></div><div class="table-wrap" id="corners"></div></div>
    <div class="card" style="margin-top:14px"><div class="card-head"><h2 class="grow">Laps</h2><span class="small muted">tick laps to compare them in Telemetry</span></div><div class="table-wrap" id="laps"></div></div>
    <div class="grid g2" style="margin-top:14px">
      <div class="card"><div class="card-head"><h2 class="grow">Handling</h2><a class="small" href="#/setup?session=${id}">Fix it in Setup →</a></div><div id="handling"></div></div>
      <div class="card"><div class="card-head"><h2 class="grow">Notes</h2></div><textarea id="notes" rows="6" style="width:100%" placeholder="What did you change, how did it feel?">${esc(s.notes)}</textarea><div class="row end" style="margin-top:8px"><button id="save-notes" class="small">Save notes</button></div></div>
    </div>`;

  // insights
  $('#insights', root).innerHTML = report?.insights?.length ? report.insights.map(i => `
    <div class="insight s${i.severity}"><div class="cat">${esc(i.category)}</div><div><div class="ttl">${esc(i.title)}</div><div class="det">${esc(i.detail)}</div></div></div>`).join('')
    : '<div class="muted">Not enough data for a debrief yet.</div>';

  // style
  const st = report?.style;
  $('#style', root).innerHTML = st ? `<div class="grid g3">
      ${mini('Full throttle', fixed(st.fullThrottlePct, 0, '%'))}
      ${mini('Braking', fixed(st.brakingPct, 0, '%'))}
      ${mini('Coasting', fixed(st.coastingPct, 1, '%'), st.coastingPct > 4 ? 'warn' : '')}
      ${mini('Trail braking', fixed(st.trailBrakePct, 0, '%'), st.trailBrakePct < 15 ? 'warn' : '')}
      ${mini('Pedal overlap', fixed(st.overlapPct, 1, '%'), st.overlapPct > 2 ? 'warn' : '')}
      ${mini('Avg peak brake', fixed(st.avgPeakBrake, 0, '%'))}
      ${mini('Upshift RPM', fixed(st.upshiftRpm, 0))}
      ${mini('Lock-ups', st.lockups, st.lockups > 2 ? 'warn' : '')}
      ${mini('Max lateral', fixed(st.maxLatG, 2, ' g'))}
    </div>` : '<div class="muted small">Needs a track map (complete a clean lap).</div>';

  // stints
  const stints = report?.stints || [];
  $('#stints', root).innerHTML = stints.length ? `<table><tr><th>Stint</th><th class="num">Laps</th><th class="num">Clean</th><th class="num">Best</th><th class="num">Average</th><th class="num">Consistency</th><th class="num">Pace trend</th><th class="num">Fuel / lap</th></tr>
    ${stints.map(st => `<tr><td>${st.stint}</td><td class="num">${st.laps}</td><td class="num">${st.validLaps}</td><td class="num ${st.best === best ? 'purple' : ''}">${lapTime(st.best)}</td><td class="num">${lapTime(st.average)}</td>
      <td class="num">${st.validLaps > 1 ? '±' + st.stdDev.toFixed(2) + 's' : '–'}</td>
      <td class="num ${st.validLaps >= 4 ? (st.trendPerLap > 0.05 ? 'bad' : st.trendPerLap < -0.05 ? 'good' : '') : ''}">${st.validLaps >= 4 ? (st.trendPerLap > 0 ? '+' : '') + st.trendPerLap.toFixed(2) + ' s/lap' : '–'}</td>
      <td class="num">${fixed(st.fuelPerLap, 2)}</td></tr>`).join('')}</table>` : '<div class="muted small">No stints.</div>';

  // corners
  const corners = report?.corners || [];
  $('#corners', root).innerHTML = corners.length ? `<table>
      <tr><th>Corner</th><th class="num">Best</th><th class="num">Avg</th><th class="num">Avg loss</th><th class="num">Apex best / avg (${speedUnit()})</th><th class="num">Brake ± m</th>${report.referenceLabel ? '<th class="num">vs ref</th><th>Advice</th>' : ''}</tr>
      ${corners.map(c => `<tr><td><b>${esc(c.name)}</b>${c.lockups ? ` <span class="tag red">${c.lockups} lock</span>` : ''}</td>
        <td class="num">${secTime(c.bestTime)}</td><td class="num">${secTime(c.avgTime)}</td>
        <td class="num ${c.avgLossToBest > 0.3 ? 'warn' : ''}">${fixed(c.avgLossToBest, 3)}</td>
        <td class="num">${speedKmh(c.minSpeedBest)} / ${speedKmh(c.minSpeedAvg)}</td>
        <td class="num">${c.brakePointSpread ? '±' + c.brakePointSpread.toFixed(0) : '–'}</td>
        ${report.referenceLabel ? `<td class="num ${deltaClass(c.vsReference?.timeDelta)}">${c.vsReference ? delta(c.vsReference.timeDelta, 2) : '–'}</td><td class="small muted">${esc(c.vsReference?.advice?.[0] || '')}</td>` : ''}</tr>`).join('')}
    </table>` : '<div class="muted small">Corner analysis needs a track model and at least one clean lap.</div>';

  // laps
  const nSec = Math.max(0, ...laps.map(l => l.sectorTimes.length));
  const bestSec = [...Array(nSec)].map((_, i) => Math.min(...laps.filter(l => l.valid && l.sectorTimes.length === nSec).map(l => l.sectorTimes[i])));
  const drawLaps = () => {
    $('#laps', root).innerHTML = `<table>
      <tr><th></th><th>Lap</th><th class="num">Stint</th><th class="num">Time</th><th class="num">Δ best</th>${[...Array(nSec)].map((_, i) => `<th class="num">S${i + 1}</th>`).join('')}<th class="num">Fuel</th><th class="num">Track</th><th>Status</th></tr>
      ${laps.map(l => `<tr class="click ${l.valid ? '' : 'invalid'} ${selected.has(l.id) ? 'sel' : ''}" data-id="${l.id}">
        <td><input type="checkbox" ${selected.has(l.id) ? 'checked' : ''}></td>
        <td>${l.lapNumber}</td><td class="num">${l.stint}</td>
        <td class="num ${l.lapTime === best && l.valid ? 'purple' : ''}">${lapTime(l.lapTime)}</td>
        <td class="num">${l.valid && best ? delta(l.lapTime - best, 3) : ''}</td>
        ${[...Array(nSec)].map((_, i) => { const v = l.sectorTimes[i]; return `<td class="num ${l.valid && v === bestSec[i] ? 'purple' : ''}">${isNum(v) ? v.toFixed(3) : '–'}</td>`; }).join('')}
        <td class="num">${fixed(l.fuelUsed, 2)}</td><td class="num">${temp(l.trackTemp)}</td>
        <td>${l.valid ? '<span class="tag green">clean</span>' : `<span class="tag">${esc(l.invalidReason)}</span>`}${l.hasGps ? '' : ' <span class="tag" title="Recorded live; racing line from dead-reckoning until the .ibt is imported">live</span>'}</td>
      </tr>`).join('')}</table>`;
    for (const tr of $$('#laps tr[data-id]', root)) tr.onclick = () => {
      const lid = +tr.dataset.id;
      selected.has(lid) ? selected.delete(lid) : selected.add(lid);
      drawLaps();
    };
  };
  drawLaps();

  // handling
  const hd = report?.handling;
  $('#handling', root).innerHTML = hd?.valid ? `
    <div class="matrix">
      <div></div><div class="h">Slow</div><div class="h">Medium</div><div class="h">Fast</div>
      ${['entry', 'mid', 'exit'].map(p => `<div class="h">${p}</div>${['slow', 'medium', 'fast'].map(b => {
        const c = hd.cells.find(x => x.phase === p && x.speedBand === b);
        if (!c || c.samples < 60) return `<div class="c dim small">not enough data</div>`;
        return `<div class="c ${c.tendency}"><b>${c.tendency}</b><div class="tiny muted">US ${c.understeerRate.toFixed(0)}% · OS ${c.oversteerRate.toFixed(0)}%${c.countersteer ? ` · ${c.countersteer} catches` : ''}</div></div>`;
      }).join('')}`).join('')}
    </div>
    <div class="small muted" style="margin-top:10px">${hd.findings.map(esc).join('<br>')}</div>`
    : '<div class="muted small">Handling analysis needs a few clean laps at the limit.</div>';

  drawPace($('#pace', root), $('#pace-legend', root), report, laps);

  $('#cmp', root).onclick = () => {
    if (!selected.size) return toast('Tick one or more laps first');
    location.hash = '#/telemetry?laps=' + [...selected].join(',');
  };
  $('#del', root).onclick = async () => {
    if (!confirm('Delete this session and all its laps? This cannot be undone.')) return;
    await api(`/api/sessions/${id}`, { method: 'DELETE' });
    location.hash = '#/sessions';
  };
  $('#save-notes', root).onclick = async () => { await api(`/api/sessions/${id}/notes`, { body: { notes: $('#notes', root).value } }); toast('Notes saved'); };
}

function stat(label, value, sub) {
  return `<div class="card"><div class="stat"><span class="label">${label}</span><span class="value">${value}</span><span class="sub">${esc(sub || '')}</span></div></div>`;
}
function mini(label, value, cls = '') {
  return `<div class="stat"><span class="label">${label}</span><span class="value ${cls}" style="font-size:18px">${value}</span></div>`;
}

function drawPace(canvas, legend, report, laps) {
  const w = canvas.clientWidth, h = canvas.clientHeight, dpr = devicePixelRatio || 1;
  canvas.width = w * dpr; canvas.height = h * dpr;
  const ctx = canvas.getContext('2d'); ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  const pts = laps.filter(l => !l.outLap && !l.inLap && l.lapTime > 0);
  if (!pts.length || !report?.bestLap) return;
  const best = report.bestLap;
  const lim = best * 1.06;
  const shown = pts.filter(l => l.lapTime <= lim);
  if (!shown.length) return;
  const min = best - 0.3, max = Math.max(...shown.map(l => l.lapTime)) + 0.2;
  const pad = { l: 56, r: 10, t: 10, b: 22 };
  const X = i => pad.l + (i / Math.max(1, pts.length - 1)) * (w - pad.l - pad.r);
  const Y = v => pad.t + (1 - (v - min) / (max - min)) * (h - pad.t - pad.b);
  ctx.strokeStyle = '#232A35'; ctx.fillStyle = '#5A6375'; ctx.font = '11px Bahnschrift, Segoe UI';
  for (let k = 0; k <= 4; k++) { const v = min + (max - min) * k / 4; ctx.beginPath(); ctx.moveTo(pad.l, Y(v)); ctx.lineTo(w - pad.r, Y(v)); ctx.stroke(); ctx.fillText(lapTime(v).slice(0, -1), 4, Y(v) + 4); }
  const colors = ['#22D37E', '#4DA3FF', '#FF9640', '#B26BFF', '#F5C542', '#FF6FB5'];
  const stints = [...new Set(pts.map(l => l.stint))];
  stints.forEach((st, si) => {
    const c = colors[si % colors.length];
    const sp = pts.map((l, i) => ({ l, i })).filter(o => o.l.stint === st && o.l.lapTime <= lim);
    ctx.strokeStyle = c; ctx.lineWidth = 1.5; ctx.beginPath();
    sp.forEach((o, k) => { const x = X(o.i), y = Y(o.l.lapTime); k ? ctx.lineTo(x, y) : ctx.moveTo(x, y); }); ctx.stroke();
    for (const o of sp) { ctx.fillStyle = o.l.valid ? c : '#0B0E13'; ctx.strokeStyle = c; ctx.beginPath(); ctx.arc(X(o.i), Y(o.l.lapTime), 3.5, 0, Math.PI * 2); ctx.fill(); ctx.stroke(); }
  });
  legend.innerHTML = stints.map((st, si) => {
    const sum = report.stints.find(x => x.stint === st);
    return `<span><i style="background:${colors[si % colors.length]}"></i>Stint ${st}${sum && isNum(sum.trendPerLap) && sum.validLaps >= 4 ? ` · ${sum.trendPerLap > 0 ? '+' : ''}${sum.trendPerLap.toFixed(2)}s/lap` : ''}${sum && isNum(sum.fuelPerLap) ? ` · ${sum.fuelPerLap.toFixed(2)} L/lap` : ''}</span>`;
  }).join('') + '<span class="muted">hollow = invalid lap</span>';
}
