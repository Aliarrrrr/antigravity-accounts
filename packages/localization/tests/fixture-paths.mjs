import path from 'node:path';
import {fileURLToPath} from 'node:url';

// Official program inputs stay outside source control and release assets.
// Never silently use the already modified live installation as a test input.
export const testDataRoot=path.resolve(process.env.LOCALIZATION_TEST_DATA_ROOT||path.join(path.dirname(fileURLToPath(import.meta.url)),'..','testdata'));
