/**
 * QuickRemote 关于页服务
 *
 * 职责：
 *   1. 托管 about 页面静态资源（public/）
 *   2. /dl/<client>   下载计数 + 302 跳转到 qd 分享链接
 *   3. /api/stats     返回下载统计（各客户端总量 + 每日趋势）
 *
 * 数据分两处：
 *   - data/stats.json  ← 实际计数（运行时状态，必须挂持久化卷 volumes:["data"]）
 *   - seed.json        ← 历史基数配置（包内文件，随发布流程维护）
 */

import http from 'node:http';
import { readFile, stat, writeFile, rename, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PUBLIC_DIR = path.join(__dirname, 'public');
const DATA_DIR = path.join(__dirname, 'data');       // 持久化卷挂载点
const STATS_FILE = path.join(DATA_DIR, 'stats.json');
const SEED_FILE = path.join(__dirname, 'seed.json'); // 历史基数配置（可选）
const PORT = process.env.PORT || 8080;
const TZ = 'Asia/Shanghai';

/**
 * 下载目标 —— 单一事实来源。
 * 页面上的下载按钮是相对路径 dl/<id>，真实地址只在这里维护。
 * 版本号同步发布时一并更新（页面上展示的版本文案仍写在 public/index.html）。
 */
const CLIENTS = {
  pc: {
    name: 'PC 客户端',
    version: 'v1.1.67',
    url: 'https://qd.solutionx.top/d/p/60139f9c-66ea-4974-8b3e-bf45b6983a77',
  },
  android: {
    name: 'Android App',
    version: 'v1.0.80',
    url: 'https://qd.solutionx.top/d/p/b6e62638-78bc-4c9d-86a4-63b061103000',
  },
};
const CLIENT_IDS = Object.keys(CLIENTS);

const DAY_MS = 86400000;
const MAX_TREND_DAYS = 90;
const RETENTION_DAYS = 400;  // stats.json 只保留最近这么多天的明细
const DEDUPE_MS = 3000;      // 同一 IP 对同一客户端的重复请求窗口

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.ico': 'image/x-icon',
  '.txt': 'text/plain; charset=utf-8',
};

/* ------------------------------------------------------------------ 日期工具 */

const dayFmt = new Intl.DateTimeFormat('en-CA', {
  timeZone: TZ, year: 'numeric', month: '2-digit', day: '2-digit',
});
/** 东八区日期 YYYY-MM-DD；不依赖服务器本地时区。 */
const ymd = (d = new Date()) => dayFmt.format(d);

/* ------------------------------------------------------------------ 状态 */

function zeroMap() {
  const o = {};
  for (const id of CLIENT_IDS) o[id] = 0;
  return o;
}
function emptyState() {
  return { since: ymd(), updatedAt: null, counted: zeroMap(), daily: {} };
}

let stats = emptyState();
let baseline = zeroMap();
let writeChain = Promise.resolve();

function mergeNums(base, src) {
  const out = { ...base };
  if (src && typeof src === 'object') {
    for (const id of CLIENT_IDS) {
      const n = Number(src[id]);
      if (Number.isFinite(n) && n >= 0) out[id] = Math.floor(n);
    }
  }
  return out;
}

let dataDirOk = false;

async function loadStats() {
  // ⚠️ 重建应用（remove+deploy）会换系统用户 uid，旧卷目录可能不可读写。
  // 本函数在模块顶层 await —— 一旦抛出即进程退出、端口不监听、整站 502（2026-09-14 事故）。
  // 因此这里只能降级、绝不能抛。
  try {
    await mkdir(DATA_DIR, { recursive: true });
    dataDirOk = true;
  } catch (err) {
    console.error('[stats] data 目录不可用，降级为内存统计:', err.message);
  }

  // 1) 历史基数来自包内配置，每次启动都读，便于随发布流程修正
  try {
    const cfg = JSON.parse(await readFile(SEED_FILE, 'utf8'));
    baseline = mergeNums(zeroMap(), cfg.baseline ?? cfg.total);
    if (typeof cfg.since === 'string') stats.since = cfg.since;
  } catch { /* 无 seed.json 或格式非法：基数为 0 */ }

  // 2) 实际计数读持久化卷
  let raw = null;
  const exists = await stat(STATS_FILE).then(i => i.isFile()).catch(() => false);
  if (exists) {
    try {
      raw = JSON.parse(await readFile(STATS_FILE, 'utf8'));
    } catch (err) {
      console.error('[stats] stats.json 解析失败，重新初始化:', err.message);
    }
  }

  if (raw && typeof raw === 'object') {
    stats.since = typeof raw.since === 'string' ? raw.since : stats.since;
    stats.counted = mergeNums(zeroMap(), raw.counted);
    stats.daily = pruneDaily(raw.daily);
    stats.updatedAt = typeof raw.updatedAt === 'string' ? raw.updatedAt : null;
  } else {
    stats = { ...emptyState(), since: stats.since };
    await persist();
    console.log('[stats] 首次初始化，已创建 stats.json');
  }
}

