import { api, onLive, lapTime, delta, deltaClass, fixed, speed, speedUnit, speedKmh, pressure, pressureUnit, temp, esc, isNum, toast, dateTime, $ } from '../core.js';

let off = null, model = null, modelVersion = -1, mapCanvas = null, lastState = null;

export function destroy() { off?.(); off = null; }

export async function render(root) {
  root.innerHTML = `<div id="live-root"></div>`;
  const el = $('#live-root', root);
  let mode = null;
  off = onLive(s => {
    lastState = s;
    const m = s.status === 'waiting' ? 'waiting' : 'live';
    if (m !== mode) { mode = m; m === 'waiting' ? renderWaiting(el) : renderLiveShell(el); }
    if (m === 'live') update(el, s);
    else updateWaiting(el, s);
  });
}

// ---------------------------------------------------------------- not connected

async function renderWaiting(el) {
  el.innerHTML = `
    <div class="page-head"><div class="grow"><h1>Ready when you are</h1><p>Start iRacing and get in the car — recording, analysis, overlays and your engineer start automatically.</p></div></div>
    <div class="grid g2">
      <div class="card">
        <div class="card-head"><h2 class="grow">How it works</h2></div>
        <ol class="steps">
          <li><b>Run iRacing in borderless / windowed mode</b> so the overlays can sit on top (Options → Graphics → Window mode).</li>
          <li><b>Drive.</b> Every lap is recorded and compared to your reference (PB by default). The engineer talks you through laps, fuel, flags and incidents.</li>
          <li><b>After the session</b>, iRacing's .ibt file is imported automatically to add GPS racing lines and tyre data. Open <a href="#/sessions">Sessions</a> for the debrief.</li>
          <li>Position overlays with <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F9</kbd> (drag to move, scroll to resize), hide them with <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F10</kbd>, switch the delta reference with <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F11</kbd>.</li>
        </ol>
      </div>
      <div class="card">
        <div class="card-head"><h2 class="grow">Try it now</h2></div>
        <p class="muted" style="margin-top:0">No iRacing running? Replay one of your recorded sessions as if it were live — overlays, delta, corner feedback and the voice engineer all work.</p>
        <div class="row" id="demo-row"><span class="spinner"></span></div>
        <hr style="border:none;border-top:1px solid var(--line);margin:16px 0">
        <p class="muted" style="margin:0 0 10px">Bring in your history from iRacing's telemetry folder to unlock personal bests, leaderboards and the setup journal.</p>
        <a class="btn" href="#/import">Import telemetry history</a>
      </div>
      <div class="card span2">
        <div class="card-head"><h2 class="grow">Recent sessions</h2><a href="#/sessions">All sessions →</a></div>
        <div id="recent"><div class="empty"><span class="spinner"></span></div></div>
      </div>
      <div class="card span2"><div class="card-head"><h2 class="grow">Engineer log</h2></div><div class="feed" id="feed"></div></div>
    </div>`;
  const [files, sessions] = await Promise.all([api('/api/import/files').catch(() => null), api('/api/sessions').catch(() => [])]);
  const latest = files?.files?.filter(f => f.size > 2_000_000)[0];
  const row = $('#demo-row', el);
  if (latest) {
    row.innerHTML = `<button class="primary" id="demo">▶ Replay latest session</button><select id="demo-speed"><option value="1">Real time</option><option value="2">2×</option><option value="4">4×</option><option value="10">10×</option></select><span class="small muted">${esc(latest.name)}</span>`;
    $('#demo', el).onclick = async () => {
      await api('/api/replay', { body: { file: latest.path, speed: +$('#demo-speed', el).value } });
      toast('Replay started — overlays are visible while it runs.');
    };
  } else row.innerHTML = `<span class="muted small">No .ibt files found in your iRacing telemetry folder.</span>`;
  const recent = $('#recent', el);
  if (!sessions?.length) recent.innerHTML = `<div class="empty">No sessions yet. Drive, or import your telemetry history.</div>`;
  else recent.innerHTML = `<table><tr><th>When</th><th>Car</th><th>Track</th><th>Type</th><th class="num">Laps</th><th class="num">Best</th></tr>
      ${sessions.slice(0, 6).map(s => `<tr class="click" onclick="location.hash='#/session/${s.id}'"><td>${dateTime(s.startedAt)}</td><td>${esc(s.carName)}</td><td>${esc(s.trackName)}</td><td>${esc(s.sessionType)}</td><td class="num">${s.validLaps}/${s.lapCount}</td><td class="num">${lapTime(s.bestLap)}</td></tr>`).join('')}</table>`;
}

