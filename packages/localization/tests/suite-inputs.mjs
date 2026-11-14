import fs from 'node:fs';
import path from 'node:path';
import {testDataRoot} from './fixture-paths.mjs';

const old=path.join(testDataRoot,'verification','repair-2026-10-10','baseline');
const pinned=process.env.ANTIGRAVITY_TEST_INSTALL||path.join(testDataRoot,'verification','baseline');
const upgraded=process.env.ANTIGRAVITY_TEST_UPGRADE_INSTALL||path.join(testDataRoot,'verification','repair-2026-10-09','baseline');
const archive=root=>path.join(root,'resources','app.asar');
const current=path.join(testDataRoot,'verification','repair-2026-11-04','current','dist','preload.js');
export const requiredInputs=Object.freeze({
  '2-25-compatibility.test.mjs':[archive(path.join(testDataRoot,'verification','repair-2026-10-22','baseline'))],
  '2-26-compatibility.test.mjs':[archive(path.join(testDataRoot,'verification','repair-2026-10-24','baseline'))],
  '2-27-compatibility.test.mjs':[archive(path.join(testDataRoot,'verification','repair-2026-10-29','baseline'))],
  '2-29-accounts-compatibility.test.mjs':[process.env.ANTIGRAVITY_TEST_ACCOUNTS_STATE_ROOT?path.join(process.env.ANTIGRAVITY_TEST_ACCOUNTS_STATE_ROOT,'adapter-installation.json'):''],
  '2-31-compatibility.test.mjs':[path.join(testDataRoot,'verification','2.31.0-compat-review','current','dist','preload.js')],
  '2-32-compatibility.test.mjs':[path.join(testDataRoot,'verification','2.32.1-compat-review','current','dist','preload.js'),path.join(testDataRoot,'verification','2.32.1-compat-review','accounts-main.js')],
  'automatic-compatibility.test.mjs':[archive(process.env.ANTIGRAVITY_TEST_AUTOMATIC_INSTALL||old)],
  'build-source-race.test.mjs':[current],
  'durable.test.mjs':[archive(old),archive(upgraded)],
  'localization-refresh.test.mjs':[archive(old)],
  'native-compatibility.test.mjs':[archive(old),current,path.join(testDataRoot,'state','backups','2.28.2','0CB80A18EBE174F7EE0C4B619CDCF57B9BF015BD5427FFD60195CF41B7B0FD9D','app.asar')],
  'native-patches.test.mjs':[archive(pinned)],
  'prepare-localization.test.mjs':[archive(process.env.LOCALIZATION_TEST_PREPARE_INSTALL||old)],
  'toolkit.test.mjs':[archive(pinned)],
  'translation-module-refresh.test.mjs':[archive(old),current],
  'version-compatibility.test.mjs':[archive(pinned),archive(upgraded)],
});
export const missingInputs=name=>(requiredInputs[name]||[]).filter(filename=>!filename||!fs.existsSync(filename));
