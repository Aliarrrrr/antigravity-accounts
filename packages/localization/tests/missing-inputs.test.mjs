import test from 'node:test';
import {requiredInputs,missingInputs} from './suite-inputs.mjs';

for(const name of Object.keys(requiredInputs)) if(missingInputs(name).length)
  test(name+' requires separately supplied original-program inputs',{skip:'Official program inputs are absent; this suite was not exercised.'},()=>{});
