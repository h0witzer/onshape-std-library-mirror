// elements.mjs — the document's tab inventory and its current microversion.
//
// Read-only. This is the first thing to run when a script needs a namespace: a same-document
// custom feature is referenced as `e<eid>::m<microversion>`, and the microversion moves every
// time a tab is written, so it has to be read fresh rather than remembered.
import { openSession, apiGet, DID, WID } from './lib.mjs';

const { browser, page } = await openSession();
try {
  const els = await apiGet(page, `/api/v14/documents/d/${DID}/w/${WID}/elements`);
  console.log(`elements: ${els.status}`);
  for (const e of els.body ?? []) {
    console.log(`  ${e.elementType.padEnd(14)} ${e.id}  ${JSON.stringify(e.name)}`);
  }
  const mv = await apiGet(page, `/api/v14/documents/d/${DID}/w/${WID}/currentmicroversion`);
  console.log(`\ncurrentmicroversion: ${mv.status}  ${JSON.stringify(mv.body)}`);
} finally {
  await browser.close();
}
