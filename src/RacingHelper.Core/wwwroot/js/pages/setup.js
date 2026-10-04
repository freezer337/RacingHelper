import { api, lapTime, delta, deltaClass, esc, isNum, dateTime, date, toast, bytes, $, $$ } from '../core.js';

export async function render(root, params) {
  root.innerHTML = `
    <div class="page-head"><div class="grow"><h1>Setup</h1><p>Describe what the car does — or let your telemetry tell us — and get specific changes against your current setup.</p></div></div>
    <div class="tabs" id="tabs">
      <button data-t="optimiser" class="on">Optimiser</button><button data-t="auto">Auto-detect from a session</button><button data-t="journal">Setup journal</button><button data-t="installer">Setup installer</button>
    </div>
    <div id="tab"></div>`;
  const tabs = { optimiser, auto, journal, installer };
  const show = t => { $$('#tabs button', root).forEach(b => b.classList.toggle('on', b.dataset.t === t)); tabs[t]($('#tab', root), params); };
  for (const b of $$('#tabs button', root)) b.onclick = () => show(b.dataset.t);
  show(params.session ? 'auto' : params.tab || 'optimiser');
}

// ---------------------------------------------------------------- optimiser (questions)

async function optimiser(el) {
  const symptoms = await api('/api/setup/symptoms');
  const req = { symptom: 'understeer', phase: 'mid', speed: 'all', severity: 2, preference: 'neutral', category: 'auto' };
  const phased = ['understeer', 'oversteer'];
  el.innerHTML = `
    <div class="grid g2">
      <div class="card">
        <h3>1 · What is the car doing?</h3><div class="chips" id="sym" style="margin:10px 0 18px"></div>
        <div id="phase-q"><h3>2 · Where in the corner?</h3><div class="chips" id="phase" style="margin:10px 0 18px">
          ${[['entry', 'Entry / braking'], ['mid', 'Mid-corner'], ['exit', 'Exit / on throttle'], ['all', 'Everywhere']].map(([v, l]) => `<span class="chip" data-v="${v}">${l}</span>`).join('')}</div>
        <h3>3 · Which corners?</h3><div class="chips" id="speed" style="margin:10px 0 18px">
          ${[['slow', 'Slow corners'], ['fast', 'Fast corners'], ['all', 'All corners']].map(([v, l]) => `<span class="chip" data-v="${v}">${l}</span>`).join('')}</div></div>
        <h3>How bad is it?</h3><div class="chips" id="sev" style="margin:10px 0 18px">${[[1, 'Slight'], [2, 'Noticeable'], [3, 'Severe']].map(([v, l]) => `<span class="chip" data-v="${v}">${l}</span>`).join('')}</div>
        <h3>Your preference</h3><div class="chips" id="pref" style="margin:10px 0 6px">${[['stable', 'Stable, predictable'], ['neutral', 'Balanced'], ['pointy', 'Pointy, rotates easily']].map(([v, l]) => `<span class="chip" data-v="${v}">${l}</span>`).join('')}</div>
      </div>
      <div class="card"><div class="card-head"><h2 class="grow">Recommended changes</h2><span class="small muted" id="src"></span></div><div id="out"></div></div>
    </div>`;
  $('#sym', el).innerHTML = symptoms.map(s => `<span class="chip" data-v="${s.id}">${esc(s.label)}</span>`).join('');
  const bind = (id, key, num) => {
    for (const c of $$(`#${id} .chip`, el)) c.onclick = () => { req[key] = num ? +c.dataset.v : c.dataset.v; sync(); run(); };
  };
  const sync = () => {
    const mark = (id, v) => $$(`#${id} .chip`, el).forEach(c => c.classList.toggle('on', c.dataset.v == v));
    mark('sym', req.symptom); mark('phase', req.phase); mark('speed', req.speed); mark('sev', req.severity); mark('pref', req.preference);
    $('#phase-q', el).style.display = phased.includes(req.symptom) ? '' : 'none';
  };
  const run = async () => {
    const r = await api('/api/setup/advise', { body: req });
    $('#src', el).textContent = r.source;
    $('#out', el).innerHTML = adviceHtml(r.advice);
  };
  bind('sym', 'symptom'); bind('phase', 'phase'); bind('speed', 'speed'); bind('sev', 'severity', true); bind('pref', 'preference');
  sync(); run();
}

