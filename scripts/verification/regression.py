"""仅回归方案列出的既有 M0、精确时间和道具脚本，不新增验证范围。"""
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
output = (root / (sys.argv[1] if len(sys.argv) > 1 else "artifacts/battle-regression")).resolve()
output.mkdir(parents=True, exist_ok=True)
scripts = sorted((root / "scripts").glob("m0*.json")) + sorted((root / "scripts").glob("precise-*.json")) + sorted((root / "scripts/items").glob("items-*.json"))
results = []
for script in scripts:
    with open(output / (script.stem + ".log"), "w", encoding="utf-8") as log:
        result = subprocess.run(["dotnet", str(root / "src/QQT.Server/bin/Debug/net10.0/QQT.Server.dll"), "--verify", "--script", str(script)], cwd=root, stdout=log, stderr=subprocess.STDOUT)
    results.append({"script": str(script.relative_to(root)), "passed": result.returncode == 0})
    if result.returncode:
        print("FAIL", script.name, flush=True)
        break
report = {"status": "passed" if all(r["passed"] for r in results) and len(results) == len(scripts) else "failed", "scripts": results}
(output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(f"{report['status'].upper()} existing-script-regression {len(results)}/{len(scripts)}", flush=True)
raise SystemExit(0 if report["status"] == "passed" else 1)