function pruneDaily(daily) {
  const out = {};
  if (!daily || typeof daily !== 'object') return out;
  const cutoff = ymd(new Date(Date.now() - RETENTION_DAYS * DAY_MS));
  let dropped = 0;
  for (const [date, row] of Object.entries(daily)) {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) continue;
    if (date < cutoff) { dropped++; continue; }
    out[date] = mergeNums(zeroMap(), row);
  }
  if (dropped) console.log(`[stats] 已清理 ${dropped} 天超出保留期的明细`);
  return out;
}

/** 串行化落盘；先写临时文件再 rename，保证原子性。 */
function persist() {
  const snapshot = JSON.stringify(stats);
  writeChain = writeChain
    .then(async () => {
      const tmp = STATS_FILE + '.tmp';
      await writeFile(tmp, snapshot, 'utf8');
      await rename(tmp, STATS_FILE);
    })
    .catch(err => console.error('[stats] 写入失败:', err.message));
  return writeChain;
}

function record(id) {
  const day = ymd();
  const row = (stats.daily[day] ||= zeroMap());
  row[id] = (row[id] || 0) + 1;
  stats.counted[id] = (stats.counted[id] || 0) + 1;
  stats.updatedAt = new Date().toISOString();
  return persist();
}

/* ------------------------------------------------------------------ 请求去重 */

const recent = new Map(); // "ip|client" -> timestamp

function clientIp(req) {
  const xff = req.headers['x-forwarded-for'];
  if (typeof xff === 'string' && xff.trim()) return xff.split(',')[0].trim();
  const real = req.headers['x-real-ip'];
  if (typeof real === 'string' && real.trim()) return real.trim();
  return req.socket.remoteAddress || '-';
}

/** 同一 IP 在 DEDUPE_MS 内对同一客户端的重复请求只计一次。 */
function isDuplicate(ip, id) {
  const key = ip + '|' + id;
  const now = Date.now();
  const last = recent.get(key);
  recent.set(key, now);
  if (recent.size > 4000) {
    for (const [k, t] of recent) if (now - t > DEDUPE_MS) recent.delete(k);
  }
  return last !== undefined && now - last < DEDUPE_MS;
}

/* ------------------------------------------------------------------ 统计计算 */

function buildStats(days) {
  const today = ymd();

  const trend = [];
  for (let i = days - 1; i >= 0; i--) {
    const date = ymd(new Date(Date.now() - i * DAY_MS));
    const row = { date };
    let total = 0;
    for (const id of CLIENT_IDS) {
      const n = stats.daily[date]?.[id] || 0;
      row[id] = n;
      total += n;
    }
    row.total = total;
    trend.push(row);
  }

  const clients = CLIENT_IDS.map(id => ({
    id,
    name: CLIENTS[id].name,
    version: CLIENTS[id].version,
    baseline: baseline[id] || 0,
    counted: stats.counted[id] || 0,
    total: (baseline[id] || 0) + (stats.counted[id] || 0),
    today: stats.daily[today]?.[id] || 0,
    series: trend.map(r => r[id]),
  }));

  const countedTotal = CLIENT_IDS.reduce((s, id) => s + (stats.counted[id] || 0), 0);
  const baselineTotal = CLIENT_IDS.reduce((s, id) => s + (baseline[id] || 0), 0);

  return {
    generatedAt: new Date().toISOString(),
    since: stats.since,
    timezone: TZ,
    updatedAt: stats.updatedAt,
    total: countedTotal + baselineTotal,
    countedTotal,
    baselineTotal,
    today: CLIENT_IDS.reduce((s, id) => s + (stats.daily[today]?.[id] || 0), 0),
    range: { days, from: trend[0].date, to: trend[trend.length - 1].date },
    clients,
    trend,
  };
}

/* ------------------------------------------------------------------ HTTP */

