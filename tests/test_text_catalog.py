import copy
import gzip
import importlib.util
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('text_catalog', Path(__file__).resolve().parents[1]/'tools/text_catalog.py')
catalog = importlib.util.module_from_spec(spec); spec.loader.exec_module(catalog)


class TextCatalogChecks(unittest.TestCase):
    def entry(self, source='攻撃力+[0,0,0]%', target='공격력 +[0,0,0]%'):
        return dict(id='test', source=source, target=target, scope='ui', kind='exact',
                    sourceHash=catalog.hash_text(source), reviewer='test', evidence=[dict(id='source', sourceHash=catalog.hash_text(source))])

    def test_multiline_control_and_link_preservation(self):
        source = '[skilllevel,1,3,<link="毒">毒</link>を3秒付与]\r\n[duration]秒間攻撃力+[0,0,0]%'
        target = '[skilllevel,1,3,<link="毒">독</link>을3초 부여]\r\n[duration]초 동안 공격력 +[0,0,0]%'
        catalog.validate(self.entry(source, target))
        for changed in [target.replace('"毒"', '"독"'), target.replace('1,3,', '1,2,'), target.replace('3초', '4초'), target.replace('[0,0,0]', '[0,1,0]')]:
            with self.assertRaises(ValueError): catalog.validate(self.entry(source, changed))

    def test_changed_source_is_not_exported(self):
        e = self.entry(); old = dict(id='source', source=e['source'], sourceHash=e['sourceHash'], scope='ui')
        self.assertEqual(len(catalog.current_entries([e], dict(items=[old]))[0]), 1)
        changed = dict(old, source='攻撃力+[0,1,0]%', sourceHash=catalog.hash_text('攻撃力+[0,1,0]%'))
        active, stale = catalog.current_entries([e], dict(items=[changed]))
        self.assertEqual(active, []); self.assertEqual(stale, ['test'])

    def test_composite_format_tokens_survive(self):
        catalog.validate(self.entry('{0}文字まで入力可能', '{0}자까지 입력 가능'))
        with self.assertRaises(ValueError): catalog.validate(self.entry('{0}文字まで入力可能', '0자까지 입력 가능'))
        with self.assertRaises(ValueError): catalog.validate(self.entry('所持{0:N0}', '보유 {0}'))
        with self.assertRaises(ValueError): catalog.validate(self.entry('3秒間20ダメージ', '20초 동안 3 피해'))

    def test_master_literal_parameter_interleaving_survives_expansion(self):
        e=dict(self.entry('8秒間被ダメージ+[0,0,0]%','8초 동안 받는 피해 +[0,0,0]%'),scope='master:OverSkillMasterData:68:text')
        catalog.validate(e)
        with self.assertRaises(ValueError):
            catalog.validate(dict(e,target='받는 피해 +[0,0,0]% 효과를 8초 동안 부여'))

    def test_template_uses_complete_numeric_evidence(self):
        e = self.entry('闘技場に{{count}}回挑戦する', '투기장에 {{count}}회 도전하기')
        e.update(kind='numeric', slots={'count':'integer'}, examples=['闘技場に10回挑戦する'])
        catalog.validate(e)
        pattern = catalog.numeric_pattern(e)
        self.assertIsNotNone(pattern.fullmatch('闘技場に３回挑戦する'))
        for source in ['闘技場に全部回挑戦する','闘技場に3回挑戦する追加','別の闘技場に3回挑戦する']:
            self.assertIsNone(pattern.fullmatch(source))
        broken = dict(e, target='투기장에 {{other}}회 도전하기')
        with self.assertRaises(ValueError): catalog.validate(broken)

    def test_conflicts_and_master_scope_are_rejected(self):
        e = self.entry(); row = dict(id='source', source=e['source'], sourceHash=e['sourceHash'], scope='ui')
        with self.assertRaises(ValueError): catalog.current_entries([e, dict(e, id='other', target='공격 +[0,0,0]%')], dict(items=[row]))
        with self.assertRaises(ValueError): catalog.current_entries([dict(e, scope='master:Wrong:1:text')], dict(items=[row]))

    def test_reviewed_compositions_follow_source_and_name_changes(self):
        fmt = dict(self.entry('<size=90%>オート{0}</size>\n{1}強化','<size=90%>오토 {0}</size>\n{1} 강화'),id='format')
        name = dict(self.entry('星の脈動','별의 맥동'),id='name')
        recipes = [dict(id='button',template='format',arguments=[dict(number='1'),dict(entry='name')])]
        generated, stale = catalog.compose_entries([fmt,name], recipes)
        self.assertEqual(stale, [])
        self.assertEqual(generated[0]['target'], '<size=90%>오토 1</size>\n별의 맥동 강화')
        # An outdated format/name is absent from active reviews: all dependent outputs disappear.
        self.assertEqual(catalog.compose_entries([fmt],recipes), ([],['button']))
        changed = dict(name,target='별의 고동')
        self.assertIn('별의 고동',catalog.compose_entries([fmt,changed],recipes)[0][0]['target'])
        with self.assertRaises(ValueError):
            catalog.compose_entries([fmt,name],[dict(recipes[0],arguments=[dict(number='one'),dict(entry='name')])])
        conflicting = dict(self.entry(generated[0]['source'],'<size=90%>오토 1</size>\n다른 이름 강화'),id='conflict')
        with self.assertRaises(ValueError): catalog.compose_entries([fmt,name,conflicting],recipes)

    def test_composition_rejects_executable_or_unknown_format_fields(self):
        name = dict(self.entry('名前','이름'),id='name')
        for source in ('{0.__class__}', '{0[0]}', '{0!r}', '{0:unknown}', '{1}'):
            fmt = dict(self.entry(source,source),id='format')
            recipe = dict(id='test',template='format',arguments=[dict(entry='name')])
            with self.assertRaises(ValueError): catalog.compose_entries([fmt,name],[recipe])

    def test_update_keeps_old_bundle_for_evidence_but_exports_current_source_only(self):
        with TemporaryDirectory(dir=Path(__file__).resolve().parent) as temp:
            root = Path(temp)
            data = root/'data'
            text = data/'text'
            manifest = data/'catalog-windows_r18.json'
            old_bundle, new_bundle, shared_bundle = (letter * 64 for letter in 'abc')

            def select(*bundles):
                catalog.save(manifest, {'data': [
                    dict(hash=b[:32], path=b[32:]+'.bytes', size=0) for b in bundles]})

            def extract(bundle, row_id, source):
                directory = data/'extracted'/bundle
                catalog.save(directory/'complete.json', {})
                tree = directory/'1.json.gz'
                tree.write_bytes(gzip.compress(json.dumps({'records': [{'id': row_id}]}).encode()))
                row = dict(source_id=bundle+':1', field_path='/records/0/text', ja=source,
                           category='kept/masterdata', containers=['master_data.asset'],
                           tree_file=str(tree.relative_to(root)))
                (directory/'source-records.jsonl').write_text(json.dumps(row)+'\n', encoding='utf8')

            extract(old_bundle, 7, '旧説明')
            extract(new_bundle, 7, '新説明')
            extract(shared_bundle, 8, '共通説明')
            with patch.multiple(catalog, ROOT=root, TEXT=text):
                select(old_bundle, shared_bundle)
                previous = catalog.scan()
                old = next(r for r in previous['items'] if r['rowId'] == 7)
                entry = self.entry(old['source'], '이전 설명')
                entry['evidence'] = [{k: old[k] for k in ('id', 'sourceHash')}]
                source, context = '画面の文', 'menu/Label'
                observation = dict(id=catalog.hash_text(context+'\x1f'+source), source=source, context=context)
                catalog.save(text/'observed-ui.json', {'catalog': previous['catalog'], 'items': [observation]})
                live = self.entry(source, '화면 문구')
                live.update(id='live', evidence=[dict(id=observation['id'], sourceHash=live['sourceHash'])])
                self.assertEqual(catalog.current_entries([entry, live], catalog.review_inventory())[1], [])

                select(new_bundle, shared_bundle)
                with self.assertRaises(ValueError): catalog.review_inventory()
                current = catalog.scan()
                diff = catalog.inventory_delta(previous, current)
                self.assertEqual((diff['added'], diff['removed']), ([], []))
                self.assertEqual([r['current']['source'] for r in diff['changed']], ['新説明'])
                self.assertEqual(catalog.current_entries([entry, live], catalog.review_inventory()), ([], ['test', 'live']))
                catalog.export([entry, live])
                self.assertEqual(catalog.read(data/'exports/text-catalog.json', {})['entries'], [])
                self.assertTrue((data/'extracted'/old_bundle/'source-records.jsonl').is_file())


if __name__ == '__main__': unittest.main()
