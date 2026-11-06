import { cp, mkdir, readFile, realpath, rename, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { readAsar, rewriteAsar, sha256 } from './asar.mjs';
import { patchSources, inspectSourceCompatibility } from './patch-plan.mjs';
import { modelMenuEntrySource } from './model-menu-entry.mjs';
import { patchNativeDiagnostics, restoreNativeDiagnostics, inspectNativeDiagnostics } from './native-diagnostics-patch.mjs';
import { patchNativeProjectRefresh, restoreNativeProjectRefresh } from './native-project-refresh-patch.mjs';
import { extractNativeFrontend, inspectFrontendBindings } from './frontend-bindings.mjs';

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const addonFiles = ['runtime.cjs', 'component-client.cjs', 'switch-coordinator.cjs', 'token-barrier.cjs', 'submission-gate.cjs', 'native-task-adapter.cjs', 'native-agent-state.cjs', 'model-menu-bridge.cjs', 'native-view.cjs', 'update-handoff.cjs'];
const uiFiles = ['window.cjs'];

function inside(child, parent) { const relative = path.relative(parent, child); return relative === '' || relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative); }
async function absent(filename) { try { await stat(filename); return false; } catch (error) { if (error.code === 'ENOENT') return true; throw error; } }
async function futureRealpath(filename) {
  const suffix = [];
  let existing = path.resolve(filename);
  while (await absent(existing)) { suffix.unshift(path.basename(existing)); existing = path.dirname(existing); }
  return path.join(await realpath(existing), ...suffix);
}

async function checkedAccountsRoot(accountsStateRoot, sourceInstall) {
  if (accountsStateRoot === undefined) return undefined;
  if (typeof accountsStateRoot !== 'string' || !path.isAbsolute(accountsStateRoot) || /[\0\r\n]/.test(accountsStateRoot))
    throw new Error('The account state directory must be an absolute existing directory outside the installation');
  const root = path.resolve(accountsStateRoot);
  let physical;
  try {
    if (!(await stat(root)).isDirectory()) throw new Error();
    physical = await realpath(root);
  } catch { throw new Error('The account state directory must be an absolute existing directory outside the installation'); }
  if (inside(root, sourceInstall) || inside(physical, sourceInstall))
    throw new Error('The account state directory must be outside the installation');
  return root;
}

