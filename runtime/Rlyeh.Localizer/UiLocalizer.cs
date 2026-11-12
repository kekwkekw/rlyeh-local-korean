using HarmonyLib;
using Rlyeh.Localizer.Core;
using Rog.View;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using Utage;


namespace Rlyeh.Localizer;

// UI labels are independent of the UTAGE adapter. No XUnity component detector is used.
internal sealed class UiLocalizer
{
    private readonly Plugin plugin;
    private readonly Dictionary<int, UiState> states = new();
    private readonly Dictionary<int, Component> owners = new();
    internal readonly TmpBufferLocalizer Tmp;
    private TMP_FontAsset? fallback;
    private float nextScan;
    private bool applying;

    private sealed class UiState
    {
        public readonly RevisionSlot Slot = new();
        public string? Target;
        public string? Context;
    }
    public UiLocalizer(Plugin plugin) { this.plugin = plugin; Tmp = new TmpBufferLocalizer(plugin, this); }
    internal static bool HasJapanese(string value) => value.Any(c => (c >= '\u3040' && c <= '\u30ff') || (c >= '\u3400' && c <= '\u9fff'));
    internal static bool HasKorean(string value) => value.Any(c => c >= '가' && c <= '힣');
    internal static string Context(Component component)
    {
        var parts = new List<string>();
        for (Transform? t = component.transform; t && parts.Count < 32; t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return component.gameObject.scene.name + "/" + string.Join("/", parts);
    }
    public void Tick()
    {
        Tmp.Tick();
        if (Time.realtimeSinceStartup < nextScan) return;
        nextScan = Time.realtimeSinceStartup + 1;
        UiReadability.PruneDestroyedViews();
        // Serialized labels may never call a setter. Scan active labels only once a second.
        foreach (var text in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Text>()) Queue(text);
        foreach (int id in owners.Where(p => !p.Value).Select(p => p.Key).ToArray()) { owners.Remove(id); states.Remove(id); }
        Tmp.Prune();
    }
    public void Queue(UnityEngine.UI.Text text)
    {
        if (applying || !text || !text.isActiveAndEnabled || text.TryCast<UguiNovelText>() != null || plugin.StoryNameIds.Contains(text.GetInstanceID())) return;
        var input = text.GetComponentInParent<UnityEngine.UI.InputField>();
        if (input && input.textComponent == text) return;
        string source = text.text ?? "";
        owners[text.GetInstanceID()] = text;
        if (HasKorean(source)) text.font = plugin.GetKoreanFont();
        if (!HasJapanese(source)) return;
        string context = Context(text);
        Request(text.GetInstanceID(), source, context, () => text && text.isActiveAndEnabled && Context(text) == context ? text.text : null, result =>
        {
            text.font = plugin.GetKoreanFont();
            text.text = result;
        });
    }
    internal void ApplyBoundFormat(TMP_Text? text, string template, string? nativeName, int numericSlots)
    {
        if (applying || text is null || !text) return;
        string source = TmpBufferLocalizer.ReadCurrentSource(text) ?? "";
        owners[text.GetInstanceID()] = text;
        if (!BoundUiFormat.TryBind(plugin.Service.Catalog, template, source, nativeName, numericSlots, out var bound)) return;
        if (nativeName == null || !HasJapanese(nativeName))
        {
            if (!bound.TryRender(nativeName, out string result)) return;
            int id = text.GetInstanceID();
            if (!states.TryGetValue(id, out var state)) states[id] = state = new UiState();
            state.Context = Context(text); state.Slot.Begin(source);
            Apply(state, source, result, value => ApplyTmp(text, value));
            return;
        }
        string context = Context(text);
        // Only the wallet name may go to hdTrans. The whole native output remains
        // the revision/read guard, including the selected quantity and draw count.
        Request(text.GetInstanceID(), source, context,
            () => text && text.isActiveAndEnabled && Context(text) == context ? TmpBufferLocalizer.ReadCurrentSource(text) : null,
            result => ApplyTmp(text, result), nativeName,
            name => bound.TryRender(name, out string result) ? result : source);
    }
    private void ApplyTmp(TMP_Text text, string result)
    {
        EnsureTmpFont(text);
        text.text = result;
        UiReadability.Apply(text);
    }
    internal void EnsureTmpFont(TMP_Text text)
    {
        if (!text.font) return;
        if (!fallback)
        {
            var font = plugin.GetKoreanFont();
            if (plugin.IsBundledKoreanFont)
            {
                try
                {
                    var bundledFont = TMP_FontAsset.CreateFontAsset(font, 90, 9,
                        GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
                    if (bundledFont && bundledFont.TryAddCharacters("한글힣", false))
                        fallback = bundledFont;
                    else if (bundledFont)
                        UnityEngine.Object.Destroy(bundledFont);
                }
                catch (Exception ex) { plugin.Log.LogWarning("Bundled TMP font: " + ex.Message); }
            }
            if (!fallback)
            {
                plugin.Log.LogWarning("Noto Serif CJK KR Bold unavailable for TMP; using installed Korean font");
                fallback = TMP_FontAsset.CreateFontAsset("Noto Sans KR", "Regular", 90);
                if (!fallback) fallback = TMP_FontAsset.CreateFontAsset("Malgun Gothic", "Regular", 90);
                if (!fallback) throw new InvalidOperationException("TMP could not load a Korean OS font face");
            }
            fallback!.hideFlags = HideFlags.DontUnloadUnusedAsset;
            UnityEngine.Object.DontDestroyOnLoad(fallback);
            plugin.Log.LogInfo($"TMP Korean font ready: {fallback.faceInfo.familyName} {fallback.faceInfo.styleName}");
        }
        var fallbacks = text.font.fallbackFontAssetTable;
        if (fallbacks == null) text.font.fallbackFontAssetTable = fallbacks = new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
        if (!fallbacks.Contains(fallback)) fallbacks.Add(fallback);
    }
    internal static bool IsRankingNickname(TMP_Text text, string source)
    {
        // The three native ranking views use the Name group for the nickname value.
        // Only exclude a label owned by the view and populated from its player model.
        if (text.transform.parent?.name != "Name") return false;
        var row = text.GetComponentInParent<ServerRankingListContentView>();
        if (row && row.m_nameText == text) return true;
        var reward = text.GetComponentInParent<ServerRankingRewardPopupView>();
        if (reward && reward.m_nameText == text) return true;
        var own = text.GetComponentInParent<ServerRankingMyContentView>();
        // The user's unconfigured nickname arrives from the game as its standard placeholder.
        return own && own.m_nameText == text && source != "未設定";
    }
    private bool Request(int id, string source, string context, Func<string?>? read, Action<string> apply,
        string? translationSource = null, Func<string, string>? compose = null)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 6000 || !HasJapanese(source) ||
            !HasJapanese(new DisplayText(source).Plain)) return false;
        states.TryGetValue(id, out var state);
        if (state != null && context == state.Context && source == state.Target) return true;
        bool ready = plugin.Service.TryGetReady(translationSource ?? source, "ui", out string translation, context);
        if (!ready && (read == null || (state != null && context == state.Context &&
            state.Slot.Source == source && state.Target == null))) return false;
        if (state == null) states[id] = state = new UiState();
        state.Context = context;
        long revision = state.Slot.Begin(source); state.Target = null;
        if (ready)
        {
            Apply(state, source, compose == null ? translation : compose(translation), apply);
            return true;
        }
        if (read == null) return false;
        if (!plugin.Service.Request(translationSource ?? source, "ui", (result, error) =>
        {
            if (!state.Slot.Accepts(revision, source)) return;
            // A mesh/context change can invalidate this attempt transiently. Let the next
            // active scan retry instead of retaining a permanently pending UI request.
            if (read() != source) { state.Slot.Invalidate(); state.Target = null; return; }
            if (error != null || result == null) { state.Target = source; return; }
            Apply(state, source, compose == null ? result : compose(result), apply);
        }, context)) { state.Target = null; state.Slot.Invalidate(); }
        return false;
    }
    private void Apply(UiState state, string source, string result, Action<string> apply)
    {
        state.Target = result;
        applying = true;
        try { apply(result); }
        catch (Exception ex) { state.Target = source; plugin.Log.LogWarning("UI apply: " + ex.Message); }
        finally { applying = false; }
    }
}

[HarmonyPatch(typeof(UnityEngine.UI.Text), "set_text")]
internal static class UiTextPatch
{
    private static void Postfix(UnityEngine.UI.Text __instance) => Plugin.Instance.Ui?.Queue(__instance);
}

