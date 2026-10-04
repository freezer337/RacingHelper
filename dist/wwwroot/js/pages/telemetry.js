import { api, lapTime, delta, deltaClass, esc, isNum, dateTime, lapColors, speedKmh, speedUnit, settings, toast, $, $$ } from '../core.js';
import { TelemetryChart, TrackMapView } from '../charts.js';

let chart = null, map = null;
export function destroy() { chart?.destroy(); map?.destroy(); chart = map = null; }

export async function render(root, params) {
  const ids = (params.laps || '').split(',').filter(Boolean);
  if (!ids.length) return picker(root);
  root.innerHTML = `<div class="empty"><span class="spinner"></span> Loading telemetry…</div>`;
  const data = await api(`/api/compare?laps=${ids.join(',')}&step=2`);
  if (data.error) { root.innerHTML = `<div class="card"><h2>Can't compare these laps</h2><p class="muted">${esc(data.error)}</p><a href="#/telemetry">Pick other laps</a></div>`; return; }

  const laps = data.laps.map((l, i) => ({ ...l, color: lapColors[i % lapColors.length] }));
  const mph = settings?.speedUnit === 'mph';
  const conv = v => mph ? v / 1.609344 : v;
  root.innerHTML = `
    <div class="page-head">
      <div class="grow"><h1>Telemetry</h1><p>Drag on a chart to zoom, scroll to zoom around the cursor, double-click to reset. Click a corner to jump to it.</p></div>
      <a class="btn" href="#/telemetry">Pick laps</a>
    </div>
    <div class="card tight" style="margin-bottom:14px"><div class="row" id="lapchips"></div></div>
    <div class="grid" style="grid-template-columns:minmax(0,1.7fr) minmax(300px,1fr)">
      <div class="card tight"><div id="chart"></div></div>
      <div class="grid" style="align-content:start">
        <div class="card tight">
          <div class="row" style="justify-content:space-between;margin-bottom:8px">
            <div class="seg" id="mapmode"><button data-m="laps" class="on">Lines</button><button data-m="speed">Speed</button><button data-m="inputs">Inputs</button>${laps.length > 1 ? '<button data-m="delta">Gain/loss</button>' : ''}</div>
            <button class="small ghost" id="reset">Full lap</button>
          </div>
          <canvas id="map" class="map-canvas"></canvas>
          <div class="legend tiny" style="margin-top:6px"><span>■ braking point</span><span>● apex (min speed)</span><span>▲ throttle pickup</span></div>
        </div>
        <div class="card tight"><div class="chips" id="cornerchips"></div></div>
        <div class="card tight"><div id="sectors"></div></div>
      </div>
    </div>
    <div class="card" style="margin-top:14px" id="cmpcard"><div class="card-head"><h2 class="grow">Corner by corner</h2><span class="small muted" id="cmp-sub"></span></div><div class="table-wrap" id="cmp"></div></div>`;

  // lap chips
  $('#lapchips', root).innerHTML = laps.map((l, i) => `
    <div class="chip" style="border-color:${l.color}55;color:var(--text);cursor:default">
      <i style="width:10px;height:10px;border-radius:50%;background:${l.color};display:inline-block"></i>
      <b class="num">${lapTime(l.lapTime)}</b><span class="muted">lap ${l.lapNumber} · ${esc(l.driver)} · ${dateTime(l.startedAt)}</span>
      ${i === 0 ? '<span class="tag purple">reference</span>' : `<span class="num ${deltaClass(l.lapTime - laps[0].lapTime)}">${delta(l.lapTime - laps[0].lapTime)}</span>`}
      ${i > 0 ? `<button class="small ghost" data-ref="${l.id}" title="Use as reference">⇄</button>` : ''}
      <button class="small ghost" data-rm="${l.id}" title="Remove">✕</button>
    </div>`).join('');
  for (const b of $$('[data-ref]', root)) b.onclick = () => { const rest = ids.filter(x => x !== b.dataset.ref); location.hash = `#/telemetry?laps=${[b.dataset.ref, ...rest].join(',')}`; };
  for (const b of $$('[data-rm]', root)) b.onclick = () => { const rest = ids.filter(x => x !== b.dataset.rm); location.hash = `#/telemetry?laps=${rest.join(',')}`; };

  const chans = l => ({
    speed: l.speed.map(conv), delta: l.delta, throttle: l.throttle, brake: l.brake, steer: l.steer, gear: l.gear, rpm: l.rpm, latG: l.latG,
  });
  const lanes = [
    { key: 'speed', label: 'Speed', unit: speedUnit(), height: 170 },
    ...(laps.length > 1 ? [{ key: 'delta', label: 'Delta', unit: 's vs ref', height: 95, zero: true, symmetric: true, minSpan: 0.2 }] : []),
    { key: 'throttle', label: 'Throttle', unit: '%', height: 72, min: -4, max: 104 },
    { key: 'brake', label: 'Brake', unit: '%', height: 72, min: -4, max: 104 },
    { key: 'steer', label: 'Steering', unit: '°', height: 80, zero: true },
    { key: 'gear', label: 'Gear', height: 56 },
    { key: 'rpm', label: 'RPM', height: 70 },
    { key: 'latG', label: 'Lateral', unit: 'g', height: 70, zero: true, symmetric: true },
  ];
  const fmt = (k, v) => k === 'delta' ? delta(v, 3) : k === 'gear' ? v.toFixed(0) : k === 'latG' ? v.toFixed(2) : v.toFixed(0);
  map = new TrackMapView($('#map', root));
  chart = new TelemetryChart($('#chart', root), {
    lanes, format: fmt,
    onCursor: x => map.setCursor(x),
    onZoom: (a, b) => map.setDomain(a >= 1 || b < data.length - 1 ? a : null, b),
  });
  chart.setData({
    step: data.step, length: data.length,
    corners: data.model.corners.map(c => ({ name: c.name, start: c.segStart, end: c.segEnd, apex: c.apex })),
    sectors: data.model.sectorStarts.slice(1).map(p => p * data.length),
    laps: laps.map(l => ({ color: l.color, channels: chans(l) })),
  });

  map.setModel({ ...data.model, length: data.length });
  const allSpeeds = laps.flatMap(l => l.speed).filter(isNum);
  map.setSpeedRange(Math.min(...allSpeeds), Math.max(...allSpeeds));
  map.setLaps(laps.map(l => ({ x: l.x, y: l.y, color: l.color, speed: l.speed, throttle: l.throttle, brake: l.brake, delta: l.delta })), data.step);
  const markers = [];
  laps.forEach((l, li) => {
    for (const c of l.analysis.corners) {
      if (isNum(c.brakePoint)) markers.push({ lap: li, dist: c.brakePoint, kind: 'brake', color: l.color });
      if (isNum(c.apexDist)) markers.push({ lap: li, dist: c.apexDist, kind: 'apex', color: l.color });
      if (isNum(c.throttlePoint)) markers.push({ lap: li, dist: c.throttlePoint, kind: 'throttle', color: l.color });
    }
  });
  map.setMarkers(markers);
  for (const b of $$('#mapmode button', root)) b.onclick = () => { $$('#mapmode button', root).forEach(x => x.classList.toggle('on', x === b)); map.setMode(b.dataset.m); };
  $('#reset', root).onclick = () => { chart.resetZoom(); map.setDomain(null); };

  // corner chips → zoom
  const zoomTo = c => { chart.setDomain(c.segStart, c.segEnd, false); map.setDomain(c.segStart, c.segEnd); };
  $('#cornerchips', root).innerHTML = data.model.corners.map(c => `<span class="chip" data-c="${c.index}">${esc(c.name)}</span>`).join('') + '<span class="chip" data-c="all">Full lap</span>';
  for (const ch of $$('#cornerchips .chip', root)) ch.onclick = () => {
    $$('#cornerchips .chip', root).forEach(x => x.classList.toggle('on', x === ch));
    if (ch.dataset.c === 'all') { chart.resetZoom(); map.setDomain(null); return; }
    zoomTo(data.model.corners.find(c => c.index === +ch.dataset.c));
  };

  // sectors
  const n = Math.max(...laps.map(l => l.sectors.length));
  $('#sectors', root).innerHTML = n > 1 ? `<table><tr><th></th>${[...Array(n)].map((_, i) => `<th class="num">S${i + 1}</th>`).join('')}<th class="num">Lap</th></tr>
    ${laps.map((l, li) => `<tr><td><i style="display:inline-block;width:10px;height:10px;border-radius:50%;background:${l.color}"></i></td>
      ${[...Array(n)].map((_, i) => { const v = l.sectors[i], r = laps[0].sectors[i]; return `<td class="num">${isNum(v) ? v.toFixed(3) : '–'}${li > 0 && isNum(v) && isNum(r) ? `<div class="tiny ${deltaClass(v - r)}">${delta(v - r)}</div>` : ''}</td>`; }).join('')}
      <td class="num">${lapTime(l.lapTime)}</td></tr>`).join('')}</table>` : '<span class="muted small">No sector data.</span>';

  // corner comparison (lap 2 vs reference)
  if (laps.length < 2) { $('#cmpcard', root).querySelector('#cmp').innerHTML = cornerTable(laps[0]); $('#cmp-sub', root).textContent = 'single lap — add a reference lap to see where time is gained or lost'; return; }
  const cmp = laps[1].comparison;
  $('#cmp-sub', root).textContent = `${lapTime(laps[1].lapTime)} vs reference ${lapTime(laps[0].lapTime)}`;
  $('#cmp', root).innerHTML = `<table>
    <tr><th>Corner</th><th class="num">Time</th><th class="num">Brake point</th><th class="num">Entry</th><th class="num">Apex</th><th class="num">Exit</th><th class="num">Throttle</th><th>What to do</th></tr>
    ${cmp.map(c => `<tr class="click" data-c="${c.corner}">
      <td><b>${esc(c.name)}</b></td>
      <td class="num ${deltaClass(c.timeDelta)}">${delta(c.timeDelta, 3)}</td>
      <td class="num">${m(c.brakeDiff, ' m', true)}</td>
      <td class="num">${kmh(c.entrySpeedDiff)}</td>
      <td class="num">${kmh(c.minSpeedDiff)}</td>
      <td class="num">${kmh(c.exitSpeedDiff)}</td>
      <td class="num">${m(c.throttleDiff, ' m', false)}</td>
      <td class="small">${c.advice.map(esc).join('<br>')}</td></tr>`).join('')}
  </table>`;
  for (const tr of $$('#cmp tr[data-c]', root)) tr.onclick = () => zoomTo(data.model.corners.find(c => c.index === +tr.dataset.c));
}

