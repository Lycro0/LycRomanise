using System.Text;
using System.Text.RegularExpressions;
using Windows.Globalization;

namespace LycRomanise.Romanize;

// Windows' Japanese phonetic analyzer (the IME dictionary) splits words and reads kanji in context; kana then go to Hepburn.
public static partial class Japanese
{
    static readonly Dictionary<string, string> Kana = BuildKana();

    // Lyric words the analyzer may read in a literary way.
    static readonly Dictionary<string, string> Words = new()
    {
        ["こんにちは"] = "こんにちわ", ["こんばんは"] = "こんばんわ", ["今日は"] = "こんにちわ",
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
        // ヷ-ヺ have no hiragana form, so ToHiragana leaves them as katakana. Small Ainu kana too.
        Add("ヷ ヸ ヹ ヺ", "va vi ve vo");
        Add("ㇰ ㇱ ㇲ ㇳ ㇴ ㇵ ㇶ ㇷ ㇸ ㇹ ㇺ ㇻ ㇼ ㇽ ㇾ ㇿ", "ku shi su to nu ha hi fu he ho mu ra ri ru re ro");
        return d;
    }

    static string ToHiragana(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c >= 'ァ' && c <= 'ヶ' ? (char)(c - 0x60) : c);
        return sb.ToString();
    }

    // Iteration marks repeat the kana before them (ゞ/ヾ voiced). The analyzer reads a lone mark as
    // "kurikaeshi", so expand them first. Stray combining dakuten left after NFKC are dropped.
    static string ExpandMarks(string s)
    {
        if (s.IndexOfAny(['ゝ', 'ゞ', 'ヽ', 'ヾ', '゙', '゚']) < 0) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '゙' or '゚') continue;
            if (c is 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ')
            {
                var prev = sb.Length > 0 ? sb[^1] : '\0';
                bool kana = (prev >= 'ぁ' && prev <= 'ゖ') || (prev >= 'ァ' && prev <= 'ヺ');
                if (!kana) continue;
                if (c is 'ゞ' or 'ヾ')
                {
                    var voiced = (prev + "゙").Normalize(NormalizationForm.FormC);
                    sb.Append(voiced.Length == 1 ? voiced[0] : prev);
                }
                else sb.Append(prev);
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool IsKana(char c) => (c >= '぀' && c <= 'ヿ') || (c >= 'ㇰ' && c <= 'ㇿ') || (c >= 'ｦ' && c <= 'ﾟ');

    public static string KanaToRomaji(string kana)
    {
        string norm;
        // A lone surrogate makes Normalize throw; keep the text as it is rather than lose the line.
        try { norm = kana.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { norm = kana; }
        var h = ToHiragana(ExpandMarks(norm));
        var sb = new StringBuilder();
        bool geminate = false;
        for (int i = 0; i < h.Length;)
        {
            var c = h[i];
            if (c == 'っ') { geminate = true; i++; continue; }
            if (c == 'ー')
            {
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

    static bool _warned;

    // Windows' Japanese reader fails unless called on an STA thread (kanji left unread), so other calls go to one STA thread.
    static readonly System.Collections.Concurrent.BlockingCollection<(string text, TaskCompletionSource<IReadOnlyList<JapanesePhoneme>?> done)> _work = new();
    static Thread? _sta;

    static IReadOnlyList<JapanesePhoneme>? Analyze(string chunk)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return JapanesePhoneticAnalyzer.GetWords(chunk);
        lock (_work)
        {
            if (_sta == null)
            {
                _sta = new Thread(() =>
                {
                    foreach (var (text, done) in _work.GetConsumingEnumerable())
                    {
                        try { done.SetResult(JapanesePhoneticAnalyzer.GetWords(text)); }
                        catch (Exception ex) { done.SetException(ex); }
                    }
                }) { IsBackground = true, Name = "japanese reader" };
                _sta.SetApartmentState(ApartmentState.STA);
                _sta.Start();
            }
        }
        var tcs = new TaskCompletionSource<IReadOnlyList<JapanesePhoneme>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add((chunk, tcs));
        return tcs.Task.GetAwaiter().GetResult();
    }

    // Furigana written into the lyric itself, "天使（てんし）" or "天使(てんし)": the reading in brackets after kanji.
    [GeneratedRegex(@"(?<=[\p{IsCJKUnifiedIdeographs}々〆ヶ])[（(][\p{IsHiragana}\p{IsKatakana}ー]+[）)]")]
    private static partial Regex Furigana();
    public static string StripFurigana(string text) => Furigana().Replace(text, "");

    public static string Romanize(string text)
    {
        text = ExpandMarks(StripFurigana(text.Normalize(NormalizationForm.FormKC)));
        var parts = new List<string>();
        // The analyzer reads under 100 characters at a time (exactly 100 returns no words).
        // The advance sits in the iterator so the fallback's continue cannot skip it.
        for (int start = 0, len = 0; start < text.Length; start += len)
        {
            len = Math.Min(99, text.Length - start);
            // Never split a surrogate pair (emoji, Ext-B kanji such as 𠮟) across two chunks.
            if (start + len < text.Length && char.IsHighSurrogate(text[start + len - 1])) len--;
            var chunk = text.Substring(start, len);
            IReadOnlyList<JapanesePhoneme>? words = null;
            try { words = Analyze(chunk); }
            catch (Exception ex) { if (!_warned) { _warned = true; LycRomanise.Core.Log.Error("japanese reader", ex); } }
            if (words == null) { parts.Add(KanaToRomaji(chunk)); continue; }
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                var display = w.DisplayText;
                var yomi = Words.TryGetValue(display, out var forced) ? forced : w.YomiText;
                if (string.IsNullOrWhiteSpace(display)) continue;
                // An emoji has a reading ("niyari" for a grin) but is not lyrics text: keep it as is.
                if (display.Any(char.IsSurrogate) && !display.EnumerateRunes().Any(Rune.IsLetterOrDigit))
                {
                    parts.Add(display);
                    continue;
                }
                if (PunctOnly().IsMatch(display))
                {
                    var p = string.Concat(display.Select(c => Punct.TryGetValue(c, out var m) ? m : c.ToString())).Trim();
                    if (p.Length == 0) continue;
                    if (parts.Count > 0 && ".,!?)~".Contains(p[0])) parts[^1] += p; else parts.Add(p);
                    continue;
                }
                string reading;
                bool particle = !w.IsPhraseStart && i > 0;
                if (display == "は" && (particle || (i > 0 && words[i - 1].DisplayText is "こんにち" or "こんばん" or "今日" or "今晩"))) reading = "wa";
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
                // A word that opens with small っ (って, っていた) always belongs to the word before: "natte", not "na tte".
                bool smallTsu = display.Length > 0 && display[0] is 'っ' or 'ッ';
                if (parts.Count > 0 && i > 0 && smallTsu && !PunctOnly().IsMatch(parts[^1])) parts[^1] += reading;
                else if (!w.IsPhraseStart && i > 0 && parts.Count > 0 && hiraganaOnly && !Particles.Contains(display)
                    && !PunctOnly().IsMatch(parts[^1]))
                    parts[^1] += reading;
                else parts.Add(reading);
            }
        }
        var joined = Regex.Replace(string.Join(' ', parts), " {2,}", " ").Trim();
        // The analyzer can return nothing for odd mixes (katakana and hiragana in one word): read the kana directly.
        return joined.Length == 0 && text.Any(IsKana) ? KanaToRomaji(text) : joined;
    }
}
