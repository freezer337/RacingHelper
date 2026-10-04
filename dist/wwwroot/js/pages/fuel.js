import { api, onLive, lapTime, fixed, esc, isNum, toast, $, $$ } from '../core.js';

let off = null;
export function destroy() { off?.(); off = null; }

export async function render(root) {
  const combos = await api('/api/combos').catch(() => []);
  root.innerHTML = `
    <div class="page-head"><div class="grow"><h1>Fuel calculator</h1><p>Live strategy while you drive, and a race planner that fills itself in from your own laps.</p></div></div>
    <div class="grid g2">
      <div class="card"><div class="card-head"><h2 class="grow">Live</h2><span class="small muted" id="live-sub"></span></div><div id="live"><div class="muted small">Shown while you're in a session.</div></div></div>
      <div class="card">
        <div class="card-head"><h2 class="grow">Race planner</h2></div>
        <div class="row" style="margin-bottom:12px"><select id="combo" style="flex:1"><option value="">Fill from history…</option>${combos.map(c => `<option value="${esc(c.carPath)}|${esc(c.trackKey)}">${esc(c.carName)} · ${esc(c.trackName)}</option>`).join('')}</select><button id="fill">Auto-fill</button></div>
        <div class="form-grid">
          <label class="field">Race length<div class="row" style="flex-wrap:nowrap"><input type="number" id="len" value="30" style="width:90px"><select id="unit"><option value="min">minutes</option><option value="laps">laps</option></select></div></label>
          <label class="field">Lap time (m:ss.s)<input id="lap" value="1:30.0"></label>
          <label class="field">Fuel per lap (L)<input type="number" step="0.01" id="fpl" value="2.5"></label>
          <label class="field">Tank (L)<input type="number" step="0.1" id="tank" value="100"></label>
          <label class="field">Margin (laps)<input type="number" step="0.5" id="margin" value="1"></label>
          <label class="field">Pit stop loss (s)<input type="number" id="loss" value="35"></label>
        </div>
        <div id="plan" style="margin-top:16px"></div>
      </div>
    </div>`;

  const parseLap = s => { const m = String(s).split(':'); return m.length > 1 ? (+m[0]) * 60 + parseFloat(m[1]) : parseFloat(s); };
  const calc = async () => {
    const minutes = $('#unit', root).value === 'min';
    const input = {
      raceMinutes: minutes ? +$('#len', root).value : 0, raceLaps: minutes ? 0 : +$('#len', root).value,
      lapTimeSec: parseLap($('#lap', root).value), fuelPerLap: +$('#fpl', root).value, tankLitres: +$('#tank', root).value,
      marginLaps: +$('#margin', root).value, pitLossSec: +$('#loss', root).value, startFuel: -1,
    };
    const p = await api('/api/fuel/plan', { body: input });
    $('#plan', root).innerHTML = p.totalLaps ? `
      <div class="stats">
        <div class="stat"><span class="label">Total fuel</span><span class="value">${fixed(p.totalFuel, 1)} L</span></div>
        <div class="stat"><span class="label">Race laps</span><span class="value">${fixed(p.totalLaps, 0)}</span></div>
        <div class="stat"><span class="label">Pit stops</span><span class="value">${p.pitStops}</span></div>
        <div class="stat"><span class="label">Laps per tank</span><span class="value">${fixed(p.lapsPerTank, 1)}</span></div>
      </div>
      <p>${esc(p.summary)}</p>
      <table><tr><th>Stint</th><th class="num">Fuel</th></tr>${p.stintFuel.map((f, i) => `<tr><td>${i === 0 ? 'Start' : 'Stop ' + i}</td><td class="num">${fixed(f, 1)} L</td></tr>`).join('')}</table>`
      : `<p class="muted">${esc(p.summary)}</p>`;
  };
  for (const id of ['len', 'unit', 'lap', 'fpl', 'tank', 'margin', 'loss']) $('#' + id, root).oninput = calc;
  const fill = async (car, track) => {
    const q = car ? `?car=${encodeURIComponent(car)}&track=${encodeURIComponent(track)}` : '';
    const s = await api('/api/fuel/suggest' + q);
    if (isNum(s.fuelPerLap)) $('#fpl', root).value = s.fuelPerLap.toFixed(2);
    if (isNum(s.lapTime)) $('#lap', root).value = lapTime(s.lapTime);
    if (isNum(s.tank) && s.tank > 0) $('#tank', root).value = s.tank.toFixed(1);
    toast(isNum(s.fuelPerLap) ? `Filled from ${s.samples} of your laps` : 'No fuel history for this combo yet');
    calc();
  };
  $('#fill', root).onclick = () => { const v = $('#combo', root).value; if (!v) return toast('Pick a car & track'); const [c, t] = v.split('|'); fill(c, t); };
  calc();

  let autofilled = false;
  off = onLive(s => {
    const el = $('#live', root); if (!el) return;
    if (s.status === 'waiting' || !s.fuel) { el.innerHTML = '<div class="muted small">Shown while you\'re in a session.</div>'; return; }
    if (!autofilled && s.carPath) { autofilled = true; fill(); }
    const f = s.fuel;
    $('#live-sub', root).textContent = `${s.car} · lap ${s.lap}`;
    el.innerHTML = `
      <div class="stats">
        <div class="stat"><span class="label">In tank</span><span class="value">${fixed(f.fuelLevel, 1)} L</span><span class="sub">of ${fixed(s.tankCapacity, 0)} L</span></div>
        <div class="stat"><span class="label">Predicted / lap</span><span class="value">${fixed(f.perLapPredicted, 2)}</span><span class="sub">last ${fixed(f.perLapLast, 2)} · 5-lap ${fixed(f.perLapAvg, 2)} · max ${fixed(f.perLapMax, 2)}</span></div>
        <div class="stat"><span class="label">Laps in tank</span><span class="value ${f.lapsInTank < 2 ? 'bad' : ''}">${fixed(f.lapsInTank, 1)}</span></div>
      </div>
      ${isNum(f.fuelToFinish) ? `<div class="stats" style="margin-top:14px">
        <div class="stat"><span class="label">Laps to go</span><span class="value">${fixed(f.lapsRemaining, 1)}</span></div>
        <div class="stat"><span class="label">Fuel to finish</span><span class="value">${fixed(f.fuelToFinish, 1)} L</span></div>
        <div class="stat"><span class="label">${f.fuelToAdd > 0.1 ? 'Add at stop' : 'Spare at flag'}</span><span class="value ${f.fuelToAdd > 0.1 ? 'warn' : 'good'}">${f.fuelToAdd > 0.1 ? Math.ceil(f.fuelToAdd) + ' L' : fixed(f.spare, 1) + ' L'}</span><span class="sub">${f.stopsRemaining ? f.stopsRemaining + ' stop(s)' : ''}</span></div>
      </div>
      <div class="row" style="margin-top:12px"><button id="setfuel" ${f.fuelToAdd > 0.1 ? '' : 'disabled'}>Set pit fuel to ${Math.ceil(f.fuelToAdd || 0)} L</button></div>` : '<p class="muted small">No race length in this session (practice) — fuel-to-finish appears in timed / lap races.</p>'}`;
    const b = $('#setfuel', el);
    if (b) b.onclick = async () => { const r = await api('/api/pit/fuel', { body: { litres: Math.ceil(f.fuelToAdd) } }); toast(r.ok ? 'Pit fuel set' : 'Not in a live session', !r.ok); };
  });
}
