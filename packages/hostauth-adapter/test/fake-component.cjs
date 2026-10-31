'use strict';
const readline = require('node:readline');
const input = readline.createInterface({ input: process.stdin });
input.on('line', (line) => {
  const request = JSON.parse(line);
  const scenario = request.params?.scenario;
  if (scenario === 'malformed') { process.stdout.write('not-json\n'); return; }
  if (scenario === 'huge') { process.stdout.write('x'.repeat(140000)); return; }
  if (scenario === 'exit') { process.exit(2); }
  if (scenario === 'hang') return;
  if (scenario === 'wrong-id') { process.stdout.write(JSON.stringify({ id: request.id + 100, result: {} }) + '\n'); return; }
  if (scenario === 'error') {
    process.stderr.write('DO_NOT_LOG_FAKE_TOKEN');
    process.stdout.write(JSON.stringify({ id: request.id, error: { code: 'oauth_failed', message: 'DO_NOT_LOG_FAKE_TOKEN' } }) + '\n');
    return;
  }
  const result = request.method === 'getToken'
    ? { accessToken: 'independent-fake-access', tokenType: 'Bearer', refreshToken: 'independent-fake-refresh', expiry: new Date(Date.now() + 3600000).toISOString(), isGcpTos: false }
    : { configured: true, method: request.method };
  process.stdout.write(JSON.stringify({ id: request.id, result }) + '\n');
  if (request.method === 'shutdown') { input.close(); process.stdin.destroy(); }
});
