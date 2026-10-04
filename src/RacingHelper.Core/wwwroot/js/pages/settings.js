import { api, esc, toast, loadSettings, $, $$ } from '../core.js';

export async function render(root) {
  const [s, voices, status] = await Promise.all([api('/api/settings'), api('/api/voices').catch(() => []), api('/api/status')]);
  const sel = (id, opts, val) => `<select id="${id}">${opts.map(([v, l]) => `<option value="${v}" ${String(val) === String(v) ? 'selected' : ''}>${l}</option>`).join('')}</select>`;
  const chk = (id, val, label) => `<label class="check"><input type="checkbox" id="${id}" ${val ? 'checked' : ''}> ${label}</label>`;
  root.innerHTML = `
    <div class="page-head"><div class="grow"><h1>Settings</h1><p>Everything is stored locally in <span class="num">${esc(status.dataFolder)}</span>.</p></div><button class="primary" id="save">Save</button></div>
    <div class="grid g2">
      <div class="card"><h2 style="margin-bottom:12px">Race engineer</h2>
        <div class="grid" style="gap:12px">
          ${chk('voiceEnabled', s.voiceEnabled, 'Speak messages')}
          <label class="field">How chatty${sel('voiceVerbosity', [['minimal', 'Minimal — flags, fuel, damage, PBs'], ['normal', 'Normal — plus lap times & biggest loss'], ['detailed', 'Detailed — plus gains and conditions']], s.voiceVerbosity)}</label>
          ${chk('cornerCallouts', s.cornerCallouts, 'Call out each corner where I lose time (immediately after the corner)')}
          <label class="field">Voice${sel('voiceName', [['', 'Windows default'], ...voices.map(v => [v, v])], s.voiceName)}</label>
          <div class="form-grid"><label class="field">Rate<input type="range" id="voiceRate" min="-5" max="6" value="${s.voiceRate}"></label><label class="field">Volume<input type="range" id="voiceVolume" min="0" max="100" value="${s.voiceVolume}"></label></div>
          <div><button id="test" class="small">Radio check</button></div>
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
        <div style="margin-top:12px">${chk('allowLan', s.allowLan, 'Allow opening the dashboard from other devices on my network (e.g. a tablet next to the rig) — restart to apply')}</div>
      </div>
    </div>`;
  $('#test', root).onclick = async () => { await save(true); await api('/api/engineer/test', { body: {} }); };
  $('#save', root).onclick = () => save();
  async function save(quiet) {
    const v = id => $('#' + id, root);
    const next = {
      ...s,
      voiceEnabled: v('voiceEnabled').checked, voiceVerbosity: v('voiceVerbosity').value, cornerCallouts: v('cornerCallouts').checked,
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
