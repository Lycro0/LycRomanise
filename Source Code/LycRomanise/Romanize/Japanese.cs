using System.Text;
using System.Text.RegularExpressions;
using Windows.Globalization;

namespace LycRomanise.Romanize;

/// <summary>Japanese → Hepburn romaji. Windows' own Japanese phonetic analyzer (the IME's dictionary)
/// splits the line into words and reads the kanji in context; the kana are then written in Hepburn with
/// the particle readings (は wa, へ e, を o), long vowels and small っ handled.</summary>
public static partial class Japanese
{
    static readonly Dictionary<string, string> Kana = BuildKana();

    // Lyric words the analyzer may read in a literary way.
    static readonly Dictionary<string, string> Words = new()
    {
        ["今日"] = "きょう", ["明日"] = "あした", ["昨日"] = "きのう", ["一人"] = "ひとり", ["二人"] = "ふたり",
        ["大人"] = "おとな", ["貴方"] = "あなた", ["運命"] = "うんめい", ["未来"] = "みらい", ["永遠"] = "えいえん",
        ["私"] = "わたし", ["君"] = "きみ", ["僕"] = "ぼく", ["俺"] = "おれ", ["心"] = "こころ", ["夢"] = "ゆめ", ["愛"] = "あい",
        ["涙"] = "なみだ", ["空"] = "そら", ["声"] = "こえ", ["風"] = "かぜ", ["光"] = "ひかり", ["花"] = "はな", ["歌"] = "うた",
        ["恋"] = "こい", ["海"] = "うみ", ["雨"] = "あめ", ["星"] = "ほし", ["雪"] = "ゆき", ["道"] = "みち", ["今"] = "いま",
        ["夜"] = "よる", ["月"] = "つき", ["嘘"] = "うそ", ["手"] = "て", ["目"] = "め",
    };

    static Dictionary<string, string> BuildKana()
    {
        var d = new Dictionary<string, string>();
        void Add(string kana, string romaji) { foreach (var (k, r) in kana.Split(' ').Zip(romaji.Split(' '))) d[k] = r; }
        Add("あ い う え お か き く け こ さ し す せ そ た ち つ て と な に ぬ ね の は ひ ふ へ ほ ま み む め も や ゆ よ ら り る れ ろ わ ゐ ゑ を ん",
            "a i u e o ka ki ku ke ko sa shi su se so ta chi tsu te to na ni nu ne no ha hi fu he ho ma mi mu me mo ya yu yo ra ri ru re ro wa i e o n");
        Add("が ぎ ぐ げ ご ざ じ ず ぜ ぞ だ ぢ づ で ど ば び ぶ べ ぼ ぱ ぴ ぷ ぺ ぽ ゔ",
            "ga gi gu ge go za ji zu ze zo da ji zu de do ba bi bu be bo pa pi pu pe po vu");
        Add("ぁ ぃ ぅ ぇ ぉ ゃ ゅ ょ ゎ ゕ ゖ", "a i u e o ya yu yo wa ka ke");
        Add("きゃ きゅ きょ しゃ しゅ しょ ちゃ ちゅ ちょ にゃ にゅ にょ ひゃ ひゅ ひょ みゃ みゅ みょ りゃ りゅ りょ",
            "kya kyu kyo sha shu sho cha chu cho nya nyu nyo hya hyu hyo mya myu myo rya ryu ryo");
        Add("ぎゃ ぎゅ ぎょ じゃ じゅ じょ ぢゃ ぢゅ ぢょ びゃ びゅ びょ ぴゃ ぴゅ ぴょ",
            "gya gyu gyo ja ju jo ja ju jo bya byu byo pya pyu pyo");
        // Sounds katakana writes for loanwords (converted to hiragana first).
        Add("ふぁ ふぃ ふぇ ふぉ ゔぁ ゔぃ ゔぇ ゔぉ てぃ でぃ とぅ どぅ うぃ うぇ うぉ ちぇ しぇ じぇ つぁ つぃ つぇ つぉ いぇ でゅ てゅ ふゅ くぁ ぐぁ",
            "fa fi fe fo va vi ve vo ti di tu du wi we wo che she je tsa tsi tse tso ye dyu tyu fyu kwa gwa");
        return d;
    }

