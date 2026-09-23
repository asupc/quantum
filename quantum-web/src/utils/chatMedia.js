/**
 * 会话媒体地址解析与鉴权加载。
 *
 * 地址语义与 App 端 ChatRepository.mediaUrl 一致：
 *   - http(s)://…            → 外链，直接用（无需 JWT）
 *   - api/… 或 /api/…        → 自家通道（AppMedia/AppUpload），拼当前站点地址 + 需 JWT
 *   - 其余（裸 FileId）       → /api/AppUpload/{fileId}，需 JWT
 *
 * 浏览器标签不会自动带 localStorage 里的 JWT，因此需鉴权的媒体必须先 fetch 成 blob
 * 再用 object URL 交给 <img>/<video>/<audio>（App 侧对应 OkHttp 带 JWT 拉流）。
 */
import { DownloadUrl } from '@/api/app';

const AUTH_PREFIXES = ['/api/AppUpload/', '/api/AppMedia/'];
const blobCache = new Map();
const MAX_CACHE = 24;
/** 同一鉴权地址并发加载去重：url → 进行中的 Promise */
const inflight = new Map();
/** 当前仍挂在 <img>/<video>/<audio> 上的 object URL：延迟 revoke 须跳过它们（§3-11） */
const retained = new Set();
/** 已淘汰、待延迟 revoke 的 object URL → 定时器 */
const pendingRevoke = new Map();
/** 淘汰后宽限期：期间若对象仍被展示则不回收，避免正在渲染的媒体链接被提前 revoke */
const REVOKE_GRACE_MS = 30000;

/** 判断内容是否为站内鉴权地址 */
export function needsAuth(url) {
  return AUTH_PREFIXES.some((prefix) => url.startsWith(prefix));
}

/**
 * content → 可直接使用的地址（尚未鉴权加载，只是拼装）
 * @param {string} content 消息内容（外链 / 相对 api 地址 / FileId）
 * @param {string} contentType text/image/video/audio/file
 */
export function resolveMediaUrl(content, contentType) {
  const raw = (content || '').trim();
  if (!raw) {
    return '';
  }
  if (/^https?:\/\//i.test(raw)) {
    return raw;
  }
  if (raw.startsWith('api/')) {
    return `/${raw}`;
  }
  if (raw.startsWith('/api/')) {
    return raw;
  }
  // 裸 FileId：文本类型不会走到这里，媒体类型统一按上传件下载
  return contentType && contentType !== 'text' ? DownloadUrl(raw) : '';
}

/** 是否自家服务器地址（自家内容不显示「保存到服务器」，与 App isOwnServerUrl 同义） */
export function isOwnServerUrl(url) {
  return needsAuth(url) || url.startsWith(window.location.origin);
}

async function fetchBlob(url) {
  const token = localStorage.getItem('accessToken');
  const response = await fetch(url, {
    headers: token ? { Authorization: token } : {}
  });
  if (!response.ok) {
    throw new Error(`媒体加载失败（${response.status}）`);
  }
  return response.blob();
}

/** 标记某 object URL 正在被展示：延迟 revoke 期间据此跳过（组件挂载 src 后调用） */
export function retainMedia(objectUrl) {
  if (objectUrl) {
    retained.add(objectUrl);
  }
}

/** 取消展示标记：组件卸载/换源后调用，让宽限期到点后可回收 */
export function releaseMedia(objectUrl) {
  if (objectUrl) {
    retained.delete(objectUrl);
  }
}

/** 延迟回收：宽限期后仅在对象已不被展示时 revoke，否则顺延一个宽限期（§3-11） */
function scheduleRevoke(objectUrl) {
  if (!objectUrl || pendingRevoke.has(objectUrl)) {
    return;
  }
  const timer = setTimeout(() => {
    pendingRevoke.delete(objectUrl);
    if (retained.has(objectUrl)) {
      scheduleRevoke(objectUrl); // 仍在视口展示 → 不回收，再等一轮
      return;
    }
    URL.revokeObjectURL(objectUrl);
  }, REVOKE_GRACE_MS);
  pendingRevoke.set(objectUrl, timer);
}

/**
 * 取可用地址：外链原样返回；鉴权地址拉成 blob 并缓存 object URL。
 * 同一地址并发请求合流为一个 fetch（in-flight 去重），缓存满时淘汰最旧、延迟 revoke。
 * @returns {Promise<string>} 可用于 src 的地址
 */
export async function loadMediaUrl(url) {
  if (!url || !needsAuth(url)) {
    return url;
  }
  if (blobCache.has(url)) {
    return blobCache.get(url);
  }
  const existing = inflight.get(url);
  if (existing) {
    return existing;
  }
  const task = (async () => {
    const blob = await fetchBlob(url);
    const objectUrl = URL.createObjectURL(blob);
    if (blobCache.size >= MAX_CACHE) {
      const oldestKey = blobCache.keys().next().value;
      const oldestObjectUrl = blobCache.get(oldestKey);
      blobCache.delete(oldestKey);
      scheduleRevoke(oldestObjectUrl);
    }
    blobCache.set(url, objectUrl);
    return objectUrl;
  })();
  inflight.set(url, task);
  try {
    return await task;
  } finally {
    inflight.delete(url);
  }
}

/** 清缓存（退出登录/切服务器时调用） */
export function clearMediaCache() {
  pendingRevoke.forEach((timer) => clearTimeout(timer));
  pendingRevoke.clear();
  blobCache.forEach((objectUrl) => URL.revokeObjectURL(objectUrl));
  blobCache.clear();
  retained.clear();
}

/** 可播放类型判定（与 App MediaTypes 白名单一致） */
const AUDIO_EXTS = ['mp3', 'flac', 'm4a', 'aac', 'wav'];
const VIDEO_EXTS = ['mp4', 'mkv', 'webm', 'm4v'];

function extOf(url) {
  const clean = (url || '').split('#')[0].split('?')[0];
  const name = clean.substring(clean.lastIndexOf('/') + 1);
  const dot = name.lastIndexOf('.');
  return dot > 0 ? name.substring(dot + 1).toLowerCase() : '';
}

export function isPlayable(url) {
  const ext = extOf(url);
  return AUDIO_EXTS.includes(ext) || VIDEO_EXTS.includes(ext);
}

export function isPlayableAudio(url) {
  return AUDIO_EXTS.includes(extOf(url));
}

export function isPlayableVideo(url) {
  return VIDEO_EXTS.includes(extOf(url));
}