function updateWaiting(el, s) {
  const feed = $('#feed', el);
  if (feed) feed.innerHTML = feedHtml(s.messages);
}

// ---------------------------------------------------------------- live

function renderLiveShell(el) {
  el.innerHTML = `
    <div class="grid g-live">
      <div class="grid">
        <div class="card">
          <div class="card-head"><h3 class="grow">Delta</h3>
            <div class="seg" id="refmode"><button data-m="pb">PB</button><button data-m="session">Session best</button><button data-m="last">Last lap</button></div>
          </div>
          <div class="row" style="align-items:flex-end;gap:28px">
            <div><div class="delta-big" id="delta">–</div><div class="small muted" id="reflabel"></div></div>
            <div class="stats" style="margin-left:auto">
              <div class="stat"><span class="label">Current</span><span class="value" id="cur">–</span></div>
              <div class="stat"><span class="label">Predicted</span><span class="value" id="pred">–</span></div>
            </div>
          </div>
          <div class="delta-bar"><div id="dbar"></div></div>
          <div class="stats" style="margin-top:12px">
            <div class="stat"><span class="label">Last lap</span><span class="value" id="last">–</span><span class="sub" id="lastd"></span></div>
            <div class="stat"><span class="label">Session best</span><span class="value" id="sbest">–</span></div>
            <div class="stat"><span class="label">Personal best</span><span class="value purple" id="pb">–</span></div>
            <div class="stat"><span class="label">Position</span><span class="value" id="pos">–</span></div>
            <div class="stat"><span class="label">Lap</span><span class="value" id="lap">–</span><span class="sub" id="remain"></span></div>
          </div>
        </div>
        <div class="grid g2">
          <div class="card"><div class="card-head"><h3 class="grow">Sectors</h3></div><div id="sectors"></div></div>
          <div class="card">
            <div class="card-head"><h3 class="grow">Inputs</h3><span class="num" id="gear" style="font-size:22px"></span></div>
            <div class="small muted">Throttle</div><div class="pedal"><div id="thr" style="background:var(--accent)"></div></div>
            <div class="small muted" style="margin-top:8px">Brake</div><div class="pedal"><div id="brk" style="background:var(--red)"></div></div>
            <div class="row" style="margin-top:12px;justify-content:space-between">
              <div class="stat"><span class="label">Speed</span><span class="value" id="spd">–</span></div>
              <div class="stat"><span class="label">Ref speed</span><span class="value purple" id="rspd">–</span></div>
              <div class="stat"><span class="label">Next brake</span><span class="value" id="nbrake">–</span><span class="sub" id="ncorner"></span></div>
            </div>
          </div>
        </div>
        <div class="card"><div class="card-head"><h3 class="grow">Corner feedback · this lap</h3><span class="small muted" id="lastcorner"></span></div><div class="small" id="coachtip" style="margin-bottom:8px"></div><div id="corners"></div></div>
        <div class="card"><div class="card-head"><h3 class="grow">Race engineer</h3></div><div class="feed" id="feed"></div></div>
      </div>
      <div class="grid" style="align-content:start">
        <div class="card tight"><canvas id="livemap" class="map-canvas"></canvas></div>
        <div class="card"><div class="card-head"><h3 class="grow">Setup session</h3><button class="small" id="setup-toggle">Start</button></div><div id="setupsess"></div></div>
        <div class="card"><div class="card-head"><h3 class="grow">Fuel</h3><a href="#/fuel" class="small">Planner →</a></div><div id="fuel"></div></div>
        <div class="card"><div class="card-head"><h3 class="grow">Tyres</h3><a href="#/tyres" class="small">Tyre tool →</a></div><div id="tyreload"></div><div id="tyres"></div></div>
        <div class="card"><div class="card-head"><h3 class="grow">Conditions & car</h3></div><div id="wx"></div></div>
      </div>
    </div>`;
  for (const b of el.querySelectorAll('#refmode button')) b.onclick = async () => {
    await api('/api/reference', { body: { mode: b.dataset.m } });
    toast('Reference: ' + b.textContent);
  };
  $('#setup-toggle', el).onclick = async () => { await api('/api/ask/setup-toggle', { body: {} }); };
  mapCanvas = $('#livemap', el);
  modelVersion = -1;
}

