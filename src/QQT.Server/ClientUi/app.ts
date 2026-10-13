export {};

type Position = { x: number; y: number };
type Player = { id: number; position: Position; hit: boolean; alive: boolean; deathTick: number | null };
type Bubble = { id: number; ownerId: number; position: Position; dueAt: number };
type Move = { playerId: number; from: Position; to: Position };
type GameEvent = { sequence: number; kind: string; tick: number; playerId: number; bubbleId: number; position: Position; from: Position; success: boolean; value: number; reason: string };
type Frame = { type: "frame"; runId: string; name: string; totalTicks: number; status: string; error: string; tick: number; size: number; durationMs: number; animate: boolean; terrain: number[]; players: Player[]; bubbles: Bubble[]; moves: Move[]; events: GameEvent[]; crateCount: number; bubbleCount: number; result?: { passed: boolean; safePlayers: number; walls: number; placed: number; exploded: number; destroyed: number; mapMatches: boolean; deadPlayers: number; deaths: number } };

function element<T extends HTMLElement>(id: string): T { return document.getElementById(id) as T; }
const canvas = element<HTMLCanvasElement>("map");
const context = canvas.getContext("2d")!;
const start = element<HTMLButtonElement>("start");
const connection = element("connection");
const status = element("status");
const tick = element("tick");
const totalTicks = element("total-ticks");
const scenarioTitle = element("scenario-title");
const scenarioFooter = element("scenario-footer");
const progress = element("progress");
const bubbles = element("bubbles");
const crates = element("crates");
const safe = element("safe");
const result = element("result");
const players = element("players");
const log = element("log");
const eventCount = element("event-count");
// 按页面生命周期复用颜色表与渲染状态，绘制循环不构造临时集合。
const colors = ["#91d5ff", "#ffa99b", "#c4b0fa", "#b9ed8b"];
let socket: WebSocket | null = null;
let current: Frame | null = null;
let target: Frame | null = null;
let animationStart = 0;
let flashStart = -10000;
let loggedSequence = 0;
let viewSize = 600;

function gridName(p: Position): string { return String.fromCharCode(65 + p.x) + (p.y + 1); }
function eventText(e: GameEvent): string {
  switch (e.kind) {
    case "move": return `玩家 ${e.playerId}：${gridName(e.from)} → ${gridName(e.position)}`;
    case "blocked": return `玩家 ${e.playerId}：${gridName(e.position)} 被阻挡`;
    case "placed": return `玩家 ${e.playerId} 在 ${gridName(e.position)} 放置泡泡 #${e.bubbleId}，t=${e.tick + 3} 到期`;
    case "exploded": return `泡泡 #${e.bubbleId} 在 ${gridName(e.position)} 爆炸`;
    case "crateDestroyed": return `${gridName(e.position)} 箱子销毁`;
    case "safety": return `玩家 ${e.playerId} 在 ${gridName(e.position)}：${e.success ? "安全" : "受击"}`;
    case "death": return `玩家 ${e.playerId} 在 ${gridName(e.position)} 死亡，保留 ${e.value} 颗在场泡泡`;
    case "actionIgnored": return `玩家 ${e.playerId} 已死亡，第 ${e.value} 轮动作已忽略`;
    default: return `玩家 ${e.playerId} 在 ${gridName(e.position)} 放置${e.reason === "dead" ? "取消：已死亡" : "失败"}`;
  }
}

