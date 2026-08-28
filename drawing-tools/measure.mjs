// measure.mjs — ground-truth bounding boxes straight from the model, in metres, so the
// drawing view scale can be computed rather than inferred from what the view renders.
import { openSession, apiGet } from './lib.mjs';

const DID = '52635ed919358eeea41371dc';
const WID = '3fd566638e978a09e85a41cb';

const targets = [
  ['Big Case assembly', `/api/v6/assemblies/d/${DID}/w/${WID}/e/196d1dcc6c8f1cc346a97033/boundingboxes`],
  ['New Big Case studio (all)', `/api/v6/partstudios/d/${DID}/w/${WID}/e/d02af6edc49b9c78d7d55b65/boundingboxes`],
  ['PCB studio (all)', `/api/v6/partstudios/d/${DID}/w/${WID}/e/1c51e0034963ba8ea21ce7c5/boundingboxes`],
  ['Shitty big case studio', `/api/v6/partstudios/d/${DID}/w/${WID}/e/a24d27e87f1a5ec57a4247c7/boundingboxes`],
  ['small Enclosure studio', `/api/v6/partstudios/d/${DID}/w/${WID}/e/cd9a8ded2f10f7d5d9095b79/boundingboxes`],
];

const span = (b) => b && b.highX !== undefined
  ? `${((b.highX - b.lowX) * 1000).toFixed(2)} x ${((b.highY - b.lowY) * 1000).toFixed(2)} x ${((b.highZ - b.lowZ) * 1000).toFixed(2)} mm`
  : JSON.stringify(b).slice(0, 200);

const { browser, page } = await openSession();
try {
  for (const [label, path] of targets) {
    const r = await apiGet(page, path);
    console.log(`${label}: ${r.status}`);
    if (r.status === 200) console.log(`  ${span(r.body)}`);
    else console.log(`  ${JSON.stringify(r.body).slice(0, 160)}`);
  }

  // Per-part boxes in the enclosure studio, to identify base vs cover by size.
  console.log('\nper-part boxes in "New Big Case":');
  const parts = await apiGet(page, `/api/v6/parts/d/${DID}/w/${WID}/e/d02af6edc49b9c78d7d55b65`);
  for (const p of parts.body ?? []) {
    const bb = await apiGet(page, `/api/v6/parts/d/${DID}/w/${WID}/e/d02af6edc49b9c78d7d55b65/partid/${encodeURIComponent(p.partId)}/boundingboxes`);
    console.log(`  ${p.partId.padEnd(6)} ${String(p.name).padEnd(18)} ${bb.status === 200 ? span(bb.body) : bb.status}`);
  }
} finally {
  await browser.close();
}
