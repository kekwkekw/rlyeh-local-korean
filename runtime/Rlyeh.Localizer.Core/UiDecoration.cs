using System.Text.RegularExpressions;

namespace Rlyeh.Localizer.Core;

// Only uniform, whole-label presentation can share an undecorated cache key.
// Interior tags, links, sprites, styles and control commands keep their own key.
public sealed record UiDecoration(string Prefix, string Body, string Suffix)
{
    private static readonly Regex Opening = new(@"\G<(?<name>b|i|s|u|color|size|scale|alpha|font-weight|cspace|mspace|voffset|align)(?:=(?<arg>[^<>]+))?>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Number = new(@"\A[+-]?[0-9]+(?:\.[0-9]+)?(?:%|em|px)?\z", RegexOptions.CultureInvariant);
    private static bool Valid(Match tag)
    {
        string name = tag.Groups["name"].Value.ToLowerInvariant(), arg = tag.Groups["arg"].Value.ToLowerInvariant();
        return name switch
        {
            "b" or "i" or "s" or "u" => arg == "",
            "align" => arg is "left" or "right" or "center" or "justified" or "flush",
            "color" => arg is "black" or "blue" or "green" or "orange" or "purple" or "red" or "white" or "yellow" ||
                Regex.IsMatch(arg, @"\A#(?:[0-9a-f]{6}|[0-9a-f]{8})\z"),
            "alpha" => Regex.IsMatch(arg, @"\A#[0-9a-f]{2}\z"),
            "font-weight" => Regex.IsMatch(arg, @"\A[1-9]00\z"),
            _ => Number.IsMatch(arg)
        };
    }
    private static readonly Regex Closing = new(@"\G</(?<name>b|i|s|u|color|size|scale|alpha|font-weight|cspace|mspace|voffset|align)>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    public static bool TryParse(string source, out UiDecoration decoration)
    {
        decoration = null!;
        if (source.Length > 6000 || !source.StartsWith('<')) return false;
        int start = 0;
        var stack = new Stack<string>();
        while (true)
        {
            var match = Opening.Match(source, start);
            if (!match.Success || !Valid(match)) break;
            stack.Push(match.Groups["name"].Value.ToLowerInvariant());
            start += match.Length;
        }
        if (start == 0 || start == source.Length) return false;
        int end = source.IndexOf('<', start);
        if (end < 0) end = source.Length;
        string body = source[start..end];
        if (string.IsNullOrWhiteSpace(body) || body.Contains('>')) return false;
        for (int position = end; position < source.Length;)
        {
            var match = Closing.Match(source, position);
            if (!match.Success || stack.Count == 0 || stack.Pop() != match.Groups["name"].Value.ToLowerInvariant()) return false;
            position += match.Length;
        }
        // TMP permits unclosed presentation tags through the end of a label.
        decoration = new(source[..start], body, source[end..]);
        return true;
    }
    public bool TryRestore(string translation, out string result)
    {
        result = Prefix + translation + Suffix;
        try { TextCatalog.ValidateControls(Prefix + Body + Suffix, result); return true; }
        catch (InvalidDataException) { result = ""; return false; }
    }
}
