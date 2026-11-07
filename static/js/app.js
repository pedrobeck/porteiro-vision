// VISION - DASHBOARD CONTROLLER

window.cameras = [];
let currentLayout = "auto";
let focusedCameraId = null;
let pollInterval = null;
let currentAuthMode = "login"; // 'login' ou 'register'
let isHubOnline = false;

document.addEventListener("DOMContentLoaded", async () => {
  initTheme();
  initLayoutControls();
  initModal();
  
  // Conecta ao Hub local diretamente
  await initHubStatus();

  await loadCameras();
  startStatusPolling();
  registerPWA();
});

// --- PWA SERVICE WORKER ---
function registerPWA() {
  if ("serviceWorker" in navigator) {
    if (isLocalEnvironment()) {
      // No ambiente local de casa (PC/Tablet), desregistra Service Worker para eliminar cache e lentidão
      navigator.serviceWorker.getRegistrations().then((registrations) => {
        for (const reg of registrations) {
          reg.unregister();
        }
      });
      if ("caches" in window) {
        caches.keys().then((keys) => {
          keys.forEach((k) => caches.delete(k));
        });
      }
    } else {
      navigator.serviceWorker.register("/sw.js").catch(() => {});
    }
  }
}

// --- STREAM HUB URL & CLOUDFLARE TUNNEL (MODO HÍBRIDO AUTOMÁTICO) ---
function isLocalEnvironment() {
  const host = window.location.hostname;
  return host === "localhost" || host === "127.0.0.1" || host.startsWith("192.168.") || host.startsWith("10.");
}

function getStreamBaseUrl() {
  if (isLocalEnvironment()) {
    return "";
  }
  const hub = localStorage.getItem("stream_hub_url") || "";
  return hub.replace(/\/+$/, "");
}

async function initHubStatus() {
  const hubPill = document.getElementById("hubStatusPill");
  const localIpPill = document.getElementById("localIpPill");
  const hubDot = document.getElementById("hubDot");
  const hubText = document.getElementById("hubStatusText");

  if (!hubPill) return;

  isHubOnline = true;
  hubPill.style.display = "inline-flex";
  if (hubDot) hubDot.className = "hub-dot online";
  if (hubText) hubText.textContent = "Rede Local (0ms)";
  if (localIpPill) localIpPill.style.display = "flex";
}

async function autoSyncHubFromSupabase() {
  return true;
}

function openHubConfigModal() {
  const input = document.getElementById("hubTunnelUrl");
  if (input) input.value = localStorage.getItem("stream_hub_url") || "";
  const alertBox = document.getElementById("hubTestAlert");
  if (alertBox) alertBox.style.display = "none";
  const modal = document.getElementById("hubModal");
  if (modal) modal.classList.add("open");
}

function closeHubModal() {
  const modal = document.getElementById("hubModal");
  if (modal) modal.classList.remove("open");
}

async function testHubConnection() {
  const input = document.getElementById("hubTunnelUrl");
  const url = input ? input.value.trim().replace(/\/+$/, "") : "";
  const alertBox = document.getElementById("hubTestAlert");

  if (!url) {
    alertBox.className = "test-alert error";
    alertBox.textContent = "✕ Informe a URL do túnel (ex: https://xxx.trycloudflare.com)";
    alertBox.style.display = "block";
    return;
  }

  alertBox.className = "test-alert";
  alertBox.textContent = "Testando conexão com o seu Hub Local...";
  alertBox.style.display = "block";

  try {
    const res = await fetch(`${url}/api/status`, { mode: "cors" });
    if (res.ok) {
      const data = await res.json();
      alertBox.className = "test-alert success";
      const camCount = data.cameras ? data.cameras.length : 0;
      alertBox.textContent = `✓ Hub conectado com sucesso! (${camCount} câmera(s) ativa(s))`;
    } else {
      throw new Error(`HTTP ${res.status}`);
    }
  } catch (err) {
    alertBox.className = "test-alert error";
    alertBox.textContent = "✕ Falha ao conectar no Hub. Verifique se o start.bat está rodando no PC.";
  }
}

async function saveHubConfig() {
  const input = document.getElementById("hubTunnelUrl");
  const url = input ? input.value.trim().replace(/\/+$/, "") : "";
  if (url && !url.startsWith("http://") && !url.startsWith("https://")) {
    alert("A URL deve começar com https:// ou http://");
    return;
  }

  localStorage.setItem("stream_hub_url", url);
  closeHubModal();
  renderGrid();
}

// --- LAYOUT SWITCHER ---
function initLayoutControls() {
  const buttons = document.querySelectorAll(".layout-btn");
  buttons.forEach((btn) => {
    btn.addEventListener("click", () => {
      buttons.forEach((b) => b.classList.remove("active"));
      btn.classList.add("active");
      const layout = btn.dataset.layout;
      setLayout(layout);
    });
  });
}

function setLayout(layout) {
  currentLayout = layout;
  const grid = document.getElementById("cameraGrid");
  grid.className = `camera-grid layout-${layout}`;
  unfocusCamera();

  // Mantém os fluxos ativos sem recarregar ou travar telas ao mudar de layout
}

// --- CARREGAMENTO E RENDERIZAÇÃO DAS CÂMERAS ---
async function loadCameras(forceFullRender = false) {
  try {
    const baseUrl = getStreamBaseUrl();
    const res = await fetch(`${baseUrl}/api/cameras`);
    const data = await res.json();
    if (data && Array.isArray(data)) {
      const prevIds = (window.cameras || []).map((c) => c.id).join(",");
      const newIds = data.map((c) => c.id).join(",");
      window.cameras = data;
      
      const grid = document.getElementById("cameraGrid");
      const hasCells = grid && grid.querySelector(".camera-cell");
      
      if (forceFullRender || prevIds !== newIds || !hasCells) {
        renderGrid();
      } else {
        updateExistingGridLabels(data);
      }
    }
  } catch (err) {
    console.error("Erro ao carregar câmeras:", err);
  }
}

function updateExistingGridLabels(cameras) {
  cameras.forEach((cam) => {
    const cell = document.querySelector(`.camera-cell[data-id="${cam.id}"]`);
    if (cell) {
      const nameEl = cell.querySelector(".camera-name");
      if (nameEl) {
        const isSubLens = !!cam.is_sub_lens;
        nameEl.innerHTML = `${escapeHtml(cam.name)}${isSubLens ? `<span class="badge-sublens">FIXA</span>` : ''}`;
      }
      const indicator = cell.querySelector(".status-indicator");
      if (indicator && cam.status) {
        indicator.className = `status-indicator ${cam.status.toLowerCase()}`;
      }
    }
  });
}

function startStatusPolling() {
  const fetchStatus = async () => {
    try {
      const baseUrl = getStreamBaseUrl();
      if (!isLocalEnvironment() && !isHubOnline) return;
      const res = await fetch(`${baseUrl}/api/status`, { mode: "cors" });
      const data = await res.json();
      if (data && data.cameras) {
        updateCameraStatuses(data.cameras);
      }
      if (data && data.system) {
        updateSystemStats(data.system);
      }
    } catch (err) {}
  };

  fetchStatus();
  if (pollInterval) clearInterval(pollInterval);
  pollInterval = setInterval(fetchStatus, 3000);
}

function updateSystemStats(sys) {
  if (!sys) return;

  const statCpu = document.getElementById("statCpu");
  const statRam = document.getElementById("statRam");
  const statNet = document.getElementById("statNet");
  const pillCpu = document.getElementById("pillCpu");
  const pillRam = document.getElementById("pillRam");

  if (statCpu && sys.cpu_percent !== undefined) {
    statCpu.textContent = `${sys.cpu_percent}%`;
    if (pillCpu) {
      pillCpu.className = sys.cpu_percent >= 50 ? "stat-pill danger" : (sys.cpu_percent >= 25 ? "stat-pill warn" : "stat-pill");
    }
  }

  if (statRam) {
    const ramDisplay = sys.ram_str || (sys.ram_used_mb ? `${sys.ram_used_mb} MB` : (sys.ram_used_gb ? `${Math.round(sys.ram_used_gb * 1024)} MB` : ""));
    if (ramDisplay) {
      statRam.textContent = ramDisplay;
    }
    if (pillRam) {
      const mb = sys.ram_used_mb || (sys.ram_used_gb ? sys.ram_used_gb * 1024 : 0);
      pillRam.className = mb >= 2048 ? "stat-pill danger" : (mb >= 1024 ? "stat-pill warn" : "stat-pill");
    }
  }

  if (statNet) {
    const down = sys.net_down_str || "0 KB/s";
    const up = sys.net_up_str || "0 KB/s";
    statNet.textContent = `↓ ${down} / ↑ ${up}`;
  }
}

function updateCameraStatuses(statuses) {
  if (!statuses) return;
  statuses.forEach((st) => {
    if (window.cameras) {
      const cam = window.cameras.find((c) => c.id === st.id);
      if (cam) {
        cam.status = st.status;
        cam.fps = st.fps;
      }
    }
    const cell = document.querySelector(`.camera-cell[data-id="${st.id}"]`);
    if (cell) {
      const indicator = cell.querySelector(".status-indicator");
      if (indicator) {
        indicator.className = `status-indicator ${st.status.toLowerCase()}`;
      }

      // Indicador REC de gravação ativa na câmera (Fase 3)
      let recBadge = cell.querySelector(".cam-rec-badge");
      if (st.recording) {
        if (!recBadge) {
          recBadge = document.createElement("div");
          recBadge.className = "cam-rec-badge";
          recBadge.innerHTML = '<span class="cam-rec-dot"></span> REC';
          cell.appendChild(recBadge);
        }
      } else if (recBadge) {
        recBadge.remove();
      }
    }
  });

  if (typeof onStandbyStatusUpdated === "function") {
    onStandbyStatusUpdated();
  }
}

