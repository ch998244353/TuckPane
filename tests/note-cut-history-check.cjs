'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const htmlPath = path.resolve(__dirname, '../src/TuckPane/Assets/NoteEditor.html');
const script = fs.readFileSync(htmlPath, 'utf8').match(/<script\b[^>]*>([\s\S]*?)<\/script>/i)?.[1];
assert.ok(script, 'Missing inline editor script.');
new vm.Script(script, { filename: htmlPath }); // Syntax only; never run the editor/DOM.
const source = script.match(/function onCutText\(event\)\s*\{[\s\S]*?^\s{6}\}/m)?.[0];
assert.ok(source, 'Missing production onCutText handler.');
assert.match(script, /editor\.addEventListener\('cut',\s*onCutText\)/);
assert.doesNotMatch(source, /preventDefault|deleteFromDocument|deleteContents|execCommand/);

let selected = '中文第一行\n第二行';
const timers = [], messages = [];
const cut = vm.runInNewContext(`(${source})`, {
  window: { getSelection: () => selected == null ? null : { toString: () => selected } },
  setTimeout: (callback, delay) => { assert.equal(delay, 0); timers.push(callback); },
  post: message => messages.push(JSON.parse(JSON.stringify(message)))
}, { timeout: 1000 });
const event = { preventDefault: () => assert.fail('Default cut must remain enabled.') };
cut(event);
assert.equal(messages.length, 0, 'Do not race the browser default clipboard write.');
assert.equal(timers.length, 1, 'Schedule exactly one host write.');
selected = 'selection changed after cut';
timers.shift()();
assert.deepEqual(messages, [{ type: 'copyText', text: '中文第一行\n第二行' }], 'Send the original selection snapshot once.');
for (selected of ['', null]) cut(event);
assert.equal(timers.length, 0, 'Empty/missing selection must not schedule a write.');
assert.equal(messages.length, 1);
const host = fs.readFileSync(path.resolve(__dirname, '../src/TuckPane/NoteWindow.xaml.cs'), 'utf8');
assert.match(host, /Clipboard\.SetContentWithOptions\(\s*content,\s*new ClipboardContentOptions\s*\{\s*IsAllowedInHistory\s*=\s*true\s*\}\)/);
console.log('PASS note-cut-history: production handler snapshot, delayed single write, empty selection and default-cut preservation; full script syntax and host history option. Clipboard, undo and Win+V remain manual.');