/**
 * ⚠️ 平台反向代理对 /app/{id}/ 路径的响应体有 32KB（32768 字节）截断，
 * 超过部分直接丢弃且不报错 —— 页面从 29.7KB 涨到 39.4KB 后曾整页断尾。
 * 对策：HTML/CSS/JS 拆分，保证任何单个响应都远低于该阈值
 * （HTML 19.2K / CSS 15.7K / JS 4.5K）。
 *
 * ⚠️ 502 双故障模型（2026-09-13 实测，v1.0.111~115 排查全程）：
 *   故障一（已修复，勿回退）：响应带 Content-Encoding: gzip 时，平台对落地页
 *   响应做解压→注入→再压处理会失败并回 502，且按 Accept-Encoding 分变体缓存——
 *   浏览器全带此头 → 浏览器全 502，curl 不带 → 正常。对策：Node 永不 gzip。
 *   故障二（平台侧行为，无法代码修复）：每次蓝绿升级会清落地页缓存并立即回源，
 *   若回源瞬间路由未收敛（新端口刚切换），拉到的 502 会被缓存，/about 由此持续
 *   502（css/js 等普通路径不受影响、约 10 分钟内自行收敛）。
 *   → 对策：①发版后等 ≥10 分钟再验证 /about；②若被投毒，用同一包再 upgrade 一次
 *   可强制刷新缓存（回源时路由已收敛即成功）；③避免短时间内连续多次升级。
 *   Vary 头保留（HTTP 语义正确），但实测并非 502 的开关。
 *   （⚠️ 2026-09-14 更正：Vary 与 Cache-Control 都不是开关，见下条"真凶定案"。）
 *
 * ⚠️ 502 真凶定案（2026-09-14，同一 app 逐项改响应头的对照实验）：
 *   **静态响应只要带 Content-Length，平台反向代理转发即 502**；改为 chunked（不设该头）立刻全绿。
 *   证据矩阵（/about、/style.css、/app.js 表现一致，均为页面级可见故障）：
 *     Cache-Control: no-cache  | Vary ✓ | Content-Length ✓ → 502
 *     Cache-Control: no-store  | Vary ✓ | Content-Length ✓ → 502
 *     Cache-Control: no-store  | Vary ✗ | Content-Length ✓ → 502
 *     Cache-Control: no-store  | Vary ✗ | Content-Length ✗（chunked）→ 200 ✅
 *   已排除 Cache-Control 与 Vary。注意：/api/* 的 JSON 分支带 Content-Length 也正常，
 *   说明平台只对"静态资源转发"这条链路敏感。
 *   → 铁律：静态响应永不设 Content-Length（交由 chunked）。
 */

function resolvePath(urlPath) {
  const clean = urlPath.split('?')[0].split('#')[0];
  let p = clean;
  if (p.endsWith('/')) p += 'index.html';
  if (p === '/' || p === '/about' || p === '/index.html') p = '/index.html';
  return path.normalize(path.join(PUBLIC_DIR, p));
}

async function sendJson(req, res, code, body) {
  const payload = Buffer.from(JSON.stringify(body), 'utf8');
  const headers = {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    Vary: 'Accept-Encoding', // 平台铁律②：响应必须带 Vary，缺失会导致 HTML 502（见上）
  };
  headers['Content-Length'] = payload.length;
  res.writeHead(code, headers);
  res.end(payload);
}

// 请求日志（诊断 502：判断手机/用户的请求是否真正到达 Node）。内存环形缓冲，重启即清。
const REQLOG_CAP = 400;
const reqlog = [];
function logReq(req) {
  try {
    reqlog.push({
      t: new Date().toISOString(),
      ip: clientIp(req),
      m: req.method,
      p: (req.url || '/').split('?')[0],
      ua: String(req.headers['user-agent'] || '').slice(0, 80),
    });
    if (reqlog.length > REQLOG_CAP) reqlog.splice(0, reqlog.length - REQLOG_CAP);
  } catch { /* 日志失败不影响服务 */ }
}

