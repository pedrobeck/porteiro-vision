import { VideoRTC } from './video-rtc.js';

/**
 * PorteiroStream - Componente WebRTC de alta performance
 * Conecta via WebSocket ao go2rtc e vincula tracks diretamente ao elemento <video>
 */
export class PorteiroStream extends VideoRTC {
  constructor() {
    super();
    this.mode = 'webrtc';
    this.media = 'video';
  }

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
      this.video.removeAttribute('controls');
      this.video.autoplay = true;
      this.video.muted = true;
      this.video.defaultMuted = true;
      this.video.playsInline = true;
      this.video.setAttribute('playsinline', '');
      this.video.setAttribute('webkit-playsinline', '');
      this.video.setAttribute('muted', '');
      this.video.setAttribute('autoplay', '');
      this.video.style.objectFit = 'contain';
      this.video.style.width = '100%';
      this.video.style.height = '100%';
      this.video.style.background = '#000';
      this.video.style.pointerEvents = 'none';

      this.video.addEventListener('loadeddata', () => {
        console.log('[WebRTC] loadeddata disparado com sucesso');
        this.dispatchEvent(new CustomEvent('streamconnected', {
          detail: { mode: 'RTC' },
          bubbles: true,
          composed: true
        }));
      });

      this.video.addEventListener('playing', () => {
        console.log('[WebRTC] video playing');
        this.dispatchEvent(new CustomEvent('streamconnected', {
          detail: { mode: 'RTC' },
          bubbles: true,
          composed: true
        }));
      });
    }
  }

  onwebrtc() {
    const pc = new RTCPeerConnection(this.pcConfig);

    pc.addEventListener('icecandidate', ev => {
      if (ev.candidate && this.mode.includes('webrtc/tcp') && ev.candidate.protocol === 'udp') return;
      const candidate = ev.candidate ? ev.candidate.toJSON().candidate : '';
      this.send({ type: 'webrtc/candidate', value: candidate });
    });

    // Vincula o stream diretamente à tag <video> existente no DOM
    pc.ontrack = (ev) => {
      console.log('[WebRTC] ontrack recebido:', ev.track.kind);
      if (ev.streams && ev.streams[0]) {
        this.video.srcObject = ev.streams[0];
      } else if (ev.track) {
        if (!this.video.srcObject) {
          this.video.srcObject = new MediaStream([ev.track]);
        } else {
          this.video.srcObject.addTrack(ev.track);
        }
      }
      this.play();
      this.pcState = WebSocket.OPEN;
      this.dispatchEvent(new CustomEvent('streamconnected', {
        detail: { mode: 'RTC' },
        bubbles: true,
        composed: true
      }));
    };

    pc.addEventListener('connectionstatechange', () => {
      console.log('[WebRTC] connectionstatechange:', pc.connectionState);
      if (pc.connectionState === 'connected') {
        const tracks = pc.getTransceivers()
          .filter(tr => tr.currentDirection === 'recvonly')
          .map(tr => tr.receiver.track);
        if (tracks.length > 0 && !this.video.srcObject) {
          this.video.srcObject = new MediaStream(tracks);
        }
        this.play();
        this.pcState = WebSocket.OPEN;
        this.dispatchEvent(new CustomEvent('streamconnected', {
          detail: { mode: 'RTC' },
          bubbles: true,
          composed: true
        }));
      } else if (pc.connectionState === 'failed' || pc.connectionState === 'disconnected') {
        pc.close();
        this.pcState = WebSocket.CLOSED;
        this.pc = null;
        this.dispatchEvent(new CustomEvent('streamfailed', { bubbles: true, composed: true }));
        this.onconnect();
      }
    });

    this.onmessage['webrtc'] = msg => {
      switch (msg.type) {
        case 'webrtc/candidate':
          if (this.mode.includes('webrtc/tcp') && msg.value.includes(' udp ')) return;
          pc.addIceCandidate({ candidate: msg.value, sdpMid: '0' }).catch(er => {
            console.warn('[WebRTC] addIceCandidate aviso:', er);
          });
          break;
        case 'webrtc/answer':
          console.log('[WebRTC] SDP Answer recebido de go2rtc');
          pc.setRemoteDescription({ type: 'answer', sdp: msg.value }).catch(er => {
            console.warn('[WebRTC] setRemoteDescription erro:', er);
          });
          break;
        case 'error':
          if (!msg.value.includes('webrtc/offer')) return;
          pc.close();
      }
    };

    this.createOffer(pc).then(offer => {
      console.log('[WebRTC] Enviando SDP Offer para go2rtc...');
      this.send({ type: 'webrtc/offer', value: offer.sdp });
    });

    this.pcState = WebSocket.CONNECTING;
    this.pc = pc;
  }

  async createOffer(pc) {
    const tc = pc.addTransceiver('video', { direction: 'recvonly' });
    if ('RTCRtpReceiver' in window && 'getCapabilities' in RTCRtpReceiver) {
      try {
        const codecs = RTCRtpReceiver.getCapabilities('video').codecs;
        const h264 = codecs.filter(c => c.mimeType.toLowerCase() === 'video/h264');
        const other = codecs.filter(c => c.mimeType.toLowerCase() !== 'video/h264' && c.mimeType.toLowerCase() !== 'video/h265');
        // Prioriza e isola H.264 na oferta SDP para eliminar o bug da faixa verde de H.265 em tablets Android
        if (h264.length > 0) {
          tc.setCodecPreferences([...h264, ...other]);
        }
      } catch (e) {
        console.warn('[WebRTC] setCodecPreferences:', e);
      }
    }

    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    return offer;
  }
}

if (!customElements.get('porteiro-stream')) {
  customElements.define('porteiro-stream', PorteiroStream);
}

if (!customElements.get("vision-stream")) {
  customElements.define("vision-stream", PorteiroStream);
}
