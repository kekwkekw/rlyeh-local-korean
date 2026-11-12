"""Reviewed UI/Master text, separate from the hdTrans term glossary.

scan -> inventory.json; import a review JSON -> reviewed.json; export only current sources.
No translation endpoint, online service, or game writes.
"""
import argparse
from collections import Counter, defaultdict
import gzip
import hashlib
import json
from pathlib import Path
import re
import subprocess
import string

ROOT = Path(__file__).resolve().parent.parent
TEXT = ROOT / 'data/text'
# CJK headings in angle brackets are visible prose, while ASCII tags are renderer controls.
CONTROLS = re.compile(r'<(?=[A-Za-z/#?!])[^>]+>|(?<!\{)\{\d+(?:,[+-]?\d+)?(?::[^{}]*)?\}(?!\})|\[skilllevel,\d+,\d+,|\[(?:\d+(?:,\d+)+|duration|(?:SEC|MAG),\d+,\d+)\]|\]')
SLOTS = re.compile(r'\{\{([a-z][a-z0-9_]*)\}\}')
NUMBERS = re.compile(r'[0-9０-９]+(?:[.,．][0-9０-９]+)*')
MASTER_PARAMETERS = r'\[(?:\d+(?:,\d+)+|duration|(?:SEC|MAG),\d+,\d+)\]'
MASTER_NUMBER_STREAM = re.compile(MASTER_PARAMETERS + '|' + NUMBERS.pattern)
UI_FIELDS = {'m_text', 'm_Text', 'm_format', 'm_textFormat', 'm_StringArgument'}
SKILL_TYPES = {'over_skill_data': 'OverSkillMasterData', 'auto_skill_data': 'AutoSkillMasterData',
               'passive_skill_data': 'AbilityMasterData', 'potential_skill_data': 'AbilityMasterData',
               'skin_skill_data': 'SkinSkillMasterData'}


def hash_text(s):
    return hashlib.sha256(s.encode('utf-8')).hexdigest()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + '.tmp')
    tmp.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    tmp.replace(path)


def read(path, default):
    return json.loads(path.read_text(encoding='utf-8-sig')) if path.exists() else default


def current_catalog(data):
    raw = (data/'catalog-windows_r18.json').read_bytes()
    files = {row['hash'] + row['path']: row['size'] for row in json.loads(raw.decode('utf-8-sig'))['data']}
    if not files or any(not re.fullmatch(r'[0-9a-fA-F]{64}\.bytes', name) for name in files):
        raise ValueError('Invalid Windows asset catalog')
    return hashlib.sha256(raw).hexdigest()[:16], files


def review_inventory():
    inventory = read(TEXT/'inventory.json', {})
    catalog, _ = current_catalog(ROOT/'data')
    if inventory.get('catalog') != catalog:
        raise ValueError('Run text-scan for the current Windows asset catalog first')
    observed = read(TEXT/'observed-ui.json', {})
    for row in observed.get('items', []) if observed.get('catalog') == catalog else []:
        source, context = row['source'], row['context']
        if not source.strip() or not context.strip() or row['id'] != hash_text(context + '\x1f' + source):
            raise ValueError('Invalid runtime UI observation')
        inventory['items'].append(dict(row, sourceHash=hash_text(source), scope='ui'))
    return inventory


def decode_id(value):
    if isinstance(value, dict) and isinstance(value.get('hiddenValue'), int):
        k = value['currentCryptoKey']
        return (value['hiddenValue'] - k) ^ k
    return value if isinstance(value, (str, int)) else None


def policy(table, field):
    if table in {'unit_emote_data', 'login_bonus_cell', 'profile_description_data', 'character_connection_data'} or field == 'flavorText':
        return 'hdtrans'
    if field in {'cv', 'illustrator', 'enemyTotalPower', 'sales_conditions'}:
        return 'inspect'
    if table in SKILL_TYPES and field == 'text':
        return 'skill-template'
    if table in {'mission_data', 'progress_reward_table_data'}:
        return 'repeated-text'
    if field.lower() in {'name', 'title', 'display_name', 'wallet_name'}:
        return 'name-or-title'
    return 'exact'


def extracted_records(extracted):
    # Stream matching records through ripgrep instead of reading every record file in Python.
    cmd = ['rg', '--no-ignore', '--no-filename', '-j', '8', '-g', 'source-records.jsonl',
           '-e', '"category": "kept/masterdata"', '-e', '"category": "kept/other_text"', str(extracted)]
    with subprocess.Popen(cmd, stdout=subprocess.PIPE) as proc:
        for line in proc.stdout:
            yield json.loads(line)
        if proc.wait() != 0:
            raise RuntimeError('Extraction search failed')


