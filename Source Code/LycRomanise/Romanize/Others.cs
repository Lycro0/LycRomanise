using System.Globalization;
using System.Text;

namespace LycRomanise.Romanize;

/// <summary>Cyrillic, Greek, Arabic-script, Hebrew and Devanagari, rule based. Ported from v1.8.1.</summary>
public static class Others
{
    static string Cased(char src, string outp) => outp.Length > 0 && char.IsUpper(src) ? char.ToUpperInvariant(outp[0]) + outp[1..] : outp;

    static IEnumerable<(bool word, string chunk)> SplitWords(string text, Func<char, bool> isLetter)
    {
        var sb = new StringBuilder();
        bool? mode = null;
        foreach (var ch in text)
        {
            bool m = isLetter(ch);
            if (mode == null || m == mode) sb.Append(ch);
            else { yield return (mode.Value, sb.ToString()); sb.Clear().Append(ch); }
            mode = m;
        }
        if (sb.Length > 0) yield return (mode ?? false, sb.ToString());
    }

    // ----------------------------------------------------------------- Cyrillic
    static readonly Dictionary<char, string> Cyr = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo", ['ж'] = "zh", ['з'] = "z",
        ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r",
        ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya", ['є'] = "ye", ['і'] = "i", ['ї'] = "yi",
        ['ґ'] = "g", ['ў'] = "u", ['ђ'] = "dj", ['ј'] = "j", ['љ'] = "lj", ['њ'] = "nj", ['ћ'] = "c", ['џ'] = "dz", ['ѓ'] = "gj",
        ['ќ'] = "kj", ['ѕ'] = "dz",
    };

    public static string Cyrillic(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text) sb.Append(Cyr.TryGetValue(char.ToLowerInvariant(ch), out var r) ? Cased(ch, r) : ch.ToString());
        return sb.ToString();
    }

    // -------------------------------------------------------------------- Greek
    static readonly Dictionary<char, string> Gr = new()
    {
        ['α'] = "a", ['β'] = "v", ['γ'] = "g", ['δ'] = "d", ['ε'] = "e", ['ζ'] = "z", ['η'] = "i", ['θ'] = "th", ['ι'] = "i",
        ['κ'] = "k", ['λ'] = "l", ['μ'] = "m", ['ν'] = "n", ['ξ'] = "x", ['ο'] = "o", ['π'] = "p", ['ρ'] = "r", ['σ'] = "s",
        ['ς'] = "s", ['τ'] = "t", ['υ'] = "i", ['φ'] = "f", ['χ'] = "ch", ['ψ'] = "ps", ['ω'] = "o",
    };
    static readonly Dictionary<string, string> GrDi = new()
    {
        ["ου"] = "ou", ["ει"] = "i", ["οι"] = "i", ["αι"] = "e", ["υι"] = "i", ["μπ"] = "b", ["ντ"] = "d", ["γκ"] = "g",
        ["γγ"] = "ng", ["γχ"] = "nch", ["τσ"] = "ts", ["τζ"] = "tz",
    };

    public static string Greek(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var chars = new StringBuilder();
        var splitBefore = new HashSet<int>();
        foreach (var c in decomposed)
        {
            if (c == '̈') { if (chars.Length > 0) splitBefore.Add(chars.Length - 1); }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) chars.Append(c);
        }
        var src = chars.ToString();
        var o = new StringBuilder();
        int i = 0;
        while (i < src.Length)
        {
            var pair = i + 1 < src.Length ? src.Substring(i, 2) : src[i..];
            var low = pair.ToLowerInvariant();
            if (pair.Length == 2 && !splitBefore.Contains(i + 1))
            {
                if (GrDi.TryGetValue(low, out var di)) { o.Append(Cased(pair[0], di)); i += 2; continue; }
                if (low is "αυ" or "ευ")
                {
                    var nxt = i + 2 < src.Length ? char.ToLowerInvariant(src[i + 2]) : '\0';
                    bool voiceless = nxt == '\0' || "κξπσςτφχψθ".Contains(nxt) || !char.IsLetter(nxt);
                    o.Append(Cased(pair[0], (low == "αυ" ? "a" : "e") + (voiceless ? "f" : "v")));
                    i += 2;
                    continue;
                }
            }
            var ch = src[i];
            o.Append(Gr.TryGetValue(char.ToLowerInvariant(ch), out var r) ? Cased(ch, r) : ch.ToString());
            i++;
        }
        return o.ToString();
    }

    // ----------------------------------------------------------- vowel guessing
    static string FillVowels(List<(char kind, string text)> tokens)
    {
        var sb = new StringBuilder();
        bool justInserted = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            sb.Append(tokens[i].text);
            if (tokens[i].kind == 'c' && i + 1 < tokens.Count && tokens[i + 1].kind == 'c')
            {
                if (!justInserted) sb.Append('a');
                justInserted = !justInserted;
            }
            else justInserted = false;
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------- Arabic
    static readonly Dictionary<char, string> ArCons = new()
    {
        ['ب'] = "b", ['ت'] = "t", ['ث'] = "th", ['ج'] = "j", ['ح'] = "h", ['خ'] = "kh", ['د'] = "d", ['ذ'] = "dh", ['ر'] = "r",
        ['ز'] = "z", ['س'] = "s", ['ش'] = "sh", ['ص'] = "s", ['ض'] = "d", ['ط'] = "t", ['ظ'] = "z", ['ع'] = "'", ['غ'] = "gh",
        ['ف'] = "f", ['ق'] = "q", ['ك'] = "k", ['ل'] = "l", ['م'] = "m", ['ن'] = "n", ['ه'] = "h", ['پ'] = "p", ['چ'] = "ch",
        ['ژ'] = "zh", ['گ'] = "g", ['ک'] = "k", ['ٹ'] = "t", ['ڈ'] = "d", ['ڑ'] = "r", ['ں'] = "n", ['ھ'] = "h", ['ہ'] = "h",
    };
    static readonly Dictionary<char, string> ArAlef = new()
    {
        ['ا'] = "a", ['أ'] = "a", ['إ'] = "i", ['آ'] = "aa", ['ٱ'] = "a", ['ى'] = "a", ['ة'] = "a", ['ؤ'] = "u", ['ئ'] = "i",
    };
    static readonly HashSet<char> ArSilent = new() { 'ء', 'ـ', 'ٔ', 'ٕ' };
    static readonly Dictionary<char, string> ArShort = new() { ['َ'] = "a", ['ُ'] = "u", ['ِ'] = "i", ['ٰ'] = "a" };
    static readonly Dictionary<char, string> ArTanwin = new() { ['ً'] = "an", ['ٌ'] = "un", ['ٍ'] = "in" };
    static readonly Dictionary<char, (string c, string v)> ArGlides = new() { ['و'] = ("w", "u"), ['ي'] = ("y", "i"), ['ی'] = ("y", "i"), ['ے'] = ("y", "e") };
    const char Sukun = 'ْ', Shadda = 'ّ';

    static bool IsArabicLetter(char c) => ArCons.ContainsKey(c) || ArAlef.ContainsKey(c) || ArSilent.Contains(c) || ArShort.ContainsKey(c)
        || ArTanwin.ContainsKey(c) || ArGlides.ContainsKey(c) || c == Sukun || c == Shadda;

    static string ArabicWord(string word)
    {
        var tokens = new List<(char kind, string text)>();
        bool marks = false;
        for (int i = 0; i < word.Length; i++)
        {
            char ch = word[i], nxt = i + 1 < word.Length ? word[i + 1] : '\0';
            if (ArCons.TryGetValue(ch, out var c)) tokens.Add(('c', c));
            else if (ArAlef.TryGetValue(ch, out var a)) tokens.Add(('v', a));
            else if (ArGlides.TryGetValue(ch, out var g))
            {
                bool prevCons = tokens.Count > 0 && tokens[^1].kind == 'c';
                if (i == 0 || ArShort.ContainsKey(nxt) || ArAlef.ContainsKey(nxt) || ArTanwin.ContainsKey(nxt) || !prevCons) tokens.Add(('c', g.c));
                else tokens.Add(('v', g.v));
            }
            else if (ArShort.TryGetValue(ch, out var s))
            {
                if (ch == 'ٰ' && tokens.Count > 0 && tokens[^1].kind == 'v') continue;
                tokens.Add(('v', s)); marks = true;
            }
            else if (ArTanwin.TryGetValue(ch, out var t)) { tokens.Add(('v', t)); marks = true; }
            else if (ch == Shadda)
            {
                if (tokens.Count > 0 && tokens[^1].kind == 'c' && tokens[^1].text.Length == 1) tokens[^1] = ('c', tokens[^1].text + tokens[^1].text);
                marks = true;
            }
            else if (ch == Sukun) marks = true;
        }
        return marks ? string.Concat(tokens.Select(t => t.text)) : FillVowels(tokens);
    }

    public static string Arabic(string text) =>
        string.Concat(SplitWords(text, IsArabicLetter).Select(p => p.word ? ArabicWord(p.chunk) : p.chunk));

    // ------------------------------------------------------------------- Hebrew
    static readonly Dictionary<char, string> HeCons = new()
    {
        ['א'] = "", ['ב'] = "v", ['ג'] = "g", ['ד'] = "d", ['ה'] = "h", ['ז'] = "z", ['ח'] = "kh", ['ט'] = "t", ['י'] = "y",
        ['כ'] = "kh", ['ך'] = "kh", ['ל'] = "l", ['מ'] = "m", ['ם'] = "m", ['נ'] = "n", ['ן'] = "n", ['ס'] = "s", ['ע'] = "",
        ['פ'] = "f", ['ף'] = "f", ['צ'] = "ts", ['ץ'] = "ts", ['ק'] = "k", ['ר'] = "r", ['ש'] = "sh", ['ת'] = "t", ['װ'] = "v", ['ײ'] = "ey",
    };
    static readonly Dictionary<char, string> HeNiqqud = new()
    {
        ['ַ'] = "a", ['ָ'] = "a", ['ֲ'] = "a", ['ֳ'] = "o", ['ֱ'] = "e", ['ֵ'] = "e", ['ֶ'] = "e",
        ['ִ'] = "i", ['ֹ'] = "o", ['ֻ'] = "u",
    };
    static readonly Dictionary<char, string> HeHard = new() { ['ב'] = "b", ['כ'] = "k", ['ך'] = "k", ['פ'] = "p", ['ף'] = "p" };
    const char Sheva = 'ְ', Dagesh = 'ּ', ShinDot = 'ׁ', SinDot = 'ׂ';

    static bool IsHebrewLetter(char c) => HeCons.ContainsKey(c) || c == 'ו' || HeNiqqud.ContainsKey(c)
        || c is Sheva or Dagesh or ShinDot or SinDot or 'ׇ' or '־' or '׳';

    static string HebrewWord(string word)
    {
        word = word.Replace("־", "");
        var tokens = new List<(char kind, string text)>();
        bool marks = false;
        for (int i = 0; i < word.Length; i++)
        {
            char ch = word[i], nxt = i + 1 < word.Length ? word[i + 1] : '\0';
            if (HeNiqqud.TryGetValue(ch, out var v)) { tokens.Add(('v', v)); marks = true; }
            else if (ch is Sheva or 'ׇ') marks = true;
            else if (ch == Dagesh)
            {
                if (tokens.Count > 0 && tokens[^1].kind == 'c' && HeHard.TryGetValue(word[Math.Max(0, i - 1)], out var hard)) tokens[^1] = ('c', hard);
                marks = true;
            }
            else if (ch == SinDot) { if (tokens.Count > 0 && tokens[^1].kind == 'c') tokens[^1] = ('c', "s"); }
            else if (ch == ShinDot) { }
            else if (ch == 'ו')
            {
                if (nxt == 'ֹ' && i > 0) continue;
                tokens.Add(i == 0 || HeNiqqud.ContainsKey(nxt) ? ('c', "v") : ('v', "o"));
            }
            else if (ch == 'י' && i > 0 && tokens.Count > 0 && tokens[^1].kind == 'c' && nxt == '\0') tokens.Add(('v', "i"));
            else if (ch == 'ה' && nxt == '\0' && i > 0) continue;
            else if (ch is '׳' or '\'')
            {
                if (tokens.Count > 0 && tokens[^1].kind == 'c')
                    tokens[^1] = ('c', tokens[^1].text switch { "g" => "j", "z" => "zh", "ts" => "ch", var x => x });
            }
            else if (HeCons.TryGetValue(ch, out var c))
            {
                if (i == 0 && HeHard.TryGetValue(ch, out var h) && c.Length > 0) tokens.Add(('c', h));
                else if (c.Length == 0) { if (i == 0 && !HeNiqqud.ContainsKey(nxt)) tokens.Add(('v', "a")); }
                else tokens.Add(('c', c));
            }
        }
        tokens = tokens.Where(t => t.text.Length > 0 || t.kind == 'v').ToList();
        return marks ? string.Concat(tokens.Select(t => t.text)) : FillVowels(tokens);
    }

    public static string Hebrew(string text) =>
        string.Concat(SplitWords(text, IsHebrewLetter).Select(p => p.word ? HebrewWord(p.chunk) : p.chunk));

    // --------------------------------------------------------------- Devanagari
    static readonly Dictionary<char, string> DevCons = new()
    {
        ['क'] = "k", ['ख'] = "kh", ['ग'] = "g", ['घ'] = "gh", ['ङ'] = "ng", ['च'] = "ch", ['छ'] = "chh", ['ज'] = "j", ['झ'] = "jh",
        ['ञ'] = "ny", ['ट'] = "t", ['ठ'] = "th", ['ड'] = "d", ['ढ'] = "dh", ['ण'] = "n", ['त'] = "t", ['थ'] = "th", ['द'] = "d",
        ['ध'] = "dh", ['न'] = "n", ['प'] = "p", ['फ'] = "ph", ['ब'] = "b", ['भ'] = "bh", ['म'] = "m", ['य'] = "y", ['र'] = "r",
        ['ल'] = "l", ['व'] = "v", ['श'] = "sh", ['ष'] = "sh", ['स'] = "s", ['ह'] = "h", ['ळ'] = "l",
    };
    static readonly Dictionary<char, string> DevNukta = new() { ['ड'] = "r", ['ढ'] = "rh", ['फ'] = "f", ['ज'] = "z", ['क'] = "q", ['ख'] = "kh", ['ग'] = "gh" };
    static readonly Dictionary<char, string> DevVowels = new()
    {
        ['अ'] = "a", ['आ'] = "aa", ['इ'] = "i", ['ई'] = "ee", ['उ'] = "u", ['ऊ'] = "oo", ['ऋ'] = "ri", ['ए'] = "e", ['ऐ'] = "ai",
        ['ओ'] = "o", ['औ'] = "au", ['ऑ'] = "o",
    };
    static readonly Dictionary<char, string> DevMatras = new()
    {
        ['ा'] = "aa", ['ि'] = "i", ['ी'] = "ee", ['ु'] = "u", ['ू'] = "oo", ['ृ'] = "ri", ['े'] = "e", ['ै'] = "ai", ['ो'] = "o",
        ['ौ'] = "au", ['ॉ'] = "o",
    };
    static readonly Dictionary<char, string> DevMarks = new() { ['ं'] = "n", ['ँ'] = "n", ['ः'] = "h" };
    const char Virama = '्', Nukta = '़';

    static bool IsDev(char c) => DevCons.ContainsKey(c) || DevVowels.ContainsKey(c) || DevMatras.ContainsKey(c) || DevMarks.ContainsKey(c) || c is Virama or Nukta;

    static string DevWord(string word)
    {
        var o = new List<(string text, bool pending)>();
        for (int i = 0; i < word.Length; i++)
        {
            var ch = word[i];
            if (DevCons.TryGetValue(ch, out var r))
            {
                if (i + 1 < word.Length && word[i + 1] == Nukta) { r = DevNukta.GetValueOrDefault(ch, r); i++; }
                o.Add((r, true));
            }
            else if (ch == Virama) { if (o.Count > 0) o[^1] = (o[^1].text, false); }
            else if (DevMatras.TryGetValue(ch, out var m)) { if (o.Count > 0) o[^1] = (o[^1].text, false); o.Add((m, false)); }
            else if (DevVowels.TryGetValue(ch, out var v)) o.Add((v, false));
            else if (DevMarks.TryGetValue(ch, out var mk)) o.Add((mk, false));
        }
        var sb = new StringBuilder();
        for (int i = 0; i < o.Count; i++)
        {
            sb.Append(o[i].text);
            if (o[i].pending && i != o.Count - 1) sb.Append('a');
        }
        return sb.ToString();
    }

    public static string Devanagari(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text) sb.Append(c >= '०' && c <= '९' ? (char)('0' + (c - '०')) : c);
        return string.Concat(SplitWords(sb.ToString(), IsDev).Select(p => p.word ? DevWord(p.chunk) : p.chunk));
    }
}
