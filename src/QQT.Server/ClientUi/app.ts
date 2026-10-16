export {};

type Fraction = { numerator: number; denominator: number };
type ExactPosition = { x: Fraction; y: Fraction };
type Position = { x: number; y: number };
type Player = { id: number; position: Position; hit: boolean; alive: boolean; deathTick: Fraction | null; point: ExactPosition; speed: number; capacity: number; range: number; lifetime: Fraction };
type Bubble = { id: number; ownerId: number; position: Position; dueAt: Fraction };
type Move = { playerId: number; from: ExactPosition; to: ExactPosition; start: Fraction; end: Fraction };
type GameEvent = { sequence: number; kind: string; tick: Fraction; relatedTime: Fraction; point: ExactPosition; roundId: number; playerId: number; bubbleId: number; position: Position; from: Position; success: boolean; value: number; reason: string };
type Frame = { type: "frame"; runId: string; name: string; totalTicks: number; status: string; error: string; tick: number; size: number; time: Fraction; durationMs: number; animate: boolean; terrain: number[]; players: Player[]; bubbles: Bubble[]; moves: Move[]; events: GameEvent[]; crateCount: number; bubbleCount: number; ended: boolean; winnerId: number | null; endTick: Fraction | null; result?: { passed: boolean; safePlayers: number; walls: number; placed: number; exploded: number; destroyed: number; mapMatches: boolean; deadPlayers: number; deaths: number } };

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
// 缓存只在收到服务端快照时更新；绘制循环复用，不分配每帧容器。
const placedEvents = new Map<number, GameEvent>();
const explodedEvents = new Map<number, GameEvent>();
const destroyedTimes = new Map<number, number>();
function number(value: Fraction): number { return value.numerator / value.denominator; }
function exact(value: Fraction): string { return value.denominator === 1 ? String(value.numerator) : `${value.numerator}/${value.denominator}`; }
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
    case "entered": return `玩家 ${e.playerId} 跨线进入 ${gridName(e.position)}`;
    case "returnStarted": return `玩家 ${e.playerId} 遇阻，原速退回 ${gridName(e.position)} 中心`;
    case "returned": return `玩家 ${e.playerId} 已退回 ${gridName(e.position)} 中心，本轮停止移动`;
    case "initialBubble": return `初始泡泡 #${e.bubbleId} 在 ${gridName(e.position)}，t=${exact(e.relatedTime)} 到期`;
    case "move": return `玩家 ${e.playerId}：${gridName(e.from)} → ${gridName(e.position)}`;
    case "blocked": return `玩家 ${e.playerId}：${gridName(e.position)} 被阻挡`;
    case "placed": return `玩家 ${e.playerId} 在 ${gridName(e.position)} 放置泡泡 #${e.bubbleId}，t=${exact(e.relatedTime)} 到期`;
    case "exploded": return `泡泡 #${e.bubbleId} 在 ${gridName(e.position)} 爆炸`;
    case "chained": return `泡泡 #${e.value} 连锁引爆 ${gridName(e.position)} 的泡泡 #${e.bubbleId}`;
    case "matchEnded": return e.playerId > 0 ? `对局结束：玩家 ${e.playerId} 获胜` : "对局结束：全部淘汰，平局";
    case "crateDestroyed": return `${gridName(e.position)} 箱子销毁`;
    case "safety": return `玩家 ${e.playerId} 在 ${gridName(e.position)}：${e.success ? "安全" : "受击"}`;
    case "death": return `玩家 ${e.playerId} 在 ${gridName(e.position)} 死亡，保留 ${e.value} 颗在场泡泡`;
    case "actionIgnored": return `玩家 ${e.playerId} 已死亡，第 ${e.value} 轮动作已忽略`;
    default: return `玩家 ${e.playerId} 在 ${gridName(e.position)} 放置${e.reason === "dead" ? "取消：已死亡" : e.reason === "capacity" ? "失败：容量不足" : "跳过：未到达中心"}`;
  }
}

