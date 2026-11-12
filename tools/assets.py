"""Current PC catalog acquisition. Raw bytes and manifest expectations remain separate."""
import argparse
import concurrent.futures as cf
import hashlib
import json
import os
from pathlib import Path
import threading
import time
import requests

ROOT = Path(__file__).resolve().parent.parent
local = threading.local()

def acquire(item, base, raw):
    if not hasattr(local, 'session'):
        local.session = requests.Session()
    name = item['hash'] + item['path']
    if '/' in name or '\\' in name or '..' in name:
        raise ValueError('Unsafe catalog path: ' + name)
    dest = raw / name
    result = dict(item, url=base + name, file=str(dest.relative_to(ROOT)))
    try:
        if dest.exists() and dest.stat().st_size == item['size']:
            payload_hash = hashlib.file_digest(dest.open('rb'), 'sha256').hexdigest()
            return dict(result, status='verified', actual_size=dest.stat().st_size, sha256=payload_hash)
        for attempt in range(4):
            try:
                part = dest.with_suffix('.partial')
                digest = hashlib.sha256()
                with local.session.get(base + name, stream=True, timeout=(15, 90)) as response:
                    response.raise_for_status()
                    with part.open('wb') as output:
                        for block in response.iter_content(256 * 1024):
                            output.write(block); digest.update(block)
                actual = part.stat().st_size
                part.replace(dest)
                return dict(result, status='verified' if actual == item['size'] else 'size_mismatch', actual_size=actual, sha256=digest.hexdigest())
            except (requests.RequestException, OSError):
                if attempt == 3: raise
                time.sleep(attempt + 1)
    except Exception as error:
        return dict(result, status='failed', error=str(error))

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--workers', type=int, default=12)
    parser.add_argument('--refresh', action='store_true', help='Fetch the current public PC catalog before resuming')
    args = parser.parse_args()
    catalog_path = ROOT / 'data/catalog-windows_r18.json'
    if args.refresh:
        response=requests.post('https://api.cthulhu-rog.net/api/asset_bundle/version',timeout=20)
        response.raise_for_status(); version=response.json()
        if version.get('status',{}).get('code')!=1: raise RuntimeError('Version API failed')
        asset_version=version['data']['version'].split('/')[-1]
        if not asset_version.isalnum(): raise ValueError('Unexpected asset version')
        catalog_response=requests.get(f'https://assets.cthulhu-rog.net/assetbundles/production/{asset_version}/windows_r18/ablist.json',timeout=30)
        catalog_response.raise_for_status(); current=catalog_response.json()
        if not isinstance(current.get('data'),list) or not current['data']:raise ValueError('Empty catalog')
        if catalog_path.exists():
            previous=catalog_path.read_bytes(); history=ROOT/'data/catalog-history';history.mkdir(exist_ok=True)
            (history/(hashlib.sha256(previous).hexdigest()+'.json')).write_bytes(previous)
        temporary=catalog_path.with_suffix('.tmp');temporary.write_bytes(catalog_response.content);temporary.replace(catalog_path)
        (ROOT/'data/asset-version.json').write_text(json.dumps(version,ensure_ascii=False,indent=2),encoding='utf-8')
    catalog = json.loads(catalog_path.read_text(encoding='utf-8-sig'))
    catalog_id = hashlib.sha256(catalog_path.read_bytes()).hexdigest()[:16]
    raw = ROOT / 'data/raw/assets'; raw.mkdir(parents=True, exist_ok=True)
    state_path = ROOT / f'data/acquisition-{catalog_id}.jsonl'
    state = {}
    if state_path.exists():
        for line in state_path.read_text(encoding='utf-8').splitlines():
            try:
                row = json.loads(line); state[row['hash'] + row['path']] = row
            except ValueError: pass
    items = list({a['hash']+a['path']:a for a in catalog['data']}.values())
    def verified(item):
        name = item['hash'] + item['path']
        path = raw / name
        return state.get(name, {}).get('status') == 'verified' and path.is_file() and path.stat().st_size == item['size']

    pending = [item for item in items if not verified(item)]
    # All entries are downloaded. Small bundles are first so glossary extraction can begin early.
    pending.sort(key=lambda a:a['size'])
    base = f"https://assets.cthulhu-rog.net/assetbundles/production/ver_{catalog['baseVersion']}/windows_r18/"
    print(json.dumps({'catalog':catalog_id,'total':len(items),'pending':len(pending),'bytes':sum(a['size'] for a in pending)}), flush=True)
    last = time.monotonic()
    with state_path.open('a', encoding='utf-8') as output, cf.ThreadPoolExecutor(max_workers=args.workers) as pool:
        jobs = {pool.submit(acquire, a, base, raw):a for a in pending}
        for n, future in enumerate(cf.as_completed(jobs), 1):
            row = future.result(); state[row['hash']+row['path']] = row
            output.write(json.dumps(row, ensure_ascii=False) + '\n'); output.flush()
            if time.monotonic() - last > 20:
                print(json.dumps({'done':n,'pending_total':len(pending),'last_status':row['status']}), flush=True); last=time.monotonic()
    summary = {'catalog':catalog_id, 'total':len(items), 'states': {s:sum(r['status']==s for r in state.values()) for s in ['verified','size_mismatch','failed']}}
    (ROOT/'data/acquisition-summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
    print(json.dumps(summary),flush=True)
    if summary['states']['verified'] != len(items):
        raise RuntimeError('Asset acquisition incomplete; retry before extracting')

if __name__ == '__main__': main()