function renderGrid() {
  const grid = document.getElementById("cameraGrid");
  grid.innerHTML = "";

  if (!window.cameras || window.cameras.length === 0) {
    grid.innerHTML = `
      <div class="empty-state">
        <div class="empty-icon">📹</div>
        <h2>Nenhuma câmera cadastrada</h2>
        <p>Clique em "+ Adicionar Câmera" acima para conectar sua primeira câmera (iCSee, Intelbras, Tapo, RTSP).</p>
        <button class="btn-primary" onclick="openAddCameraModal()" style="margin-top: 10px;">
          + Cadastrar Câmera
        </button>
      </div>
    `;
    return;
  }

  const baseUrl = getStreamBaseUrl();
  const needsHubConfig = !isLocalEnvironment() && (!baseUrl || !isHubOnline);

  window.cameras.forEach((cam) => {
    const cell = document.createElement("div");
    cell.className = "camera-cell";
    cell.dataset.id = cam.id;

    const streamUrl = `${baseUrl}/api/stream/${cam.id}`;
    const wsUrl = `/api/ws?src=${encodeURIComponent(cam.id + '_sub')}`;

    let feedHtml = "";
    if (needsHubConfig) {
      feedHtml = `
        <div class="feed-wrapper" style="cursor: pointer; display: flex; align-items: center; justify-content: center; background: rgba(10, 15, 25, 0.9);" onclick="openHubConfigModal()">
          <div style="text-align: center; padding: 24px;">
            <div style="font-size: 36px; margin-bottom: 8px;">📡</div>
            <div style="font-size: 15px; font-weight: 700; color: #fff;">Computador de Casa Offline</div>
            <div style="font-size: 12px; color: var(--accent-cyan); margin-top: 6px; line-height: 1.4;">
              Inicie o <code>start.bat</code> no seu PC para transmitir automaticamente.
            </div>
            <div style="font-size: 10px; color: var(--text-muted); margin-top: 10px;">
              Clique para checar conexão ou configurar manualmente
            </div>
          </div>
        </div>
      `;
    } else {
      const snapUrl = `${baseUrl}/api/snapshot/${cam.id}?t=${Date.now()}`;
      feedHtml = `
        <div class="feed-wrapper" ondblclick="toggleFocusCamera('${cam.id}', event)">
          <img class="feed-ambient" id="ambient-${cam.id}" src="${snapUrl}" alt="" aria-hidden="true">
          <img class="feed-poster" id="poster-${cam.id}" src="${snapUrl}" alt="${escapeHtml(cam.name)}">
          <porteiro-stream class="feed-video" data-id="${cam.id}" data-quality="sub" src="${wsUrl}" style="position: relative; z-index: 2; opacity: 0; transition: opacity 0.3s ease;"></porteiro-stream>
          <img class="feed-img fallback-feed" style="display: none; position: absolute; top: 0; left: 0; width: 100%; height: 100%; z-index: 3;" data-src="${streamUrl}" alt="${escapeHtml(cam.name)}" loading="lazy">
          <div class="stream-protocol-badge rtc" id="proto-${cam.id}">RTC</div>
        </div>
      `;
    }

    const isMulti = !!cam.multi_lens;
    const isSubLens = !!cam.is_sub_lens;
    const multiLensBtn = (cam.brand === "icsee" && !isSubLens) ? `
      <button class="action-icon-btn btn-multilens ${isMulti ? 'active-multilens' : ''}" 
              title="${isMulti ? 'Clique para alternar para 1 Lente (Principal)' : 'Clique para ativar as 3 Lentes Simultâneas (Móvel + 2 Fixas)'}" 
              onclick="toggleMultiLens('${cam.id}', event)">
        👁️ ${isMulti ? '3 Lentes' : '1 Lente'}
      </button>
    ` : '';

    cell.innerHTML = `
      <div class="cell-header">
        <div class="cell-title-box">
          <div class="status-indicator ${cam.status ? cam.status.toLowerCase() : 'connecting'}"></div>
          <div class="camera-name">
            ${escapeHtml(cam.name)}
            ${isSubLens ? `<span class="badge-sublens">FIXA</span>` : ''}
          </div>
        </div>
        <div class="cell-actions">
          ${multiLensBtn}
          <button class="cell-lupa-btn" title="Ampliar Câmera (Foco)" onclick="toggleFocusCamera('${cam.id}', event)">🔍</button>
          ${!isSubLens ? `<button class="action-icon-btn btn-settings" title="Configurar" onclick="openEditCameraModal('${cam.id}', event)">⚙️</button>` : ''}
        </div>
      </div>
      ${feedHtml}
      ${(cam.ptz_enabled && !isSubLens) ? renderPtzJoystick(cam.id) : ""}
    `;

    const pStream = cell.querySelector("porteiro-stream");
    const fallbackImg = cell.querySelector(".fallback-feed");
    const badge = cell.querySelector(`#proto-${cam.id}`);

    if (pStream && fallbackImg) {
      let rtcTimeout = setTimeout(() => {
        if (pStream.style.display !== "none" && (!pStream.video || pStream.video.readyState === 0)) {
          console.warn(`[Stream] Timeout WebRTC para ${cam.id}, ativando fallback MJPEG`);
          pStream.style.display = "none";
          fallbackImg.src = fallbackImg.dataset.src;
          fallbackImg.style.display = "block";
          if (badge) {
            badge.textContent = "MJPEG";
            badge.className = "stream-protocol-badge mjpeg";
          }
        }
      }, 12000);

      pStream.addEventListener("streamconnected", (ev) => {
        clearTimeout(rtcTimeout);
        pStream.style.opacity = "1";
        const posterEl = cell.querySelector(`#poster-${cam.id}`);
        if (posterEl) {
          posterEl.style.zIndex = "0";
        }
        const mode = ev.detail?.mode || "RTC";
        if (badge) {
          badge.textContent = mode;
          badge.className = "stream-protocol-badge rtc";
        }
        pStream.style.display = "block";
        fallbackImg.style.display = "none";
        console.log(`[Stream] WebRTC conectado com sucesso para ${cam.id}!`);
      });

      pStream.addEventListener("streamfailed", () => {
        clearTimeout(rtcTimeout);
        pStream.style.display = "none";
        fallbackImg.src = fallbackImg.dataset.src;
        fallbackImg.style.display = "block";
        if (badge) {
          badge.textContent = "MJPEG";
          badge.className = "stream-protocol-badge mjpeg";
        }
      });
    }

    cell.addEventListener("click", (e) => {
      if (e.target.closest("button") || e.target.closest(".ptz-joystick") || needsHubConfig) return;
      
      // Se a célula já estiver em foco (tela cheia), não precisa alternar
      if (cell.classList.contains("focused")) return;

      // Ao clicar na imagem, alterna a exibição da Lupa e configurações
      const isShowing = cell.classList.contains("show-lupa");
      document.querySelectorAll(".camera-cell.show-lupa").forEach(c => c.classList.remove("show-lupa"));
      if (!isShowing) {
        cell.classList.add("show-lupa");
      }
    });

    grid.appendChild(cell);
  });
}

function renderPtzJoystick(camId) {
  return `
    <div class="ptz-joystick" style="display: none;" id="ptz-${camId}">
      <div></div>
      <button class="ptz-btn" onmousedown="sendPtz('${camId}', 'up')" onmouseup="sendPtz('${camId}', 'stop')" ontouchstart="sendPtz('${camId}', 'up')" ontouchend="sendPtz('${camId}', 'stop')">▲</button>
      <div></div>
      <button class="ptz-btn" onmousedown="sendPtz('${camId}', 'left')" onmouseup="sendPtz('${camId}', 'stop')" ontouchstart="sendPtz('${camId}', 'left')" ontouchend="sendPtz('${camId}', 'stop')">◀</button>
      <button class="ptz-btn" onclick="sendPtz('${camId}', 'stop')">⏹</button>
      <button class="ptz-btn" onmousedown="sendPtz('${camId}', 'right')" onmouseup="sendPtz('${camId}', 'stop')" ontouchstart="sendPtz('${camId}', 'right')" ontouchend="sendPtz('${camId}', 'stop')">▶</button>
      <div></div>
      <button class="ptz-btn" onmousedown="sendPtz('${camId}', 'down')" onmouseup="sendPtz('${camId}', 'stop')" ontouchstart="sendPtz('${camId}', 'down')" ontouchend="sendPtz('${camId}', 'stop')">▼</button>
      <div></div>
    </div>
  `;
}

// --- DUAL-STREAM: CONTROLE DE QUALIDADE (SD Contínuo vs HD sob demanda) ---
function setStreamQuality(camId, quality) {
  const cell = document.querySelector(`.camera-cell[data-id="${camId}"]`);
  if (!cell) return;
  const pStream = cell.querySelector("porteiro-stream");
  if (!pStream) return;

  const currentQuality = pStream.dataset.quality || "sub";
  if (currentQuality === quality) return;

  pStream.dataset.quality = quality;
  const suffix = quality === "hd" ? "_hd" : "_sub";
  const newSrc = `/api/ws?src=${encodeURIComponent(camId + suffix)}`;
  console.log(`[DualStream] Câmera ${camId} comutada para ${quality.toUpperCase()}`);
  pStream.src = newSrc;
}

// --- FOCO E TELA CHEIA ---
function toggleFocusCamera(camId, event) {
  if (event) event.stopPropagation();

  const cell = document.querySelector(`.camera-cell[data-id="${camId}"]`);
  if (!cell) return;

  if (focusedCameraId === camId) {
    unfocusCamera();
  } else {
    unfocusCamera();
    cell.classList.add("focused");
    focusedCameraId = camId;

    // EXPANSÃO 100% INSTANTÂNEA: Mantém o fluxo atual rodando liso sem tela preta nem atraso!
    // O usuário pode tocar no botão "SD/HD" no cabeçalho se desejar 1080p nítido sob demanda.

    const ptz = document.getElementById(`ptz-${camId}`);
    if (ptz) ptz.style.display = "grid";
  }
}

function unfocusCamera() {
  if (focusedCameraId) {
    const prevId = focusedCameraId;
    const old = document.querySelector(`.camera-cell[data-id="${prevId}"]`);
    if (old) {
      old.classList.remove("focused");
      old.classList.remove("show-lupa");
      const ptz = document.getElementById(`ptz-${prevId}`);
      if (ptz) ptz.style.display = "none";
    }
    focusedCameraId = null;
  }
}

