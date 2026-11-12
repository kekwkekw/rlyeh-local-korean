"""Extract text-bearing objects; keep the original bundle for every excluded object.
The decrypt/structured traversal primitives come from the user's monmusu extractor.
"""
import concurrent.futures as cf
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import re
import sys
import time
import UnityPy
from text_catalog import current_catalog

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT/'external/monmusu/extractor'))
from core import decrypt, iter_strings, book_rows
JP = re.compile(r'[\u3040-\u30ff\u3400-\u9fff]')

def clean(value):
    if isinstance(value, dict):
        if 'hiddenChars' in value:
            return ''.join(chr(c) for c in value['hiddenChars'])
        if 'hiddenValue' in value and 'currentCryptoKey' in value and isinstance(value['hiddenValue'], int):
            return (value['hiddenValue'] - value['currentCryptoKey']) ^ value['currentCryptoKey']
        return {k:clean(v) for k,v in value.items()}
    if isinstance(value,list): return [clean(v) for v in value]
    return value

def json_default(value):
    if isinstance(value,bytes): return {'$bytes':value.hex()}
    raise TypeError(type(value).__name__)

def process(path):
    destination = ROOT/'data/extracted'/path.stem
    complete = destination/'complete.json'
    if complete.exists(): return json.loads(complete.read_text(encoding='utf-8'))
    destination.mkdir(parents=True,exist_ok=True)
    results=[]; errors=[]; records=[]; scenarios=[]
    try:
        raw=path.read_bytes()
        env=UnityPy.load(raw if raw.startswith(b'Unity') else decrypt(raw))
        containers={}
        for key,ptr in env.container.items(): containers.setdefault(ptr.path_id,[]).append(key)
        for obj in env.objects:
            source_id=path.stem+':'+str(obj.path_id)
            info={'source_id':source_id,'raw_file':str(path.relative_to(ROOT)), 'object_path_id':obj.path_id,'type':obj.type.name,'containers':containers.get(obj.path_id,[])}
            if obj.type.name not in ('MonoBehaviour','TextAsset','MonoScript'):
                results.append(dict(info,category='excluded/media_or_structure',reason='Unity object type; original bytes retained in raw bundle')); continue
            try:
                tree=obj.read_typetree()
                category='kept/other_text'
                if 'importGridList' in tree: category='kept/scenario_books'
                elif any('mdf_output' in s.lower() or 'masterdata' in s.lower() for s in info['containers']): category='kept/masterdata'
                if obj.type.name=='MonoScript': category='excluded/script_metadata'
                raw_tree=json.dumps(tree,ensure_ascii=True,separators=(',',':'),default=json_default).encode()
                tree_path=destination/(str(obj.path_id)+'.json.gz')
                with gzip.open(tree_path,'wb',compresslevel=1) as f:f.write(raw_tree)
                decoded=clean(tree)
                if 'importGridList' in tree:
                    for row in book_rows(tree,source_id):
                        scenarios.append(row)
                for pointer,text in iter_strings(decoded):
                    if JP.search(text) and category!='excluded/script_metadata' and not any(0xD800<=ord(c)<=0xDFFF or ord(c)==0 for c in text):
                        records.append(dict(info,field_path=pointer,ja=text,category=category,record_id=hashlib.sha256((source_id+pointer+text).encode()).hexdigest()[:24],tree_file=str(tree_path.relative_to(ROOT))))
                results.append(dict(info,category=category,tree_file=str(tree_path.relative_to(ROOT))))
            except Exception as ex:
                errors.append(dict(info,error=str(ex)))
                results.append(dict(info,category='excluded/unreadable',reason=str(ex)))
    except Exception as ex: errors.append({'file':str(path),'error':str(ex)})
    for name,rows in [('objects',results),('source-records',records),('scenario-rows',scenarios),('errors',errors)]:
        (destination/(name+'.jsonl')).write_text(''.join(json.dumps(r,ensure_ascii=False,default=json_default)+'\n' for r in rows),encoding='utf-8')
    summary={'file':path.name,'objects':len(results),'source_records':len(records),'scenario_rows':len(scenarios),'errors':len(errors)}
    # Failed files stay retryable; their errors are preserved.
    if not errors: complete.write_text(json.dumps(summary),encoding='utf-8')
    return summary

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--workers',type=int,default=2); args=parser.parse_args()
    catalog, expected = current_catalog(ROOT/'data')
    raw = ROOT/'data/raw/assets'
    files = [raw/name for name in sorted(expected)]
    missing = [path.name for path in files if not path.is_file() or path.stat().st_size != expected[path.name]]
    if missing:
        raise ValueError(f'Acquire catalog {catalog} first: {len(missing)} missing or incomplete bundles (first: {missing[0]})')
    (ROOT/'data/extracted').mkdir(exist_ok=True)
    totals={'files':0,'objects':0,'source_records':0,'scenario_rows':0,'errors':0}
    last=time.monotonic()
    with cf.ProcessPoolExecutor(max_workers=args.workers) as pool:
        for row in pool.map(process,files,chunksize=12):
            totals['files']+=1
            for key in totals:
                if key!='files':totals[key]+=row[key]
            if time.monotonic()-last>20:print(json.dumps(totals),flush=True);last=time.monotonic()
    (ROOT/'data/extraction-summary.json').write_text(json.dumps(dict(catalog=catalog, **totals),indent=2),encoding='utf-8')
    print(json.dumps(totals),flush=True)
    if totals['errors']:
        raise RuntimeError('Extraction failed; see the bundle error records')

if __name__=='__main__': main()