def scan():
    catalog, files = current_catalog(ROOT/'data')
    extracted = ROOT/'data/extracted'
    bundles = {Path(name).stem for name in files}
    missing = [bundle for bundle in sorted(bundles) if not (extracted/bundle/'complete.json').is_file()
               or not (extracted/bundle/'source-records.jsonl').is_file()]
    if missing:
        raise ValueError(f'Incomplete extraction for catalog {catalog}: {len(missing)} bundles (first: {missing[0]})')
    groups = {}; trees = {}; unique_ids = {}; counts = Counter()
    for r in extracted_records(extracted):
        if r['source_id'].split(':', 1)[0] not in bundles: continue
        field = r['field_path'].split('/')[-1]
        master = r['category'] == 'kept/masterdata'
        containers = r['containers']
        if not master and not (field in UI_FIELDS or (field in {'Name', 'Description'} and any('minigame' in c.lower() for c in containers))):
            continue
        source = r['ja']; table = Path(containers[0]).stem if containers else ''
        row_id = None; relative = r['field_path']; stable = False
        if master and containers:
            match = re.match(r'^/records/(\d+)(/.*)$', relative)
            if match:
                treefile = r['tree_file']
                if treefile not in trees:
                    trees[treefile] = json.loads(gzip.decompress((ROOT/treefile).read_bytes()))
                    unique_ids[treefile] = Counter(decode_id(v.get('id', v.get('Id'))) for v in trees[treefile]['records'])
                row = trees[treefile]['records'][int(match[1])]
                row_id = decode_id(row.get('id', row.get('Id')))
                if row_id is not None and unique_ids[treefile][row_id] == 1:
                    relative = match[2]; stable = True
        location = f"{containers[0]}:{row_id}:{relative}" if stable else r['source_id'] + r['field_path']
        context = 'ui'
        if table in SKILL_TYPES and field == 'text' and row_id is not None:
            context = f'master:{SKILL_TYPES[table]}:{row_id}:text'
        # UI strings can share a review, but keep every source location as evidence.
        uid = hash_text(location if master else 'ui\x1f' + source)[:24]
        entry = {'id': uid, 'source': source, 'sourceHash': hash_text(source), 'scope': context,
                 'category': 'master' if master else 'ui', 'table': table, 'rowId': row_id,
                 'field': relative, 'stableLocation': stable, 'policy': policy(table, field) if master else 'exact', 'evidence': []}
        if uid in groups and groups[uid]['source'] != source:
            raise ValueError('Conflicting source location: ' + location)
        entry = groups.setdefault(uid, entry)
        entry['evidence'].append({k: r[k] for k in ('source_id', 'field_path', 'containers', 'tree_file')})
        counts[entry['category']] += 1
    items = sorted(groups.values(), key=lambda r: (r['category'], r['table'], r['id']))
    inventory = {'version': 1, 'catalog': catalog, 'sourceCounts': dict(counts), 'units': len(items),
                 'policyCounts': dict(Counter(r['policy'] for r in items)), 'items': items}
    save(TEXT/'inventory.json', inventory)
    print(json.dumps({k:v for k,v in inventory.items() if k != 'items'}, ensure_ascii=False))
    return inventory


def inventory_delta(previous, current):
    before = {r['id']: r for r in previous['items']}
    after = {r['id']: r for r in current['items']}
    return {
        'beforeCatalog': previous.get('catalog'), 'currentCatalog': current.get('catalog'),
        'added': [after[key] for key in sorted(after.keys() - before.keys())],
        'changed': [{'before': before[key], 'current': after[key]} for key in sorted(before.keys() & after.keys())
                    if before[key]['sourceHash'] != after[key]['sourceHash']],
        'removed': [before[key] for key in sorted(before.keys() - after.keys())],
    }


