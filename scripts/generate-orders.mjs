import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const directory = resolve(root, 'examples/batch');
mkdirSync(directory, { recursive: true });
const material = (parts, sheets = [[2800, 2070, 4]], name = 'MCP white board 16', thickness = 16) => ({ name, thickness, parts, sheets });
// Part rows: number, length, width, quantity, rotation permission, assembly.
const cases = [
    { id: '01-shelves', materials: [material([['S1', 720, 450, 6, 1, 'Shelves']])], placed: 6 },
    { id: '02-cabinet', materials: [material([['SIDE', 1800, 550, 2, 0, 'Cabinet'], ['TOP', 568, 550, 2, 1, 'Cabinet'], ['SHELF', 568, 530, 4, 1, 'Cabinet']])], placed: 8 },
    { id: '03-two-materials', materials: [material([['SIDE', 1800, 550, 2, 0, 'Cabinet'], ['SHELF', 568, 530, 3, 1, 'Cabinet']]), material([['BACK', 1800, 800, 2, 0, 'Cabinet']], [[2800, 2070, 2]], 'MCP MDF back 3', 3)], placed: 7 },
    { id: '04-rotation-required', materials: [material([['R1', 450, 800, 1, 1, 'Rotation']], [[1000, 500, 1]])], placed: 1 },
    { id: '05-rotation-forbidden', materials: [material([['R1', 450, 800, 1, 0, 'Rotation'], ['SMALL', 200, 200, 1, 1, 'Rotation']], [[1000, 500, 1]])], placed: 1 },
    { id: '06-oversize-part', materials: [material([['TOO-LARGE', 3000, 3000, 1, 1, 'Oversize'], ['S1', 600, 400, 2, 1, 'Shelves']])], placed: 2 },
    { id: '07-many-copies', materials: [material([['S1', 800, 600, 24, 1, 'Series']])], placed: 24 },
    { id: '08-repeated-numbers', materials: [material([['1', 700, 400, 2, 0, 'Cabinet A'], ['1', 500, 350, 3, 1, 'Cabinet B']])], placed: 5 },
];
const escape = value => String(value).replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
const manifest = [];
for (const testCase of cases) {
    const name = `MCP batch ${testCase.id}`;
    const groups = testCase.materials.map(group => {
        const parts = group.parts.map(([number, length, width, quantity, rotate, assembly]) => `        <part number="${escape(number)}" length="${length}" width="${width}" thick="${group.thickness}" quantity="${quantity}" rotate="${rotate}" assembly="${escape(assembly)}" />`).join('\n');
        const sheets = group.sheets.map(([length, width, quantity]) => `        <sheet length="${length}" width="${width}" thick="${group.thickness}" quantity="${quantity}" />`).join('\n');
        return `    <material name="${escape(group.name)}" wid_cut="3.2" sheet_border="5;5;5;5" nest_direct="2" corner="0">\n      <list_parts>\n${parts}\n      </list_parts>\n      <list_sheets>\n${sheets}\n      </list_sheets>\n    </material>`;
    }).join('\n');
    const xml = `<?xml version="1.0" encoding="utf-8"?>\n<data><data_order name="${name}" note="Furniture integration batch; integer millimetres">\n  <list_materials>\n${groups}\n  </list_materials>\n</data_order></data>\n`;
    writeFileSync(resolve(directory, `${testCase.id}.xml`), xml);
    const requested = testCase.materials.flatMap(group => group.parts).reduce((sum, part) => sum + part[3], 0);
    manifest.push({ id: testCase.id, file: `${testCase.id}.xml`, name, materials: testCase.materials.length, requestedParts: requested, expectedPlacedParts: testCase.placed, expectedUnplacedParts: requested - testCase.placed });
}
writeFileSync(resolve(directory, 'manifest.json'), JSON.stringify({ description: 'Source orders only. Expected counts describe these feasible examples; no Astra results are generated.', cases: manifest }, null, 2) + '\n');
console.log(`Generated ${cases.length} source orders in ${directory}`);