export async function buildCopy({ sourceAsar, outputRoot, componentExecutable, uiDirectory, languageServerPath, nativeOAuthExtractor, accountsStateRoot, allowMissingPayload = false }) {
  sourceAsar = await realpath(sourceAsar);
  outputRoot = await futureRealpath(outputRoot);
  const sourceInstall = path.resolve(path.dirname(sourceAsar), '..');
  accountsStateRoot = await checkedAccountsRoot(accountsStateRoot, sourceInstall);
  // The builder has no mode which writes the installed archive. Installation is
  // an explicit, separate action against an already reviewed prepared result.
  if (inside(outputRoot, sourceInstall) || inside(sourceAsar, outputRoot)) throw new Error('The output must be separate from the source installation');
  if (!await absent(outputRoot)) throw new Error('Output already exists; choose a new directory');
  const original = await readFile(sourceAsar);
  const compatibility = inspectSourceCompatibility(readAsar(original));
  const replacements = patchSources(readAsar(original));
  replacements.set('dist/hostauth-addon/model-menu-preload.cjs', Buffer.from(modelMenuEntrySource()));
  for (const name of addonFiles) replacements.set('dist/hostauth-addon/' + name, await readFile(path.join(packageRoot, 'src', name)));
  if (uiDirectory) {
    for (const name of uiFiles) replacements.set('dist/hostauth-addon/account-plugin/' + name, await readFile(path.join(uiDirectory, name)));
  } else if (!allowMissingPayload) throw new Error('An account plugin UI directory is required');
  if (!componentExecutable && !allowMissingPayload) throw new Error('A published independent account component is required');
  const patched = rewriteAsar(original, replacements);
  const componentName = componentExecutable ? path.basename(componentExecutable) : 'ProfileHub.Accounts.exe';
  const config = { schemaVersion: 1, component: 'component/' + componentName };
  if (accountsStateRoot !== undefined) config.accountsStateRoot = accountsStateRoot;
  let originalNative, patchedNative, nativeDiagnostics, providerNative, nativeProjectRefresh;
  if (languageServerPath) {
    const extractor = nativeOAuthExtractor || (await import(pathToFileURL(path.resolve(packageRoot, '../account-plugin/native-oauth.mjs')))).extractNativeOAuth;
    config.oauthClient = await extractor(languageServerPath);
    originalNative = await readFile(languageServerPath);
    const result = patchNativeDiagnostics(originalNative);
    patchedNative = result.buffer;
    nativeDiagnostics = result.report;
    const providerResult = patchNativeProjectRefresh(patchedNative);
    providerNative = providerResult.buffer;
    nativeProjectRefresh = providerResult.report;
    if (nativeDiagnostics.version !== compatibility.sourceContractVersion || nativeProjectRefresh.version !== compatibility.sourceContractVersion)
      throw new Error('Native program and application source contracts differ');
    config.nativeProgram = { version: nativeDiagnostics.version, originalSha256: sha256(originalNative),
      patchedSha256: sha256(patchedNative), providerSha256: sha256(providerNative) };
    config.frontendBindings = inspectFrontendBindings(extractNativeFrontend(originalNative));
  } else if (!allowMissingPayload) throw new Error('The installed language-server program is required to configure the matching desktop OAuth client');
  const manifest = {
    schemaVersion: 3, adapterVersion: '0.4.0-preview.8', antigravityVersion: compatibility.antigravityVersion,
    originalAsarSha256: sha256(original), patchedAsarSha256: sha256(patched),
    originalSourceSha256: compatibility.sourceSha256, compatibility,
    originalNativeSha256: originalNative ? sha256(originalNative) : undefined,
    patchedNativeSha256: patchedNative ? sha256(patchedNative) : undefined,
    nativeDiagnostics,
    providerNativeSha256: providerNative ? sha256(providerNative) : undefined,
    frontendBindings: config.frontendBindings,
    accountsStateRoot,
    nativeProjectRefresh,
    changedFiles: [...replacements].map(([name, bytes]) => ({ name, sha256: sha256(bytes) })),
    payloadComplete: Boolean(componentExecutable && uiDirectory && languageServerPath),
    runtimeVerified: false,
    limitation: 'Unconfigured installations retain native authentication. Native-provider activation and actual cross-account quota use require separately authorized OAuth and native runtime validation.',
  };
  await mkdir(path.join(outputRoot, 'resources', 'hostauth-addon'), { recursive: true });
  await mkdir(path.join(outputRoot, 'restore'));
  try {
    await writeFile(path.join(outputRoot, 'resources', 'app.asar'), patched, { flag: 'wx' });
    await writeFile(path.join(outputRoot, 'restore', 'app.asar'), original, { flag: 'wx' });
    if (patchedNative) {
      await mkdir(path.join(outputRoot, 'resources', 'bin'));
      await mkdir(path.join(outputRoot, 'restore', 'bin'));
      await writeFile(path.join(outputRoot, 'resources', 'bin', 'language_server.exe'), patchedNative, { flag: 'wx' });
      await writeFile(path.join(outputRoot, 'restore', 'bin', 'language_server.exe'), originalNative, { flag: 'wx' });
      await mkdir(path.join(outputRoot, 'resources', 'hostauth-addon', 'native'));
      await writeFile(path.join(outputRoot, 'resources', 'hostauth-addon', 'native', 'language_server.exe'), providerNative, { flag: 'wx' });
    }
    await writeFile(path.join(outputRoot, 'resources', 'hostauth-addon', 'config.json'), JSON.stringify(config, null, 2) + '\n', { flag: 'wx' });
    if (componentExecutable) {
      const component = await realpath(componentExecutable);
      if (!(await stat(component)).isFile()) throw new Error('The component executable must be a file');
      // Releases are self-contained single-file executables. Copy that exact
      // artifact so adjacent debug files or developer data cannot be installed.
      const componentRoot = path.join(outputRoot, 'resources', 'hostauth-addon', 'component');
      await mkdir(componentRoot);
      await cp(component, path.join(componentRoot, path.basename(component)), { errorOnExist: true, force: false });
    }
    await writeFile(path.join(outputRoot, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n', { flag: 'wx' });
  } catch (error) {
    // Output was required to be absent and is owned by this failed build.
    await rm(outputRoot, { recursive: true, force: true });
    throw error;
  }
  return manifest;
}

async function validatePrepared(preparedRoot) {
  const manifest = JSON.parse(await readFile(path.join(preparedRoot, 'manifest.json'), 'utf8'));
  if (manifest.schemaVersion !== 3) throw new Error('Unsupported prepared adapter manifest');
  const original = await readFile(path.join(preparedRoot, 'restore', 'app.asar'));
  const patched = await readFile(path.join(preparedRoot, 'resources', 'app.asar'));
  if (sha256(original) !== manifest.originalAsarSha256 || sha256(patched) !== manifest.patchedAsarSha256) throw new Error('Prepared archive contents differ from the manifest');
  const compatibility = inspectSourceCompatibility(readAsar(original));
  if (compatibility.antigravityVersion !== manifest.antigravityVersion) throw new Error('Prepared version differs from its original program');
  patchSources(readAsar(original));
  const originalNative = await readFile(path.join(preparedRoot, 'restore', 'bin', 'language_server.exe'));
  const patchedNative = await readFile(path.join(preparedRoot, 'resources', 'bin', 'language_server.exe'));
  const native = inspectNativeDiagnostics(originalNative);
  if (native.version !== compatibility.sourceContractVersion ||
      manifest.originalNativeSha256 !== native.originalSha256 || manifest.patchedNativeSha256 !== native.patchedSha256 ||
      sha256(originalNative) !== manifest.originalNativeSha256 || sha256(patchedNative) !== manifest.patchedNativeSha256 ||
      !restoreNativeDiagnostics(patchedNative).buffer.equals(originalNative)) throw new Error('Prepared native program differs from the reviewed patch');
  const providerNative = await readFile(path.join(preparedRoot, 'resources', 'hostauth-addon', 'native', 'language_server.exe'));
  if (sha256(providerNative) !== manifest.providerNativeSha256 ||
      !restoreNativeProjectRefresh(providerNative).buffer.equals(patchedNative)) throw new Error('Prepared provider program differs from the reviewed project refresh patch');
  const config = JSON.parse(await readFile(path.join(preparedRoot, 'resources', 'hostauth-addon', 'config.json'), 'utf8'));
  if (config.accountsStateRoot !== manifest.accountsStateRoot) throw new Error('Prepared account state directory differs from its manifest');
  const expectedBindings = inspectFrontendBindings(extractNativeFrontend(originalNative));
  if (JSON.stringify(config.frontendBindings) !== JSON.stringify(expectedBindings) ||
      JSON.stringify(config.nativeProgram) !== JSON.stringify({ version: native.version, originalSha256: native.originalSha256,
        patchedSha256: native.patchedSha256, providerSha256: sha256(providerNative) }))
    throw new Error('Prepared frontend or native bindings differ from the original program');
  return { manifest, original, patched, originalNative, patchedNative, config };
}

async function replaceFile(filename, bytes) {
  const staged = filename + '.hostauth-new';
  if (!await absent(staged)) throw new Error('An unfinished adapter replacement exists');
  await writeFile(staged, bytes, { flag: 'wx' });
  try { await rename(staged, filename); }
  catch (error) { await rm(staged, { force: true }); throw error; }
}

// These operations are also usable against a synthetic resources directory in
// offline tests. Callers must stop all Antigravity processes before using them
// on an installation. No App, LS or account operation is performed here.
export async function applyPrepared({ preparedRoot, resourcesRoot, backupRoot, processesStopped, acknowledgeUnverifiedRuntime }) {
  if (processesStopped !== true || acknowledgeUnverifiedRuntime !== true) throw new Error('Application shutdown and unverified runtime acknowledgement are required');
  preparedRoot = await realpath(preparedRoot);
  resourcesRoot = await realpath(resourcesRoot);
  backupRoot = await futureRealpath(backupRoot);
  if (inside(backupRoot, resourcesRoot) || inside(resourcesRoot, backupRoot) || inside(preparedRoot, resourcesRoot) || inside(resourcesRoot, preparedRoot)) throw new Error('Prepared files, resources and recovery directory must be separate');
  if (!await absent(backupRoot) || !await absent(path.join(resourcesRoot, 'hostauth-addon'))) throw new Error('An adapter or recovery directory already exists');
  const { manifest, original, patched, originalNative, patchedNative, config } = await validatePrepared(preparedRoot);
  await checkedAccountsRoot(config.accountsStateRoot, path.resolve(resourcesRoot, '..'));
  if (!manifest.payloadComplete) throw new Error('Prepared account component or UI payload is incomplete');
  const destination = path.join(resourcesRoot, 'app.asar');
  const nativeDestination = path.join(resourcesRoot, 'bin', 'language_server.exe');
  if (sha256(await readFile(destination)) !== manifest.originalAsarSha256) throw new Error('The installed archive changed; rebuild against the current program');
  if (sha256(await readFile(nativeDestination)) !== manifest.originalNativeSha256) throw new Error('The installed native program changed; rebuild against the current program');
  await mkdir(backupRoot, { recursive: false });
  await writeFile(path.join(backupRoot, 'app.asar'), original, { flag: 'wx' });
  await writeFile(path.join(backupRoot, 'language_server.exe'), originalNative, { flag: 'wx' });
  await writeFile(path.join(backupRoot, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n', { flag: 'wx' });
  let nativeReplaced = false;
  let archiveReplaced = false;
  try {
    await cp(path.join(preparedRoot, 'resources', 'hostauth-addon'), path.join(resourcesRoot, 'hostauth-addon'), { recursive: true, force: false, errorOnExist: true });
    await replaceFile(nativeDestination, patchedNative);
    nativeReplaced = true;
    await replaceFile(destination, patched);
    archiveReplaced = true;
  } catch (error) {
    // Roll back files already replaced. If rollback itself fails, retain the
    // complete addon and recovery directory for Restore instead of deleting a
    // dependency which the remaining patched archive may still require.
    try {
      if (archiveReplaced) await replaceFile(destination, original);
      if (nativeReplaced) await replaceFile(nativeDestination, originalNative);
      await rm(path.join(resourcesRoot, 'hostauth-addon'), { recursive: true, force: true });
    } catch { throw new Error('Installation failed and recovery is incomplete; close the App and use Restore with the retained recovery directory'); }
    throw error;
  }
  return manifest;
}

export async function restorePrepared({ resourcesRoot, backupRoot, processesStopped }) {
  if (processesStopped !== true) throw new Error('Application shutdown is required');
  resourcesRoot = await realpath(resourcesRoot);
  backupRoot = await realpath(backupRoot);
  if (inside(backupRoot, resourcesRoot) || inside(resourcesRoot, backupRoot)) throw new Error('Recovery directory must be separate from resources');
  const manifest = JSON.parse(await readFile(path.join(backupRoot, 'manifest.json'), 'utf8'));
  const original = await readFile(path.join(backupRoot, 'app.asar'));
  if (![1, 2, 3].includes(manifest.schemaVersion) || sha256(original) !== manifest.originalAsarSha256) throw new Error('Original recovery archive is invalid');
  const destination = path.join(resourcesRoot, 'app.asar');
  const current = sha256(await readFile(destination));
  if (current !== manifest.patchedAsarSha256 && current !== manifest.originalAsarSha256) throw new Error('The program was updated or modified; refusing to overwrite it with an older archive');
  let nativeDestination, originalNative, currentNative;
  if (manifest.schemaVersion >= 2) {
    nativeDestination = path.join(resourcesRoot, 'bin', 'language_server.exe');
    originalNative = await readFile(path.join(backupRoot, 'language_server.exe'));
    const native = inspectNativeDiagnostics(originalNative);
    if (native.state !== 'original' || manifest.originalNativeSha256 !== native.originalSha256 || manifest.patchedNativeSha256 !== native.patchedSha256) throw new Error('Original native recovery program is invalid');
    currentNative = sha256(await readFile(nativeDestination));
    if (![native.originalSha256, native.patchedSha256].includes(currentNative)) throw new Error('The native program was updated or modified; refusing to overwrite it with an older program');
  }
  if (originalNative && currentNative !== manifest.originalNativeSha256) await replaceFile(nativeDestination, originalNative);
  if (current !== manifest.originalAsarSha256) await replaceFile(destination, original);
  await rm(path.join(resourcesRoot, 'hostauth-addon'), { recursive: true, force: true });
  return { restored: true, originalAsarSha256: manifest.originalAsarSha256 };
}
