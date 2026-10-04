// Canvas charts: stacked telemetry lanes sharing a distance axis, and a track map.
import { isNum } from './core.js';

const css = n => getComputedStyle(document.documentElement).getPropertyValue(n).trim();

function setupCanvas(canvas, w, h) {
  const dpr = window.devicePixelRatio || 1;
  canvas.width = Math.max(1, Math.round(w * dpr));
  canvas.height = Math.max(1, Math.round(h * dpr));
  canvas.style.height = h + 'px';
  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  return ctx;
}

export class TelemetryChart {
  constructor(container, { lanes, onCursor, onZoom, format }) {
    this.el = container;
    this.lanes = lanes;
    this.onCursor = onCursor;
    this.onZoom = onZoom;
    this.format = format || ((key, v) => isNum(v) ? v.toFixed(Math.abs(v) < 10 ? 2 : 0) : '–');
    this.data = null;
    this.domain = [0, 1];
    this.cursor = null;
    this.drag = null;
    container.innerHTML = '';
    container.classList.add('chart-wrap');
    this.laneEls = lanes.map(l => {
      const div = document.createElement('div');
      div.className = 'lane';
      div.innerHTML = `<canvas class="base"></canvas><canvas class="over" style="position:absolute;left:0;top:0"></canvas><div class="lane-label">${l.label}${l.unit ? ` <span class="dim">${l.unit}</span>` : ''}</div><div class="lane-values"></div>`;
      container.appendChild(div);
      const base = div.querySelector('.base'), over = div.querySelector('.over');
      for (const c of [base, over]) {
        c.addEventListener('mousemove', e => this._move(e, c));
        c.addEventListener('mouseleave', () => { if (!this.drag) this.setCursor(null, true); });
        c.addEventListener('mousedown', e => { this.drag = { x0: this._xAt(e, c), x1: this._xAt(e, c) }; });
        c.addEventListener('dblclick', () => this.resetZoom());
        c.addEventListener('wheel', e => { e.preventDefault(); this._wheel(e, c); }, { passive: false });
      }
      return { lane: l, div, base, over, values: div.querySelector('.lane-values') };
    });
    this._up = () => {
      if (!this.drag) return;
      const [a, b] = [Math.min(this.drag.x0, this.drag.x1), Math.max(this.drag.x0, this.drag.x1)];
      this.drag = null;
      if (b - a > (this.domain[1] - this.domain[0]) * 0.01) this.setDomain(a, b, true); else this.drawOverlay();
    };
    window.addEventListener('mouseup', this._up);
    this._ro = new ResizeObserver(() => this.draw());
    this._ro.observe(container);
  }

  destroy() { window.removeEventListener('mouseup', this._up); this._ro.disconnect(); }

  setData(data) {
    this.data = data;
    this.domain = [0, data.length];
    this.draw();
  }

  resetZoom() { if (this.data) this.setDomain(0, this.data.length, true); }

  setDomain(a, b, fire) {
    this.domain = [Math.max(0, a), Math.min(this.data?.length ?? b, b)];
    this.draw();
    if (fire && this.onZoom) this.onZoom(this.domain[0], this.domain[1]);
  }

  _xAt(e, canvas) {
    const r = canvas.getBoundingClientRect();
    const f = (e.clientX - r.left) / r.width;
    return this.domain[0] + f * (this.domain[1] - this.domain[0]);
  }

  _move(e, canvas) {
    const x = this._xAt(e, canvas);
    if (this.drag) { this.drag.x1 = x; this.drawOverlay(); return; }
    this.setCursor(x, true);
  }

  _wheel(e, canvas) {
    if (!this.data) return;
    const x = this._xAt(e, canvas);
    const [a, b] = this.domain;
    const k = e.deltaY > 0 ? 1.25 : 0.8;
    let na = x - (x - a) * k, nb = x + (b - x) * k;
    if (nb - na < 30) return;
    if (na < 0) { nb -= na; na = 0; }
    if (nb > this.data.length) { na -= nb - this.data.length; nb = this.data.length; }
    this.setDomain(Math.max(0, na), Math.min(this.data.length, nb), true);
  }

  setCursor(x, fire) {
    this.cursor = x;
    this.drawOverlay();
    if (fire && this.onCursor) this.onCursor(x);
  }

