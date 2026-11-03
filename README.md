# 🛡️ Porteiro Vision Cloud — Central de Vigilância Inteligente

Sistema inovador de monitoramento e central de portaria para câmeras IP (**iCSee / Xiongmai**, **Intelbras**, **TP-Link Tapo**, **Hikvision** e **RTSP Genérico**), 100% integrável à nuvem com **autenticação e banco de dados via Supabase**.

---

## ✨ Recursos Principais

* **🔐 100% Online com Supabase:**
  * Autenticação de usuários (Login / Cadastro) gerenciada pelo Supabase Auth.
  * Isolamento de dados por usuário com **Row Level Security (RLS)**: cada usuário só visualiza e gerencia as suas próprias câmeras cadastradas.
  * Sincronização automática em nuvem das configurações das câmeras.
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

---

## 🗄️ Configuração do Supabase (Passo a Passo)

1. No seu painel do **[Supabase](https://supabase.com/)**, crie um projeto (ou use um existente).
2. Abra o menu **SQL Editor** no painel do Supabase.
3. Cole e execute o script contido no arquivo:
   👉 **[`supabase_schema.sql`](./supabase_schema.sql)**
4. Em **Project Settings -> API**, copie sua:
   * **Project URL** (ex: `https://xyz.supabase.co`)
   * **Anon / Public Key** (ex: `eyJhbGciOi...`)
5. Você pode inserir essas chaves:
   * Como variáveis de ambiente: `SUPABASE_URL` e `SUPABASE_ANON_KEY`.
   * Ou diretamente na tela de login da aplicação clicando em **"⚙️ Configurar chaves do Supabase"**.

---

## 🚀 Como Rodar Localmente

1. Execute:
   ```cmd
   python app.py
   ```
   *(Ou dê um duplo clique no arquivo `start.bat`)*

2. Acesse:
   * **No seu computador:** `http://localhost:8080`
   * **No seu Tablet / Celular Android:** `http://SEU_IP_LOCAL:8080` (ex: `http://192.168.4.124:8080`)

---

## ☁️ Deploy 100% Online (Vercel ou Render)

### Opção 1: Render (Recomendado para Streaming Contínuo e OpenCV)
1. Crie uma conta no [Render](https://render.com).
2. Conecte o repositório Git do seu GitHub.
3. Escolha **Web Service** usando o `Dockerfile` existente no projeto.
4. Defina as variáveis de ambiente:
   * `SUPABASE_URL`
   * `SUPABASE_ANON_KEY`
5. Clique em **Deploy**.

### Opção 2: Vercel
1. Conecte o repositório no painel da [Vercel](https://vercel.com).
2. O arquivo `vercel.json` configurará o runtime Python automaticamente.
3. Configure as variáveis de ambiente `SUPABASE_URL` e `SUPABASE_ANON_KEY`.

---

## 🛠️ Tecnologias Utilizadas

* **Autenticação & Nuvem:** Supabase (Auth + PostgreSQL com RLS + Supabase JS v2).
* **Backend:** Python 3.13, Flask, OpenCV (FFmpeg VideoCapture multithreaded), Waitress WSGI.
* **Frontend:** Vanilla HTML5, CSS3 Glassmorphism, JavaScript ES6+, Progressive Web App (PWA).
* **Protocolos de Câmera:** RTSP (RFC 2326), DVRIP (porta 34567 - Xiongmai), HTTP MJPEG / Snapshots.
