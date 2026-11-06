const CACHE_NAME = "vision-v4.0";

self.addEventListener("install", (e) => {
  self.skipWaiting();
});

self.addEventListener("activate", (e) => {
  // Purga imediatamente todo e qualquer cache acumulado no navegador
  e.waitUntil(
    caches.keys().then((keys) => {
      return Promise.all(
        keys.map((k) => caches.delete(k))
      );
    })
  );
  self.clients.claim();
});

self.addEventListener("fetch", (e) => {
  const url = new URL(e.request.url);
  const isLocal = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname.startsWith("192.168.");
  
  // Em rede local, ou rotas de API, snapshots e streaming: NUNCA intercepta nem armazena em cache
  if (isLocal || url.pathname.startsWith("/api/") || url.pathname.startsWith("/download/") || e.request.method !== "GET") {
    return;
  }

  // Apenas acesso PWA remoto: busca na rede direto
  e.respondWith(
    fetch(e.request).catch(() => caches.match(e.request))
  );
});
