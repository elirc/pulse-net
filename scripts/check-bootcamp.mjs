import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const folder = path.join(root, 'astradocs', 'bootcamp');
const ledger = JSON.parse(fs.readFileSync(path.join(folder, 'stories.json'), 'utf8'));
const errors = [];
const allowed = new Set(['planned', 'in_progress', 'implemented', 'verified']);
const actual = new Set();
const expected = new Set();
for (const file of ['18-junior-user-stories.md', '19-midlevel-feature-user-stories.md', '20-mid-senior-feature-user-stories.md']) {
  const text = fs.readFileSync(path.join(root, 'astradocs', file), 'utf8');
  for (const match of text.matchAll(/<a id="((?:us|mid|sr)-\d+)"/g)) expected.add(match[1].toUpperCase());
}
function checkLink(base, target, label) {
  if (/^(?:https?:|mailto:|#)/.test(target)) return;
  const file = target.split('#')[0];
  if (file && !fs.existsSync(path.resolve(base, decodeURIComponent(file)))) errors.push(`${label}: missing ${target}`);
}
for (const story of ledger.stories) {
  if (actual.has(story.id)) errors.push(`Duplicate story: ${story.id}`);
  actual.add(story.id);
  if (!allowed.has(story.status)) errors.push(`${story.id}: invalid status ${story.status}`);
  checkLink(folder, story.plan, story.id);
  for (const file of story.implementation) checkLink(folder, file, story.id);
  if (story.lesson) checkLink(folder, story.lesson, story.id);
  if (['implemented', 'verified'].includes(story.status) && (!story.implementation.length || !story.lesson))
    errors.push(`${story.id}: implementation status requires source and lesson links`);
  if (story.status === 'verified' && !story.verification.length) errors.push(`${story.id}: verification evidence missing`);
}
if (expected.size !== 75 || actual.size !== 75) errors.push(`Expected 75 stories; plans=${expected.size}, ledger=${actual.size}`);
for (const id of expected) if (!actual.has(id)) errors.push(`Story missing from ledger: ${id}`);
for (const id of actual) if (!expected.has(id)) errors.push(`Unknown story in ledger: ${id}`);
for (const file of fs.readdirSync(folder).filter(name => name.endsWith('.md'))) {
  const text = fs.readFileSync(path.join(folder, file), 'utf8');
  for (const match of text.matchAll(/\[[^\]]*\]\(([^\s)]+)\)/g)) checkLink(folder, match[1], file);
}
if (errors.length) {
  console.error(errors.join('\n'));
  process.exitCode = 1;
} else {
  const counts = Object.fromEntries([...allowed].map(status => [status, ledger.stories.filter(story => story.status === status).length]));
  console.log(`Bootcamp links and all 75 story IDs are valid. ${JSON.stringify(counts)}`);
  console.log('This checks documentation consistency, not feature correctness. Read the recorded test evidence.');
}
