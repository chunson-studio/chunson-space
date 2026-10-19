"""Build a bounded ZIP member manifest from an explicitly verified, finished public ZIP."""
import argparse
import hashlib
import json
import re
import stat
import struct
import zipfile
from pathlib import Path

ASSET = 'Chunson-Full-Windows-x64.zip'

def no_links(path):
    for p in [path, *path.parents]:
        if p.exists() and (p.is_symlink() or getattr(p.lstat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT):
            raise ValueError('Linked input/output')

def relative_path(path):
    if not path or len(path) > 180 or re.search(r'[\\:<>"|?*\x00-\x1f]', path) or path.startswith('/'):
        raise ValueError('Unsafe member path')
    parts = path.split('/')
    if any(not p or p in ('.', '..') or p.endswith(('.', ' ')) or re.match(r'^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)', p, re.I) for p in parts):
        raise ValueError('Unsafe member component')
    allowed = (path in ('ChunsonSwitch.exe', 'createdump.exe', 'ChunsonSwitch.deps.json', 'ChunsonSwitch.runtimeconfig.json', 'LICENSE.txt', 'LICENSE', 'README.md') or path.lower().endswith(('.dll', '.pdb'))) if len(parts) == 1 else (
        parts[0] in ('ThirdParty', 'Resources') or path.startswith(('scripts/proxy/', 'Widgets/')) or path == 'runtimes/mihomo/mihomo.exe' or len(parts) == 2 and re.fullmatch(r'[a-z]{2}(-[A-Za-z]{2,4})?', parts[0]) and path.endswith('.resources.dll'))
    if not allowed:
        raise ValueError('Not an application asset: ' + path)
    return path

def hash_stream(stream):
    h = hashlib.sha256()
    for block in iter(lambda: stream.read(65536), b''):
        h.update(block)
    return h.hexdigest()

def reject_extra(extra):
    while extra:
        if len(extra) < 4:
            raise ValueError('Invalid ZIP extra')
        kind, size = struct.unpack('<HH', extra[:4])
        if kind in (1, 0x9901) or size > len(extra) - 4:
            raise ValueError('Unsupported ZIP64/encrypted structure')
        extra = extra[4 + size:]

def generate(archive, version, prefix, zip_check):
    archive = Path(archive).absolute(); no_links(archive)
    if not re.fullmatch(r'v?\d{1,5}\.\d{1,5}\.\d{1,5}(\.\d{1,5})?', version) or any(int(x) > 65535 for x in version.lstrip('v').split('.')):
        raise ValueError('Invalid stable version')
    if not re.fullmatch(r'[A-Za-z0-9_-]+/', prefix):
        raise ValueError('Explicit single distribution root required')
    size = archive.stat().st_size
    if size > 2 * 1024 ** 3:
        raise ValueError('Archive too large')
    with archive.open('rb') as raw:
        archive_hash = hash_stream(raw)
        raw.seek(max(0, size - 65557)); tail = raw.read(65557)
    end = tail.rfind(b'PK\x05\x06')
    if end < 0 or len(tail) - end < 22:
        raise ValueError('Missing EOCD')
    _, disk, cd_disk, entries_disk, entries, cd_size, cd_offset, comment = struct.unpack('<4s4H2LH', tail[end:end + 22])
    if disk or cd_disk or entries_disk != entries or entries == 65535 or cd_offset + cd_size != size - len(tail) + end or len(tail) - end != 22 + comment:
        raise ValueError('Unsupported split/ZIP64/trailing archive structure')
    check = json.loads(Path(zip_check).read_text(encoding='utf-8-sig'))
    if not check.get('passed') or check.get('sha256', check.get('archiveSha256', '')).lower() != archive_hash:
        raise ValueError('Completed ZIP/hash verification required')
    files, seen, spans, total = [], set(), [], 0
    with zipfile.ZipFile(archive) as z, archive.open('rb') as raw:
        if len(z.infolist()) != entries or entries > 8192:
            raise ValueError('Invalid central directory count')
        for info in z.infolist():
            name = info.filename
            if not name.startswith(prefix):
                raise ValueError('Member outside distribution')
            rel = name[len(prefix):].rstrip('/')
            if not rel:
                if not info.is_dir(): raise ValueError('Invalid root member')
            else:
                # Directories need path validation but are not installation targets.
                if info.is_dir():
                    relative_path(rel + '/__directory__.dll') if rel.split('/')[0] not in ('runtimes', 'scripts') else relative_path('ThirdParty/' + rel)
                else: relative_path(rel)
            key = name.casefold().rstrip('/')
            if key in seen: raise ValueError('Case-insensitive duplicate member')
            seen.add(key)
            mode = (info.external_attr >> 16) & 0xffff
            if stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFDIR) or info.external_attr & 0x400:
                raise ValueError('Link/special member')
            if info.flag_bits & ~0x80e or info.compress_type not in (0, 8) or info.file_size > 256 * 1024 ** 2 or info.compress_size > 128 * 1024 ** 2:
                raise ValueError('Encrypted/unsupported ZIP member')
            reject_extra(info.extra)
            raw.seek(info.header_offset); header = raw.read(30)
            if len(header) != 30: raise ValueError('Truncated local header')
            sig, needed, flags, method, _, _, crc, compressed, unpacked, nl, el = struct.unpack('<4s5H3L2H', header)
            local_name = raw.read(nl).decode('utf-8' if flags & 0x800 else 'cp437'); extra = raw.read(el); reject_extra(extra)
            if sig != b'PK\x03\x04' or needed > 20 or flags != info.flag_bits or method != info.compress_type or local_name != name:
                raise ValueError('Local/central header mismatch')
            offset = info.header_offset + 30 + nl + el
            finish = offset + info.compress_size
            if not flags & 8 and (crc, compressed, unpacked) != (info.CRC, info.compress_size, info.file_size):
                raise ValueError('Local size/CRC mismatch')
            if flags & 8:
                raw.seek(finish); descriptor = raw.read(4)
                if descriptor == b'PK\x07\x08': descriptor = raw.read(4); finish += 4
                descriptor += raw.read(8); finish += 12
                if len(descriptor) != 12 or struct.unpack('<3L', descriptor) != (info.CRC, info.compress_size, info.file_size):
                    raise ValueError('Unsupported data descriptor')
            if finish > cd_offset: raise ValueError('Member exceeds data area')
            spans.append((info.header_offset, finish))
            if info.is_dir():
                if info.file_size: raise ValueError('Directory has content')
                continue
            with z.open(info) as stream: digest = hash_stream(stream)  # zipfile also verifies CRC
            total += info.file_size
            if total > 2 * 1024 ** 3 or len(files) >= 4096: raise ValueError('Manifest bound exceeded')
            files.append(dict(path=rel, method=info.compress_type, offset=offset, compressedSize=info.compress_size, size=info.file_size, sha256=digest))
    end = 0
    for begin, finish in sorted(spans):
        if begin < end: raise ValueError('Overlapping ZIP members')
        end = finish
    paths = {f['path'].casefold() for f in files}
    if not {'chunsonswitch.exe', 'chunsonswitch.dll'} <= paths: raise ValueError('Application entry missing')
    for p in paths:
        if any('/'.join(p.split('/')[:i]) in paths for i in range(1, len(p.split('/')))): raise ValueError('File/directory collision')
    result = dict(schema=1, version=version.lstrip('v'), assetName=ASSET, assetSize=size, assetSha256=archive_hash, files=files)
    if len(json.dumps(result).encode()) > 2 * 1024 ** 2: raise ValueError('Manifest too large')
    return result

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--archive', required=True); p.add_argument('--version', required=True)
    p.add_argument('--prefix', required=True); p.add_argument('--zip-check', required=True); p.add_argument('--output', required=True)
    a = p.parse_args(); output = Path(a.output).absolute(); no_links(output)
    result = generate(a.archive, a.version, a.prefix, a.zip_check)
    with output.open('x', encoding='utf-8') as f: json.dump(result, f, ensure_ascii=False, separators=(',', ':'))
    print(json.dumps(dict(files=len(result['files']), assetBytes=result['assetSize'], manifestBytes=output.stat().st_size)))

if __name__ == '__main__': main()
