using System.Text.Json;
using TMPro;
using UnityEngine;

namespace Rlyeh.Localizer;

// Opt-in diagnostic build only. Reads rendered UI metrics; never changes layout or game data.
internal static class UiLayoutProbe
{
    private static float next;
    private static readonly HashSet<string> seen = new();
    internal static void Tick()
    {
        if (Time.realtimeSinceStartup < next) return;
        next = Time.realtimeSinceStartup + 2;
        string path = Path.Combine(BepInEx.Paths.GameRootPath, "BepInEx", "plugins", "Rlyeh.Localizer", "cache", "ui-layout-probe.jsonl");
        foreach (var text in UnityEngine.Object.FindObjectsOfType<TMP_Text>())
        {
            if (!text || !text.isActiveAndEnabled || !text.text.Any(c => c >= '가' && c <= '힣')) continue;
            text.ForceMeshUpdate();
            var parts = new List<string>();
            for (Transform? t = text.transform; t && parts.Count < 32; t = t.parent) parts.Add(t.name);
            parts.Reverse(); string context = string.Join("/", parts);
            var rect = text.rectTransform.rect;
            var koreanFaces = new SortedSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < text.textInfo.characterCount; i++)
            {
                var glyph = text.textInfo.characterInfo[i];
                if (glyph.character >= '가' && glyph.character <= '힣' && glyph.fontAsset)
                    koreanFaces.Add(glyph.fontAsset.faceInfo.familyName + " " + glyph.fontAsset.faceInfo.styleName);
            }
            var row = new { context, text = text.text, parsed = text.GetParsedText(), fontSize = text.fontSize,
                min = text.fontSizeMin, max = text.fontSizeMax, auto = text.enableAutoSizing,
                width = rect.width, height = rect.height, renderedWidth = text.renderedWidth, renderedHeight = text.renderedHeight,
                lines = text.textInfo.lineCount, overflow = text.isTextOverflowing, overflowMode = text.overflowMode.ToString(),
                scale = text.rectTransform.lossyScale.y, characters = text.textInfo.characterCount, koreanFaces };
            string json = JsonSerializer.Serialize(row);
            if (seen.Count < 4000 && seen.Add(json)) File.AppendAllText(path, json + "\n");
        }
    }
}
