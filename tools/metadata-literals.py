"""Unity IL2CPP metadata literals, including v35 offsets and v38+ section counts.
Layout reference: official SamboyCoding/Cpp2IL LibCpp2IL/Metadata classes.
"""
import hashlib,json,re,struct
from pathlib import Path
ROOT=Path(__file__).resolve().parent.parent
import argparse
parser=argparse.ArgumentParser(description='Extract literals from your own game metadata; no game files are distributed.')
parser.add_argument('metadata', type=Path)
path=parser.parse_args().metadata
raw=path.read_bytes()
magic,version=struct.unpack_from('<2I',raw)
if magic!=0xFAB11BAF:raise ValueError('Not IL2CPP metadata')
section_size=12 if version>=38 else 8
offset,size=struct.unpack_from('<2I',raw,8)
data_offset,data_size=struct.unpack_from('<2I',raw,8+section_size)
if offset+size>len(raw) or data_offset+data_size>len(raw):raise ValueError('Invalid ranges')
result=[]
if version>=35:
    indexes=struct.unpack_from('<'+'I'*(size//4),raw,offset)
    if list(indexes)!=sorted(indexes):raise ValueError('Nonmonotonic literal offsets')
    for i,index in enumerate(indexes):
        end=indexes[i+1] if i+1<len(indexes) else data_size
        if not 0<=index<=end<=data_size:raise ValueError('Invalid literal bounds')
        result.append({'index':i,'offset':data_offset+index,'byte_length':end-index,'text':raw[data_offset+index:data_offset+end].decode('utf-8')})
else:
    for i in range(size//8):
        length,index=struct.unpack_from('<2I',raw,offset+i*8)
        result.append({'index':i,'offset':data_offset+index,'byte_length':length,'text':raw[data_offset+index:data_offset+index+length].decode('utf-8')})
output=ROOT/'data/local';output.mkdir(exist_ok=True)
(output/'metadata-literals.jsonl').write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in result),encoding='utf-8')
summary={'version':version,'source':str(path),'sha256':hashlib.sha256(raw).hexdigest(),'literals':len(result),'japanese_candidates':sum(bool(re.search(r'[\u3040-\u30ff\u3400-\u9fff]',r['text'])) for r in result)}
(output/'metadata-summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
print(json.dumps(summary))
