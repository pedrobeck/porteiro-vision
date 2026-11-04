const CACHE_NAME = "porteiro-vision-v2";

self.addEventListener("install", (e) => {
  self.skipWaiting();
});

self.addEventListener("activate", (e) => {
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
  // Ignora chamadas de API, downloads e streams dinâmicos
  if (e.request.url.includes("/api/") || e.request.url.includes("/download/") || e.request.method !== "GET") {
    return;
  }
  // Estratégia Network-First: Sempre busca a versão mais recente na rede
  e.respondWith(
    fetch(e.request)
      .then((response) => {
        if (response && response.status === 200) {
          const clone = response.clone();
          caches.open(CACHE_NAME).then((cache) => cache.put(e.request, clone));
        }
        return response;
      })
      .catch(() => caches.match(e.request))
  );
});
