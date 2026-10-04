import { api, esc, toast, loadSettings, $, $$ } from '../core.js';

export async function render(root) {
  const [s, status, cc, controls] = await Promise.all([api('/api/settings'), api('/api/status'), api('/api/crewchief').catch(() => null), api('/api/controls').catch(() => null)]);
  const sel = (id, opts, val) => `<select id="${id}">${opts.map(([v, l]) => `<option value="${v}" ${String(val) === String(v) ? 'selected' : ''}>${l}</option>`).join('')}</select>`;
  const chk = (id, val, label) => `<label class="check"><input type="checkbox" id="${id}" ${val ? 'checked' : ''}> ${label}</label>`;
  root.innerHTML = `
    <div class="page-head"><div class="grow"><h1>Settings</h1><p>Everything is stored locally in <span class="num">${esc(status.dataFolder)}</span>.</p></div><button class="primary" id="save">Save</button></div>
    <div class="grid g2">
      <div class="card"><h2 style="margin-bottom:12px">Race engineer</h2>
        <div class="grid" style="gap:12px">
          ${chk('voiceEnabled', s.voiceEnabled, 'Speak messages')}
          <label class="field">Radio mode <span class="dim">(switch it while driving with the "Radio mode" button or key below)</span>${sel('radioMode', [['auto', 'Automatic by session — practice: lots of info, qualifying: half silent, race: normal'], ['practice', 'Always practice — lap times, gains and losses, setup and tyre calls'], ['quali', 'Always qualifying — only tyre warm-up, "push now" and where you lose time'], ['race', 'Always race — the normal race radio'], ['minimal', 'Minimal — flags, fuel, damage, PBs']], s.radioMode)}</label>
          ${chk('quietInCorners', s.quietInCorners, 'Only talk on straights (hold messages until there is room to finish them before the next corner)')}
          ${chk('cornerCallouts', s.cornerCallouts, 'Call out each corner where I lose time (immediately after the corner)')}
          <label class="field">Who speaks${sel('voiceOutput', [['auto', 'CrewChief when it is connected, otherwise Windows voice'], ['crewchief', 'Only CrewChief (silent when CrewChief is not running)'], ['windows', 'Always Windows voice']], s.voiceOutput)}</label>
          <div class="form-grid"><label class="field">Rate<input type="range" id="voiceRate" min="-5" max="6" value="${s.voiceRate}"></label><label class="field">Volume<input type="range" id="voiceVolume" min="0" max="100" value="${s.voiceVolume}"></label></div>
          <div><button id="test" class="small">Radio check</button></div>
        </div>
      </div>
      <div class="card"><h2 style="margin-bottom:12px">Managing the car</h2>
        <div class="grid" style="gap:12px">
          ${chk('tyreManager', s.tyreManager, 'Tyre management: tell me when tyres are cold, overheating (cool them) and when I can push again')}
          <label class="field">Corner coaching <span class="dim">(a short tip before a corner where you keep losing time)</span>${sel('coachingMode', [['practice', 'In practice only'], ['always', 'Practice, qualifying and race'], ['off', 'Off']], s.coachingMode)}</label>
          ${chk('hotspotWarnings', s.hotspotWarnings, "Warn me before a corner where I've gone off twice this session")}
          ${chk('inCarAdvice', s.inCarAdvice, 'Tell me which in-car adjustments to make, all in one sentence ("increase TC by 1, and move brake bias back 0.5"). You make the change.')}
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
            <li>Save and restart CrewChief, then press <b>Start Application</b> in CrewChief. The status above turns green. Racing Helper's messages then come over CrewChief's radio in a TTS voice.</li>
            <li>CrewChief only talks while you're in a session (in the car, on track). In the menus and the garage the radio check, and everything else, comes on the Windows voice instead, so nothing gets lost.</li>
          </ol>
        </div>
      </div>
      <div class="card span2"><h2 style="margin-bottom:6px">Buttons & keys</h2>
        <p class="small muted" style="margin:0 0 12px">Ask the engineer while you drive. Click <b>Bind</b>, then press a button on your wheel or button box (Moza, Fanatec, Simucube… any controller Windows sees). The answer comes straight away, even mid-corner, because you asked for it.${controls?.controllers?.length ? ` Controllers seen so far: ${controls.controllers.map(esc).join(', ')}.` : ''}</p>
        <div id="controls">${controlsHtml(controls)}</div>
      </div>
      <div class="card"><h2 style="margin-bottom:6px">Automatic pit service</h2>
        <p class="small muted" style="margin:0 0 12px">When you enter pit road, Racing Helper fills in iRacing's pit menu for you, using iRacing's own pit commands, then tells you what it set. The garage setup (wing, springs…) can't be changed at a stop.</p>
        <div class="grid" style="gap:10px">
          <label class="field">Use it${sel('autoPit', [['race', 'In races'], ['always', 'In every session'], ['off', 'Off']], s.autoPit)}</label>
          ${chk('autoPitFuel', s.autoPitFuel, 'Fuel: exactly what I need to finish (plus the safety margin)')}
          <label class="field">Tyres${sel('autoPitTyres', [['auto', 'Change them if enough laps are left'], ['always', 'Always change'], ['never', 'Never change'], ['manual', "Don't touch the tyre boxes"]], s.autoPitTyres)}</label>
          <label class="field">"Enough laps left" means at least<input type="number" min="1" max="50" id="autoPitTyreMinLaps" value="${s.autoPitTyreMinLaps}"></label>
          ${chk('autoPitPressures', s.autoPitPressures, 'New tyres get the cold pressures my last run here says I need')}
          ${chk('autoPitFastRepair', s.autoPitFastRepair, 'Fast repair when there is damage')}
          ${chk('autoPitWindscreen', s.autoPitWindscreen, 'Windscreen tear-off')}
        </div>
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
        <div style="margin-top:12px">${chk('allowLan', s.allowLan, 'Allow opening the dashboard from other devices on my network (e.g. a phone or tablet next to the rig) — restart Racing Helper to apply')}</div>
        <div class="small" style="margin-top:8px">${status.lanUrls?.length
          ? `On your phone or tablet (same Wi-Fi as this PC), open: ${status.lanUrls.map(u => `<b class="num">${esc(u)}#/pace</b>`).join(' or ')}. If it doesn't load, allow Racing Helper through Windows Firewall for private networks.`
          : s.allowLan ? '<span class="muted">Restart Racing Helper (tray icon → Exit, then start it again) and the address for your phone appears here.</span>'
          : '<span class="muted">Turn this on, save, and restart Racing Helper: the address to type on your phone appears here.</span>'}</div>
      </div>
    </div>`;
  const radioCheck = async () => {
    await save(true);
    const r = await api('/api/engineer/test', { body: {} });
    toast(r.via === 'crewchief' ? 'Sent to CrewChief: ' + r.text
      : r.crewChiefConnected ? 'Windows voice: CrewChief is connected but only talks once you are in a session on track. Press it again from the car (or use your radio check button).'
      : 'Windows voice: CrewChief is not connected.');
  };
  $('#test', root).onclick = radioCheck;
  $('#cc-test', root).onclick = radioCheck;
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
    if (b.dataset.act === 'keyclear') await api('/api/controls/key', { body: { action: id, keys: '' } });
    if (b.dataset.act === 'keysreset') await api('/api/controls/keys-reset', { body: {} });
    if (b.dataset.act === 'key') {
      b.textContent = 'Press keys… (Esc cancels)'; b.classList.add('primary');
      const keys = await captureKeys();
      if (keys) {
        const r = await api('/api/controls/key', { body: { action: id, keys } }).catch(() => ({ ok: false, error: 'Failed' }));
        toast(r.ok ? `${keys} set` : r.error);
      }
    }
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
      voiceEnabled: v('voiceEnabled').checked, radioMode: v('radioMode').value, cornerCallouts: v('cornerCallouts').checked,
      voiceOutput: v('voiceOutput').value, tyreManager: v('tyreManager').checked, coachingMode: v('coachingMode').value, liveSetupAdvice: v('liveSetupAdvice').checked,
      quietInCorners: v('quietInCorners').checked, inCarAdvice: v('inCarAdvice').checked,
      autoPit: v('autoPit').value, autoPitFuel: v('autoPitFuel').checked, autoPitTyres: v('autoPitTyres').value, autoPitTyreMinLaps: Math.max(1, +v('autoPitTyreMinLaps').value || 6),
      autoPitPressures: v('autoPitPressures').checked, autoPitFastRepair: v('autoPitFastRepair').checked, autoPitWindscreen: v('autoPitWindscreen').checked, hotspotWarnings: v('hotspotWarnings').checked, setupRunLaps: Math.min(10, Math.max(3, +v('setupRunLaps').value || 5)),
      crewChiefEnabled: v('crewChiefEnabled').checked, crewChiefSkipDuplicates: v('crewChiefSkipDuplicates').checked, crewChiefPort: +v('crewChiefPort').value || 1883,
      voiceRate: +v('voiceRate').value, voiceVolume: +v('voiceVolume').value,
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
  else if (cc.connected && cc.live) line = dot('var(--accent)') + `Connected${cc.driverName ? ` as <b>${esc(cc.driverName)}</b>` : ''} · in a session${cc.usingCrewChief ? ' · CrewChief is the voice' : ''}`;
  else if (cc.connected) line = dot('var(--accent)') + `Connected${cc.driverName ? ` as <b>${esc(cc.driverName)}</b>` : ''} · <span class="muted">CrewChief takes over once you're in a session on track (until then: Windows voice)</span>`;
  else line = dot('#F5B942') + `Waiting for CrewChief on port ${cc.port}`;
  const cfg = !cc.configFound ? "CrewChief's MQTT file wasn't found yet (start CrewChief once, then click Set up)."
    : cc.configured ? 'CrewChief is set up to use Racing Helper.'
    : `CrewChief currently points at ${esc(cc.configServer || '?')}:${cc.configPort || '?'} — click Set up CrewChief.`;
  return `<div>${line}</div><div class="small muted" style="margin-top:4px">${cfg}</div>`;
}

