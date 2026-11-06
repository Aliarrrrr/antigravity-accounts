import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
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
