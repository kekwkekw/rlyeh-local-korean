using Rlyeh.Localizer.Core;
using System.Text.Json;

void Check(bool result, string label) { if (!result) throw new Exception(label); Console.WriteLine("PASS " + label); }
UiDecorationChecks.Run(Check);
var doc = new DisplayText("<color=red>こんにちは</color><interval=0.2>、<speed=2>世界！");
Check(doc.Plain == "こんにちは、世界！", "one complete request without control tags");
string restored = doc.Restore("안녕하세요, 세계!");
Check(restored.Contains("<interval=0.2>") && restored.Contains("<speed=2>") && restored.StartsWith("<color=red>") && restored.Contains("</color>"), "display tags retained");
Check(new DisplayText("1 < 2 と 3 > 1").Plain == "1 < 2 と 3 > 1", "literal angle brackets preserved");
Check(new DisplayText("<unknown=1>文").HasUnsupportedTag, "unknown tag flagged");
var multiline = new DisplayText("ルルイエの散策中、チェイシィを見かけた。<interval=0.8>\n物陰に隠れて何やら様子を窺っているようだが……。");
string korean = "루루이에의 산책 중, 체이시를 보았다.\n그늘에 숨어 무엇인가 모습을 살피고 있는 것 같지만…….";
Check(multiline.Restore(korean).Contains("보았다.<interval=0.8>\n"), "real story interval remains at its hard line boundary");
var aligned = new DisplayAlignment(multiline.Plain, korean);
Check(aligned.MapVisible(20, 45, 53) == 21 && aligned.MapVisible(21, 45, 53) == 22, "native pause maps to translated line end");
Check(aligned.MapVisible(45, 45, 53) == -1, "multiline completion fully visible");
int previousAligned = 0;
for(int i=0;i<45;i++) { int v=aligned.MapVisible(i,45,53); if(v<previousAligned || v>53)throw new Exception("non-monotonic aligned visibility"); previousAligned=v; }
Check(true, "aligned visibility stays monotonic");
for (int source = 1; source < 100; source++) for (int target = 1; target < 140; target++)
{
    int last = 0;
    for (int i = 0; i < source; i++) { int value = DisplayText.MapVisible(i, source, target); if (value < last || value > target) throw new Exception("non-monotonic"); last = value; }
    if (DisplayText.MapVisible(source, source, target) != -1) throw new Exception("clipped final line");
}
Check(DisplayText.MapVisible(-1, 20, 60) == -1, "instant display and long translations fully visible");
var slot = new RevisionSlot(); long first = slot.Begin("same line"); long second = slot.Begin("next");
Check(!slot.Accepts(first, "same line") && slot.Accepts(second, "next"), "late response rejected");
slot.Begin("same line"); Check(!slot.Accepts(first, "same line"), "ABA repeated dialogue rejected");
slot.Invalidate(); Check(!slot.Accepts(second, "next"), "cleared page invalidates pending work");
TextCatalog.Entry Reviewed(string id, string scope, string source, string target, string kind = "exact") => new()
{ Id = id, Scope = scope, Source = source, Target = target, SourceHash = TextCatalog.Hash(source), Reviewer = "test", Kind = kind };
var rule = Reviewed("mission", "ui", "闘技場に{{n}}回挑戦する", "투기장에 {{n}}회 도전하기", "numeric");
rule.Slots["n"] = "integer";
var catalog = new TextCatalog(new TextCatalog.Document { Entries = new() {
    rule, Reviewed("context", "ui:scene/menu", "無", "없음"),
    Reviewed("master", "master:OverSkillMasterData:5:text", "[duration]秒間、攻撃力+[0,0,0]%", "[duration]초 동안 공격력 +[0,0,0]%") } });
