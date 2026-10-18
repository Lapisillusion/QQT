# QQT · 模拟验证与联机对战

可运行的起步验证程序，当前开发分支为 `dev/m0-startup`。已实现精确分数时间、逐格移动与归属、跨线阻挡、强制回退、中心放置、途中连锁及即时终局。另已实现四类道具、固定／随机箱内分配、下一轮属性激活和局部观察计算。保留四份 M0 与 39 份精确时间场景，新增 15 份道具场景。

普通 C# 模拟核心＋ASP.NET Core 服务端＋TypeScript/Canvas 浏览器 UI。前后端代码分开，页面资源与 UI WebSocket 由同一进程提供。已接入真实 WebSocket 对战协议，并提供不含策略 AI 的独立 C# 脚本客户端。

## 运行

需要 .NET 10 SDK、Node.js/npm。首次安装前端编译依赖：

```powershell
npm.cmd ci --ignore-scripts --no-audit --no-fund --registry=https://registry.npmjs.org --cache .npm-cache
```

在项目根目录构建前端并启动：

```powershell
npm.cmd run build:ui
dotnet run --project src/QQT.Server/QQT.Server.csproj
```

打开 [http://127.0.0.1:5080](http://127.0.0.1:5080)，点击“开始验证”。竞技提前结束或脚本指定轮次播放完成后显示验收结果，点击“重新运行”恢复地图与玩家。页面仅有开始／重跑，没有暂停或回放。

## 联机对战

构建UI后以 `dotnet run --project src/QQT.Server -- --battle` 启动，页面选择2／3／4人、逐帧开关及可选种子并创建；满员后UI手动开始。逐帧时点击“tick演示”才开启该轮观察、决策与模拟。AI连接 `/ws/ai`，每轮可查询冻结详情并提交唯一锁定计划，开局后掉线淘汰，默认最多500 tick，可由 Match.MaxTicks 配置。

完整验收入口：

```powershell
./scripts/verification/verify-battle.ps1
```

严格使用已确认的真实网络脚本、生命周期边界、既有核心回归及真实Chrome界面场景。协议、环境要求、独立预期与实际证据见 [对战运行与验收记录](docs/battle/对战运行与验收记录.md)。

## 脚本与配置

- [scripts/m0.json](scripts/m0.json)：原四轮安全场景，最终四人存活。
- [scripts/m0-death.json](scripts/m0-death.json)：六轮死亡场景，验证死亡只发生一次、取消同刻放置和后续动作、已放泡泡继续爆炸。
- [scripts/m0-chain-victory.json](scripts/m0-chain-victory.json)：声明六轮，在 t=4 玩家 3 获胜后停止，保留未引爆泡泡。
- [scripts/m0-perimeter-clear.json](scripts/m0-perimeter-clear.json)：108 轮，四人各清一条外边，最后玩家 1、2、4 自爆，玩家 3 获胜。
- 每轮声明四人的方向和沿途中心放置列表（同轮同中心最多一颗）；已死亡玩家的预设动作只记录并忽略。`expected` 声明玩家最终位置／死亡时刻、死亡时遗留泡泡数、泡泡实际放置／爆炸时间、被毁箱子和动作取消次数。
- [appsettings.json](src/QQT.Server/appsettings.json)：监听地址、脚本路径、推进／动画时长、日志分类级别和消息池参数。
- [TODO.md](TODO.md)：未指定工程配置的合法初值和后续范围。
- [AGENTS.md](AGENTS.md)：日志、池化、注释和关键路径验收规范。

构建时脚本、配置和前端复制到输出目录。默认读取输出目录 appsettings.json，可用 --config 指定完整替代文件；文件内相对路径统一以配置文件目录解析。每次建局／重跑重新读取并冻结本局参数，监听、日志与网络缓冲修改需重启。详见 [统一配置说明](docs/config/服务端配置说明.md)。不指定 initial 时沿用正式固定地图与默认参数；指定 initial 可运行空地图和自定义出生点、正整数速度、容量、范围、分数寿命的测试环境。另支持视野参数和道具配置。联机和旧脚本共用配置的竞技上限，默认500 tick；旧脚本另受 totalTicks 和 MaxScriptTicks 约束，并核对自身expected。

## 日志验收

无需浏览器，也可直接运行同一脚本与模拟路径，跳过现实播放间隔：

```powershell
dotnet run --project src/QQT.Server/QQT.Server.csproj -- --verify
```

成功返回进程码 0，失败返回 1。可用 `--script` 指定另一个兼容 M0 的 JSON 文件，路径相对当前工作目录：

```powershell
dotnet run --project src/QQT.Server/QQT.Server.csproj -- --verify --script scripts/m0.json
```

最终日志应包含：

```text
VALIDATION ... Passed=True Tick=4 Crates=325 Walls=100 Bubbles=0
SafePlayers=4 Placed=4 Exploded=4 Destroyed=4 MapMatches=True
```

同时核对 `FINAL_PLAYER` 四人位置，以及 t=1 的四次放置、t=4 的四次爆炸与 C1/S1/C21/S21 箱子销毁。每个脚本按自己的 `expected` 验收，预期死亡不会被误报为验证失败。日志类别为 Script、Tick、Tick.Movement、Tick.Bubble、Tick.Explosion、Tick.Map、Tick.Death、Simulation.Match，关键日志使用 LoggerMessage 源生成。

模拟状态、事件和临时覆盖采用池化数组；JSON writer 与网络消息缓冲使用对象池和数组池，所有发送结束才归还。框架、日志格式化和浏览器 JSON 解析仍可能分配，不宣称零 GC。

## 范围与验收证据

最初范围见 [M0 评估](docs/最小验证程序_M0范围评估.md)，当前扩展范围与实机结果见 [精确时间验收记录](docs/精确时间实施与验收记录.md)。详细规则和后续首版设计见 [文档索引](docs/README.md)。本次已完成构建、实际脚本日志验收、浏览器完整运行及重跑；见 [M0 验收记录](docs/M0实施与验收记录.md)。

## 死亡场景

通过日志验证：

```powershell
dotnet run --project src/QQT.Server/QQT.Server.csproj -- --verify --script scripts/m0-death.json
```

在 UI 展示死亡场景时，先停止当前服务，再使用相同脚本启动：

```powershell
dotnet run --project src/QQT.Server/QQT.Server.csproj -- --script scripts/m0-death.json
```

浏览器仍打开本机 5080 端口，脚本名与轮数会自动显示。死亡玩家标记为灰色叉号，列表显示死亡时刻；存活数量与验证是否通过分别展示。

关键预期：玩家 1 在 t=4 于 A1 死亡，死亡时仍有一颗泡泡；t=4 的放置被取消，第 5／6 轮动作被忽略；A2 的遗留泡泡在 t=6 爆炸。最终 3 人存活、1 人死亡、泡泡 0、箱子 327，且仅发生一次死亡事件。此脚本仍有三人存活，因此按脚本轮数结束验证，不产生竞技胜负。

详情和实际日志证据见 [死亡处理验收记录](docs/M0死亡处理与验收记录.md)。

## 连锁与获胜场景

从仓库根构建并直接运行程序集，可避免 `dotnet run` 的项目工作目录影响 `--script` 相对路径：

```powershell
npm.cmd run build:ui
dotnet build QQT.sln
dotnet src/QQT.Server/bin/Debug/net10.0/QQT.Server.dll --verify --script scripts/m0-chain-victory.json
```

展示页面时去掉 `--verify`，打开本机 5080 端口并点击开始。t=4 两条递归连锁和其他自然到期泡泡统一结算，玩家 1、2、4 死亡，玩家 3 在 A20 获胜。最终放置 9、引爆 8、箱子 323、硬墙 100、在场泡泡 1；第 9 颗仍属于玩家 3，放置 t=3、预定到期 t=6，结束后不再推进。

`expected.end` 声明结束时刻与胜者；泡泡 `explodedAt: null` 表示结束现场仍保留，`dueAt` 和 `sourceBubbleId` 核对预定到期与连锁来源。原脚本格式仍兼容。原地等待并选择当前格放置是在轮初执行，不表示轮末延迟放置。

范围 1 的正式脚本无法区分射线被泡泡截断，以及胜者终点放置取消的错误。必要边界使用同一核心的合成夹具检查，不引入游戏参数修改或测试框架：

```powershell
dotnet run --project src/QQT.Core.Checks/QQT.Core.Checks.csproj -- E:/Project/Codex/QQT/scripts/m0-chain-victory.json
```

输入路径须为绝对路径。检查范围 3 射线穿透、墙／箱子截断和旧地图、同格连锁去重、胜者放置取消、同批全灭平局、结束后推进拒绝及重置。实际日志、浏览器完整播放和重跑证据见 [连锁与即时获胜验收记录](docs/M0连锁与即时获胜验收记录.md)。

## 四边清理场景

从仓库根构建后执行完整日志验收：

```powershell
dotnet build QQT.sln
dotnet src/QQT.Server/bin/Debug/net10.0/QQT.Server.dll --verify --script scripts/m0-perimeter-clear.json
```

展示 UI 时去掉 `--verify`，浏览器打开本机 5080 端口并点击开始。默认每轮 1 秒，完整运行约 108 秒。

P1 负责上边 A1→U1，P2 负责右边 U1→U21，P3 负责左边 A21→A1，P4 负责下边 U21→A21。t=100 清完四边的 68 个箱子，爆炸同时波及 32 个内侧箱子。t=105 四人到达各边终点，第 106 轮起点（仍为 t=105）玩家 1、2、4 放置泡泡并原地等待，t=108 自爆，P3 在 A1 获胜。最终放置／引爆均为 71、箱子 229、硬墙 100、泡泡 0。

此场景沿用固定速度 1、范围 1 和寿命 3。用户已授权将 `Scenario.MaxScriptTicks` 从 8 提高到 128；事件与轨迹缓冲以及历史按实际需要从数组池扩容，不再以原单轮 96 条或历史 12288 条作为上限。该轮数上限是验证工具容量，不是竞技 500 tick 上限。默认脚本仍为 `scripts/m0.json`。

实际四个脚本日志回归、108 轮浏览器播放与重跑结果见 [四边清理验收记录](docs/M0四边清理脚本验收记录.md)。

## 精确时间与独立测试环境

[39 份脚本清单](docs/精确时间脚本验收清单.md)逐项对应讨论过的场景和边界，每份 JSON 自带初始条件、四人完整动作及独立预期。36 份不含箱子；另 3 份单箱补充脚本验证清除后不重启、同刻旧地图和后续地图传播。

从仓库根目录运行快慢玩家阻挡与回退场景：

```powershell
npm.cmd run build:ui
dotnet build QQT.sln
dotnet src/QQT.Server/bin/Debug/net10.0/QQT.Server.dll --verify --script scripts/precise-13-fast-blocks-slow.json
```

去掉 `--verify` 可在浏览器播放；玩家状态提示显示实际速度、容量、范围、寿命和质点坐标。日志时间以 `1/10` 等精确分数显示，UI 消息的时间和质点坐标使用分子／分母对象，展示小数不参与裁决。

批量执行全部新场景：

```powershell
Get-ChildItem scripts/precise-*.json | ForEach-Object {
    dotnet src/QQT.Server/bin/Debug/net10.0/QQT.Server.dll --verify --script $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "脚本验收失败：$($_.Name)" }
}
```

测试环境字段及检查点、事件断言说明见 [实施与验收记录](docs/精确时间实施与验收记录.md)。验证通过与竞技结束独立：未声明终局的脚本在指定轮数完成验收，现场可以仍有多人存活，不额外制造死亡来结束比赛。

## 道具与局部观察

已实现速度、容量、范围、视野半径四类 +1 道具。开局只预藏于箱子，每格一件；固定位置配置或按剩余需求数量加权随机分配，位置不足时跳过。箱毁露出、中心批量拾取、下轮激活；视野增益为 R=2→3，即未裁剪时 5×5→7×7。爆炸销毁原有露出道具，同批新露出者保留。

脚本顶层 `items` 完整覆盖 appsettings 的 `Items`，两者均省略时不生成道具。字段示例与生命周期见 [实施计划](docs/items/道具需求与实施计划.md)，实际结果见 [道具验收记录](docs/items/道具实施与验收记录.md)。默认仍运行原安全场景，不擅自设定四类道具的玩法数量。

从仓库根运行集成场景：

```powershell
npm.cmd run build:ui
dotnet build QQT.sln --no-restore
dotnet src/QQT.Server/bin/Debug/net10.0/QQT.Server.dll --verify --script scripts/items/items-14-integrated-rerun.json
```

去掉 `--verify` 后打开本机 5080 端口，查看四类道具露出、逐中心拾取、下一轮属性变化及终局。UI 使用鞋子／炸弹／火焰／眼睛图标及 +1 角标表示四类道具。玩家实时属性看板以 2×2 彩色卡片对应四名玩家，显示移速、火力、泡泡容量、视野大小与半径，并分别提示下轮生效的增益。

局部观察由独立接口 `PrepareRound → Observe` 计算，用于脚本验证；隐藏箱内内容和视野外实体不进入玩家观察。当前 UI 仍为全局观战；AI局部观察已通过对战协议下发，玩家视野切换预览后续实现。

正式地图的三阶段道具与全灭场景见 [脚本15](scripts/items/items-15-official-three-stage-draw.json)：四人依次取得速度／容量／范围增益，同轮双放后移动一次退到离最近泡泡两格，t=22 全灭平局。正式地图、默认出生与初始参数均沿用；[验收记录](docs/items/正式地图三阶段道具验证.md)含范围1对照及浏览器播放／重跑结果。