def validate(entry):
    for key in ('id', 'source', 'target', 'scope', 'reviewer'):
        if not isinstance(entry.get(key), str) or not entry[key].strip() or '\0' in entry[key]:
            raise ValueError('Invalid ' + key)
    if len(entry['source']) > 12000 or len(entry['target']) > 16000:
        raise ValueError('Text too long')
    if entry['sourceHash'] != hash_text(entry['source']):
        raise ValueError('Source hash mismatch')
    if not (entry['scope'] == 'ui' or entry['scope'].startswith(('ui:', 'master:'))):
        raise ValueError('Unsupported scope')
    source, target = entry['source'], entry['target']
    if CONTROLS.findall(source) != CONTROLS.findall(target):
        raise ValueError('Display/control tokens changed: ' + entry['id'])
    def nums(s): return NUMBERS.findall(CONTROLS.sub('', SLOTS.sub('', s)))
    if nums(source) != nums(target):
        raise ValueError('Literal numbers changed: ' + entry['id'])
    if entry['scope'].startswith('master:'):
        def numeric_stream(text):
            surface = CONTROLS.sub(lambda m: m[0] if re.fullmatch(MASTER_PARAMETERS,m[0]) else '',text)
            return MASTER_NUMBER_STREAM.findall(surface)
        if numeric_stream(source) != numeric_stream(target):
            raise ValueError('Native numeric/parameter order changed: '+entry['id'])
    kind = entry.get('kind', 'exact'); slots = entry.get('slots', {})
    if kind == 'exact':
        if slots: raise ValueError('Slots on exact text')
    elif kind == 'numeric' and entry['scope'].startswith('ui'):
        a, b = SLOTS.findall(source), SLOTS.findall(target)
        if not a or Counter(a) != Counter(b) or set(a) != set(slots) or any(v not in ('integer', 'number') for v in slots.values()):
            raise ValueError('Invalid numeric placeholders')
        pattern = numeric_pattern(entry)
        if not entry.get('examples'): raise ValueError('Numeric template requires source examples')
        if any(not pattern.fullmatch(example) for example in entry['examples']):
            raise ValueError('Template does not match evidence examples')
    else:
        raise ValueError('Unsupported kind')


def numeric_pattern(entry):
    parts = []; seen = set(); cursor = 0
    for m in SLOTS.finditer(entry['source']):
        parts.append(re.escape(entry['source'][cursor:m.start()])); name = m[1]
        if name in seen: parts.append('(?P=' + name + ')')
        else:
            capture = r'[0-9０-９]+' if entry['slots'][name] == 'integer' else r'[+-]?[0-9０-９]+(?:[.,．][0-9０-９]+)*'
            parts.append('(?P<' + name + '>' + capture + ')'); seen.add(name)
        cursor = m.end()
    parts.append(re.escape(entry['source'][cursor:]))
    return re.compile(''.join(parts))


def current_entries(reviewed, inventory):
    lookup = {r['id']: r for r in inventory['items']}; active = []; stale = []; exact = {}
    for entry in reviewed:
        validate(entry)
        evidence = entry.get('evidence', [])
        if not evidence or any(e['id'] not in lookup or lookup[e['id']]['sourceHash'] != e['sourceHash'] for e in evidence):
            stale.append(entry['id']); continue
        current_sources = {lookup[e['id']]['source'] for e in evidence}
        if entry.get('kind', 'exact') == 'exact' and entry['source'] not in current_sources:
            raise ValueError('Exact source does not match evidence')
        if entry.get('kind') == 'numeric' and not set(entry['examples']).issubset(current_sources):
            raise ValueError('Numeric examples not found in current evidence')
        # Master scopes must agree with the extractor's known runtime adapter.
        if entry['scope'].startswith('master:') and any(lookup[e['id']]['scope'] != entry['scope'] for e in evidence):
            raise ValueError('Master scope does not match evidence')
        if entry.get('kind', 'exact') == 'exact':
            key = (entry['scope'], entry['source'])
            if key in exact and exact[key] != entry['target']: raise ValueError('Conflicting exact text')
            exact[key] = entry['target']
        active.append(entry)
    return active, stale


