// inspect.mjs — read-only reconnaissance of the target document.
// Dumps element list, assembly structure, BOM, and part metadata to out/ so the
// drawing build can be written against real ids instead of guesses.
import { writeFile, mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, apiGet, OUT_DIR } from './lib.mjs';

const DID = process.env.OS_DID ?? '52635ed919358eeea41371dc';
const WID = process.env.OS_WID ?? '3fd566638e978a09e85a41cb';
const EID = process.env.OS_EID ?? 'd02af6edc49b9c78d7d55b65';

const save = async (name, data) => {
  await writeFile(join(OUT_DIR, name), JSON.stringify(data, null, 2));
  console.log(`  -> out/${name}`);
};

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });

  console.log('elements...');
  const elements = await apiGet(page, `/api/v6/documents/d/${DID}/w/${WID}/elements`);
  console.log(`  status ${elements.status}`);
  await save('elements.json', elements.body);

  if (Array.isArray(elements.body)) {
    for (const e of elements.body) {
      const mark = e.id === EID ? '  <== TARGET' : '';
      console.log(`  [${e.elementType}] ${e.name}  ${e.id}${mark}`);
    }
  }

  const target = Array.isArray(elements.body) ? elements.body.find((e) => e.id === EID) : null;
  const kind = target?.elementType ?? 'UNKNOWN';
  console.log(`\ntarget element type: ${kind}`);

  if (kind === 'ASSEMBLY') {
    console.log('assembly definition...');
    const asm = await apiGet(page, `/api/v6/assemblies/d/${DID}/w/${WID}/e/${EID}?includeMateFeatures=false&includeNonSolids=false`);
    console.log(`  status ${asm.status}`);
    await save('assembly.json', asm.body);
    for (const i of asm.body?.rootAssembly?.instances ?? []) {
      console.log(`  instance: ${i.name}  type=${i.type}  partId=${i.partId ?? '-'}  eid=${i.elementId}`);
    }

    console.log('bom...');
    const bom = await apiGet(page, `/api/v6/assemblies/d/${DID}/w/${WID}/e/${EID}/bom?indented=false&multiLevel=false`);
    console.log(`  status ${bom.status}`);
    await save('bom.json', bom.body);
  } else if (kind === 'PARTSTUDIO') {
    console.log('parts...');
    const parts = await apiGet(page, `/api/v6/parts/d/${DID}/w/${WID}/e/${EID}`);
    console.log(`  status ${parts.status}`);
    await save('parts.json', parts.body);
    for (const p of parts.body ?? []) console.log(`  part: ${p.name}  partId=${p.partId}`);
  }

  console.log('document metadata (units)...');
  const doc = await apiGet(page, `/api/v6/documents/${DID}`);
  await save('document.json', doc.body);
  console.log(`  defaultUnits: ${JSON.stringify(doc.body?.defaultWorkspace?.units ?? doc.body?.units ?? 'n/a')}`);

  console.log('\ndone.');
} finally {
  await browser.close();
}
