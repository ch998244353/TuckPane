// Non-GUI tests of production command/acknowledgement functions with an in-memory editing boundary.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const file = path.join(__dirname, '../src/TuckPane/Assets/NoteEditor.html');
const script = fs.readFileSync(file, 'utf8').match(/<script\b[^>]*>([\s\S]*?)<\/script>/i)[1];
new vm.Script(script, { filename: file });
const names = ['captureEditTarget', 'validEditTarget', 'nativeImageEdit', 'rememberImageTarget',
  'hasImageSelection', 'clearsImageSelection', 'imageCommand', 'completeImageClipboard'];
const functions = names.map(name => {
  const match = script.match(new RegExp(`(?:async )?function ${name}\\([^)]*\\) \\{[\\s\\S]*?^      \\}`, 'm'));
  assert.ok(match, `Missing production ${name}`);
  return match[0];
}).join('\n');

function fixture() {
  const calls = [], messages = [], pastes = [];
  const image = { inside: true, isConnected: true, getAttribute: () => 'data:image/png;base64,AA==' };
  const root = { inside: true };
  const range = { commonAncestorContainer: root, collapsed: true, cloneRange() { return { ...this }; },
    selectNode(node) { calls.push(['selectNode', node]); }, collapse(start) { calls.push(['collapse', start]); } };
  const selection = { rangeCount: 1, text: '', getRangeAt: () => range, toString() { return this.text; },
    removeAllRanges() {}, addRange() {} };
  const context = vm.createContext({
    documentEpoch: 1, editRevision: 0, imageRequestId: 0, imageRequests: new Map(), maximumHtmlLength: 1024,
    selectedImage: image, editor: { contains: node => node?.inside === true, innerHTML: 'body', focus() {} },
    window: { getSelection: () => selection },
    document: { execCommand(command, unused, value) { calls.push(['edit', command, value]); return true; },
      createElement: () => ({ innerHTML: '', appendChild(fragment) { this.innerHTML = fragment.html; } }) },
    cleanHtml: value => value, selectImage() {}, alignImagesToRuledGrid() {}, positionHandles() {},
    scheduleChanged: () => calls.push(['changed']), post: payload => messages.push(payload),
    insertImageSources: async (sources, target) => pastes.push({ sources, target }),
    insertText: (text, target) => pastes.push({ text, target })
  });
  vm.runInContext(functions, context, { timeout: 1000 });
  return { context, image, range, selection, calls, messages, pastes };
}

