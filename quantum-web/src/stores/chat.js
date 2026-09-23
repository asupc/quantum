import { defineStore } from 'pinia';
import { message } from '@/libs/naive-discrete';
import { AppSocket, WS_STATE } from '@/libs/ws';
import { clearMediaCache } from '@/utils/chatMedia';
import {
    SessionsOverview,
    SessionsRead,
    SessionMessages,
    SyncMessages,
    SubmitCommand,
    DeleteSession as DeleteSessionApi,
    NotificationList
} from '@/api/app';
import { GetTask } from '@/api/task';
import { parsePayload } from '@/utils/messagePayload';
import { sessionPreview } from '@/utils/chatFormat';

/**
 * 会话状态层（Web 端会话页的数据唯一来源）。
 *
 * 与 App 端 ChatRepository/ChatViewModel 的对应关系：
 *  - 消息实时主通道是 WS `/ws/app`（服务端全连接广播，客户端按帧内 `session` 分流）；
 *  - 断线/落后用 REST 增量补拉（`afterSeq` 游标，终止条件「本页 < limit」，空页不推进游标）；
 *  - 未读是**按会话的本地水位**（`readSeqs`），服务端 `sessions/overview` 按水位算未读；
 *    服务端 `messages/read` 只按全局 Seq 推进 Status，两者语义不同，不能混用；
 *  - 删除会话（2026-09-21 日志删除逻辑调整）只删会话实体：服务端消息记录保留（删除入口收敛到
 *    日志中心），MaxSeq 恒为当前全局最大，既有游标回拨 min(当前, MaxSeq) 退化为 no-op（兼容保留）。
 *
 * 与 App 的差异（有意为之，见 docs/Web端会话交互与UI预览方案.md §2）：
 *  - 不做全量历史同步：首屏只拉会话列表 + 当前会话最近一页，游标直接对齐服务端 MaxSeq；
 *  - 乐观气泡：先插入本地 pending 行，服务端回显（WS 回显帧 / REST 后补拉）到达后替换；
 *  - 上翻加载更早历史（App 侧 loadMore 未接线）；
 *  - chosen（已选 key 集合，按选项消息 seq 记忆，多值：可重复点选下每个点过的 key 都标「已选」）
 *    与 pickRows（点选产生的回显行 msgId → label，渲染为居中系统提示而非用户气泡）
 *    均持久化在 localStorage——单端本地记录，不进服务端、不同步其它端（见 docs/选项已选态改造计划.md）。
 */

const PAGE_SIZE = 50;
const CATCH_UP_PAGE = 500;
const CATCH_UP_MAX_PAGES = 20;
const READ_THROTTLE_MS = 3000;
// session_read 帧后的权威重算防抖（2026-09-21 双端同步）：多会话连续推进只合并一次 overview
const SESSION_READ_REFRESH_DEBOUNCE_MS = 500;

let socket = null;

// §3-8：共享 in-flight，合并并发/短窗内的重复触发（重连补拉 + catchUp 后 refreshSessions 等），
// 避免同一时刻多次向服务端发同样的会话快照/补拉请求。
let sessionsInflight = null;
let catchUpInflight = null;

// 点选代发匹配队列（内存，非渲染态）：发送成功后入队 {content, label}，applyMessage 见到
// 同 content 的「接收」回显行即写 pickRows 并出队；刷新即清，未匹配的行回落普通气泡显示。
let pendingPicks = [];

// §3-4：列表按 Seq 升序维护（与旧 sort 口径一致：空 Seq 沉底）。实时帧几乎都是最新 Seq，
// 二分定位插入点即可 O(log n)，取代每次 push 后整表 sort 的 O(n log n)。
function seqOrder(item) {
    return item.Seq || Number.MAX_SAFE_INTEGER;
}

function insertBySeq(rows, row) {
    const target = seqOrder(row);
    let lo = 0;
    let hi = rows.length;
    while (lo < hi) {
        const mid = (lo + hi) >> 1;
        if (seqOrder(rows[mid]) <= target) {
            lo = mid + 1;
        } else {
            hi = mid;
        }
    }
    rows.splice(lo, 0, row);
}

// §3-10：会话预览纯文本在建/改行时算一次存 previewText（sessionPreview 允许返回空串，占位由模板兜底），
// 列表模板与 filtered 直读该字段，不再每行每次渲染 parseRich。
function withPreview(entry) {
    return { ...entry, previewText: sessionPreview(entry.Last) };
}