  valueAt(series, x) {
    if (!series || !this.data) return NaN;
    const i = x / this.data.step;
    const i0 = Math.floor(i), i1 = Math.min(series.length - 1, i0 + 1);
    if (i0 < 0 || i0 >= series.length) return NaN;
    const a = series[i0], b = series[i1];
    if (!isNum(a)) return b; if (!isNum(b)) return a;
    return a + (b - a) * (i - i0);
  }

  draw() {
    if (!this.data) return;
    const w = this.el.clientWidth;
    if (w <= 0) return;
    const [d0, d1] = this.domain;
    const { step, laps, corners, sectors } = this.data;
    const grid = css('--line'), text = css('--dim');
    for (const L of this.laneEls) {
      const lane = L.lane, h = lane.height;
      const ctx = setupCanvas(L.base, w, h);
      setupCanvas(L.over, w, h);
      L.over.style.top = '0';
      ctx.clearRect(0, 0, w, h);
      const pad = { t: 18, b: 6 };
      // y range
      let min = lane.min, max = lane.max;
      if (min === undefined || max === undefined) {
        let lo = Infinity, hi = -Infinity;
        for (const lap of laps) {
          const s = lap.channels[lane.key]; if (!s) continue;
          const i0 = Math.max(0, Math.floor(d0 / step)), i1 = Math.min(s.length - 1, Math.ceil(d1 / step));
          for (let i = i0; i <= i1; i++) { const v = s[i]; if (isNum(v)) { if (v < lo) lo = v; if (v > hi) hi = v; } }
        }
        if (!isFinite(lo)) { lo = 0; hi = 1; }
        if (lane.symmetric) { const m = Math.max(Math.abs(lo), Math.abs(hi), lane.minSpan || 0.1); lo = -m; hi = m; }
        const span = (hi - lo) || 1;
        min = min ?? lo - span * 0.06; max = max ?? hi + span * 0.06;
      }
      const X = d => (d - d0) / (d1 - d0) * w;
      const Y = v => pad.t + (1 - (v - min) / (max - min)) * (h - pad.t - pad.b);
      L.X = X; L.Y = Y;

      // corner bands + sector lines
      for (const c of corners || []) {
        if (c.end < d0 || c.start > d1) continue;
        ctx.fillStyle = 'rgba(255,255,255,0.025)';
        ctx.fillRect(X(c.start), 0, X(c.end) - X(c.start), h);
      }
      ctx.strokeStyle = grid; ctx.lineWidth = 1;
      for (const s of sectors || []) { if (s < d0 || s > d1) continue; ctx.beginPath(); ctx.setLineDash([3, 4]); ctx.moveTo(X(s), 0); ctx.lineTo(X(s), h); ctx.stroke(); }
      ctx.setLineDash([]);
      if (lane.zero && min < 0 && max > 0) { ctx.strokeStyle = css('--line2'); ctx.beginPath(); ctx.moveTo(0, Y(0)); ctx.lineTo(w, Y(0)); ctx.stroke(); }

      // series (draw base lap last so it sits on top when equal)
      const order = laps.map((l, i) => i).reverse();
      for (const li of order) {
        const lap = laps[li];
        const s = lap.channels[lane.key];
        if (!s) continue;
        const i0 = Math.max(0, Math.floor(d0 / step) - 1), i1 = Math.min(s.length - 1, Math.ceil(d1 / step) + 1);
        const stride = Math.max(1, Math.floor((i1 - i0) / (w * 1.5)));
        ctx.strokeStyle = lap.color;
        ctx.lineWidth = li === 0 ? 1.6 : 1.4;
        ctx.globalAlpha = lap.dim ? 0.45 : 1;
        ctx.beginPath();
        let pen = false;
        for (let i = i0; i <= i1; i += stride) {
          const v = s[i];
          if (!isNum(v)) { pen = false; continue; }
          const x = X(i * step), y = Y(v);
          if (!pen) { ctx.moveTo(x, y); pen = true; } else ctx.lineTo(x, y);
        }
        ctx.stroke();
        ctx.globalAlpha = 1;
      }
      // corner names on the first lane
      if (L === this.laneEls[0]) {
        ctx.fillStyle = text; ctx.font = '10px Segoe UI';
        for (const c of corners || []) if (c.apex >= d0 && c.apex <= d1) ctx.fillText(c.name, X(c.apex) + 2, h - 6);
      }
    }
    this.drawOverlay();
  }

