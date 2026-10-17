"""固定脚本网络验收；每个参赛连接由独立 C# 进程持有。全局数据仅用于断言。"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import threading
import time
import traceback

ROOT = Path(__file__).resolve().parents[2]
CLIENT = ROOT / "src/QQT.ScriptClient/bin/Debug/net10.0/QQT.ScriptClient.dll"
SERVER = ROOT / "src/QQT.Server/bin/Debug/net10.0/QQT.Server.dll"


def require(condition, message):
    if not condition:
        raise AssertionError(message)


class Peer:
    def __init__(self, url, output, name):
        self.name = name
        self.messages = []
        self.events = []
        self.condition = threading.Condition()
        self.stderr = open(output / (name + ".stderr.log"), "w", encoding="utf-8")
        self.process = subprocess.Popen(["dotnet", str(CLIENT), url, str(output / (name + ".jsonl"))], cwd=ROOT,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.stderr,
                                        text=True, encoding="utf-8")
        threading.Thread(target=self._read, daemon=True).start()
        deadline = time.monotonic() + 15
        with self.condition:
            while not self.events:
                require(self.condition.wait(max(0, deadline-time.monotonic())), name + ": connection timeout")
        require(self.events[0]["event"] == "connected", name + ": connection failed")

    def _read(self):
        for line in self.process.stdout:
            value = json.loads(line)
            with self.condition:
                self.events.append(value)
                if value["event"] == "received":
                    self.messages.append(value["message"])
                self.condition.notify_all()

    def command(self, op, **fields):
        self.process.stdin.write(json.dumps({"op": op, **fields}, separators=(",", ":")) + "\n")
        self.process.stdin.flush()

    def send(self, message):
        marker = len(self.messages)
        self.command("send", message=message)
        return marker

    def wait(self, predicate, after=0, timeout=15):
        deadline = time.monotonic() + timeout
        with self.condition:
            while True:
                for value in self.messages[after:]:
                    if predicate(value):
                        return value
                remaining = deadline-time.monotonic()
                require(remaining > 0, f"{self.name}: message timeout; last={self.messages[-1:]}")
                self.condition.wait(remaining)

    def stop(self):
        if self.process.poll() is None:
            self.command("stop")
            try:
                self.process.wait(5)
            except subprocess.TimeoutExpired:
                self.process.kill()
        self.stderr.close()


class Harness:
    def __init__(self, url, output):
        self.url, self.output = url, output
        self.peers = []
        self.serial = 0
        self.ui = self.peer("ui", True)
        self.game = ""
        self.results = []

    def peer(self, name, ui=False):
        p = Peer(self.url + ("/ws/ui" if ui else "/ws/ai"), self.output, name)
        self.peers.append(p)
        return p

    def create(self, config):
        marker = self.ui.send({"type": "match.create", "payload": config})
        state = self.ui.wait(lambda m: m["type"] == "session.state" and m["payload"]["phase"] == "preparing", marker)
        self.game = state["gameId"]
        return self.ui.wait(lambda m: m["type"] == "frame" and m["runId"] == self.game, marker)

    def request(self, peer, kind, payload, round_id=None, game=None, request_id=None):
        self.serial += 1
        rid = request_id or f"r{self.serial}"
        message = {"type": kind, "gameId": self.game if game is None else game, "requestId": rid, "payload": payload}
        if round_id is not None:
            message["roundId"] = round_id
        marker = peer.send(message)
        return peer.wait(lambda m: m.get("requestId") == rid, marker)

    def register(self, count, prefix):
        clients = []
        for i in range(count):
            p = self.peer(f"{prefix}-p{i+1}")
            result = self.request(p, "register.request", {})
            require(result["payload"].get("playerId") == i+1, "registration id mismatch")
            clients.append(p)
        self.ui.wait(lambda m: m["type"] == "session.state" and m["gameId"] == self.game and m["payload"]["registeredCount"] == count)
        return clients

    def start(self, manual):
        marker = self.ui.send({"type": "start", "gameId": self.game})
        return self.ui.wait(lambda m: m["type"] == "session.state" and m["payload"]["phase"] == ("awaitingTick" if manual else "collecting"), marker)

    def click(self, round_id):
        marker = self.ui.send({"type": "tick.demonstrate", "gameId": self.game, "roundId": round_id})
        self.ui.wait(lambda m: m["type"] == "session.state" and m["payload"]["phase"] == "collecting" and m["payload"]["nextRoundId"] == round_id, marker)

    def observation(self, peer, round_id):
        return peer.wait(lambda m: m["type"] == "round.started" and m["gameId"] == self.game and m["roundId"] == round_id)["payload"]

    def frame(self, tick):
        return self.ui.wait(lambda m: m["type"] == "frame" and m["runId"] == self.game and m["tick"] == tick)

    def submit(self, peer, plan, round_id):
        result = self.request(peer, "action.submit", plan, round_id)["payload"]
        require(result == {"success": True, "locked": True, "plan": plan, "error": None}, f"plan mismatch: {result}")

    def abort(self):
        marker = self.ui.send({"type": "match.abort", "gameId": self.game})
        self.ui.wait(lambda m: m["type"] == "session.state" and m["payload"]["phase"] == "aborted", marker)

    def passed(self, name, details):
        self.results.append({"scenario": name, "passed": True, "details": details})
        print("PASS", name, flush=True)

    def minimal(self):
        spec = json.loads((ROOT / "scripts/battle/minimal.json").read_text(encoding="utf-8"))
        initial = self.create(spec["config"])
        require([p["position"] for p in initial["players"]] == spec["spawns"], "seed 42 spawn mapping")
        clients = self.register(2, "minimal")
        self.start(True)
        require(not any(m["type"] == "round.started" for p in clients for m in p.messages), "first round opened before click")
        self.click(1)
        for p in clients:
            observation = self.observation(p, 1)
            require(observation["time"] == {"numerator":0,"denominator":1}, "round 1 time")
        for p, plan in zip(clients, spec["plans"]):
            self.submit(p, plan, 1)
        frame = self.frame(1)
        exp = spec["expected"]
        require(frame["time"] == exp["time"] and [p["position"] for p in frame["players"]] == exp["positions"], "minimal final position/time")
        require(sum(p["alive"] for p in frame["players"]) == exp["alive"] and frame["bubbleCount"] == exp["bubbles"] and frame["crateCount"] == exp["crates"], "minimal counts")
        self.abort()
        for p in clients:
            p.stop()
        self.passed(spec["name"], "two real C# clients; exact time/positions/counts")

    def window(self, clients, round_id, seconds=1.2):
        counts = [sum(m["type"] == "round.started" for m in p.messages) for p in clients]
        deadline = time.monotonic() + seconds
        with self.ui.condition:
            while time.monotonic() < deadline:
                self.ui.condition.wait(deadline-time.monotonic())
        require(counts == [sum(m["type"] == "round.started" for m in p.messages) for p in clients], f"unexpected observation during wait at round {round_id}")
        latest = next(m for m in reversed(self.ui.messages) if m["type"] == "frame" and m["runId"] == self.game)
        require(latest["time"] == {"numerator":round_id-1,"denominator":1}, "logical clock advanced while waiting")

    @staticmethod
    def privacy(observation):
        require(set(observation) == {"time","self","participantIds","visible"}, "extra observation payload fields")
        require(set(observation["self"]) == {"playerId","position","speed","bubbleCapacity","activeBubbleCount","blastRange","bubbleLifetime","visionRadius","actionPoints"}, "self fields")
        require(set(observation["visible"]) == {"cells","players","bubbles","items"}, "visible fields")
        for player in observation["visible"]["players"]:
            require(set(player) == {"playerId","position"}, "paid player fields leaked")
        for bubble in observation["visible"]["bubbles"]:
            require(set(bubble) == {"queryId","position"}, "paid bubble fields leaked")
        p = observation["self"]
        for cell in observation["visible"]["cells"]:
            require(set(cell) == {"position","terrain"} and abs(cell["position"]["x"]-p["position"]["x"]) <= p["visionRadius"] and abs(cell["position"]["y"]-p["position"]["y"]) <= p["visionRadius"], "hidden cell leaked")
        ids = observation["participantIds"] + [v["playerId"] for v in observation["visible"]["players"]]
        require(len(set(ids)) == len(ids) and p["playerId"] not in ids, "participant IDs overlap")

    def query(self, peer, payload, round_id=1, **kwargs):
        return self.request(peer, "query.request", payload, round_id, **kwargs)["payload"]

    def information(self):
        spec = json.loads((ROOT / "scripts/battle/information.json").read_text(encoding="utf-8"))
        for manual in (False, True):
            self.create({**spec["config"],"manualTickAdvance":manual})
            clients = self.register(3, f"info-{manual}")
            self.start(manual)
            if manual:
                self.click(1)
            obs = self.observation(clients[0], 1)
            self.privacy(obs)
            require(obs["participantIds"] == [3] and obs["visible"]["players"] == [{"playerId":2,"position":{"x":2,"y":1}}], "visible/outside player split")
            require(obs["visible"]["bubbles"] == [{"queryId":1,"position":{"x":1,"y":1}}] and not obs["visible"]["items"], "bubble/item privacy")
            expected_cells = [{"position":{"x":x,"y":y},"terrain":"empty"} for y in range(4) for x in range(4)]
            require(obs["visible"]["cells"] == expected_cells, "clipped cells and hidden contents")
            for step in spec["queries"]:
                q = {"targetType":step["targetType"],"targetId":step["targetId"]}
                result = self.query(clients[0], q, request_id=step.get("requestId"))
                expected_data = spec["expectedPlayers"][step["targetId"]-1] if step["targetType"] == "player" else {"ownerId":1,"blastRange":1,"dueAt":{"numerator":3,"denominator":1}}
                require(result == {**q,"success":True,"cost":step["cost"],"actionPoints":step["balance"],"data":expected_data,"error":None}, f"query expected mismatch: {result}")
            failures = [({"targetType":"player","targetId":2},{},"insufficientActionPoints"),({"targetType":"bubble","targetId":999},{},"invalidTarget"),({"targetType":"player","targetId":2},{"game":"old-game"},"wrongGame"),({"targetType":"player","targetId":2},{"round_id":999},"wrongRound")]
            for payload, options, code in failures:
                result = self.query(clients[0], payload, **options)
                require(not result["success"] and result["cost"] == 0 and result["actionPoints"] == 0 and result["data"] is None and result["error"]["code"] == code, f"failure accounting: {result}")
            for plan in spec["invalidPlans"]:
                result = self.request(clients[0], "action.submit", plan, 1)["payload"]
                require(result == {"success":False,"locked":False,"plan":None,"error":{"code":"invalidPlan","message":"invalidPlan"}}, "invalid plan locked")
            self.submit(clients[0], spec["plan"], 1)
            result = self.query(clients[0], {"targetType":"bubble","targetId":1})
            require(result["error"]["code"] == "planLocked" and result["actionPoints"] == 0 and result["cost"] == 0 and result["data"] is None, "query after lock")
            self.submit(clients[0], spec["plan"], 1)
            changed = self.request(clients[0], "action.submit", {"direction":"right","placements":[{"x":2,"y":1}]}, 1)["payload"]
            require(changed["plan"] == spec["plan"] and changed["locked"], "locked plan replaced")
            self.submit(clients[1], spec["plan"], 1)
            self.window(clients, 1)
            state = next(m for m in reversed(self.ui.messages) if m["type"] == "session.state" and m["gameId"] == self.game)
            require(state["payload"]["waitingPlayerIds"] == [3], "N-1 barrier")
            self.submit(clients[2], spec["plan"], 1)
            frame = self.frame(1)
            require(frame["time"] == spec["expected"]["time"] and frame["bubbleCount"] == 2 and [p["position"] for p in frame["players"]] == [p["position"] for p in spec["expectedPlayers"]], "one tick result")
            require(not any(e["kind"] in ("placed","exploded","move") for e in frame["events"]), "wait actions had effects")
            if manual:
                self.ui.wait(lambda m:m["type"]=="session.state" and m["payload"]["phase"]=="awaitingTick" and m["payload"]["nextRoundId"]==2)
                self.window(clients, 2)
            else:
                for p in clients:
                    require(self.observation(p,2)["self"]["actionPoints"] == 10, "new-round budget")
            for i in (1,2):
                clients[i].command("close")
                frame = self.ui.wait(lambda m:m["type"]=="frame" and m["runId"]==self.game and any(e["kind"]=="death" and e["playerId"]==i+1 for e in m["events"]))
                require(frame["time"] == spec["expected"]["time"] and frame["bubbleCount"] == 2, "disconnect advanced clock or removed bombs")
            ended = clients[0].wait(lambda m:m["type"]=="match.ended")["payload"]
            require(ended["winnerId"] == 1 and ended["time"] == spec["expected"]["time"], "disconnect winner")
            require(not any(e["kind"] == "exploded" for e in frame["events"]), "waiting triggered explosion")
            for p in clients:
                p.stop()
            self.passed(spec["name"] + ("-manual" if manual else "-auto"), "privacy; seven exact fields; fees 8,5,4,3,0; locks; N-1 barrier; disconnect at t=1")

    def main_match(self, browser=False):
        spec = json.loads((ROOT / "scripts/battle/main.json").read_text(encoding="utf-8"))
        traces = []
        for manual in (False, True):
            if browser:
                print("BROWSER_CREATE", "manual" if manual else "auto", flush=True)
                marker=len(self.ui.messages)
                state=self.ui.wait(lambda m:m["type"]=="session.state" and m["gameId"]!=self.game and m["payload"]["phase"]=="preparing" and m["payload"]["manualTickAdvance"]==manual,marker,120)
                self.game=state["gameId"]
                initial=self.ui.wait(lambda m:m["type"]=="frame" and m["runId"]==self.game)
            else:
                initial = self.create({**spec["config"],"manualTickAdvance":manual})
            require([p["position"] for p in initial["players"]] == spec["spawns"], "main golden spawn mapping")
            clients = self.register(4, f"main-{manual}")
            if browser:
                print("BROWSER_START", "manual" if manual else "auto",flush=True)
                self.ui.wait(lambda m:m["type"]=="session.state" and m["gameId"]==self.game and m["payload"]["phase"]==("awaitingTick" if manual else "collecting"),timeout=120)
            else:
                self.start(manual)
            for round_id, plans in enumerate(spec["rounds"],1):
                if manual:
                    if round_id == 1:
                        self.window(clients, 1)
                    if browser:
                        print("BROWSER_TICK",round_id,flush=True)
                        self.ui.wait(lambda m:m["type"]=="session.state" and m["gameId"]==self.game and m["payload"]["phase"]=="collecting" and m["payload"]["nextRoundId"]==round_id,timeout=120)
                    else:
                        self.click(round_id)
                for i,p in enumerate(clients):
                    obs = self.observation(p,round_id)
                    self.privacy(obs)
                    position = spec["spawns"][i] if round_id == 1 else spec["positions"][round_id-2][i]
                    own_count = int(2 <= round_id <= 4 or (round_id in (8,9) and i != 1))
                    expected_self = {"playerId":i+1,"position":position,"speed":1,"bubbleCapacity":3,"activeBubbleCount":own_count,"blastRange":1,"bubbleLifetime":{"numerator":3,"denominator":1},"visionRadius":2,"actionPoints":10}
                    require(obs["self"] == expected_self and obs["time"] == {"numerator":round_id-1,"denominator":1}, f"round {round_id} self")
                    require(obs["participantIds"] == [j for j in range(1,5) if j != i+1] and obs["visible"]["players"] == [] and obs["visible"]["items"] == [], "main visibility")
                    expected_bubbles = []
                    if own_count:
                        expected_bubbles = [{"queryId":1,"position":spec["positions"][0][i]}]
                    require(obs["visible"]["bubbles"] == expected_bubbles, "main visible bubbles")
                    if round_id == spec["queryRound"]:
                        target_id = spec["outsideTargets"][i]
                        for kind,target,cost,balance in (("bubble",1,1,9),("player",target_id,3,6),("bubble",1,1,5)):
                            response = self.query(p,{"targetType":kind,"targetId":target},round_id,request_id=f"repeat-{i}" if kind=="bubble" else None)
                            data = {"ownerId":i+1,"blastRange":1,"dueAt":{"numerator":4,"denominator":1}} if kind=="bubble" else {"playerId":target_id,"position":spec["positions"][0][target_id-1],"speed":1,"bubbleCapacity":3,"blastRange":1,"visionWidth":5,"activeBubbleCount":1}
                            require(response == {"targetType":kind,"targetId":target,"success":True,"cost":cost,"actionPoints":balance,"data":data,"error":None}, "main query data/fee")
                for p,plan in zip(clients,plans):
                    self.submit(p,plan,round_id)
                frame = self.frame(round_id)
                require(frame["time"] == {"numerator":round_id,"denominator":1} and [p["position"] for p in frame["players"]] == spec["positions"][round_id-1], f"main tick {round_id} positions/time")
                require(frame["bubbleCount"] == spec["bubbleCounts"][round_id-1] and frame["crateCount"] == (329 if round_id<4 else 325), "main tick counts")
                if round_id == 4:
                    destroyed = [e["position"] for e in frame["events"] if e["kind"]=="crateDestroyed"]
                    require(sorted(destroyed,key=lambda p:(p["y"],p["x"])) == sorted(spec["destroyedCrates"],key=lambda p:(p["y"],p["x"])) and all(p["alive"] for p in frame["players"]), "t=4 crates and safety")
            exp=spec["expected"]
            require(frame["ended"] and frame["winnerId"]==exp["winnerId"] and frame["time"]==exp["time"], "main actual competitive result")
            require(sum(c==1 for c in frame["terrain"])==exp["walls"] and sum(not p["alive"] for p in frame["players"])==exp["dead"] and sum(p["alive"] for p in frame["players"])==exp["alive"], "main final counts")
            for kind,key in (("placed","placed"),("exploded","exploded"),("crateDestroyed","destroyed"),("death","dead")):
                require(sum(e["kind"]==kind for e in frame["events"])==exp[key], "main event totals: "+kind)
            require(all(e["tick"]==exp["time"] for e in frame["events"] if e["kind"]=="death"), "main death times")
            require([e["tick"] for e in frame["events"] if e["kind"]=="placed"] == [{"numerator":1,"denominator":1}]*4+[{"numerator":6,"denominator":1}]*3, "exact placement times")
            for p in clients:
                p.wait(lambda m:m["type"]=="match.ended")
            self.window(clients,10,.2)
            if browser: print("BROWSER_END", "manual" if manual else "auto",flush=True)
            traces.append({"clients":[[{"type":m["type"],"roundId":m.get("roundId"),"payload":m["payload"]} for m in p.messages] for p in clients],"events":frame["events"],"terrain":frame["terrain"],"players":frame["players"]})
            for p in clients:
                p.stop()
            self.passed(spec["name"]+("-manual" if manual else "-auto"), "t=9 P2 wins; walls=100 crates=325 placed=7 exploded=7 destroyed=4 dead=3; no round 10")
        require(traces[0]==traces[1], "auto/manual normalized protocol and simulation traces differ")
        (self.output/"main-normalized.json").write_text(json.dumps(traces[0],ensure_ascii=False),encoding="utf-8")

    def lifecycle(self):
        spec=json.loads((ROOT/"scripts/battle/lifecycle.json").read_text(encoding="utf-8"))
        wait=spec["waitPlan"]
        for n in spec["roomSizes"]:
            first=self.create({"playerCount":n,"manualTickAdvance":True,"seed":42})
            require(len(first["players"])==n and [p["position"] for p in first["players"]]==spec["goldenSpawns"][:n], "actual player count/spawns")
            clients=self.register(n-1,f"room-{n}")
            marker=self.ui.send({"type":"start","gameId":self.game})
            rejected=self.ui.wait(lambda m:m["type"]=="error",marker)
            require(rejected["payload"]["error"]["code"]=="notReady", "underfilled start accepted")
            # 准备断线释放首席，替补必须得到同一编号，不产生死亡。
            clients[0].command("close")
            self.ui.wait(lambda m:m["type"]=="session.state" and m["payload"]["registeredCount"]==n-2 and m["gameId"]==self.game)
            replacement=self.peer(f"replacement-{n}")
            require(self.request(replacement,"register.request",{})["payload"]["playerId"]==1,"preparation seat not reused")
            clients[0]=replacement
            last=self.peer(f"last-{n}")
            require(self.request(last,"register.request",{})["payload"]["playerId"]==n,"last seat")
            clients.append(last)
            marker=len(self.ui.messages)
            extra=self.peer(f"extra-{n}")
            reply=self.request(extra,"register.request",{})["payload"]
            require(not reply["success"] and reply["error"]["code"]=="roomFull","extra registration accepted")
            require(not any(m["type"]=="round.started" for p in clients for m in p.messages),"full room auto started")
            self.start(True)
            other=self.peer(f"other-ui-{n}",True)
            first_mark=len(clients[0].messages)
            self.ui.send({"type":"tick.demonstrate","gameId":self.game,"roundId":1})
            other.send({"type":"tick.demonstrate","gameId":self.game,"roundId":1})
            for p in clients:
                self.observation(p,1)
            self.ui.wait(lambda m:m["type"]=="session.state" and m["payload"]["phase"]=="collecting",marker)
            for p in clients:
                self.submit(p,wait,1)
            frame=self.frame(1)
            require(len(frame["players"])==n and all(p["alive"] for p in frame["players"]) and not any(e["kind"]=="death" for e in frame["events"]),"fake participants/deaths")
            marker=self.ui.send({"type":"tick.demonstrate","gameId":self.game,"roundId":1})
            self.ui.wait(lambda m:m["type"]=="error",marker)
            self.window(clients,2,.15)
            require(all(sum(m["type"]=="round.started" for m in p.messages)==1 for p in clients),"duplicate click accumulated permit")
            # UI 消失不影响参赛者；重连仍可从等待按钮继续。
            self.ui.stop(); other.stop()
            self.ui=self.peer(f"reconnected-ui-{n}",True)
            state=self.ui.wait(lambda m:m["type"]=="session.state")
            require(state["payload"]["phase"]=="awaitingTick" and state["payload"]["registeredCount"]==n,"UI disconnect eliminated player")
            self.click(2)
            for p in clients:
                self.observation(p,2)
                self.submit(p,wait,2)
            self.frame(2)
            for i in range(n-1,0,-1):
                clients[i].command("close")
                frame=self.ui.wait(lambda m:m["type"]=="frame" and m["runId"]==self.game and not m["players"][i]["alive"])
                require(frame["time"]=={"numerator":2,"denominator":1},"awaiting tick disconnect advanced time")
            clients[0].wait(lambda m:m["type"]=="match.ended")
            require(frame["winnerId"]==1 and frame["ended"],"waiting button did not resolve outcome")
            old_game=self.game
            for p in clients:
                p.stop()
            # 同种子再次建局，旧局命令不能启动新局。
            repeated=self.create({"playerCount":n,"manualTickAdvance":True,"seed":42})
            require([p["position"] for p in repeated["players"]]==spec["goldenSpawns"][:n],"seed changed on repeated creation")
            marker=self.ui.send({"type":"tick.demonstrate","gameId":old_game,"roundId":3})
            require(self.ui.wait(lambda m:m["type"]=="error",marker)["payload"]["error"]["code"]=="wrongGame","old game affected new game")
            self.abort()
            self.passed(f"lifecycle-room-{n}","underfill/full/replacement; seed; manual first/duplicate/old gate; UI reconnect; waiting drop; new game isolation")
        for invalid in spec["invalidPlayerCounts"]:
            marker=self.ui.send({"type":"match.create","payload":{"playerCount":invalid}})
            self.ui.wait(lambda m:m["type"]=="error",marker)
        self.passed("lifecycle-invalid-counts","1, 5, and 2.5 rejected")
        self.create(spec["sameCell"])
        clients=self.register(3,"same-cell")
        self.start(False)
        observation=self.observation(clients[0],1)
        require(observation["visible"]["bubbles"]==[{"queryId":1,"position":{"x":1,"y":1}},{"queryId":2,"position":{"x":1,"y":1}}],"same-cell IDs collapsed")
        for target,owner in ((1,1),(2,3)):
            result=self.query(clients[0],{"targetType":"bubble","targetId":target})
            require(result["data"]["ownerId"]==owner,"same-cell ID detail mismatch")
        self.submit(clients[1],wait,1)
        clients[2].command("close")
        self.ui.wait(lambda m:m["type"]=="frame" and m["runId"]==self.game and not m["players"][2]["alive"])
        result=self.query(clients[0],{"targetType":"player","targetId":3})
        require(result["error"]["code"]=="targetEliminated" and result["actionPoints"]==8 and result["cost"]==0 and result["data"] is None,"dropped target or budget reset")
        result=self.query(clients[0],{"targetType":"bubble","targetId":2})
        require(result["data"]=={"ownerId":3,"blastRange":1,"dueAt":{"numerator":3,"denominator":1}} and result["actionPoints"]==7,"retained bubble frozen details")
        repeated=self.request(clients[1],"action.submit",{"direction":"right","placements":[]},1)["payload"]
        require(repeated["plan"]==wait,"other player's lock reset by drop")
        self.submit(clients[0],wait,1)
        self.frame(1)
        require(self.observation(clients[0],2)["participantIds"]==[] and self.observation(clients[0],2)["visible"]["players"]==[{"playerId":2,"position":{"x":2,"y":1}}],"eliminated ID present in new round")
        result=self.query(clients[0],{"targetType":"bubble","targetId":1},2)
        require(result["cost"]==1 and result["actionPoints"]==9,"new round reused ID")
        old=self.query(clients[0],{"targetType":"bubble","targetId":1},1)
        require(old["error"]["code"]=="wrongRound" and old["cost"]==0 and old["data"] is None and old["actionPoints"]==9,"old-round query not isolated")
        # 自动模式 UI 关闭后继续保持收集，不淘汰参赛者。
        self.ui.stop(); self.ui=self.peer("auto-reconnected-ui",True)
        state=self.ui.wait(lambda m:m["type"]=="session.state")
        require(state["payload"]["phase"]=="collecting" and state["payload"]["waitingPlayerIds"]==[1,2],"automatic UI reconnect changed collection")
        clients[1].command("close")
        clients[0].wait(lambda m:m["type"]=="match.ended")
        for p in clients:p.stop()
        self.passed("lifecycle-frozen-targets","same-cell distinct IDs; lock/budget retained after drop; retained bubbles; old round rejected; eliminated IDs omitted")
        self.create(spec["executionFailures"])
        clients=self.register(2,"execution")
        self.start(True); self.click(1)
        observation=self.observation(clients[0],1)
        require(observation["visible"]["items"]==[] and any(c["terrain"]=="crate" and c["position"]=={"x":1,"y":2} for c in observation["visible"]["cells"]),"hidden crate item leaked")
        self.submit(clients[0],spec["geometricPlan"],1); self.submit(clients[1],wait,1)
        frame=self.frame(1)
        require(frame["players"][0]["position"]=={"x":1,"y":1} and frame["bubbleCount"]==1,"blocked/capacity execution changed world")
        require(any(e["kind"]=="blocked" and e["tick"]=={"numerator":1,"denominator":4} for e in frame["events"]),"block time")
        require(any(e["kind"]=="placementSkipped" and e["reason"]=="capacity" and e["tick"]=={"numerator":0,"denominator":1} for e in frame["events"]),"capacity was not execution failure")
        require(any(e["kind"]=="placementSkipped" and e["reason"]=="notReached" and e["tick"]=={"numerator":1,"denominator":1} for e in frame["events"]),"unreached center not skipped")
        self.abort()
        for p in clients:p.stop()
        self.passed("lifecycle-geometric-vs-execution","valid nominal plan locked; wall returns at exact speed; capacity skip; hidden item stays private")

    def tick_limit(self):
        spec=json.loads((ROOT/"scripts/battle/lifecycle.json").read_text(encoding="utf-8"))["tickLimit"]
        self.create({k:v for k,v in spec.items() if k in ("playerCount","manualTickAdvance","seed")})
        clients=self.register(2,"limit")
        self.start(False)
        for round_id in range(1,spec["expectedTick"]+1):
            for p in clients:
                self.observation(p,round_id)
                self.submit(p,{"direction":"wait","placements":[]},round_id)
        frame=self.frame(500)
        require(frame["ended"] and frame["winnerId"] is None and frame["time"]=={"numerator":500,"denominator":1} and all(p["alive"] for p in frame["players"]),"500 tick draw")
        for p in clients:
            end=p.wait(lambda m:m["type"]=="match.ended")["payload"]
            require(end["reason"]=="tickLimit", "tick limit end reason")
        self.window(clients,501,.2)
        for p in clients:p.stop()
        self.passed("lifecycle-500-tick-limit","configured 1ms start interval; 500 actual network rounds; two alive draw; no 501")

    def browser_sizes(self):
        for n in (2,3):
            print("BROWSER_DISPLAY_CREATE",n,flush=True)
            marker=len(self.ui.messages)
            state=self.ui.wait(lambda m:m["type"]=="session.state" and m["gameId"]!=self.game and m["payload"]["phase"]=="preparing" and m["payload"]["playerCount"]==n,marker,120)
            self.game=state["gameId"]
            clients=self.register(n,f"browser-display-{n}")
            print("BROWSER_DISPLAY_START",n,flush=True)
            self.ui.wait(lambda m:m["type"]=="session.state" and m["gameId"]==self.game and m["payload"]["phase"]=="awaitingTick",timeout=120)
            for i in range(n-1,0,-1):
                clients[i].command("close")
                self.ui.wait(lambda m:m["type"]=="frame" and m["runId"]==self.game and not m["players"][i]["alive"])
            clients[0].wait(lambda m:m["type"]=="match.ended")
            print("BROWSER_DISPLAY_END",n,flush=True)
            for p in clients:p.stop()
            self.passed(f"browser-player-display-{n}","real browser checks actual player cards")

    def verify_records(self):
        directory=self.output/"server-jsonl"
        require(directory.exists(), "server JSONL directory missing")
        records={}
        for file in directory.glob("*.jsonl"):
            rows=[json.loads(line) for line in file.read_text(encoding="utf-8").splitlines()]
            require(rows, "empty server JSONL")
            records[file.stem]=rows
            configuration=next(r["message"] for r in rows if r["recordType"]=="configuration")
            frames=[m for peer in self.peers for m in peer.messages if m["type"]=="frame" and m["runId"]==file.stem]
            if frames:
                require(configuration["playerCount"]==len(frames[0]["players"]) and configuration["spawns"]==[{"playerId":p["id"],"position":p["position"]} for p in frames[0]["players"]], "recorded config/spawns differ from global initial frame")
                recorded_events={r["message"]["sequence"]:r["message"] for r in rows if r["recordType"]=="event"}
                for event in frames[-1]["events"]:
                    require(recorded_events.get(event["sequence"])==event,"global event and JSONL differ")
        checked=0
        for peer in self.peers:
            registration=next((m for m in peer.messages if m["type"]=="register.result" and m["payload"]["success"]),None)
            if registration is None:continue
            game=registration["gameId"]; player=registration["payload"]["playerId"]
            require(game in records,"client game missing server record")
            outgoing=[r["message"] for r in records[game] if r["recordType"]=="outgoing" and r["playerId"]==player]
            cursor=0
            for message in peer.messages:
                while cursor<len(outgoing) and outgoing[cursor]!=message:cursor+=1
                require(cursor<len(outgoing),f"client {peer.name} response missing from server JSONL: {message['type']}")
                cursor+=1; checked+=1
        ui_commands=[r["message"] for rows in records.values() for r in rows if r["recordType"]=="uiCommand"]
        for peer in self.peers:
            for event in peer.events:
                if event["event"]=="sent" and event["message"]["type"] in ("start","tick.demonstrate","match.abort"):
                    require(event["message"] in ui_commands,"UI control missing server record")
        (self.output/"evidence-report.json").write_text(json.dumps({"status":"passed","games":len(records),"clientMessagesChecked":checked,"configurationAndEvents":"matched"},indent=2),encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--only", default="all", choices=["minimal", "information", "main", "lifecycle", "limit", "browser", "all"])
    parser.add_argument("--url", default="ws://127.0.0.1:5080")
    parser.add_argument("--output", default="artifacts/battle-verification")
    parser.add_argument("--external-server", action="store_true")
    parser.add_argument("--tick-interval", type=int, default=1000)
    args = parser.parse_args()
    output = (ROOT / args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    report = {"status": "running", "scenarios": []}
    server = None
    server_log = open(output / "server.log", "w", encoding="utf-8")
    harness = None
    try:
        if not args.external_server:
            env = os.environ.copy()
            env["Scenario__TickIntervalMilliseconds"] = str(args.tick_interval)
            env["Urls"] = args.url.replace("ws://", "http://")
            env["Battle__RecordDirectory"] = str(output / "server-jsonl")
            server = subprocess.Popen(["dotnet", str(SERVER), "--battle"], cwd=ROOT, env=env, stdout=server_log, stderr=subprocess.STDOUT)
            # 只用于进程启动连接就绪，场景先后均通过协议屏障。
            import urllib.request
            deadline = time.monotonic() + 15
            while True:
                try:
                    urllib.request.urlopen(args.url.replace("ws://", "http://"), timeout=1).close()
                    break
                except OSError:
                    require(time.monotonic() < deadline, "server startup timeout")
                    time.sleep(.05)
        harness = Harness(args.url, output)
        if args.only in ("all", "minimal"):
            harness.minimal()
        if args.only in ("all", "information"):
            harness.information()
        if args.only in ("all", "main"):
            harness.main_match()
        if args.only in ("all", "lifecycle"):
            harness.lifecycle()
        if args.only == "browser":
            harness.main_match(True)
            harness.browser_sizes()
        if args.only == "limit":
            harness.tick_limit()
        harness.verify_records()
        report["status"] = "passed"
    except Exception as error:
        report["status"] = "failed"
        report["firstDifference"] = str(error)
        report["traceback"] = traceback.format_exc()
        # 先固定失败证据和服务端停止阶段，再关闭连接；清理不制造竞技胜者。
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        if harness and harness.game:
            try:
                harness.abort()
            except Exception:
                pass
        print(report["traceback"], flush=True)
    finally:
        if harness:
            report["scenarios"] = harness.results
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        if harness:
            for peer in harness.peers:
                peer.stop()
        if server:
            server.terminate()
            server.wait(10)
        server_log.close()
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
