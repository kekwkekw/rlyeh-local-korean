using HarmonyLib;
using Rlyeh.Localizer.Core;
using TMPro;
using UnityEngine;

namespace Rlyeh.Localizer;

// The single general TMP translation path. Work at the common processing input,
// before layout/glyph generation, without calling a text setter or reading text.
internal sealed class TmpBufferLocalizer
{
    private sealed class State
    {
        internal readonly RevisionSlot Slot = new();
        internal TMP_Text Text = null!;
        internal string Context = "", Raw = "";
        internal TMP_Text.TextInputSources Mode;
        internal string? Target, LayoutTarget;
        internal float LayoutWidth = -1;
        internal bool Pending, Failed;
    }
    private readonly Plugin plugin;
    private readonly UiLocalizer ui;
    private readonly Dictionary<int, State> states = new();
    private readonly Dictionary<int, State> layouts = new(), retries = new();
    private readonly Dictionary<int, int> measurements = new();
    private readonly HashSet<string> warnings = new();
    private bool layingOut;
    internal TmpBufferLocalizer(Plugin plugin, UiLocalizer ui) { this.plugin = plugin; this.ui = ui; }

    internal static string? ReadBuffer(TMP_Text text)
    {
        var backing = text.m_TextBackingArray;
        int count = backing.m_Index;
        var array = backing.m_Array;
        if (count < 0 || count > 6000 || array == null || count > array.Length) return null;
        var chars = new char[count];
        for (int i = 0; i < count; i++) chars[i] = (char)array[i];
        return new string(chars);
    }
    internal static string? ReadCurrentSource(TMP_Text text) =>
        text.m_inputSource is TMP_Text.TextInputSources.TextString or TMP_Text.TextInputSources.TextInputBox
            ? text.m_text : ReadBuffer(text);

    internal void BeginMeasure(TMP_Text text)
    {
        int id = text.GetInstanceID();
        measurements.TryGetValue(id, out int depth);
        measurements[id] = depth + 1;
    }
    internal void EndMeasure(TMP_Text text)
    {
        int id = text.GetInstanceID();
        if (measurements.TryGetValue(id, out int depth) && depth > 1) measurements[id] = depth - 1;
        else measurements.Remove(id);
    }
    private static bool Protected(TMP_Text text, string source)
    {
        if (GachaConfirmText.Owns(text) || UiLocalizer.IsRankingNickname(text, source)) return true;
        var input = text.GetComponentInParent<TMP_InputField>();
        return input && input.textComponent == text;
    }
    internal string? Resolve(TMP_Text text, out string? source)
    {
        source = ReadBuffer(text);
        int id = text.GetInstanceID();
        bool measuring = measurements.ContainsKey(id);
        bool japanese = source != null && UiLocalizer.HasJapanese(source);
        bool korean = source != null && UiLocalizer.HasKorean(source);
        if (!japanese && !korean)
        {
            if (!measuring) Discard(id);
            return null;
        }
        if (Protected(text, source!))
        {
            if (!measuring) Discard(id);
            if (korean) ui.EnsureTmpFont(text);
            return null;
        }
        string context = UiLocalizer.Context(text);
        string? target = null;
        if (measuring)
        {
            if (plugin.Service.TryGetReady(source!, "ui", out string ready, context)) target = ready;
        }
        else
        {
            if (!states.TryGetValue(id, out var state) || state.Text != text)
                states[id] = state = new() { Text = text };
            string raw = text.m_text ?? "";
            var mode = text.m_inputSource;
            if (state.Slot.Source != source || state.Context != context || state.Raw != raw || state.Mode != mode)
            {
                state.Slot.Begin(source!); state.Context = context; state.Raw = raw; state.Mode = mode;
                state.Target = null; state.Pending = state.Failed = false;
                retries.Remove(id); layouts.Remove(id);
                if (!japanese || !UiLocalizer.HasJapanese(new DisplayText(source!, ui: true).Plain)) state.Target = source;
            }
            if (state.Target == null && !state.Failed)
            {
                if (plugin.Service.TryGetReady(source!, "ui", out string ready, context)) SetTarget(state, ready);
                else if (!state.Pending) Request(state);
            }
            target = state.Target;
            if (target != null && UiLocalizer.HasKorean(target) && !layingOut &&
                (state.LayoutTarget != target || Math.Abs(text.rectTransform.rect.width - state.LayoutWidth) > 0.5f))
                layouts[id] = state;
        }
        if (korean || (target != null && UiLocalizer.HasKorean(target))) ui.EnsureTmpFont(text);
        if (target == null || target == source) return null;
        TextCatalog.ValidateControls(source!, target);
        return target;
    }
    private void SetTarget(State state, string target)
    {
        try { TextCatalog.ValidateControls(state.Slot.Source, target); state.Target = target; }
        catch (InvalidDataException) { state.Failed = true; Warn("TMP translation changed display controls or numbers; original retained"); }
    }
    private bool Current(State state, long revision, string source)
    {
        var text = state.Text;
        return state.Slot.Accepts(revision, source) && text && text.m_inputSource == state.Mode &&
            UiLocalizer.Context(text) == state.Context && !Protected(text, source) &&
            (state.Mode is TMP_Text.TextInputSources.TextString or TMP_Text.TextInputSources.TextInputBox
                ? text.m_text == state.Raw : ReadBuffer(text) == source);
    }
    private void Request(State state)
    {
        string source = state.Slot.Source;
        long revision = state.Slot.Revision;
        int id = state.Text.GetInstanceID();
        state.Pending = true;
        bool accepted = plugin.Service.Request(source, "ui", (value, error) =>
        {
            if (!state.Slot.Accepts(revision, source)) return;
            state.Pending = false;
            if (!Current(state, revision, source))
            {
                if (plugin.Trace) plugin.Log.LogInfo("TMP stale result ignored rev=" + revision);
                return;
            }
            if (error != null || value == null) { state.Failed = true; return; }
            SetTarget(state, value);
            if (state.Target == null) return;
            // Formatted and array setters have already parsed their input. Rebuild
            // processing for those too, including inactive pooled objects.
            if (state.Mode is TMP_Text.TextInputSources.TextString or TMP_Text.TextInputSources.TextInputBox)
                state.Text.ParseInputText();
            else state.Text.PopulateTextProcessingArray();
            state.Text.havePropertiesChanged = true;
            state.Text.SetVerticesDirty();
            state.Text.SetLayoutDirty();
        }, state.Context);
        if (!accepted) { state.Pending = false; retries[id] = state; }
    }
    internal void Tick()
    {
        foreach (var (id, state) in retries.ToArray())
        {
            retries.Remove(id);
            if (Current(state, state.Slot.Revision, state.Slot.Source) && !state.Pending && !state.Failed) Request(state);
        }
    }
    internal void ApplyLayouts()
    {
        if (layingOut) return;
        layingOut = true;
        try
        {
            foreach (var (id, state) in layouts.ToArray())
            {
                var text = state.Text;
                if (text && !text.isActiveAndEnabled) continue;
                layouts.Remove(id);
                if (!text || state.Target == null || !Current(state, state.Slot.Revision, state.Slot.Source)) continue;
                try
                {
                    UiReadability.Apply(text);
                    state.LayoutTarget = state.Target;
                    state.LayoutWidth = text.rectTransform.rect.width;
                }
                catch (Exception ex) { Warn("TMP layout: " + ex.Message); }
            }
        }
        finally { layingOut = false; }
    }
    internal void Prune()
    {
        foreach (int id in states.Where(p => !p.Value.Text).Select(p => p.Key).ToArray())
        { states[id].Slot.Invalidate(); states.Remove(id); layouts.Remove(id); retries.Remove(id); measurements.Remove(id); }
        foreach (int id in layouts.Where(p => !p.Value.Text).Select(p => p.Key).ToArray()) layouts.Remove(id);
    }
    private void Discard(int id)
    {
        if (states.Remove(id, out var old)) old.Slot.Invalidate();
        layouts.Remove(id); retries.Remove(id);
    }
    internal void Warn(string message)
    {
        if (warnings.Count < 32 && warnings.Add(message)) plugin.Log.LogWarning(message);
    }
}

[HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.PopulateTextProcessingArray))]
internal static class TmpBufferTranslationPatch
{
    private static void Prefix(TMP_Text __instance, out string? __state)
    {
        __state = null;
        var localizer = Plugin.Instance.Ui?.Tmp;
        if (localizer == null || !__instance) return;
        try
        {
            string? target = localizer.Resolve(__instance, out string? source);
            if (target == null) return;
            __state = source;
            __instance.PopulateTextBackingArray(target);
        }
        catch (Exception ex)
        {
            if (__state != null) { __instance.PopulateTextBackingArray(__state); __state = null; }
            localizer.Warn("TMP buffer translation: " + ex.Message);
        }
    }
    // Finalizer runs after both successful processing and exceptions. No text
    // setter, mesh update, input-mode change or dirty-getter read occurs here.
    private static Exception? Finalizer(TMP_Text __instance, string? __state, Exception? __exception)
    {
        if (__state != null && __instance) __instance.PopulateTextBackingArray(__state);
        return __exception;
    }
}

[HarmonyPatch]
internal static class TmpMeasurementPatch
{
    private static System.Reflection.MethodBase[] TargetMethods() => new[]
    {
        AccessTools.Method(typeof(TMP_Text), nameof(TMP_Text.GetPreferredValues), new[] { typeof(string) }),
        AccessTools.Method(typeof(TMP_Text), nameof(TMP_Text.GetPreferredValues), new[] { typeof(string), typeof(float), typeof(float) })
    };
    private static void Prefix(TMP_Text __instance, out string? __state)
    {
        __state = null;
        if (Plugin.Instance.Ui == null) return;
        __state = TmpBufferLocalizer.ReadBuffer(__instance);
        Plugin.Instance.Ui.Tmp.BeginMeasure(__instance);
    }
    private static void Finalizer(TMP_Text __instance, string? __state)
    {
        Plugin.Instance.Ui?.Tmp.EndMeasure(__instance);
        if (__state == null || !__instance) return;
        try
        {
            // Native sizing bypasses SetTextInternal's hook in this build. At the public
            // sizing boundary, the temporary string has already been measured.
            // Restore the binding and processed characters without scheduling a
            // second layout or recursively calling a sizing API.
            __instance.PopulateTextBackingArray(__state);
            __instance.PopulateTextProcessingArray();
            __instance.SetArraySizes(__instance.m_TextProcessingArray);
        }
        catch (Exception ex) { Plugin.Instance.Ui?.Tmp.Warn("TMP measurement restore: " + ex.Message); }
    }
}