  drawOverlay() {
    if (!this.data) return;
    const w = this.el.clientWidth;
    for (const L of this.laneEls) {
      const ctx = L.over.getContext('2d');
      const dpr = window.devicePixelRatio || 1;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      const h = L.lane.height;
      ctx.clearRect(0, 0, w, h);
      if (this.drag) {
        const a = L.X(Math.min(this.drag.x0, this.drag.x1)), b = L.X(Math.max(this.drag.x0, this.drag.x1));
        ctx.fillStyle = 'rgba(34,211,126,0.12)'; ctx.fillRect(a, 0, b - a, h);
      }
      if (this.cursor == null || !L.X) { L.values.innerHTML = ''; continue; }
      const x = L.X(this.cursor);
      ctx.strokeStyle = 'rgba(232,236,242,0.6)'; ctx.lineWidth = 1;
      ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, h); ctx.stroke();
      const vals = [];
      for (const lap of this.data.laps) {
        const s = lap.channels[L.lane.key]; if (!s) continue;
        const v = this.valueAt(s, this.cursor);
        if (isNum(v)) {
          ctx.fillStyle = lap.color; ctx.beginPath(); ctx.arc(x, L.Y(v), 3, 0, Math.PI * 2); ctx.fill();
          vals.push(`<span style="color:${lap.color}">${this.format(L.lane.key, v)}</span>`);
        }
      }
      L.values.innerHTML = vals.join('');
    }
  }
}

/** Track map with lap lines, colouring modes, markers and a cursor. */
export class TrackMapView {
  constructor(canvas) {
    this.canvas = canvas;
    this.model = null;
    this.laps = [];
    this.markers = [];
    this.cursor = null;
    this.domain = null;
    this.mode = 'laps';
    this.step = 2;
    this._ro = new ResizeObserver(() => this.draw());
    this._ro.observe(canvas);
  }
  destroy() { this._ro.disconnect(); }
  setModel(m) { this.model = m; this.draw(); }
  setLaps(laps, step) { this.laps = laps; this.step = step; this.draw(); }
  setMarkers(m) { this.markers = m; this.draw(); }
  setDomain(a, b) { this.domain = a == null ? null : [a, b]; this.draw(); }
  setCursor(d) { this.cursor = d; this.draw(); }
  setMode(m) { this.mode = m; this.draw(); }