const kmh = v => !isNum(v) ? '–' : `<span class="${v >= 0 ? 'good' : 'bad'}">${v >= 0 ? '+' : '−'}${speedKmh(Math.abs(v))}</span>`;
// brake: + = later (good when same apex), throttle: + = later (bad)
const m = (v, unit, laterIsGood) => !isNum(v) ? '–' : `<span class="${(v >= 0) === laterIsGood ? 'good' : 'bad'}">${v >= 0 ? '+' : '−'}${Math.abs(v).toFixed(0)}${unit}</span>`;

function cornerTable(l) {
  return `<table><tr><th>Corner</th><th class="num">Segment</th><th class="num">Braking at</th><th class="num">Entry</th><th class="num">Apex</th><th class="num">Exit (${speedUnit()})</th><th class="num">Gear</th></tr>
    ${l.analysis.corners.map(c => `<tr><td><b>${esc(c.name)}</b></td><td class="num">${isNum(c.segTime) ? c.segTime.toFixed(3) : '–'}</td><td class="num">${isNum(c.brakePoint) ? c.brakePoint.toFixed(0) + ' m' : isNum(c.liftPoint) ? 'lift' : 'flat'}</td>
      <td class="num">${speedKmh(c.entrySpeed * 3.6)}</td><td class="num">${speedKmh(c.minSpeed * 3.6)}</td><td class="num">${speedKmh(c.exitSpeed * 3.6)}</td><td class="num">${c.apexGear}</td></tr>`).join('')}</table>`;
}

