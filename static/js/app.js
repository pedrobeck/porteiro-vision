// PORTEIRO VISION - DASHBOARD CONTROLLER

window.cameras = [];
let currentLayout = "auto";
let focusedCameraId = null;
let pollInterval = null;
let currentAuthMode = "login"; // 'login' ou 'register'
let isHubOnline = false;

document.addEventListener("DOMContentLoaded", () => {
  initLayoutControls();
  initModal();
  initHubStatus();
  
  // Inicializa o Supabase (usa chaves salvas ou variáveis padrão)
  const isSbReady = initSupabase();
  if (!isSbReady) {
    console.log("[Supabase] Aguardando configuração de credenciais no modal.");
  }

  loadCameras();
  startStatusPolling();
  registerPWA();
});

// --- PWA SERVICE WORKER ---
function registerPWA() {
  if ("serviceWorker" in navigator) {
    navigator.serviceWorker.register("/sw.js").catch(() => {});
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

  if (isLocalEnvironment()) {
    // 1. DENTRO DE CASA: Modo Direto 0ms
    isHubOnline = true;
    hubPill.style.display = "inline-flex";
    if (hubDot) hubDot.className = "hub-dot online";
    if (hubText) hubText.textContent = "Rede Local (0ms)";
    if (localIpPill) localIpPill.style.display = "flex";
  } else {
    // 2. FORA DE CASA (VERCEL / 4G): Sincronização 100% Automática
    if (localIpPill) localIpPill.style.display = "none";
    hubPill.style.display = "inline-flex";
    
    // Busca túnel ativo do Supabase
    await autoSyncHubFromSupabase();

    // Re-verifica periodicamente para reconectar sozinho quando o PC ligar
    setInterval(autoSyncHubFromSupabase, 15000);
  }
}

async function autoSyncHubFromSupabase() {
  if (isLocalEnvironment()) return true;

  const hubDot = document.getElementById("hubDot");
  const hubText = document.getElementById("hubStatusText");

  try {
    const sbUrl = window.ENV_SUPABASE_URL || localStorage.getItem("sb_url");
    const sbKey = window.ENV_SUPABASE_ANON_KEY || localStorage.getItem("sb_anon_key");

    if (!sbUrl || !sbKey) return false;

    const res = await fetch(`${sbUrl}/rest/v1/hub_sync?id=eq.active_hub&select=*`, {
      headers: { "apikey": sbKey, "Authorization": `Bearer ${sbKey}` }
    });
    const data = await res.json();

    if (data && data.length > 0 && data[0].tunnel_url && data[0].is_online) {
      const liveTunnel = data[0].tunnel_url.replace(/\/+$/, "");
      const previousUrl = localStorage.getItem("stream_hub_url");
      localStorage.setItem("stream_hub_url", liveTunnel);

      // Testa se o túnel responde
      try {
        const testRes = await fetch(`${liveTunnel}/api/status`, { mode: "cors" });
        if (testRes.ok) {
          const wasOnline = isHubOnline;
          isHubOnline = true;
          if (hubDot) hubDot.className = "hub-dot online";
          if (hubText) hubText.textContent = "Hub Online (Auto)";
          
          if (!wasOnline || previousUrl !== liveTunnel) {
            renderGrid();
          }
          return true;
        }
      } catch (err) {}
    }

    // Se não conectou, marca offline
    const wasOnline = isHubOnline;
    isHubOnline = false;
    if (hubDot) hubDot.className = "hub-dot offline";
    if (hubText) hubText.textContent = "PC Offline (start.bat)";
    if (wasOnline) renderGrid();
    return false;
  } catch (err) {
    isHubOnline = false;
    if (hubDot) hubDot.className = "hub-dot offline";
    if (hubText) hubText.textContent = "PC Offline";
    return false;
  }
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
  await autoSyncHubFromSupabase();
  renderGrid();
}

// --- AUTH CONTROLS (SUPABASE) ---
async function handleAuthSubmit(e) {
  e.preventDefault();
  const email = document.getElementById("authEmail").value.trim();
  const password = document.getElementById("authPassword").value;
  const alertBox = document.getElementById("authAlert");
  const submitBtn = document.getElementById("authSubmitBtn");

  alertBox.className = "test-alert";
  alertBox.style.display = "none";
  submitBtn.disabled = true;
  submitBtn.textContent = "Verificando...";

  try {
    await supabaseSignIn(email, password);
    alertBox.className = "test-alert success";
    alertBox.textContent = "✓ Acesso autorizado!";
    alertBox.style.display = "block";
  } catch (err) {
    alertBox.className = "test-alert error";
    alertBox.textContent = "✕ " + (err.message || "Erro na autenticação. Verifique a senha.");
    alertBox.style.display = "block";
  } finally {
    submitBtn.disabled = false;
    submitBtn.textContent = "Entrar";
  }
}

function toggleSupabaseConfig() {
  const box = document.getElementById("supabaseConfigBox");
  box.style.display = box.style.display === "none" ? "block" : "none";
  document.getElementById("cfgSbUrl").value = localStorage.getItem("sb_url") || "";
  document.getElementById("cfgSbKey").value = localStorage.getItem("sb_anon_key") || "";
}

function saveCustomSupabaseConfig() {
  const url = document.getElementById("cfgSbUrl").value.trim();
  const key = document.getElementById("cfgSbKey").value.trim();
  if (url && key) {
    initSupabase(url, key);
    alert("Chaves do Supabase salvas com sucesso! Agora você pode criar conta ou entrar.");
    document.getElementById("supabaseConfigBox").style.display = "none";
  } else {
    alert("Informe a URL e a Anon Key do projeto Supabase.");
  }
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
}

// --- CARREGAMENTO E RENDERIZAÇÃO DAS CÂMERAS ---
async function loadCameras() {
  try {
    const res = await fetch("/api/cameras");
    window.cameras = await res.json();
    renderGrid();
  } catch (err) {
    console.error("Erro ao carregar câmeras:", err);
  }
}

function startStatusPolling() {
  pollInterval = setInterval(async () => {
    try {
      const baseUrl = getStreamBaseUrl();
      if (!isLocalEnvironment() && !isHubOnline) return;
      const res = await fetch(`${baseUrl}/api/status`, { mode: "cors" });
      const data = await res.json();
      updateCameraStatuses(data.cameras);
    } catch (err) {}
  }, 4000);
}

function updateCameraStatuses(statuses) {
  if (!statuses) return;
  statuses.forEach((st) => {
    const cell = document.querySelector(`.camera-cell[data-id="${st.id}"]`);
    if (cell) {
      const indicator = cell.querySelector(".status-indicator");
      const meta = cell.querySelector(".camera-meta");
      if (indicator) {
        indicator.className = `status-indicator ${st.status.toLowerCase()}`;
      }
      if (meta) {
        meta.textContent = `${st.status} | ${st.fps || 0} FPS`;
      }
    }
  });
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
      feedHtml = `
        <div class="feed-wrapper" ondblclick="toggleFullscreen(event)">
          <img class="feed-img" src="${streamUrl}" alt="${escapeHtml(cam.name)}" loading="lazy">
        </div>
      `;
    }

    cell.innerHTML = `
      <div class="cell-header">
        <div class="cell-title-box">
          <div class="status-indicator ${cam.status ? cam.status.toLowerCase() : 'connecting'}"></div>
          <div>
            <div class="camera-name">${escapeHtml(cam.name)}</div>
            <div class="camera-meta">${cam.status || "CONECTANDO"} | ${cam.fps || 0} FPS</div>
          </div>
        </div>
        <div class="cell-actions">
          <button class="action-icon-btn" title="Capturar Foto" onclick="takeSnapshot('${cam.id}', event)">📸</button>
          <button class="action-icon-btn" title="Ampliar / Tela Cheia" onclick="toggleFocusCamera('${cam.id}', event)">⛶</button>
          <button class="action-icon-btn" title="Configurar" onclick="openEditCameraModal('${cam.id}', event)">⚙️</button>
        </div>
      </div>
      ${feedHtml}
      ${cam.ptz_enabled ? renderPtzJoystick(cam.id) : ""}
    `;

    cell.addEventListener("click", (e) => {
      if (!e.target.closest("button") && !e.target.closest(".ptz-joystick") && !needsHubConfig) {
        toggleFocusCamera(cam.id);
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

    const ptz = document.getElementById(`ptz-${camId}`);
    if (ptz) ptz.style.display = "grid";
  }
}

function unfocusCamera() {
  if (focusedCameraId) {
    const old = document.querySelector(`.camera-cell[data-id="${focusedCameraId}"]`);
    if (old) {
      old.classList.remove("focused");
      const ptz = document.getElementById(`ptz-${focusedCameraId}`);
      if (ptz) ptz.style.display = "none";
    }
    focusedCameraId = null;
  }
}

function toggleFullscreen(event) {
  if (event) event.stopPropagation();
  if (!document.fullscreenElement) {
    document.documentElement.requestFullscreen().catch(() => {});
  } else {
    document.exitFullscreen().catch(() => {});
  }
}

// --- SNAPSHOT & PTZ ---
function takeSnapshot(camId, event) {
  if (event) event.stopPropagation();
  const baseUrl = getStreamBaseUrl();
  const link = document.createElement("a");
  link.href = `${baseUrl}/api/snapshot/${camId}?t=${Date.now()}`;
  link.download = `porteiro_cam_${camId}_${Date.now()}.jpg`;
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
  document.getElementById("camUser").value = "admin";
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
  document.getElementById("camUser").value = cam.username || "";
  document.getElementById("camPassword").value = cam.password || "";
  document.getElementById("camStreamType").value = cam.stream_type || "sub";
  document.getElementById("camCustomUrl").value = cam.custom_url || "";
  document.getElementById("camPtzEnabled").checked = !!cam.ptz_enabled;

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
  const data = getFormData();
  if (!data.host) {
    alert("Por favor, informe o IP ou endereço da câmera.");
    return;
  }

  try {
    let res;
    if (editingCamId) {
      res = await fetch(`/api/cameras/${editingCamId}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(data)
      });
      data.id = editingCamId;
    } else {
      res = await fetch("/api/cameras", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(data)
      });
    }

    if (res.ok) {
      // Salva também na nuvem do Supabase se o usuário estiver logado
      if (typeof saveCameraToSupabase === "function") {
        await saveCameraToSupabase(data);
      }
      closeModal();
      await loadCameras();
    } else {
      const err = await res.json();
      alert("Erro ao salvar: " + (err.error || "Tente novamente"));
    }
  } catch (err) {
    alert("Erro de conexão com o servidor: " + err.message);
  }
}

async function deleteCamera(camId) {
  if (!confirm("Tem certeza que deseja excluir esta câmera?")) return;
  try {
    const res = await fetch(`/api/cameras/${camId}`, { method: "DELETE" });
    if (res.ok) {
      if (typeof deleteCameraFromSupabase === "function") {
        await deleteCameraFromSupabase(camId);
      }
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
    username: document.getElementById("camUser").value,
    password: document.getElementById("camPassword").value,
    stream_type: document.getElementById("camStreamType").value,
    custom_url: document.getElementById("camCustomUrl").value,
    ptz_enabled: document.getElementById("camPtzEnabled").checked
  };
}

function escapeHtml(str) {
  if (!str) return "";
  return str.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}