/** Suggested garage changes from /api/setup/auto (Setup, Live pace and Live pages). compact: top changes only. */
export function suggestionsHtml(r, compact = false) {
  if (!r?.items?.length) return r?.handling?.valid
    ? '<div class="small muted">No consistent handling problem: the time is in the driving now (see What to work on).</div>'
    : '<div class="small muted">Needs a few clean laps at the limit (3+).</div>';
  if (compact) return `<ol style="margin:0;padding-left:20px;display:flex;flex-direction:column;gap:8px">${r.items.slice(0, 3).map(it => {
      const c = it.advice.changes[0];
      return `<li><div class="small muted">${esc(it.advice.title)}${it.request.phase ? ' · ' + esc(it.request.phase) : ''}</div>${c ? `<div><b>${esc(c.parameter)}</b> — ${esc(c.action)}</div>` : ''}</li>`;
    }).join('')}</ol>`;
  return r.items.map(it => `<h3 style="margin:6px 0 10px">${esc(it.advice.title)} · ${esc(it.request.phase)} · ${esc(it.request.speed)}</h3>${adviceHtml({ ...it.advice, changes: it.advice.changes.slice(0, 4), drivingTips: it.advice.drivingTips.slice(0, 1), notes: [] })}`).join('<hr style="border:none;border-top:1px solid var(--line);margin:14px 0">');
}

function adviceHtml(a) {
  return `
    <ol style="margin:0;padding-left:20px;display:flex;flex-direction:column;gap:10px">
      ${a.changes.slice(0, 8).map((c, i) => `<li ${i > 3 ? 'class="muted"' : ''}>
        <div><b>${esc(c.parameter)}</b> — ${esc(c.action)} <span class="tag ${i < 3 ? 'green' : ''}">${i < 3 ? 'try first' : esc(c.area)}</span></div>
        <div class="small muted">${esc(c.why)} · ${esc(c.amount)}</div>
        ${c.current.length ? `<div class="tiny" style="margin-top:3px">Current: <span class="num">${c.current.map(esc).join(' · ')}</span></div>` : ''}
      </li>`).join('')}
    </ol>
    ${a.drivingTips.length ? `<h3 style="margin-top:18px">Driving technique first</h3><ul class="steps">${a.drivingTips.map(t => `<li>${esc(t)}</li>`).join('')}</ul>` : ''}
    <p class="small muted">${a.notes.map(esc).join(' ')}</p>`;
}

// ---------------------------------------------------------------- auto from telemetry

async function auto(el, params) {
  const sessions = (await api('/api/sessions')).filter(s => s.validLaps >= 3);
  el.innerHTML = `
    <div class="card" style="margin-bottom:14px"><div class="row"><select id="sess" style="min-width:360px">${sessions.map(s => `<option value="${s.id}">${dateTime(s.startedAt)} · ${esc(s.carName)} · ${esc(s.trackName)} · ${s.validLaps} clean laps</option>`).join('')}</select>
      <span class="small muted">Handling is detected from steering vs. yaw response at the limit, plus tyre temps, wheelspin and lock-ups.</span></div></div>
    <div id="res"></div>`;
  if (!sessions.length) { $('#res', el).innerHTML = '<div class="card empty">Needs a session with at least 3 clean laps.</div>'; return; }
  if (params.session) $('#sess', el).value = params.session;
  const load = async () => {
    $('#res', el).innerHTML = '<div class="empty"><span class="spinner"></span></div>';
    const r = await api(`/api/setup/auto?session=${$('#sess', el).value}`);
    const hd = r.handling;
    $('#res', el).innerHTML = `
      <div class="grid g2">
        <div class="card"><div class="card-head"><h2 class="grow">What the car is doing</h2></div>
          ${hd?.valid ? `<div class="matrix">
            <div></div><div class="h">Slow</div><div class="h">Medium</div><div class="h">Fast</div>
            ${['entry', 'mid', 'exit'].map(p => `<div class="h">${p}</div>${['slow', 'medium', 'fast'].map(b => {
              const c = hd.cells.find(x => x.phase === p && x.speedBand === b);
              if (!c || c.samples < 60) return `<div class="c dim small">little data${c?.samples ? ` (${(c.samples / 60).toFixed(0)} s)` : ''}</div>`;
              const bal = typeof c.balance === 'number' && isFinite(c.balance) ? c.balance : null;
              const how = bal == null ? '' : bal >= 1 ? `needs ${bal.toFixed(0)}% more lock` : bal <= -1 ? `rotates ${(-bal).toFixed(0)}% more` : 'as normal';
              return `<div class="c ${c.tendency}" title="At the limit: ${c.understeerRate.toFixed(0)}% of the time clearly understeering, ${c.oversteerRate.toFixed(0)}% clearly oversteering · ${(c.samples / 60).toFixed(0)} s of data"><b>${c.tendency}</b><div class="tiny muted">${how} · ${(c.samples / 60).toFixed(0)} s at the limit</div></div>`;
            }).join('')}`).join('')}</div>
            <ul class="steps" style="margin-top:12px">${hd.findings.map(f => `<li>${esc(f)}</li>`).join('')}</ul>
            ${hd.hotSpots.length ? `<div class="small muted">Hot spots: ${hd.hotSpots.map(h => `${esc(h.corner)} (${h.kind} on ${h.phase}, ${h.count} laps)`).join(', ')}</div>` : ''}`
          : '<div class="muted">Not enough laps at the limit to judge the balance.</div>'}
          <p class="small muted" style="margin:8px 0 0">How it's measured: how much steering the car needs at the limit compared with the same car at moderate cornering, in the same kind of corner and speed. Within ±10% is neutral.</p>
          <p class="small muted">Setup: ${esc(r.source)}</p>
        </div>
        <div class="card"><div class="card-head"><h2 class="grow">Suggested changes</h2></div>
          ${r.items.length ? suggestionsHtml(r)
          : '<div class="insight s0"><div class="ttl">No consistent handling problem detected. Work on driving consistency first — see the session debrief.</div></div>'}
        </div>
      </div>`;
  };
  $('#sess', el).onchange = load;
  load();
}