function applyFrame(frame: Frame, now: number): void {
  const newRun = current?.runId !== frame.runId;
  if (newRun) { loggedSequence = 0; log.replaceChildren(); flashStart = -10000; }
  current = frame;
  flashStart = -10000; // 途中爆炸已按轨迹播放，轮末不重新播放一次。
  tick.textContent = String(frame.tick);
  totalTicks.textContent = frame.totalTicks > 0 ? String(frame.totalTicks) : "—";
  scenarioTitle.textContent = frame.name;
  scenarioFooter.textContent = frame.totalTicks > 0 ? `M0 · ${frame.totalTicks} 轮脚本` : "M0 · 预设脚本";
  progress.style.width = `${frame.ended ? 100 : frame.totalTicks > 0 ? frame.tick / frame.totalTicks * 100 : 0}%`;
  bubbles.textContent = String(frame.bubbleCount);
  crates.textContent = String(frame.crateCount);
  let safeCount = 0;
  players.replaceChildren();
  for (const p of frame.players) {
    if (p.alive) safeCount++;
    const row = document.createElement("div"); row.className = "player";
    const dot = document.createElement("i"); dot.className = "player-dot"; dot.style.background = colors[p.id - 1];
    row.title = `速度 ${p.speed} 格/tick · 容量 ${p.capacity} · 范围 ${p.range} · 寿命 ${exact(p.lifetime)} tick · 质点 (${exact(p.point.x)}, ${exact(p.point.y)})`;
    row.append(dot, `P${p.id} · ${gridName(p.position)}${p.alive ? "" : ` · 已死亡 t=${exact(p.deathTick!)}`}`); players.append(row);
  }
  safe.textContent = String(safeCount);
  status.textContent = frame.status === "running" ? "验证进行中" : frame.status === "passed" ? "验证通过" : frame.status === "failed" ? "验证失败" : frame.status === "error" ? "运行错误" : "准备就绪";
  const outcome = frame.ended ? `${frame.winnerId === null ? "全部淘汰，平局" : `玩家 ${frame.winnerId} 获胜`}（t=${exact(frame.endTick!)}）` : "";
  if (outcome) status.textContent += ` · ${outcome}`;
  result.className = "result";
  if (frame.status === "passed") {
    result.textContent = `验证通过：${outcome ? outcome + "；" : ""}存活 ${frame.result!.safePlayers} 人，死亡 ${frame.result!.deadPlayers} 人，${frame.result!.exploded} 颗泡泡已爆炸，${frame.result!.destroyed} 个指定箱子已销毁。`; result.classList.add("success");
  } else if (frame.status === "failed" || frame.status === "error") {
    result.textContent = frame.error || "验证失败：请查看位置、计时和服务端日志。"; result.classList.add("failure");
  } else result.textContent = "运行后按脚本预期核对位置、死亡时刻和泡泡处理。";
  for (const e of frame.events) {
    if (e.sequence <= loggedSequence) continue;
    if (e.kind === "exploded" && number(e.tick) === number(frame.time) && e.roundId === frame.tick && !newRun) flashStart = now;
    if (e.kind !== "covered" && e.kind !== "state") {
      const row = document.createElement("div"); row.className = "log-entry";
      const time = document.createElement("time"); time.textContent = `t=${exact(e.tick)}`;
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
  if (current?.runId !== data.runId) { placedEvents.clear(); explodedEvents.clear(); destroyedTimes.clear(); }
  for (const event of data.events) {
    if (event.kind === "placed" || event.kind === "initialBubble") placedEvents.set(event.bubbleId, event);
    if (event.kind === "exploded") explodedEvents.set(event.bubbleId, event);
    if (event.kind === "crateDestroyed") destroyedTimes.set(event.position.y * data.size + event.position.x, number(event.tick));
  }
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
    amount = Math.min(1, (now - animationStart) / (target.durationMs * Math.max(0.001, number(target.time) - number(current!.time))));
    if (amount >= 1) { applyFrame(target, now); target = null; }
  }
  context.clearRect(0, 0, viewSize, viewSize);
  if (current) {
    const scene = target ?? current;
    const logicalTime = target ? number(current.time) + (number(target.time) - number(current.time)) * amount : number(current.time);
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
      const destroyedAt = destroyedTimes.get(y * 21 + x);
      const type = outer ? 1 : destroyedAt !== undefined && destroyedAt <= logicalTime ? 0 : current.terrain[y * 21 + x];
      context.fillStyle = type === 1 ? (outer ? "#34454f" : "#4b606a") : type === 2 ? "#9d7a46" : "#1e3039";
      context.fillRect(px + 1, py + 1, cell - 2, cell - 2);
      if (type === 2) {
        context.strokeStyle = "#ba985f"; context.lineWidth = Math.max(1, cell * .045);
        context.strokeRect(px + cell * .16, py + cell * .16, cell * .68, cell * .68);
        context.beginPath(); context.moveTo(px + cell * .18, py + cell * .2); context.lineTo(px + cell * .8, py + cell * .8); context.stroke();
      }
    }
    for (const b of placedEvents.values()) {
      if (number(b.tick) > logicalTime || (explodedEvents.has(b.bubbleId) && number(explodedEvents.get(b.bubbleId)!.tick) <= logicalTime)) continue;
      const x = origin + (b.position.x + 1.5) * cell, y = origin + (b.position.y + 1.5) * cell;
      context.fillStyle = "#68cee1"; context.strokeStyle = "#b7eff8"; context.lineWidth = cell * .045;
      context.beginPath(); context.arc(x, y, cell * .34, 0, Math.PI * 2); context.fill(); context.stroke();
      context.fillStyle = "#12363f"; context.font = `600 ${cell * .33}px sans-serif`; context.fillText((number(b.relatedTime) - logicalTime).toFixed(1), x, y);
    }
    for (const p of current.players) {
      let x = number(p.point.x), y = number(p.point.y);
      if (target) for (const move of target.moves) if (move.playerId === p.id) {
        const startTime = number(move.start), endTime = number(move.end);
        if (logicalTime < startTime) break;
        const fraction = Math.min(1, (logicalTime - startTime) / (endTime - startTime));
        x = number(move.from.x) + (number(move.to.x) - number(move.from.x)) * fraction;
        y = number(move.from.y) + (number(move.to.y) - number(move.from.y)) * fraction;
        if (logicalTime < endTime) break;
      }
      const nextPlayer = scene.players[p.id - 1];
      const dead = nextPlayer.deathTick !== null && number(nextPlayer.deathTick) <= logicalTime;
      const px = origin + (x + 1.5) * cell, py = origin + (y + 1.5) * cell;
      context.fillStyle = "#0d1920"; context.beginPath(); context.arc(px, py + cell * .06, cell * .37, 0, Math.PI * 2); context.fill();
      context.fillStyle = dead ? "#6b7378" : colors[p.id - 1];
      context.beginPath(); context.arc(px, py, cell * .31, 0, Math.PI * 2); context.fill();
      context.font = `700 ${cell * .34}px sans-serif`; context.fillStyle = "#172a31"; context.fillText(String(p.id), px, py);
      if (dead) {
        context.strokeStyle = "#efb3ac"; context.lineWidth = Math.max(1.5, cell * .06);
        context.beginPath(); context.moveTo(px - cell * .23, py - cell * .23); context.lineTo(px + cell * .23, py + cell * .23);
        context.moveTo(px + cell * .23, py - cell * .23); context.lineTo(px - cell * .23, py + cell * .23); context.stroke();
      }
    }
    const flash = target ? .8 : 1 - (now - flashStart) / 650;
    if (flash > 0) for (const e of scene.events) if (e.kind === "covered" && e.roundId === scene.tick && number(e.tick) <= logicalTime && (target ? logicalTime - number(e.tick) < .3 : number(e.tick) === logicalTime)) {
      context.fillStyle = `rgba(255,217,121,${flash * .8})`;
      context.fillRect(origin + (e.position.x + 1) * cell + 1, origin + (e.position.y + 1) * cell + 1, cell - 2, cell - 2);
    }
  }
  requestAnimationFrame(draw);
}

resize(); connect(); requestAnimationFrame(draw);
