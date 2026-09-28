// OneSLicenseHub Client Application — Oxide Computer Company Style Reference
let state = {
  overview: null,
  activeView: 'digi-150', // 'digi-150' by default, 'digi-151', 'both', 'catalog'
  filterCategory: 'all',
  filterStatus: 'all',
  filterEnv: 'all',        // catalog: 'all' | 'PROD' | 'DEV'
  filterMedium: 'all',     // catalog: 'all' | 'Программная' | 'Аппаратная'
  searchQuery: '',
  selectedPort: null,
  pollingIntervalMinutes: 60,
  nextScheduledRefresh: null,
  lastRefreshTime: null,
  isLoading: false,
  timerInterval: null
};
window.__state = state;

// DOM Elements
const elValTotalPorts = document.getElementById('valTotalPorts');
const elValOccupiedPorts = document.getElementById('valOccupiedPorts');
const elValInUsePorts = document.getElementById('valInUsePorts');
const elValFreePorts = document.getElementById('valFreePorts');
const elValTotalCapacity = document.getElementById('valTotalCapacity');

const elHeaderPulse = document.getElementById('headerPulse');
const elTextStatus150 = document.getElementById('textStatus150');
const elTextStatus151 = document.getElementById('textStatus151');
const elBadgeStatus150 = document.getElementById('badgeStatus150');
const elBadgeStatus151 = document.getElementById('badgeStatus151');

const elCountdownText = document.getElementById('countdownText');
const elLastSyncText = document.getElementById('lastSyncText');
const elSearchInput = document.getElementById('tableSearch');
const elBtnRefresh = document.getElementById('btnRefresh');
const elBtnRefreshText = document.getElementById('btnRefreshText');
const elBtnExportCsv = document.getElementById('btnExportCsv');
const elBtnExportJson = document.getElementById('btnExportJson');
const elSelPollingInterval = document.getElementById('selPollingInterval');

// Sections
const elSectionDigi150 = document.getElementById('sectionDigi150');
const elSectionDigi151 = document.getElementById('sectionDigi151');
const elSectionCatalog = document.getElementById('sectionCatalog');

// Tables
const elTbodyDigi150 = document.getElementById('tbodyDigi150');
const elTbodyDigi151 = document.getElementById('tbodyDigi151');
const elTbodyCatalog = document.getElementById('tbodyCatalog');

// Summary Chips
const elChipOcc150 = document.getElementById('chipOcc150');
const elChipInUse150 = document.getElementById('chipInUse150');
const elChipPool150 = document.getElementById('chipPool150');

const elChipOcc151 = document.getElementById('chipOcc151');
const elChipInUse151 = document.getElementById('chipInUse151');
const elChipPool151 = document.getElementById('chipPool151');

const elChipTotalCatalog = document.getElementById('chipTotalCatalogLicenses');

// Tab counters
const elTabCountBoth = document.getElementById('tabCountBoth');
const elTabCount150 = document.getElementById('tabCount150');
const elTabCount151 = document.getElementById('tabCount151');
const elTabCountCatalog = document.getElementById('tabCountCatalog');

// Drawer elements
const drawerBackdrop = document.getElementById('drawerBackdrop');
const detailDrawer = document.getElementById('detailDrawer');
const btnCloseDrawer = document.getElementById('btnCloseDrawer');

// Init
const VIEWS = ['digi-150', 'digi-151', 'both', 'catalog'];

// Switches the main view; the URL hash (#catalog, #both, ...) makes every view linkable
function selectView(view, updateHash = true) {
  if (!VIEWS.includes(view)) return;
  document.querySelectorAll('.view-tab-btn').forEach(b => b.classList.toggle('active', b.dataset.view === view));
  state.activeView = view;
  if (updateHash) {
    try { history.replaceState(null, '', `#${view}`); } catch { /* ignore */ }
  }
  applyViewVisibility();
}

document.addEventListener('DOMContentLoaded', async () => {
  setupEventListeners();
  const hashView = decodeURIComponent(location.hash.replace('#', ''));
  if (VIEWS.includes(hashView)) selectView(hashView, false);
  else document.body.dataset.view = state.activeView;
  await loadSettings();
  await loadData(false);
  startAutoRefreshTimer();
});

