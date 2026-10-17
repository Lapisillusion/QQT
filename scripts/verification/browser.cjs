// 真实 Chrome 的隔离会话；所有开始和逐帧许可均点击页面按钮，C# 客户端继续执行固定脚本。
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { createInterface } = require('node:readline');
let playwright;
try { playwright = require('playwright'); }
catch {
  const bundled = path.join(process.env.USERPROFILE, '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
  playwright = require(bundled);
}
const root = path.resolve(__dirname, '../..');
const output = path.resolve(root, process.argv[2] || 'artifacts/battle-browser');
const executable = process.env.QQT_CHROME || 'D:/Program Files/Google/Chrome/Application/chrome.exe';
const url = 'http://127.0.0.1:5080';
const assert = (value, reason) => { if (!value) throw new Error(reason); };
fs.mkdirSync(output, { recursive: true });

(async () => {
  let browser, runner;
  const serverLog = fs.openSync(path.join(output, 'server.log'), 'w');
  const server = spawn('dotnet', ['src/QQT.Server/bin/Debug/net10.0/QQT.Server.dll', '--battle'], {
    cwd: root, windowsHide: true, stdio: ['ignore', serverLog, serverLog],
    env: { ...process.env, Urls: url, Battle__RecordDirectory: path.join(output, 'server-jsonl') }
  });
  const report = { status: 'running', browser: executable, checks: [], errors: [] };
  try {
    for (let attempt = 0; ; attempt++) {
      try { if ((await fetch(url)).ok) break; } catch { }
      assert(attempt < 100, 'server startup timeout');
      await new Promise(resolve => setTimeout(resolve, 50));
    }
    browser = await playwright.chromium.launch({ executablePath: executable, headless: true });
    const page = await browser.newPage({ viewport: { width: 1440, height: 1080 }, deviceScaleFactor: 1 });
    page.on('pageerror', error => report.errors.push(String(error)));
    await page.goto(url);
    await page.locator('#battle-controls').waitFor({ state: 'visible' });
    runner = spawn('python', ['scripts/verification/battle.py', '--only', 'browser', '--external-server', '--output', output], {
      cwd: root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
    });
    const runnerLog = fs.createWriteStream(path.join(output, 'runner.log'));
    runner.stderr.pipe(runnerLog);
    let mode = 'auto';
    for await (const line of createInterface({ input: runner.stdout })) {
      runnerLog.write(line + '\n'); process.stdout.write(line + '\n');
      const [command, value] = line.split(' ');
      if (command === 'BROWSER_CREATE' || command === 'BROWSER_DISPLAY_CREATE') {
        mode = value;
        await page.locator('#create-match').waitFor({ state: 'visible' });
        await page.waitForFunction(() => !document.querySelector('#create-match').disabled);
        await page.locator('#player-count').selectOption(command === 'BROWSER_CREATE' ? '4' : value);
        await page.locator('#manual-tick').setChecked(value !== 'auto');
        await page.locator('#spawn-seed').fill('42');
        await page.locator('#create-match').click();
      } else if (command === 'BROWSER_START') {
        await page.waitForFunction(() => !document.querySelector('#start').disabled);
        assert(await page.locator('#players .player').count() === 4, 'four player cards');
        assert(await page.locator('#demonstrate').isVisible() === (value === 'manual'), 'manual button visibility');
        await page.screenshot({ path: path.join(output, `${value}-ready.png`), fullPage: true });
        await page.locator('#start').click();
      } else if (command === 'BROWSER_TICK') {
        await page.waitForFunction(() => !document.querySelector('#demonstrate').disabled);
        assert((await page.locator('#status').innerText()).includes('等待 tick演示'), 'manual waiting status');
        if (value === '1') {
          assert(await page.locator('#tick').innerText() === '0', 'first tick gate');
          await page.screenshot({ path: path.join(output, 'manual-first-gate.png'), fullPage: true });
        }
        await page.locator('#demonstrate').click();
      } else if (command === 'BROWSER_END') {
        await page.waitForFunction(() => document.querySelector('#tick').textContent === '9');
        assert((await page.locator('#result').innerText()).includes('玩家 2 获胜（t=9）'), 'UI final winner/time');
        assert(await page.locator('#crates').innerText() === '325' && await page.locator('#bubbles').innerText() === '0' && await page.locator('#safe').innerText() === '1', 'UI final counters');
        assert(await page.locator('#players .is-dead').count() === 3, 'UI final deaths');
        const positions = await page.locator('.player-location').allTextContents();
        assert(JSON.stringify(positions) === JSON.stringify(['B1','A20','T21','T1']), 'UI animation endpoints');
        assert(await page.locator('#demonstrate').isDisabled(), 'ended tick button disabled');
        await page.screenshot({ path: path.join(output, `${value}-final.png`), fullPage: true });
        report.checks.push({ mode: value, tick: 9, winnerId: 2, crates: 325, positions, screenshot: `${value}-final.png` });
      } else if (command === 'BROWSER_DISPLAY_START') {
        await page.waitForFunction(() => !document.querySelector('#start').disabled);
        assert(await page.locator('#players .player').count() === Number(value), 'actual player count UI');
        assert((await page.locator('#seats').innerText()) === `${value}/${value} 已登记`, 'actual registered count UI');
        await page.screenshot({ path: path.join(output, `players-${value}.png`), fullPage: true });
        await page.locator('#start').click();
        report.checks.push({ playerCount: Number(value), screenshot: `players-${value}.png` });
      }
    }
    const code = await new Promise(resolve => runner.exitCode !== null ? resolve(runner.exitCode) : runner.once('exit', resolve));
    assert(code === 0, 'browser script clients failed');
    assert(report.checks.length === 4 && report.errors.length === 0, 'browser checks incomplete or JavaScript error');
    report.status = 'passed';
  } catch (error) {
    report.status = 'failed'; report.firstDifference = String(error); console.error(error); process.exitCode = 1;
    // 先固定验收失败并通过 UI 通道中止，再清理参赛连接。
    fs.writeFileSync(path.join(output, 'browser-report.json'), JSON.stringify(report, null, 2));
    try {
      await new Promise((resolve, reject) => {
        const control = new WebSocket(url.replace('http:', 'ws:') + '/ws/ui');
        const timeout = setTimeout(() => { control.close(); reject(new Error('abort acknowledgement timeout')); }, 3000);
        control.onmessage = event => {
          const message = JSON.parse(String(event.data));
          if (message.type !== 'session.state') return;
          if (['idle','ended','aborted'].includes(message.payload.phase)) { clearTimeout(timeout); control.close(); resolve(); }
          else control.send(JSON.stringify({type:'match.abort',gameId:message.gameId}));
        };
        control.onerror = () => { clearTimeout(timeout); reject(new Error('abort control connection failed')); };
      });
    } catch (cleanupError) { report.cleanupError = String(cleanupError); server.kill(); }

  } finally {
    fs.writeFileSync(path.join(output, 'browser-report.json'), JSON.stringify(report, null, 2));
    if (browser) await browser.close();
    if (runner && runner.exitCode === null) runner.kill();
    server.kill(); fs.closeSync(serverLog);
  }
})();
