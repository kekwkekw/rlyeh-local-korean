using Rlyeh.Localizer.Core;
using System.Text.Json;

internal static class SummonFormatChecks
{
    internal static void Run(TextCatalog catalog, Action<bool, string> check)
    {
        const string paid = "<color=#ffff1e>{0}を{1}個</color>消費してガチャを<color=#ffff1e>{2}回</color>引きます\r\nよろしいですか?";
        const string free = "召喚を<color=#ffff1e>{0}回</color>引きます\r\nよろしいですか?";
        const string shortage = "※{0}が不足しています";
        using var fixture = JsonDocument.Parse(File.ReadAllText("tests/summon-format-fixtures.json"));
        int count = 0;
        foreach (var row in fixture.RootElement.EnumerateArray())
        {
            string name = row.GetProperty("name").GetString()!;
            string target = row.GetProperty("target").GetString()!;
            string cost = row.GetProperty("cost").GetString()!;
            string draws = row.GetProperty("draws").GetString()!;
            if (!catalog.TryUi(name, null, out var translatedName) || translatedName != target)
                throw new Exception("Wallet review missing: " + name);
            string source = string.Format(paid, name, cost, draws);
            string expected = $"<color=#ffff1e>{target} {cost}개</color>로 <color=#ffff1e>{draws}회</color> 뽑습니다.\r\n진행할까요?";
            if (!BoundUiFormat.TryBind(catalog, paid, source, name, 2, out var bound) ||
                !bound.TryRender(target, out string actual) || actual != expected) throw new Exception("Summon: " + name);
            if (!BoundUiFormat.TryBind(catalog, shortage, string.Format(shortage, name), name, 0, out bound) ||
                !bound.TryRender(target, out actual) || actual != "※ " + target + " 부족") throw new Exception("Shortage: " + name);
            count += 2;
        }
        check(count > 0, "gem and 13 ticket names, amounts, draw counts and shortages: " + count);
        check(BoundUiFormat.TryBind(catalog, free, string.Format(free, "10"), null, 1, out var freeBound) &&
            freeBound.TryRender(null, out var freeResult) && freeResult == "<color=#ffff1e>10회</color> 뽑습니다.\r\n진행할까요?", "free summon uses its own reviewed format");
        string native = string.Format(paid, "召喚券(邪神祭2)", "3", "30");
        check(!BoundUiFormat.TryBind(catalog, paid, native, "召喚券(ピックアップ)", 2, out _), "a different banner wallet cannot bind");
        check(!BoundUiFormat.TryBind(catalog, paid, native + "追加", "召喚券(邪神祭2)", 2, out _) &&
            !BoundUiFormat.TryBind(catalog, paid, native.Replace("#ffff1e", "#ffffff"), "召喚券(邪神祭2)", 2, out _) &&
            !BoundUiFormat.TryBind(catalog, paid, native.Replace("を3個", "を全部個"), "召喚券(邪神祭2)", 2, out _), "changed format, emphasis or nonnumeric amount stays native");
        BoundUiFormat.TryBind(catalog, paid, native, "召喚券(邪神祭2)", 2, out var guarded);
        check(!guarded.TryRender("소환권(사신제 3)", out _) && !guarded.TryRender("<b>소환권(사신제 2)</b>", out _), "name translation cannot change embedded numbers or introduce tags");
        string future = "新しい召喚券(2099)";
        check(BoundUiFormat.TryBind(catalog, paid, string.Format(paid, future, "2", "20"), future, 2, out var futureBound) &&
            futureBound.TryRender("새 소환권(2099)", out var futureResult) && futureResult.Contains("2개") && futureResult.Contains("20회"), "new wallet names work without a hardcoded name list");
        check(!BoundUiFormat.TryBind(new TextCatalog(new TextCatalog.Document()), paid, native, "召喚券(邪神祭2)", 2, out _), "unreviewed native template cannot be guessed");
    }
}
