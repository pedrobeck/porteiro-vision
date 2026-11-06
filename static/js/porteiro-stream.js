import { VideoRTC } from './video-rtc.js';

/**
 * PorteiroStream - Componente customizado baseado em VideoRTC
 * Decodificação direta H.264 no navegador/tablet via WebRTC com fallback para MSE.
 */
export class PorteiroStream extends VideoRTC {
  static get observedAttributes() {
    return ['src', 'mode'];
  }

  attributeChangedCallback(name, oldValue, newValue) {
    if (name === 'src' && newValue && newValue !== oldValue) {
      this.src = newValue;
    }
    if (name === 'mode' && newValue && newValue !== oldValue) {
      this.mode = newValue;
    }
  }

  oninit() {
    super.oninit();
    if (this.video) {
      this.video.controls = false;
      this.video.autoplay = true;
      this.video.muted = true;
      this.video.playsInline = true;
      this.video.style.objectFit = 'contain';
      this.video.style.width = '100%';
      this.video.style.height = '100%';
      this.video.style.background = '#000';
      this.video.style.pointerEvents = 'none';
    }
  }

  onpcvideo(video2) {
    super.onpcvideo(video2);
    if (this.pcState !== WebSocket.CLOSED) {
      this.dispatchEvent(new CustomEvent('streamconnected', {
        detail: { mode: 'RTC' },
        bubbles: true,
        composed: true
      }));
    }
  }

  onmse() {
    super.onmse();
    this.dispatchEvent(new CustomEvent('streamconnected', {
      detail: { mode: 'MSE' },
      bubbles: true,
      composed: true
    }));
  }

  onclose() {
    const result = super.onclose();
    this.dispatchEvent(new CustomEvent('streamfailed', {
      detail: { src: this.wsURL },
      bubbles: true,
      composed: true
    }));
    return result;
  }
}

if (!customElements.get('porteiro-stream')) {
  customElements.define('porteiro-stream', PorteiroStream);
}
