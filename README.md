# QQT · M0 模拟验证

可运行的起步验证程序，当前开发分支为 `dev/m0-startup`。保留原四人安全脚本，并新增死亡、动作取消和遗留泡泡验证。

普通 C# 模拟核心＋ASP.NET Core 服务端＋TypeScript/Canvas 浏览器 UI。前后端代码分开，页面资源与 UI WebSocket 由同一进程提供。没有真实 AI 客户端。

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

打开 [http://127.0.0.1:5080](http://127.0.0.1:5080)，点击“开始验证”。脚本指定轮次播放完成后显示验收结果，点击“重新运行”恢复地图与玩家。页面仅有开始／重跑，没有暂停或回放。

## 脚本与配置

- [scripts/m0.json](scripts/m0.json)：原四轮安全场景，最终四人存活。
- [scripts/m0-death.json](scripts/m0-death.json)：六轮死亡场景，验证死亡只发生一次、取消同刻放置和后续动作、已放泡泡继续爆炸。
- 每轮声明四人的方向和最多一个放置点；已死亡玩家的预设动作只记录并忽略。`expected` 声明玩家最终位置／死亡时刻、死亡时遗留泡泡数、泡泡实际放置／爆炸时间、被毁箱子和动作取消次数。
- [appsettings.json](src/QQT.Server/appsettings.json)：监听地址、脚本路径、推进／动画时长、日志分类级别和消息池参数。
- [TODO.md](TODO.md)：未指定工程配置的合法初值和后续范围。
- [AGENTS.md](AGENTS.md)：日志、池化、注释和关键路径验收规范。

构建时脚本、配置和前端复制到输出目录。服务端从该目录读取资源，每次运行重新读取脚本；修改源脚本或源配置后，停止当前服务再构建／启动。当前路径是 M0 固定场景，不支持完整 AI 协议、道具、连锁或任意速度。

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

同时核对 `FINAL_PLAYER` 四人位置，以及 t=1 的四次放置、t=4 的四次爆炸与 C1/S1/C21/S21 箱子销毁。每个脚本按自己的 `expected` 验收，预期死亡不会被误报为验证失败。日志类别为 Script、Tick、Tick.Movement、Tick.Bubble、Tick.Explosion、Tick.Map、Tick.Death，关键日志使用 LoggerMessage 源生成。

模拟状态、事件和临时覆盖采用池化数组；JSON writer 与网络消息缓冲使用对象池和数组池，所有发送结束才归还。框架、日志格式化和浏览器 JSON 解析仍可能分配，不宣称零 GC。

## 范围与验收证据

实现范围以 [M0 评估](docs/最小验证程序_M0范围评估.md) 为准。详细规则和后续首版设计见 [文档索引](docs/README.md)。本次已完成构建、实际脚本日志验收、浏览器完整运行及重跑；见 [M0 验收记录](docs/M0实施与验收记录.md)。

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

关键预期：玩家 1 在 t=4 于 A1 死亡，死亡时仍有一颗泡泡；t=4 的放置被取消，第 5／6 轮动作被忽略；A2 的遗留泡泡在 t=6 爆炸。最终 3 人存活、1 人死亡、泡泡 0、箱子 327，且仅发生一次死亡事件。仍按脚本轮数结束验证，不实现竞技胜负。

详情和实际日志证据见 [死亡处理验收记录](docs/M0死亡处理与验收记录.md)。