async function ensureModel(s) {
  if (s.trackModelVersion === modelVersion) return;
  modelVersion = s.trackModelVersion;
  model = s.trackModelVersion ? await api('/api/track/current').catch(() => null) : null;
}

function update(el, s) {
  if (!$('#delta', el)) return;
  ensureModel(s);
  const set = (id, v, cls) => { const e = $('#' + id, el); if (!e) return; e.textContent = v; if (cls !== undefined) e.className = e.className.replace(/\b(good|bad|muted|purple)\b/g, '').trim() + ' ' + cls; };
  for (const b of el.querySelectorAll('#refmode button')) b.classList.toggle('on', b.dataset.m === s.referenceMode);
  set('delta', s.outLap ? 'OUT LAP' : delta(s.delta, 2), s.outLap ? 'muted' : deltaClass(s.delta));
  set('reflabel', s.referenceLabel ? 'vs ' + s.referenceLabel : 'No reference yet — drive a clean lap');
  set('cur', lapTime(s.currentLapTime)); set('pred', lapTime(s.predictedLap));
  set('last', lapTime(s.lastLapTime)); set('lastd', isNum(s.lastLapDelta) ? delta(s.lastLapDelta) + ' vs ref' : '');
  set('sbest', lapTime(s.sessionBest)); set('pb', lapTime(s.personalBest));
  set('pos', s.classPosition > 0 ? `P${s.classPosition}${s.carsInClass ? '/' + s.carsInClass : ''}` : '–');
  set('lap', s.lap ?? '–');
  set('remain', s.sessionLapsRemain > 0 ? `${s.sessionLapsRemain} to go` : isNum(s.sessionTimeRemain) && s.sessionTimeRemain > 0 && s.sessionTimeRemain < 86400 ? new Date(s.sessionTimeRemain * 1000).toISOString().substr(11, 8) + ' left' : '');
  const bar = $('#dbar', el);
  if (isNum(s.delta)) {
    const f = Math.min(1, Math.abs(s.delta)) * 50;
    bar.style.background = s.delta <= 0 ? 'var(--accent)' : 'var(--red)';
    bar.style.left = s.delta <= 0 ? '50%' : (50 - f) + '%'; bar.style.width = f + '%';
  } else bar.style.width = '0';

  $('#sectors', el).innerHTML = s.sectors.length ? s.sectors.map(x => {
    const cls = x.color === 'purple' ? 'purple' : x.color === 'green' ? 'good' : x.color === 'yellow' ? 'warn' : deltaClass(x.delta);
    return `<div class="sector-row ${x.state}"><span class="lbl">S${x.index}</span><span>${x.state === 'pending' ? '<span class="dim">' + (isNum(x.refTime) ? x.refTime.toFixed(3) : '–') + '</span>' : (isNum(x.time) ? x.time.toFixed(3) : '–')}</span><span class="right ${cls}">${x.state === 'pending' ? '' : delta(x.delta)}</span></div>`;
  }).join('') : '<div class="muted small">Sectors appear once a flying lap starts.</div>';

  $('#thr', el).style.width = (s.throttle * 100) + '%';
  $('#brk', el).style.width = (s.brake * 100) + '%';
  set('gear', s.gear === 0 ? 'N' : s.gear === -1 ? 'R' : s.gear);
  set('spd', speed(s.speed) + ' ' + speedUnit()); set('rspd', isNum(s.refSpeed) ? speed(s.refSpeed) : '–');
  set('nbrake', isNum(s.nextBrakeDist) ? s.nextBrakeDist.toFixed(0) + ' m' : '–');
  set('ncorner', s.nextCorner ? `${s.nextCorner}${isNum(s.nextCornerRefMinSpeed) ? ' · apex ' + speedKmh(s.nextCornerRefMinSpeed) : ''}` : '');

  const lc = s.lastCorner;
  set('lastcorner', lc ? `last: ${lc.name} ${delta(lc.timeDelta, 2)}` : '');
  $('#corners', el).innerHTML = s.lapCorners.length ? `<table><tr><th>Corner</th><th class="num">Δ time</th><th class="num">Apex</th><th class="num">Δ apex</th><th class="num">Brake</th><th>Note</th></tr>
    ${s.lapCorners.map(c => `<tr><td>${esc(c.name)}</td><td class="num ${deltaClass(c.timeDelta)}">${delta(c.timeDelta, 2)}</td><td class="num">${speedKmh(c.minSpeed)}</td><td class="num ${deltaClass(-c.minSpeedDiff)}">${isNum(c.minSpeedDiff) ? (c.minSpeedDiff >= 0 ? '+' : '−') + Math.abs(c.minSpeedDiff).toFixed(0) : '–'}</td><td class="num">${isNum(c.brakeDiff) ? (c.brakeDiff >= 0 ? '+' : '−') + Math.abs(c.brakeDiff).toFixed(0) + ' m' : '–'}</td><td class="small muted">${esc(c.advice || c.verdict)}</td></tr>`).join('')}</table>`
    : `<div class="muted small">${s.referenceLabel ? 'Feedback appears after each corner on a flying lap.' : 'Complete a clean lap to set a reference.'}</div>`;

  $('#feed', el).innerHTML = feedHtml(s.messages);
  $('#coachtip', el).innerHTML = s.coachTip ? `<span class="muted">Coach:</span> ${esc(s.coachTip)}` : '';
  $('#tyreload', el).innerHTML = tyreLoadHtml(s.tyreLoad);
  const running = !['Off', 'Done'].includes(s.setupState);
  set('setup-toggle', running ? 'Stop' : 'Start');
  $('#setupsess', el).innerHTML = (s.setupInstruction ? `<div class="warn" style="font-weight:600;margin-bottom:6px">To do: ${esc(s.setupInstruction)}</div>` : '')
    + `<div class="small ${s.setupStatus ? '' : 'muted'}">${esc(s.setupStatus || 'Drive a run, change one thing, compare. Start it here or from a wheel button.')}</div>`
    + (s.quietMode ? '<div class="small warn" style="margin-top:6px">Quiet mode on — only important calls.</div>' : '');

  const f = s.fuel;
  $('#fuel', el).innerHTML = f ? `<div class="stats">
      <div class="stat"><span class="label">Level</span><span class="value">${fixed(f.fuelLevel, 1)} L</span></div>
      <div class="stat"><span class="label">Per lap</span><span class="value">${fixed(f.perLapPredicted, 2)}</span><span class="sub">last ${fixed(f.perLapLast, 2)} · max ${fixed(f.perLapMax, 2)}</span></div>
      <div class="stat"><span class="label">Laps left</span><span class="value ${f.lapsInTank < 2 ? 'bad' : ''}">${fixed(f.lapsInTank, 1)}</span></div>
      ${isNum(f.fuelToFinish) ? `<div class="stat"><span class="label">To finish</span><span class="value">${fixed(f.fuelToFinish, 1)} L</span><span class="sub ${f.fuelToAdd > 0.1 ? 'warn' : 'good'}">${f.fuelToAdd > 0.1 ? 'add ' + Math.ceil(f.fuelToAdd) + ' L' : '+' + fixed(f.spare, 1) + ' L spare'}</span></div>` : ''}
    </div>` : '<span class="muted small">–</span>';

  $('#tyres', el).innerHTML = s.tyres.length === 4 ? `<div class="tyre-grid">${s.tyres.map(t => `
      <div class="tyre"><div class="row" style="justify-content:space-between"><b>${t.name}</b><span class="num ${t.pressStatus === 'low' ? 'blue' : t.pressStatus === 'high' ? 'bad' : ''}">${isNum(t.pressure) ? pressure(t.pressure) : pressure(t.coldPressure) + (isNum(t.coldPressure) ? ' cold' : '')} <span class="dim small">${pressureUnit()}</span></span></div>
      <div class="temps">${[t.tempIn, t.tempMid, t.tempOut].map(v => `<div style="background:${tempColor(v, t.tempStatus)}">${temp(v)}</div>`).join('')}</div>
      <div class="tiny muted">in · mid · out${t.carcass ? ' · pit reading' : ''}${isNum(t.wear) ? ` · wear ${(t.wear * 100).toFixed(0)}%` : ''}</div></div>`).join('')}</div>` : '';

  const w = s.weather, hl = s.health;
  $('#wx', el).innerHTML = `<div class="kv">
    <span class="k">Air / track</span><span class="num">${temp(w.airTemp)} / ${temp(w.trackTemp)} ${Math.abs(w.trackTempTrend) > 0.5 ? `<span class="small muted">(${w.trackTempTrend > 0 ? '↑' : '↓'}${Math.abs(w.trackTempTrend).toFixed(0)}°/h)</span>` : ''}</span>
    <span class="k">Sky / track</span><span>${esc(w.skies)} · ${esc(w.wetness)}${w.precipitation > 0.02 ? ` · rain ${(w.precipitation * 100).toFixed(0)}%` : ''}</span>
    <span class="k">Incidents</span><span class="num">${hl.incidents}x${hl.incidentLimit ? ' / ' + hl.incidentLimit : ''}</span>
    <span class="k">Damage</span><span class="${hl.repairLeft > 0.5 || hl.repairFlag ? 'bad' : hl.possibleDamage ? 'warn' : 'good'}">${hl.repairLeft > 0.5 ? `repairs ${hl.repairLeft.toFixed(0)}s` : hl.optRepairLeft > 0.5 ? `optional repairs ${hl.optRepairLeft.toFixed(0)}s` : hl.possibleDamage ? `pace −${hl.paceLossPct.toFixed(1)}% since contact` : 'none reported'}</span>
    ${hl.warnings.length ? `<span class="k">Warnings</span><span class="bad">${esc(hl.warnings.join(', '))}</span>` : ''}
  </div>`;
  drawMap(s);
}

