import { api, onLive, lapTime, esc, isNum, dateTime, pressure, pressureUnit, temp, toast, settings, $, $$ } from '../core.js';

let off = null;
export function destroy() { off?.(); off = null; }

const toUnit = kpa => settings?.pressureUnit === 'psi' ? kpa * 0.1450377 : settings?.pressureUnit === 'bar' ? kpa / 100 : kpa;
const fromUnit = v => settings?.pressureUnit === 'psi' ? v / 0.1450377 : settings?.pressureUnit === 'bar' ? v * 100 : v;

export async function render(root, params) {
  const sessions = (await api('/api/sessions')).filter(s => s.lapCount > 0);
  root.innerHTML = `
    <div class="page-head"><div class="grow"><h1>Tyre tool</h1><p>Hot pressures and temperatures per lap or stint, against a target window — with suggested cold pressures you can send straight to the pit crew.</p></div></div>
    <div class="card" style="margin-bottom:14px"><div class="row">
      <select id="sess" style="min-width:340px">${sessions.map(s => `<option value="${s.id}">${dateTime(s.startedAt)} · ${esc(s.carName)} · ${esc(s.trackName)} · ${esc(s.sessionType)}</option>`).join('')}</select>
      <select id="stint"></select>
      <div class="seg" id="mode"><button data-m="avg" class="on">Average</button><button data-m="max">Peak</button></div>
    </div></div>
    <div id="body"></div>`;
  if (!sessions.length) { $('#body', root).innerHTML = '<div class="card empty">No sessions with tyre data yet. Tyre temps & hot pressures come from iRacing .ibt files (imported automatically after each session).</div>'; return; }
  if (params.session) $('#sess', root).value = params.session;
  let mode = 'avg';
  const load = async (resetStint) => {
    const id = $('#sess', root).value;
    const stintSel = $('#stint', root);
    const st = resetStint ? '' : stintSel.value;
    const d = await api(`/api/tyres/session/${id}?mode=${mode}${st ? '&stint=' + st : ''}`);
    if (resetStint) stintSel.innerHTML = `<option value="">All stints</option>` + d.stints.map(s => `<option value="${s}">Stint ${s}</option>`).join('');
    draw(root, d);
  };
  $('#sess', root).onchange = () => load(true);
  $('#stint', root).onchange = () => load(false);
  for (const b of $$('#mode button', root)) b.onclick = () => { mode = b.dataset.m; $$('#mode button', root).forEach(x => x.classList.toggle('on', x === b)); load(false); };
  load(true);
}