function setupEventListeners() {
  // Manual refresh button
  elBtnRefresh.addEventListener('click', () => {
    loadData(true);
  });

  // Export buttons
  elBtnExportCsv.addEventListener('click', exportToCsv);
  elBtnExportJson.addEventListener('click', exportToJson);

  // Search input
  elSearchInput.addEventListener('input', (e) => {
    state.searchQuery = e.target.value.toLowerCase().trim();
    renderAllTables();
  });

  // Navigation View Tabs
  document.querySelectorAll('.view-tab-btn').forEach(btn => {
    btn.addEventListener('click', (e) => {
      const targetBtn = e.target.closest('.view-tab-btn');
      if (!targetBtn) return;
      const view = targetBtn.dataset.view;
      if (!view) return;
      selectView(view);
    });
  });

  // Category & Status Filters
  document.querySelectorAll('.filter-pill').forEach(btn => {
    btn.addEventListener('click', (e) => {
      const target = e.target.closest('.filter-pill');
      if (!target) return;
      const type = target.dataset.filter;
      const val = target.dataset.value;

      document.querySelectorAll(`.filter-pill[data-filter="${type}"]`).forEach(b => b.classList.remove('active'));
      target.classList.add('active');

      if (type === 'category') state.filterCategory = val;
      if (type === 'status') state.filterStatus = val;
      if (type === 'env') state.filterEnv = val;
      if (type === 'medium') state.filterMedium = val;

      renderAllTables();
    });
  });

  // Polling interval change
  elSelPollingInterval.addEventListener('change', async (e) => {
    const mins = parseInt(e.target.value, 10);
    if (!mins || isNaN(mins)) return;

    try {
      const res = await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pollingIntervalMinutes: mins })
      });
      if (res.ok) {
        const data = await res.json();
        state.pollingIntervalMinutes = data.pollingIntervalMinutes;
        if (data.nextScheduledRefresh) {
          state.nextScheduledRefresh = new Date(data.nextScheduledRefresh);
        } else {
          state.nextScheduledRefresh = new Date(Date.now() + mins * 60 * 1000);
        }
        updateCountdown();
      }
    } catch (err) {
      console.error('Failed to update polling interval:', err);
    }
  });

  // Drawer modal controls
  btnCloseDrawer.addEventListener('click', closeDrawer);
  drawerBackdrop.addEventListener('click', closeDrawer);

  // Hardware & Network Telemetry Drawers Toggle
  // Device card toggle in the hub header; open state is remembered per hub
  const setupDrawerToggle = (btnId, drawerId) => {
    const btn = document.getElementById(btnId);
    const drawer = document.getElementById(drawerId);
    if (!btn || !drawer) return;
    const storageKey = `onesl.drawer.${drawerId}`;

    const setOpen = (open) => {
      btn.classList.toggle('active', open);
      btn.setAttribute('aria-expanded', String(open));
      drawer.hidden = !open;
      try { localStorage.setItem(storageKey, open ? '1' : '0'); } catch { /* storage unavailable */ }
    };

    let initial = false;
    try { initial = localStorage.getItem(storageKey) === '1'; } catch { /* storage unavailable */ }
    setOpen(initial);

    btn.addEventListener('click', () => setOpen(drawer.hidden));
  };

  setupDrawerToggle('toggleDrawer150', 'drawer150');
  setupDrawerToggle('toggleDrawer151', 'drawer151');

  // Copy buttons
  const wireCopyBtn = (btnId, textGetter) => {
    const btn = document.getElementById(btnId);
    if (!btn) return;
    btn.addEventListener('click', async () => {
      const text = typeof textGetter === 'function' ? textGetter() : textGetter;
      if (!text || text === '—') return;
      try {
        await navigator.clipboard.writeText(text);
        const icon = btn.querySelector('.material-symbols-outlined');
        const origIcon = icon ? icon.textContent : 'content_copy';
        btn.classList.add('copied');
        if (icon) icon.textContent = 'check';
        setTimeout(() => {
          btn.classList.remove('copied');
          if (icon) icon.textContent = origIcon;
        }, 1500);
      } catch (err) {
        console.error('Clipboard copy failed:', err);
      }
    });
  };

  wireCopyBtn('btnCopyMac150', () => document.getElementById('specMac150')?.textContent?.trim());
  wireCopyBtn('btnCopyGuid150', () => document.getElementById('specGuid150')?.textContent?.trim());
  wireCopyBtn('btnCopySerial150', () => document.getElementById('specSerial150')?.textContent?.trim());

  wireCopyBtn('btnCopyMac151', () => document.getElementById('specMac151')?.textContent?.trim());
  wireCopyBtn('btnCopyGuid151', () => document.getElementById('specGuid151')?.textContent?.trim());
  wireCopyBtn('btnCopySerial151', () => document.getElementById('specSerial151')?.textContent?.trim());

  // Chip-level highlight: every element with the same data-hl in the section (ports, hosts, server cards)
  let currentHlKey = null;
  const clearHl = () => {
    document.querySelectorAll('.hl-source, .hl-match').forEach(el => el.classList.remove('hl-source', 'hl-match'));
    currentHlKey = null;
  };
  document.addEventListener('mouseover', (e) => {
    const chip = e.target.closest('[data-hl]');
    if (!chip) {
      if (currentHlKey) clearHl();
      return;
    }
    const key = chip.dataset.hl;
    if (!key || key === currentHlKey) return;
    clearHl();
    currentHlKey = key;
    const scope = chip.closest('.hub-table-section') || document;
    scope.querySelectorAll('[data-hl]').forEach(el => {
      if (el.dataset.hl === key) el.classList.add(el === chip ? 'hl-source' : 'hl-match');
    });
  });

  // Universal matching cell highlight on hover
  let currentMatchKey = null;

  document.addEventListener('mouseover', (e) => {
    const td = e.target.closest('td[data-match]');
    if (!td) return;
    const match = td.dataset.match;
    if (!match || match.endsWith(':') || match.endsWith(':undefined')) return;

    if (currentMatchKey === match) return;
    currentMatchKey = match;

    document.querySelectorAll('td.cell-match-highlight, td.cell-hover-source').forEach(el => {
      el.classList.remove('cell-match-highlight', 'cell-hover-source');
    });

    td.classList.add('cell-hover-source');

    const table = td.closest('table');
    if (table) {
      table.querySelectorAll('td[data-match]').forEach(el => {
        if (el !== td && el.dataset.match === match) {
          el.classList.add('cell-match-highlight');
        }
      });
    }
  });

  document.addEventListener('mouseout', (e) => {
    const td = e.target.closest('td[data-match]');
    if (!td) return;
    if (!e.relatedTarget || !e.relatedTarget.closest('td[data-match]')) {
      currentMatchKey = null;
      document.querySelectorAll('td.cell-match-highlight, td.cell-hover-source').forEach(el => {
        el.classList.remove('cell-match-highlight', 'cell-hover-source');
      });
    }
  });
}

function applyViewVisibility() {
  const isBoth = state.activeView === 'both';
  const is150 = state.activeView === 'digi-150' || isBoth;
  const is151 = state.activeView === 'digi-151' || isBoth;
  const isCatalog = state.activeView === 'catalog';
  document.body.dataset.view = state.activeView; // CSS hides per-hub device controls in the combined view

  if (elSectionDigi150) elSectionDigi150.style.display = is150 ? 'flex' : 'none';
  if (elSectionDigi151) elSectionDigi151.style.display = is151 ? 'flex' : 'none';
  if (elSectionCatalog) elSectionCatalog.style.display = isCatalog ? 'flex' : 'none';

  renderAllTables();
}

async function loadSettings() {
  try {
    const resp = await fetch('/api/settings');
    if (resp.ok) {
      const data = await resp.json();
      state.pollingIntervalMinutes = data.pollingIntervalMinutes || 60;
      elSelPollingInterval.value = String(state.pollingIntervalMinutes);
      if (data.nextScheduledRefresh && !data.nextScheduledRefresh.startsWith('0001')) {
        state.nextScheduledRefresh = new Date(data.nextScheduledRefresh);
      }
      if (data.lastRefreshTime && !data.lastRefreshTime.startsWith('0001')) {
        state.lastRefreshTime = new Date(data.lastRefreshTime);
      }
    }
  } catch (err) {
    console.warn('Could not load initial settings, using defaults', err);
  }
}

function startAutoRefreshTimer() {
  if (state.timerInterval) clearInterval(state.timerInterval);
  updateCountdown();
  state.timerInterval = setInterval(updateCountdown, 1000);
}