// Waits for one key combination in the dashboard and returns it like "Ctrl+Shift+F5" (null on Esc / timeout).
function captureKeys() {
  return new Promise(resolve => {
    const done = v => { window.removeEventListener('keydown', onKey, true); clearTimeout(t); resolve(v); };
    const t = setTimeout(() => done(null), 15000);
    function onKey(e) {
      e.preventDefault(); e.stopPropagation();
      if (['Control', 'Shift', 'Alt', 'Meta'].includes(e.key)) return;   // wait for the real key
      if (e.key === 'Escape') return done(null);
      const name = keyName(e.code);
      if (!name) return toast("That key can't be used, pick another.");
      done([e.ctrlKey && 'Ctrl', e.shiftKey && 'Shift', e.altKey && 'Alt', name].filter(Boolean).join('+'));
    }
    window.addEventListener('keydown', onKey, true);
  });
}

// browser key code → Windows key name
function keyName(code) {
  let m;
  if ((m = code.match(/^Key([A-Z])$/))) return m[1];
  if ((m = code.match(/^Digit(\d)$/))) return 'D' + m[1];
  if ((m = code.match(/^F(\d{1,2})$/))) return 'F' + m[1];
  if ((m = code.match(/^Numpad(\d)$/))) return 'NumPad' + m[1];
  return { NumpadAdd: 'Add', NumpadSubtract: 'Subtract', NumpadMultiply: 'Multiply', NumpadDivide: 'Divide', NumpadDecimal: 'Decimal',
    ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right', Insert: 'Insert', Delete: 'Delete', Home: 'Home', End: 'End',
    PageUp: 'PageUp', PageDown: 'PageDown', Space: 'Space', Enter: 'Enter', Tab: 'Tab', Backquote: 'Oemtilde', Minus: 'OemMinus', Equal: 'Oemplus',
    BracketLeft: 'OemOpenBrackets', BracketRight: 'OemCloseBrackets', Semicolon: 'OemSemicolon', Quote: 'OemQuotes', Comma: 'Oemcomma', Period: 'OemPeriod',
    Slash: 'OemQuestion', Backslash: 'OemPipe', Pause: 'Pause', ScrollLock: 'Scroll' }[code] || null;
}

