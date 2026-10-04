import { api, esc, toast, $, $$ } from '../core.js';

export async function render(root) {
  const draw = async () => {
    const o = await api('/api/overlays');
    root.innerHTML = `
      <div class="page-head"><div class="grow"><h1>Overlays</h1><p>Real-time HUD elements drawn on top of iRacing. Run iRacing in <b>borderless window</b> mode so they stay visible.</p></div>
        <button id="edit" class="${o.editMode ? 'primary' : ''}">${o.editMode ? '✓ Done positioning' : 'Move & resize on screen'}</button></div>
      <div class="card" style="margin-bottom:14px"><div class="row" style="gap:24px">
        <label class="check"><input type="checkbox" id="onlycar" ${o.onlyInCar ? 'checked' : ''}> Only show while I'm in the car</label>
        <label class="field" style="flex-direction:row;align-items:center;gap:8px">Frame rate <select id="fps">${[15, 20, 30, 45, 60].map(f => `<option ${o.fps === f ? 'selected' : ''}>${f}</option>`).join('')}</select></label>
        <span class="small muted">Hotkeys: <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F9</kbd> move/resize (drag, scroll wheel) · <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F10</kbd> hide all · <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F11</kbd> switch reference · <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F12</kbd> dashboard</span>
      </div></div>
      <div class="grid g3">${o.items.map(i => `
        <div class="card overlay-card" data-id="${i.id}">
          <div class="row" style="justify-content:space-between;flex-wrap:nowrap"><h2>${esc(i.name)}</h2><label class="switch"><input type="checkbox" class="en" ${i.enabled ? 'checked' : ''}><span></span></label></div>
          <div class="desc">${esc(i.description)}</div>
          <div class="row small muted" style="flex-wrap:nowrap">Size <input type="range" class="scale" min="0.5" max="2" step="0.05" value="${i.scale}" style="flex:1"><span class="num sv">${Math.round(i.scale * 100)}%</span></div>
          <div class="row small muted" style="flex-wrap:nowrap">Background <input type="range" class="op" min="0.2" max="1" step="0.05" value="${i.opacity}" style="flex:1"><button class="small ghost reset" title="Reset position">⟲</button></div>
        </div>`).join('')}</div>`;
    $('#edit', root).onclick = async () => { await api('/api/overlays', { body: { editMode: !o.editMode } }); if (!o.editMode) toast('Drag overlays to move them, scroll to resize. Click the button again (or Ctrl+Shift+F9) when done.'); draw(); };
    $('#onlycar', root).onchange = e => api('/api/overlays', { body: { onlyInCar: e.target.checked } });
    $('#fps', root).onchange = e => api('/api/overlays', { body: { fps: +e.target.value } });
    for (const card of $$('.overlay-card', root)) {
      const id = card.dataset.id;
      $('.en', card).onchange = e => api('/api/overlays', { body: { id, enabled: e.target.checked } });
      $('.scale', card).oninput = e => { $('.sv', card).textContent = Math.round(e.target.value * 100) + '%'; };
      $('.scale', card).onchange = e => api('/api/overlays', { body: { id, scale: +e.target.value } });
      $('.op', card).onchange = e => api('/api/overlays', { body: { id, opacity: +e.target.value } });
      $('.reset', card).onclick = () => api('/api/overlays', { body: { id, resetPosition: true } }).then(() => toast('Position reset'));
    }
  };
  draw();
}