    static string ToHiragana(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c >= 'ァ' && c <= 'ヶ' ? (char)(c - 0x60) : c);
        return sb.ToString();
    }

    public static bool IsKana(char c) => (c >= '぀' && c <= 'ヿ') || (c >= 'ㇰ' && c <= 'ㇿ') || (c >= 'ｦ' && c <= 'ﾟ');

    /// <summary>Kana (hiragana or katakana) → Hepburn.</summary>
    public static string KanaToRomaji(string kana)
    {
        var h = ToHiragana(kana.Normalize(NormalizationForm.FormKC));
        var sb = new StringBuilder();
        bool geminate = false;
        for (int i = 0; i < h.Length;)
        {
            var c = h[i];
            if (c == 'っ') { geminate = true; i++; continue; }
            if (c == 'ー')
            {
                // A long vowel: repeat the vowel before it.
                var last = sb.Length > 0 ? sb[^1] : '\0';
                if ("aiueo".Contains(last)) sb.Append(last);
                i++;
                continue;
            }
            string? romaji = null;
            int used = 0;
            if (i + 1 < h.Length && Kana.TryGetValue(h.Substring(i, 2), out var two)) { romaji = two; used = 2; }
            else if (Kana.TryGetValue(c.ToString(), out var one)) { romaji = one; used = 1; }
            if (romaji == null) { sb.Append(c); i++; geminate = false; continue; }
            if (geminate)
            {
                sb.Append(romaji.StartsWith("ch") ? 't' : romaji[0]);
                geminate = false;
            }
            // ん before a vowel or y is written n' so "kan'i" does not read as "kani".
            if (sb.Length > 0 && sb[^1] == 'n' && i > 0 && h[i - 1] == 'ん' && "aiueoy".Contains(romaji[0])) sb.Append('\'');
            sb.Append(romaji);
            i += used;
        }
        if (geminate) sb.Append('t');   // a trailing っ is a glottal stop
        return sb.ToString();
    }

    [GeneratedRegex(@"^[\p{P}\p{S}\s]+$")] private static partial Regex PunctOnly();

    static readonly HashSet<string> Particles = new()
    {
        "は", "が", "を", "に", "で", "と", "も", "へ", "や", "の", "か", "ね", "よ", "な", "さえ", "から", "まで", "より", "って",
        "だけ", "しか", "ばかり", "ほど", "など", "ぞ", "ぜ", "わ", "さ", "けど", "けれど", "のに", "ので", "し", "でも", "こそ",
        "だ", "です", "じゃ", "では", "なら", "とか", "ばかり", "くらい", "ぐらい", "ずつ", "には", "とは", "では", "へと",
    };

    static readonly Dictionary<char, string> Punct = new()
    {
        ['。'] = ".", ['、'] = ",", ['！'] = "!", ['？'] = "?", ['「'] = "", ['」'] = "", ['『'] = "", ['』'] = "",
        ['（'] = "(", ['）'] = ")", ['…'] = "...", ['～'] = "~", ['〜'] = "~", ['・'] = " ", ['♪'] = "♪",
    };

    public static string Romanize(string text)
    {
        text = text.Normalize(NormalizationForm.FormKC);
        var parts = new List<string>();
        // The analyzer reads at most 100 characters at a time.
        for (int start = 0; start < text.Length; start += 100)
        {
            var chunk = text.Substring(start, Math.Min(100, text.Length - start));
            IReadOnlyList<JapanesePhoneme>? words = null;
            try { words = JapanesePhoneticAnalyzer.GetWords(chunk); } catch { }
            if (words == null) { parts.Add(KanaToRomaji(chunk)); continue; }
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                var display = w.DisplayText;
                var yomi = Words.TryGetValue(display, out var forced) ? forced : w.YomiText;
                if (string.IsNullOrWhiteSpace(display)) continue;
                if (PunctOnly().IsMatch(display))
                {
                    var p = string.Concat(display.Select(c => Punct.TryGetValue(c, out var m) ? m : c.ToString())).Trim();
                    if (p.Length == 0) continue;
                    if (parts.Count > 0 && ".,!?)~".Contains(p[0])) parts[^1] += p; else parts.Add(p);
                    continue;
                }
                string reading;
                bool particle = !w.IsPhraseStart && i > 0;
                if (particle && display == "は") reading = "wa";
                else if (particle && display == "へ") reading = "e";
                else if (display == "を") reading = "o";
                else if (yomi.Any(Chinese.IsHan) || (display.Any(Chinese.IsHan) && yomi == display))
                    reading = Chinese.Romanize(display);   // a character the Japanese dictionary does not know
                else if (yomi.All(c => IsKana(c) || c == 'ー')) reading = KanaToRomaji(yomi);
                else reading = KanaToRomaji(display);
                if (reading.Length == 0) continue;
                // Verb and adjective endings (て, る, ない, ます…) run on into the word before;
                // particles stand apart, as Hepburn writes them.
                bool hiraganaOnly = display.All(c => c >= 'ぁ' && c <= 'ゟ');
                if (!w.IsPhraseStart && i > 0 && parts.Count > 0 && hiraganaOnly && !Particles.Contains(display)
                    && !PunctOnly().IsMatch(parts[^1]))
                    parts[^1] += reading;
                else parts.Add(reading);
            }
        }
        return Regex.Replace(string.Join(' ', parts), " {2,}", " ").Trim();
    }
}
