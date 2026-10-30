const $ = id => document.getElementById(id);
const REFRESH_MS = 5000;

function rate(bps) {
  const bits = bps * 8;
  const units = ['bps', 'Kbps', 'Mbps', 'Gbps'];
  let i = 0, v = bits;
  while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
  return `${v.toFixed(v >= 100 || i === 0 ? 0 : 1)} ${units[i]}`;
}

function bytes(n) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0, v = n;
  while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
  return `${v.toFixed(v >= 100 || i === 0 ? 0 : 1)} ${units[i]}`;
}

function makeChart(canvas) {
  return new Chart(canvas, {
    type: 'line',
    data: {
      datasets: [
        { label: 'Download', borderColor: '#73bf69', backgroundColor: '#73bf6933', fill: true, pointRadius: 0, borderWidth: 1.5, tension: 0.15, data: [] },
        { label: 'Upload', borderColor: '#5794f2', backgroundColor: '#5794f233', fill: true, pointRadius: 0, borderWidth: 1.5, tension: 0.15, data: [] },
      ],
    },
    options: {
      responsive: true, maintainAspectRatio: false, animation: false, parsing: false,
      interaction: { mode: 'index', intersect: false },
      scales: {
        x: {
          type: 'linear', grid: { color: '#2c3235' },
          ticks: {
            color: '#8e8e9e', maxTicksLimit: 8,
            callback: v => {
              const d = new Date(v);
              const t = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
              return Number($('range').value) > 1440 ? `${d.toLocaleDateString([], { month: 'short', day: 'numeric' })} ${t}` : t;
            },
          },
        },
        y: { beginAtZero: true, grid: { color: '#2c3235' }, ticks: { color: '#8e8e9e', callback: v => rate(v) } },
      },
      plugins: {
        legend: { labels: { color: '#ccccdc' } },
        tooltip: {
          callbacks: {
            title: items => new Date(items[0].parsed.x).toLocaleString(),
            label: ctx => `${ctx.dataset.label}: ${rate(ctx.parsed.y)}`,
          },
        },
      },
    },
  });
}

const routerChart = makeChart($('routerChart'));
const deviceChart = makeChart($('deviceChart'));

async function getJson(url) {
  const r = await fetch(url);
  if (!r.ok) throw new Error(`${url}: HTTP ${r.status}`);
  return r.json();
}

function plot(chart, hist) {
  chart.data.datasets[0].data = hist.points.map(p => ({ x: p.t * 1000, y: p.down }));
  chart.data.datasets[1].data = hist.points.map(p => ({ x: p.t * 1000, y: p.up }));
  chart.options.scales.x.min = hist.from * 1000;
  chart.options.scales.x.max = hist.to * 1000;
  chart.update();
}

// Shared time window. windowEnd === null means "live" (window ends at the present).
let windowEnd = null;
let oldestSample = null;
let chartsDirty = true;

const nowSec = () => Math.floor(Date.now() / 1000);
const windowSec = () => Number($('range').value) * 60;
const stepSec = () => ($('step').value === 'window' ? windowSec() : Number($('step').value) * 60);
const effectiveEnd = () => windowEnd ?? nowSec();

// Clamp the window so it never starts before the oldest stored sample, and snap to live near the present.
function setWindowEnd(end) {
  const now = nowSec();
  if (oldestSample !== null) end = Math.max(end, oldestSample + windowSec());
  windowEnd = end >= now - 5 ? null : end;
  chartsDirty = true;
  updateNav();
  refresh();
}

function canGoBack() {
  return oldestSample !== null && effectiveEnd() - windowSec() > oldestSample;
}

