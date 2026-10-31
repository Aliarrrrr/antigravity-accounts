import { buildCopy } from '../src/package-copy.mjs';

if (process.versions.electron) process.noAsar = true;

const values = new Map();
for (let index = 2; index < process.argv.length; index += 2) {
  const key = process.argv[index];
  if (!['--source-asar', '--output', '--component', '--ui', '--language-server', '--allow-missing-payload'].includes(key) || !process.argv[index + 1]) throw new Error('Expected --source-asar PATH --output NEW_DIRECTORY [--component EXE --ui DIRECTORY --language-server EXE]');
  values.set(key, process.argv[index + 1]);
}
try {
  const manifest = await buildCopy({ sourceAsar: values.get('--source-asar'), outputRoot: values.get('--output'), componentExecutable: values.get('--component'), uiDirectory: values.get('--ui'), languageServerPath: values.get('--language-server'), allowMissingPayload: values.get('--allow-missing-payload') === 'true' });
  console.log(JSON.stringify({ output: values.get('--output'), antigravityVersion: manifest.antigravityVersion, payloadComplete: manifest.payloadComplete, runtimeVerified: false }, null, 2));
} catch (error) { console.error(error.message); process.exitCode = 1; }
