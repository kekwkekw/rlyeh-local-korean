"""Build an offline installer archive from explicit local inputs. Never installs or downloads."""
from pathlib import Path
import argparse, hashlib, json, shutil, zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = '0.1.0'
DATE = (2026, 11, 13, 0, 0, 0)
SOURCE_ZIPS = ['hdTrans-0.9.5-source.zip', 'BepInEx-be.785-source.zip',
               'Il2CppInterop-1.5.3-rlyeh-source.zip', 'UnityDoorstop-4.5.0-source.zip',
               'Dobby-1.0.5-source.zip', 'NotoSerifCJKkr-font-source.zip']
ROOT_FILES = ['.gitignore','.gitattributes','LICENSE','README.md','SOURCE_CODE.md','THIRD_PARTY.md','build.ps1','inputs.lock.json']
DATA_FILES = ['canonical/glossary.json','exports/exact.json','exports/manifest.json','exports/names.txt',
              'exports/text-catalog.json','exports/userdict.txt','text/compositions.json','text/observed-ui.json','text/reviewed.json']
DENIED = {'.git','bin','obj','__pycache__','_glossary_runs','cache','evidence'}

def sha(path):
    with path.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest()

def source_files():
    files = [ROOT / f for f in ROOT_FILES] + [ROOT/'data'/f for f in DATA_FILES]
    for folder in ('runtime','tests','tools','licenses','docs','installer'):
        files += [p for p in (ROOT/folder).rglob('*') if p.is_file() and not (set(p.relative_to(ROOT).parts) & DENIED)
                  and p.suffix.lower() not in ('.dll','.pdb','.exe','.pyc','.log')]
    for p in files:
        if not p.is_file() or p.is_symlink(): raise ValueError('Missing/linked source: '+str(p))
    return sorted(set(files))

def archive(path, entries):
    with zipfile.ZipFile(path,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for name,p in sorted(entries):
            info=zipfile.ZipInfo(name,DATE)
            info.compress_type=zipfile.ZIP_DEFLATED
            info.external_attr=0o100644 << 16
            z.writestr(info,p.read_bytes())

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('bepinex-root','interop-build','hdtrans-zip','font-root','inputs-dir','output-dir'):
        parser.add_argument('--'+name,type=Path,required=True)
    args=parser.parse_args()
    out=args.output_dir.resolve()
    if out.exists(): raise ValueError('Use a new output directory: '+str(out))
    lock=json.loads((ROOT/'inputs.lock.json').read_text(encoding='utf-8'))
    checked={}
    def verify(key,path):
        expected=lock['files'][key]['sha256']
        actual=sha(path)
        if actual!=expected: raise ValueError('Input differs from lock: '+key)
        checked[key]=actual
    verify('hdTrans-0.9.5-windows.zip',args.hdtrans_zip)
    for name in SOURCE_ZIPS: verify(name,args.inputs_dir/name)
    verify('notoserifcjkkr-bold.bundle',args.font_root/'notoserifcjkkr-bold.bundle')
    for name,sub in [('Il2CppInterop.Runtime.dll','Il2CppInterop.Runtime/net6.0'),('Il2CppInterop.Common.dll','Il2CppInterop.Common/netstandard2.0')]:
        verify(name,args.interop_build/sub/name)
    # Exact stock files lock the original loader and embedded runtime, not a user's game installation.
    stock=[]
    for folder in ('BepInEx/core','dotnet'):
        stock += [p for p in (args.bepinex_root/folder).rglob('*') if p.is_file()]
    stock += [args.bepinex_root/n for n in ('.doorstop_version','doorstop_config.ini','winhttp.dll')]
    stock_manifest={p.relative_to(args.bepinex_root).as_posix():sha(p) for p in stock}
    if stock_manifest!=lock['bepinex_files']: raise ValueError('Stock BepInEx inputs changed')
    files=source_files()
    out.mkdir(parents=True)
    package=out/f'Rlyeh-Local-Korean-{VERSION}-hdTrans-0.9.5'
    payload=package/'payload'
    def copy(src,relative):
        dest=package/relative
        dest.parent.mkdir(parents=True,exist_ok=True)
        shutil.copyfile(src,dest)
    for path in stock: copy(path,'payload/'+path.relative_to(args.bepinex_root).as_posix())
    for name,sub in [('Il2CppInterop.Runtime.dll','Il2CppInterop.Runtime/net6.0'),('Il2CppInterop.Common.dll','Il2CppInterop.Common/netstandard2.0')]:
        copy(args.interop_build/sub/name,'payload/BepInEx/core/'+name)
    plugin='payload/BepInEx/plugins/Rlyeh.Localizer/'
    for name in ('Rlyeh.Localizer.dll','Rlyeh.Localizer.Core.dll'):
        copy(ROOT/'runtime/Rlyeh.Localizer/bin/Release/net6.0'/name,plugin+name)
    for name in ('exact.json','manifest.json','names.txt','text-catalog.json','userdict.txt'):
        copy(ROOT/'data/exports'/name,plugin+'glossary/'+name)
    for name in ('notoserifcjkkr-bold.bundle','OFL.txt'): copy(args.font_root/name,plugin+'fonts/'+name)
    with zipfile.ZipFile(args.hdtrans_zip) as z:
        for name in ('hdTransEngine.dll','hdtrans-data/dicts.hdsnap'):
            data=z.read(name)
            if hashlib.sha256(data).hexdigest()!=lock['files'][name]['sha256']: raise ValueError('hdTrans member mismatch')
            dest=package/plugin/'native'/name
            dest.parent.mkdir(parents=True,exist_ok=True)
            dest.write_bytes(data)
    for name in ('README.md','SOURCE_CODE.md','THIRD_PARTY.md','LICENSE','inputs.lock.json'): copy(ROOT/name,name)
    for folder in ('licenses','docs'):
        for p in (ROOT/folder).rglob('*'):
            if p.is_file(): copy(p,p.relative_to(ROOT).as_posix())
    for p in (ROOT/'installer').iterdir():
        if p.is_file(): copy(p,p.name)
    for name in SOURCE_ZIPS: copy(args.inputs_dir/name,'sources/'+name)
    source_name=f'rlyeh-localizer-{VERSION}-source.zip'
    archive(out/source_name,[(p.relative_to(ROOT).as_posix(),p) for p in files])
    copy(out/source_name,'sources/'+source_name)
    contents=[{'path':p.relative_to(package).as_posix(),'size':p.stat().st_size,'sha256':sha(p)}
              for p in sorted(package.rglob('*')) if p.is_file()]
    manifest={'schema':1,'version':VERSION,'preparedDate':'2026-11-13','hdTrans':'0.9.5',
              'status':'stable','files':contents}
    (package/'package.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    archive(out/(package.name+'.zip'),[(package.name+'/'+p.relative_to(package).as_posix(),p) for p in package.rglob('*') if p.is_file()])
    zips=sorted(out.glob('*.zip'))
    (out/'SHA256SUMS.txt').write_text(''.join(f'{sha(p)}  {p.name}\n' for p in zips),encoding='ascii')
    (out/'build-result.json').write_text(json.dumps({'version':VERSION,'payloadFiles':sum(f['path'].startswith('payload/') for f in contents),
        'sourceFiles':len(files),'packageFiles':len(contents)+1,'artifacts':[{'name':p.name,'size':p.stat().st_size,'sha256':sha(p)} for p in zips]},indent=2),encoding='utf-8')
    print((out/'build-result.json').read_text())

if __name__=='__main__': main()
