'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const { randomUUID } = require('node:crypto');
const { ComponentError } = require('./component-client.cjs');
const MODES = new Set(['off', 'on', 'manual']);

function createAccountPreferences(filename) {
  if (typeof filename !== 'string' || !path.isAbsolute(filename)) throw new ComponentError('account_preferences_invalid');
  async function get() {
    try {
      const value = JSON.parse(await fs.readFile(filename, 'utf8'));
      if (value?.schemaVersion !== 1 || !MODES.has(value.dataUsageConsent)) throw new Error();
      return { dataUsageConsent: value.dataUsageConsent };
    } catch (error) {
      if (error.code === 'ENOENT') return { dataUsageConsent: 'off' };
      throw new ComponentError('account_preferences_unavailable');
    }
  }
  async function set(value) {
    if (!value || typeof value !== 'object' || Array.isArray(value) ||
        Object.keys(value).length !== 1 || !MODES.has(value.dataUsageConsent)) throw new ComponentError('unsupported_parameter');
    const pending = filename + '.' + randomUUID() + '.new';
    try {
      await fs.mkdir(path.dirname(filename), { recursive: true });
      await fs.writeFile(pending, JSON.stringify({ schemaVersion: 1, dataUsageConsent: value.dataUsageConsent }) + '\n', { flag: 'wx', mode: 0o600 });
      await fs.rename(pending, filename);
      return { dataUsageConsent: value.dataUsageConsent };
    } catch {
      throw new ComponentError('account_preferences_unavailable');
    } finally { await fs.rm(pending, { force: true }).catch(() => {}); }
  }
  return Object.freeze({ get, set });
}

module.exports = { createAccountPreferences };