function originKey() {
    return `quantum.chat.${window.location.origin}`;
}

function readJson(key, fallback) {
    try {
        const raw = localStorage.getItem(key);
        return raw ? JSON.parse(raw) : fallback;
    } catch (e) {
        return fallback;
    }
}

function writeJson(key, value) {
    try {
        localStorage.setItem(key, JSON.stringify(value));
    } catch (e) {
        // 存储不可用（隐私模式）时降级为纯内存态，不影响会话功能
    }
}

export const useChatStore = defineStore('chat', {
    state: () => ({
        wsState: WS_STATE.CLOSED,
        wsDetail: '',
        sessions: [],
        taskMap: {},
        /**
         * 站内通知元数据（msgId → {title, category, jump}）：通知行的分类色点/标题/jump 以
         * t_app_notification 为准（会话行正文只是服务端合成的「【标题】\n内容」），与 App 同做法。
         */
        notifyMap: {},
        currentKey: '',
        messagesBySession: {},
        hasMoreBySession: {},
        loadingSession: false,
        loadingEarlier: false,
        refreshing: false,
        sending: false,
        readSeqs: {},
        /** 已选 key 集合（seq → string[]，多值）：单端本地记忆，localStorage 持久化 */
        chosen: {},
        /**
         * 点选产生的回显行标记（msgId → { label }）：命中行渲染为居中系统提示「已选择「label」」，
         * 不再显示为用户气泡。localStorage 持久化，单端语义。
         */
        pickRows: {},
        /**
         * 跨会话触发迁移映射（msgId → {from, to}）：乱序防御——迁移帧先于 echo/补拉到达时
         * 行不在本地，先记映射，applyMessage 落行时命中则改写会话键并清来源会话的 pending
         * 乐观气泡。有界（超 500 丢最旧）；仅内存态，刷新页面即清（服务端行已是新键）。
         */
        moves: new Map(),
        syncCursor: 0,
        ready: false
    }),

    getters: {
        currentMessages: (state) => state.messagesBySession[state.currentKey] || [],
        /** §3-10：未读总数派生自 sessions（响应式缓存），取代原 state 字段 + 各处手动 reduce */
        unreadTotal: (state) => state.sessions.reduce((sum, item) => sum + (item.Unread || 0), 0),
        currentSession(state) {
            return state.sessions.find((item) => item.SessionKey === state.currentKey) || null;
        },
        /** 本会话最新一条带 options 的机器人消息 seq：更早的选项块据此置灰（与 App 同判定） */
        latestOptionsSeq(state) {
            const list = state.messagesBySession[state.currentKey] || [];
            for (let i = list.length - 1; i >= 0; i--) {
                const item = list[i];
                if (item.Direction === 1 && item.options && item.options.length) {
                    return item.Seq;
                }
            }
            return 0;
        },
        hasMoreEarlier: (state) => !!state.hasMoreBySession[state.currentKey]
    },

    actions: {
        /**
         * 建立全局实时态（主框架挂载时调用，幂等）：
         * 载入本地水位/已选态 → 拉会话列表 → 连 WS。会话详情由会话页按需打开。
         */
        async initRealtime() {
            if (this.ready) {
                return;
            }
            const base = originKey();
            this.readSeqs = readJson(`${base}.readSeqs`, {});
            // chosen 兼容旧单值格式（string → [string]），多值集合化后读取即归一
            const rawChosen = readJson(`${base}.chosen`, {});
            const chosen = {};
            Object.keys(rawChosen).forEach((seq) => {
                const val = rawChosen[seq];
                chosen[seq] = Array.isArray(val) ? val : [val];
            });
            this.chosen = chosen;
            this.pickRows = readJson(`${base}.pickRows`, {});
            this.ready = true;
            await Promise.all([this.loadTasks(), this.refreshSessions(), this.loadNotifications()]);
            this.connect();
        },

        /** 站内通知元数据（通知卡标题/分类/jump 的事实源） */
        async loadNotifications() {
            try {
                const res = await NotificationList(1, 200, false);
                const map = {};
                (res?.Data || []).forEach((item) => {
                    if (item.MsgId) {
                        map[item.MsgId] = {
                            title: item.Title || '',
                            category: item.Category || 'system',
                            jump: item.Jump || ''
                        };
                    }
                });
                this.notifyMap = { ...this.notifyMap, ...map };
            } catch (e) {
                // 通知元数据缺失时卡片按正文【标题】降级渲染
            }
        },

        /** 任务列表（会话标题映射：会话键 = 任务 Id 或任务配置的会话名，后者本就可读） */
        async loadTasks() {
            try {
                const res = await GetTask({ PageIndex: 1, PageSize: 500 });
                const map = {};
                (res?.Data || []).forEach((task) => {
                    map[task.Id] = task.Name;
                });
                this.taskMap = map;
            } catch (e) {
                // 标题映射失败不影响会话可用（回退显示会话键原文）
            }
        },

        /** 会话列表快照（服务端按本地水位算未读） */
        async refreshSessions() {
            // §3-8：并发/短窗内合流，复用同一 in-flight Promise（避免重连补拉与连锁刷新重复打服务端）
            if (sessionsInflight) {
                return sessionsInflight;
            }
            this.refreshing = true;
            sessionsInflight = this._refreshSessionsOnce().finally(() => {
                sessionsInflight = null;
                this.refreshing = false;
            });
            return sessionsInflight;
        },

        async _refreshSessionsOnce() {
            const res = await SessionsOverview(this.readSeqs);
            const entries = res?.Sessions || [];
            // 吸收服务端权威水位（2026-09-21 双端同步：对端在离线期间的已读进度追赶）——
            // 只进合并本地并持久化，水位回拨（clear 语义）不在本路径处理（clear 已各自本地清零）
            const next = { ...this.readSeqs };
            let changed = false;
            entries.forEach((item) => {
                const key = item.SessionKey ?? '';
                if ((next[key] || 0) < (item.ReadSeq || 0)) {
                    next[key] = item.ReadSeq;
                    changed = true;
                }
            });
            if (changed) {
                this.readSeqs = next;
                writeJson(`${originKey()}.readSeqs`, next);
            }
            // 旧响应竞态防御（in-flight 合流）：session_read 帧的快路径已把该会话 Unread 置 0，
            // 但防抖刷新可能与一个早于帧到达、仍在途的旧 overview 请求合流——旧响应按过时水位
            // 算出的虚高 Unread 会晚到回写，表现为「角标消了又回来」。本地水位比响应 ReadSeq
            // 新的会话，其响应 Unread 基准已过时：保留现有条目的 Unread，等下次刷新对齐。
            const effectiveRead = changed ? next : this.readSeqs;
            this.sessions = entries.map((item) => {
                const key = item.SessionKey ?? '';
                if ((effectiveRead[key] || 0) > (item.ReadSeq || 0)) {
                    const existing = this.sessions.find((s) => s.SessionKey === key);
                    return withPreview({ ...item, Unread: existing ? existing.Unread : item.Unread });
                }
                return withPreview(item);
            });
            // 首次对齐游标：首屏不拉全量历史，游标直接推到服务端最新
            // （当前会话的消息由 session/messages 单独取，早于游标的消息不会漏）
            this.syncCursor = Math.max(this.syncCursor, res?.MaxSeq || 0);
        },

        /** 打开会话：首屏拉最近一页（仅当未加载过），随后推水位 + 上报已读 */
        async openSession(sessionKey) {
            const key = sessionKey || '';
            this.currentKey = key;
            if (!this.messagesBySession[key]) {
                this.loadingSession = true;
                try {
                    const res = await SessionMessages(key, 0, PAGE_SIZE);
                    const rows = (res?.Messages || []).map((row) => this.decorate(row));
                    this.messagesBySession = { ...this.messagesBySession, [key]: rows };
                    this.hasMoreBySession = { ...this.hasMoreBySession, [key]: !!res?.HasMore };
                } finally {
                    this.loadingSession = false;
                }
            }
            this.advanceWatermark(key);
        },

        /** 上翻加载更早历史 */
        async loadEarlier() {
            const key = this.currentKey;
            const list = this.messagesBySession[key] || [];
            if (this.loadingEarlier || !this.hasMoreBySession[key] || !list.length) {
                return;
            }
            this.loadingEarlier = true;
            try {
                const firstSeq = list[0]?.Seq || 0;
                const res = await SessionMessages(key, firstSeq, PAGE_SIZE);
                const rows = (res?.Messages || []).map((row) => this.decorate(row));
                const existing = this.messagesBySession[key] || [];
                const seen = new Set(existing.map((item) => item.MsgId));
                const merged = [...rows.filter((row) => !seen.has(row.MsgId)), ...existing];
                this.messagesBySession = { ...this.messagesBySession, [key]: merged };
                this.hasMoreBySession = { ...this.hasMoreBySession, [key]: !!res?.HasMore };
            } finally {
                this.loadingEarlier = false;
            }
        },

        /** 断线/启动补拉：按全局游标把错过的消息补齐（空页不推进游标） */
        async catchUp() {
            // §3-8：并发锁——重连与手动触发可能同时发起，复用同一 in-flight，避免重复分页拉取
            if (catchUpInflight) {
                return catchUpInflight;
            }
            catchUpInflight = this._catchUpOnce().finally(() => {
                catchUpInflight = null;
            });
            return catchUpInflight;
        },

        async _catchUpOnce() {
            for (let page = 0; page < CATCH_UP_MAX_PAGES; page++) {
                let res;
                try {
                    res = await SyncMessages(this.syncCursor, CATCH_UP_PAGE);
                } catch (e) {
                    return;
                }
                const rows = res?.Data || [];
                rows.forEach((row) => this.applyMessage(row));
                if (rows.length < CATCH_UP_PAGE) {
                    return;
                }
                this.syncCursor = Math.max(this.syncCursor, res?.MaxSeq || 0);
            }
        },

        /**
         * 发送消息（文本 / 图片 / 文件）；返回是否已提交。
         * @param targetTask 点选选项代发时携带的来源任务 Id（服务端精确路由用，见方案 §2.4）；
         *                   手打 / 重发不带（undefined 字段序列化时被丢弃，不影响老服务端）
         * @param pickLabel  点选代发时携带的选项 label：乐观行与回显行据此渲染为居中系统提示
         *                   （「已选择「label」」）而非用户气泡；手打 / 重发不带
         */
        async send(content, { contentType = 'text', contentText = null, targetTask = null, pickLabel = null } = {}) {
            const text = (content || '').trim();
            const caption = (contentText || '').trim();
            if (!text && contentType === 'text') {
                return false;
            }
            const now = Date.now();
            // 2s 发送节流已于 2026-09-19 按所有者要求移除（客户端与服务端均不限频）：
            // 仅手打保留 in-flight 发送互斥（防同一条在请求未返回时重复提交），
            // 点选代发（pickLabel 非空）豁免互斥——可重复点选语义下连点（逐首保存多首歌）不拦。
            const pick = !!pickLabel;
            if (!pick) {
                if (this.sending) {
                    return false;
                }
                this.sending = true;
            }

            const key = this.currentKey;
            const temp = {
                Seq: null,
                MsgId: `local-${now}`,
                Direction: 2,
                Content: text,
                ContentType: contentType,
                ContentText: caption || null,
                SessionKey: key || null,
                CreateTime: null,
                // 点选代发：乐观行即按系统提示形态渲染，回显行到达后无缝接管
                pickLabel: pickLabel || null,
                pending: true,
                failed: false,
                localTime: now
            };
            this.pushMessage(key, temp);

            try {
                let accepted = false;
                let errorText = '指令提交失败';
                if (contentType === 'text') {
                    // 文本优先走 WS（与 App 一致），未连接时回落 REST；targetTask 为点选来源任务（WS 字段 targetTask）
                    const sent = this.sendFrame({
                        type: 'command',
                        content: text,
                        session: key || undefined,
                        targetTask: targetTask || undefined
                    });
                    if (sent) {
                        accepted = true;
                    }
                }
                if (!accepted) {
                    const res = await SubmitCommand({
                        Content: text,
                        ContentType: contentType,
                        ContentText: caption || undefined,
                        Session: key || undefined,
                        TargetTaskId: targetTask || undefined
                    });
                    accepted = !!res?.Accepted;
                    errorText = res?.Message || errorText;
                }
                if (!accepted) {
                    this.markFailed(key, temp.MsgId, errorText);
                    message.error(errorText);
                    return false;
                }
                if (pickLabel) {
                    // 提交成功才入队（失败重发会再走一次 send，不提前占位）
                    pendingPicks.push({ content: text, label: pickLabel });
                }
                if (contentType !== 'text') {
                    // 附件走 REST：服务端回显不会经 WS 直达时靠补拉兜底
                    await this.catchUp();
                }
                return true;
            } catch (e) {
                this.markFailed(key, temp.MsgId, e?.message || '指令提交失败');
                return false;
            } finally {
                // 点选代发未占用发送互斥，也不释放（防止连点中提前解锁手打的 in-flight）
                if (!pick) {
                    this.sending = false;
                }
            }
        },

        /**
         * 点选选项（可重复点选）：以 reply 文本发送（带选项 label → 消息行渲染为居中系统提示），
         * 成功后把 key 追加进该选项块的已选集合（多值，去重——每个点过的 key 都保留「已选」标记）。
         * 载荷根部的 taskId（服务端注入的产生该消息的真实任务 Id）随发送透传做精确路由，
         * 防合并会话后点选退化为组内多触发；手打 / 重发不带。
         */
        async pickOption(msg, option) {
            const targetTask = parsePayload(msg.Payload)?.taskId || null;
            const ok = await this.send(option.reply || option.key, { targetTask, pickLabel: option.label || option.key });
            if (ok && msg.Seq) {
                const keys = [...new Set([...(this.chosen[msg.Seq] || []), option.key])];
                this.chosen = { ...this.chosen, [msg.Seq]: keys };
                writeJson(`${originKey()}.chosen`, this.chosen);
                // 已选态需即刻反映到当前气泡（chosenKeys 是建行时的快照，写回后必须回填）
                const key = msg.SessionKey || '';
                const list = this.messagesBySession[key] || [];
                this.messagesBySession = {
                    ...this.messagesBySession,
                    [key]: list.map((item) => (item.Seq === msg.Seq ? { ...item, chosenKeys: keys } : item))
                };
            }
            return ok;
        },

        /** 重发失败的乐观气泡（点选代发行带 pickLabel，重发后仍按系统提示形态显示） */
        async resend(msg) {
            const key = msg.SessionKey || '';
            this.removeMessage(key, msg.MsgId);
            return this.send(msg.Content, {
                contentType: msg.ContentType,
                contentText: msg.ContentText,
                pickLabel: msg.pickLabel || null
            });
        },

        chosenKeysOf(msg) {
            return msg?.Seq ? this.chosen[msg.Seq] || [] : [];
        },

        /**
         * 删除会话（会话列表悬浮入口与会话详情顶栏入口共用，2026-09-21 日志删除逻辑调整）：
         * 服务端只删会话实体（会话数据）——消息记录（业务真实产生的数据）保留，删除入口收敛到
         * 日志中心；本地移除会话项 + 清窗口 + 水位归零。该会话的任务再推送消息时会话自动重建
         * （重建带全部保留历史）。当前会话被删时保持 currentKey 不变（右侧停留空视图，后续消息到达即重建）。
         */
        async deleteSession(sessionKey) {
            const key = sessionKey || '';
            const res = await DeleteSessionApi(key);
            if (!res?.Deleted) {
                return res;
            }
            this.messagesBySession = { ...this.messagesBySession, [key]: [] };
            this.hasMoreBySession = { ...this.hasMoreBySession, [key]: false };
            this.sessions = this.sessions.filter((item) => item.SessionKey !== key);
            const nextReadSeqs = { ...this.readSeqs, [key]: 0 };
            this.readSeqs = nextReadSeqs;
            writeJson(`${originKey()}.readSeqs`, nextReadSeqs);
            // 游标回拨保留（消息不再删除后 MaxSeq 恒为当前最大，min 退化为 no-op，防御性兼容）
            this.syncCursor = Math.min(this.syncCursor, res?.MaxSeq || 0);
            message.success('会话已删除（消息记录保留，任务再推送时自动重建）');
            return res;
        },

        /** 本地水位推进（进入会话/收到新消息时），并节流上报服务端已读 */
        advanceWatermark(key) {
            const list = this.messagesBySession[key] || [];
            const lastSeq = list.length ? list[list.length - 1].Seq || 0 : 0;
            if (!lastSeq) {
                return;
            }
            if ((this.readSeqs[key] || 0) < lastSeq) {
                this.readSeqs = { ...this.readSeqs, [key]: lastSeq };
                writeJson(`${originKey()}.readSeqs`, this.readSeqs);
            }
            this.sessions = this.sessions.map((item) =>
                item.SessionKey === key ? { ...item, Unread: 0 } : item
            );
            this.reportRead(key, lastSeq);
        },

        /**
         * 已读上报（3s 节流合批，按会话记 pending——2026-09-21 双端同步批次起调 sessions/read，
         * 服务端按会话推进权威水位并广播 session_read 给其他端；失败静默，下次进水印再报）
         */
        reportRead(key, seq) {
            if (!key || !seq) {
                return;
            }
            const pending = Math.max(this._pendingReadSeqs?.[key] || 0, seq);
            this._pendingReadSeqs = { ...this._pendingReadSeqs, [key]: pending };
            if (this._readTimer) {
                return;
            }
            this._readTimer = setTimeout(() => {
                const batch = this._pendingReadSeqs || {};
                this._pendingReadSeqs = {};
                this._readTimer = null;
                if (Object.keys(batch).length > 0) {
                    SessionsRead(batch).catch(() => {
                        // 上报失败不打断阅读，下次进水印会再报
                    });
                }
            }, READ_THROTTLE_MS);
        },

        /** 建立长连接（幂等） */
        connect() {
            if (socket) {
                return;
            }
            socket = new AppSocket({
                onFrame: (frame) => this.handleFrame(frame),
                onState: (state, detail) => {
                    const wasClosed = this.wsState !== WS_STATE.OPEN;
                    this.wsState = state;
                    this.wsDetail = detail || '';
                    // 重连成功即补拉断线期间错过的消息与未读
                    if (state === WS_STATE.OPEN && wasClosed) {
                        this.catchUp().then(() => this.refreshSessions());
                    }
                }
            });
            socket.connect(AppSocket.rawToken());
        },

        disconnect() {
            if (socket) {
                socket.close();
                socket = null;
            }
            clearMediaCache();
            this.wsState = WS_STATE.CLOSED;
            // §3-7：彻底重置运行态。401 自动登出/主动退出后若不还原，重登录进主框架时
            // initRealtime 因 ready=true 早退、connect 因 socket 残留早退 → WS 双死锁收不到实时帧。
            this.ready = false;
            this.messagesBySession = {};
            this.notifyMap = {};
            this.moves = new Map();
            this.syncCursor = 0;
            if (this._readTimer) {
                clearTimeout(this._readTimer);
                this._readTimer = null;
            }
            if (this._sessionsRefreshTimer) {
                clearTimeout(this._sessionsRefreshTimer);
                this._sessionsRefreshTimer = null;
            }
            this._pendingReadSeqs = {};
        },

        sendFrame(frame) {
            return socket ? socket.send(frame) : false;
        },

        /** WS 下行帧分发 */
        handleFrame(frame) {
            if (!frame || !frame.type) {
                return;
            }
            switch (frame.type) {
                case 'message':
                    this.applyMessage(this.fromFrame(frame));
                    this.ackFrame(frame.msgId);
                    break;
                case 'notify': {
                    // 通知元数据先进映射表，再让 decorate 统一挂到消息行（与 REST 拉取同一条路径）
                    if (frame.msgId) {
                        this.notifyMap = {
                            ...this.notifyMap,
                            [frame.msgId]: {
                                title: frame.title || '',
                                category: frame.category || 'system',
                                jump: frame.jump || ''
                            }
                        };
                    }
                    const row = this.fromFrame({
                        ...frame,
                        content: `【${frame.title || ''}】\n${frame.content || ''}`,
                        contentType: 'notify'
                    });
                    this.applyMessage(row);
                    this.ackFrame(frame.msgId);
                    break;
                }
                case 'message_moved':
                    // 跨会话触发消息迁移：搬本地行 + 视情况自动切换（return 使调用方可 await）
                    return this.applyMessageMove(frame);
                case 'session_read': {
                    // 对端已读（2026-09-21 双端同步）：服务端权威水位推进到 frame.seq——
                    // 本地水位只进对齐（幂等），该会话未读快路径置 0（列表/侧栏角标即时消），
                    // 再防抖合并一次 overview 权威重算（顺带吸收响应 ReadSeq 完成离线追赶）
                    const readKey = frame.session || '';
                    const readSeq = frame.seq || 0;
                    if (!readSeq || (this.readSeqs[readKey] || 0) >= readSeq) {
                        break;
                    }
                    this.readSeqs = { ...this.readSeqs, [readKey]: readSeq };
                    writeJson(`${originKey()}.readSeqs`, this.readSeqs);
                    this.sessions = this.sessions.map((item) =>
                        item.SessionKey === readKey ? { ...item, Unread: 0 } : item
                    );
                    this.scheduleSessionsRefresh();
                    break;
                }
                case 'error':
                    this.wsDetail = frame.content || '';
                    if (frame.content) {
                        message.warning(frame.content);
                    }
                    break;
                default:
                    // sync_done / pong 等无需处理
                    break;
            }
        },

        ackFrame(msgId) {
            if (msgId) {
                this.sendFrame({ type: 'ack', msgId });
            }
        },

        /**
         * session_read 帧后的防抖合并刷新（2026-09-21 双端同步）：500ms 窗口内多会话连续推进
         * 只触发一次 overview——服务端按权威水位重算 Unread（快路径置 0 只是即时代价最小的 UI 抖动消除，
         * 权威值以这次重算为准，侧栏 unreadTotal 派生随之更新）
         */
        scheduleSessionsRefresh() {
            if (this._sessionsRefreshTimer) {
                return;
            }
            this._sessionsRefreshTimer = setTimeout(() => {
                this._sessionsRefreshTimer = null;
                this.refreshSessions().catch(() => {
                    // 刷新失败不打断：本地快路径置 0 已消角标，下次 refreshSessions 再对齐
                });
            }, SESSION_READ_REFRESH_DEBOUNCE_MS);
        },

        /** WS 帧（camelCase）→ 消息行（PascalCase，与 REST 行统一） */
        fromFrame(frame) {
            return {
                Seq: frame.seq || 0,
                MsgId: frame.msgId,
                Direction: frame.direction || 1,
                Content: frame.content,
                ContentType: frame.contentType || 'text',
                ContentText: frame.contentText || null,
                Payload: frame.payload || null,
                SessionKey: frame.session || null,
                CreateTime: frame.createTime || null
            };
        },

        /** 归一化一行消息：解析载荷 + 通知元数据 + 已选态（多值）+ 点选行标记 */
        decorate(row) {
            const payload = parsePayload(row.Payload);
            return {
                ...row,
                options: payload?.options || [],
                poster: payload?.poster || '',
                notifyMeta: row.ContentType === 'notify'
                    ? { ...(this.notifyMap[row.MsgId] || {}), body: row.Content }
                    : null,
                chosenKeys: row.Seq ? this.chosen[row.Seq] || [] : [],
                pickLabel: row.MsgId ? (this.pickRows[row.MsgId] || {}).label || null : null
            };
        },

        /** 应用一条消息（来自 WS 帧 / 补拉 / history） */
        applyMessage(raw) {
            // 乱序防御：迁移帧先到（行未落本地）时按 moves 改写会话键，并清掉来源会话同内容的
            // pending 乐观气泡（否则它永远挂着转圈）
            const move = raw?.MsgId ? this.moves.get(raw.MsgId) : null;
            if (move) {
                raw = { ...raw, SessionKey: move.to || null };
                const fromList = this.messagesBySession[move.from];
                const stale = fromList?.filter(
                    (item) => item.pending && item.Content === raw.Content && item.ContentType === raw.ContentType
                );
                if (stale?.length) {
                    const ids = new Set(stale.map((item) => item.MsgId));
                    this.messagesBySession = {
                        ...this.messagesBySession,
                        [move.from]: fromList.filter((item) => !ids.has(item.MsgId))
                    };
                }
            }
            // 点选代发回显匹配：「接收」行 content 命中匹配队列即写 pickRows（渲染为居中系统提示）；
            // 手打同文本竞态先到先匹配，误标后果仅是显示形态不同（计划 §3）。刷新即清队列。
            if (raw?.Direction === 2 && raw?.MsgId && !this.pickRows[raw.MsgId]) {
                const hit = pendingPicks.findIndex((item) => item.content === raw.Content);
                if (hit >= 0) {
                    this.pickRows = { ...this.pickRows, [raw.MsgId]: { label: pendingPicks[hit].label } };
                    writeJson(`${originKey()}.pickRows`, this.pickRows);
                    pendingPicks.splice(hit, 1);
                }
            }
            const row = this.decorate(raw);
            const key = row.SessionKey || '';
            const list = this.messagesBySession[key];
            if (list) {
                const rows = [...list];
                // 幂等：msgId 去重（WS 直推与 REST 补拉可能重复送达）
                const dup = rows.findIndex((item) => item.MsgId && item.MsgId === row.MsgId);
                // 乐观气泡被服务端接收行替换：同会话同内容且带 seq 的回显
                const optimistic = rows.findIndex(
                    (item) => item.pending && item.Content === row.Content && item.ContentType === row.ContentType
                );
                if (dup >= 0) {
                    rows[dup] = { ...row, pending: false };
                } else if (optimistic >= 0 && row.Direction === 2) {
                    rows[optimistic] = { ...row, pending: false };
                } else {
                    // §3-4：列表已按 Seq 升序，二分插入取代整表 sort（实时帧多为最新 Seq → 命中末尾）
                    insertBySeq(rows, row);
                }
                this.messagesBySession = { ...this.messagesBySession, [key]: rows };
                if (key === this.currentKey) {
                    this.advanceWatermark(key);
                }
            }
            this.mergeSessionEntry(row, key);
            if (row.Seq) {
                this.syncCursor = Math.max(this.syncCursor, row.Seq);
            }
        },

        /** 会话列表条目增量更新（新会话/预览/未读） */
        mergeSessionEntry(row, key) {
            const index = this.sessions.findIndex((item) => item.SessionKey === key);
            if (index < 0) {
                // 新会话（例如脚本首次推送）：拉一次快照补齐标题与未读
                this.refreshSessions();
                return;
            }
            const entry = this.sessions[index];
            const isUnread = row.Direction === 1 && key !== this.currentKey && !row.pending;
            const lastSeq = entry.Last?.Seq || 0;
            // §3-9：仅当新行不早于当前预览时才回写 Last（补拉/echo 乱序不倒退预览），
            // 并以 ...entry.Last 合并、透传 CreateTime（预览时间列读 Last.CreateTime）
            const nextLast =
                row.Seq >= lastSeq
                    ? { ...entry.Last, Seq: row.Seq, Direction: row.Direction, Content: row.Content, ContentType: row.ContentType, ContentText: row.ContentText, CreateTime: row.CreateTime ?? entry.Last?.CreateTime }
                    : entry.Last;
            const next = withPreview({
                ...entry,
                Total: (entry.Total || 0) + (row.pending ? 0 : 1),
                Unread: key === this.currentKey ? 0 : (entry.Unread || 0) + (isUnread ? 1 : 0),
                Last: nextLast
            });
            const list = [...this.sessions];
            list[index] = next;
            list.sort((a, b) => (b.Last?.Seq || 0) - (a.Last?.Seq || 0));
            this.sessions = list;
        },

        pushMessage(key, row) {
            const list = this.messagesBySession[key] || [];
            this.messagesBySession = { ...this.messagesBySession, [key]: [...list, row] };
        },

        /**
         * 跨会话触发消息迁移（2026-09-18 批次）：服务端已把该用户气泡行改键 from → to。
         * 本地搬行（行不在本地则仅记 moves，echo/补拉落行时自愈）→ 刷新会话列表快照
         * （预览/排序以服务端为准；迁移的是用户自己的已读行，未读不受影响）；
         * **正停留在来源会话时自动切换到目标会话**（只切数据态，路由 query 由会话页
         * watch(currentKey) 对齐）。from 缺省 = 默认会话；from === to 忽略。
         */
        async applyMessageMove(frame) {
            const from = frame.from || '';
            const to = frame.to || '';
            if (!frame.msgId || !to || from === to) {
                return;
            }
            this.moves.set(frame.msgId, { from, to });
            if (this.moves.size > 500) {
                this.moves.delete(this.moves.keys().next().value);
            }
            const list = this.messagesBySession[from];
            const row = list?.find((item) => item.MsgId === frame.msgId);
            if (list && row) {
                const rest = list.filter((item) => item.MsgId !== frame.msgId);
                const target = [...(this.messagesBySession[to] || []), row];
                target.sort((a, b) => (a.Seq || 0) - (b.Seq || 0));
                this.messagesBySession = { ...this.messagesBySession, [from]: rest, [to]: target };
            }
            await this.refreshSessions();
            if (this.currentKey === from) {
                await this.openSession(to);
            }
        },

        removeMessage(key, msgId) {
            const list = this.messagesBySession[key] || [];
            this.messagesBySession = {
                ...this.messagesBySession,
                [key]: list.filter((item) => item.MsgId !== msgId)
            };
        },

        markFailed(key, msgId, reason) {
            const list = this.messagesBySession[key] || [];
            this.messagesBySession = {
                ...this.messagesBySession,
                [key]: list.map((item) =>
                    item.MsgId === msgId ? { ...item, pending: false, failed: true, failReason: reason } : item
                )
            };
        }
    }
});