// ---------------------------------------------------------------- lap picker

async function picker(root) {
  root.innerHTML = `
    <div class="page-head"><div class="grow"><h1>Telemetry</h1><p>Pick a reference lap first (purple), then the lap(s) to compare against it.</p></div></div>
    <div class="card" style="margin-bottom:14px"><div class="row"><select id="combo" style="min-width:320px"></select><span class="muted small" id="pick-count"></span><button class="primary" id="go" disabled>Compare</button></div></div>
    <div class="grid g2"><div class="card"><div class="card-head"><h2 class="grow">Your fastest laps</h2></div><div id="mine" class="scroll-y"></div></div>
    <div class="card"><div class="card-head"><h2 class="grow">Other drivers</h2><span class="small muted">from imported .ibt files</span></div><div id="rivals" class="scroll-y"></div></div></div>`;
  const combos = await api('/api/combos');
  const sel = $('#combo', root);
  if (!combos.length) { sel.outerHTML = '<span class="muted">No laps yet — drive or import telemetry first.</span>'; return; }
  sel.innerHTML = combos.map(c => `<option value="${esc(c.carPath)}|${esc(c.trackKey)}">${esc(c.carName)} · ${esc(c.trackName)}${c.trackConfig ? ' – ' + esc(c.trackConfig) : ''}</option>`).join('');
  const picked = [];
  const refresh = () => {
    $('#pick-count', root).textContent = picked.length ? `${picked.length} selected` : '';
    $('#go', root).disabled = !picked.length;
    for (const tr of $$('tr[data-id]', root)) {
      const idx = picked.indexOf(tr.dataset.id);
      tr.classList.toggle('sel', idx >= 0);
      tr.querySelector('.pick').innerHTML = idx < 0 ? '' : `<i style="display:inline-block;width:10px;height:10px;border-radius:50%;background:${lapColors[idx]}"></i>`;
    }
  };
  const table = rows => rows.length ? `<table><tr><th></th><th class="num">Time</th><th>Driver</th><th>Session</th><th class="num">Track</th></tr>
      ${rows.map(l => `<tr class="click" data-id="${l.id}"><td class="pick"></td><td class="num">${lapTime(l.lapTime)}</td><td>${esc(l.driverName)}</td><td class="small muted">${esc(l.sessionLabel)}</td><td class="num small">${isNum(l.trackTemp) ? l.trackTemp.toFixed(0) + '°' : ''}${l.wetness >= 3 ? ' <span class="tag blue">wet</span>' : ''}</td></tr>`).join('')}</table>` : '<div class="muted small">None.</div>';
  const load = async () => {
    const [car, track] = sel.value.split('|');
    const lb = await api(`/api/leaderboard?car=${encodeURIComponent(car)}&track=${encodeURIComponent(track)}`);
    $('#mine', root).innerHTML = table(lb.mine);
    $('#rivals', root).innerHTML = table(lb.rivals);
    for (const tr of $$('tr[data-id]', root)) tr.onclick = () => {
      const i = picked.indexOf(tr.dataset.id);
      if (i >= 0) picked.splice(i, 1); else if (picked.length < 6) picked.push(tr.dataset.id); else toast('Up to 6 laps');
      refresh();
    };
    picked.length = 0; refresh();
  };
  sel.onchange = load;
  $('#go', root).onclick = () => location.hash = '#/telemetry?laps=' + picked.join(',');
  load();
}
