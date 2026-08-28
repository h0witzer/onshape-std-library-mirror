// discover.mjs — probe what the drawings API offers this account: available templates,
// and the coordinate/units convention of a freshly created drawing.
import { writeFile, mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { openSession, apiGet, apiPost, waitForModify, OUT_DIR } from './lib.mjs';

const DID = '52635ed919358eeea41371dc';
const WID = '3fd566638e978a09e85a41cb';
const ASM = '196d1dcc6c8f1cc346a97033';

const save = async (n, d) => { await writeFile(join(OUT_DIR, n), JSON.stringify(d, null, 2)); console.log(`  -> out/${n}`); };

const { browser, page } = await openSession();
try {
  await mkdir(OUT_DIR, { recursive: true });

  console.log('probing template discovery endpoints...');
  for (const p of [
    '/api/v6/drawings/templates',
    '/api/v6/documents?filter=9&limit=20',
    '/api/v6/documents?q=template&filter=0&limit=20',
    '/api/v6/companies',
  ]) {
    const r = await apiGet(page, p);
    console.log(`  ${r.status}  ${p}`);
    if (r.status === 200) await save(`probe${p.replace(/[^a-z0-9]/gi, '_')}.json`, r.body);
  }

  console.log('\ncreating a scratch drawing from the assembly (default template)...');
  const create = await apiPost(page, `/api/v6/drawings/d/${DID}/w/${WID}/create`, {
    drawingName: 'API probe - delete me',
    elementId: ASM,
  });
  console.log(`  status ${create.status}`);
  await save('create-probe.json', create.body);
  const drawEid = create.body?.id ?? create.body?.elementId;
  console.log(`  drawing eid: ${drawEid}`);
  if (!drawEid) { console.log('  no eid returned; stopping.'); }
  else {
    console.log('exporting DRAWING_JSON to learn the coordinate system...');
    const tr = await apiPost(page, `/api/v6/drawings/d/${DID}/w/${WID}/e/${drawEid}/translations`, { formatName: 'DRAWING_JSON' });
    console.log(`  status ${tr.status}`);
    await save('translation-probe.json', tr.body);
  }
  console.log('\ndone.');
} finally {
  await browser.close();
}
