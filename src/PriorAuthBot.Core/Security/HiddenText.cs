using System.Text;

namespace PriorAuthBot.Core.Security;

/// <summary>
/// Characters that render as nothing (chapters 10 and 12). Zero-width spaces and joiners hide where
/// a word breaks, bidirectional overrides make text display in a different order from the one the
/// model reads, and Unicode tag characters (U+E0000 to U+E007F) can spell out a whole sentence that
/// no reviewer sees: each one is an invisible copy of an ASCII character ("ASCII smuggling").
/// </summary>
public static class HiddenText
{
    private const int TagBase = 0xE0000;

    public static bool IsInvisible(char c) =>
        c is '​' or '‌' or '‍' or '⁠' or '﻿' or '­'
        || c is >= '‪' and <= '‮'
        || c is >= '⁦' and <= '⁩';

    /// <summary>True when the text has any invisible character or tag character.</summary>
    public static bool Contains(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var i = 0; i < text.Length; i++)
        {
            if (IsInvisible(text[i]) || IsTag(text, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The text a shield should scan: tag characters turned back into the ASCII they hide, so a
    /// hidden instruction is checked like a visible one. Other invisible characters are dropped,
    /// so "ig​nore" is read as "ignore".
    /// </summary>
    public static string Reveal(string text) => Rewrite(text, reveal: true);

    /// <summary>The text the model should see: every invisible and tag character removed.</summary>
    public static string Strip(string text) => Rewrite(text, reveal: false);

    private static string Rewrite(string text, bool reveal)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Contains(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (IsTag(text, i))
            {
                var codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                if (reveal && codePoint - TagBase is >= 0x20 and < 0x7F)
                {
                    builder.Append((char)(codePoint - TagBase));
                }

                i++;
            }
            else if (!IsInvisible(text[i]))
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }

    private static bool IsTag(string text, int i) =>
        text[i] == '\uDB40' && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
        && char.ConvertToUtf32(text[i], text[i + 1]) is >= TagBase and <= 0xE007F;
}
