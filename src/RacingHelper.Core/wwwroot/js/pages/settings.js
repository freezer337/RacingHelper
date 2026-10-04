import { api, esc, toast, loadSettings, $, $$ } from '../core.js';

export async function render(root) {
  const [s, voices, status, cc, controls] = await Promise.all([api('/api/settings'), api('/api/voices').catch(() => []), api('/api/status'), api('/api/crewchief').catch(() => null), api('/api/controls').catch(() => null)]);
  const sel = (id, opts, val) => `<select id="${id}">${opts.map(([v, l]) => `<option value="${v}" ${String(val) === String(v) ? 'selected' : ''}>${l}</option>`).join('')}</select>`;
  const chk = (id, val, label) => `<label class="check"><input type="checkbox" id="${id}" ${val ? 'checked' : ''}> ${label}</label>`;
  root.innerHTML = `
    <div class="page-head"><div class="grow"><h1>Settings</h1><p>Everything is stored locally in <span class="num">${esc(status.dataFolder)}</span>.</p></div><button class="primary" id="save">Save</button></div>
    <div class="grid g2">
      <div class="card"><h2 style="margin-bottom:12px">Race engineer</h2>
        <div class="grid" style="gap:12px">
          ${chk('voiceEnabled', s.voiceEnabled, 'Speak messages')}
          <label class="field">How chatty${sel('voiceVerbosity', [['minimal', 'Minimal — flags, fuel, damage, PBs'], ['normal', 'Normal — plus lap times & biggest loss'], ['detailed', 'Detailed — plus gains and conditions']], s.voiceVerbosity)}</label>
          ${chk('quietInCorners', s.quietInCorners, 'Only talk on straights (hold messages until there is room to finish them before the next corner)')}
          ${chk('cornerCallouts', s.cornerCallouts, 'Call out each corner where I lose time (immediately after the corner)')}
          <label class="field">Who speaks${sel('voiceOutput', [['auto', 'CrewChief when it is connected, otherwise Windows voice'], ['crewchief', 'Only CrewChief (silent when CrewChief is not running)'], ['windows', 'Always Windows voice']], s.voiceOutput)}</label>
          <label class="field">Windows voice${sel('voiceName', [['', 'Windows default'], ...voices.map(v => [v, v])], s.voiceName)}</label>
          <div class="form-grid"><label class="field">Rate<input type="range" id="voiceRate" min="-5" max="6" value="${s.voiceRate}"></label><label class="field">Volume<input type="range" id="voiceVolume" min="0" max="100" value="${s.voiceVolume}"></label></div>
          <div><button id="test" class="small">Radio check</button></div>
        </div>
      </div>
      <div class="card"><h2 style="margin-bottom:12px">Managing the car</h2>
        <div class="grid" style="gap:12px">
          ${chk('tyreManager', s.tyreManager, 'Tyre management: tell me when tyres are cold, overheating (cool them) and when I can push again')}
          <label class="field">Corner coaching <span class="dim">(a short tip before a corner where you keep losing time)</span>${sel('coachingMode', [['practice', 'In practice only'], ['always', 'Practice, qualifying and race'], ['off', 'Off']], s.coachingMode)}</label>
          ${chk('hotspotWarnings', s.hotspotWarnings, "Warn me before a corner where I've gone off twice this session")}
          ${chk('liveSetupAdvice', s.liveSetupAdvice, 'Practice: start a guided setup session automatically (drive a run, change one thing, compare)')}
          <label class="field">Clean laps per setup run<input type="number" min="3" max="10" id="setupRunLaps" value="${s.setupRunLaps}"></label>
          <p class="small muted" style="margin:0">Every pit stop: the crew's tyre temperature readings are turned into camber and pressure advice. Qualifying: you're told when there's time for another lap.</p>
        </div>
      </div>
      <div class="card"><h2 style="margin-bottom:12px">CrewChief</h2>
        <div class="grid" style="gap:12px">
          <div id="cc-status">${ccStatus(cc)}</div>
          ${chk('crewChiefEnabled', s.crewChiefEnabled, 'Let CrewChief V4 speak for Racing Helper')}
          ${chk('crewChiefSkipDuplicates', s.crewChiefSkipDuplicates, "Don't repeat what CrewChief already says itself (flags, fuel, lap times, incidents)")}
          <label class="field">Connection port <span class="dim">(change only if something else uses 1883)</span><input type="number" id="crewChiefPort" value="${s.crewChiefPort}"></label>
          <div class="row"><button id="cc-setup" class="small">Set up CrewChief</button><button id="cc-test" class="small">Radio check through CrewChief</button></div>
          <ol class="steps small" style="margin:0">
            <li>Click <b>Set up CrewChief</b>: it points CrewChief at Racing Helper (writes <span class="num">Documents\\CrewChiefV4\\mqtt_telemetry.json</span>, the original is backed up).</li>
            <li>In CrewChief → <b>Properties</b>: tick <b>MQTT Telemetry enabled</b>, type any <b>MQTT drivername</b>, and leave text-to-speech on (not "Never").</li>
            <li>Save and restart CrewChief. The status above turns green. Racing Helper's messages then come over CrewChief's radio in a TTS voice.</li>
          </ol>
        </div>
      </div>
      <div class="card span2"><h2 style="margin-bottom:6px">Wheel buttons & questions</h2>
        <p class="small muted" style="margin:0 0 12px">Ask the engineer while you drive. Click <b>Bind</b>, then press a button on your wheel or button box (Moza, Fanatec, Simucube… any controller Windows sees). The answer comes straight away, even mid-corner, because you asked for it.${controls?.controllers?.length ? ` Controllers seen so far: ${controls.controllers.map(esc).join(', ')}.` : ''}</p>
        <div id="controls">${controlsHtml(controls)}</div>
      </div>
      <div class="card span2"><h2 style="margin-bottom:6px">In-car adjustments by the engineer</h2>
        <p class="small muted" style="margin:0 0 10px">iRacing only lets apps change pit service (fuel, tyres, pressures, fast repair), not the garage setup or the wing. What the app <i>can</i> do is press the keys you've bound in iRacing for in-car adjustments. The setup session then makes its brake bias / TC / ABS / in-car anti-roll bar suggestion for you when you press <b>Apply</b> (a wheel button or the Live page). It reads the value back after every press, so it stops on target.</p>
        ${chk('inCarAutomation', s.inCarAutomation, 'Let the engineer make in-car changes when I press Apply (only then, never on its own)')}
        <div class="form-grid" style="grid-template-columns:repeat(auto-fill,minmax(230px,1fr));margin-top:10px">
          ${INCAR.map(([id, label]) => `<label class="field">${label} up<input data-key="${id}+" placeholder="e.g. F13" value="${esc(s.inCarKeys?.[id + '+'] || '')}"></label><label class="field">${label} down<input data-key="${id}-" placeholder="e.g. F14" value="${esc(s.inCarKeys?.[id + '-'] || '')}"></label>`).join('')}
        </div>
        <p class="small muted" style="margin:10px 0 0">In iRacing: Options → Controls → bind a <b>keyboard key</b> to each one you use (e.g. "Traction control +"), then type the same key here: <span class="num">F13</span>, <span class="num">NumPad7</span>, <span class="num">T</span> or <span class="num">Ctrl+Shift+T</span>. Keys nobody types while driving (F13–F24, numpad) work best. iRacing has to be the active window.</p>
      </div>
      <div class="card"><h2 style="margin-bottom:12px">Delta & analysis</h2>
        <div class="grid" style="gap:12px">
          <label class="field">Default delta reference${sel('referenceMode', [['pb', 'Personal best (same conditions)'], ['session', 'Session best'], ['last', 'Last lap'], ['lap', 'Specific lap (chosen in Leaderboards)']], s.referenceMode)}</label>
          <label class="field">Delta sectors${sel('deltaSectors', [['iracing', "iRacing's sectors"], ['3', '3 equal'], ['6', '6 mini-sectors'], ['9', '9 mini-sectors'], ['12', '12 mini-sectors']], s.deltaSectors)}</label>
          <label class="field">Fuel safety margin (laps)<input type="number" step="0.5" id="fuelMarginLaps" value="${s.fuelMarginLaps}"></label>
          <label class="field">Your iRacing customer ID <span class="dim">(0 = detect automatically)</span><input type="number" id="myUserId" value="${s.myUserId}"></label>
        </div>
      </div>
      <div class="card"><h2 style="margin-bottom:12px">Units</h2>
        <div class="form-grid">
          <label class="field">Speed${sel('speedUnit', [['kmh', 'km/h'], ['mph', 'mph']], s.speedUnit)}</label>
          <label class="field">Pressure${sel('pressureUnit', [['kPa', 'kPa'], ['psi', 'psi'], ['bar', 'bar']], s.pressureUnit)}</label>
          <label class="field">Temperature${sel('tempUnit', [['C', '°C'], ['F', '°F']], s.tempUnit)}</label>
        </div>
      </div>
      <div class="card"><h2 style="margin-bottom:12px">Automation</h2>
        <div class="grid" style="gap:10px">
          ${chk('autoImportIbt', s.autoImportIbt, 'Import iRacing .ibt files automatically after each session')}
          ${chk('autoStartDiskTelemetry', s.autoStartDiskTelemetry, 'Turn on iRacing telemetry logging when I get in the car (needed for racing lines & tyre data)')}
          ${chk('autoInstallSetups', s.autoInstallSetups, 'Install setups from my library when I join a session')}
        </div>
      </div>
      <div class="card span2"><h2 style="margin-bottom:12px">Folders & dashboard</h2>
        <div class="form-grid" style="grid-template-columns:repeat(auto-fill,minmax(320px,1fr))">
          <label class="field">iRacing telemetry folder<input id="telemetryFolder" value="${esc(s.telemetryFolder)}"></label>
          <label class="field">iRacing setups folder<input id="iRacingSetupsFolder" value="${esc(s.iRacingSetupsFolder)}"></label>
          <label class="field">Setup library<input id="setupLibraryFolder" value="${esc(s.setupLibraryFolder)}"></label>
          <label class="field">Dashboard port <span class="dim">(restart to apply)</span><input type="number" id="webPort" value="${s.webPort}"></label>
        </div>
        <div style="margin-top:12px">${chk('allowLan', s.allowLan, 'Allow opening the dashboard from other devices on my network (e.g. a tablet next to the rig) — restart to apply')}</div>
      </div>
    </div>`;
  $('#test', root).onclick = async () => { await save(true); await api('/api/engineer/test', { body: {} }); };
  $('#cc-test', root).onclick = async () => {
    await save(true);
    const r = await api('/api/engineer/test', { body: {} });
    toast(r.via === 'crewchief' ? 'Sent to CrewChief' : 'CrewChief is not connected — spoke with the Windows voice');
  };
  $('#cc-setup', root).onclick = async () => {
    await save(true);
    const r = await api('/api/crewchief/configure', { body: {} });
    toast(r.message);
    refreshCc();
  };
  const ctl = $('#controls', root);
  ctl.onclick = async e => {
    const b = e.target.closest('button[data-act]');
    if (!b) return;
    const id = b.dataset.id;
    if (b.dataset.act === 'ask') { const r = await api('/api/ask/' + id, { body: {} }); toast(r.answer || 'Done'); }
    if (b.dataset.act === 'clear') await api('/api/controls/clear', { body: { action: id } });
    if (b.dataset.act === 'bind') {
      b.textContent = 'Press a button…'; b.disabled = true;
      const r = await api('/api/controls/learn', { body: { action: id } }).catch(() => ({ ok: false, error: 'Failed' }));
      toast(r.ok ? `Bound to ${r.binding.deviceName} button ${r.binding.button}` : r.error);
    }
    ctl.innerHTML = controlsHtml(await api('/api/controls').catch(() => null));
  };
  async function refreshCc() {
    const el = $('#cc-status', root);
    if (!el || !document.body.contains(el)) return;
    el.innerHTML = ccStatus(await api('/api/crewchief').catch(() => null));
  }
  const ccTimer = setInterval(() => { if (!document.body.contains(root.querySelector('#cc-status'))) clearInterval(ccTimer); else refreshCc(); }, 3000);
  $('#save', root).onclick = () => save();
  async function save(quiet) {
    const v = id => $('#' + id, root);
    const next = {
      ...s,
      voiceEnabled: v('voiceEnabled').checked, voiceVerbosity: v('voiceVerbosity').value, cornerCallouts: v('cornerCallouts').checked,
      voiceOutput: v('voiceOutput').value, tyreManager: v('tyreManager').checked, coachingMode: v('coachingMode').value, liveSetupAdvice: v('liveSetupAdvice').checked,
      inCarAutomation: v('inCarAutomation').checked,
      inCarKeys: Object.fromEntries([...root.querySelectorAll('input[data-key]')].map(i => [i.dataset.key, i.value.trim()]).filter(([, k]) => k)),
      quietInCorners: v('quietInCorners').checked, hotspotWarnings: v('hotspotWarnings').checked, setupRunLaps: Math.min(10, Math.max(3, +v('setupRunLaps').value || 5)),
      crewChiefEnabled: v('crewChiefEnabled').checked, crewChiefSkipDuplicates: v('crewChiefSkipDuplicates').checked, crewChiefPort: +v('crewChiefPort').value || 1883,
      voiceName: v('voiceName').value, voiceRate: +v('voiceRate').value, voiceVolume: +v('voiceVolume').value,
      referenceMode: v('referenceMode').value, deltaSectors: v('deltaSectors').value, fuelMarginLaps: +v('fuelMarginLaps').value, myUserId: +v('myUserId').value,
      speedUnit: v('speedUnit').value, pressureUnit: v('pressureUnit').value, tempUnit: v('tempUnit').value,
      autoImportIbt: v('autoImportIbt').checked, autoStartDiskTelemetry: v('autoStartDiskTelemetry').checked, autoInstallSetups: v('autoInstallSetups').checked,
      telemetryFolder: v('telemetryFolder').value, iRacingSetupsFolder: v('iRacingSetupsFolder').value, setupLibraryFolder: v('setupLibraryFolder').value,
      webPort: +v('webPort').value, allowLan: v('allowLan').checked,
    };
    await api('/api/settings', { body: next });
    Object.assign(s, next);
    await loadSettings();
    if (!quiet) toast('Settings saved');
  }
}