// ---------------------------------------------------------------- journal

async function journal(el) {
  const combos = await api('/api/combos');
  el.innerHTML = `
    <div class="card" style="margin-bottom:14px"><div class="row"><select id="combo" style="min-width:340px">${combos.map(c => `<option value="${esc(c.carPath)}|${esc(c.trackKey)}">${esc(c.carName)} · ${esc(c.trackName)}</option>`).join('')}</select>
    <span class="small muted">Every setup you've driven is fingerprinted automatically — see which one was fastest and what changed between them.</span></div></div>
    <div id="j"></div>`;
  if (!combos.length) { $('#j', el).innerHTML = '<div class="card empty">No sessions yet.</div>'; return; }
  const load = async () => {
    const [car, track] = $('#combo', el).value.split('|');
    const j = await api(`/api/setup/journal?car=${encodeURIComponent(car)}&track=${encodeURIComponent(track)}`);
    const best = Math.min(...j.setups.map(s => s.best));
    $('#j', el).innerHTML = `
      <div class="card"><table><tr><th>Setup</th><th>Used</th><th class="num">Clean laps</th><th class="num">Best</th><th class="num">Avg of top 5</th><th class="num">Track</th><th></th></tr>
        ${j.setups.map(s => `<tr><td><b>${esc(s.name || '(unnamed)')}</b> <span class="tiny dim num">${s.hash}</span></td><td class="small muted">${date(s.first)}${date(s.first) !== date(s.last) ? ' – ' + date(s.last) : ''}</td>
          <td class="num">${s.laps}</td><td class="num ${s.best === best ? 'purple' : ''}">${lapTime(s.best)}</td><td class="num">${lapTime(s.avgTop5)}</td>
          <td class="num">${isNum(s.trackTemp) ? s.trackTemp.toFixed(0) + '°' : '–'}</td><td>${s.wet ? '<span class="tag blue">wet</span>' : ''}</td></tr>`).join('')}</table></div>
      ${j.diffs.length ? `<h3 style="margin:18px 0 8px">What changed</h3>${j.diffs.slice().reverse().map(d => {
        const a = j.setups.find(s => s.hash === d.from), b = j.setups.find(s => s.hash === d.to);
        const dt = b && a ? b.avgTop5 - a.avgTop5 : NaN;
        return `<div class="card" style="margin-bottom:10px"><div class="card-head"><b class="grow">${esc(a?.name || d.from)} → ${esc(b?.name || d.to)}</b><span class="num ${deltaClass(dt)}">${isNum(dt) ? delta(dt) + ' avg top-5' : ''}</span></div>
          <table>${d.changes.slice(0, 40).map(c => `<tr><td class="small">${esc(c.key)}</td><td class="num small muted">${esc(c.a)}</td><td class="small">→</td><td class="num small">${esc(c.b)}</td></tr>`).join('')}</table></div>`;
      }).join('')}` : ''}`;
  };
  $('#combo', el).onchange = load;
  load();
}

