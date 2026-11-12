using HarmonyLib;
using Rog.View;
using TMPro;

namespace Rlyeh.Localizer;

[HarmonyPatch(typeof(GachaDrawConfirmPage), nameof(GachaDrawConfirmPage.RefreshView))]
internal static class GachaConfirmText
{
    // These formatted fields have a dedicated adapter. The generic TMP buffer path must
    // not send a pending monetary confirmation to the sentence translator.
    internal static bool Owns(TMP_Text text)
    {
        var page = text.GetComponentInParent<GachaDrawConfirmPage>();
        return page && (page.m_text == text || (page.m_alertLabel && page.m_alertLabel.LabelText == text));
    }
    private static void Postfix(GachaDrawConfirmPage __instance)
    {
        var ui = Plugin.Instance.Ui;
        if (ui == null) return;
        string? name = __instance.m_gachaButton?.CostWallet?.DisplayName;
        // Match the game's actual output, never infer a branch from ticket IDs,
        // cost, availability, or a banner title. Unrecognized templates stay native.
        if (!string.IsNullOrEmpty(name))
        {
            ui.ApplyBoundFormat(__instance.m_text, cMessageExpand.GetString(eMessage.GACHA_CONFIRM), name, 2);
            if (__instance.m_alertLabel)
                ui.ApplyBoundFormat(__instance.m_alertLabel.LabelText, cMessageExpand.GetString(eMessage.GACHA_SHORTAGE), name, 0);
        }
        ui.ApplyBoundFormat(__instance.m_text, cMessageExpand.GetString(eMessage.GACHA_CONFIRM_FREE), null, 1);
    }
}
