"""四个独立 ClientAI 的真实网络对抗；UI 只在创建／开局和终局取证时连接。"""
import argparse
from collections import Counter
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import urllib.request

sys.dont_write_bytecode = True
from battle import Peer, ROOT, require


def read_jsonl(path):
    with open(path, encoding="utf-8") as stream:
        for line in stream:
            if line.strip():
                yield json.loads(line)


def tail(path):
    if not path.exists():
        return []
    with path.open("rb") as stream:
        stream.seek(0, 2)
        end = stream.tell()
        stream.seek(max(0, end - 65536))
        data = stream.read().splitlines()
    result = []
    for line in data:
        try:
            result.append(json.loads(line))
        except (ValueError, UnicodeDecodeError):
            pass
    return result


def audit(path):
    budgets = {}
    locked = set()
    observations = {}
    costs = Counter()
    decisions = []
    results = []
    errors = []
    for row in read_jsonl(path):
        event = row.get("event")
        if row.get("level") in ("Error", "Critical"):
            errors.append(row)
        if event == "decision":
            decisions.append(row)
        if event == "match.ended":
            results.append(row["data"])
        if event not in ("received", "sent"):
            continue
        message = row["message"]
        kind = message["type"]
        round_id = message.get("roundId", 0)
        payload = message["payload"]
        if event == "received":
            if kind == "round.started":
                budgets[round_id] = payload["self"]["actionPoints"]
                observations[round_id] = payload
            elif kind == "query.result":
                require(round_id not in locked, "locked round accepted a query")
                cost = payload["cost"]
                require(payload["actionPoints"] == budgets[round_id] - cost, "query balance mismatch")
                require(payload["actionPoints"] >= 0 and cost in (0, 1, 2, 3), "invalid query cost")
                if payload["success"]:
                    expected = 1 if payload["targetType"] == "bubble" else (2 if any(p["playerId"] == payload["targetId"] for p in observations[round_id]["visible"]["players"]) else 3)
                    require(cost == expected, "query visibility/cost mismatch")
                else:
                    require(cost == 0 and payload["data"] is None, "failed query charged or disclosed data")
                costs[payload["targetType"]] += cost
                budgets[round_id] = payload["actionPoints"]
            elif kind == "action.result":
                require(payload["success"] and payload["locked"], "AI submitted a rejected action")
                locked.add(round_id)
            elif kind == "error":
                errors.append(message)
        elif kind == "query.request":
            require(round_id not in locked, "AI queried after locking")
        elif kind == "action.submit":
            self_state = observations[round_id]["self"]
            origin = self_state["position"]
            direction = payload["direction"]
            points = set()
            for p in payload["placements"]:
                dx, dy = p["x"] - origin["x"], p["y"] - origin["y"]
                require(0 <= p["x"] < 21 and 0 <= p["y"] < 21 and abs(dx) + abs(dy) <= self_state["speed"], "placement bounds")
                require((dx == dy == 0) or direction == "right" and dx > 0 and dy == 0 or direction == "left" and dx < 0 and dy == 0 or direction == "down" and dy > 0 and dx == 0 or direction == "up" and dy < 0 and dx == 0, "placement geometry")
                require((p["x"], p["y"]) not in points, "duplicate placement")
                points.add((p["x"], p["y"]))
    require(not errors, "client errors: " + str(errors[:1]))
    require(len(results) == 1, "missing or repeated authoritative match result")
    require(len(locked) == len(observations), "an observed round did not receive a locked plan")
    elapsed = sorted(d["elapsedMilliseconds"] for d in decisions)
    return {"result": results[0], "roundsObserved": len(budgets), "lockedRounds": len(locked), "queryCosts": dict(costs),
            "decisions": len(decisions), "riskDecisions": sum(d["escapeDepth"] < d["horizon"] or d["counterRisk"] for d in decisions),
            "decisionMilliseconds": {"p50": elapsed[len(elapsed)//2] if elapsed else 0, "p95": elapsed[min(len(elapsed)-1, int(len(elapsed)*.95))] if elapsed else 0, "max": max(elapsed, default=0)}}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--port", type=int, default=5091)
    parser.add_argument("--spawn-seed", type=int, default=1847)
    parser.add_argument("--item-seed", type=int, default=7319)
    parser.add_argument("--timeout", type=int, default=1200)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    if (output / "report.json").exists():
        raise FileExistsError("该目录已有比赛报告，请选择新目录以保留原证据。")
    output.mkdir(parents=True, exist_ok=True)
    url = f"ws://127.0.0.1:{args.port}"
    env = os.environ.copy()
    env.update({"Urls": f"http://127.0.0.1:{args.port}", "Scenario__TickIntervalMilliseconds": "20", "Battle__RecordDirectory": str(output / "server-records")})
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    handles = []
    clients = []
    server = None
    control = None
    game = ""
    match_finished = False
    report = {"status": "incomplete"}
    try:
        server_log = open(output / "server.log", "w", encoding="utf-8")
        handles.append(server_log)
        server = subprocess.Popen(["dotnet", str(ROOT / "src/QQT.Server/bin/Debug/net10.0/QQT.Server.dll"), "--battle"], cwd=ROOT,
                                  env=env, stdout=server_log, stderr=subprocess.STDOUT, creationflags=flags)
        deadline = time.monotonic() + 20
        while True:
            try:
                urllib.request.urlopen(f"http://127.0.0.1:{args.port}", timeout=1).close()
                break
            except OSError:
                require(server.poll() is None and time.monotonic() < deadline, "server failed to start")
                time.sleep(.2)
        control = Peer(url + "/ws/ui", output, "ui-start")
        config = {"playerCount": 4, "manualTickAdvance": False, "seed": args.spawn_seed,
                  "items": {"mode": "random", "seed": args.item_seed, "counts": {"speed": 8, "capacity": 8, "range": 8, "vision": 8}}}
        marker = control.send({"type": "match.create", "payload": config})
        state = control.wait(lambda m: m["type"] == "session.state" and m["payload"]["phase"] == "preparing", marker)
        game = state["gameId"]
        report.update({"gameId": game, "configuration": config, "strategySeeds": [101, 211, 307, 419], "tickIntervalMilliseconds": 20})
        for i, seed in enumerate(report["strategySeeds"], 1):
            std = open(output / f"p{i}.stdout.log", "w", encoding="utf-8")
            handles.append(std)
            process = subprocess.Popen(["dotnet", str(ROOT / "src/QQT.ClientAI/bin/Debug/net10.0/QQT.ClientAI.dll"),
                                        "--server", url + "/ws/ai", "--game-id", game, "--seed", str(seed), "--log", str(output / f"p{i}.jsonl"),
                                        "--transport-log-level", "Debug", "--search-log-level", "Debug"], cwd=ROOT, stdout=std, stderr=subprocess.STDOUT, creationflags=flags)
            clients.append(process)
            control.wait(lambda m: m["type"] == "session.state" and m["gameId"] == game and m["payload"]["registeredCount"] == i, timeout=30)
        control.send({"type": "start", "gameId": game})
        control.wait(lambda m: m["type"] == "session.state" and m["gameId"] == game and m["payload"]["phase"] in ("collecting", "simulating"), timeout=30)
        control.stop()
        control = None
        deadline = time.monotonic() + args.timeout
        reported = 0
        while any(p.poll() is None for p in clients):
            require(time.monotonic() < deadline, "match watchdog expired; this is incomplete, not a competitive result")
            require(all(p.poll() in (None, 0) for p in clients), "ClientAI process failed")
            latest = max((r.get("roundId", 0) for i in range(1, 5) for r in tail(output / f"p{i}.jsonl")), default=0)
            if latest >= reported + 25:
                print(json.dumps({"progressRound": latest, "gameId": game}), flush=True)
                reported = latest
            time.sleep(1)
        match_finished = True
        audits = [audit(output / f"p{i}.jsonl") for i in range(1, 5)]
        require(all(p.returncode == 0 for p in clients), "client did not exit normally")
        result = audits[0]["result"]
        require(all(a["result"] == result for a in audits), "clients disagree on match result")
        control = Peer(url + "/ws/ui", output, "ui-final")
        state = control.wait(lambda m: m["type"] == "session.state" and m["gameId"] == game and m["payload"]["phase"] == "ended")
        frame = control.wait(lambda m: m["type"] == "frame" and m["runId"] == game)
        require(frame["winnerId"] == result["winnerId"], "UI winner differs from match result")
        require(frame["ended"] and frame["time"] == result["time"], "UI time or completion differs from match result")
        (output / "final-frame.json").write_text(json.dumps(frame, ensure_ascii=False, indent=2), encoding="utf-8")
        records = output / "server-records" / (game + ".jsonl")
        counts = Counter()
        deaths = []
        ending = []
        for row in read_jsonl(records):
            if row["recordType"] == "event":
                event = row["message"]
                kind = event.get("kind", event.get("type", "unknown"))
                counts[kind] += 1
                if kind in ("death", "Death"):
                    deaths.append(event)
            if row["recordType"] == "outgoing" and row["message"]["type"] == "match.ended":
                ending.append(row["message"]["payload"])
        require(len(ending) == 4 and all(e == result for e in ending), "server and client result mismatch")
        require(not counts["disconnected"] and not counts["Disconnected"], "a disconnect influenced competitive result")
        report.update({"status": "passed", "result": result, "clients": audits, "clientExitCodes": [p.returncode for p in clients],
                       "eventCounts": dict(counts), "deaths": deaths, "serverRecord": str(records), "finalUiPhase": state["payload"]["phase"]})
        print(json.dumps({"status": "passed", "gameId": game, "result": result, "eventCounts": dict(counts)}, ensure_ascii=False), flush=True)
    except Exception as exc:
        report["error"] = str(exc)
        if game and not match_finished:
            try:
                if control is None:
                    control = Peer(url + "/ws/ui", output, "ui-abort")
                control.send({"type": "match.abort", "gameId": game})
                control.wait(lambda m: m["type"] == "session.state" and m["payload"]["phase"] == "aborted", timeout=5)
            except Exception:
                pass
        raise
    finally:
        # 先将失败标记为未完成；只有确有失败时才清理参赛进程，不把清理产生的胜负写成验收结果。
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        if control:
            control.stop()
        for process in clients:
            if process.poll() is None:
                process.terminate()
                process.wait(5)
        if server and server.poll() is None:
            server.terminate()
            server.wait(5)
        for handle in handles:
            handle.close()


if __name__ == "__main__":
    main()