function fmtWindow(s) {
  return new Date(s * 1000).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

function updateNav() {
  $('back').disabled = !canGoBack();
  $('fwd').disabled = windowEnd === null;
  $('now').disabled = windowEnd === null;
  const w = $('window');
  if (windowEnd === null) {
    w.textContent = `Live · last ${$('range').selectedOptions[0].textContent}`;
    w.className = 'win live';
  } else {
    w.textContent = `${fmtWindow(windowEnd - windowSec())} – ${fmtWindow(windowEnd)}`;
    w.className = 'win';
  }
  if (canGoBack() === false && oldestSample !== null) {
    $('back').title = `No data before ${fmtWindow(oldestSample)} (older samples are purged)`;
  } else {
    $('back').title = 'Step back (←)';
  }
}

function summary(h) {
  return `Peak ▼ ${rate(h.peakDown)} ▲ ${rate(h.peakUp)} · Total ▼ ${bytes(h.totalDownBytes)} ▲ ${bytes(h.totalUpBytes)}`;
}

let deviceListKey = '';
let lastDevices = [];

function ipValue(ip) {
  const p = (ip || '').split('.').map(Number);
  return p.length === 4 && p.every(n => Number.isInteger(n)) ? p.reduce((a, n) => a * 256 + n, 0) : Infinity;
}

function sortDevices(devices) {
  const by = $('sortBy').value;
  const dir = $('sortDir').value === 'desc' ? -1 : 1;
  const cmp = {
    name: (a, b) => (a.name || a.mac).localeCompare(b.name || b.mac, undefined, { sensitivity: 'base', numeric: true }),
    ip: (a, b) => ipValue(a.ip) - ipValue(b.ip),
    mac: (a, b) => a.mac.localeCompare(b.mac),
    down: (a, b) => a.downBytesPerSec - b.downBytesPerSec,
  }[by];
  // Ties fall back to name so the order is stable between refreshes.
  return [...devices].sort((a, b) => dir * cmp(a, b) || a.name.localeCompare(b.name));
}

function syncDeviceList(devices, force = false) {
  const sel = $('device');
  const sorted = sortDevices(devices);
  const rateSort = $('sortBy').value === 'down';
  const key = sorted.map(d => d.mac + d.name + d.ip + (rateSort ? Math.round(d.downBytesPerSec) : '')).join('|');
  if (!force && key === deviceListKey) return;
  // Don't reshuffle the list under the cursor while the dropdown is open.
  if (!force && document.activeElement === sel) return;
  deviceListKey = key;
  const previous = sel.value || localStorage.getItem('device');
  sel.innerHTML = '';
  for (const d of sorted) {
    const o = document.createElement('option');
    o.value = d.mac;
    o.textContent = `${d.name || '(unnamed)'} — ${d.ip ? `${d.ip} — ${d.mac}` : d.mac}` + (rateSort ? ` — ${rate(d.downBytesPerSec)}` : '');
    sel.appendChild(o);
  }
  if ([...sel.options].some(o => o.value === previous)) sel.value = previous;
}

let refreshSeq = 0;
let lastMac = null;
async function refresh() {
  const seq = ++refreshSeq;
  const minutes = $('range').value;
  try {
    const [status, devices] = await Promise.all([getJson('api/status'), getJson('api/devices')]);
    if (seq !== refreshSeq) return;
    lastDevices = devices;
    syncDeviceList(devices);
    oldestSample = status.oldestSample;
    updateNav();

    const st = $('status');
    if (status.lastError) { st.textContent = `Router error: ${status.lastError}`; st.className = 'err'; }
    else if (status.lastSuccess) { st.textContent = `${status.router.onlineDevices} devices · updated ${new Date(status.lastSuccess).toLocaleTimeString()}`; st.className = ''; }
    else { st.textContent = 'waiting for first poll…'; st.className = ''; }

    $('r-down').textContent = rate(status.router.downBytesPerSec);
    $('r-up').textContent = rate(status.router.upBytesPerSec);

    const mac = $('device').value;
    if (mac !== lastMac) { lastMac = mac; chartsDirty = true; }
    const dev = devices.find(d => d.mac === mac);
    if (mac) {
      $('d-down').textContent = dev ? rate(dev.downBytesPerSec) : 'offline';
      $('d-up').textContent = dev ? rate(dev.upBytesPerSec) : 'offline';
    }

    // Charts of a past window are static, so only redraw them when live or when the window/device changed.
    if (windowEnd !== null && !chartsDirty) return;
    const end = effectiveEnd();
    const to = windowEnd === null ? '' : `&to=${end}`;
    const rh = await getJson(`api/router/history?minutes=${minutes}${to}`);
    let dh = null;
    if (mac) dh = await getJson(`api/devices/${encodeURIComponent(mac)}/history?minutes=${minutes}${to}`);
    if (seq !== refreshSeq) return;

    plot(routerChart, rh);
    $('r-sub').textContent = summary(rh);
    if (dh) {
      plot(deviceChart, dh);
      $('d-sub').textContent = summary(dh) + (dev ? ` · ${dev.connectionType} ${dev.interface}`.trimEnd() : '');
    }
    chartsDirty = false;
  } catch (e) {
    if (seq !== refreshSeq) return;
    $('status').textContent = e.message;
    $('status').className = 'err';
  }
}

$('range').addEventListener('change', () => { localStorage.setItem('range', $('range').value); setWindowEnd(effectiveEnd()); });
$('step').value = localStorage.getItem('step') || $('step').value;
$('step').addEventListener('change', () => localStorage.setItem('step', $('step').value));
$('back').addEventListener('click', () => { if (canGoBack()) setWindowEnd(effectiveEnd() - stepSec()); });
$('fwd').addEventListener('click', () => { if (windowEnd !== null) setWindowEnd(windowEnd + stepSec()); });
$('now').addEventListener('click', () => setWindowEnd(nowSec()));
document.addEventListener('keydown', e => {
  if (e.target.closest('input, select, textarea') || e.altKey || e.ctrlKey || e.metaKey) return;
  if (e.key === 'ArrowLeft') $('back').click();
  else if (e.key === 'ArrowRight') $('fwd').click();
  else if (e.key === 'End') $('now').click();
});
$('range').value = localStorage.getItem('range') || $('range').value;
$('device').addEventListener('change', () => { localStorage.setItem('device', $('device').value); $('device').blur(); chartsDirty = true; refresh(); });
for (const id of ['sortBy', 'sortDir']) {
  const el = $(id);
  el.value = localStorage.getItem(id) || el.value;
  el.addEventListener('change', () => {
    localStorage.setItem(id, el.value);
    syncDeviceList(lastDevices, true);
    refresh();
  });
}
let usage = { devices: [], router: null };
let usageSort = { key: 'total', dir: -1 };

function usageQuery() {
  const range = $('u-range').value;
  if (range !== 'custom') return `range=${range}`;
  const from = $('u-from').value, to = $('u-to').value;
  return from && to && to >= from ? `range=custom&from=${from}&to=${to}` : null;
}

function renderUsage() {
  const k = usageSort.key;
  const val = {
    name: d => (d.name || d.mac).toLowerCase(),
    ip: d => ipValue(d.ip),
    mac: d => d.mac,
    down: d => d.downBytes,
    up: d => d.upBytes,
    total: d => d.totalBytes,
  }[k];
  const rows = [...usage.devices].sort((a, b) => {
    const x = val(a), y = val(b);
    return (x < y ? -1 : x > y ? 1 : 0) * usageSort.dir;
  });
  const body = document.querySelector('#u-table tbody');
  body.innerHTML = '';
  for (const d of rows) {
    const tr = document.createElement('tr');
    tr.dataset.mac = d.mac;
    const cells = [d.name || d.mac, d.ip, d.mac, bytes(d.downBytes), bytes(d.upBytes), bytes(d.totalBytes)];
    cells.forEach((text, i) => {
      const td = document.createElement('td');
      td.textContent = text;
      if (i >= 3) td.className = 'num';
      tr.appendChild(td);
    });
    body.appendChild(tr);
  }
  document.querySelectorAll('#u-table th').forEach(th => {
    const numeric = ['down', 'up', 'total'].includes(th.dataset.k);
    const sorted = th.dataset.k === k ? (usageSort.dir > 0 ? ' sorted-asc' : ' sorted-desc') : '';
    th.className = (numeric ? 'num' : '') + sorted;
  });
}

async function refreshUsage() {
  const q = usageQuery();
  if (!q) return;
  const u = await getJson(`api/usage?${q}`);
  usage = { devices: u.devices.map(d => ({ ...d, totalBytes: d.upBytes + d.downBytes })), router: u.router };
  $('u-router').textContent = `${u.from === u.to ? u.from : `${u.from} → ${u.to}`} · Router total ▼ ${bytes(u.router.downBytes)} ▲ ${bytes(u.router.upBytes)} (${bytes(u.router.upBytes + u.router.downBytes)})`;
  renderUsage();
}

document.querySelectorAll('#u-table th').forEach(th => th.addEventListener('click', () => {
  const key = th.dataset.k;
  usageSort = { key, dir: usageSort.key === key ? -usageSort.dir : (key === 'name' || key === 'ip' || key === 'mac' ? 1 : -1) };
  renderUsage();
}));
document.querySelector('#u-table tbody').addEventListener('click', e => {
  const mac = e.target.closest('tr')?.dataset.mac;
  const sel = $('device');
  if (mac && [...sel.options].some(o => o.value === mac)) { sel.value = mac; localStorage.setItem('device', mac); refresh(); window.scrollTo({ top: 0, behavior: 'smooth' }); }
});

for (const id of ['u-range', 'u-from', 'u-to']) {
  const el = $(id);
  el.value = localStorage.getItem(id) || el.value;
  el.addEventListener('change', () => {
    localStorage.setItem(id, el.value);
    $('u-custom').hidden = $('u-range').value !== 'custom';
    refreshUsage().catch(e => { $('u-router').textContent = e.message; });
  });
}
$('u-custom').hidden = $('u-range').value !== 'custom';
refreshUsage().catch(e => { $('u-router').textContent = e.message; });
setInterval(() => refreshUsage().catch(() => {}), 30000);

updateNav();
refresh();
setInterval(refresh, REFRESH_MS);
