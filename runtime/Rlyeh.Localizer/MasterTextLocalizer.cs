using HarmonyLib;
using Rog.MasterData;

namespace Rlyeh.Localizer;

// Change only display return values; master records, skill state and calculations stay native.
internal static class MasterDisplay
{
    private static readonly HashSet<string> traced = new();
    internal static string? Scope(SkillData skill)
    {
        var data = skill.BaseData;
        string? type = data.TryCast<OverSkillMasterData>() != null ? "OverSkillMasterData" :
            data.TryCast<AutoSkillMasterData>() != null ? "AutoSkillMasterData" : null;
        return type == null ? null : $"master:{type}:{data.Id}:text";
    }
    internal static string Scope(AbilityData ability) => Scope(ability.BaseData, ability.Id);
    internal static string Scope(AbilityMasterData data, int id)
    {
        string type = data.TryCast<SkinSkillMasterData>() != null ? "SkinSkillMasterData" : "AbilityMasterData";
        return $"master:{type}:{id}:text";
    }
    internal static void Render(string scope, string source, int level, Func<string,string> resolve,
        ref string display, Func<string,string>? comparison = null, string? format = null)
    {
        var plugin = Plugin.Instance;
        string original = display;
        bool hit = comparison == null
            ? plugin.Service.Catalog.TryRenderMaster(scope, source, original, resolve, out var target)
            : plugin.Service.Catalog.TryRenderMasterDiff(scope, source, original, resolve, comparison, format!, out target);
        if (hit) display = target;
        if (plugin.TraceCatalog && traced.Count < 10000 && traced.Add(scope+":"+level+":"+original))
            plugin.Log.LogInfo($"CATALOG {(comparison == null ? "text" : "diff")} hit={hit} scope={scope} level={level} original={original} translated={display}");
    }
}
[HarmonyPatch(typeof(SkillData), nameof(SkillData.GetText))]
internal static class SkillTextPatch
{
    private static void Postfix(SkillData __instance, CharacterDataBase __0, ref string __result)
    {
#if NATIVE_CATALOG_PROBE
        if (CatalogProbe.Running) return;
#endif
        try
        {
            string? scope = MasterDisplay.Scope(__instance);
            if (scope != null) MasterDisplay.Render(scope, __instance.BaseData.Text, __instance.Level,
                token => __instance.GetParamString(token, __0), ref __result);
        }
        catch (Exception ex) { Plugin.Instance.Log.LogWarning("Skill text retained: " + ex.Message); }
    }
}
[HarmonyPatch(typeof(AbilityData), nameof(AbilityData.GetText))]
internal static class AbilityTextPatch
{
    private static void Postfix(AbilityData __instance, CharacterDataBase __0, ref string __result)
    {
#if NATIVE_CATALOG_PROBE
        if (CatalogProbe.Running) return;
#endif
        try
        {
            MasterDisplay.Render(MasterDisplay.Scope(__instance), __instance.BaseData.text, __instance.Level,
                token => __instance.GetParamString(token, __0), ref __result);
        }
        catch (Exception ex) { Plugin.Instance.Log.LogWarning("Ability text retained: " + ex.Message); }
    }
}
[HarmonyPatch(typeof(SkillData), nameof(SkillData.GetDiffText))]
internal static class SkillDiffTextPatch
{
    private static void Postfix(SkillData __instance, SkillData __0, string __1, CharacterDataBase __2, ref string __result)
    {
#if NATIVE_CATALOG_PROBE
        if (CatalogProbe.Running) return;
#endif
        try
        {
            string? scope = MasterDisplay.Scope(__instance);
            if (scope != null && scope == MasterDisplay.Scope(__0) && __instance.BaseData.Text == __0.BaseData.Text)
                MasterDisplay.Render(scope, __instance.BaseData.Text, __instance.Level,
                    token => __instance.GetParamString(token, __2), ref __result,
                    token => __0.GetParamString(token, __2), __1);
        }
        catch (Exception ex) { Plugin.Instance.Log.LogWarning("Skill comparison retained: " + ex.Message); }
    }
}
[HarmonyPatch(typeof(AbilityData), nameof(AbilityData.GetDiffText))]
internal static class AbilityDiffTextPatch
{
    private static void Postfix(AbilityData __instance, AbilityData __0, string __1, CharacterDataBase __2, ref string __result)
    {
#if NATIVE_CATALOG_PROBE
        if (CatalogProbe.Running) return;
#endif
        try
        {
            string source = __instance.BaseData.text;
            string scope = MasterDisplay.Scope(__instance);
            if (scope == MasterDisplay.Scope(__0) && source == (string)__0.BaseData.text)
                MasterDisplay.Render(scope, source, __instance.Level,
                    token => __instance.GetParamString(token, __2), ref __result,
                    token => __0.GetParamString(token, __2), __1);
        }
        catch (Exception ex) { Plugin.Instance.Log.LogWarning("Ability comparison retained: " + ex.Message); }
    }
}

