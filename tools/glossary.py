"""Evidence-backed glossary review and deterministic offline runtime exports."""
import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path
import re

ROOT=Path(__file__).resolve().parent.parent
DATA=ROOT/'data'
def digest(value): return hashlib.sha256(json.dumps(value,ensure_ascii=False,sort_keys=True).encode()).hexdigest()
def save(path,value):
    path.parent.mkdir(parents=True,exist_ok=True)
    temporary=path.with_suffix(path.suffix+'.tmp')
    temporary.write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8',newline='\n')
    temporary.replace(path)

def candidates():
    from text_catalog import current_catalog

    _, bundles = current_catalog(DATA)
    grouped=defaultdict(list)
    for observations in sorted((DATA/'local').glob('*-observations.jsonl')):
        for line in observations.read_text(encoding='utf-8').splitlines():
            row=json.loads(line)
            grouped[row['ja']].append(row)
    for name in sorted(bundles):
        path = DATA/'extracted'/Path(name).stem/'source-records.jsonl'
        for line in path.read_text(encoding='utf-8').splitlines():
            row=json.loads(line)
            field=row['field_path'].rsplit('/',1)[-1]
            if field.lower() in ('charaname','name','displayname','itemname','skillname','title') and len(row['ja'])<=60 and '\n' not in row['ja'] and not row['ja'].startswith(('#','//')):
                grouped[row['ja']].append(row)
    local=DATA/'local/metadata-literals.jsonl'
    if local.exists():
        for line in local.read_text(encoding='utf-8').splitlines():
            row=json.loads(line); ja=row['text']
            if len(ja)<=60 and re.search(r'[\u3040-\u30ff\u3400-\u9fff]',ja) and not re.search(r'[\n\r\x00<>]',ja):
                grouped[ja].append({'ja':ja,'record_id':'metadata:'+str(row['index']), 'source_id':'metadata:'+str(row['index']), 'field_path':'/literal','tree_file':'data/local/metadata-literals.jsonl','containers':['IL2CPP metadata literal']})
    items=[]
    for ja,rows in grouped.items():
        # Identical source strings keep all provenance; scope is decided during review.
        evidence=[{k:r[k] for k in ('record_id','source_id','field_path','tree_file','containers')} for r in rows]
        kind='character' if any(r['field_path']=='/charaName' for r in rows) else 'term'
        items.append({'candidate_id':digest(ja)[:20],'ja':ja,'suggested_type':kind,'evidence_count':len(evidence),'evidence':evidence,'evidence_hash':digest(evidence)})
    items.sort(key=lambda r:(r['suggested_type']!='character',r['ja']))
    pack={'schema_version':'1','pack_id':'current-'+digest(items)[:16],'items':items}
    old=DATA/'review/pack.json'
    if old.exists():
        previous=json.loads(old.read_text(encoding='utf-8'))
        save(DATA/'review/packs'/(previous['pack_id']+'.json'),previous)
    save(old,pack)
    save(DATA/'review/reply-template.json',{'schema_version':'1','pack_id':pack['pack_id'],'reviewer':'','items':[{'candidate_id':i['candidate_id'],'evidence_hash':i['evidence_hash'],'decision':'defer','ko':None,'entry_type':i['suggested_type'],'scope':'global','export_policy':'blocked','notes':''} for i in items]})
    print(json.dumps({'pack_id':pack['pack_id'],'candidates':len(items),'characters':sum(i['suggested_type']=='character' for i in items)}))