function updateCountdown() {
  if (!state.nextScheduledRefresh) {
    elCountdownText.textContent = 'ДО ОПРОСА: --:--';
    return;
  }

  const now = new Date();
  const diffMs = state.nextScheduledRefresh - now;

  if (diffMs <= 0) {
    elCountdownText.textContent = 'ОПРОС ОБОРУДОВАНИЯ...';
    if (!state.isLoading) {
      loadData(false);
    }
    return;
  }

  const totalSecs = Math.floor(diffMs / 1000);
  const hours = Math.floor(totalSecs / 3600);
  const minutes = Math.floor((totalSecs % 3600) / 60);
  const seconds = totalSecs % 60;

  if (hours > 0) {
    elCountdownText.textContent = `ДО ОПРОСА: ${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
  } else {
    elCountdownText.textContent = `ДО ОПРОСА: ${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
  }
}

async function loadData(force = false) {
  try {
    state.isLoading = true;
    elBtnRefresh.classList.add('loading');
    elBtnRefresh.disabled = true;
    elBtnRefreshText.textContent = force ? 'ОПРОС...' : 'ОБНОВЛЕНИЕ...';

    const endpoint = force ? '/api/overview/refresh' : '/api/overview';
    const method = force ? 'POST' : 'GET';
    const resp = await fetch(endpoint, { method });
    if (!resp.ok) throw new Error(`HTTP error ${resp.status}`);

    state.overview = await resp.json();
    state.lastRefreshTime = new Date();
    state.nextScheduledRefresh = new Date(Date.now() + state.pollingIntervalMinutes * 60 * 1000);

    elLastSyncText.innerHTML = `<span class="material-symbols-outlined icon-mono" style="font-size: 13px;">history</span><span>ОПРОС: ${state.lastRefreshTime.toLocaleTimeString('ru-RU')}</span>`;

    renderMetrics();
    applyViewVisibility();
    updateHealthStatus();
  } catch (err) {
    console.error('Failed to load telemetry:', err);
    elHeaderPulse.classList.remove('active');
  } finally {
    state.isLoading = false;
    elBtnRefresh.classList.remove('loading');
    elBtnRefresh.disabled = false;
    elBtnRefreshText.textContent = 'ОПРОСИТЬ СЕЙЧАС';
  }
}

function updateHealthStatus() {
  if (!state.overview) return;
  const d150 = state.overview.devices.find(d => d.id === 'digi-150');
  const d151 = state.overview.devices.find(d => d.id === 'digi-151');

  const s150 = d150 ? d150.status.toUpperCase() : 'OFFLINE';
  const s151 = d151 ? d151.status.toUpperCase() : 'OFFLINE';

  elTextStatus150.textContent = s150;
  elTextStatus151.textContent = s151;
  applyStatusBadge(elBadgeStatus150, s150);
  applyStatusBadge(elBadgeStatus151, s151);

  elHeaderPulse.classList.add('active');
}

function renderMetrics() {
  if (!state.overview) return;
  const o = state.overview;
  elValTotalPorts.textContent = o.totalPorts;
  elValOccupiedPorts.textContent = o.occupiedPorts;
  elValInUsePorts.textContent = o.inUsePorts;
  elValFreePorts.textContent = o.freePorts;
  elValTotalCapacity.textContent = o.totalLicensesCapacity;

  // Digi 150 summaries
  const d150 = o.devices.find(d => d.id === 'digi-150');
  if (d150) {
    const occ150 = d150.ports.filter(p => p.hasDevice).length;
    const inUse150 = d150.ports.filter(p => p.status === 'InUse').length;
    const cap150 = d150.ports.reduce((sum, p) => sum + (p.licenseInfo?.totalCapacity || 0), 0);
    elChipOcc150.textContent = `ЗАНЯТО: ${occ150}`;
    elChipInUse150.textContent = `В РАБОТЕ: ${inUse150}`;
    elChipPool150.textContent = `ЛИЦЕНЗИЙ: ${cap150}`;
    elTabCount150.textContent = `${occ150} / 24`;
  }

  // Digi 151 summaries
  const d151 = o.devices.find(d => d.id === 'digi-151');
  if (d151) {
    const occ151 = d151.ports.filter(p => p.hasDevice).length;
    const inUse151 = d151.ports.filter(p => p.status === 'InUse').length;
    const cap151 = d151.ports.reduce((sum, p) => sum + (p.licenseInfo?.totalCapacity || 0), 0);
    elChipOcc151.textContent = `ЗАНЯТО: ${occ151}`;
    elChipInUse151.textContent = `В РАБОТЕ: ${inUse151}`;
    elChipPool151.textContent = `ЛИЦЕНЗИЙ: ${cap151}`;
    elTabCount151.textContent = `${occ151} / 14`;
  }

  const catalog = getDiscoveredLicensesList();
  elChipTotalCatalog.textContent = `${catalog.length} ПОЗИЦИЙ`;
  elTabCountCatalog.textContent = `${catalog.length} ЛИЦЕНЗИЙ`;
  elTabCountBoth.textContent = `${o.occupiedPorts} / 38`;

  renderDeviceSpecs(d150, d151);
}

function applyStatusBadge(el, status) {
  if (!el) return;
  el.textContent = status;
  el.classList.toggle('badge-status-degraded', status === 'DEGRADED');
  el.classList.toggle('badge-status-offline', status === 'OFFLINE' || status === 'UNKNOWN');
  el.title = status === 'DEGRADED'
    ? 'Концентратор не ответил, показаны последние известные данные'
    : status === 'OFFLINE' ? 'Концентратор не ответил, данных нет' : '';
}

function formatLastSeen(iso) {
  if (!iso) return '—';
  const dt = new Date(iso);
  if (Number.isNaN(dt.getTime()) || dt.getFullYear() < 2000) return '—';
  return dt.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

// Fills the header subtitle and the device card of one hub (suffix: '150' | '151')
function renderDeviceSpec(device, suffix, defaults) {
  if (!device) return;
  const $ = (id) => document.getElementById(id + suffix);
  const set = (id, text) => { const el = $(id); if (el) el.textContent = text; };

  const port = device.port || defaults.port;
  const proto = device.protocol || defaults.protocol;
  const serial = device.serialNumber || device.productId || '';

  set('hubAddr', `${device.ipAddress}:${port}`);
  set('specModel', device.model || defaults.model);
  set('specIp', `${device.ipAddress}:${port} (${proto})`);
  set('specMac', device.macAddress || '—');
  set('specSerial', serial || '—');
  set('specFw', device.firmware || '—');
  set('specSeen', device.status === 'Online' ? formatLastSeen(device.lastCheckTime) : `${formatLastSeen(device.lastCheckTime)} • нет ответа`);

  const elLink = $('specLink');
  if (elLink) elLink.href = `${proto.toLowerCase()}://${device.ipAddress}:${port}`;

  const elGuid = $('specGuid');
  if (elGuid) {
    elGuid.textContent = device.deviceGuid || '—';
    elGuid.title = device.deviceGuid || '';
  }

  // Always-visible compact line in the header: MAC • FW
  const meta = [
    device.macAddress ? `MAC ${device.macAddress}` : '',
    device.firmware ? `FW ${device.firmware}` : ''
  ].filter(Boolean).join(' • ');
  set('hubMeta', meta ? `• ${meta}` : '');
}

function renderDeviceSpecs(d150, d151) {
  renderDeviceSpec(d150, '150', { port: 443, protocol: 'HTTPS', model: 'AnywhereUSB 24 Plus' });
  renderDeviceSpec(d151, '151', { port: 80, protocol: 'HTTP', model: 'AnywhereUSB/14' });
}

function renderAllTables() {
  if (!state.overview) return;

  if (state.activeView === 'both' || state.activeView === 'digi-150') {
    renderHubTable('digi-150', elTbodyDigi150);
  }
  if (state.activeView === 'both' || state.activeView === 'digi-151') {
    renderHubTable('digi-151', elTbodyDigi151);
  }
  if (state.activeView === 'catalog') {
    renderCatalogTable();
  }
}

function filterPort(port) {
  // Status filter
  if (state.filterStatus !== 'all' && port.status !== state.filterStatus) return false;

  // Category filter
  if (state.filterCategory !== 'all') {
    const cat = (port.licenseInfo?.category || '').toLowerCase();
    const prod = (port.licenseInfo?.productName || '').toLowerCase();
    if (state.filterCategory === 'slk' && !cat.includes('слк')) return false;
    if (state.filterCategory === 'guardant' && !cat.includes('guardant') && !prod.includes('ювелирсофт')) return false;
    if (state.filterCategory === 'dalion' && !cat.includes('далион') && !prod.includes('далион')) return false;
    if (state.filterCategory === 'hasp' && !cat.includes('hasp') && !prod.includes('hasp')) return false;
  }

  // Search query
  if (state.searchQuery) {
    const q = state.searchQuery;
    const text = [
      port.deviceName,
      port.portLabel,
      port.groupName,
      port.connectedClientHost,
      port.connectedClientIp,
      port.hardwareManufacturer,
      port.hardwareProduct,
      port.serialNumber,
      port.licenseInfo?.productName,
      port.licenseInfo?.category,
      port.licenseInfo?.dongleId,
      port.licenseInfo?.registrationNumber,
      port.licenseInfo?.licenseType
    ].filter(Boolean).join(' ').toLowerCase();

    if (!text.includes(q)) return false;
  }

  return true;
}

// Calculate host frequency across all ports
function getHostCounts() {
  const counts = new Map();
  if (!state.overview || !state.overview.allPorts) return counts;

  for (const p of state.overview.allPorts) {
    const raw = (p.connectedClientHost || '').trim();
    if (raw) {
      const norm = raw.toLowerCase();
      counts.set(norm, (counts.get(norm) || 0) + 1);
    }
  }
  return counts;
}

// Deterministic index for grouped hosts (6 muted enterprise tones)
function getHostGroupPaletteIndex(name) {
  if (!name) return 0;
  const normalized = name.toLowerCase().trim();
  let hash = 5381;
  for (let i = 0; i < normalized.length; i++) {
    hash = ((hash << 5) + hash) + normalized.charCodeAt(i);
    hash |= 0;
  }
  return Math.abs(hash) % 6;
}

// Enterprise Host Display: Clean Monochrome for unique, Subtle Group Chip only for duplicates
function getHostBadgeHtml(hostname, ip, hostCounts) {
  if (!hostname && !ip) return '<div class="cell-line-primary"><span style="color: var(--color-fog);">—</span></div>';

  const host = (hostname || '').trim();
  const rawIp = (ip || '').trim();

  const isHostIp = !host || /^[\d\.]+$/.test(host);
  const displayName = isHostIp ? (rawIp || host) : host;
  const subIp = (!isHostIp && rawIp && rawIp !== host) ? rawIp : '';

  const normKey = displayName.toLowerCase();
  const count = hostCounts ? (hostCounts.get(normKey) || 0) : 0;
  const isShared = count > 1;

  const ipMarkup = subIp ? `<div class="cell-sub cell-mono">${escapeHtml(subIp)}</div>` : '';

  if (!isShared) {
    return `
      <div class="cell-line-primary"><span class="host-plain cell-mono">${escapeHtml(displayName)}</span></div>
      ${ipMarkup}
    `;
  }

  const themeIndex = getHostGroupPaletteIndex(normKey);

  return `
    <div class="cell-line-primary">
      <span class="host-repeat host-subtle-${themeIndex}" title="Сервер подключен к нескольким портам в этой таблице (${count})">
        <span class="host-repeat-dot"></span>
        <span class="cell-mono">${escapeHtml(displayName)}</span>
      </span>
    </div>
    ${ipMarkup}
  `;
}

// Subtle Category Tag with Muted Corporate Tints
function getCategoryBadgeHtml(category) {
  if (!category) return '';
  const catLower = category.toLowerCase();
  let colorClass = '';

  if (catLower.includes('hasp')) {
    colorClass = 'cat-hasp';
  } else if (catLower.includes('guardant') || catLower.includes('ювелирсофт') || catLower.includes('отраслевой')) {
    colorClass = 'cat-guardant';
  } else if (catLower.includes('далион') || catLower.includes('sentinel')) {
    colorClass = 'cat-dalion';
  } else if (catLower.includes('слк')) {
    colorClass = 'cat-slk';
  }

  return `<span class="cat-tag ${colorClass}">${escapeHtml(category)}</span>`;
}

function getKeyModelHtml(product, manufacturer, usbVersion, license) {
  let pName = product;
  let mName = manufacturer;

  if (!pName || pName.toLowerCase() === 'usb key' || pName.toLowerCase() === 'dalion') {
    if (license) {
      const cat = (license.category || '').toLowerCase();
      const prod = (license.productName || '').toLowerCase();
      if (cat.includes('guardant') || prod.includes('ювелирсофт')) {
        pName = 'Guardant Stealth II';
        mName = 'Aktiv Co.';
      } else if (cat.includes('слк') || prod.includes('слк')) {
        pName = 'Katran СЛК Key';
        mName = 'Katran';
      } else if (cat.includes('sentinel') || cat.includes('далион') || prod.includes('далион')) {
        pName = 'Sentinel HL';
        mName = 'SafeNet Inc.';
      } else if (cat.includes('hasp')) {
        if (prod.includes('сервер')) {
          pName = 'HASP HL 3.25';
          mName = 'AKS';
        } else {
          pName = 'HASP 2.17';
          mName = 'AKS';
        }
      }
    }
  }

  if (!pName) return '<div class="cell-line-primary"><span style="color: var(--color-fog);">Нет устройства</span></div>';

  const prodLower = pName.toLowerCase();
  let keyClass = '';

  if (prodLower.includes('3.25') || prodLower.includes('hasp hl')) {
    keyClass = 'key-hasp-hl';
  } else if (prodLower.includes('2.17') || prodLower.includes('net') || prodLower.includes('hasp')) {
    keyClass = 'key-hasp-net';
  } else if (prodLower.includes('sentinel')) {
    keyClass = 'key-sentinel';
  } else if (prodLower.includes('stealth')) {
    keyClass = 'key-guardant-stealth';
  } else if (prodLower.includes('sign')) {
    keyClass = 'key-guardant-sign';
  } else if (prodLower.includes('katran') || prodLower.includes('слк')) {
    keyClass = 'key-katran';
  }

  const busParts = [];
  if (mName) busParts.push(mName);
  if (usbVersion) busParts.push(`USB ${usbVersion}`);
  const busInfo = busParts.join(' ');

  return `
    <div class="cell-line-primary"><span class="key-model ${keyClass}">${escapeHtml(pName)}</span></div>
    ${busInfo ? `<div class="hw-sub">${escapeHtml(busInfo)}</div>` : ''}
  `;
}

// Format "Количество лицензий" with strict Russian pluralization (e.g. "1 клиентская (СЛК)", "1 серверная")
function formatLicenseCount(lic) {
  if (!lic) return '<span style="color: var(--color-fog);">—</span>';

  const cap = lic.totalCapacity;
  const type = (lic.licenseType || '').toLowerCase();
  const prod = (lic.productName || '').toLowerCase();
  const cat = (lic.category || '').toLowerCase();

  if (type.includes('сервер') || prod.includes('сервер')) {
    return '<span class="cell-mono text-bone">1 серверная</span>';
  }

  if (type.includes('далион') || prod.includes('далион') || cat.includes('далион')) {
    return '<span class="cell-mono text-bone">1 сетевая (Далион)</span>';
  }

  if (cap !== null && cap !== undefined && cap > 0) {
    let word = 'клиентских';
    const mod10 = cap % 10;
    const mod100 = cap % 100;
    if (mod10 === 1 && mod100 !== 11) {
      word = 'клиентская';
    } else if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) {
      word = 'клиентские';
    }

    if (cat.includes('слк') || prod.includes('слк')) {
      return `<span class="cell-mono text-phosphor">${cap} ${word} (СЛК)</span>`;
    }
    return `<span class="cell-mono text-phosphor">${cap} ${word}</span>`;
  }

  if (lic.licenseType) {
    return `<span class="cell-mono text-silver">${escapeHtml(lic.licenseType)}</span>`;
  }

  return '<span style="color: var(--color-fog);">—</span>';
}

function renderHubTable(deviceId, tbody) {
  const device = state.overview.devices.find(d => d.id === deviceId);
  if (!device || !device.ports || device.ports.length === 0) {
    tbody.innerHTML = `<tr><td colspan="7" class="table-empty-cell">КОНЦЕНТРАТОР НЕ ОТВЕТИЛ (${escapeHtml(device?.ipAddress || deviceId)}). ПОВТОРНЫЙ ОПРОС ЧЕРЕЗ 2 МИНУТЫ ИЛИ ПО КНОПКЕ ОБНОВЛЕНИЯ</td></tr>`;
    return;
  }

  const ports = device.ports.filter(filterPort);

  if (ports.length === 0) {
    tbody.innerHTML = `<tr><td colspan="7" class="table-empty-cell">НЕТ ДАННЫХ ПО ВЫБРАННЫМ КРИТЕРИЯМ ПОИСКА</td></tr>`;
    return;
  }

  // Count hosts strictly within this active table view
  const tableHostCounts = new Map();
  for (const p of ports) {
    const raw = (p.connectedClientHost || '').trim();
    if (raw) {
      const isHostIp = !raw || /^[\d\.]+$/.test(raw);
      const name = isHostIp ? (p.connectedClientIp || raw) : raw;
      const norm = name.toLowerCase().trim();
      tableHostCounts.set(norm, (tableHostCounts.get(norm) || 0) + 1);
    }
  }

  tbody.innerHTML = ports.map(p => {
    // Status
    let statusBadge = '<span class="status-badge badge-available">СВОБОДЕН</span>';
    if (p.status === 'InUse') {
      statusBadge = '<span class="status-badge badge-in-use" title="AnywhereUSB сессия активна, идут обращения процессов 1С">В РАБОТЕ</span>';
    } else if (p.hasDevice && p.connectedClientHost) {
      statusBadge = '<span class="status-badge badge-connected" title="Группа привязана к серверу, режим ожидания">ЗАХВАЧЕН</span>';
    } else if (p.hasDevice) {
      statusBadge = '<span class="status-badge badge-unassigned" title="Ключ вставлен в порт, группа не подключена">НЕ ПРИВЯЗАН</span>';
    }

    // Client
    const clientCell = getHostBadgeHtml(p.connectedClientHost, p.connectedClientIp, tableHostCounts);

    // License
    const lic = p.licenseInfo;
    let licCell = '<div class="cell-line-primary"><span style="color: var(--color-fog);">—</span></div>';

    if (lic) {
      licCell = `
        <div class="cell-line-primary">
          ${getCategoryBadgeHtml(lic.category)}
          <span class="cell-primary">${escapeHtml(lic.productName)}</span>
        </div>
        ${(lic.dongleId || lic.registrationNumber) ? `
          <div class="cell-sub cell-mono">
            ${lic.dongleId ? 'ID: ' + escapeHtml(lic.dongleId) : ''} 
            ${lic.registrationNumber ? 'РЕГ.№ ' + escapeHtml(lic.registrationNumber) : ''}
          </div>
        ` : ''}
      `;
    } else if (p.hasDevice) {
      licCell = '<div class="cell-line-primary"><span style="color: var(--color-steel);">Не опознано</span></div>';
    }

    // Hardware with intelligent fallback deduction
    const hwCell = p.hasDevice
      ? getKeyModelHtml(p.hardwareProduct || 'USB Key', p.hardwareManufacturer, p.usbVersion, lic)
      : '<div class="cell-line-primary"><span style="color: var(--color-fog);">Нет устройства</span></div>';

    const countCell = `<div class="cell-line-primary">${formatLicenseCount(lic)}</div>`;
    const portNum = p.portNumber < 10 ? '0' + p.portNumber : p.portNumber;

    // Keys for universal cell hover matching
    const hostNorm = (p.connectedClientHost || p.connectedClientIp || '').toLowerCase().trim();
    const grpNorm = (p.groupName || ('GROUP ' + p.portNumber)).toUpperCase().trim();
    
    // Effective HW for cross-table matching
    let effectiveHw = p.hardwareProduct || '';
    if (!effectiveHw || effectiveHw.toLowerCase() === 'usb key' || effectiveHw.toLowerCase() === 'dalion') {
      if (lic) {
        const cat = (lic.category || '').toLowerCase();
        const prod = (lic.productName || '').toLowerCase();
        if (cat.includes('guardant') || prod.includes('ювелирсофт')) effectiveHw = 'Guardant Stealth II';
        else if (cat.includes('слк') || prod.includes('слк')) effectiveHw = 'Katran СЛК Key';
        else if (cat.includes('sentinel') || cat.includes('далион') || prod.includes('далион')) effectiveHw = 'Sentinel HL';
        else if (cat.includes('hasp')) effectiveHw = prod.includes('сервер') ? 'HASP HL 3.25' : 'HASP 2.17';
      }
    }
    const hwNorm = effectiveHw.toLowerCase().trim();
    const licNorm = (lic?.productName || '').toLowerCase().trim();

    let countKey = '';
    if (lic) {
      if (lic.licenseType && (lic.licenseType.toLowerCase().includes('сервер') || lic.productName?.toLowerCase().includes('сервер'))) {
        countKey = 'count:server';
      } else if (lic.totalCapacity) {
        countKey = `count:${lic.totalCapacity}`;
      } else if (lic.licenseType) {
        countKey = `count:${lic.licenseType.toLowerCase().trim()}`;
      }
    }

    return `
      <tr onclick="window.selectPortRow('${p.deviceId}', ${p.portNumber})">
        <td class="td-usb-port" data-match="port:${portNum}">
          <div class="usb-port-badge ${p.hasDevice ? 'port-has-dongle' : 'port-empty'}" title="Физический USB-порт ${portNum}">
            <span class="material-symbols-outlined port-icon">usb</span>
            <span class="port-num">${portNum}</span>
          </div>
        </td>
        <td data-match="group:${escapeHtml(grpNorm)}">
          <div class="cell-line-primary"><span class="cell-mono cell-group-name">${escapeHtml(grpNorm)}</span></div>
        </td>
        <td ${hostNorm ? `data-match="host:${escapeHtml(hostNorm)}"` : ''}>${clientCell}</td>
        <td ${p.hasDevice && hwNorm ? `data-match="hw:${escapeHtml(hwNorm)}"` : ''}>${hwCell}</td>
        <td ${licNorm ? `data-match="lic:${escapeHtml(licNorm)}"` : ''}>${licCell}</td>
        <td ${countKey ? `data-match="${escapeHtml(countKey)}"` : ''}>${countCell}</td>
        <td data-match="status:${p.status}" style="text-align: center;">
          <div class="cell-line-primary" style="display: flex; justify-content: center;">${statusBadge}</div>
        </td>
      </tr>
    `;
  }).join('');
}

function getDiscoveredLicensesList() {
  if (!state.overview) return [];

  const list = [...(state.overview.discoveredLicenses || [])];

  // Every physical port with a key gets its own entry, unless a licence-server record already
  // claims that exact port. Matching by dongle id would hide ports that share an id (e.g. a
  // software licence and the hardware key that serves it, or several ports of one pool).
  const portKey = (deviceId, portNumber) => `${deviceId}:${portNumber}`;
  const covered = new Set(list.flatMap(l => (l.placements || []).map(pl => portKey(pl.deviceId, pl.portNumber))));

  for (const p of state.overview.allPorts || []) {
    if (!p.licenseInfo) continue;
    const key = portKey(p.deviceId, p.portNumber);
    if (covered.has(key)) continue;
    covered.add(key);
    list.push(p.licenseInfo);
  }

  return list;
}

function getLicenseLocationBadgeHtml(lic) {
  let loc = (lic.location || '').trim();

  // If not explicitly stamped, attempt correlation with allPorts
  if (!loc && state.overview?.allPorts) {
    const port = state.overview.allPorts.find(p => 
      p.licenseInfo && (
        (lic.dongleId && p.licenseInfo.dongleId === lic.dongleId) || 
        (lic.registrationNumber && p.licenseInfo.registrationNumber === lic.registrationNumber)
      )
    );
    if (port) {
      const digiLabel = port.deviceId === 'digi-150' ? 'DIGI 01' : (port.deviceId === 'digi-151' ? 'DIGI 02' : port.deviceName);
      const portNum = port.portNumber < 10 ? '0' + port.portNumber : port.portNumber;
      loc = `${digiLabel}: P${portNum}`;
    }
  }

  if (!loc) {
    if (lic.detailsSummary && /Хост:\s*([^\s,;]+)/i.test(lic.detailsSummary)) {
      loc = lic.detailsSummary.match(/Хост:\s*([^\s,;]+)/i)[1];
    } else {
      loc = '—';
    }
  }

  const locUpper = loc.toUpperCase();
  if (locUpper.startsWith('DIGI 01')) {
    return `<span class="loc-chip loc-chip-digi01" title="Физический концентратор Digi 01"><span class="loc-dot"></span>${escapeHtml(loc)}</span>`;
  }
  if (locUpper.startsWith('DIGI 02')) {
    return `<span class="loc-chip loc-chip-digi02" title="Физический концентратор Digi 02"><span class="loc-dot"></span>${escapeHtml(loc)}</span>`;
  }
  if (lic.sourceServer || locUpper.includes('СЛК') || locUpper.includes('СЕРВЕР') || /\d+\.\d+\.\d+\.\d+/.test(loc)) {
    const title = lic.sourceServer ? `Сервер лицензирования ${lic.sourceServer}` : 'Сетевой сервер лицензирования';
    return `<span class="loc-chip loc-chip-net" title="${escapeHtml(title)}"><span class="material-symbols-outlined loc-icon">lan</span>${escapeHtml(loc)}</span>`;
  }
  return `<span class="cell-sub cell-mono">${escapeHtml(loc)}</span>`;
}

// ---------------------------------------------------------------------------
// Licence registry: servers panel + table grouped by licence server
// ---------------------------------------------------------------------------

function pluralRu(n, one, few, many) {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}

function serverKeyOf(name, ip) {
  return (ip || name || '').toLowerCase();
}

function getEnvTagHtml(env) {
  if (!env) return '';
  const cls = env === 'PROD' ? 'env-prod' : env === 'DEV' ? 'env-dev' : 'env-other';
  return `<span class="env-tag ${cls}">${escapeHtml(env)}</span>`;
}

function licenseMatchesFilters(lic) {
  if (state.filterCategory !== 'all') {
    const cat = (lic.category || '').toLowerCase();
    const prod = (lic.productName || '').toLowerCase();
    if (state.filterCategory === 'slk' && !cat.includes('слк')) return false;
    if (state.filterCategory === 'guardant' && !cat.includes('guardant') && !prod.includes('ювелирсофт')) return false;
    if (state.filterCategory === 'dalion' && !cat.includes('далион') && !prod.includes('далион')) return false;
    if (state.filterCategory === 'hasp' && !cat.includes('hasp') && !prod.includes('hasp')) return false;
  }

  if (state.filterEnv !== 'all' && (lic.environment || '') !== state.filterEnv) return false;
  if (state.filterMedium !== 'all' && (lic.medium || '') !== state.filterMedium) return false;

  if (state.searchQuery) {
    const text = [
      lic.category, lic.productName, lic.location, lic.dongleId, lic.programNumber,
      lic.registrationNumber, lic.licenseType, lic.organization, lic.detailsSummary,
      lic.serverName, lic.serverIp, lic.environment, lic.medium, lic.licenseStatus,
      ...(lic.consumers || []),
      ...(lic.placements || []).map(pl => `${pl.deviceLabel} P${pl.portNumber} ${pl.connectedHost}`)
    ].filter(Boolean).join(' ').toLowerCase();
    if (!text.includes(state.searchQuery)) return false;
  }

  return true;
}

function hostChipHtml(host, extraClass = '') {
  if (!host) return '';
  const key = host.toLowerCase();
  return `<span class="host-chip ${extraClass}" data-hl="host:${escapeHtml(key)}" title="Подсветить ${escapeHtml(host)}">${escapeHtml(host)}</span>`;
}

function getMediumBadgeHtml(medium) {
  if (medium === 'Программная') {
    return `<span class="medium-badge medium-soft" title="Программная лицензия: активирована на сервере, USB-ключ не нужен"><span class="material-symbols-outlined">cloud</span>ПРОГРАММНЫЙ</span>`;
  }
  if (medium === 'Аппаратная') {
    return `<span class="medium-badge medium-hard" title="Аппаратный USB-ключ"><span class="material-symbols-outlined">usb</span>АППАРАТНЫЙ</span>`;
  }
  return '<span style="color: var(--color-fog);">—</span>';
}

function portLabel(pl) {
  return `${pl.deviceLabel} · P${String(pl.portNumber).padStart(2, '0')}`;
}

// Column 1: where the key is plugged in (Digi port) or where the licence lives (server)
function getWhereHtml(lic, pl) {
  if (pl) {
    const digiCls = pl.deviceId === 'digi-150' ? 'conn-digi01' : 'conn-digi02';
    return `<div class="stack-line"><span class="conn-chip ${digiCls}" data-hl="port:${escapeHtml(pl.deviceId)}:${pl.portNumber}" title="${escapeHtml(pl.deviceLabel)}, USB-порт ${pl.portNumber}"><span class="loc-dot"></span>${escapeHtml(portLabel(pl))}</span></div>`;
  }

  const server = lic.serverName || lic.serverIp;
  if (!server) return '<span style="color: var(--color-fog);">—</span>';
  const ip = lic.serverIp && lic.serverIp !== server ? `<span class="where-ip">${escapeHtml(lic.serverIp)}</span>` : '';
  return `<div class="stack-line"><span class="material-symbols-outlined where-icon" title="Сервер">dns</span>${hostChipHtml(server)}${ip}${getEnvTagHtml(lic.environment)}</div>`;
}

// Column 5: for a Digi key, the server each port is forwarded to (same line order as column 1);
// otherwise the 1C servers that take the licence
function getForwardedHtml(lic, pl) {
  if (pl) {
    return pl.connectedHost ? hostChipHtml(pl.connectedHost) : '<span class="cell-sub">не проброшен</span>';
  }

  const hosts = lic.consumers || [];
  if (hosts.length === 0) return '<span style="color: var(--color-fog);">—</span>';
  const shown = hosts.slice(0, 4).map(h => hostChipHtml(h)).join('');
  const more = hosts.length > 4
    ? `<span class="host-chip host-chip-more" title="${escapeHtml(hosts.slice(4).join(', '))}">+${hosts.length - 4}</span>`
    : '';
  return `<div class="chip-wrap">${shown}${more}</div>`;
}

function renderCatalogRow(lic, pl) {
  const prodNorm = (lic.productName || '').toLowerCase().trim();
  const dongleNorm = (lic.dongleId || '').toLowerCase().trim();
  const regNorm = (lic.registrationNumber || '').toLowerCase().trim();
  const orgNorm = (lic.organization || '').toLowerCase().trim();
  const mediumNorm = (lic.medium || '').toLowerCase().trim();

  let countKey = '';
  if (lic.licenseType && (lic.licenseType.toLowerCase().includes('сервер') || lic.productName?.toLowerCase().includes('сервер'))) {
    countKey = 'count:server';
  } else if (lic.totalCapacity) {
    countKey = `count:${lic.totalCapacity}`;
  } else if (lic.licenseType) {
    countKey = `count:${lic.licenseType.toLowerCase().trim()}`;
  }

  const isInactive = lic.isActive === false;
  const statusLine = isInactive ? `<div class="lic-sub"><span class="lic-inactive">${escapeHtml(lic.licenseStatus || 'Неактивна')}</span></div>` : '';
  const typeLine = lic.licenseType ? `<div class="hw-sub">${escapeHtml(lic.licenseType)}</div>` : '';

  const inUse = lic.inUseCount ?? 0; // SLK connections are serie-wide, so only per-key counters are shown
  const usage = inUse > 0 && !isInactive ? `<div class="hw-sub">ЗАНЯТО: ${inUse}</div>` : '';

  const keyHtml = `
    <div class="cell-mono">${escapeHtml(lic.dongleId || '—')}</div>
    ${lic.programNumber ? `<div class="hw-sub">${lic.category?.includes('Sentinel') ? 'ФИЧА' : 'СЕРИЯ'} ${escapeHtml(lic.programNumber)}</div>` : ''}`;

  return `
    <tr class="${isInactive ? 'row-inactive' : ''}">
      <td>${getWhereHtml(lic, pl)}</td>
      <td ${mediumNorm ? `data-match="medium:${escapeHtml(mediumNorm)}"` : ''}>${getMediumBadgeHtml(lic.medium)}</td>
      <td ${countKey ? `data-match="${escapeHtml(countKey)}"` : ''}>${formatLicenseCount(lic)}${usage}</td>
      <td ${prodNorm ? `data-match="lic:${escapeHtml(prodNorm)}"` : ''}>
        <div class="lic-title">${getCategoryBadgeHtml(lic.category)}<span class="cell-primary">${escapeHtml(lic.productName)}</span></div>
        ${typeLine}${statusLine}
      </td>
      <td>${getForwardedHtml(lic, pl)}</td>
      <td ${dongleNorm ? `data-match="dongle:${escapeHtml(dongleNorm)}"` : ''}>${keyHtml}</td>
      <td ${regNorm ? `data-match="reg:${escapeHtml(regNorm)}"` : ''}><span class="cell-mono">${escapeHtml(lic.registrationNumber || '—')}</span></td>
      <td ${orgNorm ? `data-match="org:${escapeHtml(orgNorm)}"` : ''}><span class="cell-sub">${escapeHtml(lic.organization || '—')}</span></td>
    </tr>`;
}

// Sort by physical place: Digi 01 ports, Digi 02 ports, then licence servers by name
function catalogSortKey(lic, pl) {
  if (pl) return `0|${pl.deviceId}|${String(pl.portNumber).padStart(3, '0')}`;
  const server = (lic.serverName || lic.serverIp || '').toLowerCase();
  if (server) return `1|${server}|${lic.isActive === false ? 1 : 0}|${(lic.productName || '').toLowerCase()}`;
  return '2|';
}

function renderCatalogTable() {
  const licenses = getDiscoveredLicensesList();
  const filtered = licenses.filter(licenseMatchesFilters);

  // Medium pill counters ignore the medium filter itself, so both numbers stay visible
  const savedMedium = state.filterMedium;
  state.filterMedium = 'all';
  const mediumBase = licenses.filter(licenseMatchesFilters);
  state.filterMedium = savedMedium;
  const elSoft = document.getElementById('cntMediumSoft');
  if (elSoft) elSoft.textContent = mediumBase.filter(l => l.medium === 'Программная').length;
  const elHard = document.getElementById('cntMediumHard');
  if (elHard) elHard.textContent = mediumBase.filter(l => l.medium === 'Аппаратная').length;

  const activeSeats = filtered.filter(l => l.isActive !== false).reduce((sum, l) => sum + (l.totalCapacity || 0), 0);
  const servers = state.overview?.licenseServers || [];
  const allHosts = new Set(servers.map(sv => serverKeyOf(sv.host, sv.ip)));
  const hostsWithLicenses = new Set(servers.filter(sv => sv.status === 'Ok').map(sv => serverKeyOf(sv.host, sv.ip)));
  // One row per physical placement: a key seen in several ports is listed under each hub/port separately
  const rows = filtered.flatMap(lic => (lic.placements?.length ? lic.placements.map(pl => ({ lic, pl })) : [{ lic, pl: null }]));
  elChipTotalCatalog.textContent = `${rows.length} ${pluralRu(rows.length, 'ПОЗИЦИЯ', 'ПОЗИЦИИ', 'ПОЗИЦИЙ')}`;
  const elSeats = document.getElementById('chipCatalogSeats');
  if (elSeats) elSeats.textContent = `МЕСТ: ${activeSeats}`;
  const elServers = document.getElementById('chipCatalogServers');
  if (elServers) {
    elServers.textContent = `СЕРВЕРОВ: ${hostsWithLicenses.size} / ${allHosts.size}`;
    elServers.title = `Серверов лицензирования с лицензиями: ${hostsWithLicenses.size}, всего найдено: ${allHosts.size}`;
  }

  if (filtered.length === 0) {
    elTbodyCatalog.innerHTML = `<tr><td colspan="8" class="table-empty-cell">НЕТ ЛИЦЕНЗИЙ ПО ВЫБРАННЫМ КРИТЕРИЯМ</td></tr>`;
    return;
  }

  elTbodyCatalog.innerHTML = rows
    .map(r => ({ ...r, key: catalogSortKey(r.lic, r.pl) }))
    .sort((a, b) => a.key.localeCompare(b.key))
    .map(r => renderCatalogRow(r.lic, r.pl))
    .join('');
}

window.selectPortRow = function(deviceId, portNumber) {
  if (!state.overview) return;
  const port = state.overview.allPorts.find(p => p.deviceId === deviceId && p.portNumber === portNumber);
  if (port) openDrawer(port);
};

function openDrawer(port) {
  state.selectedPort = port;

  document.getElementById('drawerPortTitle').textContent = `ПОРТ ${port.portNumber < 10 ? '0' + port.portNumber : port.portNumber} (${port.groupName.toUpperCase()})`;
  document.getElementById('drawerDeviceSubtitle').textContent = port.deviceName.toUpperCase();

  document.getElementById('detHub').textContent = port.deviceName;
  document.getElementById('detPort').textContent = port.portNumber;
  document.getElementById('detGroup').textContent = (port.groupName || 'Unassigned').toUpperCase();
  document.getElementById('detStatus').textContent = port.status.toUpperCase();

  const clientHost = (port.connectedClientHost || '').trim();
  if (clientHost) {
    document.getElementById('detClientHost').innerHTML = `<span class="host-plain cell-mono">${escapeHtml(clientHost)}</span>`;
  } else {
    document.getElementById('detClientHost').textContent = '—';
  }
  document.getElementById('detClientIp').textContent = port.connectedClientIp || '—';

  document.getElementById('detManufacturer').textContent = port.hardwareManufacturer || '—';
  document.getElementById('detProduct').textContent = port.hardwareProduct || (port.hasDevice ? 'USB Key' : '—');
  document.getElementById('detUsbVer').textContent = port.usbVersion || '—';
  document.getElementById('detSerial').textContent = port.serialNumber || '—';

  const lic = port.licenseInfo;
  document.getElementById('detCategory').textContent = lic?.category || '—';
  document.getElementById('detLicProduct').textContent = lic?.productName || '—';
  document.getElementById('detDongleId').textContent = lic?.dongleId || '—';
  document.getElementById('detRegNo').textContent = lic?.registrationNumber || '—';
  document.getElementById('detLicType').textContent = lic?.licenseType || '—';
  document.getElementById('detCapacity').innerHTML = formatLicenseCount(lic);
  document.getElementById('detOrg').textContent = lic?.organization || '—';

  const procSection = document.getElementById('drawerProcessesSection');
  const procList = document.getElementById('detProcessesList');
  if (lic?.activeProcesses && lic.activeProcesses.length > 0) {
    procSection.style.display = 'flex';
    procList.innerHTML = lic.activeProcesses.map(p => `<li class="process-item">${escapeHtml(p)}</li>`).join('');
  } else {
    procSection.style.display = 'none';
  }

  drawerBackdrop.classList.add('open');
  detailDrawer.classList.add('open');
}

function closeDrawer() {
  state.selectedPort = null;
  drawerBackdrop.classList.remove('open');
  detailDrawer.classList.remove('open');
}

function exportToCsv() {
  if (!state.overview) return;

  if (state.activeView === 'catalog') {
    const licenses = getDiscoveredLicensesList();
    const headers = ['Категория', 'Продукт / Модуль', 'Расположение / Digi', 'ID / Донгл', 'Регистрационный №', 'Тип', 'Количество лицензий', 'Организация'];
    const rows = licenses.map(l => [
      l.category || '',
      l.productName || '',
      l.location || '',
      l.dongleId || '',
      l.registrationNumber || '',
      l.licenseType || '',
      l.totalCapacity ? (l.totalCapacity + ' польз.') : (l.licenseType || ''),
      l.organization || ''
    ]);

    const csvContent = '\uFEFF' + [headers, ...rows]
      .map(row => row.map(val => `"${String(val).replace(/"/g, '""')}"`).join(';'))
      .join('\r\n');

    downloadBlob(csvContent, '1C_License_Catalog_Export.csv', 'text/csv;charset=utf-8;');
    return;
  }

  const headers = ['Устройство', 'USB Порт', 'Группа', 'Хост клиента', 'IP клиента', 'Производитель ключа', 'Модель ключа', 'Категория', 'Продукт 1С', 'ID ключа', 'Рег.№', 'Лицензий', 'Статус'];
  
  const rows = state.overview.allPorts.map(p => [
    p.deviceName,
    p.portLabel,
    p.groupName,
    p.connectedClientHost,
    p.connectedClientIp,
    p.hardwareManufacturer,
    p.hardwareProduct,
    p.licenseInfo?.category || '',
    p.licenseInfo?.productName || '',
    p.licenseInfo?.dongleId || '',
    p.licenseInfo?.registrationNumber || '',
    p.licenseInfo?.totalCapacity || '',
    p.status
  ]);

  const csvContent = '\uFEFF' + [headers, ...rows]
    .map(row => row.map(val => `"${String(val).replace(/"/g, '""')}"`).join(';'))
    .join('\r\n');

  downloadBlob(csvContent, '1C_License_Hub_Export.csv', 'text/csv;charset=utf-8;');
}

function exportToJson() {
  if (!state.overview) return;
  const json = JSON.stringify(state.overview, null, 2);
  downloadBlob(json, '1C_License_Hub_Telemetry.json', 'application/json;charset=utf-8;');
}

function downloadBlob(content, filename, contentType) {
  const blob = new Blob([content], { type: contentType });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

function escapeHtml(str) {
  if (!str) return '';
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

// Universal Table Cell Matching on Hover
let activeHoverMatch = null;

document.addEventListener('mouseover', (e) => {
  const td = e.target.closest('td[data-match]');
  if (!td) return;
  const matchKey = td.getAttribute('data-match');
  if (!matchKey || matchKey === activeHoverMatch) return;

  clearCellHoverHighlights();
  activeHoverMatch = matchKey;

  const table = td.closest('table');
  if (!table) return;

  const allTds = table.querySelectorAll('td[data-match]');
  allTds.forEach(cell => {
    if (cell.getAttribute('data-match') === matchKey) {
      if (cell === td) {
        cell.classList.add('cell-hover-source');
      } else {
        cell.classList.add('cell-match-highlight');
      }
    }
  });
});

document.addEventListener('mouseout', (e) => {
  const td = e.target.closest('td[data-match]');
  if (!td) return;
  if (e.relatedTarget && td.contains(e.relatedTarget)) return;

  clearCellHoverHighlights();
  activeHoverMatch = null;
});

function clearCellHoverHighlights() {
  document.querySelectorAll('.cell-match-highlight, .cell-hover-source').forEach(el => {
    el.classList.remove('cell-match-highlight', 'cell-hover-source');
  });
}

