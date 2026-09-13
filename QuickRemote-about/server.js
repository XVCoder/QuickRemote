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
import { gzip as gzipCb } from 'node:zlib';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const gzipAsync = promisify(gzipCb);

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
    version: 'v1.1.66',
    url: 'https://qd.solutionx.top/d/p/4f1d707d-aaa6-4829-9712-4a8bad3719a6',
  },
  android: {
    name: 'Android App',
    version: 'v1.0.77',
    url: 'https://qd.solutionx.top/d/p/861db269-1076-4ac4-aba4-233fdfcb7a36',
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

async function loadStats() {
  await mkdir(DATA_DIR, { recursive: true });

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
 * 对策：文本类响应一律按需 gzip（正文缩到 ~1/4），同时 HTML/CSS/JS 拆分，
 * 保证任何单个响应都远低于该阈值。
 */
const COMPRESSIBLE_EXT = new Set(['.html', '.css', '.js', '.json', '.svg', '.txt']);

function acceptsGzip(req) {
  const ae = req.headers['accept-encoding'];
  return typeof ae === 'string' && /\bgzip\b/i.test(ae);
}

function resolvePath(urlPath) {
  const clean = urlPath.split('?')[0].split('#')[0];
  let p = clean;
  if (p.endsWith('/')) p += 'index.html';
  if (p === '/' || p === '/about' || p === '/index.html') p = '/index.html';
  return path.normalize(path.join(PUBLIC_DIR, p));
}

async function sendJson(req, res, code, body) {
  const raw = Buffer.from(JSON.stringify(body), 'utf8');
  const headers = {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    Vary: 'Accept-Encoding',
  };
  let payload = raw;
  if (acceptsGzip(req)) {
    payload = await gzipAsync(raw);
    headers['Content-Encoding'] = 'gzip';
  }
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

    const headers = {
      'Content-Type': MIME[ext] || 'application/octet-stream',
      'Cache-Control': 'no-cache',
      Vary: 'Accept-Encoding',
    };
    let payload = data;
    if (COMPRESSIBLE_EXT.has(ext) && acceptsGzip(req)) {
      payload = await gzipAsync(data);
      headers['Content-Encoding'] = 'gzip';
    }
    headers['Content-Length'] = payload.length;
    res.writeHead(200, headers);
    res.end(req.method === 'HEAD' ? undefined : payload);
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

await loadStats();

// 反 502 硬化：平台 nginx 的 upstream keepalive（通常 60s）可能复用一条 Node 已关闭的连接 → 间歇 502。
// Node 默认 keepAliveTimeout 仅 5s，必须拉到大于反代的 upstream keepalive；
// headersTimeout 必须 > keepAliveTimeout，否则连接会被提前掐断。
server.keepAliveTimeout = 72000;
server.headersTimeout = 76000;

server.listen(PORT, () => {
  console.log(`QuickRemote About 页面已启动: http://localhost:${PORT}/about`);
  console.log(`统计起始日 ${stats.since}｜累计 ${CLIENT_IDS.map(id => `${id}=${(baseline[id] || 0) + (stats.counted[id] || 0)}`).join(' ')}`);
});
