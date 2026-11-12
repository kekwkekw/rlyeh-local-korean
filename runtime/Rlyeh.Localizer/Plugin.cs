using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Rlyeh.Localizer.Core;
using UnityEngine;
using Utage;

namespace Rlyeh.Localizer;

[BepInPlugin("local.rlyeh.korean", "Rlyeh Local Korean", "0.1.0")]
public sealed class Plugin : BasePlugin
{
    internal static Plugin Instance = null!;
    internal TranslationService Service = null!;
    private Font? koreanFont;
    private string koreanFontBundlePath = null!;
    internal bool IsBundledKoreanFont { get; private set; }
    internal bool Trace;
    internal bool TraceCatalog;
    internal UiLocalizer? Ui;
    internal readonly HashSet<int> StoryNameIds = new();
    internal readonly Dictionary<int, WindowState> Windows = new();
    public override void Load()
    {
        Instance = this;
        string root = Path.GetDirectoryName(GetType().Assembly.Location)!;
        koreanFontBundlePath = Path.Combine(root, "fonts", "notoserifcjkkr-bold.bundle");
        string dictionary = Config.Bind("Local", "Dictionary", "",
            "Leave empty to use the bundled hdTrans dictionary. Relative overrides are resolved next to the plugin.").Value.Trim();
        string snapshot = Path.Combine(root, dictionary.Length == 0
            ? Path.Combine("native", "hdtrans-data", "dicts.hdsnap")
            : dictionary);
        if (dictionary.Length == 0) Log.LogInfo("Using bundled hdTrans dictionary");
        int delay = Config.Bind("Diagnostics", "TranslationDelayMs", 0, "Regression tests only: simulated slow local response. Leave zero normally.").Value;
        Trace = Config.Bind("Diagnostics", "TraceDisplay", false, "Log source/target display counts for regression tests.").Value;
        TraceCatalog = Config.Bind("Diagnostics", "TraceCatalog", false, "Log reviewed master template selection and native formatted skill text.").Value;
        Service = new TranslationService(root, snapshot, s => Log.LogInfo(s), delay);
        Log.LogInfo("Reviewed text catalog: " + Service.Catalog.Count);
        if (Config.Bind("Local", "TranslateUi", true, "Translate ordinary UI locally. Story windows always use the separate UTAGE adapter.").Value) Ui = new UiLocalizer(this);
        ClassInjector.RegisterTypeInIl2Cpp<MainThreadPump>();
        AddComponent<MainThreadPump>();
        new Harmony("local.rlyeh.korean").PatchAll();
        Log.LogInfo("UTAGE renderer + TMP processing-buffer localization; hdTrans only; no TMP polling/setter hooks");
    }
    internal void EnsureFont(AdvUguiMessageWindow window)
    {
        var font = GetKoreanFont();
        if (window.Text && window.Text.font != font) window.Text.font = font;
        if (window.nameText)
        {
            StoryNameIds.Add(window.nameText.GetInstanceID());
            if (window.nameText.font != font) window.nameText.font = font;
        }
    }
    internal Font GetKoreanFont()
    {
        if (koreanFont) return koreanFont!;
        if (File.Exists(koreanFontBundlePath))
        {
            AssetBundle? bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromFile(koreanFontBundlePath);
                if (bundle)
                {
                    var font = bundle.LoadAsset<Font>("assets/notoserifcjkkr-bold.otf");
                    if (font && font.dynamic && font.HasCharacter('한') && font.HasCharacter('힣'))
                        koreanFont = font;
                    else if (font)
                        UnityEngine.Object.Destroy(font);
                }
            }
            catch (Exception ex) { Log.LogWarning("Bundled UGUI font: " + ex.Message); }
            finally { if (bundle) bundle!.Unload(false); }
        }
        IsBundledKoreanFont = koreanFont;
        if (!koreanFont)
        {
            Log.LogWarning("Noto Serif CJK KR Bold unavailable for UGUI; using installed Korean font");
            koreanFont = Font.CreateDynamicFontFromOSFont(new[] { "Noto Sans KR", "Malgun Gothic", "Arial" }, 32);
            if (!koreanFont || !koreanFont.HasCharacter('한')) throw new InvalidOperationException("No Korean dynamic font available");
        }
        koreanFont!.hideFlags = HideFlags.DontUnloadUnusedAsset;
        UnityEngine.Object.DontDestroyOnLoad(koreanFont);
        Log.LogInfo($"Dynamic font ready: {koreanFont.name} (bundled={IsBundledKoreanFont})");
        return koreanFont;
    }
    internal void Changed(AdvUguiMessageWindow window, AdvMessageWindow model)
    {
        EnsureFont(window);
        int id = window.GetInstanceID();
        if (!Windows.TryGetValue(id, out var state)) Windows[id] = state = new WindowState(window);
        string source = model.Text?.OriginalText ?? "";
        long revision = state.Slot.Begin(source);
        state.Model = model; state.Translation = null; state.SourceLength = model.Text?.Length ?? 0;
        state.Alignment = null;
        state.Name = model.NameText ?? "";
        string scope = window.Engine.Page.ScenarioLabel ?? "";
        if (Trace) Log.LogInfo($"STORY begin rev={revision} scope={scope} chars={state.SourceLength} frame={Time.frameCount} source={source}");
        if (source.Length > 0)
        {
            if (Service.TryGetReady(source, scope, out string prepared))
                SetStoryTranslation(state, source, prepared, ready: true);
            else if (!Service.Request(source, scope, (value, error) =>
            {
                if (!state.Slot.Accepts(revision, source) || !state.Window || !state.Window.isActiveAndEnabled ||
                    state.Model?.Text?.OriginalText != source)
                {
                    if (Trace) Log.LogInfo("STORY stale result ignored rev=" + revision);
                    return;
                }
                if (error != null) { Log.LogWarning(error); return; }
                if (value != null) SetStoryTranslation(state, source, value, ready: false);
            })) Log.LogWarning("Translation queue unavailable; original retained");
        }
        string name = state.Name;
        if (name.Length == 0) return;
        if (Service.TryGetReady(name, "speaker", out string translatedName))
        {
            if (window.nameText) window.nameText.text = translatedName;
            return;
        }
        Service.Request(name, "speaker", (value, error) =>
        {
            if (error == null && value != null && state.Window && state.Window.nameText &&
                state.Slot.Accepts(revision, source) && state.Name == name)
                state.Window.nameText.text = value;
        });
    }
    private void SetStoryTranslation(WindowState state, string source, string value, bool ready)
    {
        state.Translation = value;
        state.TargetLength = new TextData(value).Length;
        state.Alignment = new DisplayAlignment(new DisplayText(source).Plain, new DisplayText(value).Plain);
        if (state.Window.isActiveAndEnabled && state.Window.Text) Apply(state, state.Window.Text.LengthOfView);
        if (Trace) Log.LogInfo($"STORY translated rev={state.Slot.Revision} chars={state.TargetLength} ready={ready} frame={Time.frameCount} text={value}");
    }
    internal void Apply(WindowState state, int originalVisible)
    {
        if (state.Translation == null || !state.Window.Text) return;
        var renderer = state.Window.Text;
        if (renderer.text != state.Translation) renderer.text = state.Translation;
        renderer.LengthOfView = state.Alignment?.MapVisible(originalVisible, state.SourceLength, state.TargetLength)
            ?? DisplayText.MapVisible(originalVisible, state.SourceLength, state.TargetLength);
        // UTAGE placed the wait marker using the source count earlier in LateUpdate.
        // Re-evaluate its native anchoring after the translated display count is set.
        state.Window.LinkIcon();
        if (Trace && originalVisible != state.LastTraced)
        {
            state.LastTraced = originalVisible;
            Log.LogInfo($"DISPLAY rev={state.Slot.Revision} sourceVisible={originalVisible}/{state.SourceLength} targetVisible={renderer.LengthOfView}/{state.TargetLength} send={state.Window.Engine.Page.IsSendChar} wait={state.Window.Engine.Page.IsWaitInputInPage}");
        }
    }
}

