using HarmonyLib;
using Rog.MasterData;
using Rlyeh.Localizer.Core;
using System.Text.Json;
using UnityEngine;

namespace Rlyeh.Localizer;

// Opt-in test build only. Reads loaded tables and constructs local display-only skill objects.
// Does not call gameplay actions or mutate account/master records.
[HarmonyPatch(typeof(AbilityData), nameof(AbilityData.GetText))]
internal static class CatalogProbe
{
    internal static bool Running;
    private static bool scanned;
    private static void Postfix(CharacterDataBase __0)
    {
        if (scanned || !Plugin.Instance.TraceCatalog) return;
        scanned = true; Running = true;
        var rows = new List<object>();
        var catalog = Plugin.Instance.Service.Catalog;
        const string format = "<color=#00C0FF>{0}</color>"; // Observed game prefab/Setup value.
        try
        {
            void Check(string scope, string source, int level, string mode, string native,
                Func<string,string> current, Func<string,string> other)
            {
                var values = new Dictionary<string,string>();
                var comparisonValues = new Dictionary<string,string>();
                string Resolve(string token) => values[token] = current(token);
                string Compare(string token) => comparisonValues[token] = other(token);
                string target;
                bool hit = mode == "text" ? catalog.TryRenderMaster(scope, source, native, Resolve, out target) :
                    catalog.TryRenderMasterDiff(scope, source, native, Resolve, Compare, format, out target);
                string? error = null;
                if (hit) { try { TextCatalog.ValidateControls(native,target); } catch (Exception ex) { error=ex.Message; } }
                rows.Add(new { scope,source,level,mode,native,target,hit,error,format,values,comparisonValues });
            }
            void Ability(AbilityMasterData data, int maxLevel)
            {
                string source = data.text, scope = MasterDisplay.Scope(data, data.Key);
                if (!catalog.TryExact(scope,source,out _)) return;
                for (int level=0; level<=maxLevel; level++)
                {
                    var current = new AbilityData(data,level);
                    var other = new AbilityData(data,Math.Max(0,level-1));
                    Check(scope,source,level,"text",current.GetText(__0),t=>current.GetParamString(t,__0),t=>other.GetParamString(t,__0));
                    Check(scope,source,level,"diff",current.GetDiffText(other,format,__0),t=>current.GetParamString(t,__0),t=>other.GetParamString(t,__0));
                }
            }
            foreach (var table in Resources.FindObjectsOfTypeAll<AbilityMasterTable>())
                foreach (var data in table.Records) Ability(data, 3);
            foreach (var table in Resources.FindObjectsOfTypeAll<SkinSkillMasterTable>())
                foreach (var data in table.Records) Ability(data, data.maxLevel);
            void Skill(SkillMasterDataBase data)
            {
                var sample = new SkillData(data,null,1);
                string? scope = MasterDisplay.Scope(sample);
                string source=data.Text;
                if (scope == null || !catalog.TryExact(scope,source,out _)) return;
                for (int level=1;level<=3;level++)
                {
                    var current = new SkillData(data,null,level);
                    var other = new SkillData(data,null,Math.Max(1,level-1));
                    Check(scope,source,level,"text",current.GetText(__0),t=>current.GetParamString(t,__0),t=>other.GetParamString(t,__0));
                    Check(scope,source,level,"diff",current.GetDiffText(other,format,__0),t=>current.GetParamString(t,__0),t=>other.GetParamString(t,__0));
                }
            }
            foreach (var table in Resources.FindObjectsOfTypeAll<OverSkillMasterTable>()) foreach (var data in table.Records) Skill(data);
            foreach (var table in Resources.FindObjectsOfTypeAll<AutoSkillMasterTable>()) foreach (var data in table.Records) Skill(data);
        }
        catch (Exception ex) { Plugin.Instance.Log.LogError("CATALOG native validation: " + ex); }
        finally
        {
            Running = false;
            string root=Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
            File.WriteAllText(Path.Combine(root,"cache","native-catalog-validation.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
            Plugin.Instance.Log.LogInfo("CATALOG native validation cases="+rows.Count);
        }
    }
}
