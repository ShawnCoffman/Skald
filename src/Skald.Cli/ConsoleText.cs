using System.Globalization;
using System.Text;

namespace Skald.Cli;

// Windows PowerShell 5.1 decodes a native program's output with the OEM code page, so anything beyond ASCII arrives garbled in
// captures, redirects and ConvertFrom-Json. Console output is therefore plain ASCII; the saved report files stay UTF-8.
public static class ConsoleText
{
    private static readonly (string From, string To)[] Replacements =
    [
        ("·", "-"), ("—", "-"), ("–", "-"), ("→", "->"), ("×", "x"), ("…", "..."), ("°", string.Empty),
        ("®", "(R)"), ("™", "(TM)"), ("©", "(C)"), ("‘", "'"), ("’", "'"), ("“", "\""), ("”", "\""), ("µ", "u")
    ];

    public static string Ascii(string text)
    {
        foreach (var (from, to) in Replacements) text = text.Replace(from, to, StringComparison.Ordinal);
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (character < 128) builder.Append(character);
            // Accents become their base letter (é -> e); anything else that cannot be shown becomes '?'.
            else if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark) builder.Append('?');
        }
        return builder.ToString();
    }
}
