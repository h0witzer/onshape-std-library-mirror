// set-params.mjs — change parameters on a feature that is ALREADY in a Part Studio.
//
// The insert scripts build a feature from its defaults, which is the wrong tool for asking a
// question of a fixture that is already configured: re-inserting it to flip one toggle resets
// every other parameter, and the run that comes back is no longer the run being compared against.
// This reads the feature as the document stores it, changes only the named parameters, and posts it
// back on its own featureId - so a rebuild is the only thing that differs between two runs.
//
// A parameter absent from the stored feature is ADDED. A new boolean on an existing feature
// instance is not stored at all until something writes it, and until then it reads as its default,
// which is how a freshly added dialog toggle appears to be ignored.
//
//   PS_EID=<part studio> MATCH=sweepRotatingCube SET=describeCorrespondence=true,measureShellSeams=true \
//     node set-params.mjs
//
// Values: `true` / `false` become booleans; anything else becomes a quantity EXPRESSION, so
// `travel=250*mm` and `patchStations=17` both work. Enums take their value name, e.g.
// `closureStage=TRIM`.
import { openSession, apiGet, apiPost, DID, WID, docUrl } from './lib.mjs';

const PS_EID = process.env.PS_EID;
const MATCH = process.env.MATCH ?? '';
const SET = process.env.SET ?? '';
if (!PS_EID || !SET) {
  console.error('PS_EID=<part studio> and SET=name=value[,name=value...] are required.');
  process.exit(2);
}

const ASSIGNMENTS = SET.split(',').map((pair) => {
  const at = pair.indexOf('=');
  if (at < 0) throw new Error(`SET entry ${JSON.stringify(pair)} is not name=value`);
  return { id: pair.slice(0, at).trim(), raw: pair.slice(at + 1).trim() };
});

const featurePath = `/api/v14/partstudios/d/${DID}/w/${WID}/e/${PS_EID}/features`;

// An enum reads back as a BTMParameterEnum with its enumName; a value that is neither a boolean
// nor a number keeps whatever btType the stored parameter already had, because the enum's own type
// name cannot be guessed from the value alone.
const applyTo = (parameters, { id, raw }) => {
  const existing = parameters.find((p) => p.parameterId === id);
  if (raw === 'true' || raw === 'false') {
    const value = raw === 'true';
    if (existing) { existing.value = value; return `boolean ${value}`; }
    parameters.push({ btType: 'BTMParameterBoolean-144', parameterId: id, value });
    return `boolean ${value} (added)`;
  }
  if (existing?.btType?.includes('Enum')) { existing.value = raw; return `enum ${raw}`; }
  if (existing?.expression !== undefined) { existing.expression = raw; return `quantity ${raw}`; }
  if (existing) { existing.value = raw; return `value ${raw}`; }
  parameters.push({ btType: 'BTMParameterQuantity-147', parameterId: id, expression: raw });
  return `quantity ${raw} (added)`;
};

const { browser, page } = await openSession();
try {
  await page.goto(docUrl(PS_EID), { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(8000);

  const before = await apiGet(page, featurePath);
  const features = (before.body?.features ?? []).filter(
    (f) => !MATCH || (f.featureType ?? '').includes(MATCH) || (f.name ?? '').includes(MATCH));
  if (features.length === 0) {
    console.error(`no feature matched ${JSON.stringify(MATCH)}`);
    process.exitCode = 1;
  }

  for (const feature of features) {
    feature.parameters = feature.parameters ?? [];
    const applied = ASSIGNMENTS.map((a) => `${a.id} -> ${applyTo(feature.parameters, a)}`);
    const res = await apiPost(page, `${featurePath}/featureid/${encodeURIComponent(feature.featureId)}`,
      { feature });
    console.log(`${feature.featureType} (${feature.featureId}): status ${res.status}`);
    for (const line of applied) console.log(`   ${line}`);
    if (res.status >= 400) {
      console.log(`   ${JSON.stringify(res.body).slice(0, 400)}`);
      process.exitCode = 1;
    }
    await page.waitForTimeout(2000);
  }
} finally {
  await browser.close();
}
