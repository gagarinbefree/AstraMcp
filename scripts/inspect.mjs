import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = resolve(root, '.cache/inspector');
mkdirSync(output, { recursive: true });
const executable = resolve(root, process.argv[2] ?? 'publish/win-x86/AstraMcp.exe');
const configuration = resolve(root, process.argv[3] ?? 'config.json');
const session = resolve(output, 'session.json');
writeFileSync(session, JSON.stringify({ mcpServers: { astra: { command: executable, args: ['--config', configuration] } } }, null, 2));
const cli = resolve(root, 'node_modules/@modelcontextprotocol/inspector/clients/launcher/build/index.js');

function invoke(name, args, expectError = false) {
    const resultPath = resolve(output, `${name}.json`);
    const run = spawnSync(process.execPath, [cli, '--cli', '--config', session, '--server', 'astra', ...args, '--output', resultPath], {
        cwd: root, encoding: 'utf8', timeout: 30_000, windowsHide: true,
    });
    writeFileSync(resolve(output, `${name}.stderr.log`), run.stderr ?? '');
    if (run.error) throw run.error;
    if (!expectError) assert.equal(run.status, 0, run.stderr);
    const result = JSON.parse(readFileSync(resultPath, 'utf8'));
    if (expectError) assert.equal(result.isError, true);
    return result;
}

const tools = invoke('tools', ['--method', 'tools/list', '--strict']);
assert.equal(tools.tools.length, 6);
assert.ok(tools.tools.every(tool => tool.description.length > 40));
const xml = readFileSync(resolve(root, 'examples/order.xml'), 'utf8');
const valid = invoke('validate', ['--method', 'tools/call', '--tool-name', 'validate_order', '--tool-args-json', JSON.stringify({ xml })]);
assert.ok(valid.content.some(block => block.type === 'text' && block.text.includes('Requested parts: 2')));
invoke('invalid-order', ['--method', 'tools/call', '--tool-name', 'validate_order', '--tool-args-json', JSON.stringify({ xml: '<wrong/>' })], true);
invoke('capabilities', ['--method', 'tools/call', '--tool-name', 'get_capabilities']);
const templates = invoke('resources', ['--method', 'resources/templates/list']);
assert.ok(templates.resourceTemplates.some(template => template.uriTemplate === 'astra://jobs/{jobId}/{kind}'));
const report = { inspectorVersion: '2.13.0', sdkVersion: '2.3.0', executable, checkedAtUtc: new Date().toISOString(), checks: ['strict tool discovery', 'valid order', 'actionable tool error', 'capabilities', 'resource template'] };
writeFileSync(resolve(output, 'report.json'), JSON.stringify(report, null, 2));
console.log(`MCP Inspector: ${report.checks.length} checks passed. Evidence: ${output}`);