function ccStatus(cc) {
  if (!cc) return '<span class="muted small">Status unavailable.</span>';
  const dot = c => `<span style="display:inline-block;width:9px;height:9px;border-radius:50%;background:${c};margin-right:6px"></span>`;
  let line;
  if (!cc.enabled) line = dot('var(--panel3)') + 'Off';
  else if (cc.status === 'error') line = dot('var(--red)') + esc(cc.error);
  else if (cc.connected) line = dot('var(--accent)') + `Connected${cc.driverName ? ` as <b>${esc(cc.driverName)}</b>` : ''}${cc.receivingTelemetry ? ' · CrewChief is running a session' : ''}${cc.usingCrewChief ? ' · CrewChief is the voice' : ''}`;
  else line = dot('#F5B942') + `Waiting for CrewChief on port ${cc.port}`;
  const cfg = !cc.configFound ? "CrewChief's MQTT file wasn't found yet (start CrewChief once, then click Set up)."
    : cc.configured ? 'CrewChief is set up to use Racing Helper.'
    : `CrewChief currently points at ${esc(cc.configServer || '?')}:${cc.configPort || '?'} — click Set up CrewChief.`;
  return `<div>${line}</div><div class="small muted" style="margin-top:4px">${cfg}</div>`;
}

const INCAR = [['bb', 'Brake bias'], ['tc', 'Traction control'], ['abs', 'ABS'], ['arbf', 'Front anti-roll bar'], ['arbr', 'Rear anti-roll bar']];

function controlsHtml(c) {
  if (!c) return '<span class="muted small">Unavailable.</span>';
  const bound = id => c.bindings.find(b => b.action === id);
  return `<table><tr><th>Question</th><th>Wheel button</th><th>Keyboard</th><th></th></tr>
    ${c.questions.map(q => {
      const b = bound(q.id);
      return `<tr><td>${esc(q.label)}</td>
        <td>${b ? `<b>${esc(b.deviceName || b.device)}</b> · button ${b.button}` : '<span class="dim">—</span>'}</td>
        <td class="small">${q.hotkey ? `<kbd>${esc(q.hotkey).replaceAll('+', '</kbd>+<kbd>')}</kbd>` : ''}</td>
        <td><div class="row" style="justify-content:flex-end;gap:6px"><button class="small" data-act="bind" data-id="${q.id}">Bind</button>${b ? `<button class="small" data-act="clear" data-id="${q.id}">Clear</button>` : ''}<button class="small" data-act="ask" data-id="${q.id}">Ask now</button></div></td></tr>`;
    }).join('')}</table>
    ${c.quiet ? '<p class="small warn" style="margin:8px 0 0">Quiet mode is on: only important calls.</p>' : ''}`;
}
