"""统一配置的真实网络验收；复用 ScriptClient，不实现决策 AI。"""
import argparse
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.request

sys.dont_write_bytecode = True
from battle import Harness, ROOT, SERVER, require


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", default="artifacts/configuration")
    args = parser.parse_args()
    output = (ROOT / args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    directory = output / "selected-config"
    directory.mkdir(exist_ok=True)
    path = directory / "server.json"
    base = json.loads((ROOT / "src/QQT.Server/appsettings.json").read_text(encoding="utf-8-sig"))
    base["Scenario"]["ScriptPath"] = "fixture.json"
    base["Battle"]["RecordDirectory"] = "../server-jsonl"
    shutil.copyfile(ROOT / "scripts/m0.json", directory / "fixture.json")
    checks = []
    server = None
    harness = None
    report = {"status": "failed", "checks": checks}

    def write(config):
        path.write_text(json.dumps(config, ensure_ascii=False, indent=2), encoding="utf-8")

    def checked(name):
        checks.append(name)
        print("PASS", name, flush=True)

    try:
        write(base)
        # 相对配置文件路径从仓库工作目录选择，文件内脚本路径从该配置目录解析。
        selected = subprocess.run(["dotnet", str(SERVER), "--verify", "--config", str(path.relative_to(ROOT))],
                                  cwd=ROOT, capture_output=True, timeout=20)
        (output / "selected-script.log").write_bytes(selected.stdout + selected.stderr)
        require(selected.returncode == 0, "selected config relative script path failed")
        # 使用隔离的二进制目录，避免改写其他任务可能正在使用的默认文件。
        isolated = output / "isolated-server"
        isolated.mkdir(exist_ok=True)
        for file in SERVER.parent.iterdir():
            if file.is_file():
                shutil.copyfile(file, isolated / file.name)
        (isolated / "appsettings.json").write_text("{broken-default", encoding="utf-8")
        isolated_result = subprocess.run(["dotnet", str(isolated / SERVER.name), "--verify", "--config", str(path)],
                                         cwd=ROOT, capture_output=True, timeout=20)
        (output / "broken-default-with-selected-config.log").write_bytes(isolated_result.stdout + isolated_result.stderr)
        require(isolated_result.returncode == 0, "selected config depends on broken default JSON")
        checked("selected-file-and-relative-script")
        invalid = [("missing-file", None), ("incomplete-file", {}), ("negative-fee", copy.deepcopy(base)),
                   ("zero-lifetime", copy.deepcopy(base)), ("invalid-items", copy.deepcopy(base))]
        invalid[2][1]["Queries"]["BubbleCost"] = -1
        invalid[3][1]["Player"]["BubbleLifetime"] = 0
        invalid[4][1]["Items"]["counts"]["vision"] = -1
        for name, config in invalid:
            if config is not None:
                write(config)
            result = subprocess.run(["dotnet", str(SERVER), "--verify", "--config", str(path if config is not None else directory / "missing.json")],
                                    cwd=ROOT, capture_output=True, timeout=20)
            (output / (name + ".log")).write_bytes(result.stdout + result.stderr)
            require(result.returncode != 0, name + " silently accepted")
        checked("invalid-configurations-rejected")

        config = copy.deepcopy(base)
        config["Urls"] = "http://127.0.0.1:5083"
        config["Match"].update(PlayerCount=3, ManualTickAdvance=True, Seed=42, MaxTicks=2)
        config["Scenario"].update(TickIntervalMilliseconds=1, AnimationMilliseconds=1300)
        config["Player"].update(Speed=2, BubbleCapacity=4, BlastRange=2, VisionRadius=3,
                                BubbleLifetime={"numerator": 5, "denominator": 2})
        config["Queries"].update(ActionPoints=7, BubbleCost=2, VisiblePlayerCost=3, OutsidePlayerCost=4)
        config["Ui"].update(CatchUpThreshold=3, PlaybackSpeed=1.25)
        config["Initial"] = {"map": "empty", "crates": [{"x": 3, "y": 1}], "players": [
            {"playerId": 1, "position": {"x": 1, "y": 1}}, {"playerId": 2, "position": {"x": 2, "y": 1}},
            {"playerId": 3, "position": {"x": 20, "y": 20}}],
            "bubbles": [{"ownerId": 1, "position": {"x": 1, "y": 3}, "placedAt": 0,
                         "dueAt": {"numerator": 5, "denominator": 2}}]}
        config["Items"] = {"mode": "fixed", "placements": [{"type": "vision", "position": {"x": 3, "y": 1}}]}
        write(config)
        # 故意提供冲突的旧环境覆盖，选定文件仍必须是唯一来源。
        env = os.environ.copy()
        env.update(Urls="http://127.0.0.1:5999", Queries__ActionPoints="99")
        with open(output / "server.log", "w", encoding="utf-8") as log:
            server = subprocess.Popen(["dotnet", str(SERVER), "--battle", "--config", str(path)], cwd=ROOT,
                                      env=env, stdout=log, stderr=subprocess.STDOUT)
            deadline = time.monotonic() + 15
            while True:
                require(server.poll() is None, "configured server exited")
                try:
                    urllib.request.urlopen(config["Urls"], timeout=.5).close()
                    break
                except OSError:
                    require(time.monotonic() < deadline, "configured server startup timeout")
                    time.sleep(.05)
            harness = Harness("ws://127.0.0.1:5083", output)
            state = harness.ui.wait(lambda m: m["type"] == "session.state")["payload"]
            require(state["defaults"] == {"playerCount": 3, "manualTickAdvance": True, "seed": 42}, "UI defaults differ from config")
            initial = harness.create({})
            require(initial["durationMs"] == 1300 and initial["catchUpThreshold"] == 3 and initial["playbackSpeed"] == 1.25, "UI timing not configured")
            require(initial["itemAllocation"]["generated"]["vision"] == 1 and initial["items"][0]["status"] == "hidden", "file Items not inherited")
            clients = harness.register(3, "configured")
            rules = next(m["payload"]["rules"] for m in clients[0].messages if m["type"] == "register.result")
            require(rules == {"maxTicks": 2, "actionPoints": 7, "bubbleCost": 2, "visiblePlayerCost": 3, "outsidePlayerCost": 4}, "public rules not configured")

            changed = copy.deepcopy(config)
            changed["Match"].update(PlayerCount=2, MaxTicks=1)
            changed["Player"]["Speed"] = 3
            changed["Queries"]["ActionPoints"] = 9
            changed["Initial"]["players"].pop()
            changed["Items"] = {"mode": "fixed", "placements": []}
            write(changed)
            harness.start(True)
            require(not any(m["type"] == "round.started" for p in clients for m in p.messages), "manual gate bypassed")
            harness.click(1)
            observed = harness.observation(clients[0], 1)
            own = observed["self"]
            require((own["speed"], own["bubbleCapacity"], own["blastRange"], own["visionRadius"], own["actionPoints"]) == (2, 4, 2, 3, 7), "initial attributes not configured or snapshot mutated")
            require(own["bubbleLifetime"] == {"numerator": 5, "denominator": 2} and observed["visible"]["items"] == [], "fraction lifetime or item privacy")
            for balance in (5, 3):
                result = harness.query(clients[0], {"targetType": "bubble", "targetId": 1}, request_id="same-id")
                require(result["success"] and result["cost"] == 2 and result["actionPoints"] == balance and result["data"]["dueAt"] == {"numerator": 5, "denominator": 2}, "configured bubble charge")
            result = harness.query(clients[0], {"targetType": "player", "targetId": 2})
            require(result["cost"] == 3 and result["actionPoints"] == 0, "configured visible charge")
            result = harness.query(clients[0], {"targetType": "player", "targetId": 3})
            require(not result["success"] and result["cost"] == 0 and result["actionPoints"] == 0, "insufficient budget changed")
            result = harness.query(clients[1], {"targetType": "player", "targetId": 3})
            require(result["cost"] == 4 and result["actionPoints"] == 3 and result["data"]["position"] == {"x": 20, "y": 20}, "configured outside charge or position")
            wait = {"direction": "wait", "placements": []}
            for p in clients:
                harness.submit(p, wait, 1)
            harness.frame(1)
            harness.click(2)
            for p in clients:
                require(harness.observation(p, 2)["self"]["actionPoints"] == 7, "round budget not reset from frozen config")
                harness.submit(p, wait, 2)
            ended = clients[0].wait(lambda m: m["type"] == "match.ended")["payload"]
            require(ended == {"winnerId": None, "time": {"numerator": 2, "denominator": 1}, "reason": "tickLimit"}, "configured tick limit")
            require(not any(m["type"] == "round.started" and m["roundId"] == 3 for p in clients for m in p.messages), "extra round after limit")
            checked("file-items-attributes-fees-frozen-rounds-and-limit")

            bad_directory = copy.deepcopy(changed)
            bad_directory["Battle"]["RecordDirectory"] = "fixture.json"
            write(bad_directory)
            marker = harness.ui.send({"type": "match.create", "payload": {}})
            require(harness.ui.wait(lambda m: m["type"] == "error", marker)["payload"]["error"]["code"] == "invalidRequest", "invalid record directory not rejected")
            marker = harness.ui.send({"type": "start", "gameId": harness.game})
            require(harness.ui.wait(lambda m: m["type"] == "error", marker)["payload"]["error"]["code"] == "notReady", "invalid directory changed ended phase")
            path.write_text("{invalid-json", encoding="utf-8")
            marker = harness.ui.send({"type": "match.create", "payload": {}})
            require(harness.ui.wait(lambda m: m["type"] == "error", marker)["payload"]["error"]["code"] == "invalidRequest", "bad reload not rejected")
            require(harness.frame(2)["ended"], "bad reload discarded ended world")
            write(changed)
            frame = harness.create({})
            require(len(frame["players"]) == 2 and frame["players"][0]["speed"] == 3 and frame["items"] == [], "new game did not reload config")
            next_clients = harness.register(2, "reloaded")
            harness.start(True); harness.click(1)
            for p in next_clients:
                require(harness.observation(p, 1)["self"]["actionPoints"] == 9, "reloaded budget")
                harness.submit(p, wait, 1)
            require(next_clients[0].wait(lambda m: m["type"] == "match.ended")["payload"]["time"] == {"numerator": 1, "denominator": 1}, "reloaded limit")
            checked("bad-reload-preserves-result-and-next-match-reloads")

            changed["Queries"]["BubbleCost"] = 0
            changed["Items"] = config["Items"]
            write(changed)
            frame = harness.create({"playerCount": 3, "manualTickAdvance": False, "seed": 17,
                                    "initial": config["Initial"], "items": None})
            require(len(frame["players"]) == 3 and frame["items"] == [], "payload items null or initial override failed")
            free_clients = harness.register(3, "free-query")
            harness.start(False)
            harness.observation(free_clients[0], 1)
            free = harness.query(free_clients[0], {"targetType": "bubble", "targetId": 1})
            require(free["success"] and free["cost"] == 0 and free["actionPoints"] == 9, "zero-cost valid query rejected")
            bad = harness.query(free_clients[0], {"targetType": "bubble", "targetId": 999})
            require(not bad["success"] and bad["error"]["code"] == "invalidTarget", "invalid free target accepted")
            harness.abort()
            frame = harness.create({"items": {"mode": "fixed", "placements": []}})
            require(frame["items"] == [], "explicit items object did not replace file items")
            harness.abort()
            frame = harness.create({"initial": None, "items": None})
            require([p["position"] for p in frame["players"]] == [{"x": 0, "y": 0}, {"x": 0, "y": 20}], "file spawn seed not applied on formal map")
            harness.abort()
            checked("payload-overrides-and-zero-cost-target-validation")
            harness.verify_records()
        report["status"] = "passed"
    except Exception as error:
        report["firstDifference"] = str(error)
        if harness and harness.game:
            try:
                state = next(m for m in reversed(harness.ui.messages) if m["type"] == "session.state")
                if state["payload"]["phase"] not in ("idle", "ended", "aborted"):
                    harness.abort()
            except Exception:
                pass
        raise
    finally:
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        if harness:
            for peer in harness.peers:
                peer.stop()
        if server and server.poll() is None:
            server.terminate(); server.wait(10)


if __name__ == "__main__":
    main()
