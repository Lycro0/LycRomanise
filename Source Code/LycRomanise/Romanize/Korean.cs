using System.Text;

namespace LycRomanise.Romanize;

/// <summary>Korean → Revised Romanization with the pronunciation rules between syllables (liaison,
/// nasalization, ㄹ assimilation, ㅎ aspiration, palatalization). Ported from v1.8.1.</summary>
public static class Korean
{
    static readonly string[] Cho = { "g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h" };
    static readonly string[] Jung = { "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i" };
    static readonly (string iso, string? remain, int? next)[] Jong =
    {
        ("", null, null), ("k", null, 0), ("k", null, 1), ("k", "k", 9), ("n", null, 2), ("n", "n", 12), ("n", "n", null),
        ("t", null, 3), ("l", null, 5), ("k", "l", 0), ("m", "l", 6), ("l", "l", 7), ("l", "l", 9), ("l", "l", 16),
        ("p", "l", 17), ("l", "l", null), ("m", null, 6), ("p", null, 7), ("p", "p", 9), ("t", null, 9), ("t", null, 10),
        ("ng", "ng", null), ("t", null, 12), ("t", null, 14), ("k", null, 15), ("t", null, 16), ("p", null, 17), ("t", null, null),
    };
    static readonly HashSet<int> KTails = new() { 1, 2, 3, 9, 24 };
    static readonly HashSet<int> TTails = new() { 7, 19, 20, 22, 23, 25, 27 };
    static readonly HashSet<int> PTails = new() { 14, 17, 18, 26 };
    static readonly Dictionary<int, int> Aspirate = new() { [0] = 15, [3] = 16, [12] = 14 };
    static readonly Dictionary<char, string> Compat = new()
    {
        ['ㄱ'] = "g", ['ㄲ'] = "kk", ['ㄴ'] = "n", ['ㄷ'] = "d", ['ㄸ'] = "tt", ['ㄹ'] = "r", ['ㅁ'] = "m", ['ㅂ'] = "b", ['ㅃ'] = "pp",
        ['ㅅ'] = "s", ['ㅆ'] = "ss", ['ㅇ'] = "ng", ['ㅈ'] = "j", ['ㅉ'] = "jj", ['ㅊ'] = "ch", ['ㅋ'] = "k", ['ㅌ'] = "t", ['ㅍ'] = "p",
        ['ㅎ'] = "h", ['ㅏ'] = "a", ['ㅐ'] = "ae", ['ㅑ'] = "ya", ['ㅒ'] = "yae", ['ㅓ'] = "eo", ['ㅔ'] = "e", ['ㅕ'] = "yeo", ['ㅖ'] = "ye",
        ['ㅗ'] = "o", ['ㅘ'] = "wa", ['ㅙ'] = "wae", ['ㅚ'] = "oe", ['ㅛ'] = "yo", ['ㅜ'] = "u", ['ㅝ'] = "wo", ['ㅞ'] = "we", ['ㅟ'] = "wi",
        ['ㅠ'] = "yu", ['ㅡ'] = "eu", ['ㅢ'] = "ui", ['ㅣ'] = "i",
    };

    sealed class Syl { public int Lead, Vowel, Tail; public string? LeadS, TailS; }

    static bool IsSyllable(char c) => c >= '가' && c <= '힣';

    static void ApplyRules(List<Syl> sy)
    {
        for (int i = 0; i < sy.Count - 1; i++)
        {
            var a = sy[i]; var b = sy[i + 1];
            int t = a.Tail, lead = b.Lead, vowel = b.Vowel;
            if (t == 0) continue;
            if (lead == 11)
            {
                if (vowel == 20 && (t == 7 || t == 25)) { a.Tail = 0; b.Lead = t == 7 ? 12 : 14; }
                continue;
            }
            if (lead == 18 && vowel == 20 && t == 7) { a.Tail = 0; b.Lead = 14; continue; }
            if ((t == 27 || t == 6 || t == 15) && Aspirate.TryGetValue(lead, out var asp))
            {
                b.Lead = asp;
                a.Tail = t == 27 ? 0 : t == 6 ? 4 : 8;
                continue;
            }
            if (t == 27 && lead == 9) { b.Lead = 10; a.Tail = 0; continue; }
            if (t == 27 && (lead == 2 || lead == 6)) { a.Tail = 4; t = 4; }
            if (lead == 18 && (KTails.Contains(t) || TTails.Contains(t) || PTails.Contains(t)))
            {
                if (t is 1 or 24 or 2 or 3) { b.Lead = 15; a.Tail = 0; }
                else if (t is 17 or 26) { b.Lead = 17; a.Tail = 0; }
                else if (t is 22 or 23) { b.Lead = 14; a.Tail = 0; }
                else { b.Lead = 16; a.Tail = 0; }
                continue;
            }
            string? group = KTails.Contains(t) ? "k" : TTails.Contains(t) ? "t" : PTails.Contains(t) ? "p" : null;
            if ((lead == 2 || lead == 6) && group != null)
            {
                a.TailS = group == "k" ? "ng" : group == "t" ? "n" : "m";
                continue;
            }
            if (lead == 5)
            {
                if (t == 4) { a.TailS = "l"; b.LeadS = "l"; }
                else if (t is 8 or 11 or 12 or 13 or 15) b.LeadS = "l";
                else if (group is "k" or "p" || t is 16 or 21)
                {
                    b.LeadS = "n";
                    if (group == "k") a.TailS = "ng";
                    else if (group == "p") a.TailS = "m";
                }
                continue;
            }
            if (lead == 2 && t is 8 or 11 or 12 or 13 or 15) b.LeadS = "l";
        }
    }

    public static string Romanize(string text)
    {
        var sb = new StringBuilder();
        int i = 0, n = text.Length;
        while (i < n)
        {
            var ch = text[i];
            if (Compat.TryGetValue(ch, out var c)) { sb.Append(c); i++; continue; }
            if (!IsSyllable(ch)) { sb.Append(ch); i++; continue; }
            int j = i;
            while (j < n && IsSyllable(text[j])) j++;
            var sy = new List<Syl>();
            for (int k = i; k < j; k++)
            {
                int idx = text[k] - 0xAC00;
                sy.Add(new Syl { Lead = idx / 588, Vowel = idx % 588 / 28, Tail = idx % 28 });
            }
            ApplyRules(sy);
            for (int k = 0; k < sy.Count; k++)
            {
                var s = sy[k];
                var syll = (s.LeadS ?? Cho[s.Lead]) + Jung[s.Vowel];
                if (s.Tail == 0) { sb.Append(syll); continue; }
                if (s.TailS != null) { sb.Append(syll).Append(s.TailS); continue; }
                var (iso, remain, next) = Jong[s.Tail];
                var nxt = k + 1 < sy.Count ? sy[k + 1] : null;
                if (nxt != null && nxt.Lead == 11)
                {
                    if (remain != null) syll += remain;
                    sb.Append(syll);
                    if (next is int ni) sb.Append(Cho[ni]);
                }
                else sb.Append(syll).Append(iso);
            }
            i = j;
        }
        return sb.ToString();
    }
}
