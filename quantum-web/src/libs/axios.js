import axios from 'axios';
import { clearLoginSession } from './authSession.js';
import { message, loadingBar } from '@/libs/naive-discrete';

class HttpRequest {
    constructor(baseUrl = '') {
        this.baseUrl = baseUrl;
        this.queue = [];
    }
    getInsideConfig() {
        const config = {
            baseURL: this.baseUrl,
            headers: {
                //
            }
        };
        return config;
    }
    /** 信封错误统一处置：401 清 token + 回登录，其余提示业务错误并拒绝（普通响应与 blob 嗅探共用） */
    rejectByEnvelope(data) {
        if (data?.Code == 401) {
            clearLoginSession();
            return Promise.reject(data.Message || '未授权');
        }
        message.error(data?.Message || '请求失败');
        return Promise.reject(data?.Message || '请求失败');
    }
    /**
     * blob 下载响应的错误信封嗅探：鉴权失败/业务异常时服务端仍把 JSON 信封装进 Blob，
     * 若直接放行，用户会下载到一个装着 {"Code":401} 的「文件」且永远不会被登出。
     * 按**正文字节**嗅探（声明的 Content-Type 不可信：任务导出 ExportAsync 就是 JSON 内容配
     * text/xml，401 信封也可能被标成 application/octet-stream）：首字节（跳过 UTF-8 BOM 与
     * 前导空白）是 `{` 才尝试宽松解析，解析失败/非信封对象一律原样放行——真实 XML/CSV/zip/
     * 图片都不会以 `{` 开头，首格以 `{` 开头的 CSV 也解析不成信封对象。真实文件流完好。
     */
    async guardBlobResponse(response) {
        const blob = response.data;
        if (!blob || blob.size > 512 * 1024) {
            return response;
        }
        // 明确二进制类型（zip/图片/音视频等）先做一个首字节闸门：取前 64 字节，
        // 跳过 UTF-8 BOM（EF BB BF）与 ASCII 空白后若首字节不是 '{'(0x7B) 就直接放行
        // ——真实二进制不付全文 UTF-8 解码成本（信封体必然以对象开头）。
        const type = (blob.type || '').toLowerCase();
        const binaryTyped = type && !type.includes('json') && !type.includes('text') && !type.includes('xml');
        if (binaryTyped) {
            let head;
            try {
                head = new Uint8Array(await blob.slice(0, 64).arrayBuffer());
            } catch (e) {
                return response; // 切片不可用（空 blob/环境不支持）不阻断下载
            }
            let i = 0;
            if (head[0] === 0xef && head[1] === 0xbb && head[2] === 0xbf) {
                i = 3; // UTF-8 BOM
            }
            while (i < head.length && (head[i] === 0x20 || head[i] === 0x09 || head[i] === 0x0a || head[i] === 0x0d)) {
                i++;
            }
            if (head[i] !== 0x7b) {
                return response;
            }
        }
        let text;
        try {
            text = await blob.text();
        } catch (e) {
            return response; // 读取失败不阻断下载
        }
        // UTF-8 BOM 与前导空白之后才是有效首字节（信封体必然以对象开头）
        const body = text.replace(/^\uFEFF/, '');
        if (!/^\s*\{/.test(body)) {
            return response;
        }
        let data = null;
        try {
            data = JSON.parse(body);
        } catch (e) {
            return response; // 首字节是 { 但整体不是 JSON（如首格以 { 开头的 CSV），照常放行
        }
        if (!data || typeof data !== 'object' || Array.isArray(data) || data.Code == null || data.Code == 200) {
            return response;
        }
        return this.rejectByEnvelope(data);
    }
    destroy(url, noLoading = false) {
        if (!noLoading) {
            this.queue.pop(); // 弹出遵循先进先出原则
            setTimeout(() => {
                if (!this.queue.length) {
                    loadingBar.finish();
                }
            }, 300);
        }
    }
    interceptors(instance, url) {
        instance.interceptors.request.use(
            (config) => {
                // 检查是否需要显示 loading
                const noLoading = config.noLoading || false;

                if (!noLoading) {
                    this.queue.push(url); // 把url添加到队列
                    setTimeout(() => {
                        if (this.queue.length) {
                            loadingBar.start();
                        }
                    }, 500);
                }
                config.headers.Authorization =
                    localStorage.getItem('accessToken');
                return config;
            },
            (error) => {
                return Promise.reject(error);
            }
        );
        // 响应拦截
        instance.interceptors.response.use(
            async (response) => {
                const noLoading = response.config.noLoading || false;
                this.destroy(url, noLoading);
                // 文件流下载（responseType: 'blob'）：先嗅探错误信封（401/业务异常仍是 JSON 体），
                // 命中即按信封契约处置；真实文件照常放行完整 response
                if (response.config.responseType === 'blob') {
                    return this.guardBlobResponse(response);
                }
                if (response.status == 200) {
                    if (response.data.Code == 200) {
                        return response.data.Data;
                    } else {
                        // 401 与非 401 业务失败统一走信封处置（提示 + reject）
                        return this.rejectByEnvelope(response.data);
                    }
                }
                // 非 200 HTTP 不得静默原样返回：提示后 reject，避免调用方把错误响应当数据用
                message.error(`请求失败（HTTP ${response.status}）`);
                return Promise.reject(response);
            },
            (error) => {
                const noLoading =
                    (error.config && error.config.noLoading) || false;
                this.destroy(url, noLoading);
                if (error.message == 'Request failed with status code 500') {
                    message.error('请求服务失败，服务端异常！');
                } else if (error.response && error.response.status == 401) {
                    clearLoginSession();
                } else {
                    message.error(error.message || '网络请求失败');
                }
                return Promise.reject(error);
            }
        );
    }
    request(options) {
        const instance = axios.create();
        options = Object.assign(this.getInsideConfig(), options);
        this.interceptors(instance, options.url);
        return instance(options);
    }
}
export default HttpRequest;
