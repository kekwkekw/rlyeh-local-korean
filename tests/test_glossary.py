"""Validate operator imports against isolated data, never the installed glossary."""
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import uuid
import unittest

spec = importlib.util.spec_from_file_location('glossary', Path(__file__).parents[1]/'tools/glossary.py')
g = importlib.util.module_from_spec(spec)
spec.loader.exec_module(g)

class ReviewTests(unittest.TestCase):
    def setUp(self):
        self.test_root = (Path(__file__).parent/'_glossary_runs').resolve()
        self.test_root.mkdir(exist_ok=True)
        self.temp = self.test_root/uuid.uuid4().hex
        self.temp.mkdir()
        self.original_data = g.DATA
        g.DATA = self.temp
        g.save(g.DATA/'review/pack.json', {'pack_id':'test-current', 'items':[
            {'candidate_id':'sample','ja':'テスト','evidence_hash':'source-v1','evidence':[{'source_id':'master:1'}]}
        ]})
        self.canonical = g.DATA/'canonical/glossary.json'
        g.save(self.canonical, [])
        self.reply = {'pack_id':'test-current','reviewer':'isolated test','items':[{
            'candidate_id':'sample','evidence_hash':'source-v1','decision':'approve',
            'ko':'테스트','entry_type':'term','scope':'ui','export_policy':'exact_override'
        }]}

    def tearDown(self):
        g.DATA = self.original_data
        assert self.temp.resolve().parent == self.test_root
        shutil.rmtree(self.temp)

    def apply(self):
        p = g.DATA/'reply.json'
        g.save(p,self.reply)
        g.import_review(p)

    def test_roundtrip_scoped_exact_and_file_hashes(self):
        self.apply()
        exact = json.loads((g.DATA/'exports/exact.json').read_text(encoding='utf-8'))
        self.assertEqual(exact, {'ui\x1fテスト':'테스트'})
        self.assertEqual((g.DATA/'exports/names.txt').read_text(encoding='utf-8'), '\n')
        manifest = json.loads((g.DATA/'exports/manifest.json').read_text(encoding='utf-8'))
        for name, expected in manifest['files'].items():
            self.assertEqual(hashlib.sha256((g.DATA/'exports'/name).read_bytes()).hexdigest(), expected)

    def test_invalid_export_preserves_accepted_files(self):
        self.apply()
        paths = [self.canonical, *sorted((g.DATA/'exports').glob('*'))]
        before = {p:p.read_bytes() for p in paths}
        self.reply['items'][0]['export_policy'] = 'names'  # UI-scoped name cannot be global.
        with self.assertRaises(ValueError): self.apply()
        self.assertEqual({p:p.read_bytes() for p in paths}, before)

    def test_stale_review_and_evidence_rejected(self):
        before = self.canonical.read_bytes()
        self.reply['pack_id'] = 'old'
        with self.assertRaises(ValueError): self.apply()
        self.reply['pack_id'] = 'test-current'
        self.reply['items'][0]['evidence_hash'] = 'old'
        with self.assertRaises(ValueError): self.apply()
        self.assertEqual(self.canonical.read_bytes(), before)

    def test_same_scope_conflict_rejected(self):
        row={'ja':'テスト','ko':'테스트','scope':'ui','review_status':'reviewed','export_policy':'exact_override'}
        with self.assertRaises(ValueError):
            g.render_exports([row,{**row,'ko':'다른 표기'}])

if __name__ == '__main__': unittest.main()