def compose_entries(active, recipes):
    """Expand reviewed UI formats + reviewed names offline, with no runtime pattern matching."""
    by_id = {r['id']: r for r in active}
    exact = {(r['scope'], r['source']): r['target'] for r in active if r.get('kind', 'exact') == 'exact'}
    generated = []; stale = []; seen = set()
    for recipe in recipes:
        rid = recipe['id']
        if not isinstance(rid, str) or not rid or rid in seen: raise ValueError('Invalid composition id')
        seen.add(rid)
        refs = [recipe['template']] + [a['entry'] for a in recipe['arguments'] if 'entry' in a]
        if any(ref not in by_id for ref in refs):
            stale.append(rid); continue
        template = by_id[recipe['template']]
        if any(by_id[ref]['scope'] != 'ui' or by_id[ref].get('kind', 'exact') != 'exact' for ref in refs):
            raise ValueError('Composition requires general UI exact entries')
        source_args = []; target_args = []
        for arg in recipe['arguments']:
            if set(arg) == {'entry'}:
                row = by_id[arg['entry']]
                source_args.append(row['source']); target_args.append(row['target'])
            elif set(arg) == {'number'} and isinstance(arg['number'], str) and re.fullmatch(r'[0-9]+',arg['number']):
                source_args.append(arg['number']); target_args.append(arg['number'])
            else: raise ValueError('Composition accepts only reviewed entries or literal integers')
        for text in (template['source'],template['target']):
            fields=[]
            for _, field, spec, conversion in string.Formatter().parse(text):
                if field is None: continue
                if not re.fullmatch(r'[0-9]+',field) or spec or conversion:
                    raise ValueError('Unsupported composition format')
                fields.append(int(field))
            if set(fields) != set(range(len(source_args))): raise ValueError('Composition arguments do not match format')
        source = template['source'].format(*source_args)
        target = template['target'].format(*target_args)
        key = ('ui',source)
        if key in exact:
            if exact[key] != target: raise ValueError('Conflicting composed text: '+rid)
            continue
        entry = dict(id='composed:'+rid,scope='ui',kind='exact',source=source,target=target,
                     sourceHash=hash_text(source),reviewer='Derived from reviewed entries: '+', '.join(refs),
                     evidence=[e for ref in refs for e in by_id[ref]['evidence']])
        if entry['id'] in by_id: raise ValueError('Composition id collides with review')
        validate(entry); exact[key]=target; generated.append(entry)
    return generated, stale


def export(reviewed=None, inventory=None):
    inventory = inventory if inventory is not None else review_inventory()
    reviewed = reviewed if reviewed is not None else read(TEXT/'reviewed.json', [])
    active, stale = current_entries(reviewed, inventory)
    generated, stale_compositions = compose_entries(active, read(TEXT/'compositions.json', []))
    keys = ('id', 'scope', 'source', 'target', 'sourceHash', 'reviewer', 'kind', 'slots')
    save(ROOT/'data/exports/text-catalog.json', {'version': 1, 'entries': [{k:r[k] for k in keys if k in r} for r in active+generated]})
    report = {'catalog': inventory.get('catalog'), 'reviewed': len(reviewed), 'active': len(active), 'stale': stale,
              'kinds': dict(Counter(r.get('kind', 'exact') for r in active)),
              'coveredUnits': len({e['id'] for r in active for e in r['evidence']}),
              'generated': len(generated), 'runtimeEntries': len(active)+len(generated),
              'staleCompositions': stale_compositions}
    save(TEXT/'status.json', report); print(json.dumps(report, ensure_ascii=False))


def import_review(path):
    reply = read(Path(path), None)
    if not reply or not reply.get('reviewer'): raise ValueError('Reviewer required')
    old = {r['id']:r for r in read(TEXT/'reviewed.json', [])}; seen = set()
    for row in reply['entries']:
        if row['id'] in seen: raise ValueError('Repeated review id')
        seen.add(row['id']); row = dict(row, reviewer=reply['reviewer'])
        validate(row); old[row['id']] = row
    proposed = sorted(old.values(), key=lambda r:r['id'])
    inventory = review_inventory()
    active, stale = current_entries(proposed, inventory)
    if set(stale) & seen: raise ValueError('Review uses stale evidence')
    compose_entries(active, read(TEXT/'compositions.json', []))
    save(TEXT/'reviewed.json', proposed)
    export(proposed, inventory)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('action', choices=['scan', 'export', 'import', 'status', 'diff']); parser.add_argument('file', nargs='?'); args = parser.parse_args()
    if args.action == 'scan': scan(); export()
    elif args.action == 'import': import_review(args.file)
    elif args.action == 'export': export()
    elif args.action == 'diff':
        if not args.file: parser.error('diff requires the previous inventory path')
        previous = read(Path(args.file), {})
        if not isinstance(previous.get('items'), list): parser.error('Previous inventory is missing or invalid')
        current = review_inventory()
        delta = inventory_delta(previous, read(TEXT/'inventory.json', {}))
        _, stale = current_entries(read(TEXT/'reviewed.json', []), current)
        print(json.dumps(dict(delta, staleReviews=stale), ensure_ascii=False, indent=2))
    else: print(json.dumps(read(TEXT/'status.json', {}), ensure_ascii=False, indent=2))
