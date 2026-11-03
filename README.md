# 🛡️ Porteiro Vision — Central de Vigilância Inteligente

Sistema inovador de monitoramento e central de portaria para câmeras IP (**iCSee / Xiongmai**, **Intelbras**, **TP-Link Tapo**, **Hikvision** e **RTSP Genérico**), projetado para rodar como um mini-servidor local e fornecer um painel web ultrarrápido para **tablets Android**, **celulares** e **computadores**.

---

## ✨ Recursos Principais

* **📺 Visualização Multitela Adaptável:**
  * Modo **Auto** (organização automática inteligente).
  * Modo **1x1** (foco solo em alta definição).
  * Modo **2x2** (4 câmeras simultâneas).
  * Modo **3x3** (grade para até 9 câmeras).
  * Modo **Hero / Porteiro** (1 câmera principal grande no centro + miniaturas laterais para foco na entrada).
* **👆 Foco e Zoom Instantâneo:** Clique em qualquer câmera da grade para expandi-la em tela cheia com alta taxa de quadros e controles dedicados.
* **🎮 Joystick PTZ Virtual:** Controle de movimento motorizado (cima, baixo, esquerda, direita) integrado via protocolo nativo Xiongmai / DVRIP.
* **📸 Captura de Fotos (Snapshot):** Salve capturas em alta resolução com data e hora com apenas 1 clique.
* **📱 Otimizado para Tablets e Celulares (PWA):**
  * Interface escura estilo *Command Center* com baixo consumo de bateria.
  * Pode ser adicionado à tela inicial do Android como aplicativo nativo (sem barra de navegação do browser).
* **⚡ Streaming de Baixa Latência:** Decodificação multithread em tempo real via OpenCV e descarte de quadros acumulados para transmissão 100% em tempo real.
* **🔒 Privacidade Total:** O vídeo trafega exclusivamente dentro da sua rede local (Wi-Fi/Ethernet), sem passar por servidores de nuvem de terceiros.

---

## 🚀 Como Iniciar o Servidor

1. Certifique-se de que o Python 3 está instalado.
2. Na pasta do projeto, execute:
   ```cmd
   python app.py
   ```
   *(Ou dê um duplo clique no arquivo `start.bat`)*

3. O servidor exibirá o endereço no terminal:
   * **No seu computador:** `http://localhost:8080`
   * **No seu Tablet / Celular Android:** `http://SEU_IP_LOCAL:8080` (ex: `http://192.168.4.124:8080`)

---

## 📱 Como Usar no Tablet / Celular Android

1. Conecte o tablet na **mesma rede Wi-Fi** do computador que está rodando o servidor.
2. Abra o navegador Chrome no tablet e acesse o endereço IP exibido no cabeçalho (ex: `http://192.168.4.124:8080`).
3. No Chrome, toque nos três pontinhos no canto superior e selecione **"Adicionar à tela inicial"** ou **"Instalar aplicativo"**.
4. Pronto! O Porteiro Vision abrirá como um app de tela cheia sem barras de endereço.

---

## 🛠️ Tecnologias Utilizadas

* **Backend:** Python 3.13, Flask, OpenCV (FFmpeg VideoCapture multithreaded), Waitress WSGI.
* **Frontend:** Vanilla HTML5, CSS3 Glassmorphism, JavaScript ES6+, Progressive Web App (PWA).
* **Protocolos Suportados:** RTSP (RFC 2326), DVRIP (porta 34567 - Xiongmai), HTTP MJPEG / Snapshots.