function draw(root, d) {
  const body = $('#body', root);
  if (!d.laps.length) { body.innerHTML = '<div class="card empty">This session has no tyre data (recorded live without an .ibt file). Keep iRacing telemetry logging on to get it.</div>'; return; }
  const t = d.target;
  body.innerHTML = `
    <div class="grid g2">
      <div class="card"><div class="card-head"><h2 class="grow">Tyres</h2><span class="small muted">inner · middle · outer</span></div><div class="tyre-grid" id="tg"></div></div>
      <div class="card"><div class="card-head"><h2 class="grow">Recommendations</h2></div><div id="advice" class="insights"></div>
        <div class="row" style="margin-top:12px"><button class="primary" id="apply">Send suggested cold pressures to pit</button><span class="small muted">applies at your next tyre change (live session only)</span></div></div>
    </div>
    <div class="card" style="margin-top:14px"><div class="card-head"><h2 class="grow">Target window</h2><span class="small muted">per car · ${esc(d.carPath)}</span></div>
      <div class="form-grid">
        <label class="field">Hot pressure min (${pressureUnit()})<input type="number" step="0.1" id="pmin" value="${toUnit(t.hotPressureMin).toFixed(1)}"></label>
        <label class="field">Hot pressure max (${pressureUnit()})<input type="number" step="0.1" id="pmax" value="${toUnit(t.hotPressureMax).toFixed(1)}"></label>
        <label class="field">Temp min (°C)<input type="number" id="tmin" value="${t.tempMin}"></label>
        <label class="field">Temp max (°C)<input type="number" id="tmax" value="${t.tempMax}"></label>
        <label class="field">Ideal inner−outer spread (°C)<input type="number" id="spread" value="${t.idealSpread}"></label>
      </div>
      <div class="row end" style="margin-top:10px"><span class="small muted">Defaults are generic starting points per car type — tune them to what's fast for you.</span><button id="save-target">Save target</button></div></div>
    <div class="card" style="margin-top:14px"><div class="card-head"><h2 class="grow">Per lap</h2></div><div class="table-wrap" id="laps"></div></div>`;

  $('#tg', root).innerHTML = d.advice.map(a => `
    <div class="tyre"><div class="row" style="justify-content:space-between"><b>${a.tyre}</b>
      <span class="num ${a.pressStatus === 'low' ? 'blue' : a.pressStatus === 'high' ? 'bad' : 'good'}">${pressure(a.pressHot)} ${pressureUnit()} hot</span></div>
      <div class="temps">${(a.tyre.startsWith('L') ? [a.tempOut, a.tempMid, a.tempIn] : [a.tempIn, a.tempMid, a.tempOut])
        .map(v => `<div style="background:${!isNum(v) ? 'var(--panel3)' : v < t.tempMin ? '#4DA3FF' : v > t.tempMax ? '#FF4D5E' : '#22D37E'}">${temp(v)}</div>`).join('')}</div>
      <div class="tiny muted" style="margin-bottom:6px">${a.tyre.startsWith('L') ? 'outer · middle · inner' : 'inner · middle · outer'}</div>
      <div class="kv small"><span class="k">Cold set</span><span class="num">${pressure(a.pressCold)}</span>
      <span class="k">Suggested cold</span><span class="num ${isNum(a.suggestedCold) ? 'warn' : ''}">${isNum(a.suggestedCold) ? pressure(a.suggestedCold) : 'keep'}</span>
      <span class="k">Avg temp</span><span class="num">${temp(a.tempAvg)}</span>
      <span class="k">Inner − outer</span><span class="num">${isNum(a.spread) ? a.spread.toFixed(1) + '°' : '–'}</span>
      <span class="k">Wear left</span><span class="num">${isNum(a.wear) ? (a.wear * 100).toFixed(1) + '%' : '–'}</span></div></div>`).join('');
  const notes = d.advice.flatMap(a => a.notes.map(n => ({ t: a.tyre, n })));
  $('#advice', root).innerHTML = notes.length ? notes.map(x => `<div class="insight s2"><div class="cat">${x.t}</div><div class="det" style="color:var(--text)">${esc(x.n)}</div></div>`).join('')
    : '<div class="insight s0"><div class="cat">tyres</div><div class="ttl">All four tyres are in the window. Nothing to change.</div></div>';

  $('#apply', root).onclick = async () => {
    const kpa = d.advice.map(a => isNum(a.suggestedCold) ? a.suggestedCold : a.pressCold);
    if (kpa.some(v => !isNum(v))) return toast('No cold pressures known for this session.', true);
    const r = await api('/api/tyres/apply', { body: kpa });
    toast(r.ok ? 'Sent to iRacing pit service.' : 'Could not send — are you in a live iRacing session?', !r.ok);
  };
  $('#save-target', root).onclick = async () => {
    const target = { hotPressureMin: fromUnit(+$('#pmin', root).value), hotPressureMax: fromUnit(+$('#pmax', root).value), tempMin: +$('#tmin', root).value, tempMax: +$('#tmax', root).value, idealSpread: +$('#spread', root).value };
    await api('/api/tyres/target', { body: { carPath: d.carPath, target } });
    toast('Target saved');
    $('#sess', root).onchange();
  };

  $('#laps', root).innerHTML = `<table><tr><th>Lap</th><th class="num">Stint</th><th class="num">Time</th>${['LF', 'RF', 'LR', 'RR'].map(n => `<th class="num">${n} ${pressureUnit()}</th><th class="num">${n} °C</th>`).join('')}<th class="num">Track</th></tr>
    ${d.laps.map(l => `<tr class="${l.valid ? '' : 'invalid'}"><td>${l.lapNumber}</td><td class="num">${l.stint}</td><td class="num">${lapTime(l.lapTime)}</td>
      ${l.tyres.map(ty => `<td class="num">${pressure(ty.pressAvg)}</td><td class="num">${isNum(ty.tempMidAvg) ? ((ty.tempInAvg + ty.tempMidAvg + ty.tempOutAvg) / 3).toFixed(0) : '–'}</td>`).join('')}
      <td class="num">${temp(l.trackTemp)}</td></tr>`).join('')}</table>`;
}
