/**
 * /ws/app 长连接客户端（复刻 App 端 AppWsClient 语义，见 docs/App端API契约.md §4）。
 *
 * - 握手：ws(s)://<host>/ws/app?token=<jwt>（同源；dev 由 vite proxy 转发，见 vite.config.js）
 * - 心跳：应用层 25s 一帧 {"type":"ping"}（服务端 60s 无帧踢连接，客户端另设静默看门狗兜底）
 * - 重连：指数退避 1s → 30s，连上即复位；断线期间消息靠 REST 游标补拉（视图层负责）
 * - 下行帧：message / notify / sync_done / pong / error（error 文案必须让用户看见）
 */
export const WS_STATE = {
  CLOSED: 'closed',
  CONNECTING: 'connecting',
  OPEN: 'open'
};

const HEARTBEAT_MS = 25000;
const SILENCE_TIMEOUT_MS = 70000;
const BACKOFF_MIN = 1000;
const BACKOFF_MAX = 30000;

export class AppSocket {
  constructor({ onFrame, onState } = {}) {
    this.onFrame = onFrame || (() => {});
    this.onState = onState || (() => {});
    this.socket = null;
    this.state = WS_STATE.CLOSED;
    this.retry = 0;
    this.heartbeatTimer = null;
    this.reconnectTimer = null;
    this.silenceTimer = null;
    this.manualClose = false;
  }

  /**
   * 裸 JWT：localStorage.accessToken 带 "Bearer " 前缀（HTTP 头需要），
   * 但 WS 握手是 query 参数、服务端按裸 JWT 验签，必须去掉前缀。
   */
  static rawToken() {
    const stored = localStorage.getItem('accessToken') || '';
    return stored.replace(/^Bearer\s+/i, '');
  }

  static url(token) {
    const scheme = window.location.protocol === 'https:' ? 'wss' : 'ws';
    return `${scheme}://${window.location.host}/ws/app?token=${encodeURIComponent(token)}`;
  }

  connect(token) {
    if (!token) {
      this.onState(WS_STATE.CLOSED, '未登录');
      return;
    }
    this.manualClose = false;
    this.clearTimers();
    const tokenNow = (token || AppSocket.rawToken()).replace(/^Bearer\s+/i, '');
    if (!tokenNow) {
      this.setState(WS_STATE.CLOSED);
      return;
    }
    this.setState(WS_STATE.CONNECTING);
    let socket;
    try {
      socket = new WebSocket(AppSocket.url(tokenNow));
    } catch (e) {
      this.scheduleReconnect();
      return;
    }
    this.socket = socket;

    socket.onopen = () => {
      this.retry = 0;
      this.setState(WS_STATE.OPEN);
      this.startHeartbeat();
      this.touchSilence();
    };
    socket.onmessage = (event) => {
      this.touchSilence();
      let frame;
      try {
        frame = JSON.parse(event.data);
      } catch (e) {
        return; // 非 JSON 帧（协议外）忽略
      }
      if (frame && frame.type === 'pong') {
        return;
      }
      this.onFrame(frame);
    };
    socket.onerror = () => {
      // onerror 之后必有 onclose，重连统一在 onclose 里调度
    };
    socket.onclose = () => {
      this.clearTimers();
      this.socket = null;
      if (this.manualClose) {
        this.setState(WS_STATE.CLOSED);
        return;
      }
      this.setState(WS_STATE.CLOSED);
      this.scheduleReconnect();
    };
  }

  /** 主动发送一帧（未连接时返回 false，调用方应回落 REST 通道） */
  send(frame) {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
      return false;
    }
    try {
      this.socket.send(JSON.stringify(frame));
      return true;
    } catch (e) {
      return false;
    }
  }

  close() {
    this.manualClose = true;
    this.clearTimers();
    if (this.socket) {
      try {
        this.socket.close();
      } catch (e) {
        // 连接已死，忽略
      }
      this.socket = null;
    }
    this.setState(WS_STATE.CLOSED);
  }

  setState(state, detail) {
    if (this.state === state && !detail) {
      return;
    }
    this.state = state;
    this.onState(state, detail);
  }

  startHeartbeat() {
    this.heartbeatTimer = setInterval(() => {
      this.send({ type: 'ping' });
    }, HEARTBEAT_MS);
  }

  /** 静默看门狗：服务端 60s 无帧会踢连接，这里超时主动重建，避免半死连接一直挂着 */
  touchSilence() {
    clearTimeout(this.silenceTimer);
    this.silenceTimer = setTimeout(() => {
      if (this.socket) {
        try {
          this.socket.close();
        } catch (e) {
          // 忽略
        }
      }
    }, SILENCE_TIMEOUT_MS);
  }

  scheduleReconnect() {
    clearTimeout(this.reconnectTimer);
    const delay = Math.min(BACKOFF_MIN * Math.pow(2, this.retry), BACKOFF_MAX);
    this.retry += 1;
    this.reconnectTimer = setTimeout(() => {
      this.connect(AppSocket.rawToken());
    }, delay);
  }

  clearTimers() {
    clearInterval(this.heartbeatTimer);
    clearTimeout(this.silenceTimer);
    this.heartbeatTimer = null;
    this.silenceTimer = null;
  }
}