// Alterna resolução SD (ultra-rápido/leve) vs HD (1080p nítido) sob demanda sem travar expansão
function toggleCameraQuality(camId, event) {
  if (event) event.stopPropagation();
  const cell = document.querySelector(`.camera-cell[data-id="${camId}"]`);
  if (!cell) return;
  const pStream = cell.querySelector("porteiro-stream");
  const btn = cell.querySelector(`#btn-quality-${camId}`);
  if (!pStream) return;

  const current = pStream.dataset.quality || "sub";
  const target = current === "sub" ? "hd" : "sub";

  if (btn) {
    btn.textContent = target === "hd" ? "HD ⏳" : "SD";
    btn.classList.toggle("active-hd", target === "hd");
  }

  setStreamQuality(camId, target);

  pStream.addEventListener("streamconnected", () => {
    if (btn) {
      btn.textContent = target.toUpperCase();
      btn.classList.toggle("active-hd", target === "hd");
    }
  }, { once: true });
}

function isFullscreen() {
  return !!(
    document.fullscreenElement ||
    document.webkitFullscreenElement ||
    document.mozFullScreenElement ||
    document.msFullscreenElement
  );
}

async function toggleFullscreen(event) {
  if (event && event.stopPropagation) event.stopPropagation();
  try {
    if (!isFullscreen()) {
      const el = document.documentElement;
      if (el.requestFullscreen) {
        await el.requestFullscreen();
      } else if (el.webkitRequestFullscreen) {
        await el.webkitRequestFullscreen();
      } else if (el.webkitRequestFullScreen) {
        await el.webkitRequestFullScreen();
      } else if (el.mozRequestFullScreen) {
        await el.mozRequestFullScreen();
      } else if (el.msRequestFullscreen) {
        await el.msRequestFullscreen();
      }
    } else {
      if (document.exitFullscreen) {
        await document.exitFullscreen();
      } else if (document.webkitExitFullscreen) {
        await document.webkitExitFullscreen();
      } else if (document.mozCancelFullScreen) {
        await document.mozCancelFullScreen();
      } else if (document.msExitFullscreen) {
        await document.msExitFullscreen();
      }
    }
  } catch (err) {
    console.warn("[Fullscreen] Falha ao alternar tela cheia:", err);
  }
}
window.toggleFullscreen = toggleFullscreen;

// --- SNAPSHOT & PTZ ---
function takeSnapshot(camId, event) {
  if (event) event.stopPropagation();
  const baseUrl = getStreamBaseUrl();
  const link = document.createElement("a");
  link.href = `${baseUrl}/api/snapshot/${camId}?t=${Date.now()}`;
  link.download = `vision_cam_${camId}_${Date.now()}.jpg`;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
}

async function sendPtz(camId, cmd) {
  const baseUrl = getStreamBaseUrl();
  try {
    await fetch(`${baseUrl}/api/cameras/${camId}/ptz`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ command: cmd, step: 4 })
    });
  } catch (err) {
    console.error("Erro PTZ:", err);
  }
}

// --- MODAL DE CADASTRO / EDIÇÃO DE CÂMERA ---
let editingCamId = null;

function initModal() {
  const modal = document.getElementById("cameraModal");
  const closeBtn = document.getElementById("closeModalBtn");
  const cancelBtn = document.getElementById("cancelModalBtn");
  const saveBtn = document.getElementById("saveCameraBtn");
  const testBtn = document.getElementById("testConnectionBtn");
  const brandSelect = document.getElementById("camBrand");

  closeBtn.onclick = closeModal;
  cancelBtn.onclick = closeModal;
  
  brandSelect.onchange = () => {
    const brand = brandSelect.value;
    const portInput = document.getElementById("camPort");
    const userInput = document.getElementById("camUser");
    const mlGroup = document.getElementById("groupMultiLens");
    if (mlGroup) {
      mlGroup.style.display = (brand === "icsee") ? "block" : "none";
    }
    if (brand === "icsee") {
      portInput.value = 554;
      if (!userInput.value) userInput.value = "admin";
    } else if (brand === "intelbras") {
      portInput.value = 554;
      if (!userInput.value) userInput.value = "admin";
    } else if (brand === "tapo") {
      portInput.value = 554;
    }
  };

  testBtn.onclick = testConnection;
  saveBtn.onclick = saveCameraForm;
}

function openAddCameraModal() {
  editingCamId = null;
  document.getElementById("modalTitle").textContent = "Cadastrar Nova Câmera";
  document.getElementById("cameraForm").reset();
  document.getElementById("camPort").value = 554;
  document.getElementById("camChannel").value = 1;
  document.getElementById("camUser").value = "admin";
  const mlGroup = document.getElementById("groupMultiLens");
  if (mlGroup) mlGroup.style.display = "none";
  const mlCheck = document.getElementById("camMultiLens");
  if (mlCheck) mlCheck.checked = false;
  const sensSelect = document.getElementById("camSensitivity");
  if (sensSelect) sensSelect.value = "medium";
  document.getElementById("testResult").className = "test-alert";
  document.getElementById("deleteCameraBtn").style.display = "none";
  document.getElementById("cameraModal").classList.add("open");
}

function openEditCameraModal(camId, event) {
  if (event) event.stopPropagation();
  editingCamId = camId;
  const cam = window.cameras.find((c) => c.id === camId);
  if (!cam) return;

  document.getElementById("modalTitle").textContent = "Configurar Câmera";
  document.getElementById("camName").value = cam.name || "";
  document.getElementById("camBrand").value = cam.brand || "generic";
  document.getElementById("camHost").value = cam.host || "";
  document.getElementById("camPort").value = cam.port || 554;
  document.getElementById("camChannel").value = cam.channel || 1;
  document.getElementById("camUser").value = cam.username || "";
  document.getElementById("camPassword").value = cam.password || "";
  document.getElementById("camStreamType").value = cam.stream_type || "sub";
  document.getElementById("camCustomUrl").value = cam.custom_url || "";
  document.getElementById("camPtzEnabled").checked = !!cam.ptz_enabled;

  const mlGroup = document.getElementById("groupMultiLens");
  if (mlGroup) {
    mlGroup.style.display = (cam.brand === "icsee") ? "block" : "none";
  }
  const mlCheck = document.getElementById("camMultiLens");
  if (mlCheck) {
    mlCheck.checked = !!cam.multi_lens;
  }
  const sensSelect = document.getElementById("camSensitivity");
  if (sensSelect) {
    sensSelect.value = cam.motion_sensitivity || "medium";
  }

  document.getElementById("testResult").className = "test-alert";
  const deleteBtn = document.getElementById("deleteCameraBtn");
  deleteBtn.style.display = "inline-block";
  deleteBtn.onclick = () => deleteCamera(camId);

  document.getElementById("cameraModal").classList.add("open");
}

function closeModal() {
  document.getElementById("cameraModal").classList.remove("open");
}

async function testConnection() {
  const testBox = document.getElementById("testResult");
  testBox.className = "test-alert";
  testBox.textContent = "Testando conexão com a câmera...";
  testBox.style.display = "block";

  const baseUrl = getStreamBaseUrl();
  if (!isLocalEnvironment() && !baseUrl) {
    testBox.className = "test-alert error";
    testBox.textContent = "✕ O computador de casa está offline. Inicie o start.bat no PC para testar.";
    return;
  }

  const data = getFormData();
  try {
    const res = await fetch(`${baseUrl}/api/cameras/test`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(data)
    });
    const result = await res.json();
    if (res.ok) {
      testBox.className = "test-alert success";
      testBox.textContent = "✓ " + (result.message || "Conexão estabelecida com sucesso!");
    } else {
      testBox.className = "test-alert error";
      testBox.textContent = "✕ " + (result.message || "Falha na conexão.");
    }
  } catch (err) {
    testBox.className = "test-alert error";
    testBox.textContent = "✕ Erro ao comunicar com o servidor: " + err.message;
  }
}

