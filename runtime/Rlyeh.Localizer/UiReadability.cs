using Rog.View;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Rlyeh.Localizer;

// Existing game views only: wrap narrow labels and let native layouts make room for Korean.
// No replacement renderer, text edits, game IDs, or fixed screen coordinates.
internal static class UiReadability
{
    private sealed record Size(RectTransform Rect, float Height);
    private static readonly Dictionary<int, Size> sizes = new();
    private static readonly Dictionary<int, (TMP_Text Text, float Height)> requests = new();
    internal static void PruneDestroyedViews()
    {
        foreach (int id in sizes.Where(pair => !pair.Value.Rect).Select(pair => pair.Key).ToArray()) sizes.Remove(id);
        foreach (int id in requests.Where(pair => !pair.Value.Text).Select(pair => pair.Key).ToArray()) requests.Remove(id);
    }
    private static float Original(RectTransform rect)
    {
        int id = rect.GetInstanceID();
        if (!sizes.TryGetValue(id, out var saved) || !saved.Rect)
            sizes[id] = saved = new(rect, rect.rect.height);
        return saved.Height;
    }
    private static bool Same(TMP_Text? a, TMP_Text b) => a is not null && a && a.GetInstanceID() == b.GetInstanceID();
    private static void Height(RectTransform rect, float height)
    {
        if (Math.Abs(rect.rect.height - height) > 0.5f)
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
    }
    internal static void Apply(TMP_Text text)
    {
        if (!text || !text.isActiveAndEnabled || text.rectTransform.rect.width < 50) return;
        var button = text.GetComponentInParent<UnitAwakePotentialContent>();
        if (button && Same(button.m_awakeText, text))
        {
            // The shipped button is NoWrap: a long name shrank the entire two-line label to 10pt.
            text.textWrappingMode = TextWrappingModes.Normal;
            text.lineSpacing = 0;
            FitRenderedLabel(text);
            return;
        }
        var active = text.GetComponentInParent<UnitDetailActiveSkillParamsView>();
        if (active && Same(active.m_descriptionText, text)) { ScrollBody(text, active); return; }
        var passive = text.GetComponentInParent<UnitDetailPassiveSkillParamsView>();
        if (passive && Same(passive.m_descriptionText, text)) { ScrollBody(text, passive); return; }
        var special = text.GetComponentInParent<UnitDetailSpecialSkillParamsView>();
        if (special && Same(special.m_descriptionText, text)) { ScrollBody(text, special); return; }
        var potential = text.GetComponentInParent<UnitDetailPotentialContentView>();
        if (potential && Same(potential.m_descriptionText, text)) { ScrollBody(text, potential); return; }
        var activeDetail = text.GetComponentInParent<ActiveSkillDetailView>();
        if (activeDetail && Same(activeDetail.m_skillText, text)) { ComparisonBody(text, activeDetail); return; }
        var passiveDetail = text.GetComponentInParent<PassiveSkillDetailView>();
        if (passiveDetail && Same(passiveDetail.m_skillText, text)) ComparisonBody(text, passiveDetail);
    }
    private static float Measure(TMP_Text text)
    {
        text.enableAutoSizing = false;
        text.fontSize = text.fontSizeMax > 0 ? Math.Min(text.fontSizeMax, 17) : 17;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.ForceMeshUpdate();
        // TMP preferred height can undercount Korean fallback lines. Measure the actual glyph mesh too.
        return Mathf.Ceil(Math.Max(text.GetPreferredValues(text.rectTransform.rect.width, float.PositiveInfinity).y,
            text.renderedHeight + text.margin.y + text.margin.w)) + 4;
    }
    private static void FitRenderedLabel(TMP_Text text)
    {
        text.enableAutoSizing = false;
        text.fontSize = text.fontSizeMax;
        float width = text.rectTransform.rect.width - text.margin.x - text.margin.z;
        float height = text.rectTransform.rect.height - text.margin.y - text.margin.w - 8;
        if (width <= 0 || height <= 0) return;
        for (int i = 0; i < 5; i++)
        {
            text.ForceMeshUpdate();
            float ratio = Math.Min(width / Math.Max(1, text.renderedWidth), height / Math.Max(1, text.renderedHeight));
            if (ratio >= 1) return;
            text.fontSize = Math.Max(text.fontSizeMin, Mathf.Floor(text.fontSize * ratio * 19.8f) / 20);
        }
        text.ForceMeshUpdate();
    }
    private static void ScrollBody(TMP_Text text, Component view)
    {
        var scroll = view.GetComponentInParent<ScrollRect>();
        var root = view.GetComponent<RectTransform>();
        if (!scroll || !scroll.content || !root || root.rect.height < 1 || !root.IsChildOf(scroll.content)) return;
        // Follow vertically stretched wrappers to the fixed-height row owned by this view.
        var row = text.rectTransform;
        while (row != root && Math.Abs(row.anchorMax.y - row.anchorMin.y) > 0.01f)
        {
            var parent = row.parent?.TryCast<RectTransform>();
            if (parent is null || !parent) return;
            row = parent;
        }
        if (row != root && !row.IsChildOf(root)) return;
        float baseRow = Original(row), baseRoot = Original(root);
        float required = Math.Max(baseRow, Measure(text));
        Height(row, required);
        if (row != root) Height(root, baseRoot + required - baseRow);
        LayoutRebuilder.MarkLayoutForRebuild(scroll.content);
    }
    private static void ComparisonBody(TMP_Text text, Component detail)
    {
        var popup = detail.GetComponentInParent<UnitAwakeSkillConfirmPopup>();
        var skill = text.GetComponentInParent<LayoutElement>();
        var layout = skill ? skill.transform.parent.GetComponent<VerticalLayoutGroup>() : null;
        if (!popup || !skill || layout is null || !layout) return;
        Component? panel = detail.GetComponentInParent<ReinforcePassiveSkillView>();
        if (!panel) panel = detail.GetComponentInParent<ReinforceOverSkillView>();
        if (!panel) panel = detail.GetComponentInParent<AcquirePassiveSkillView>();
        if (panel is null || !panel) return;
        var panelRect = panel.GetComponent<RectTransform>();
        var frame = panel.transform.parent?.parent?.TryCast<RectTransform>();
        var popupRect = popup.GetComponent<RectTransform>();
        var detailRect = detail.GetComponent<RectTransform>();
        var layoutRect = layout.GetComponent<RectTransform>();
        var skillRect = skill.GetComponent<RectTransform>();
        if (frame is null || !frame || !frame.GetComponent<ContentSizeFitter>() || frame.rect.height < 100) return;
        float baseText = Original(text.rectTransform), baseSkill = Original(skillRect);
        float required = Math.Max(baseText, Measure(text));
        // Preserve the popup's native buttons and keep its grown frame within the parent canvas.
        float room = Math.Max(0, popupRect.rect.height - Original(frame) - 32);
        float extra = Math.Min(required - baseText, room);
        requests[text.GetInstanceID()] = (text, extra);
        float panelExtra = extra;
        foreach (var request in requests.Values)
            if (request.Text && request.Text.transform.IsChildOf(panel.transform)) panelExtra = Math.Max(panelExtra, request.Height);
        float baseLayout = Original(layoutRect), baseDetail = Original(detailRect), basePanel = Original(panelRect);
        skill.minHeight = baseSkill + extra;
        Height(layoutRect, baseLayout + extra);
        Height(detailRect, baseDetail + extra);
        Height(panelRect, basePanel + panelExtra);
        // If an exceptional future description cannot fit the available canvas, native auto-size remains.
        text.enableAutoSizing = true;
        LayoutRebuilder.MarkLayoutForRebuild(frame);
    }
}
