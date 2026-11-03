// PORTEIRO VISION - DASHBOARD CONTROLLER

let cameras = [];
let currentLayout = "auto";
let focusedCameraId = null;
let pollInterval = null;

document.addEventListener("DOMContentLoaded", () => {
  initLayoutControls();
  initModal();
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
  // Ao trocar layout manual, remove foco
  unfocusCamera();
}

// --- CARREGAMENTO E RENDERIZAÇÃO DAS CÂMERAS ---
async function loadCameras() {
  try {
    const res = await fetch("/api/cameras");
    cameras = await res.json();
    renderGrid();
  } catch (err) {
    console.error("Erro ao carregar câmeras:", err);
  }
}

function startStatusPolling() {
  pollInterval = setInterval(async () => {
    try {
      const res = await fetch("/api/status");
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

  if (cameras.length === 0) {
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

  cameras.forEach((cam) => {
    const cell = document.createElement("div");
    cell.className = "camera-cell";
    cell.dataset.id = cam.id;

    const streamUrl = `/api/stream/${cam.id}`;

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
      <div class="feed-wrapper" ondblclick="toggleFullscreen(event)">
        <img class="feed-img" src="${streamUrl}" alt="${escapeHtml(cam.name)}" loading="lazy">
      </div>
      ${cam.ptz_enabled ? renderPtzJoystick(cam.id) : ""}
    `;

    cell.addEventListener("click", (e) => {
      // Se não clicou num botão, alterna o foco
      if (!e.target.closest("button") && !e.target.closest(".ptz-joystick")) {
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

// --- FOCO E TELA CHEIA (INCLINADO A TABLET/PORTEIRO) ---
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

    // Se tiver PTZ, exibe o joystick de controle
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
  const link = document.createElement("a");
  link.href = `/api/snapshot/${camId}?t=${Date.now()}`;
  link.download = `porteiro_cam_${camId}_${Date.now()}.jpg`;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
}

async function sendPtz(camId, cmd) {
  try {
    await fetch(`/api/cameras/${camId}/ptz`, {
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
  
  // Ao mudar a marca, preenche dados comuns automaticamente
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
  const cam = cameras.find((c) => c.id === camId);
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

  const data = getFormData();
  try {
    const res = await fetch("/api/cameras/test", {
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
    } else {
      res = await fetch("/api/cameras", {
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
  }
}

async function deleteCamera(camId) {
  if (!confirm("Tem certeza que deseja excluir esta câmera?")) return;
  try {
    const res = await fetch(`/api/cameras/${camId}`, { method: "DELETE" });
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