function applyFrame(frame: Frame, now: number): void {
  const newRun = current?.runId !== frame.runId;
  if (newRun) { loggedSequence = 0; log.replaceChildren(); flashStart = -10000; }
  current = frame;
  tick.textContent = String(frame.tick);
  totalTicks.textContent = frame.totalTicks > 0 ? String(frame.totalTicks) : "—";
  scenarioTitle.textContent = frame.name;
  scenarioFooter.textContent = frame.totalTicks > 0 ? `M0 · ${frame.totalTicks} 轮脚本` : "M0 · 预设脚本";
  progress.style.width = `${frame.totalTicks > 0 ? frame.tick / frame.totalTicks * 100 : 0}%`;
  bubbles.textContent = String(frame.bubbleCount);
  crates.textContent = String(frame.crateCount);
  let safeCount = 0;
  players.replaceChildren();
  for (const p of frame.players) {
    if (p.alive) safeCount++;
    const row = document.createElement("div"); row.className = "player";
    const dot = document.createElement("i"); dot.className = "player-dot"; dot.style.background = colors[p.id - 1];
    row.append(dot, `P${p.id} · ${gridName(p.position)}${p.alive ? "" : ` · 已死亡 t=${p.deathTick}`}`); players.append(row);
  }
  safe.textContent = String(safeCount);
  status.textContent = frame.status === "running" ? "验证进行中" : frame.status === "passed" ? "验证通过" : frame.status === "failed" ? "验证失败" : frame.status === "error" ? "运行错误" : "准备就绪";
  result.className = "result";
  if (frame.status === "passed") {
    result.textContent = `验证通过：存活 ${frame.result!.safePlayers} 人，死亡 ${frame.result!.deadPlayers} 人，${frame.result!.exploded} 颗泡泡已爆炸，${frame.result!.destroyed} 个指定箱子已销毁。`; result.classList.add("success");
  } else if (frame.status === "failed" || frame.status === "error") {
    result.textContent = frame.error || "验证失败：请查看位置、计时和服务端日志。"; result.classList.add("failure");
  } else result.textContent = "运行后按脚本预期核对位置、死亡时刻和泡泡处理。";
  for (const e of frame.events) {
    if (e.sequence <= loggedSequence) continue;
    if (e.kind === "exploded" && e.tick === frame.tick && !newRun) flashStart = now;
    if (e.kind !== "covered") {
      const row = document.createElement("div"); row.className = "log-entry";
      const time = document.createElement("time"); time.textContent = `t=${e.tick}`;
      const text = document.createElement("span"); text.textContent = eventText(e);
      row.append(time, text); log.append(row);
    }
    loggedSequence = e.sequence;
  }
  eventCount.textContent = `${log.childElementCount} 条`;
  log.scrollTop = log.scrollHeight;
  start.textContent = frame.tick > 0 ? "重新运行 ↗" : "开始验证 ↗";
  start.disabled = socket?.readyState !== WebSocket.OPEN || frame.status === "running";
}

function receive(message: MessageEvent<string>): void {
  // JSON.parse 返回的数据由消息持有；渲染只借用它，不复制数组或生成每帧快照。
  const data = JSON.parse(message.data) as Frame | { type: "error"; message: string };
  if (data.type === "error") { result.textContent = data.message; result.className = "result failure"; return; }
  const now = performance.now();
  if (target) { applyFrame(target, now); target = null; }
  if (data.animate && current?.runId === data.runId && data.tick === current.tick + 1) {
    target = data; animationStart = now; start.disabled = true; status.textContent = "验证进行中";
  } else { target = null; applyFrame(data, now); }
}

function connect(): void {
  const protocol = location.protocol === "https:" ? "wss:" : "ws:";
  socket = new WebSocket(`${protocol}//${location.host}/ws/ui`);
  socket.onmessage = receive;
  socket.onopen = () => { connection.textContent = "● 连接正常"; };
  socket.onclose = () => { connection.textContent = "连接已断开 · 刷新页面重连"; start.disabled = true; };
  socket.onerror = () => { connection.textContent = "连接错误"; };
}
start.addEventListener("click", () => {
  if (socket?.readyState === WebSocket.OPEN) { start.disabled = true; socket.send('{"type":"start"}'); }
});

function resize(): void {
  viewSize = canvas.getBoundingClientRect().width;
  const ratio = window.devicePixelRatio || 1;
  canvas.width = Math.round(viewSize * ratio); canvas.height = Math.round(viewSize * ratio);
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
}
window.addEventListener("resize", resize);