const LOAD_STATE = {
  cold: ['Cold — building temperature', 'blue'], ok: ['Tyres OK — push', 'good'], learning: ['Learning your normal tyre load (3 clean laps)', 'muted'],
  'hot-front': ['Fronts overheating — cool them', 'bad'], 'hot-rear': ['Rears overheating — cool them', 'bad'], hot: ['All four overheating — back off', 'bad'],
};

function tyreLoadHtml(t) {
  if (!t || !t.state) return '';
  const [label, cls] = LOAD_STATE[t.state] || [t.state, 'muted'];
  const warm = t.state === 'cold' && isNum(t.warmPct) ? ` · ${(t.warmPct * 100).toFixed(0)}%`
    : t.state.startsWith('hot') && isNum(t.coolLaps) ? ` · ~${t.coolLaps < 0.75 ? 'half a lap' : t.coolLaps.toFixed(1) + ' laps'} to cool` : '';
  const cell = (n, v) => {
    const pct = isNum(v) ? Math.round(v * 100) : null;
    const c = pct == null ? 'var(--panel3)' : v >= 1.2 ? 'var(--red)' : v >= 1.08 ? '#F5B942' : 'var(--accent)';
    return `<div style="flex:1;text-align:center"><div class="tiny muted">${n}</div><div class="num" style="border-bottom:3px solid ${c}">${pct == null ? '–' : pct + '%'}</div></div>`;
  };
  return `<div class="${cls}" style="font-weight:600">${label}${warm}</div>
    ${t.hasBaseline ? `<div class="row" style="gap:6px;margin:6px 0 10px">${['LF', 'RF', 'LR', 'RR'].map((n, i) => cell(n, t.load[i])).join('')}</div>
    <div class="tiny muted" style="margin-bottom:10px">Sliding over the last lap vs your normal clean laps here (100% = normal).</div>` : '<div style="height:8px"></div>'}`;
}