(async () => {
  for (const ok of [false, true]) {
    const f = fixture(), token = f.context.rememberImageTarget(f.image);
    f.context.imageCommand(token, 'cut');
    assert.equal(f.calls.length, 0, 'A cut must not mutate before clipboard acknowledgement.');
    assert.equal(f.messages[0].action, 'cut');
    await f.context.completeImageClipboard({ token, ok });
    assert.equal(f.calls.filter(c => c[0] === 'edit').length, ok ? 1 : 0);
    if (ok) {
      assert.equal(f.calls.find(c => c[0] === 'edit')[1], 'delete', 'Use the native undoable deletion transaction.');
      assert.equal(f.calls.some(c => c[0] === 'collapse'), false, 'Deletion must select the entire image.');
    }
    await f.context.completeImageClipboard({ token, ok: true });
    assert.equal(f.calls.filter(c => c[0] === 'edit').length, ok ? 1 : 0, 'Duplicate acknowledgements must be ignored.');
  }
  for (const invalidate of [f => f.image.isConnected = false, f => f.context.documentEpoch++,
    f => f.image.getAttribute = () => 'changed image']) {
    const f = fixture(), token = f.context.rememberImageTarget(f.image);
    f.context.imageCommand(token, 'cut');
    invalidate(f);
    await f.context.completeImageClipboard({ token, ok: true });
    assert.equal(f.calls.length, 0, 'A stale response must not delete another image/document.');
  }
  const copy = fixture(), copyToken = copy.context.rememberImageTarget(copy.image);
  copy.context.imageCommand(copyToken, 'copy');
  await copy.context.completeImageClipboard({ token: copyToken, ok: true });
  assert.equal(copy.calls.length, 0, 'Copy must leave the document and undo history untouched.');
  const paste = fixture(), pasteToken = paste.context.rememberImageTarget(paste.image);
  paste.context.imageCommand(pasteToken, 'paste');
  const dataUrl = 'data:image/png;base64,AA==';
  await paste.context.completeImageClipboard({ token: pasteToken, ok: true, dataUrl, text: 'ignored' });
  assert.equal(paste.pastes[0].sources[0], dataUrl);
  assert.equal(paste.pastes[0].target.image, paste.image, 'Preserve the image-menu target across the host round trip.');
  paste.context.nativeImageEdit('insertHTML', '<img><br>', paste.pastes[0].target);
  assert.equal(paste.calls.find(c => c[0] === 'collapse')[1], false, 'Insert after, not over, the image.');
  assert.equal(paste.calls.find(c => c[0] === 'edit')[1], 'insertHTML');
  const text = fixture(), textToken = text.context.rememberImageTarget(text.image);
  text.context.imageCommand(textToken, 'paste');
  await text.context.completeImageClipboard({ token: textToken, ok: true, text: 'hello' });
  assert.equal(text.pastes[0].text, 'hello');
  text.selection.text = 'selected body text';
  assert.equal(text.context.hasImageSelection(), false, 'Text selection takes priority over an old image selection.');
  const full = fixture();
  assert.equal(full.context.nativeImageEdit('insertHTML', 'x'.repeat(1025), full.context.captureEditTarget(full.image)), false);
  assert.equal(full.calls.length, 0, 'Reject oversized content before editing.');
  assert.equal(full.messages[0].tooLarge, true);
  const edited = fixture(), staleRange = edited.context.captureEditTarget();
  edited.context.editRevision++;
  assert.equal(edited.context.nativeImageEdit('insertHTML', '<img>', staleRange), false,
    'A live Range relocated by a later body edit must not accept asynchronous image insertion.');
  assert.equal(edited.calls.length, 0);
  const replace = fixture();
  replace.context.editor.innerHTML = 'x'.repeat(1000);
  replace.range.collapsed = false;
  replace.range.cloneContents = () => ({ html: 'x'.repeat(900) });
  assert.equal(replace.context.nativeImageEdit('insertHTML', 'y'.repeat(100), replace.context.captureEditTarget()), true,
    'A shorter replacement must deduct selected content from the capacity check.');
  for (const key of ['ArrowLeft', 'Home', 'Enter', 'x', 'Tab'])
    assert.equal(replace.context.clearsImageSelection({ key }), true, `${key} must return subsequent edits to the body.`);
  assert.equal(replace.context.clearsImageSelection({ key: 'a', ctrlKey: true }), true);
  assert.equal(replace.context.clearsImageSelection({ key: 'ArrowLeft', ctrlKey: true }), true);
  assert.equal(replace.context.clearsImageSelection({ key: 'ArrowLeft', altKey: true }), false);
  assert.equal(replace.context.clearsImageSelection({ key: 'c', ctrlKey: true }), false);
  // Resource wiring and sanitation are inspected statically; no browser/clipboard/undo engine is run.
  assert.match(script, /editor\.addEventListener\('contextmenu'/);
  assert.match(script, /editor\.addEventListener\('cut', onCutText\)/);
  assert.doesNotMatch(script.match(/function cleanHtml\(html\)[\s\S]*?^      \}/m)[0], /data-selected|imageRequests/);
  console.log('PASS note-image-commands: copy/cut acknowledgements, stale targets, image/text paste, after-image insertion, native edit transactions, size guard and syntax. Actual clipboard/undo/menu remain manual.');
})().catch(error => { console.error(error); process.exitCode = 1; });
