import { execFile, spawn } from 'node:child_process';
import { existsSync, mkdtempSync, realpathSync, rmSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const candidates = [
  process.env.PULSE_BROWSER_PATH,
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
].filter(Boolean);
const browser = candidates.find(existsSync);
if (!browser) {
  console.error('Browser not found. Set PULSE_BROWSER_PATH to a Chromium or Edge executable.');
  process.exit(2);
}

const pause = ms => new Promise(resolvePromise => setTimeout(resolvePromise, ms));
const withTimeout = (promise, ms, message) => new Promise((resolveTimed, rejectTimed) => {
  const timer = setTimeout(() => rejectTimed(new Error(message)), ms);
  Promise.resolve(promise).then(
    value => { clearTimeout(timer); resolveTimed(value); },
    error => { clearTimeout(timer); rejectTimed(error); },
  );
});
const freePort = () => new Promise((resolvePort, rejectPort) => {
  const server = createServer();
  server.once('error', rejectPort);
  server.listen(0, '127.0.0.1', () => {
    const address = server.address();
    const port = typeof address === 'object' && address ? address.port : null;
    server.close(error => error ? rejectPort(error) : resolvePort(port));
  });
});

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const pageUrl = pathToFileURL(resolve(repositoryRoot, 'astradocs/index.html')).href;
const port = await freePort();
const profile = mkdtempSync(join(tmpdir(), 'pulse-walkthrough-'));
const ownedTempRoot = realpathSync(tmpdir());
const ownedProfile = realpathSync(profile);
if (dirname(ownedProfile).toLowerCase() !== ownedTempRoot.toLowerCase()
    || !basename(ownedProfile).startsWith('pulse-walkthrough-')) {
  throw new Error(`Refusing to use unexpected disposable profile path: ${ownedProfile}`);
}
const child = spawn(browser, [
  '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  '--disable-background-networking', '--disable-component-update', '--disable-default-apps',
  '--disable-extensions', '--disable-sync', '--no-service-autorun',
  `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, pageUrl,
], { windowsHide: true, stdio: 'ignore' });
const childExit = new Promise(resolveExit => child.once('exit', resolveExit));
let socket;
let waiting;
let socketClosed;
let browserSocket;
let browserSocketClosed;
let browserCall;

try {
  let target;
  for (let i = 0; i < 80; i++) {
    try {
      target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(item => item.type === 'page');
    } catch { /* Debugging endpoint is still starting. */ }
    if (target) break;
    await pause(250);
  }
  if (!target) throw new Error('Browser did not open its debugging endpoint within 20 seconds.');

  const browserTarget = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json();
  browserSocket = new WebSocket(browserTarget.webSocketDebuggerUrl);
  browserSocketClosed = new Promise(resolveClosed => browserSocket.addEventListener('close', resolveClosed, { once: true }));
  await withTimeout(new Promise((resolveOpen, rejectOpen) => {
    browserSocket.addEventListener('open', resolveOpen, { once: true });
    browserSocket.addEventListener('error', rejectOpen, { once: true });
  }), 5_000, 'Timed out connecting to the browser-level debugging socket.');
  let browserSequence = 0;
  browserCall = (method, params = {}) => withTimeout(new Promise((resolveInfo, rejectInfo) => {
    const id = ++browserSequence;
    const onMessage = event => {
      const message = JSON.parse(event.data);
      if (message.id !== id) return;
      browserSocket.removeEventListener('message', onMessage);
      message.error ? rejectInfo(new Error(JSON.stringify(message.error))) : resolveInfo(message.result);
    };
    browserSocket.addEventListener('message', onMessage);
    browserSocket.send(JSON.stringify({ id, method, params }));
  }), 5_000, `Browser command timed out: ${method}`);
  const processInfo = await browserCall('SystemInfo.getProcessInfo');
  if (!processInfo.processInfo.some(item => item.type === 'browser'))
    throw new Error('Could not identify the isolated browser processes.');

  socket = new WebSocket(target.webSocketDebuggerUrl);
  socketClosed = new Promise(resolveClosed => socket.addEventListener('close', resolveClosed, { once: true }));
  await withTimeout(new Promise((resolveOpen, rejectOpen) => {
    socket.addEventListener('open', resolveOpen, { once: true });
    socket.addEventListener('error', rejectOpen, { once: true });
  }), 5_000, 'Timed out connecting to the browser debugging socket.');

  let sequence = 0;
  waiting = new Map();
  const rejectPending = reason => {
    for (const pending of waiting.values()) pending.reject(reason);
    waiting.clear();
  };
  socket.addEventListener('close', () => rejectPending(new Error('Browser debugging socket closed.')));
  socket.addEventListener('error', () => rejectPending(new Error('Browser debugging socket failed.')));
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    if (!message.id || !waiting.has(message.id)) return;
    const pending = waiting.get(message.id);
    waiting.delete(message.id);
    message.error ? pending.reject(new Error(JSON.stringify(message.error))) : pending.resolve(message.result);
  });
  const call = (method, params = {}) => withTimeout(new Promise((resolveCall, rejectCall) => {
    const id = ++sequence;
    waiting.set(id, { resolve: resolveCall, reject: rejectCall });
    socket.send(JSON.stringify({ id, method, params }));
  }), 5_000, `Browser command timed out: ${method}`);

  await call('Page.enable');
  await call('Page.navigate', { url: pageUrl });
  let ready = false;
  for (let i = 0; i < 40; i++) {
    const result = await call('Runtime.evaluate', {
      expression: 'document.readyState === "complete" && document.getElementById("position")?.textContent === "Step 1 of 8"',
      returnByValue: true,
    });
    if (result.result.value) { ready = true; break; }
    await pause(100);
  }
  if (!ready) throw new Error('Lesson failed to initialize.');

  const result = await call('Runtime.evaluate', { expression: `(() => {
    const d=id=>document.getElementById(id); const checks=[];
    function check(v,name){if(!v)throw Error(name);checks.push(name);}
    check(d('quiz').hidden,'quiz starts hidden');
    d('toggle-quiz').click(); check(!d('quiz').hidden && d('toggle-quiz').getAttribute('aria-expanded')==='true','quiz opens accessibly');
    document.querySelector('[data-view="code"]').click(); check(!d('quiz').hidden,'perspective preserves quiz visibility');
    document.querySelector('input[name="answer"]').click(); d('check').click();
    check(d('feedback').textContent.length>0,'answer feedback works');
    d('toggle-quiz').click(); check(!document.querySelector('input[name="answer"]:checked') && !d('feedback').textContent,'hiding clears answer and feedback');
    d('glossary').open=true;
    d('toggle-quiz').click();d('next').click();check(d('quiz').hidden,'new step hides quiz');
    d('scenario').value='failure';d('scenario').dispatchEvent(new Event('change'));
    for(let i=0;i<6;i++)d('next').click();
    check(d('counts').lastElementChild.textContent==='1','failure scenario reaches dead letter');
    d('restart').click();
    check(d('scenario').value==='success' && d('position').textContent==='Step 1 of 8','restart resets scenario and step');
    check(document.querySelector('[data-view="story"]').getAttribute('aria-pressed')==='true' && d('quiz').hidden,'restart resets perspective and quiz');
    check(document.activeElement===d('step-title'),'restart focuses lesson heading');
    check(d('glossary').open,'glossary survives restart');
    check(document.querySelectorAll('#glossary dt').length===3,'three glossary terms available');
    return checks;
  })()`, returnByValue: true });
  if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
  console.log(JSON.stringify({ result: 'PASS', checks: result.result.value }));
} finally {
  // Browser.close normally closes the socket before sending a response. Send it
  // without awaiting that response, then bound every teardown wait.
  if (browserSocket?.readyState === WebSocket.OPEN) {
    try { browserSocket.send(JSON.stringify({ id: 999999, method: 'Browser.close', params: {} })); } catch { }
  }
  await Promise.race([browserSocketClosed ?? Promise.resolve(), pause(8_000)]);
  // Escalate only if this browser's control socket is still live. Refresh the
  // PID immediately so a historical child PID cannot be killed after reuse.
  if (browserSocket?.readyState === WebSocket.OPEN && browserCall) {
    const fresh = await browserCall('SystemInfo.getProcessInfo').catch(() => null);
    const rootPid = fresh?.processInfo?.find(item => item.type === 'browser')?.id;
    if (rootPid && process.platform === 'win32') {
      await new Promise(resolveKill => execFile('taskkill.exe', ['/PID', String(rootPid), '/T', '/F'], () => resolveKill()));
    } else if (rootPid) {
      try { process.kill(rootPid, 'SIGKILL'); } catch (error) { if (error?.code !== 'ESRCH') throw error; }
    }
  }
  await pause(1_000);
  await Promise.race([socketClosed ?? Promise.resolve(), pause(1_000)]);
  await Promise.race([childExit, pause(1_000)]);
  if (child.exitCode === null) child.kill();
  await Promise.race([childExit, pause(1_000)]);
  socket?.close();
  browserSocket?.close();
  if (waiting) {
    for (const pending of waiting.values()) pending.reject(new Error('Walkthrough verification finished.'));
    waiting.clear();
  }
  let removed = false;
  for (let attempt = 0; attempt < 80 && !removed; attempt++) {
    try {
      // ownedProfile was resolved and constrained to our mkdtemp prefix above.
      rmSync(ownedProfile, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 });
      removed = true;
    } catch (error) {
      if (error?.code !== 'EPERM' && error?.code !== 'EBUSY') throw error;
      await pause(250);
    }
  }
  if (!removed) throw new Error(`Browser closed, but its disposable profile could not be removed: ${profile}`);
}
