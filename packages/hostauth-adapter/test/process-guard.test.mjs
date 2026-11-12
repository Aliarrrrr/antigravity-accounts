import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import os from 'node:os';
import { mkdir, mkdtemp, realpath, rm, symlink, writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { assertApplicationStopped, verifiedNodeSidecarIds } from '../src/process-guard.mjs';

test('external sidecar exemption requires the exact launcher, parent and import', () => {
  const base = path.resolve('sidecar-test');
  const options = { installRoot: path.join(base, 'app'), roamingRoot: path.join(base, 'roaming'), userRoot: path.join(base, 'user'), systemRoot: path.join(base, 'Windows') };
  const executable = path.join(options.installRoot, 'Antigravity.exe');
  const cmd = path.join(options.systemRoot, 'System32', 'cmd.exe');
  const shim = path.join(options.roamingRoot, 'Antigravity', 'bin', 'agy-node.cmd');
  const args = '--import=' + pathToFileURL(path.join(options.userRoot, '.gemini/antigravity/lib/node_modules/sidecar_sdk/register_loader.js')).href + ' dist/server.cjs';
  const shimText = '@echo off\r\nset ELECTRON_RUN_AS_NODE=1\r\n"' + executable + '" %*\r\n';
  const parent = { ProcessId: 201, ExecutablePath: cmd, CommandLine: '"' + cmd + '" /c "' + shim + '" ' + args };
  const child = { ProcessId: 101, ParentProcessId: 201, ExecutablePath: executable, CommandLine: '"' + executable + '" ' + args };
  const check = (processes, wrapper = shimText) => verifiedNodeSidecarIds({ ...options, processes, shimText: wrapper });
  assert.deepEqual(check([parent, child]), [101]);
  const wrappedParent = { ...parent, CommandLine: cmd + ' /c ""' + shim + '" ' + args + '"' };
  assert.deepEqual(check([wrappedParent, child]), [101]);
  for (const command of [wrappedParent.CommandLine + ' & echo extra', wrappedParent.CommandLine.slice(0, -1),
    cmd + ' /c """' + shim + '" ' + args + '""', wrappedParent.CommandLine.replace('server.cjs', 'other.cjs')])
    assert.deepEqual(check([{ ...wrappedParent, CommandLine: command }, child]), []);
  assert.deepEqual(check([child]), []);
  assert.deepEqual(check([parent, { ...child, CommandLine: child.CommandLine + ' --type=renderer' }]), []);
  assert.deepEqual(check([{ ...parent, CommandLine: parent.CommandLine.replace('agy-node.cmd', 'other.cmd') }, child]), []);
  assert.deepEqual(check([{ ...parent, ExecutablePath: path.join(base, 'cmd.exe') }, child]), []);
  assert.deepEqual(check([parent, { ...child, CommandLine: child.CommandLine.replace('server.cjs', 'other.cjs') }]), []);
  assert.deepEqual(check([parent, child], shimText.replace('set ELECTRON_RUN_AS_NODE=1\r\n', '')), []);
  assert.deepEqual(check([parent, child], shimText + 'echo changed\r\n'), []);
  assert.deepEqual(assertApplicationStopped({ tasklistText: '"Antigravity.exe","101","Console","1","1 K"', sidecarIds: [101] }), { processesStopped: true });
  assert.throws(() => assertApplicationStopped({ tasklistText: '"Antigravity.exe","101","Console","1","1 K"\n"Antigravity.exe","102","Console","1","1 K"', sidecarIds: [101] }), /Exit all/);
});

test('launcher allows Windows executable path casing but keeps the wrapper commands exact', () => {
  const base = path.resolve('Sidecar-Case-Test');
  const options = { installRoot: path.join(base, 'App'), roamingRoot: path.join(base, 'Roaming'), userRoot: path.join(base, 'User'), systemRoot: path.join(base, 'Windows') };
  const executable = path.join(options.installRoot, 'Antigravity.exe');
  const cmd = path.join(options.systemRoot, 'System32', 'cmd.exe');
  const shim = path.join(options.roamingRoot, 'Antigravity', 'bin', 'agy-node.cmd');
  const args = '--import=' + pathToFileURL(path.join(options.userRoot, '.gemini/antigravity/lib/node_modules/sidecar_sdk/register_loader.js')).href + ' dist/server.cjs';
  const parent = { ProcessId: 201, ExecutablePath: cmd, CommandLine: '"' + cmd + '" /c "' + shim + '" ' + args };
  const child = { ProcessId: 101, ParentProcessId: 201, ExecutablePath: executable, CommandLine: '"' + executable + '" ' + args };
  const wrapper = third => '@echo off\r\nset ELECTRON_RUN_AS_NODE=1\r\n' + third + '\r\n';
  const check = shimText => verifiedNodeSidecarIds({ ...options, processes: [parent, child], shimText });
  for (const value of [executable, executable.toLowerCase(), executable.toUpperCase()])
    assert.deepEqual(check(wrapper('"' + value + '" %*')), [101]);
  for (const third of [
    '"' + path.join(options.installRoot, 'Other.exe') + '" %*',
    '"' + executable.toLowerCase() + '" %* & echo extra',
    'echo extra & "' + executable.toLowerCase() + '" %*',
    '"' + executable.toLowerCase() + '" %*\r\necho extra',
    '"' + executable.toLowerCase() + '" %1',
    '"' + executable.toLowerCase() + '"  %*',
    '"' + executable.toLowerCase() + '" "%*"',
  ]) assert.deepEqual(check(wrapper(third)), []);
  assert.deepEqual(check(wrapper('"' + executable + '" %*').replace('@echo off', '@ECHO OFF')), []);
  assert.deepEqual(check(wrapper('"' + executable + '" %*').replace('set ELECTRON_RUN_AS_NODE=1', 'SET ELECTRON_RUN_AS_NODE=1')), []);
});

test('SwarmBoard main entry is exempt only in its exact external directory with an ordinary entry file', async t => {
  const temporary = await realpath(os.tmpdir());
  const base = await mkdtemp(path.join(temporary, 'accounts-sidecar-test-'));
  t.after(async () => {
    const actual = await realpath(base);
    assert.equal(path.dirname(actual), temporary);
    assert.match(path.basename(actual), /^accounts-sidecar-test-/);
    await rm(actual, { recursive: true });
  });
  const options = { installRoot: path.join(base, 'app'), roamingRoot: path.join(base, 'roaming'), userRoot: path.join(base, 'user'), systemRoot: path.join(base, 'Windows') };
  const executable = path.join(options.installRoot, 'Antigravity.exe');
  const cmd = path.join(options.systemRoot, 'System32', 'cmd.exe');
  const shim = path.join(options.roamingRoot, 'Antigravity', 'bin', 'agy-node.cmd');
  const directory = path.join(options.userRoot, '.gemini/antigravity/builtin/plugins/swarm/sidecars/swarm_board');
  const entryFile = path.join(directory, 'main.mjs');
  const prefix = '--import=' + pathToFileURL(path.join(options.userRoot, '.gemini/antigravity/lib/node_modules/sidecar_sdk/register_loader.js')).href + ' ';
  const shimText = '@echo off\r\nset ELECTRON_RUN_AS_NODE=1\r\n"' + executable + '" %*\r\n';
  const parent = { ProcessId: 201, ExecutablePath: cmd, CommandLine: cmd + ' /c ""' + shim + '" ' + prefix + 'main.mjs"' };
  const child = { ProcessId: 101, ParentProcessId: 201, ExecutablePath: executable, CommandLine: '"' + executable + '" ' + prefix + 'main.mjs', CurrentDirectory: directory };
  const check = (processes = [parent, child], wrapper = shimText) => verifiedNodeSidecarIds({ ...options, processes, shimText: wrapper });
  await mkdir(directory, { recursive: true });
  assert.deepEqual(check(), [], 'A missing main entry remains busy');
  await writeFile(entryFile, 'export {};');
  assert.deepEqual(check(), [101]);
  for (const CurrentDirectory of [undefined, null, '', '.', path.dirname(directory), directory + '-other', path.join(directory, 'child')]) {
    assert.deepEqual(check([parent, { ...child, CurrentDirectory }]), []);
  }
  for (const changed of [{ ParentProcessId: 999 }, { CommandLine: child.CommandLine + ' --type=renderer' },
    { ExecutablePath: path.join(base, 'Antigravity.exe') }, { CommandLine: child.CommandLine.replace('main.mjs', 'other.mjs') }]) {
    assert.deepEqual(check([parent, { ...child, ...changed }]), []);
  }
  assert.deepEqual(check([{ ...parent, CommandLine: parent.CommandLine.replace('main.mjs', 'dist/server.cjs') }, child]), []);
  assert.deepEqual(check([{ ...parent, CommandLine: parent.CommandLine + ' & echo extra' }, child]), []);
  assert.deepEqual(check([child]), []);
  assert.deepEqual(check([parent, child], shimText.replace('ELECTRON_RUN_AS_NODE=1', 'ELECTRON_RUN_AS_NODE=0')), []);
  const legacyParent = { ...parent, ProcessId: 202, CommandLine: parent.CommandLine.replace('main.mjs', 'dist/server.cjs') };
  const legacy = { ...child, ProcessId: 102, ParentProcessId: 202, CommandLine: child.CommandLine.replace('main.mjs', 'dist/server.cjs'), CurrentDirectory: undefined };
  const sidecarIds = check([parent, child, legacyParent, legacy]);
  assert.deepEqual(sidecarIds, [101, 102]);
  const rows = '"Antigravity.exe","101","Console","1","1 K"\n"Antigravity.exe","102","Console","1","1 K"';
  assert.deepEqual(assertApplicationStopped({ tasklistText: rows, sidecarIds }), { processesStopped: true });
  assert.throws(() => assertApplicationStopped({ tasklistText: rows + '\n"Antigravity.exe","103","Console","1","1 K"', sidecarIds }), /Exit all/);
  await rm(entryFile);
  await mkdir(entryFile);
  assert.deepEqual(check(), [], 'A directory cannot stand in for the main module');
  await rm(entryFile, { recursive: true });
  const linkedEntry = path.join(directory, 'other.mjs');
  await writeFile(linkedEntry, 'export {};');
  try { await symlink(linkedEntry, entryFile, 'file'); }
  catch (error) {
    if (error.code !== 'EPERM') throw error;
    t.diagnostic('Symbolic-link creation is unavailable for this account. The ordinary-file and directory cases were checked.');
    return;
  }
  assert.deepEqual(check(), [], 'A symbolic-link entry is not an ordinary file');
});

test('application guard excludes only its own official Node sidecar PID', () => {
  const self = '"Antigravity.exe","12000","Console","1","18,000 K"\r\n';
  assert.deepEqual(assertApplicationStopped({ tasklistText: self, ownPid: 12000, isNodeSidecar: true }), { processesStopped: true });
  assert.throws(() => assertApplicationStopped({ tasklistText: self, ownPid: 12000, isNodeSidecar: false }), /Exit all Antigravity/);
  assert.throws(() => assertApplicationStopped({ tasklistText: self + '"Antigravity.exe","12001","Console","1","180 K"', ownPid: 12000, isNodeSidecar: true }), /Exit all Antigravity/);
  assert.throws(() => assertApplicationStopped({ tasklistText: '"antigravity.exe","12001","Console","1","180 K"', ownPid: 12000 }), /Exit all Antigravity/);
});

test('application guard blocks orphan native LS and independent account component', () => {
  for (const name of ['language_server.exe', 'language_server_windows_x64.exe', 'Antigravity.Accounts.exe', 'ProfileHub.Accounts.exe']) {
    assert.throws(() => assertApplicationStopped({ tasklistText: `"${name}","12001","Console","1","180 K"`, ownPid: 12000, isNodeSidecar: true }), /Exit all Antigravity/);
  }
});

test('application guard requires valid CSV process output and rejects invalid invocation', () => {
  assert.deepEqual(assertApplicationStopped({ tasklistText: '"node.exe","11000","Console","1","180 K"', ownPid: 12000 }), { processesStopped: true });
  assert.throws(() => assertApplicationStopped({ tasklistText: '信息: 没有运行的任务匹配指定标准。', ownPid: 12000 }), /Unable to determine/);
  assert.throws(() => assertApplicationStopped({ tasklistText: 'arbitrary tasklist failure', ownPid: 12000 }), /Unable to determine/);
  assert.throws(() => assertApplicationStopped({ tasklistText: '', ownPid: 12000 }), /Unable to determine/);
  assert.throws(() => assertApplicationStopped({ tasklistText: 'x', ownPid: 0 }), /Invalid installer/);
});
