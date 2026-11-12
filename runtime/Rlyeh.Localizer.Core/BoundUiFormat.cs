using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rlyeh.Localizer.Core;

// A native view supplies the original template and its bound wallet name. Only
// integer slots are read from its final output; prices/counts are never calculated.
public sealed class BoundUiFormat
{
    private readonly string source;
    private readonly string targetTemplate;
    private readonly string[] arguments;
    private readonly bool hasName;
    private BoundUiFormat(string source, string targetTemplate, string[] arguments, bool hasName)
    { this.source = source; this.targetTemplate = targetTemplate; this.arguments = arguments; this.hasName = hasName; }

    public static bool TryBind(TextCatalog catalog, string template, string display, string? nativeName,
        int numericSlots, out BoundUiFormat bound)
    {
        bound = null!;
        if (display.Length > 6000 || numericSlots < 0 || numericSlots > 2 ||
            nativeName is { Length: 0 } || !catalog.TryExact("ui", template, out string translated)) return false;
        int count = numericSlots + (nativeName == null ? 0 : 1);
        if (count == 0) return false;
        var pattern = new StringBuilder(@"\A");
        var seen = new HashSet<int>();
        int cursor = 0;
        foreach (Match token in Regex.Matches(template, @"\{([0-2])\}"))
        {
            int index = token.Groups[1].Value[0] - '0';
            if (index >= count) return false;
            pattern.Append(Regex.Escape(template[cursor..token.Index]));
            if (nativeName != null && index == 0) pattern.Append(Regex.Escape(nativeName));
            else if (seen.Contains(index)) pattern.Append(@"\k<n").Append(index).Append('>');
            else pattern.Append("(?<n").Append(index).Append(@">[0-9]+)");
            seen.Add(index); cursor = token.Index + token.Length;
        }
        if (seen.Count != count) return false;
        pattern.Append(Regex.Escape(template[cursor..])).Append(@"\z");
        try
        {
            var match = Regex.Match(display, pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10));
            if (!match.Success) return false;
            var args = Enumerable.Range(0, count).Select(i => nativeName != null && i == 0
                ? nativeName : match.Groups["n" + i].Value).ToArray();
            if (string.Format(CultureInfo.InvariantCulture, template, args) != display) return false;
            bound = new(display, translated, args, nativeName != null);
            return true;
        }
        catch (RegexMatchTimeoutException) { return false; }
        catch (FormatException) { return false; }
    }

    public bool TryRender(string? translatedName, out string target)
    {
        target = "";
        if (hasName && string.IsNullOrWhiteSpace(translatedName)) return false;
        var args = (string[])arguments.Clone();
        if (hasName) args[0] = translatedName!;
        try
        {
            string rendered = string.Format(CultureInfo.InvariantCulture, targetTemplate, args);
            TextCatalog.ValidateControls(source, rendered);
            target = rendered;
            return true;
        }
        catch (InvalidDataException) { return false; }
        catch (FormatException) { return false; }
    }
}
