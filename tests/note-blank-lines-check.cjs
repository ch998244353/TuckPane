'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const htmlPath = path.resolve(__dirname, '../src/TuckPane/Assets/NoteEditor.html');
const html = fs.readFileSync(htmlPath, 'utf8');
const inlineScript = html.match(/<script\b[^>]*>([\s\S]*?)<\/script>/i)?.[1];
assert.ok(inlineScript, 'NoteEditor.html must contain its inline script.');
// Compilation only: no document/window, browser, selection or input automation.
new vm.Script(inlineScript, { filename: htmlPath });

const functionSource = inlineScript.match(/function blankLinesToAppend\([^)]*\)\s*\{[^{}]*\}/)?.[0];
assert.ok(functionSource, 'The production blankLinesToAppend function was not found.');
const append = vm.runInNewContext(`(${functionSource})`, {}, { timeout: 1000 });
const cases = [
  ['implicit first line', 0, 0, 25.2, 0],
  ['just above next line', 25.19, 0, 25.2, 0],
  ['next line boundary', 25.2, 0, 25.2, 1],
  ['just below next line', 25.21, 0, 25.2, 1],
  ['existing trailing empty rows', 4 * 25.2, 3 * 25.2, 25.2, 1],
  ['scrolled content coordinate', 5 * 25.2 + 3, 2 * 25.2, 25.2, 3],
  ['fractional line height', 3 * 23.75, 23.75, 23.75, 2],
  ['last line below a non-grid-aligned image', 165, 115, 25, 2],
  ['inside existing last line', 15, 25.2, 25.2, 0]
];
for (const [name, contentY, lastLineTop, lineHeight, expected] of cases)
  assert.equal(append(contentY, lastLineTop, lineHeight), expected, name);

console.log(`PASS note-blank-lines: ${cases.length} production pure-function cases and complete inline-script syntax. DOM measurement, caret, IME, images and undo remain manual acceptance.`);