function tempColor(v, status) {
  if (!isNum(v)) return 'var(--panel3)';
  return status === 'cold' ? '#4DA3FF' : status === 'hot' ? '#FF4D5E' : '#22D37E';
}

function feedHtml(msgs) {
  if (!msgs?.length) return '<div class="muted small">Messages from your engineer appear here.</div>';
  return msgs.slice().reverse().map(m => `<div class="msg ${m.category}"><span class="t">${new Date(m.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</span><span>${esc(m.text)}</span></div>`).join('');
}

function drawMap(s) {
  const c = mapCanvas;
  if (!c) return;
  const w = c.clientWidth, h = c.clientHeight;
  const dpr = devicePixelRatio || 1;
  if (c.width !== Math.round(w * dpr)) { c.width = Math.round(w * dpr); c.height = Math.round(h * dpr); }
  const ctx = c.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);
  if (!model?.x?.length) {
    ctx.fillStyle = '#8A94A6'; ctx.font = '13px Segoe UI';
    ctx.fillText('Track map builds after your first clean lap.', 14, 24);
    return;
  }
  let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
  for (let i = 0; i < model.x.length; i++) { minX = Math.min(minX, model.x[i]); maxX = Math.max(maxX, model.x[i]); minY = Math.min(minY, model.y[i]); maxY = Math.max(maxY, model.y[i]); }
  const pad = 20, k = Math.min((w - 2 * pad) / (maxX - minX), (h - 2 * pad) / (maxY - minY));
  const ox = (w - (maxX - minX) * k) / 2, oy = (h - (maxY - minY) * k) / 2;
  const P = (x, y) => [ox + (x - minX) * k, oy + (maxY - y) * k];
  ctx.lineCap = 'round'; ctx.lineJoin = 'round';
  ctx.strokeStyle = '#262D38'; ctx.lineWidth = 10; ctx.beginPath();
  model.x.forEach((x, i) => { const [a, b] = P(x, model.y[i]); i ? ctx.lineTo(a, b) : ctx.moveTo(a, b); }); ctx.closePath(); ctx.stroke();
  ctx.strokeStyle = '#5B6577'; ctx.lineWidth = 2; ctx.stroke();
  ctx.fillStyle = '#8A94A6'; ctx.font = '600 11px Segoe UI';
  for (const cn of model.corners) { const i = Math.round(cn.apex / model.step) % model.x.length; const [a, b] = P(model.x[i], model.y[i]); ctx.fillText(cn.name, a + 8, b - 6); }
  const posAt = pct => { const f = pct * model.length / model.step; const i = Math.floor(f) % model.x.length, j = (i + 1) % model.x.length, t = f - Math.floor(f); return [model.x[i] + (model.x[j] - model.x[i]) * t, model.y[i] + (model.y[j] - model.y[i]) * t]; };
  for (const car of [...s.mapCars].sort((a, b) => a.isPlayer - b.isPlayer)) {
    const [x, y] = posAt(car.pct); const [a, b] = P(x, y);
    ctx.beginPath(); ctx.arc(a, b, car.isPlayer ? 7 : 5, 0, Math.PI * 2);
    ctx.fillStyle = car.isPlayer ? '#22D37E' : car.inPit ? '#4A5262' : car.classColor;
    ctx.fill(); ctx.lineWidth = car.isPlayer ? 2 : 1; ctx.strokeStyle = car.isPlayer ? '#fff' : '#0B0E13'; ctx.stroke();
  }
}
