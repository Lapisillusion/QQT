"""赛后交叉核对公开观察、AI 选择和权威爆炸；不被参赛客户端调用。"""
import argparse
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory")
    args = parser.parse_args()
    root = Path(args.directory)
    report = json.loads((root / "report.json").read_text(encoding="utf-8"))
    records = [json.loads(line) for line in Path(report["serverRecord"]).read_text(encoding="utf-8").splitlines()]
    events = [r["message"] for r in records if r["recordType"] == "event"]
    ownership = {e["bubbleId"]: e for e in events if e["kind"] in ("placed", "initialBubble")}
    review = []
    for death in report["deaths"]:
        player, final_round = death["playerId"], death["roundId"]
        rows = [json.loads(line) for line in (root / f"p{player}.jsonl").read_text(encoding="utf-8").splitlines()]
        sources = []
        for event in events:
            if event["kind"] == "covered" and event["tick"] == death["tick"] and event["position"] == death["position"]:
                bubble = ownership.get(event["bubbleId"], {})
                sources.append({"bubbleId": event["bubbleId"], "owner": bubble.get("playerId"), "position": bubble.get("position"), "placed": bubble.get("tick"), "due": bubble.get("relatedTime")})
        history = []
        for round_id in range(max(1, final_round-2), final_round+1):
            current = [r for r in rows if r.get("roundId") == round_id]
            observation = next(r["message"]["payload"] for r in current if r.get("event") == "received" and r["message"]["type"] == "round.started")
            decision = next((r for r in current if r.get("event") == "decision"), None)
            candidates = sorted([r for r in current if r.get("event") == "candidate"], key=lambda r: r["score"], reverse=True)[:5]
            queries = [r for r in current if r.get("event") == "query"]
            history.append({"round": round_id, "self": observation["self"], "visibleBubbles": observation["visible"]["bubbles"],
                            "visiblePlayers": observation["visible"]["players"], "queries": queries, "decision": decision, "bestCandidates": candidates})
        review.append({"player": player, "deathTime": death["tick"], "deathPosition": death["position"], "actualSources": sources, "lastRounds": history})
    (root / "review.json").write_text(json.dumps(review, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps([{"player": r["player"], "deathTime": r["deathTime"], "actualSources": r["actualSources"],
                       "lastDecision": r["lastRounds"][-1]["decision"]} for r in review], ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