function draw(now: number): void {
  let amount = 1;
  if (target) {
    amount = Math.min(1, (now - animationStart) / target.durationMs);
    if (amount >= 1) { applyFrame(target, now); target = null; }
  }
  context.clearRect(0, 0, viewSize, viewSize);
  if (current) {
    const cell = viewSize / 24;
    const origin = cell;
    context.font = `${cell * .32}px "Segoe UI", sans-serif`;
    context.textAlign = "center"; context.textBaseline = "middle";
    context.fillStyle = "#8399a3";
    for (let i = 0; i < 21; i++) {
      context.fillText(String.fromCharCode(65 + i), origin + (i + 1.5) * cell, cell * .4);
      context.fillText(String(i + 1), cell * .4, origin + (i + 1.5) * cell);
    }
    for (let y = -1; y <= 21; y++) for (let x = -1; x <= 21; x++) {
      const px = origin + (x + 1) * cell, py = origin + (y + 1) * cell;
      const outer = x === -1 || y === -1 || x === 21 || y === 21;
      const type = outer ? 1 : current.terrain[y * 21 + x];
      context.fillStyle = type === 1 ? (outer ? "#34454f" : "#4b606a") : type === 2 ? "#9d7a46" : "#1e3039";
      context.fillRect(px + 1, py + 1, cell - 2, cell - 2);
      if (type === 2) {
        context.strokeStyle = "#ba985f"; context.lineWidth = Math.max(1, cell * .045);
        context.strokeRect(px + cell * .16, py + cell * .16, cell * .68, cell * .68);
        context.beginPath(); context.moveTo(px + cell * .18, py + cell * .2); context.lineTo(px + cell * .8, py + cell * .8); context.stroke();
      }
    }
    for (const b of current.bubbles) {
      const x = origin + (b.position.x + 1.5) * cell, y = origin + (b.position.y + 1.5) * cell;
      context.fillStyle = "#68cee1"; context.strokeStyle = "#b7eff8"; context.lineWidth = cell * .045;
      context.beginPath(); context.arc(x, y, cell * .34, 0, Math.PI * 2); context.fill(); context.stroke();
      context.fillStyle = "#12363f"; context.font = `600 ${cell * .33}px sans-serif`; context.fillText(String(b.dueAt - current.tick), x, y);
      context.fillStyle = "#d5f8fc"; context.beginPath(); context.arc(x - cell * .11, y - cell * .12, cell * .07, 0, Math.PI * 2); context.fill();
    }
    for (const p of current.players) {
      let x = p.position.x, y = p.position.y;
      if (target) for (const move of target.moves) if (move.playerId === p.id) {
        x = move.from.x + (move.to.x - move.from.x) * amount; y = move.from.y + (move.to.y - move.from.y) * amount; break;
      }
      const px = origin + (x + 1.5) * cell, py = origin + (y + 1.5) * cell;
      context.fillStyle = "#0d1920"; context.beginPath(); context.arc(px, py + cell * .06, cell * .37, 0, Math.PI * 2); context.fill();
      context.fillStyle = p.hit ? "#6b7378" : colors[p.id - 1];
      context.beginPath(); context.arc(px, py, cell * .31, 0, Math.PI * 2); context.fill();
      context.font = `700 ${cell * .34}px sans-serif`; context.fillStyle = "#172a31"; context.fillText(String(p.id), px, py);
      if (!p.alive) {
        context.strokeStyle = "#efb3ac"; context.lineWidth = Math.max(1.5, cell * .06);
        context.beginPath(); context.moveTo(px - cell * .23, py - cell * .23); context.lineTo(px + cell * .23, py + cell * .23);
        context.moveTo(px + cell * .23, py - cell * .23); context.lineTo(px - cell * .23, py + cell * .23); context.stroke();
      }
    }
    const flash = 1 - (now - flashStart) / 650;
    if (flash > 0) for (const e of current.events) if (e.kind === "covered" && e.tick === current.tick) {
      context.fillStyle = `rgba(255,217,121,${flash * .8})`;
      context.fillRect(origin + (e.position.x + 1) * cell + 1, origin + (e.position.y + 1) * cell + 1, cell - 2, cell - 2);
    }
  }
  requestAnimationFrame(draw);
}

resize(); connect(); requestAnimationFrame(draw);
