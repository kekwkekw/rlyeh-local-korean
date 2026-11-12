using HarmonyLib;
using TMPro;
using UnityEngine;
using System.Text.Json;

namespace Rlyeh.Localizer;

// Diagnostic build only. All writes target synthetic offscreen text objects.
// Real game UI is observed at processing/render, without calling its text getter,
// forcing a mesh update, clicking controls or modifying game models.
internal static class TmpRegressionProbe
{
    private static readonly Dictionary<string, string> last = new();
    private static readonly Dictionary<int, bool> processingReady = new();
    private static int count;
    private static bool started;
    internal static void Record(TMP_Text text, string phase)
    {
        if (!text || count >= 24000) return;
        string raw = text.m_text ?? "", buffer = TmpBufferLocalizer.ReadBuffer(text) ?? "";
        string parsed = phase == "render" ? text.GetParsedText() ?? "" : "";
        if (!UiLocalizer.HasJapanese(raw + buffer + parsed) && !UiLocalizer.HasKorean(parsed)) return;
        int id = text.GetInstanceID();
        string context = UiLocalizer.Context(text);
        bool ready = UiLocalizer.HasJapanese(buffer) && Plugin.Instance.Service.TryGetReady(buffer, "ui", out _, context);
        if (phase == "buffer") processingReady[id] = ready;
        string key = id + phase, value = raw + "\u001f" + buffer + "\u001f" + parsed;
        if (last.TryGetValue(key, out var old) && old == value) return;
        last[key] = value;
        var faces = new HashSet<string>();
        if (phase == "render") for (int i = 0; i < text.textInfo.characterCount; i++)
        {
            var glyph = text.textInfo.characterInfo[i];
            if (glyph.character >= '가' && glyph.character <= '힣' && glyph.fontAsset)
                faces.Add(glyph.fontAsset.faceInfo.familyName + " " + glyph.fontAsset.faceInfo.styleName);
        }
        count++;
        Plugin.Instance.Log.LogInfo("COMMON_UI " + JsonSerializer.Serialize(new {
            frame = Time.frameCount, ms = Time.realtimeSinceStartup * 1000, phase, id, context, active = text.isActiveAndEnabled,
            mode = text.m_inputSource.ToString(), dirty = text.m_IsTextBackingStringDirty, raw, buffer, parsed, ready,
            readyAtProcessing = processingReady.GetValueOrDefault(id),
            protectedNickname = UiLocalizer.IsRankingNickname(text, buffer),
            width = text.rectTransform.rect.width, height = text.rectTransform.rect.height,
            fontSize = text.fontSize, overflow = text.isTextOverflowing, characters = text.textInfo.characterCount, faces
        }));
    }
    private static void Check(bool ok, string label)
    {
        Plugin.Instance.Log.LogInfo("TMP_REGRESSION " + (ok ? "PASS " : "FAIL ") + label);
        if (!ok) throw new InvalidOperationException(label);
    }
    internal static void Tick()
    {
        if (started || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "menu") return;
        var original = UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>().FirstOrDefault(x => x && x.font);
        if (!original) return;
        started = true;
        try { Run(original!); } catch (Exception ex) { Plugin.Instance.Log.LogError("TMP_REGRESSION " + ex); }
    }
    private static void Run(TMP_Text original)
    {
        string[] hooked = Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m)?.Owners.Contains("local.rlyeh.korean") == true)
            .Select(m => m.DeclaringType!.FullName + "." + m.Name).OrderBy(x => x).ToArray();
        Plugin.Instance.Log.LogInfo("TMP_HOOKS " + JsonSerializer.Serialize(hooked));
        Check(!hooked.Any(x => x == "TMPro.TMP_Text.set_text" || x == "TMPro.TextMeshProUGUI.OnEnable" ||
            x.Contains("Explore") || x.Contains("ServerRanking")), "no old TMP setter/enable or dispatch/ranking adapters");
        var root = new GameObject("Rlyeh_TMP_Regression_Offscreen");
        root.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var child = new GameObject("Text");
        child.transform.SetParent(root.transform, false);
        var text = child.AddComponent<TextMeshProUGUI>();
        text.rectTransform.anchoredPosition = new Vector2(-20000, -20000);
        text.font = original.font; text.fontSize = 24; text.rectTransform.sizeDelta = new Vector2(900, 300);
        void Render(string expected, string label)
        {
            string raw = text.m_text ?? "", backing = TmpBufferLocalizer.ReadBuffer(text) ?? "";
            var mode = text.m_inputSource; bool dirty = text.m_IsTextBackingStringDirty;
            text.ForceMeshUpdate(true);
            Check(text.GetParsedText() == expected, label + " parsed=" + text.GetParsedText());
            Check(text.m_text == raw && TmpBufferLocalizer.ReadBuffer(text) == backing && text.m_inputSource == mode &&
                text.m_IsTextBackingStringDirty == dirty, label + " preserves native source/mode/dirty");
        }
        text.text = "<scale=0.7>おまかせ装備";
        // String setters defer their first backing-array population until parsing.
        text.ParseInputText(); Render("자동 장비", "string setter and whole-label tag");
        text.SetText("闘技場に{0}回挑戦する", 987f); Render("투기장에 987회 도전하기", "formatted numeric SetText");
        text.SetCharArray(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<char>("闘技場に987回挑戦する".ToCharArray()));
        Render("투기장에 987회 도전하기", "char array");
        text.SetText(new Il2CppSystem.Text.StringBuilder("闘技場に987回挑戦する")); Render("투기장에 987회 도전하기", "StringBuilder");
        string arraySource = TmpBufferLocalizer.ReadBuffer(text)!;
        var size = text.GetPreferredValues("別の測定用文字", 400, 300);
        Check(size.x > 0 && size.y > 0 && TmpBufferLocalizer.ReadBuffer(text) == arraySource,
            "external sizing returns dimensions and preserves array-based binding");
        text.PopulateTextProcessingArray(); Render("투기장에 987회 도전하기", "array rendering after external sizing");
        size = text.GetPreferredValues("おまかせ装備");
        Check(size.x > 0 && size.y > 0 && TmpBufferLocalizer.ReadBuffer(text) == arraySource,
            "single-argument sizing preserves binding and measures a ready translation");
        Render("투기장에 987회 도전하기", "current processing restored after both sizing overloads");
        text.m_text = "原石"; text.SetText("{0}", 99358f); Render("99358", "stale Japanese placeholder with numeric backing");
        text.text = "<scale=0.9>すべて外す"; text.ParseInputText();
        text.GetPreferredValues("別の測定用文字", 100, 100);
        text.ParseInputText(); Render("모두 해제", "external measurement does not become UI binding");
        string cold = "検証用の古い表示" + DateTime.UtcNow.Ticks;
        text.text = cold; text.ParseInputText();
        // The completion queue cannot be drained until the following Update.
        text.SetText("{0}", 12345f); Render("12345", "pooled text rebound before late translation");
        Plugin.Instance.Service.Request(cold, "ui", (value, error) =>
        {
            try { Check(error == null && value != null, "real cold translation completed"); Render("12345", "late result cannot overwrite numeric rebind"); }
            finally { UnityEngine.Object.Destroy(root); }
        });
    }
}

[HarmonyPatch(typeof(TMP_Text), nameof(TMP_Text.PopulateTextProcessingArray))]
internal static class TmpRegressionBufferProbe
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(TMP_Text __instance) => TmpRegressionProbe.Record(__instance, "buffer");
}
[HarmonyPatch(typeof(TextMeshProUGUI), nameof(TextMeshProUGUI.Rebuild))]
internal static class TmpRegressionRenderProbe
{
    private static void Postfix(TextMeshProUGUI __instance, UnityEngine.UI.CanvasUpdate __0)
    {
        if (__0 == UnityEngine.UI.CanvasUpdate.PreRender && __instance.isActiveAndEnabled)
            TmpRegressionProbe.Record(__instance, "render");
    }
}