Check(catalog.TryUi("闘技場に５回挑戦する", null, out var mission) && mission == "투기장에 ５회 도전하기", "numeric template keeps actual full-width value");
Check(!catalog.TryUi("闘技場に全部回挑戦する", null, out _) && !catalog.TryUi("闘技場に5回挑戦する追加", null, out _), "numeric templates require complete typed match");
Check(catalog.TryUi("無", "scene/menu", out var contextual) && contextual == "없음" && !catalog.TryUi("無", "scene/battle", out _), "context-specific exact cannot leak to another screen");
Check(catalog.TryExact("master:OverSkillMasterData:5:text", "[duration]秒間、攻撃力+[0,0,0]%", out _) && !catalog.TryExact("master:OverSkillMasterData:5:text", "[duration]秒間、攻撃力+[0,1,0]%", out _), "same master ID with changed source cannot reuse old translation");
string NativeValue(string token) => token == "[duration]" ? "15" : "200";
Check(catalog.TryRenderMaster("master:OverSkillMasterData:5:text", "[duration]秒間、攻撃力+[0,0,0]%", "15秒間、攻撃力+200%", NativeValue, out var skill) && skill == "15초 동안 공격력 +200%", "master template reuses native-computed values");
Check(!catalog.TryRenderMaster("master:OverSkillMasterData:5:text", "[duration]秒間、攻撃力+[0,0,0]%", "15秒間、攻撃力+201%", NativeValue, out _), "unexpected native formatting or values decline reviewed replacement");
var conditionalSource = "[skilllevel,1,3,<link=毒>毒</link>+[0,0,0]%]";
var conditionalTarget = "[skilllevel,1,3,발동 시, <link=毒>독</link> +[0,0,0]%]";
var conditionalCatalog = new TextCatalog(new TextCatalog.Document { Entries = new() {
    Reviewed("conditional", "master:AbilityMasterData:9:text", conditionalSource, conditionalTarget),
    Reviewed("collision", "master:AbilityMasterData:9:text", "別の能力", "다른 능력") } });
string Enabled(string token) => token == "[0,0,0]" ? "30" : token.Contains('독') ? throw new Exception("Korean must not reach native condition parser") : token[16..^1];
string Disabled(string token) => token == "[0,0,0]" ? "30" : "";
Check(conditionalCatalog.TryRenderMaster("master:AbilityMasterData:9:text", conditionalSource, "<link=毒>毒</link>+30%", Enabled, out var cond) && cond == "발동 시, <link=毒>독</link> +30%", "native condition evaluates original only; Korean commas cannot hide a clause");
Check(conditionalCatalog.TryRenderMaster("master:AbilityMasterData:9:text", conditionalSource, "", Disabled, out cond) && cond == "", "inactive conditional text stays empty");
Check(conditionalCatalog.TryRenderMasterDiff("master:AbilityMasterData:9:text", conditionalSource, "<color=#00C0FF><link=毒>毒</link>+30%</color>", Enabled, Disabled, "<color=#00C0FF>{0}</color>", out cond) && cond == "<color=#00C0FF>발동 시, <link=毒>독</link> +30%</color>", "newly unlocked condition keeps native whole-clause emphasis");
Check(conditionalCatalog.TryExact("master:AbilityMasterData:9:text", "別の能力", out var collision) && collision == "다른 능력", "overlapping ability IDs are isolated by exact original source");
var malformedSource = "[skilllevel,1,3,対象+1]\r\n[skilllevel,1,3凍結+[0,0,0]％]";
var malformedTarget = "[skilllevel,1,3,대상 +1]\r\n[skilllevel,1,3凍結+[0,0,0]％]";
var malformedCatalog = new TextCatalog(new TextCatalog.Document { Entries = new() {
    Reviewed("malformed", "master:AbilityMasterData:32:text", malformedSource, malformedTarget) } });