  _bounds() {
    const base = this.laps[0];
    const xs = [], ys = [];
    if (this.domain && base) {
      const i0 = Math.floor(this.domain[0] / this.step), i1 = Math.ceil(this.domain[1] / this.step);
      for (const lap of this.laps) for (let i = i0; i <= i1 && i < lap.x.length; i++) if (isNum(lap.x[i])) { xs.push(lap.x[i]); ys.push(lap.y[i]); }
    }
    if (xs.length < 2 && this.model) { xs.push(...this.model.x); ys.push(...this.model.y); }
    let minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys);
    const pad = Math.max(maxX - minX, maxY - minY) * 0.08 + 5;
    return { minX: minX - pad, maxX: maxX + pad, minY: minY - pad, maxY: maxY + pad };
  }

  draw() {
    const c = this.canvas;
    const w = c.clientWidth, h = c.clientHeight || w;
    if (!w || !this.model) return;
    const ctx = setupCanvas(c, w, h);
    c.style.height = '';
    ctx.clearRect(0, 0, w, h);
    const b = this._bounds();
    const k = Math.min(w / (b.maxX - b.minX), h / (b.maxY - b.minY));
    const ox = (w - (b.maxX - b.minX) * k) / 2, oy = (h - (b.maxY - b.minY) * k) / 2;
    const P = (x, y) => [ox + (x - b.minX) * k, oy + (b.maxY - y) * k];
    this._P = P;

    // track
    const m = this.model;
    const trackW = Math.max(6, Math.min(26, 12 * k));
    ctx.lineCap = 'round'; ctx.lineJoin = 'round';
    ctx.strokeStyle = '#262D38'; ctx.lineWidth = trackW;
    ctx.beginPath();
    m.x.forEach((x, i) => { const [px, py] = P(x, m.y[i]); i ? ctx.lineTo(px, py) : ctx.moveTo(px, py); });
    ctx.closePath(); ctx.stroke();

    // laps
    const order = this.laps.map((l, i) => i).reverse();
    for (const li of order) {
      const lap = this.laps[li];
      const colorBy = this.mode !== 'laps' && li === Math.min(1, this.laps.length - 1) ? this.mode : null;
      ctx.lineWidth = li === 0 ? 2.2 : 2;
      if (!colorBy) {
        ctx.strokeStyle = lap.color;
        ctx.beginPath(); let pen = false;
        for (let i = 0; i < lap.x.length; i++) {
          if (!isNum(lap.x[i])) { pen = false; continue; }
          const [px, py] = P(lap.x[i], lap.y[i]);
          if (!pen) { ctx.moveTo(px, py); pen = true; } else ctx.lineTo(px, py);
        }
        ctx.stroke();
      } else {
        ctx.lineWidth = 3.2;
        for (let i = 1; i < lap.x.length; i++) {
          if (!isNum(lap.x[i]) || !isNum(lap.x[i - 1])) continue;
          ctx.strokeStyle = this._color(colorBy, lap, i);
          const [ax, ay] = P(lap.x[i - 1], lap.y[i - 1]), [bx, by] = P(lap.x[i], lap.y[i]);
          ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by); ctx.stroke();
        }
      }
    }

    // corner labels
    ctx.font = '600 11px Segoe UI'; ctx.fillStyle = '#8A94A6';
    for (const cn of m.corners || []) {
      const i = Math.round(cn.apex / m.step) % m.x.length;
      const [px, py] = P(m.x[i], m.y[i]);
      if (px < -20 || py < -20 || px > w + 20 || py > h + 20) continue;
      ctx.fillText(cn.name, px + 9, py - 7);
    }
    // markers
    for (const mk of this.markers) {
      const lap = this.laps[mk.lap ?? 0]; if (!lap) continue;
      const i = Math.round(mk.dist / this.step);
      if (!isNum(lap.x[i])) continue;
      const [px, py] = P(lap.x[i], lap.y[i]);
      ctx.fillStyle = mk.color; ctx.strokeStyle = '#0B0E13'; ctx.lineWidth = 1.5;
      ctx.beginPath();
      if (mk.kind === 'brake') ctx.rect(px - 4, py - 4, 8, 8);
      else if (mk.kind === 'throttle') { ctx.moveTo(px, py - 5); ctx.lineTo(px + 5, py + 4); ctx.lineTo(px - 5, py + 4); ctx.closePath(); }
      else ctx.arc(px, py, 4, 0, Math.PI * 2);
      ctx.fill(); ctx.stroke();
    }
    // start/finish
    const [sx, sy] = P(m.x[0], m.y[0]);
    ctx.fillStyle = '#E8ECF2'; ctx.fillRect(sx - 2, sy - 2, 4, 4);
    // cursor
    if (this.cursor != null) {
      for (const lap of this.laps) {
        const i = Math.round(this.cursor / this.step);
        if (!isNum(lap.x[i])) continue;
        const [px, py] = P(lap.x[i], lap.y[i]);
        ctx.fillStyle = lap.color; ctx.strokeStyle = '#fff'; ctx.lineWidth = 2;
        ctx.beginPath(); ctx.arc(px, py, 5.5, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
      }
    }
  }

  _color(mode, lap, i) {
    if (mode === 'speed') {
      const s = lap.speed?.[i]; if (!isNum(s)) return '#444';
      const t = Math.max(0, Math.min(1, (s - this._smin) / (this._smax - this._smin || 1)));
      return `hsl(${(1 - t) * 240}, 85%, 55%)`;
    }
    if (mode === 'inputs') {
      const br = lap.brake?.[i] || 0, th = lap.throttle?.[i] || 0;
      if (br > 5) return `rgba(255,77,94,${0.35 + br / 160})`;
      if (th > 95) return '#22D37E';
      if (th > 5) return '#F5C542';
      return '#8A94A6';
    }
    if (mode === 'delta') {
      const d = lap.delta; if (!d) return '#888';
      const a = d[i], b = d[Math.max(0, i - 5)];
      if (!isNum(a) || !isNum(b)) return '#666';
      const g = a - b; // + = losing time here
      if (g > 0.004) return '#FF4D5E'; if (g < -0.004) return '#22D37E'; return '#8A94A6';
    }
    return lap.color;
  }

  setSpeedRange(min, max) { this._smin = min; this._smax = max; }
}