function controlsHtml(c) {
  if (!c) return '<span class="muted small">Unavailable.</span>';
  const bound = id => c.bindings.find(b => b.action === id);
  return `<table><tr><th>Action</th><th>Wheel button</th><th>Keyboard</th><th></th></tr>
    ${c.questions.map(q => {
      const b = bound(q.id);
      return `<tr><td>${esc(q.label)}</td>
        <td>${b ? `<b>${esc(b.deviceName || b.device)}</b> · button ${b.button}` : '<span class="dim">—</span>'}</td>
        <td class="small"><div class="row" style="gap:6px">${q.hotkey ? `<kbd>${esc(q.hotkey).replaceAll('+', '</kbd>+<kbd>')}</kbd>` : '<span class="dim">—</span>'}<button class="small" data-act="key" data-id="${q.id}">Set key</button>${q.hotkey ? `<button class="small" data-act="keyclear" data-id="${q.id}">×</button>` : ''}</div></td>
        <td><div class="row" style="justify-content:flex-end;gap:6px"><button class="small" data-act="bind" data-id="${q.id}">Bind wheel</button>${b ? `<button class="small" data-act="clear" data-id="${q.id}">Clear</button>` : ''}${q.app ? '' : `<button class="small" data-act="ask" data-id="${q.id}">Ask now</button>`}</div></td></tr>`;
    }).join('')}</table>
    ${c.keyErrors?.length ? `<p class="small bad" style="margin:8px 0 0">Couldn't register: ${c.keyErrors.map(esc).join(', ')}</p>` : ''}
    <p class="small muted" style="margin:8px 0 0">Keyboard shortcuts work while iRacing is in front. Avoid keys iRacing itself uses (plain F1–F12, letters): combinations like Ctrl+Shift+… or F13–F24 are safest. <button class="small" data-act="keysreset">Reset keys to defaults</button></p>
    ${c.quiet ? '<p class="small warn" style="margin:8px 0 0">Quiet mode is on: only important calls.</p>' : ''}`;
}
