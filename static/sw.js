const CACHE_NAME = "vision-v6.0-nocache";

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

// NUNCA retém cache: busca sempre direto da rede
self.addEventListener("fetch", (e) => {
  e.respondWith(fetch(e.request));
});
