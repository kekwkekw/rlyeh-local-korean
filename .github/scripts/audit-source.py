"""Check the tracked public source tree for private inputs and binary files."""
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / 'tools'))
from package import DATA_FILES, ROOT_FILES

source_folders = {'docs', 'installer', 'licenses', 'runtime', 'tests', 'tools'}
allowed_suffixes = {'.cmd', '.cs', '.csproj', '.json', '.md', '.patch', '.ps1', '.py', '.txt'}
automation_files = {
    '.github/workflows/ci.yml',
    '.github/scripts/audit-source.py',
}
denied_parts = {'bin', 'obj', 'cache', 'evidence', 'external', 'interop', 'payload',
                'legacy_patch', '__pycache__', '_glossary_runs'}
private_patterns = {
    'personal filesystem path': re.compile(
        r'(?i)[A-Z]:[\\/]+(?:Users|Documents and Settings)[\\/]|/(?:home|Users)/[^\s/]+/'
    ),
    'private key or GitHub token': re.compile(
        r'-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----|'
        r'\bgh[pousr]_[A-Za-z0-9]{36,}\b|\bgithub_pat_[A-Za-z0-9_]{60,}\b'
    ),
}

index = subprocess.check_output(['git', 'ls-files', '--stage', '-z'], cwd=root)
paths = set()
errors = []
for entry in index.decode('utf-8').split('\0'):
    if not entry:
        continue
    metadata, name = entry.split('\t', 1)
    mode, _, stage = metadata.split()
    path = PurePosixPath(name)
    if mode not in {'100644', '100755'} or stage != '0':
        errors.append(f'{name}: expected a regular tracked file')
        continue
    paths.add(name)
    allowed = (
        name in ROOT_FILES
        or name in automation_files
        or (path.parts[0] == 'data' and path.as_posix()[5:] in DATA_FILES)
        or (path.parts[0] in source_folders and path.suffix in allowed_suffixes)
    )
    if not allowed or set(part.lower() for part in path.parts) & denied_parts:
        errors.append(f'{name}: outside the source inventory')
        continue
    try:
        content = (root / name).read_text(encoding='utf-8-sig')
    except UnicodeDecodeError:
        errors.append(f'{name}: expected UTF-8 source or data')
        continue
    if '\0' in content:
        errors.append(f'{name}: binary content')
    for label, pattern in private_patterns.items():
        if pattern.search(content):
            errors.append(f'{name}: {label}')

expected = set(ROOT_FILES) | {f'data/{name}' for name in DATA_FILES}
errors.extend(f'{name}: missing required source' for name in sorted(expected - paths))
if errors:
    sys.exit('\n'.join(errors))
print(f'Checked {len(paths)} tracked source files.')
