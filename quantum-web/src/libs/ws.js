/**
 * /ws/app 长连接客户端（复刻 App 端 AppWsClient 语义，见 docs/App端API契约.md §4）。
 *
 * - 握手：ws(s)://<host>/ws/app（同源；dev 由 vite proxy 转发，见 vite.config.js）。
 *   **URL 不携带令牌**（token 进 query 会被代理/访问日志留存），鉴权走服务端已支持的首帧
 *   {"type":"auth","token":"<jwt>"}（AppWebSocketMiddleware：token 留空允许升级，
 *   30s 内未完成鉴权即断；首帧鉴权成功前连接不在注册表、收不到广播）
 * - 心跳：应用层 25s 一帧 {"type":"ping"}（服务端 60s 无帧踢连接，客户端另设静默看门狗兜底）
 * - 重连：指数退避 1s → 30s，连上即复位；**鉴权被拒不再重连**（error 帧「token 无效或已过期」
 *   或 1008 PolicyViolation 关闭时清令牌 + 断会话 + 回登录，与 REST 401 同一登出实现，见 libs/authSession.js）
 * - 下行帧：message / notify / sync_done / pong / error（鉴权类 error 已内部处置，其余文案必须让用户看见）
 */
import { clearLoginSession } from './authSession.js';

export const WS_STATE = {
  CLOSED: 'closed',
  CONNECTING: 'connecting',
  OPEN: 'open'
};

const HEARTBEAT_MS = 25000;
const SILENCE_TIMEOUT_MS = 70000;
const BACKOFF_MIN = 1000;
const BACKOFF_MAX = 30000;
/** 服务端鉴权失败专用关闭码（AppWebSocketMiddleware.TryAuthenticateAsync → PolicyViolation） */
const WS_CLOSE_POLICY_VIOLATION = 1008;
/** 无令牌升级后服务端固定先下发的「请首帧鉴权」提示：协议内噪声，不外抛（否则每次连接都弹告警） */
const PRE_AUTH_PROMPT = /未鉴权|首帧/;
/** 鉴权被拒文案（TryAuthenticateAsync：'token 无效或已过期，请重新登录。'）→ 清令牌回登录 */
const AUTH_REJECTED = /token\s*(?:无效|失效|过期)|重新登录/;

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
    // 本次连接是否已被判「鉴权失败」：登出只做一次，随下次 connect 复位
    this.authRejected = false;
  }

  /**
   * 裸 JWT：localStorage.accessToken 带 "Bearer " 前缀（HTTP 头需要），
   * 但 WS 首帧鉴权按裸 JWT 验签，必须去掉前缀。
   */
  static rawToken() {
    const stored = localStorage.getItem('accessToken') || '';
    return stored.replace(/^Bearer\s+/i, '');
  }

  /**
   * 握手地址：**只含路径**，令牌不进 query（会被代理/访问日志留存）。
   * 服务端 token 留空即允许升级并等首帧 auth，超时（30s）未鉴权才断。
   */
  static url() {
    const scheme = window.location.protocol === 'https:' ? 'wss' : 'ws';
    return `${scheme}://${window.location.host}/ws/app`;
  }

  connect(token) {
    if (!token) {
      this.onState(WS_STATE.CLOSED, '未登录');
      return;
    }
    this.manualClose = false;
    this.authRejected = false;
    this.clearTimers();
    const tokenNow = (token || AppSocket.rawToken()).replace(/^Bearer\s+/i, '');
    if (!tokenNow) {
      this.setState(WS_STATE.CLOSED);
      return;
    }
    this.setState(WS_STATE.CONNECTING);
    let socket;
    try {
      socket = new WebSocket(AppSocket.url());
    } catch (e) {
      this.scheduleReconnect();
      return;
    }
    this.socket = socket;

    socket.onopen = () => {
      this.retry = 0;
      // 首帧即鉴权：必须在任何业务/心跳帧之前，服务端只认首帧 auth 完成登录
      this.send({ type: 'auth', token: tokenNow });
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
      if (frame && frame.type === 'error') {
        const content = String(frame.content || '');
        if (AUTH_REJECTED.test(content)) {
          // 鉴权被拒（token 过期/用途不符）：清令牌回登录并停止重连，绝不带着死令牌空转退避
          this.rejectAuth();
          return;
        }
        if (PRE_AUTH_PROMPT.test(content)) {
          // 无令牌升级后服务端固定先下发的兜底提示（首帧 auth 在途）：协议内噪声，不外抛
          return;
        }
      }
      this.onFrame(frame);
    };
    socket.onerror = () => {
      // onerror 之后必有 onclose，重连统一在 onclose 里调度
    };
    socket.onclose = (event) => {
      this.clearTimers();
      this.socket = null;
      if (this.manualClose) {
        this.setState(WS_STATE.CLOSED);
        return;
      }
      // 服务端判「鉴权失败」的关闭：清令牌回登录（与 REST 401 同一实现），不再重连。
      // 注意只看 1008（PolicyViolation）+ 已收到的鉴权失败帧：网络断开/后端重启的 1006
      // 从未 open 时同样无关闭码语义，但那不是凭据故障，仍须退避重连。
      if (this.authRejected || event?.code === WS_CLOSE_POLICY_VIOLATION) {
        this.rejectAuth();
        return;
      }
      this.setState(WS_STATE.CLOSED);
      this.scheduleReconnect();
    };
  }

  /** 鉴权失败收尾（幂等）：清令牌 + 断会话 + 回登录，并彻底停掉重连、心跳与在途连接 */
  rejectAuth() {
    if (this.authRejected) {
      return;
    }
    this.authRejected = true;
    this.manualClose = true;
    this.clearTimers();
    clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
    if (this.socket) {
      try {
        this.socket.close();
      } catch (e) {
        // 连接已死，忽略
      }
      this.socket = null;
    }
    this.setState(WS_STATE.CLOSED);
    clearLoginSession();
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
