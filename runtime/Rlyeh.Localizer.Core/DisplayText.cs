using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rlyeh.Localizer.Core;

// UTAGE owns the original TextData and timing. Only the display string is translated.
// Tags are retained as opaque anchors and never sent to the translation engine.
public sealed class DisplayText
{
    public const string PolicyVersion = "anchors-v2-line-boundaries";
    private static readonly HashSet<string> Tags = new(StringComparer.OrdinalIgnoreCase)
    { "b", "i", "color", "size", "ruby", "rt", "em", "sup", "sub", "speed", "interval", "wait", "br", "emoji", "sprite", "material", "quad", "link", "nobr", "align", "voffset", "space" };
    private static readonly HashSet<string> UiTags = new(StringComparer.OrdinalIgnoreCase)
    { "scale", "alpha", "font-weight", "cspace", "mspace", "s", "u", "mark", "indent", "line-indent", "line-height", "margin", "margin-left", "margin-right", "width", "rotate", "pos" };
    private static readonly Regex Tag = new(@"^/?(?<name>[a-zA-Z]+)(?:\s[^<>]*|=[^<>]*)?/?$", RegexOptions.Compiled);
    private static readonly Regex UiTag = new(@"^/?(?<name>[a-zA-Z][a-zA-Z0-9-]*)(?:\s[^<>]*|=[^<>]*)?/?$", RegexOptions.Compiled);
    public string Plain { get; }
    public bool HasUnsupportedTag { get; }
    public bool UsesUiExtensions { get; }
    private readonly List<(int Offset, string Value)> anchors = new();

    public DisplayText(string source, bool ui = false)
    {
        var plain = new StringBuilder();
        for (int i = 0; i < source.Length;)
        {
            if (source[i] == '<')
            {
                int end = source.IndexOf('>', i + 1);
                if (end >= 0)
                {
                    string raw = source.Substring(i, end - i + 1);
                    var match = (ui ? UiTag : Tag).Match(raw[1..^1]);
                    if (match.Success)
                    {
                        string name = match.Groups["name"].Value;
                        if (ui && UiTags.Contains(name)) UsesUiExtensions = true;
                        if (!Tags.Contains(name) && !(ui && UiTags.Contains(name))) HasUnsupportedTag = true;
                        anchors.Add((plain.Length, raw));
                        i = end + 1;
                        continue;
                    }
                }
            }
            plain.Append(source[i++]);
        }
        Plain = plain.ToString();
    }

    public string Restore(string translated)
    {
        if (anchors.Count == 0) return translated;
        // Grapheme boundaries prevent inserting tags into Hangul or surrogate pairs.
        var boundaries = StringInfo.ParseCombiningCharacters(translated).Append(translated.Length).ToArray();
        var alignment = new DisplayAlignment(Plain, translated);
        var result = new StringBuilder();
        int previous = 0;
        foreach (var (offset, value) in anchors)
        {
            int position = alignment.MapOffset(offset);
            int boundary = Array.BinarySearch(boundaries, position);
            if (boundary < 0) position = boundaries[~boundary];
            result.Append(translated, previous, position - previous);
            result.Append(value);
            previous = position;
        }
        result.Append(translated, previous, translated.Length - previous);
        return result.ToString();
    }

    public static int MapVisible(int originalVisible, int originalLength, int targetLength)
    {
        if (originalVisible < 0 || originalVisible >= originalLength) return -1;
        if (originalVisible == 0 || originalLength <= 0) return 0;
        return Math.Clamp((int)Math.Ceiling((double)originalVisible * targetLength / originalLength), 0, targetLength);
    }
}

// Translate the whole request once, but retain hard line boundaries when both
// sides have the same number of lines. This keeps end-of-line pauses in place.
public sealed class DisplayAlignment
{
    private readonly string source, target;
    private readonly List<(int Source, int Target)> points = new() { (0, 0) };
    public DisplayAlignment(string source, string target)
    {
        this.source = source; this.target = target;
        var sourceBreaks = Enumerable.Range(0, source.Length).Where(i => source[i] == '\n').ToArray();
        var targetBreaks = Enumerable.Range(0, target.Length).Where(i => target[i] == '\n').ToArray();
        if (sourceBreaks.Length == targetBreaks.Length)
            for (int i = 0; i < sourceBreaks.Length; i++)
            {
                points.Add((sourceBreaks[i], targetBreaks[i]));
                points.Add((sourceBreaks[i] + 1, targetBreaks[i] + 1));
            }
        points.Add((source.Length, target.Length));
    }
    public int MapOffset(int offset, bool ceiling = false)
    {
        if (offset <= 0) return 0;
        if (offset >= source.Length) return target.Length;
        for (int i = 1; i < points.Count; i++)
        {
            var end = points[i]; var start = points[i - 1];
            if (offset > end.Source || end.Source == start.Source) continue;
            double mapped = start.Target + (double)(offset - start.Source) * (end.Target - start.Target) / (end.Source - start.Source);
            return (int)(ceiling ? Math.Ceiling(mapped) : Math.Round(mapped));
        }
        return target.Length;
    }
    public int MapVisible(int visible, int sourceLength, int targetLength)
    {
        if (visible < 0 || visible >= sourceLength) return -1;
        // UTAGE can count an icon/ruby differently from UTF-16 plain text.
        // Use its parsed lengths whenever our plain-text offsets are not valid.
        return source.Length == sourceLength && target.Length == targetLength
            ? MapOffset(visible, true) : DisplayText.MapVisible(visible, sourceLength, targetLength);
    }
}

public sealed class RevisionSlot
{
    public long Revision { get; private set; }
    public string Source { get; private set; } = "";
    public long Begin(string source) { Source = source; return ++Revision; }
    public void Invalidate() { Source = ""; ++Revision; }
    public bool Accepts(long revision, string source) => Revision == revision && Source == source;
}