const server = http.createServer(async (req, res) => {
  const urlPath = (req.url || '/').split('?')[0];
  logReq(req);

  try {
    // 下载计数 + 跳转
    const dl = urlPath.match(/^\/dl\/([a-z0-9_-]+)\/?$/i);
    if (dl) {
      const id = dl[1].toLowerCase();
      const target = CLIENTS[id];
      if (!target) {
        res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
        res.end('404 Not Found');
        return;
      }
      // 先记录再响应；落盘异步，不阻塞跳转
      if (req.method === 'GET' && !isDuplicate(clientIp(req), id)) {
        record(id).catch(() => {});
      }
      res.writeHead(302, {
        Location: target.url,
        'Cache-Control': 'no-store, no-cache, must-revalidate',
        'Referrer-Policy': 'no-referrer',
      });
      res.end();
      return;
    }

    // 统计接口
    if (urlPath === '/api/stats' || urlPath === '/api/stats/') {
      const raw = new URL(req.url, 'http://localhost').searchParams.get('days');
      let days = parseInt(raw ?? '14', 10);
      if (!Number.isFinite(days)) days = 14;
      days = Math.min(Math.max(days, 1), MAX_TREND_DAYS);
      sendJson(req, res, 200, buildStats(days));
      return;
    }

    // 请求日志接口（502 诊断：对比“用户看到 502 的时刻”与“请求到达 Node 的记录”）
    if (urlPath === '/api/reqlog' || urlPath === '/api/reqlog/') {
      const nRaw = parseInt(new URL(req.url, 'http://localhost').searchParams.get('n') ?? '60', 10);
      const n = Math.min(Math.max(Number.isFinite(nRaw) ? nRaw : 60, 1), REQLOG_CAP);
      sendJson(req, res, 200, { now: new Date().toISOString(), buffered: reqlog.length, recent: reqlog.slice(-n) });
      return;
    }

    // 存活探针（判断"进程是否活着"比 reqlog 更直接；不含敏感信息）
    if (urlPath === '/api/health' || urlPath === '/api/health/') {
      sendJson(req, res, 200, {
        ok: true,
        pid: process.pid,
        uptimeSec: Math.round(process.uptime()),
        dataDir: dataDirOk ? 'ok' : 'degraded',
        buffered: reqlog.length,
      });
      return;
    }

    if (req.method !== 'GET' && req.method !== 'HEAD') {
      res.writeHead(405, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('405 Method Not Allowed');
      return;
    }

    // 静态资源
    const filePath = resolvePath(req.url);
    if (!filePath.startsWith(PUBLIC_DIR)) {
      res.writeHead(403, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('Forbidden');
      return;
    }
    const info = await stat(filePath);
    if (!info.isFile()) throw new Error('not a file');
    const ext = path.extname(filePath).toLowerCase();
    const data = await readFile(filePath);

    // ⚠️⚠️ 2026-09-14 平台 502 的真凶已用对照实验（同 app 逐项改头）锁死：
    //   静态响应只要带 Content-Length，平台反向代理就在转发时 502；
    //   去掉 Content-Length（改用 chunked）立刻全绿。
    //   证据矩阵（/about /style.css /app.js 同表现）：
    //     no-cache + Vary + Content-Length → 502
    //     no-store + Vary + Content-Length → 502
    //     no-store + 无 Vary + Content-Length → 502
    //     no-store + 无 Vary + 无 Content-Length（chunked）→ 200 ✅
    //   结论：Vary 与 Cache-Control 都不是开关（旧"Vary 铁律"为误判），唯 Content-Length 是。
    //   /api/* 走 JSON 分支，带 Content-Length 也正常——本平台只对"静态资源转发"这条链路敏感。
    //   ⛔ 勿再给静态响应加 Content-Length；大文件/长内容一律交给 chunked。
    //   ⛔ 同理勿把大段内容内联回 HTML（32KB 截断另见文件头注释）。
    const headers = {
      'Content-Type': MIME[ext] || 'application/octet-stream',
      'Cache-Control': 'no-store',
    };
    res.writeHead(200, headers);
    res.end(req.method === 'HEAD' ? undefined : data);
  } catch {
    res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('404 Not Found');
  }
});

// 退出前刷盘（蓝绿升级会停掉旧进程）
let closing = false;
for (const sig of ['SIGTERM', 'SIGINT']) {
  process.on(sig, () => {
    if (closing) return;
    closing = true;
    writeChain.finally(() => process.exit(0));
    setTimeout(() => process.exit(0), 2000).unref();
  });
}

// 反 502 硬化：任何未捕获异常/未处理拒绝都不许杀掉进程——页面必须始终可服务。
process.on('uncaughtException', err => console.error('[fatal-guard] uncaughtException:', (err && err.stack) || err));
process.on('unhandledRejection', err => console.error('[fatal-guard] unhandledRejection:', (err && err.stack) || err));

await loadStats();

// 反 502 硬化：平台 nginx 的 upstream keepalive（通常 60s）可能复用一条 Node 已关闭的连接 → 间歇 502。
// Node 默认 keepAliveTimeout 仅 5s，必须拉到大于反代的 upstream keepalive；
// headersTimeout 必须 > keepAliveTimeout，否则连接会被提前掐断。
server.keepAliveTimeout = 72000;
server.headersTimeout = 76000;

server.listen(PORT, () => {
  console.log(`QuickRemote About 页面已启动: http://localhost:${PORT}/about｜pid=${process.pid}｜data卷=${dataDirOk ? 'ok' : '降级(内存统计)'}`);
  console.log(`统计起始日 ${stats.since}｜累计 ${CLIENT_IDS.map(id => `${id}=${(baseline[id] || 0) + (stats.counted[id] || 0)}`).join(' ')}`);
});
server.on('error', err => console.error('[server] listen 失败:', err.message));
