using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rlyeh.Localizer.Core;

// Local reviewed text only. No engine calls, Unity objects, or filesystem writes during lookup.
public sealed class TextCatalog
{
    public sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Scope { get; set; } = "";
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public string SourceHash { get; set; } = "";
        public string Reviewer { get; set; } = "";
        public string Kind { get; set; } = "exact";
        public Dictionary<string, string> Slots { get; set; } = new();
    }
    public sealed class Document
    {
        public int Version { get; set; } = 1;
        public List<Entry> Entries { get; set; } = new();
    }
    private sealed record Rule(Entry Entry, Regex Pattern);
    private readonly Dictionary<(string, string), string> exact = new();
    private readonly Dictionary<string, List<Rule>> rules = new();
    private static readonly Regex Slots = new(@"\{\{([a-z][a-z0-9_]*)\}\}", RegexOptions.CultureInvariant);
    // CJK headings in angle brackets are visible prose. ASCII renderer tags stay protected.
    private static readonly Regex Controls = new(@"<(?=[A-Za-z/#?!])[^>]+>|(?<!\{)\{\d+(?:,[+-]?\d+)?(?::[^{}]*)?\}(?!\})|\[skilllevel,\d+,\d+,|\[(?:\d+(?:,\d+)+|duration|(?:SEC|MAG),\d+,\d+)\]|\]", RegexOptions.CultureInvariant);
    private static readonly Regex Numbers = new(@"[0-9０-９]+(?:[.,．][0-9０-９]+)*", RegexOptions.CultureInvariant);
    private static readonly Regex MasterParameters = new(@"\[(?:\d+(?:,\d+)+|duration|(?:SEC|MAG),\d+,\d+)\]", RegexOptions.CultureInvariant);
    private static readonly Regex MasterConditions = new(@"\[(skilllevel,\d+,\d+,)([^\[\]]*)\]", RegexOptions.CultureInvariant);
    private static readonly Regex ConditionBlocks = new(@"\[skilllevel,[^\[\]]*\]", RegexOptions.CultureInvariant);
    public static string Hash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    public static TextCatalog Load(string path) => !File.Exists(path) ? new(new Document()) :
        new(JsonSerializer.Deserialize<Document>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty text catalog"));
    public int Count { get; }

    public TextCatalog(Document document)
    {
        if (document.Version != 1) throw new InvalidDataException("Unsupported text catalog version");
        var ids = new HashSet<string>();
        foreach (var e in document.Entries)
        {
            if (!ids.Add(e.Id) || string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Reviewer) ||
                e.SourceHash != Hash(e.Source) || e.Source.Length > 12000 || e.Target.Length > 16000 ||
                string.IsNullOrWhiteSpace(e.Target) || e.Source.Contains('\0') || e.Target.Contains('\0') ||
                !(e.Scope == "ui" || e.Scope.StartsWith("ui:", StringComparison.Ordinal) || e.Scope.StartsWith("master:", StringComparison.Ordinal)))
                throw new InvalidDataException("Invalid reviewed text: " + e.Id);
            ValidateControls(e.Source, e.Target);
            if (e.Kind == "exact")
            {
                if (e.Slots.Count != 0) throw new InvalidDataException("Slots on exact entry: " + e.Id);
                if (exact.TryGetValue((e.Scope, e.Source), out var prior) && prior != e.Target)
                    throw new InvalidDataException("Conflicting exact text: " + e.Id);
                exact[(e.Scope, e.Source)] = e.Target;
            }
            else if (e.Kind == "numeric" && (e.Scope == "ui" || e.Scope.StartsWith("ui:", StringComparison.Ordinal)))
            {
                var sourceSlots = Slots.Matches(e.Source).Select(m => m.Groups[1].Value).ToArray();
                var targetSlots = Slots.Matches(e.Target).Select(m => m.Groups[1].Value).ToArray();
                if (sourceSlots.Length == 0 || !sourceSlots.OrderBy(s => s).SequenceEqual(targetSlots.OrderBy(s => s)) ||
                    !sourceSlots.Distinct().OrderBy(s => s).SequenceEqual(e.Slots.Keys.OrderBy(s => s)))
                    throw new InvalidDataException("Placeholder mismatch: " + e.Id);
                var pattern = new StringBuilder(@"\A"); int cursor = 0; var seen = new HashSet<string>();
                foreach (Match m in Slots.Matches(e.Source))
                {
                    pattern.Append(Regex.Escape(e.Source[cursor..m.Index]));
                    string slot = m.Groups[1].Value;
                    if (seen.Add(slot))
                    {
                        string capture = e.Slots[slot] switch
                        {
                            "integer" => @"[0-9０-９]+",
                            "number" => @"[+-]?[0-9０-９]+(?:[.,．][0-9０-９]+)*",
                            _ => throw new InvalidDataException("Unknown numeric slot type: " + slot)
                        };
                        pattern.Append("(?<").Append(slot).Append('>').Append(capture).Append(')');
                    }
                    else pattern.Append(@"\k<").Append(slot).Append('>');
                    cursor = m.Index + m.Length;
                }
                pattern.Append(Regex.Escape(e.Source[cursor..])).Append(@"\z");
                if (!rules.TryGetValue(e.Scope, out var list)) rules[e.Scope] = list = new();
                list.Add(new(e, new Regex(pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10))));
            }
            else throw new InvalidDataException("Unsupported text kind: " + e.Id);
        }
        Count = document.Entries.Count;
    }

    public static void ValidateControls(string source, string target)
    {
        // Preserve the control stream, including conditional block boundaries and link IDs.
        // Literal numbers outside tags/parameters must also survive a reviewed translation.
        if (!Controls.Matches(source).Select(m => m.Value).SequenceEqual(Controls.Matches(target).Select(m => m.Value)))
            throw new InvalidDataException("Display/control tokens changed");
        static IEnumerable<string> LiteralNumbers(string text) => Numbers.Matches(Controls.Replace(Slots.Replace(text, ""), ""))
            .Select(m => m.Value);
        if (!LiteralNumbers(source).SequenceEqual(LiteralNumbers(target))) throw new InvalidDataException("Literal numbers changed");
    }

    public bool TryExact(string scope, string source, out string target) => exact.TryGetValue((scope, source), out target!);
    public bool TryRenderMaster(string scope, string originalTemplate, string nativeDisplay, Func<string, string> resolveParameter, out string target)
    {
        target = "";
        if (!TryExact(scope, originalTemplate, out string reviewed)) return false;
        var values = new Dictionary<string, string>();
        string Resolve(Match m)
        {
            if (!values.TryGetValue(m.Value, out var value)) values[m.Value] = value = resolveParameter(m.Value);
            return value;
        }
        // The game computes every value. Require an exact reproduction of its original output
        // before using those same values in Korean; unknown formatting paths remain untouched.
        try
        {
            string original = MasterParameters.Replace(originalTemplate, Resolve);
            string translated = MasterParameters.Replace(reviewed, Resolve);
            var conditions = new List<(string Header, string Body, string Output, string Raw)>();
            original = ConditionBlocks.Replace(original, m =>
            {
                string output = Resolve(m);
                var parsed = MasterConditions.Match(m.Value);
                // Some shipped data has malformed conditions. Never repair their semantics:
                // accept only native suppression and require the reviewed block to stay verbatim.
                if (!parsed.Success && output.Length != 0) throw new FormatException("Unknown native conditional output");
                conditions.Add((parsed.Groups[1].Value, parsed.Groups[2].Value, output, m.Value));
                return output;
            });
            if (original.Contains("[skilllevel,", StringComparison.Ordinal) || original != nativeDisplay) return false;
            int index = 0;
            translated = ConditionBlocks.Replace(translated, m =>
            {
                if (index >= conditions.Count) throw new FormatException("Conditional count changed");
                var condition = conditions[index++];
                if (condition.Header.Length == 0)
                {
                    if (m.Value != condition.Raw) throw new FormatException("Malformed native clause changed");
                    return "";
                }
                var parsed = MasterConditions.Match(m.Value);
                if (!parsed.Success || condition.Header != parsed.Groups[1].Value) throw new FormatException("Conditional header changed");
                if (condition.Output == "") return "";
                // Native conditions reject commas in translated prose. Evaluate only the
                // original clause and carry its visibility/emphasis onto the paired Korean body.
                int position = condition.Output.IndexOf(condition.Body, StringComparison.Ordinal);
                if (condition.Body.Length == 0 || position < 0 || position != condition.Output.LastIndexOf(condition.Body, StringComparison.Ordinal))
                    throw new FormatException("Unknown native conditional output");
                return condition.Output[..position] + parsed.Groups[2].Value + condition.Output[(position + condition.Body.Length)..];
            });
            if (index != conditions.Count || translated.Contains("[skilllevel,", StringComparison.Ordinal)) return false;
            ValidateControls(nativeDisplay, translated);
            target = translated;
        }
        catch (FormatException) { return false; }
        catch (InvalidDataException) { return false; }
        return true;
    }
    public bool TryRenderMasterDiff(string scope, string originalTemplate, string nativeDisplay,
        Func<string, string> current, Func<string, string> comparison, string format, out string target)
    {
        // The receiver is the displayed skill. The one format slot wraps its changed value.
        return TryRenderMaster(scope, originalTemplate, nativeDisplay, token =>
        {
            string value = current(token);
            return value == comparison(token) ? value : string.Format(format, value);
        }, out target);
    }
    public bool TryUi(string source, string? context, out string target)
        => LookupUi(source, context, out target) == 1;
    // -1 is an explicit ambiguity/timeout, not a cache miss.
    internal int LookupUi(string source, string? context, out string target)
    {
        target = "";
        if (source.Length > 6000) return -1;
        string scoped = "ui:" + context;
        if (context != null && TryExact(scoped, source, out target)) return 1;
        if (TryExact("ui", source, out target)) return 1;
        // A conflicting or timed-out contextual match must not silently fall through to a broad rule.
        if (context != null)
        {
            int result = TryRules(scoped, source, out target);
            if (result != 0) return result;
        }
        return TryRules("ui", source, out target);
    }
    private int TryRules(string scope, string source, out string target)
    {
        target = "";
        if (!rules.TryGetValue(scope, out var list)) return 0;
        string? found = null;
        foreach (var rule in list)
        {
            Match match;
            try { match = rule.Pattern.Match(source); }
            catch (RegexMatchTimeoutException) { return -1; }
            if (!match.Success) continue;
            string value = Slots.Replace(rule.Entry.Target, m => match.Groups[m.Groups[1].Value].Value);
            if (found != null && found != value) return -1;
            found = value;
        }
        if (found == null) return 0;
        target = found; return 1;
    }
}
