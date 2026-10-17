// Evaluate only generated offline fixtures; never load the live FlClash root.
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const assert = require('assert/strict');
const root = path.resolve(process.argv[2]);
const marker = JSON.parse(fs.readFileSync(path.join(root, '.chunson-flclash-fixture.json'), 'utf8'));
assert.equal(path.resolve(marker.root), root);
assert.equal(marker.purpose, 'ChunsonSwitch.FlClashDns.Checks');
assert.ok(path.basename(root).startsWith('flclash-dns-fixture-'));
const spec = JSON.parse(fs.readFileSync(path.join(root, 'all-fixture.json'), 'utf8'));
const prefs = JSON.parse(JSON.parse(fs.readFileSync(path.join(root, 'shared_preferences.json'), 'utf8'))['flutter.config']);
for (const item of spec) {
  assert.match(item.profile, /^\d+$/); assert.match(item.script, /^\d+$/);
  const input = JSON.parse(fs.readFileSync(path.join(root, 'profiles', item.profile + '.yaml'), 'utf8'));
  const code = fs.readFileSync(path.join(root, 'scripts', item.script + '.js'), 'utf8');
  let result = JSON.parse(JSON.stringify(vm.runInNewContext(code + '\nmain(JSON.parse(inputJson));',
    { inputJson: JSON.stringify(input) }, { timeout: 1000 })));
  // Model the modern normalProfileKeys mask, applied after the profile script.
  for (const key of prefs.patchClashConfig['dns-override-keys'] || []) {
    if (Object.hasOwn(prefs.patchClashConfig.dns, key)) result.dns[key] = prefs.patchClashConfig.dns[key];
  }
  result.tun = { ...result.tun, ...prefs.patchClashConfig.tun };
  result.ipv6 = prefs.patchClashConfig.ipv6;
  assert.deepEqual(result.proxies, input.proxies, 'node credentials/definitions preserved');
  assert.deepEqual(result['proxy-groups'], input['proxy-groups'], 'groups preserved');
  assert.deepEqual(result.rules, item.custom ? ['DOMAIN,movie.fixture,DIRECT', ...input.rules] : input.rules, 'routing preserved');
  if (item.custom) assert.equal(result.originalCustom, 'preserved');
  assert.equal(result.dns.enable, true);
  assert.equal(result.dns.ipv6, false);
  assert.equal(result.ipv6, false);
  assert.equal(result.dns['respect-rules'], true);
  assert.deepEqual(result.dns.fallback, []);
  assert.ok(result.dns.nameserver.every(server => server.startsWith('https://') && server.endsWith('#' + item.group)));
  assert.ok(result.dns['default-nameserver'].every(server => server.startsWith('https://')));
  assert.ok(result.dns['proxy-server-nameserver'].every(server => server.startsWith('https://')));
  assert.equal(result.dns['nameserver-policy']['local.lan'], '192.168.1.1', 'local DNS policy preserved');
  assert.equal(result.tun.enable, true);
  assert.equal(result.tun['strict-route'], true);
  if (item.current) fs.writeFileSync(path.join(root, 'config.yaml'), JSON.stringify(result));
}
console.log('PASS generated DNS for ' + spec.length + ' subscriptions; original routing preserved');