internal sealed class WindowState
{
    public readonly AdvUguiMessageWindow Window;
    public readonly RevisionSlot Slot = new();
    public AdvMessageWindow? Model;
    public string Name = "";
    public string? Translation;
    public DisplayAlignment? Alignment;
    public int SourceLength, TargetLength;
    public int LastTraced = int.MinValue;
    public WindowState(AdvUguiMessageWindow window) => Window = window;
}

public sealed class MainThreadPump : MonoBehaviour
{
    private float nextProbe;
    public MainThreadPump(IntPtr pointer) : base(pointer) { }
    public void LateUpdate() => Plugin.Instance.Ui?.Tmp.ApplyLayouts();
    public void Update()
    {
        Plugin.Instance.Service.Drain();
        Plugin.Instance.Ui?.Tick();
#if TMP_REGRESSION_PROBE
        TmpRegressionProbe.Tick();
#endif
#if UI_LAYOUT_PROBE
        UiLayoutProbe.Tick();
#endif
        if (Plugin.Instance.Trace && Time.realtimeSinceStartup < 90 && Time.realtimeSinceStartup > nextProbe)
        {
            nextProbe = Time.realtimeSinceStartup + 10;
            Plugin.Instance.Log.LogInfo($"PROBE frame={Time.frameCount} scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} scale={Time.timeScale} windows={UnityEngine.Object.FindObjectsOfType<AdvUguiMessageWindow>().Length}");
        }
    }
}

[HarmonyPatch(typeof(AdvUguiMessageWindow), nameof(AdvUguiMessageWindow.OnTextChanged))]
internal static class TextChangedPatch
{
    private static void Postfix(AdvUguiMessageWindow __instance, AdvMessageWindow __0)
    { try { Plugin.Instance.Changed(__instance, __0); } catch (Exception ex) { Plugin.Instance.Log.LogError(ex); } }
}
[HarmonyPatch(typeof(AdvUguiMessageWindow), nameof(AdvUguiMessageWindow.LateUpdate))]
internal static class DisplayProgressPatch
{
    private static void Postfix(AdvUguiMessageWindow __instance)
    {
        try
        {
            if (Plugin.Instance.Windows.TryGetValue(__instance.GetInstanceID(), out var state))
                Plugin.Instance.Apply(state, __instance.Text.LengthOfView);
        }
        catch (Exception ex) { Plugin.Instance.Log.LogError(ex); }
    }
}
[HarmonyPatch(typeof(AdvUguiMessageWindow), nameof(AdvUguiMessageWindow.Clear))]
internal static class ClearPatch
{
    private static void Prefix(AdvUguiMessageWindow __instance)
    {
        if (Plugin.Instance.Windows.Remove(__instance.GetInstanceID(), out var state)) state.Slot.Invalidate();
    }
}
