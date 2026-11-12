using Rlyeh.Localizer.Core;

internal static class UiDecorationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(UiDecoration.TryParse("<scale=0.7>おまかせ装備", out var label) && label.Body == "おまかせ装備" &&
            label.TryRestore("자동 장비", out string result) && result == "<scale=0.7>자동 장비", "unclosed whole-label scale preserves tag and reuses text");
        check(UiDecoration.TryParse("<color=#ffff1e><size=80%>5階</size></color>", out var nested) &&
            nested.TryRestore("5층", out _), "nested whole-label presentation preserves order and values");
        check(!nested.TryRestore("6층", out _) && !label.TryRestore("<sprite=1>장비", out _), "decoration restore rejects numeric changes and added control tags");
        foreach (string source in new[] { "装備<scale=0.7>します", "<color=red>一部</color>だけ", "<link=毒>毒</link>",
            "<sprite=1>武器", "<interval=0.8>話", "<unknown=1>文", "<b>文字</i>", "<scale=oops>文字", "<color=not-a-color>文字" })
            check(!UiDecoration.TryParse(source, out _), "unsafe/partial UI decoration keeps full source: " + source);
        check(!new DisplayText("<scale=0.7>装備", ui: true).HasUnsupportedTag &&
            new DisplayText("<scale=0.7>装備").HasUnsupportedTag, "new TMP tags are UI-only; story policy unchanged");
        var doc = new DisplayText("最初<scale=0.7>装備</scale><cspace=0.2em>次</cspace>", ui: true);
        string translated = doc.Restore("처음 장비 다음");
        TextCatalog.ValidateControls("最初<scale=0.7>装備</scale><cspace=0.2em>次</cspace>", translated);
        check(!doc.HasUnsupportedTag && doc.Plain == "最初装備次" && doc.UsesUiExtensions, "inline UI tags remain one complete request with control stream preserved");
        check(new DisplayText("<unknown-extension=1>文", ui: true).HasUnsupportedTag, "unknown hyphenated UI tags are rejected");
    }
    internal static void Service(TranslationService service, Action<bool, string> check)
    {
        check(service.TryGetReady("<scale=0.7>おまかせ装備", "ui", out string equipment) && equipment == "<scale=0.7>자동 장비",
            "real reviewed artifact button is ready without engine initialization");
        check(service.TryGetReady("<scale=0.9>すべて外す", "ui", out string remove) && remove == "<scale=0.9>모두 해제",
            "real reviewed remove button reuses undecorated catalog");
        check(service.TryGetReady("<scale=0.8>編成リセット", "ui", out string reset) && reset == "<scale=0.8>편성 초기화",
            "already-reviewed decorated exact remains available");
    }
}
