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
          ticks: { color: '#8e8e9e', maxTicksLimit: 8, callback: v => new Date(v).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) },
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
  chart.update();
}

function summary(h) {
  return `Peak ▼ ${rate(h.peakDown)} ▲ ${rate(h.peakUp)} · Total ▼ ${bytes(h.totalDownBytes)} ▲ ${bytes(h.totalUpBytes)}`;
}

let deviceListKey = '';
function syncDeviceList(devices) {
  const sel = $('device');
  const key = devices.map(d => d.mac + d.name).sort().join('|');
  if (key === deviceListKey) return;
  deviceListKey = key;
  const previous = sel.value || localStorage.getItem('device');
  sel.innerHTML = '';
  for (const d of [...devices].sort((a, b) => (a.name || a.mac).localeCompare(b.name || b.mac))) {
    const o = document.createElement('option');
    o.value = d.mac;
    o.textContent = `${d.name || '(unnamed)'} — ${d.ip || d.mac}`;
    sel.appendChild(o);
  }
  if ([...sel.options].some(o => o.value === previous)) sel.value = previous;
}

async function refresh() {
  const minutes = $('range').value;
  try {
    const [status, devices] = await Promise.all([getJson('api/status'), getJson('api/devices')]);
    syncDeviceList(devices);

    const st = $('status');
    if (status.lastError) { st.textContent = `Router error: ${status.lastError}`; st.className = 'err'; }
    else if (status.lastSuccess) { st.textContent = `${status.router.onlineDevices} devices · updated ${new Date(status.lastSuccess).toLocaleTimeString()}`; st.className = ''; }
    else { st.textContent = 'waiting for first poll…'; st.className = ''; }

    $('r-down').textContent = rate(status.router.downBytesPerSec);
    $('r-up').textContent = rate(status.router.upBytesPerSec);
    const rh = await getJson(`api/router/history?minutes=${minutes}`);
    plot(routerChart, rh);
    $('r-sub').textContent = summary(rh);

    const mac = $('device').value;
    if (mac) {
      const dev = devices.find(d => d.mac === mac);
      $('d-down').textContent = dev ? rate(dev.downBytesPerSec) : 'offline';
      $('d-up').textContent = dev ? rate(dev.upBytesPerSec) : 'offline';
      const dh = await getJson(`api/devices/${encodeURIComponent(mac)}/history?minutes=${minutes}`);
      plot(deviceChart, dh);
      $('d-sub').textContent = summary(dh) + (dev ? ` · ${dev.connectionType} ${dev.interface}`.trimEnd() : '');
    }
  } catch (e) {
    $('status').textContent = e.message;
    $('status').className = 'err';
  }
}

$('range').addEventListener('change', refresh);
$('device').addEventListener('change', () => { localStorage.setItem('device', $('device').value); refresh(); });
refresh();
setInterval(refresh, REFRESH_MS);
