#!/usr/bin/env python3
"""Download and verify the pinned restic.exe (third_party/restic/VERSION).

usage: python3 tools/fetch_restic.py [--out third_party/restic/restic.exe]

Verification: the zip must match the official SHA256SUMS of the release AND the zip_sha256 pin;
the extracted exe must match exe_sha256. While a pin is "unpinned" the computed value is printed
and the script fails (CI must never run with an unverified binary).
"""
import hashlib, io, os, sys, urllib.request, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VERSION_FILE = os.path.join(ROOT, 'third_party', 'restic', 'VERSION')

def read_version():
    v = {}
    for line in open(VERSION_FILE, encoding='utf-8'):
        line = line.strip()
        if line and not line.startswith('#'):
            k, _, val = line.partition('=')
            v[k.strip()] = val.strip()
    return v

def get(url):
    req = urllib.request.Request(url, headers={'User-Agent': 'backupforge-build'})
    with urllib.request.urlopen(req, timeout=120) as r:
        return r.read()

def fetch_linux(v):
    """Test helper for the Linux CI: the official linux_amd64 build, verified against the release's SHA256SUMS only."""
    import bz2
    name = f"restic_{v['version']}_linux_amd64.bz2"
    url = v['url'].rsplit('/', 1)[0] + '/' + name
    data = get(url)
    sha = hashlib.sha256(data).hexdigest()
    sums = {l.split()[1].lstrip('*'): l.split()[0].lower() for l in get(v['sums_url']).decode().splitlines() if len(l.split()) == 2}
    if sums.get(name) != sha:
        print(f'ERROR: {name} sha256 {sha} does not match SHA256SUMS ({sums.get(name)})')
        return 1
    out = os.path.join(ROOT, 'third_party', 'restic', 'restic')
    with open(out, 'wb') as f:
        f.write(bz2.decompress(data))
    os.chmod(out, 0o755)
    print(f'written {out} (verified against SHA256SUMS)')
    return 0

def main():
    if '--linux' in sys.argv:
        return fetch_linux(read_version())
    out = os.path.join(ROOT, 'third_party', 'restic', 'restic.exe')
    if '--out' in sys.argv:
        out = sys.argv[sys.argv.index('--out') + 1]
    v = read_version()
    data = get(v['url'])
    zip_sha = hashlib.sha256(data).hexdigest()
    sums = get(v['sums_url']).decode('utf-8')
    official = {}
    for line in sums.splitlines():
        parts = line.split()
        if len(parts) == 2:
            official[parts[1].lstrip('*')] = parts[0].lower()
    ok = True
    if official.get(v['zip']) != zip_sha:
        print(f"ERROR: {v['zip']} sha256 {zip_sha} does not match SHA256SUMS ({official.get(v['zip'])})")
        ok = False
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        names = [n for n in z.namelist() if n.endswith('.exe')]
        if len(names) != 1:
            print(f'ERROR: expected exactly one exe in the zip, got {names}')
            return 1
        exe = z.read(names[0])
    exe_sha = hashlib.sha256(exe).hexdigest()
    print(f"restic {v['version']}: zip_sha256={zip_sha} exe_sha256={exe_sha}")
    for key, actual in (('zip_sha256', zip_sha), ('exe_sha256', exe_sha)):
        pin = v.get(key, 'unpinned')
        if pin == 'unpinned':
            print(f'ERROR: {key} is not pinned in third_party/restic/VERSION (computed {actual})')
            ok = False
        elif pin.lower() != actual:
            print(f'ERROR: {key} mismatch: pinned {pin}, got {actual}')
            ok = False
    if not ok:
        return 1
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, 'wb') as f:
        f.write(exe)
    print(f'written {out}')
    return 0

sys.exit(main())
