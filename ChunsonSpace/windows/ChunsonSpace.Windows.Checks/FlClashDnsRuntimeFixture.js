// Offline-only generated configuration fixture. Never load a live FlClash directory.
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const root = path.resolve(process.argv[2]);
const marker = JSON.parse(fs.readFileSync(path.join(root, '.chunson-flclash-fixture.json'), 'utf8'));
if (!path.basename(root).startsWith('flclash-dns-fixture-') || marker.purpose !== 'ChunsonSwitch.FlClashDns.Checks' || path.resolve(marker.root) !== root) throw Error('Invalid fixture root');
const scripts = fs.readdirSync(path.join(root, 'scripts')).filter(name => name.endsWith('.js'));
const outer = JSON.parse(fs.readFileSync(path.join(root, 'shared_preferences.json'), 'utf8'));
const preferences = JSON.parse(outer['flutter.config']);
const patch = preferences.patchClashConfig;
const modern = !Object.prototype.hasOwnProperty.call(preferences, 'overrideDns');
const globalOnly = process.argv[3] === '--global-only';
if (process.argv[3] && !globalOnly) throw Error('Unknown fixture generation mode');
if (globalOnly && !modern) throw Error('Global-only mode requires the modern fixture');
let transform;
if (!globalOnly) {
  if (scripts.length !== 1) throw Error('Fixture needs one managed script');
  const code = fs.readFileSync(path.join(root, 'scripts', scripts[0]), 'utf8');
  // The application's script and config share one JS realm. Parse JSON inside
  // this realm so plain-object validation sees its own Object.prototype.
  transform = config => vm.runInNewContext(code + '\nmain(JSON.parse(fixtureConfigJson));',
    { fixtureConfigJson: JSON.stringify(config) }, { timeout: 1000 });
}
let runtime;
if (modern) {
  const profiles = fs.readdirSync(path.join(root, 'profiles')).filter(name => name.endsWith('.yaml'));
  if (profiles.length !== 1) throw Error('Modern fixture requires exactly one raw profile');
  // Upstream setup evaluates the script against rawConfig first. A numeric profile
  // ID may exceed JavaScript's integer precision, so use the sole guarded fixture file.
  const raw = JSON.parse(fs.readFileSync(path.join(root, 'profiles', profiles[0]), 'utf8'));
  runtime = globalOnly ? raw : transform(raw);
  // ProfileOverrides normalProfileKeys intersects the stored DNS mask; unsupported
  // keys never enter the global patch. Merge only the selected fields after script.
  const normalKeys = new Set(['listen', 'ipv6', 'enhanced-mode', 'fake-ip-filter',
    'fake-ip-filter-mode', 'default-nameserver', 'nameserver',
    'proxy-server-nameserver', 'direct-nameserver']);
  const dns = Object.assign({}, runtime.dns);
  if (dns.enable !== true) {
    dns.enable = true;
    dns['enhanced-mode'] = 'fake-ip';
    dns.nameserver = ['https://doh.pub/dns-query', 'https://dns.alidns.com/dns-query'];
  }
  for (const key of patch['dns-override-keys'] || []) {
    if (normalKeys.has(key) && Object.prototype.hasOwnProperty.call(patch.dns, key)) dns[key] = patch.dns[key];
  }
  const nonDnsPatch = Object.assign({}, patch);
  delete nonDnsPatch.dns;
  delete nonDnsPatch['dns-override-keys'];
  if (patch.tun) nonDnsPatch.tun = Object.assign({}, runtime.tun, patch.tun);
  runtime = Object.assign(runtime, nonDnsPatch, { dns });
} else {
  const prior = JSON.parse(fs.readFileSync(path.join(root, 'config.yaml'), 'utf8'));
  runtime = transform(Object.assign(prior, patch));
}
fs.writeFileSync(path.join(root, 'config.yaml'), JSON.stringify(runtime));
