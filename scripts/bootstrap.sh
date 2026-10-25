#!/usr/bin/env bash
set -euo pipefail
studio_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
studio_tools="${STUDIO_TOOLS_ROOT:-/workspace/.tools}"
export STUDIO_TOOLS_ROOT="$studio_tools"
mkdir -p "$studio_tools"
python3 - "$studio_root/global.json" "$studio_tools" <<'PY'
import hashlib, json, pathlib, platform, sys, tarfile, urllib.request
version = json.loads(pathlib.Path(sys.argv[1]).read_text())['sdk']['version']
root = pathlib.Path(sys.argv[2]); target = root / 'dotnet'
if (target / 'sdk' / version).is_dir():
    print('Pinned SDK already installed:', version)
    raise SystemExit(0)
architecture = {'x86_64':'x64','aarch64':'arm64'}.get(platform.machine())
if platform.system() != 'Linux' or architecture is None:
    raise SystemExit('Install the SDK pinned in global.json using the official installer on this platform.')
with urllib.request.urlopen('https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json', timeout=60) as response:
    metadata = json.load(response)
sdk = next(s for r in metadata['releases'] for s in r.get('sdks', []) if s['version'] == version)
artifact = next(f for f in sdk['files'] if f['rid'] == 'linux-' + architecture and f['name'].endswith('.tar.gz'))
archive = root / ('dotnet-' + version + '.tar.gz')
urllib.request.urlretrieve(artifact['url'], archive)
with archive.open('rb') as stream: digest = hashlib.file_digest(stream, 'sha512').hexdigest()
if digest.lower() != artifact['hash'].lower():
    archive.unlink()
    raise SystemExit('SDK checksum mismatch; installation stopped.')
target.mkdir(exist_ok=True)
with tarfile.open(archive) as stream: stream.extractall(target, filter='data')
archive.unlink()
print('Installed checksum-verified SDK:', version)
PY
source "$studio_root/scripts/env.sh"
cd "$studio_root"
dotnet restore YouTubeStudio.slnx --locked-mode
dotnet build YouTubeStudio.slnx --no-restore