async function saveCameraForm() {
  const saveBtn = document.getElementById("saveCameraBtn");
  const data = getFormData();
  if (!data.host) {
    alert("Por favor, informe o IP ou endereço da câmera.");
    return;
  }

  if (saveBtn) {
    saveBtn.disabled = true;
    saveBtn.innerHTML = "⏳ Salvando...";
  }

  try {
    const baseUrl = getStreamBaseUrl();
    let res;
    if (editingCamId) {
      res = await fetch(`${baseUrl}/api/cameras/${editingCamId}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(data)
      });
      data.id = editingCamId;
    } else {
      res = await fetch(`${baseUrl}/api/cameras`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(data)
      });
    }

    if (res.ok) {
      closeModal();
      await loadCameras();
    } else {
      const err = await res.json();
      alert("Erro ao salvar: " + (err.error || "Tente novamente"));
    }
  } catch (err) {
    alert("Erro de conexão com o servidor: " + err.message);
  } finally {
    if (saveBtn) {
      saveBtn.disabled = false;
      saveBtn.innerHTML = "Salvar Câmera";
    }
  }
}

async function deleteCamera(camId) {
  if (!confirm("Tem certeza que deseja excluir esta câmera?")) return;
  try {
    const baseUrl = getStreamBaseUrl();
    const res = await fetch(`${baseUrl}/api/cameras/${camId}`, { method: "DELETE" });
    if (res.ok) {
      closeModal();
      await loadCameras();
    }
  } catch (err) {
    alert("Erro ao excluir câmera: " + err.message);
  }
}

function getFormData() {
  return {
    name: document.getElementById("camName").value,
    brand: document.getElementById("camBrand").value,
    host: document.getElementById("camHost").value,
    port: parseInt(document.getElementById("camPort").value) || 554,
    channel: parseInt(document.getElementById("camChannel").value) || 1,
    username: document.getElementById("camUser").value,
    password: document.getElementById("camPassword").value,
    stream_type: document.getElementById("camStreamType").value,
    custom_url: document.getElementById("camCustomUrl").value,
    ptz_enabled: document.getElementById("camPtzEnabled").checked,
    multi_lens: document.getElementById("camMultiLens") ? document.getElementById("camMultiLens").checked : false,
    motion_sensitivity: document.getElementById("camSensitivity") ? document.getElementById("camSensitivity").value : "medium"
  };
}

async function toggleMultiLens(camId, event) {
  if (event) event.stopPropagation();
  const btn = event?.currentTarget;
  if (btn) {
    btn.disabled = true;
    btn.style.opacity = "0.5";
  }

  try {
    const baseUrl = getStreamBaseUrl();
    const res = await fetch(`${baseUrl}/api/cameras/${camId}/toggle_multilens`, {
      method: "POST"
    });
    const data = await res.json();
    if (res.ok) {
      await loadCameras();
    } else {
      alert("Erro ao alternar lentes: " + (data.error || "Tente novamente"));
    }
  } catch (err) {
    alert("Erro de conexão: " + err.message);
  } finally {
    if (btn) {
      btn.disabled = false;
      btn.style.opacity = "1";
    }
  }
}

function escapeHtml(str) {
  if (!str) return "";
  return str.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

// ========================================================
// MODO STANDBY (PATRULHA EM TELA CHEIA ALEATÓRIA)
// ========================================================

let isStandbyActive = false;
let isStandbyPaused = false;
let standbyIntervalSec = 8;
let standbyTimerRemaining = 8;
let standbyTimerInterval = null;
let standbyCurrentCamId = null;
let standbyHistory = [];
let standbyIdleTimeout = null;
let standbyClockInterval = null;
let standbyFitMode = "contain"; // 'contain' ou 'cover'

function toggleStandbyMode() {
  if (isStandbyActive) {
    exitStandbyMode();
  } else {
    enterStandbyMode();
  }
}

// Pausa streams em segundo plano para liberar conexões e Wi-Fi no tablet
function pauseGridStreams() {
  document.querySelectorAll(".camera-cell porteiro-stream").forEach((st) => {
    try { st.ondisconnect(); } catch (e) {}
    st.removeAttribute("src");
  });
  document.querySelectorAll(".camera-cell .feed-img").forEach((img) => {
    img.removeAttribute("src");
    img.src = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";
  });
}

// Retoma os streams normais ao sair do Standby
function resumeGridStreams() {
  const baseUrl = getStreamBaseUrl();
  document.querySelectorAll(".camera-cell").forEach((cell) => {
    const camId = cell.dataset.id;
    const st = cell.querySelector("porteiro-stream");
    if (st && camId) {
      st.style.display = "block";
      st.dataset.quality = "sub";
      st.src = `/api/ws?src=${encodeURIComponent(camId + '_sub')}`;
    }
    const img = cell.querySelector(".feed-img");
    if (img && camId && img.style.display !== "none") {
      img.src = `${baseUrl}/api/stream/${camId}`;
    }
  });
}

function getFunctioningCameras() {
  if (!window.cameras || window.cameras.length === 0) return [];
  // Filtra câmeras com status ONLINE (funcionando)
  const functioning = window.cameras.filter((c) => c.status === "ONLINE");
  // Se ainda não houve polling de status inicial, considera câmeras não marcadas como offline
  if (functioning.length === 0) {
    const unconfirmed = window.cameras.filter((c) => c.status !== "OFFLINE" && c.status !== "ERROR");
    return unconfirmed;
  }
  return functioning;
}

async function enterStandbyMode() {
  const overlay = document.getElementById("standbyOverlay");
  const btn = document.getElementById("standbyBtn");
  if (!overlay) return;

  isStandbyActive = true;
  isStandbyPaused = false;
  standbyHistory = [];
  
  if (btn) btn.classList.add("active");
  overlay.style.display = "flex";
  overlay.classList.remove("idle", "cursor-hidden");

  // Pausa o grid de fundo para liberar conexões e acelerar o tablet
  pauseGridStreams();

  // Requisita tela cheia no navegador (ideal para tablets e TVs)
  try {
    if (!document.fullscreenElement) {
      if (overlay.requestFullscreen) {
        await overlay.requestFullscreen();
      } else if (document.documentElement.requestFullscreen) {
        await document.documentElement.requestFullscreen();
      }
    }
  } catch (err) {}

  // Inicializa o relógio digital
  startStandbyClock();

  // Configura os detectores de inatividade para auto-hide do HUD
  setupStandbyInactivityHandlers();

  // Escuta tecla ESC e saída de fullscreen
  document.addEventListener("keydown", handleStandbyKeyDown);
  document.addEventListener("fullscreenchange", handleStandbyFullscreenChange);

  // Inicializa a primeira câmera aleatória
  switchStandbyCamera(true);
}

function exitStandbyMode() {
  const overlay = document.getElementById("standbyOverlay");
  const btn = document.getElementById("standbyBtn");
  
  isStandbyActive = false;
  isStandbyPaused = false;
  
  if (btn) btn.classList.remove("active");
  if (overlay) {
    overlay.style.display = "none";
    overlay.classList.remove("idle", "cursor-hidden");
  }

  // Limpa timers
  clearInterval(standbyTimerInterval);
  clearInterval(standbyClockInterval);
  clearTimeout(standbyIdleTimeout);

  // Desconecta o stream do standby
  const feedImg = document.getElementById("standbyFeedImg");
  if (feedImg) {
    feedImg.onerror = null;
    feedImg.src = "";
  }
  const standbyVideo = document.getElementById("standbyFeedVideo");
  if (standbyVideo) {
    try { standbyVideo.ondisconnect(); } catch (e) {}
    standbyVideo.removeAttribute("src");
    standbyVideo.style.display = "none";
  }

  // Remove listeners
  document.removeEventListener("keydown", handleStandbyKeyDown);
  document.removeEventListener("fullscreenchange", handleStandbyFullscreenChange);

  // Retoma as câmeras normais do painel
  resumeGridStreams();

  // Sai da tela cheia se ainda estiver ativo
  if (document.fullscreenElement) {
    try {
      document.exitFullscreen().catch(() => {});
    } catch (e) {}
  }
}

function handleStandbyKeyDown(e) {
  if (!isStandbyActive) return;
  if (e.key === "Escape") {
    exitStandbyMode();
  } else if (e.key === "ArrowRight") {
    nextStandbyCamera();
  } else if (e.key === "ArrowLeft") {
    prevStandbyCamera();
  } else if (e.key === " " || e.key === "Spacebar") {
    togglePauseStandby();
  }
}

function handleStandbyFullscreenChange() {
  if (isStandbyActive && !document.fullscreenElement) {
    // Se o usuário saiu do modo de tela cheia (ex: apertou ESC nativo), fecha o standby
    exitStandbyMode();
  }
}

function setupStandbyInactivityHandlers() {
  const overlay = document.getElementById("standbyOverlay");
  if (!overlay) return;

  const resetIdle = () => {
    overlay.classList.remove("idle", "cursor-hidden");
    clearTimeout(standbyIdleTimeout);
    if (isStandbyActive) {
      standbyIdleTimeout = setTimeout(() => {
        if (isStandbyActive) {
          overlay.classList.add("idle", "cursor-hidden");
        }
      }, 3500);
    }
  };

  overlay.addEventListener("mousemove", resetIdle);
  overlay.addEventListener("touchstart", resetIdle, { passive: true });
  overlay.addEventListener("click", resetIdle);
  resetIdle();
}

function startStandbyClock() {
  const clockEl = document.getElementById("standbyClock");
  const dateEl = document.getElementById("standbyDate");

  const update = () => {
    const now = new Date();
    if (clockEl) {
      clockEl.textContent = now.toLocaleTimeString("pt-BR", { hour12: false });
    }
    if (dateEl) {
      dateEl.textContent = now.toLocaleDateString("pt-BR", {
        weekday: "long",
        day: "2-digit",
        month: "long"
      });
    }
  };

  update();
  clearInterval(standbyClockInterval);
  standbyClockInterval = setInterval(update, 1000);
}

function pickRandomFunctioningCamera(excludeId = null) {
  const cams = getFunctioningCameras();
  if (cams.length === 0) return null;
  if (cams.length === 1) return cams[0];

  // Filtra câmeras excluindo a atual para não repetir duas vezes seguidas
  const candidates = excludeId ? cams.filter((c) => c.id !== excludeId) : cams;
  const pool = candidates.length > 0 ? candidates : cams;
  const randomIndex = Math.floor(Math.random() * pool.length);
  return pool[randomIndex];
}

function switchStandbyCamera(isInitial = false) {
  const functioningCams = getFunctioningCameras();
  const emptyState = document.getElementById("standbyEmptyState");
  const counterEl = document.getElementById("standbyCamsCounter");
  const feedImg = document.getElementById("standbyFeedImg");

  if (counterEl) {
    counterEl.textContent = `${functioningCams.length} Câmera(s) Online em Rotação`;
  }

  if (functioningCams.length === 0) {
    if (emptyState) emptyState.style.display = "block";
    if (feedImg) feedImg.style.display = "none";
    standbyCurrentCamId = null;
    resetStandbyTimer();
    return;
  }

  if (emptyState) emptyState.style.display = "none";
  if (feedImg) feedImg.style.display = "block";

  const nextCam = pickRandomFunctioningCamera(standbyCurrentCamId);
  if (!nextCam) return;

  // Guarda histórico para o botão "Anterior"
  if (standbyCurrentCamId && standbyCurrentCamId !== nextCam.id) {
    standbyHistory.push(standbyCurrentCamId);
    if (standbyHistory.length > 20) standbyHistory.shift();
  }

  standbyCurrentCamId = nextCam.id;
  updateStandbyHud(nextCam);

  const standbyVideo = document.getElementById("standbyFeedVideo");
  if (standbyVideo) {
    standbyVideo.style.display = "none";
    standbyVideo.src = `/api/ws?src=${encodeURIComponent(nextCam.id + '_sub')}`;
    standbyVideo.addEventListener("streamconnected", () => {
      standbyVideo.style.display = "block";
    }, { once: true });
  }

  if (feedImg) {
    const baseUrl = getStreamBaseUrl();
    const streamUrl = `${baseUrl}/api/stream/${nextCam.id}?t=${Date.now()}`;

    // Efeito suave de transição sem apagar a imagem anterior
    feedImg.classList.add("switching");

    // Tratamento de erro resiliente (se der timeout no tablet, pula automaticamente)
    feedImg.onerror = () => {
      console.warn("[Standby] Erro ou timeout na câmera. Tentando próxima ativa...");
      if (isStandbyActive) {
        setTimeout(switchStandbyCamera, 1000);
      }
    };

    // A imagem anterior permanece na tela até o novo fluxo carregar os primeiros quadros!
    feedImg.src = streamUrl;

    setTimeout(() => {
      if (feedImg) feedImg.classList.remove("switching");
    }, 300);
  }

  resetStandbyTimer();
}

function updateStandbyHud(cam) {
  if (!cam) return;
  const nameEl = document.getElementById("standbyCamName");
  const brandEl = document.getElementById("standbyBrandPill");
  const metaEl = document.getElementById("standbyCamMeta");

  if (nameEl) nameEl.textContent = cam.name || "Câmera";
  if (brandEl) brandEl.textContent = (cam.brand || "RTSP").toUpperCase();
  if (metaEl) {
    metaEl.textContent = `AO VIVO • ${cam.fps || 0} FPS`;
  }
}

function onStandbyStatusUpdated() {
  if (!isStandbyActive) return;

  const functioningCams = getFunctioningCameras();
  const counterEl = document.getElementById("standbyCamsCounter");
  if (counterEl) {
    counterEl.textContent = `${functioningCams.length} Câmera(s) Online em Rotação`;
  }

  // Se a câmera atualmente exibida caiu (ficou OFFLINE ou sumiu do status), avança imediatamente
  if (standbyCurrentCamId) {
    const currentCam = window.cameras.find((c) => c.id === standbyCurrentCamId);
    if (!currentCam || currentCam.status !== "ONLINE") {
      console.log("[Standby] Câmera atual perdeu sinal. Avançando para próxima ativa...");
      switchStandbyCamera();
      return;
    }
    // Atualiza FPS dinâmico no HUD
    const metaEl = document.getElementById("standbyCamMeta");
    if (metaEl) {
      metaEl.textContent = `AO VIVO • ${currentCam.fps || 0} FPS`;
    }
  } else if (functioningCams.length > 0) {
    // Estava sem câmeras e alguma voltou a ficar online
    switchStandbyCamera();
  }
}

function resetStandbyTimer() {
  clearInterval(standbyTimerInterval);
  standbyTimerRemaining = standbyIntervalSec;
  updateStandbyProgressBar(100);

  if (isStandbyPaused) return;

  const stepMs = 100;
  const totalSteps = (standbyIntervalSec * 1000) / stepMs;
  let currentStep = totalSteps;

  standbyTimerInterval = setInterval(() => {
    if (!isStandbyActive || isStandbyPaused) return;

    currentStep--;
    const percent = Math.max(0, (currentStep / totalSteps) * 100);
    updateStandbyProgressBar(percent);

    if (currentStep <= 0) {
      clearInterval(standbyTimerInterval);
      switchStandbyCamera();
    }
  }, stepMs);
}

function updateStandbyProgressBar(percent) {
  const bar = document.getElementById("standbyProgressBar");
  if (bar) {
    bar.style.width = `${percent}%`;
  }
}

function nextStandbyCamera() {
  switchStandbyCamera();
}

function prevStandbyCamera() {
  if (standbyHistory.length === 0) {
    switchStandbyCamera();
    return;
  }
  const prevId = standbyHistory.pop();
  const prevCam = window.cameras.find((c) => c.id === prevId && c.status === "ONLINE");
  if (!prevCam) {
    switchStandbyCamera();
    return;
  }

  standbyCurrentCamId = prevCam.id;
  updateStandbyHud(prevCam);

  const standbyVideo = document.getElementById("standbyFeedVideo");
  if (standbyVideo) {
    standbyVideo.style.display = "block";
    standbyVideo.src = `/api/ws?src=${encodeURIComponent(prevCam.id)}`;
  }

  const feedImg = document.getElementById("standbyFeedImg");
  if (feedImg) {
    const baseUrl = getStreamBaseUrl();
    feedImg.classList.add("switching");
    feedImg.src = `${baseUrl}/api/stream/${prevCam.id}?t=${Date.now()}`;
    setTimeout(() => {
      if (feedImg) feedImg.classList.remove("switching");
    }, 300);
  }

  resetStandbyTimer();
}

function togglePauseStandby() {
  isStandbyPaused = !isStandbyPaused;
  const btn = document.getElementById("standbyPauseBtn");
  if (btn) {
    btn.textContent = isStandbyPaused ? "▶" : "⏸";
    btn.title = isStandbyPaused ? "Retomar rotação automática" : "Pausar rotação";
  }
  if (!isStandbyPaused) {
    resetStandbyTimer();
  }
}

function changeStandbyInterval(val) {
  const sec = parseInt(val) || 8;
  standbyIntervalSec = sec;
  resetStandbyTimer();
}

function toggleStandbyFit() {
  const stage = document.getElementById("standbyStage");
  const btn = document.getElementById("standbyFitBtn");
  if (!stage) return;

  if (standbyFitMode === "contain") {
    standbyFitMode = "cover";
    stage.classList.add("fit-cover");
    if (btn) btn.textContent = "⛶ Preencher";
  } else {
    standbyFitMode = "contain";
    stage.classList.remove("fit-cover");
    if (btn) btn.textContent = "⛶ Ajustar";
  }
}

// ========================================================
// MODO NOTURNO INTELIGENTE (TELA ESCURA COM AUTO-WAKE)
// ========================================================

let isNightModeActive = false;
let isNightAwake = false;
let nightSelectedCamId = "ALL";
let nightDurationSec = 30;
let nightSoundEnabled = true;
let nightRecordEnabled = true;
let nightPollInterval = null;
let nightWakeTimer = null;
let nightClockInterval = null;
let nightPosterInterval = null;
let nightAudioCtx = null;

function selectDurationPill(val) {
  document.querySelectorAll(".vigia-duration-pill").forEach(p => {
    if (p.getAttribute("data-val") === String(val)) {
      p.classList.add("active");
    } else {
      p.classList.remove("active");
    }
  });
  const sel = document.getElementById("nightDurationSelect");
  if (sel) sel.value = String(val);
}

function openNightModeModal() {
  const modal = document.getElementById("nightModeModal");
  const select = document.getElementById("nightCameraSelect");
  if (!modal || !select) return;

  // Preenche opções de câmeras
  select.innerHTML = "";
  
  // Opção Qualquer Câmera
  const allOpt = document.createElement("option");
  allOpt.value = "ALL";
  allOpt.textContent = "🔍 Qualquer Câmera (Todas com Movimento)";
  select.appendChild(allOpt);

  let garageId = null;
  if (window.cameras && window.cameras.length > 0) {
    window.cameras.forEach((cam) => {
      const opt = document.createElement("option");
      opt.value = cam.id;
      opt.textContent = `📹 ${cam.name} (${(cam.brand || "RTSP").toUpperCase()})`;
      // Prioriza a câmera principal da garagem (evita selecionar acidentalmente sub-lente Fixa 2)
      if (cam.name.toLowerCase().includes("garagem") && (!garageId || !cam.is_sub_lens)) {
        garageId = cam.id;
      }
      select.appendChild(opt);
    });
  }

  // Recupera preferências salvas ou pré-seleciona a Garagem
  const savedCam = localStorage.getItem("night_cam_id") || garageId || "ALL";
  select.value = savedCam;

  const savedDuration = localStorage.getItem("night_duration") || "30";
  const durationSelect = document.getElementById("nightDurationSelect");
  if (durationSelect) durationSelect.value = savedDuration;
  selectDurationPill(savedDuration);

  const savedSound = localStorage.getItem("night_sound");
  const soundCheck = document.getElementById("nightSoundEnabled");
  if (soundCheck && savedSound !== null) {
    soundCheck.checked = savedSound === "true";
  }

  const savedSens = localStorage.getItem("night_sensitivity") || "medium";
  const sensSelect = document.getElementById("nightSensitivitySelect");
  if (sensSelect) sensSelect.value = savedSens;

  const savedRecord = localStorage.getItem("night_record_enabled");
  const recordCheck = document.getElementById("nightRecordEnabled");
  if (recordCheck) {
    recordCheck.checked = savedRecord !== null ? (savedRecord === "true") : true;
  }

  const savedToast = localStorage.getItem("night_toast_enabled");
  const toastCheck = document.getElementById("nightToastEnabled");
  if (toastCheck) {
    toastCheck.checked = savedToast !== null ? (savedToast === "true") : true;
  }

  modal.classList.add("open");
}

function closeNightModeModal() {
  const modal = document.getElementById("nightModeModal");
  if (modal) modal.classList.remove("open");
}

async function startNightModeFromModal() {
  const select = document.getElementById("nightCameraSelect");
  const durationSelect = document.getElementById("nightDurationSelect");
  const soundCheck = document.getElementById("nightSoundEnabled");
  const sensSelect = document.getElementById("nightSensitivitySelect");

  nightSelectedCamId = select ? select.value : "ALL";
  nightDurationSec = durationSelect ? (parseInt(durationSelect.value) || 30) : 30;
  nightSoundEnabled = soundCheck ? soundCheck.checked : true;
  const recordCheck = document.getElementById("nightRecordEnabled");
  nightRecordEnabled = recordCheck ? recordCheck.checked : true;
  const toastCheck = document.getElementById("nightToastEnabled");
  const nightToastEnabled = toastCheck ? toastCheck.checked : true;
  const nightSensitivity = sensSelect ? sensSelect.value : "medium";

  localStorage.setItem("night_cam_id", nightSelectedCamId);
  localStorage.setItem("night_duration", nightDurationSec);
  localStorage.setItem("night_sound", nightSoundEnabled);
  localStorage.setItem("night_record_enabled", nightRecordEnabled);
  localStorage.setItem("night_toast_enabled", nightToastEnabled);
  localStorage.setItem("night_sensitivity", nightSensitivity);

  // Sincroniza configuração de notificações Windows
  try {
    const baseUrl = getStreamBaseUrl();
    await fetch(`${baseUrl}/api/notifications/toggle`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled: nightToastEnabled })
    });
  } catch { }

  // Aplica sensibilidade imediatamente no backend
  try {
    const baseUrl = getStreamBaseUrl();
    await fetch(`${baseUrl}/api/motion/config`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        sensitivity: nightSensitivity,
        camera_id: (nightSelectedCamId !== "ALL" ? nightSelectedCamId : null)
      })
    });
  } catch (err) {
    console.warn("[NightMode] Erro ao sincronizar sensibilidade:", err);
  }

  closeNightModeModal();
  enterNightMode();
}

async function enterNightMode() {
  const overlay = document.getElementById("nightOverlay");
  const btn = document.getElementById("nightModeBtn");
  if (!overlay) return;

  isNightModeActive = true;
  isNightAwake = false;

  if (btn) btn.classList.add("active");
  overlay.style.display = "flex";

  // Atualiza etiqueta da câmera monitorada com a sensibilidade ativa
  const labelEl = document.getElementById("nightMonitoredCamLabel");
  if (labelEl) {
    const savedSens = localStorage.getItem("night_sensitivity") || "medium";
    const sensLabel = savedSens === "low" ? "Baixa" : (savedSens === "high" ? "Alta" : "Média");
    if (nightSelectedCamId === "ALL") {
      labelEl.textContent = `Todas as Câmeras • Sensibilidade: ${sensLabel}`;
    } else {
      const cam = (window.cameras || []).find((c) => c.id === nightSelectedCamId);
      const camName = cam ? cam.name : "Câmera Selecionada";
      labelEl.textContent = `${camName} • Sensibilidade: ${sensLabel}`;
    }
  }

  // Pausa streams do painel para economizar Wi-Fi e bateria no tablet
  pauseGridStreams();

  // Atualiza etiqueta de gravação (REC) no painel de descanso noturno
  const recPill = document.getElementById("nightSleepRecPill");
  if (recPill) {
    if (nightRecordEnabled) {
      recPill.textContent = "REC: ATIVO";
      recPill.style.color = "var(--brand)";
      recPill.style.borderColor = "rgba(61, 214, 245, 0.3)";
      recPill.style.background = "rgba(61, 214, 245, 0.15)";
    } else {
      recPill.textContent = "REC: DESATIVADO";
      recPill.style.color = "var(--text-muted)";
      recPill.style.borderColor = "rgba(255, 255, 255, 0.1)";
      recPill.style.background = "rgba(255, 255, 255, 0.05)";
    }
  }

  // Notifica a central para desligar o RTSP das outras câmeras e configurar se deve gravar eventos
  try {
    const baseUrl = getStreamBaseUrl();
    fetch(`${baseUrl}/api/night_mode/enter`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        camera_id: nightSelectedCamId,
        record_events: nightRecordEnabled
      })
    }).catch(() => {});
  } catch (err) {}

  // Requisita tela cheia
  try {
    if (!document.fullscreenElement) {
      if (overlay.requestFullscreen) {
        await overlay.requestFullscreen();
      } else if (document.documentElement.requestFullscreen) {
        await document.documentElement.requestFullscreen();
      }
    }
  } catch (err) {}

  // Inicia relógio noturno
  startNightClock();

  // Garante estado de descanso
  forceNightSleep();

  // Inicia verificação rápida de movimento (a cada 750ms)
  startNightMotionPolling();

  // Teclado ESC para sair
  document.addEventListener("keydown", handleNightKeyDown);
  document.addEventListener("fullscreenchange", handleNightFullscreenChange);
}

function exitNightMode() {
  const overlay = document.getElementById("nightOverlay");
  const btn = document.getElementById("nightModeBtn");

  isNightModeActive = false;
  isNightAwake = false;

  if (btn) btn.classList.remove("active");
  if (overlay) overlay.style.display = "none";

  // Limpa timers
  clearInterval(nightPollInterval);
  clearInterval(nightWakeTimer);
  clearInterval(nightClockInterval);
  clearInterval(nightPosterInterval);

  // Limpa stream e poster do alerta
  const feed = document.getElementById("nightWakeFeedImg");
  const poster = document.getElementById("nightWakePosterImg");
  if (feed) {
    feed.removeAttribute("src");
    feed.dataset.camId = "";
  }
  if (poster) {
    poster.removeAttribute("src");
  }
  const wakeVideo = document.getElementById("nightWakeVideo");
  if (wakeVideo) {
    try { wakeVideo.ondisconnect(); } catch (e) {}
    wakeVideo.removeAttribute("src");
    wakeVideo.style.display = "none";
  }

  // Remove listeners
  document.removeEventListener("keydown", handleNightKeyDown);
  document.removeEventListener("fullscreenchange", handleNightFullscreenChange);

  // Retoma as câmeras do painel
  resumeGridStreams();

  // Notifica a central para retomar todas as câmeras
  try {
    const baseUrl = getStreamBaseUrl();
    fetch(`${baseUrl}/api/night_mode/exit`, { method: "POST" }).catch(() => {});
  } catch (err) {}

  // Sai de tela cheia
  if (document.fullscreenElement) {
    try {
      document.exitFullscreen().catch(() => {});
    } catch (e) {}
  }
}

function handleNightKeyDown(e) {
  if (!isNightModeActive) return;
  if (e.key === "Escape") {
    exitNightMode();
  }
}

function handleNightFullscreenChange() {
  if (isNightModeActive && !document.fullscreenElement) {
    exitNightMode();
  }
}

function startNightClock() {
  const clockEl = document.getElementById("nightSleepClock");
  const dateEl = document.getElementById("nightSleepDate");

  const update = () => {
    const now = new Date();
    if (clockEl) {
      clockEl.textContent = now.toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit", hour12: false });
    }
    if (dateEl) {
      dateEl.textContent = now.toLocaleDateString("pt-BR", {
        weekday: "long",
        day: "2-digit",
        month: "long"
      });
    }
  };

  update();
  clearInterval(nightClockInterval);
  nightClockInterval = setInterval(update, 1000);
}

function forceNightSleep() {
  isNightAwake = false;
  clearInterval(nightWakeTimer);
  clearInterval(nightPosterInterval);

  const sleepView = document.getElementById("nightSleepView");
  const wakeView = document.getElementById("nightWakeView");
  const feed = document.getElementById("nightWakeFeedImg");
  const poster = document.getElementById("nightWakePosterImg");

  if (sleepView) sleepView.style.display = "flex";
  if (wakeView) wakeView.style.display = "none";
  if (feed) {
    feed.removeAttribute("src");
    feed.dataset.camId = "";
  }
  if (poster) {
    poster.removeAttribute("src");
  }
  const wakeVideo = document.getElementById("nightWakeVideo");
  if (wakeVideo) {
    try { wakeVideo.ondisconnect(); } catch (e) {}
    wakeVideo.removeAttribute("src");
    wakeVideo.style.display = "none";
  }
}

function startNightMotionPolling() {
  clearInterval(nightPollInterval);
  let nightNetSampleCounter = 0;

  const checkMotion = async () => {
    if (!isNightModeActive) return;
    try {
      const baseUrl = getStreamBaseUrl();

      // Amostra telemetria de tráfego de rede a cada 3 segundos
      nightNetSampleCounter++;
      if (nightNetSampleCounter % 4 === 0) {
        try {
          const sRes = await fetch(`${baseUrl}/api/status?t=${Date.now()}`);
          if (sRes.ok) {
            const sData = await sRes.json();
            const netTextEl = document.getElementById("nightSleepNetText");
            if (netTextEl && sData.system) {
              const camDown = sData.system.net_down_str || "0 KB/s";
              const tabletUp = isNightAwake ? (sData.system.net_up_str || "0 KB/s") : "0 KB/s (Em Repouso)";
              netTextEl.textContent = `Câmera ↓ ${camDown} • Tablet ↑ ${tabletUp}`;
            }
          }
        } catch (_) {}
      }

      const res = await fetch(`${baseUrl}/api/motion?t=${Date.now()}`);
      if (!res.ok) return;
      const motionData = await res.json();

      let triggeredCamId = null;
      let triggeredCamName = "";

      if (nightSelectedCamId === "ALL") {
        for (const [cid, data] of Object.entries(motionData)) {
          if (data.motion === true) {
            triggeredCamId = cid;
            triggeredCamName = data.name;
            break;
          }
        }
      } else {
        const camData = motionData[nightSelectedCamId];
        if (camData && camData.motion === true) {
          triggeredCamId = nightSelectedCamId;
          triggeredCamName = camData.name;
        } else {
          // Se a câmera monitorada for a pai de multi-lentes (ex: garagem), checa também as sub-lentes fixas
          for (const subIdx of [1, 2]) {
            const subId = `${nightSelectedCamId}_sub${subIdx}`;
            const subData = motionData[subId];
            if (subData && subData.motion === true) {
              triggeredCamId = nightSelectedCamId; // Desperta na câmera principal
              triggeredCamName = camData ? camData.name : subData.name;
              break;
            }
          }
        }
      }

      if (triggeredCamId) {
        triggerNightWake(triggeredCamId, triggeredCamName);
      }
    } catch (err) {}
  };

  checkMotion();
  nightPollInterval = setInterval(checkMotion, 750);
}

function triggerNightWake(camId, camName) {
  const sleepView = document.getElementById("nightSleepView");
  const wakeView = document.getElementById("nightWakeView");
  let feed = document.getElementById("nightWakeFeedImg");
  let poster = document.getElementById("nightWakePosterImg");
  const nameEl = document.getElementById("nightAlertCamName");

  // SE JÁ ESTÁ ACORDADO: renova o tempo de vigília e nunca interrompe a transmissão ativa!
  if (isNightAwake) {
    resetNightWakeCountdown();
    return;
  }

  isNightAwake = true;
  if (nameEl) nameEl.textContent = (camName || "GARAGEM").toUpperCase();

  const wakeRecBadge = document.getElementById("nightWakeRecBadge");
  if (wakeRecBadge) {
    wakeRecBadge.style.display = nightRecordEnabled ? "inline-flex" : "none";
  }

  if (sleepView) sleepView.style.display = "none";
  if (wakeView) wakeView.style.display = "flex";

  const baseUrl = getStreamBaseUrl();
  const snapshotUrl = `${baseUrl}/api/snapshot/${camId}?t=${Date.now()}`;
  const streamUrl = `${baseUrl}/api/stream/${camId}?wake=${Date.now()}`;

  // Se o poster não existir no DOM (ex: cache de versão antiga), cria dinamicamente
  if (!poster && wakeView) {
    poster = document.createElement("img");
    poster.id = "nightWakePosterImg";
    poster.className = "night-wake-feed-img";
    poster.alt = "Foto Alerta";
    poster.style.zIndex = "1";
    wakeView.insertBefore(poster, feed);
  }

  // 1. Snapshot instantâneo (Renderiza em ~20ms da memória do Hub, eliminando 100% da tela preta!)
  if (poster) {
    poster.src = snapshotUrl;
    poster.style.display = "block";
    poster.style.zIndex = "2";
  }

  // 2. Conecta stream WebRTC H.264 prioritário
  const wakeVideo = document.getElementById("nightWakeVideo");
  if (wakeVideo) {
    // Mantém vídeo oculto até conectar para que o usuário veja o snapshot imediato sem tela preta nem ícone de play!
    wakeVideo.style.display = "none";
    wakeVideo.style.zIndex = "3";

    wakeVideo.addEventListener("streamconnected", () => {
      console.log("[NightWake] WebRTC conectado com sucesso, revelando vídeo ao vivo!");
      wakeVideo.style.display = "block";
      if (poster) poster.style.zIndex = "1";
    }, { once: true });

    wakeVideo.addEventListener("streamfailed", () => {
      console.warn("[NightWake] WebRTC falhou, ativando fallback MJPEG");
      wakeVideo.style.display = "none";
      if (feed) {
        feed.style.display = "block";
        feed.style.zIndex = "3";
        feed.src = streamUrl;
      }
    }, { once: true });

    wakeVideo.src = `/api/ws?src=${encodeURIComponent(camId + '_sub')}`;
  } else if (feed) {
    feed.style.display = "block";
    feed.style.zIndex = "3";
    feed.style.background = "transparent";
    feed.dataset.camId = camId;
    feed.src = streamUrl;

    feed.onerror = () => {
      if (isNightAwake && poster) {
        poster.src = `${baseUrl}/api/snapshot/${camId}?t=${Date.now()}`;
      }
    };
  }

  // 3. Fallback contínuo de fotos: atualiza o poster enquanto o vídeo ao vivo não estiver visível
  clearInterval(nightPosterInterval);
  nightPosterInterval = setInterval(() => {
    if (isNightAwake && poster && isNightModeActive && (!wakeVideo || wakeVideo.style.display === "none")) {
      poster.src = `${baseUrl}/api/snapshot/${camId}?t=${Date.now()}`;
    } else if (!isNightAwake || !isNightModeActive) {
      clearInterval(nightPosterInterval);
    }
  }, 1500);

  // Toca alerta sonoro suave se habilitado
  if (nightSoundEnabled) {
    playNightChime();
  }

  resetNightWakeCountdown();
}

function resetNightWakeCountdown() {
  clearInterval(nightWakeTimer);

  const totalMs = nightDurationSec * 1000;
  let remainingMs = totalMs;
  const stepMs = 100;

  const countEl = document.getElementById("nightWakeCountdown");
  const barEl = document.getElementById("nightProgressBar");

  if (countEl) countEl.textContent = nightDurationSec;
  if (barEl) barEl.style.width = "100%";

  nightWakeTimer = setInterval(() => {
    if (!isNightModeActive || !isNightAwake) {
      clearInterval(nightWakeTimer);
      return;
    }

    remainingMs -= stepMs;
    const secLeft = Math.ceil(remainingMs / 1000);
    const percent = Math.max(0, (remainingMs / totalMs) * 100);

    if (countEl) countEl.textContent = secLeft;
    if (barEl) barEl.style.width = `${percent}%`;

    if (remainingMs <= 0) {
      clearInterval(nightWakeTimer);
      console.log("[Modo Noturno] Tempo esgotado. Retornando ao descanso escuro...");
      forceNightSleep();
    }
  }, stepMs);
}

// Sintetizador Web Audio API: Chime suave de radar / segurança sem precisar de arquivo externo
function playNightChime() {
  try {
    if (!nightAudioCtx) {
      nightAudioCtx = new (window.AudioContext || window.webkitAudioContext)();
    }
    if (nightAudioCtx.state === "suspended") {
      nightAudioCtx.resume();
    }

    const now = nightAudioCtx.currentTime;

    // Primeiro tom suave (880 Hz - Nota A5)
    const osc1 = nightAudioCtx.createOscillator();
    const gain1 = nightAudioCtx.createGain();
    osc1.type = "sine";
    osc1.frequency.setValueAtTime(880, now);
    gain1.gain.setValueAtTime(0.25, now);
    gain1.gain.exponentialRampToValueAtTime(0.001, now + 0.4);
    osc1.connect(gain1);
    gain1.connect(nightAudioCtx.destination);
    osc1.start(now);
    osc1.stop(now + 0.4);

    // Segundo tom harmônico (1320 Hz - Nota E6)
    const osc2 = nightAudioCtx.createOscillator();
    const gain2 = nightAudioCtx.createGain();
    osc2.type = "sine";
    osc2.frequency.setValueAtTime(1320, now + 0.12);
    gain2.gain.setValueAtTime(0.25, now + 0.12);
    gain2.gain.exponentialRampToValueAtTime(0.001, now + 0.65);
    osc2.connect(gain2);
    gain2.connect(nightAudioCtx.destination);
    osc2.start(now + 0.12);
    osc2.stop(now + 0.65);
  } catch (err) {}
}

// Recarrega o app / câmeras com feedback visual (útil para Tablet/APK)
function refreshApp() {
  const btn = document.getElementById('reloadBtn');
  if (btn) {
    btn.classList.add('loading');
    const txt = btn.querySelector('.reload-text');
    if (txt) txt.textContent = 'Carregando...';
  }

  // Se estiver em modo noturno ou standby, sai limpo antes de recarregar
  try {
    if (typeof exitNightMode === 'function') exitNightMode();
  } catch (e) {}

  setTimeout(() => {
    window.location.reload(true);
  }, 200);
}

window.refreshApp = refreshApp;


// --- GESTÃO DE TEMAS VISUAIS / FUNDO ---
const THEMES = [
  { id: 'vision', label: 'Fundo: Vision' },
  { id: 'oled', label: 'Fundo: OLED' },
  { id: 'carbon', label: 'Fundo: Carbono' },
  { id: 'graphite', label: 'Fundo: Grafite' },
  { id: 'midnight', label: 'Fundo: Meia-Noite' }
];

function initTheme() {
  const saved = localStorage.getItem("vision_theme") || localStorage.getItem("porteiro_theme") || "vision";
  applyTheme(saved);
}

function applyTheme(themeId) {
  document.documentElement.setAttribute("data-theme", themeId);
  if (document.body) document.body.setAttribute("data-theme", themeId);
  localStorage.setItem("vision_theme", themeId);
  const label = document.getElementById("themePickerLabel");
  const drawerTheme = document.getElementById("drawerThemeLabel");
  const found = THEMES.find(t => t.id === themeId);
  if (label && found) {
    label.textContent = found.label;
  }
  if (drawerTheme && found) {
    drawerTheme.textContent = `Ativo: ${found.label.replace('Fundo: ', '')}`;
  }
}

function cycleTheme() {
  const current = localStorage.getItem("vision_theme") || localStorage.getItem("porteiro_theme") || "vision";
  const idx = THEMES.findIndex(t => t.id === current);
  const next = THEMES[(idx + 1) % THEMES.length];
  applyTheme(next.id);
  const cleanName = next.label.replace('Fundo: ', '');
  if (typeof showToast === "function") {
    showToast(`🎨 Tema: ${cleanName}`, 2200);
  }
}
window.cycleTheme = cycleTheme;
window.applyTheme = applyTheme;

// --- MENU SANDUÍCHE (MODO EM PÉ / RETRATO) ---
function toggleMobileMenu() {
  const drawer = document.getElementById("mobileDrawer");
  const backdrop = document.getElementById("mobileDrawerBackdrop");
  if (!drawer) return;
  const isOpen = drawer.classList.contains("open");
  if (isOpen) {
    closeMobileMenu();
  } else {
    drawer.classList.add("open");
    if (backdrop) backdrop.classList.add("open");
    updateThemeDrawerLabel();
  }
}

function closeMobileMenu() {
  const drawer = document.getElementById("mobileDrawer");
  const backdrop = document.getElementById("mobileDrawerBackdrop");
  if (drawer) drawer.classList.remove("open");
  if (backdrop) backdrop.classList.remove("open");
}

function updateThemeDrawerLabel() {
  const current = localStorage.getItem("vision_theme") || "vision";
  const found = THEMES.find(t => t.id === current);
  const drawerTheme = document.getElementById("drawerThemeLabel");
  if (drawerTheme && found) {
    drawerTheme.textContent = `Ativo: ${found.label.replace('Fundo: ', '')}`;
  }
}

window.toggleMobileMenu = toggleMobileMenu;
window.closeMobileMenu = closeMobileMenu;
window.updateThemeDrawerLabel = updateThemeDrawerLabel;

// Alterna entre Ajustar na Tela (contain) e Preencher 100% da Tela sem bordas (cover)
function toggleCameraFill(camId, event) {
  if (event) event.stopPropagation();
  const cell = document.querySelector(`.camera-cell[data-id="${camId}"]`);
  if (!cell) return;
  cell.classList.toggle("fill-screen");
  const isFilled = cell.classList.contains("fill-screen");
  const btn = cell.querySelector(`#btn-fill-${camId}`);
  if (btn) {
    btn.classList.toggle("active-fill", isFilled);
    btn.style.color = isFilled ? "var(--accent-cyan)" : "";
    btn.style.borderColor = isFilled ? "var(--accent-cyan)" : "";
    btn.title = isFilled ? "Modo: Preencher Tela (clique para Ajustar)" : "Modo: Ajustar na Tela (clique para Preencher)";
  }
}


/* ========================================================
   FASE 3: CONTROLLER DE GRAVAÇÕES & EVENTOS (HISTÓRICO)
   ======================================================== */

let currentEvents = [];
let activePlayerEvent = null;

async function openEventsModal() {
  const modal = document.getElementById("eventsModal");
  if (!modal) return;
  modal.style.display = "flex";

  // Preenche opções de filtro com as câmeras ativas
  const select = document.getElementById("eventsCamFilter");
  if (select && window.cameras) {
    const curVal = select.value;
    select.innerHTML = '<option value="ALL">📹 Todas as Câmeras</option>';
    window.cameras.forEach(c => {
      const opt = document.createElement("option");
      opt.value = c.id;
      opt.textContent = `📹 ${c.name}`;
      select.appendChild(opt);
    });
    if (curVal) select.value = curVal;
  }

  await loadEventsList();
}

function closeEventsModal() {
  const modal = document.getElementById("eventsModal");
  if (modal) modal.style.display = "none";
}

async function loadEventsList() {
  const loading = document.getElementById("eventsLoadingState");
  const empty = document.getElementById("eventsEmptyState");
  const grid = document.getElementById("eventsGrid");
  const badge = document.getElementById("eventsSummaryBadge");
  const filter = document.getElementById("eventsCamFilter")?.value || "ALL";

  if (loading) loading.style.display = "block";
  if (empty) empty.style.display = "none";
  if (grid) grid.innerHTML = "";

  try {
    const baseUrl = getHubBaseUrl();
    const url = `${baseUrl}/api/recordings?camera_id=${encodeURIComponent(filter)}&limit=100&t=${Date.now()}`;
    const res = await fetch(url);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);

    const events = await res.json();
    currentEvents = events || [];

    if (loading) loading.style.display = "none";

    // Atualiza contadores
    const count = currentEvents.length;
    let totalBytes = currentEvents.reduce((acc, ev) => acc + (ev.size_bytes || 0), 0);
    const sizeStr = formatBytes(totalBytes);

    if (badge) {
      badge.textContent = `${count} gravação(ões) • ${sizeStr}`;
    }

    // Badge no botão superior e no menu sanduíche
    ["eventsCountBadge", "mobileEventsBadge", "drawerEventsBadge"].forEach(id => {
      const el = document.getElementById(id);
      if (el) {
        if (count > 0) {
          el.style.display = "inline-block";
          el.textContent = count > 99 ? "99+" : count;
        } else {
          el.style.display = "none";
        }
      }
    });

    if (count === 0) {
      if (empty) empty.style.display = "block";
      return;
    }

    // Renderiza cards de eventos
    currentEvents.forEach(ev => {
      const card = createEventCard(ev);
      grid.appendChild(card);
    });
  } catch (err) {
    console.error("[Events] Erro ao carregar eventos:", err);
    if (loading) loading.style.display = "none";
    if (empty) {
      empty.style.display = "block";
      empty.querySelector("h4").textContent = "Erro ao carregar gravações";
      empty.querySelector("p").textContent = err.message;
    }
  }
}

function createEventCard(ev) {
  const card = document.createElement("div");
  card.className = "event-card";

  const baseUrl = getHubBaseUrl();
  const thumbUrl = `${baseUrl}/api/recordings/thumb/${ev.thumb_file}`;
  const videoUrl = `${baseUrl}/api/recordings/video/${ev.video_file}`;

  card.innerHTML = `
    <div class="event-thumb-box" onclick="playEvent('${ev.id}')">
      <img src="${thumbUrl}" class="event-thumb-img" alt="${ev.camera_name}" onerror="this.src='/static/favicon-32.png'; this.style.objectFit='contain';">
      <div class="event-play-overlay">
        <div class="event-play-icon">▶</div>
      </div>
      <div class="event-cam-tag">${ev.camera_name}</div>
      <div class="event-duration-tag">⏱️ ${ev.duration_sec || 15}s</div>
    </div>
    <div class="event-details">
      <div class="event-time-row">
        <span>📅 ${ev.date_str}</span>
        <span>🕒 ${ev.time_str}</span>
      </div>
      <div class="event-size-row">
        <span>🎬 Clipe H.264 (.MP4)</span>
        <span>💾 ${ev.size_str}</span>
      </div>
      <div class="event-actions-row">
        <button class="event-action-btn" onclick="playEvent('${ev.id}')" title="Assistir clipe">
          ▶️ Assistir
        </button>
        <a class="event-action-btn" href="${videoUrl}" download="${ev.camera_name}_${ev.date_str}_${ev.time_str}.mp4" title="Baixar clipe para o dispositivo">
          ⬇️ Baixar
        </a>
        <button class="event-action-btn btn-delete" onclick="deleteEvent('${ev.id}')" title="Excluir gravação">
          🗑️
        </button>
      </div>
    </div>
  `;

  return card;
}

function playEvent(id) {
  const ev = currentEvents.find(e => e.id === id);
  if (!ev) return;
  activePlayerEvent = ev;

  const modal = document.getElementById("eventPlayerModal");
  const title = document.getElementById("eventPlayerTitle");
  const video = document.getElementById("eventPlayerVideo");
  const downloadBtn = document.getElementById("eventPlayerDownloadBtn");
  const deleteBtn = document.getElementById("eventPlayerDeleteBtn");

  if (!modal || !video) return;

  const baseUrl = getHubBaseUrl();
  const videoUrl = `${baseUrl}/api/recordings/video/${ev.video_file}`;

  title.textContent = `📹 ${ev.camera_name} • ${ev.date_str} às ${ev.time_str} (${ev.duration_sec || 15}s)`;
  video.src = videoUrl;
  video.currentTime = 0;
  video.play().catch(() => {});

  if (downloadBtn) {
    downloadBtn.href = videoUrl;
    downloadBtn.download = `${ev.camera_name}_${ev.date_str}_${ev.time_str}.mp4`;
  }

  if (deleteBtn) {
    deleteBtn.onclick = () => deleteEvent(ev.id, true);
  }

  modal.style.display = "flex";
}

function closeEventPlayer() {
  const modal = document.getElementById("eventPlayerModal");
  const video = document.getElementById("eventPlayerVideo");
  if (video) {
    video.pause();
    video.removeAttribute("src");
    video.load();
  }
  if (modal) modal.style.display = "none";
  activePlayerEvent = null;
}

async function deleteEvent(id, closePlayerAfter = false) {
  if (!confirm("Tem certeza que deseja excluir esta gravação de 15s?")) return;

  try {
    const baseUrl = getHubBaseUrl();
    const res = await fetch(`${baseUrl}/api/recordings/${id}`, { method: "DELETE" });
    if (res.ok) {
      if (closePlayerAfter) closeEventPlayer();
      await loadEventsList();
    } else {
      alert("Erro ao excluir gravação.");
    }
  } catch (err) {
    alert("Erro na requisição: " + err.message);
  }
}

async function triggerManualRecording(camId) {
  try {
    const baseUrl = getHubBaseUrl();
    const res = await fetch(`${baseUrl}/api/recordings/trigger/${camId}`, { method: "POST" });
    const data = await res.json();
    if (res.ok) {
      showToast(`🎬 ${data.message || "Gravação iniciada!"}`);
    } else {
      showToast(`⚠️ ${data.error || "Não foi possível gravar"}`);
    }
  } catch (err) {
    showToast("⚠️ Erro ao iniciar gravação manual");
  }
}

function showToast(message, duration = 3500) {
  let toastEl = document.getElementById("visionWebToast");
  if (!toastEl) {
    toastEl = document.createElement("div");
    toastEl.id = "visionWebToast";
    toastEl.style.cssText = "position: fixed; bottom: 24px; right: 24px; background: #111C33; color: #EAF2FF; border: 1px solid #3DD6F5; box-shadow: 0 8px 32px rgba(0,0,0,0.45), 0 0 16px rgba(61,214,245,0.25); border-radius: 10px; padding: 12px 20px; font-size: 13px; font-weight: 600; z-index: 99999; display: flex; align-items: center; gap: 10px; transform: translateY(20px); opacity: 0; transition: all 0.25s ease; pointer-events: none;";
    document.body.appendChild(toastEl);
  }

  toastEl.innerHTML = message;
  toastEl.style.transform = "translateY(0)";
  toastEl.style.opacity = "1";

  if (window._visionToastTimer) clearTimeout(window._visionToastTimer);
  window._visionToastTimer = setTimeout(() => {
    toastEl.style.transform = "translateY(20px)";
    toastEl.style.opacity = "0";
  }, duration);
}

async function testToastNotification() {
  try {
    const btn = document.getElementById("notifTestBtn");
    if (btn) btn.style.opacity = "0.5";
    showToast("⏳ Disparando notificação nativa no Windows...");

    const baseUrl = getStreamBaseUrl();
    const res = await fetch(`${baseUrl}/api/notifications/test`, { method: "POST" });
    const data = await res.json();
    if (res.ok && data.success) {
      showToast("🔔 Notificação Toast enviada ao Windows com foto!");
    } else {
      showToast(`⚠️ ${data.message || data.error || "Falha ao disparar"}`);
    }
  } catch (err) {
    showToast("⚠️ Erro de conexão ao testar notificação: " + err.message);
  } finally {
    const btn = document.getElementById("notifTestBtn");
    if (btn) btn.style.opacity = "1";
  }
}

function formatBytes(bytes) {
  if (!bytes || bytes === 0) return "0 MB";
  if (bytes < 1024) return bytes + " B";
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + " KB";
  return (bytes / (1024 * 1024)).toFixed(1) + " MB";
}