string SuppressedMalformed(string token) => token == "[0,0,0]" ? "30" : token.StartsWith("[skilllevel,1,3,") ? "対象+1" : "";
Check(malformedCatalog.TryRenderMaster("master:AbilityMasterData:32:text", malformedSource, "対象+1\r\n", SuppressedMalformed, out var malformed) && malformed == "대상 +1\r\n", "native-suppressed malformed clause stays suppressed without repairing game data");
Check(!malformedCatalog.TryRenderMaster("master:AbilityMasterData:32:text", malformedSource, "対象+1\r\n凍結+30％", t => t == "[skilllevel,1,3凍結+30％]" ? "凍結+30％" : SuppressedMalformed(t), out _), "new native behavior for malformed clause declines replacement");
var conflicting = Reviewed("other", "ui", rule.Source, "결과 {{n}}", "numeric"); conflicting.Slots["n"] = "integer";
var ambiguous = new TextCatalog(new TextCatalog.Document { Entries = new() { rule, conflicting } });
Check(!ambiguous.TryUi("闘技場に5回挑戦する", null, out _), "ambiguous templates decline rather than selecting first");
bool refused = false;
try { TextCatalog.ValidateControls("[skilllevel,1,3,<link=毒>毒</link>を3秒付与]", "[skilllevel,1,3,<link=독>독</link>을3초 부여]"); }
catch (InvalidDataException) { refused = true; }
Check(refused, "conditional block and Japanese link identifiers are protected");
refused = false;
try { TextCatalog.ValidateControls("{0}文字まで入力可能", "0자까지 입력 가능"); }
catch (InvalidDataException) { refused = true; }
Check(refused, "composite formatting placeholders cannot become literal numbers");
if (args.Length == 3 && args[0] == "--native-catalog")
{
    var nativeCatalog = TextCatalog.Load(args[2]);
    using var fixtures = JsonDocument.Parse(File.ReadAllText(args[1]));
    int count=0;
    foreach (var row in fixtures.RootElement.EnumerateArray())
    {
        string Get(string key) => row.GetProperty(key).GetString()!;
        string Resolve(string token) => row.GetProperty("values").GetProperty(token).GetString()!;
        string Compare(string token) => row.GetProperty("comparisonValues").GetProperty(token).GetString()!;
        bool hit=Get("mode")=="text" ? nativeCatalog.TryRenderMaster(Get("scope"),Get("source"),Get("native"),Resolve,out var target) :
            nativeCatalog.TryRenderMasterDiff(Get("scope"),Get("source"),Get("native"),Resolve,Compare,Get("format"),out target);
        if (!hit || !row.GetProperty("hit").GetBoolean() || target != Get("target"))
            throw new Exception("Native replay mismatch: "+Get("scope")+" "+Get("mode")+" "+row.GetProperty("level"));
        TextCatalog.ValidateControls(Get("native"),target);
        count++;
    }
    Check(count>0,"native display/upgrade fixtures replayed: "+count);
}
else if ((args.Length == 3 || args.Length == 4) && args[0] == "--service")
{
    var messages = new List<string>();
    var service = new TranslationService(args[1], args[2], s => { messages.Add(s); Console.WriteLine(s); }, 50);
    SummonFormatChecks.Run(service.Catalog, Check);
    UiDecorationChecks.Service(service, Check);
    var results = new Dictionary<string, string>();
    int remaining = 8;
    service.Request("<scale=0.7>今日はいい天気ですね。", "ui", (value,error) => { if(error!=null)throw new Exception(error); results["decorated"]=value!; remaining--; });
    service.Request("闘技場に987回挑戦する", "ui", (value,error) => { if(error!=null)throw new Exception(error); results["mission"]=value!; remaining--; }, "test/mission");
    foreach (var (source, scope) in new[] { ("――ルルイエへ。", "story-test"), ("ネオン", "speaker"), ("ルルイエのヴァイス。", "story-test"), ("は い", "ui") })
    {
        service.Request(source, scope, (value, error) => { if(error!=null)throw new Exception(error); results[source]=value!; remaining--; });
    }
    var active = new RevisionSlot();
    long old = active.Begin("古い台詞");
    bool staleApplied = false;
    service.Request("古い台詞", "story-test", (value, error) => { if(active.Accepts(old,"古い台詞")) staleApplied=true; remaining--; });
    active.Begin("現在の台詞");
    const string futureWallet = "新しい召喚券(2099)";
    const string summonTemplate = "<color=#ffff1e>{0}を{1}個</color>消費してガチャを<color=#ffff1e>{2}回</color>引きます\r\nよろしいですか?";
    Check(BoundUiFormat.TryBind(service.Catalog, summonTemplate, string.Format(summonTemplate, futureWallet, "3", "30"), futureWallet, 2, out var futureSummon), "future wallet binds to the original confirmation");
    remaining++;
    service.Request(futureWallet, "ui", (value, error) =>
    {
        Check(error == null && futureSummon.TryRender(value, out var text) && text.Contains("3개") && text.Contains("30회") && text.Contains("2099"), "real hdTrans name translation preserves confirmation quantities");
        remaining--;
    });
    service.Request("<size=32>ルルイエ</size><interval=0.8>へ行こう。", "story-test", (value,error) => { if(error!=null)throw new Exception(error); results["tagged"]=value!; remaining--; });
    using var reviewRequests = JsonDocument.Parse(args.Length == 4 ? File.ReadAllText(args[3]) : "[]");
    foreach (var request in reviewRequests.RootElement.EnumerateArray())
    {
        var item=request.Clone(); string key="review:"+item.GetProperty("id").GetString(); remaining++;
        service.Request(item.GetProperty("source").GetString()!,item.GetProperty("scope").GetString()!,
            (value,error)=> { if(error!=null)throw new Exception(error); results[key]=value!; remaining--; });
    }
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while(remaining>0 && DateTime.UtcNow<deadline) { service.Drain(); Thread.Sleep(10); }
    Check(remaining==0, "real worker completed all requests");
    Check(!service.TryGetReady("――ルルイエへ。", "story-test", out _), "diagnostic delay keeps warm story text asynchronous");
    Check(!staleApplied,"delayed real translation cannot overwrite newer dialogue");
    Check(results["mission"] == "투기장에 987회 도전하기", "installed reviewed numeric rule precedes real hdTrans");
    Check(results["ネオン"]=="네온", "canonical speaker exact override");
    Check(results["は い"]=="예", "reviewed UI exact override handles spaced confirmation label");
    Check(results["――ルルイエへ。"].Contains("루루이에"),"canonical city name used inside sentence");
    Check(results["ルルイエのヴァイス。"].Contains("바이스"),"canonical hdTrans user dictionary loaded");
    Check(results["tagged"].Contains("<interval=0.8>") && results["tagged"].Contains("루루이에"), "tagged complete line translated and controls restored");
    Check(results["decorated"].StartsWith("<scale=0.7>") && results["decorated"].Any(c => c >= '가' && c <= '힣'), "cold decorated UI uses local engine and restores original presentation");
    foreach (var request in reviewRequests.RootElement.EnumerateArray())
    {
        string key="review:"+request.GetProperty("id").GetString();
        Check(request.TryGetProperty("exact",out var exactValue) ? results[key]==exactValue.GetString() :
            results[key].Contains(request.GetProperty("contains").GetString()!,StringComparison.Ordinal), key);
    }
    foreach(var result in results) Console.WriteLine(result.Key+" => "+result.Value);
}
else if (args.Length == 3 && args[0] == "--warm-service")
{
    bool initialized = false;
    var service = new TranslationService(args[1], args[2], message =>
    {
        if (message.StartsWith("hdTrans ready ", StringComparison.Ordinal)) initialized = true;
        Console.WriteLine(message);
    });
    Check(service.TryGetReady("サーバー内で初めて総合戦力が\r\n2750000以上に到達", "ui", out string rank) &&
        rank == "서버 최초 총 전투력\r\n2750000 이상 달성", "reviewed rank number is available synchronously at UI binding");
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (!initialized && DateTime.UtcNow < deadline) { service.Drain(); Thread.Sleep(5); }
    Check(initialized, "warm service loaded the persisted dictionary revision");
    UiDecorationChecks.Service(service, Check);
    Check(service.TryGetReady("<scale=0.9>今日はいい天気ですね。", "ui", out string decoratedUi) && decoratedUi.StartsWith("<scale=0.9>") && decoratedUi.Any(c => c >= '가' && c <= '힣'), "different whole-label presentation reuses persisted UI body cache");
    Check(!service.TryGetReady("<scale=0.7>ネオン", "ui", out _), "decorated UI cannot consume speaker-only exact overrides");
    Check(service.TryGetReady("――ルルイエへ。", "story-test", out string cached) && cached.Contains("루루이에"),
        "persisted hdTrans text is available without a request or callback");
    Check(!service.TryGetReady("――ルルイエへ。", "different-scope", out _),
        "synchronous cache preserves translation scope");
    Check(service.TryGetReady("ネオン", "speaker", out string speaker) && speaker == "네온",
        "canonical speaker is available synchronously");
}
else if (args.Length == 2)
{
    using var engine = new HdTransEngine(args[0], args[1]);
    string translated = engine.Translate("今日はいい天気ですね。");
    Check(translated.Any(c => c >= '가' && c <= '힣'), "real local hdTrans C API: " + translated);
}