def import_review(path):
    pack=json.loads((DATA/'review/pack.json').read_text(encoding='utf-8'))
    reply=json.loads(Path(path).read_text(encoding='utf-8-sig'))
    if reply.get('pack_id')!=pack['pack_id'] or not reply.get('reviewer'): raise ValueError('Wrong pack or missing reviewer')
    lookup={i['candidate_id']:i for i in pack['items']}
    canonical_path=DATA/'canonical/glossary.json'
    canonical=json.loads(canonical_path.read_text(encoding='utf-8')) if canonical_path.exists() else []
    existing={i['entry_id']:i for i in canonical}
    seen=set()
    for answer in reply['items']:
        cid=answer['candidate_id']
        if cid in seen: raise ValueError('Repeated candidate id')
        seen.add(cid)
        source=lookup[cid]
        if answer.get('evidence_hash')!=source['evidence_hash']: raise ValueError('Evidence changed: '+cid)
        if answer['decision'] not in ('approve','defer','reject'): raise ValueError('Unknown review decision')
        if answer['decision']!='approve': continue
        ko=answer.get('ko','').strip()
        if not ko or '\t' in ko or '\n' in ko: raise ValueError('Invalid Korean spelling')
        entry={'entry_id':cid,'ja':source['ja'],'ko':ko,'entry_type':answer['entry_type'],'scope':answer['scope'],
               'review_status':'reviewed','reviewer':reply['reviewer'],'review_notes':answer.get('notes',''),
               'entry_revision':existing.get(cid,{}).get('entry_revision',0)+1,'export_policy':answer['export_policy'],
               'evidence_hash':source['evidence_hash'],'evidence_source_ids':[e['source_id'] for e in source['evidence']]}
        existing[cid]=entry
    updated=sorted(existing.values(),key=lambda r:r['entry_id'])
    # Validate the entire proposed result before touching the accepted glossary.
    content,manifest=render_exports(updated)
    save(canonical_path,updated)
    write_exports(content,manifest)

def render_exports(canonical):
    names=[]; userdict=[]; exact={}; spellings={}
    for row in canonical:
        if row['review_status']!='reviewed' or row['export_policy']=='blocked': continue
        ja,ko,scope=row['ja'],row['ko'],row['scope']
        if not isinstance(scope,str) or not scope or '\x1f' in scope: raise ValueError('Invalid glossary scope')
        if any(not isinstance(s,str) or not s or any(c in s for c in ('\x00','\t','\r','\n','\x1f')) for s in (ja,ko)): raise ValueError('Invalid glossary text')
        key=(str(scope),ja)
        if key in spellings and spellings[key]!=ko: raise ValueError('Conflicting translations: '+ja)
        spellings[key]=ko
        policy=row['export_policy']
        if policy in ('names','userdict') and scope!='global': raise ValueError('Scoped term cannot enter global hdTrans dictionaries')
        if policy=='names':
            if re.search(r'\s|[|/]',ja): raise ValueError('Unsupported names spelling: '+ja)
            kind={'character':'given','place':'place','organization':'org'}.get(row['entry_type'],'nick')
            names.append(f'{ja}\t{ko}\t{kind}')
            exact['speaker\x1f'+ja]=ko
        elif policy=='userdict': userdict.append(f'{ja}\t{ko}')
        elif policy=='exact_override':
            if not isinstance(scope,str):raise ValueError('Exact scope must be a scenario label or global')
            exact[ja if scope=='global' else scope+'\x1f'+ja]=ko
        else: raise ValueError('Unknown export policy')
    content={'names.txt':'\n'.join(sorted(set(names)))+'\n','userdict.txt':'\n'.join(sorted(set(userdict)))+'\n','exact.json':json.dumps(exact,ensure_ascii=False,sort_keys=True,indent=2)+'\n'}
    manifest={'status':'reviewed','canonical_revision':digest(canonical),'entries':len(canonical),'names':len(names),'userdict':len(userdict),'exact':len(exact),'files':{n:hashlib.sha256(v.encode()).hexdigest() for n,v in content.items()}}
    return content,manifest

def write_exports(content,manifest):
    target=DATA/'exports';target.mkdir(exist_ok=True)
    for name,value in content.items():
        temporary=target/(name+'.tmp')
        temporary.write_text(value,encoding='utf-8',newline='\n')
        temporary.replace(target/name)
    save(target/'manifest.json',manifest)
    print(json.dumps({k:manifest[k] for k in ('entries','names','userdict','exact')}))

def export():
    canonical=json.loads((DATA/'canonical/glossary.json').read_text(encoding='utf-8'))
    write_exports(*render_exports(canonical))

if __name__=='__main__':
    parser=argparse.ArgumentParser(); parser.add_argument('command',choices=['candidates','import','export']);parser.add_argument('path',nargs='?');args=parser.parse_args()
    if args.command=='candidates':candidates()
    elif args.command=='import':import_review(args.path)
    else:export()
