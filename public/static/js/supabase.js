// PORTEIRO VISION - SUPABASE AUTH & CLOUD SYNC MODULE

let supabaseClient = null;
let currentUser = null;

// Inicializa o cliente do Supabase
function initSupabase(url, anonKey) {
  const serverUrl = window.ENV_SUPABASE_URL;
  const serverKey = window.ENV_SUPABASE_ANON_KEY;

  // Se a URL do servidor mudou em relação ao cache local do navegador, limpa o cache antigo
  const cachedUrl = localStorage.getItem("sb_url");
  if (serverUrl && cachedUrl && cachedUrl !== serverUrl) {
    console.log("[Supabase] Projeto alterado. Limpando cache antigo...");
    localStorage.removeItem("sb_url");
    localStorage.removeItem("sb_anon_key");
  }

  const finalUrl = url || serverUrl || localStorage.getItem("sb_url");
  const finalKey = anonKey || serverKey || localStorage.getItem("sb_anon_key");

  if (finalUrl && finalKey && window.supabase) {
    try {
      supabaseClient = window.supabase.createClient(finalUrl, finalKey);
      localStorage.setItem("sb_url", finalUrl);
      localStorage.setItem("sb_anon_key", finalKey);
      setupAuthListener();
      return true;
    } catch (e) {
      console.error("[Supabase] Erro ao instanciar cliente:", e);
      return false;
    }
  }
  return false;
}

function setupAuthListener() {
  if (!supabaseClient) return;

  supabaseClient.auth.onAuthStateChange(async (event, session) => {
    if (session && session.user) {
      currentUser = session.user;
      onUserLoggedIn(currentUser);
    } else {
      currentUser = null;
      onUserLoggedOut();
    }
  });

  // Checa sessão atual
  supabaseClient.auth.getSession().then(({ data }) => {
    if (data.session && data.session.user) {
      currentUser = data.session.user;
      onUserLoggedIn(currentUser);
    } else {
      onUserLoggedOut();
    }
  });
}

function onUserLoggedIn(user) {
  // Esconde modal de login
  document.getElementById("authModal").classList.remove("open");
  
  // Atualiza cabeçalho com usuário
  const userPill = document.getElementById("userProfilePill");
  if (userPill) {
    userPill.style.display = "flex";
    document.getElementById("userEmailDisplay").textContent = user.email;
  }
  
  // Carrega câmeras da nuvem do Supabase
  loadCamerasFromSupabase();
}

function onUserLoggedOut() {
  const userPill = document.getElementById("userProfilePill");
  if (userPill) userPill.style.display = "none";
  
  // Abre modal de autenticação
  document.getElementById("authModal").classList.add("open");
}

// --- OPERAÇÕES DE AUTENTICAÇÃO ---

async function supabaseSignIn(email, password) {
  if (!supabaseClient) throw new Error("Supabase não configurado");
  const { data, error } = await supabaseClient.auth.signInWithPassword({
    email,
    password
  });
  if (error) throw error;
  return data;
}

async function supabaseSignUp(email, password) {
  if (!supabaseClient) throw new Error("Supabase não configurado");
  const { data, error } = await supabaseClient.auth.signUp({
    email,
    password
  });
  if (error) throw error;
  return data;
}

async function supabaseSignOut() {
  if (supabaseClient) {
    await supabaseClient.auth.signOut();
  }
  currentUser = null;
  onUserLoggedOut();
}

// --- OPERAÇÕES NO BANCO DE DADOS (TABELA CAMERAS) ---

async function loadCamerasFromSupabase() {
  if (!supabaseClient || !currentUser) return;
  try {
    const { data, error } = await supabaseClient
      .from("cameras")
      .select("*")
      .order("created_at", { ascending: true });

    if (error) throw error;
    if (data && data.length > 0) {
      // Se não temos câmeras locais carregadas ou o hub está offline, usa as da nuvem
      if (!window.cameras || window.cameras.length === 0) {
        window.cameras = data;
        renderGrid();
      }
    }
  } catch (err) {
    console.warn("[Supabase] Não foi possível carregar câmeras da nuvem:", err.message);
  }
}

async function saveCameraToSupabase(cam) {
  if (!supabaseClient || !currentUser) return null;
  try {
    const payload = {
      user_id: currentUser.id,
      name: cam.name,
      brand: cam.brand || "generic",
      host: cam.host,
      port: cam.port || 554,
      username: cam.username || "admin",
      password: cam.password || "",
      stream_type: cam.stream_type || "sub",
      custom_url: cam.custom_url || "",
      ptz_enabled: !!cam.ptz_enabled
    };

    if (cam.id && !cam.id.startsWith("cam0")) {
      payload.id = cam.id;
    }

    const { data, error } = await supabaseClient
      .from("cameras")
      .upsert(payload)
      .select();

    if (error) throw error;
    return data ? data[0] : null;
  } catch (err) {
    console.error("[Supabase] Erro ao salvar câmera na nuvem:", err);
    return null;
  }
}

async function deleteCameraFromSupabase(camId) {
  if (!supabaseClient || !currentUser) return;
  try {
    await supabaseClient.from("cameras").delete().eq("id", camId);
  } catch (err) {
    console.error("[Supabase] Erro ao deletar câmera da nuvem:", err);
  }
}
