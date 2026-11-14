# Antigravity Chinese localization engine

The runtime code, dictionaries and reviewed translation scopes are maintained with Antigravity Accounts. The compatibility bundle chooses which installed components to prepare; it does not enable independent accounts or Chinese localization merely because both engines are present.

## Preparing a combined installation

`prepareLocalization({ sourceInstallRoot, targetInstallRoot, outputRoot, compatibleSource: true })` builds from a separate unlocalized candidate, including an Accounts-prepared candidate. Both input roots must identify the same application version, executable and unpacked content. The returned manifest identifies the actual target installation. No target file, account process or application is opened for writing.

The coordinator writes the resulting ASAR through its installation transaction and uses `localizationInstallRecord(manifest, { artifactDir, appliedUtc })` for the existing schema-1 installation record. The coordinator must preserve the unlocalized source and its original metadata under the configured localization backup root. Account authorizations and existing localization state remain in their current locations.

## Production package

Install the locked production dependencies with `npm ci --omit=dev --ignore-scripts --no-audit --no-fund`. The runtime payload consists of the two root entrypoints, package metadata, `src`, `dictionaries`, the launch/maintenance scripts listed in `runtime-files.json`, licenses, notices and production `node_modules`. Tests and all original-program inputs, state, backups, build output and source-review material are excluded.

Run `node scripts/Sync-NativePrograms.mjs` from this repository before packaging. It copies the Accounts-owned reviewed native-program description and generates the PowerShell fallback rejection list. The consistency test detects an outdated generated copy. This gives the standalone Chinese engine the same native-program rejection set without requiring an Accounts installation next to it.

The registered independent Node runtime is reused. The bundle does not replace a system runtime or store account credentials.

## Tests

`npm test` runs the source-independent tests and explicitly reports skipped suites that need original vendor inputs. To exercise those suites, set `LOCALIZATION_TEST_DATA_ROOT` to an external test-data directory containing the prior `verification` inputs. Set `ANTIGRAVITY_TEST_INSTALL` to the unmodified 2.19.1 copy and `ANTIGRAVITY_TEST_UPGRADE_INSTALL` to its 2.21.1 input. The test runner never implicitly chooses the modified live installation. Historical Accounts tests additionally require their explicit public receipt directory. These inputs are not redistributed.