// ---------------------------------------------------------------- installer

async function installer(el) {
  const s = await api('/api/settings');
  const draw = async () => {
    const lib = await api('/api/setups/library');
    const byCar = new Map();
    for (const f of lib.files) { if (!byCar.has(f.carPath)) byCar.set(f.carPath, []); byCar.get(f.carPath).push(f); }
    el.innerHTML = `
      <div class="grid g2">
        <div class="card"><div class="card-head"><h2 class="grow">Your setup library</h2><button class="small" data-open="library">Open folder</button></div>
          <p class="small muted" style="margin-top:0">Put setups in <span class="num">${esc(lib.folder)}</span>, one folder per iRacing car folder name (e.g. <span class="num">superformulalights324</span>). Sub-folders are kept. When you join a session in that car they're copied into <span class="num">iRacing\\setups\\&lt;car&gt;\\RacingHelper</span>, ready in the garage.</p>
          <div class="row"><button class="primary" id="install-all">Install all now</button><button data-open="iracing-setups">Open iRacing setups</button></div>
        </div>
        <div class="card"><div class="card-head"><h2 class="grow">Auto-install</h2></div>
          <label class="check"><input type="checkbox" id="auto" ${s.autoInstallSetups ? 'checked' : ''}> Install automatically when I join a session</label>
          <div class="form-grid" style="margin-top:12px">
            <label class="field">Setup type<select id="type">${['all', 'race', 'quali', 'wet'].map(t => `<option ${s.setupTypeFilter === t ? 'selected' : ''}>${t}</option>`).join('')}</select></label>
            <label class="field">Versions<select id="latest"><option value="1" ${s.setupLatestOnly ? 'selected' : ''}>Latest only</option><option value="0" ${!s.setupLatestOnly ? 'selected' : ''}>All versions</option></select></label>
            <label class="field">Track<select id="track"><option value="1" ${s.setupMatchTrack ? 'selected' : ''}>Matching track only</option><option value="0" ${!s.setupMatchTrack ? 'selected' : ''}>All tracks</option></select></label>
          </div>
          <p class="small muted">Type is read from file / folder names (race, quali, wet). Tyre pressures inside iRacing .sto files can't be edited — use the Tyre tool to set pit pressures instead.</p>
        </div>
      </div>
      <div class="card" style="margin-top:14px"><div class="card-head"><h2 class="grow">Library</h2><span class="small muted">${lib.files.length} setups</span></div>
        ${lib.files.length ? [...byCar].map(([car, files]) => `<h3 style="margin:12px 0 6px">${esc(car)}</h3><table>${files.map(f => `<tr><td>${esc(f.relativePath)}</td><td><span class="tag ${f.type === 'race' ? 'green' : f.type === 'quali' ? 'purple' : f.type === 'wet' ? 'blue' : ''}">${f.type}</span></td><td class="small muted">${date(f.modified)}</td><td class="num small">${bytes(f.size)}</td><td>${f.installed ? '<span class="tag green">installed</span>' : ''}</td></tr>`).join('')}</table>`).join('')
        : '<div class="muted small">Library is empty.</div>'}
      </div>`;
    for (const b of $$('[data-open]', el)) b.onclick = () => api('/api/open-folder', { body: { which: b.dataset.open } });
    $('#install-all', el).onclick = async () => {
      const r = await api('/api/setups/install', { body: { carPath: '', ignoreFilters: 'true' } });
      const n = r.reduce((a, x) => a + x.installed.length, 0);
      toast(n ? `Installed ${n} setup file(s)` : 'Everything is already installed');
      draw();
    };
    const save = async () => {
      const cur = await api('/api/settings');
      await api('/api/settings', { body: { ...cur, autoInstallSetups: $('#auto', el).checked, setupTypeFilter: $('#type', el).value, setupLatestOnly: $('#latest', el).value === '1', setupMatchTrack: $('#track', el).value === '1' } });
      toast('Saved');
    };
    for (const id of ['auto', 'type', 'latest', 'track']) $('#' + id, el).onchange = save;
  };
  draw();
}
