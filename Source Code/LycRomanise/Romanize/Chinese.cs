using System.Text;
using Microsoft.International.Converters.PinYinConverter;

namespace LycRomanise.Romanize;

/// <summary>Mandarin → pinyin with tone marks, with the 不 / 一 tone changes, common lyric readings of
/// characters with several pronunciations, and Chinese punctuation made ordinary.</summary>
public static class Chinese
{
    // Characters whose first dictionary reading is the wrong one in everyday lyrics.
    static readonly Dictionary<char, string> Preferred = new()
    {
        ['的'] = "de5", ['了'] = "le5", ['着'] = "zhe5", ['得'] = "de5", ['么'] = "me5", ['们'] = "men5", ['个'] = "ge4",
        ['和'] = "he2", ['还'] = "hai2", ['都'] = "dou1", ['行'] = "xing2", ['长'] = "chang2", ['为'] = "wei4",
        ['觉'] = "jue2", ['只'] = "zhi3", ['没'] = "mei2", ['重'] = "zhong4", ['一'] = "yi1", ['不'] = "bu4", ['说'] = "shuo1",
        ['会'] = "hui4", ['吗'] = "ma5", ['呢'] = "ne5", ['吧'] = "ba5", ['啊'] = "a5", ['乐'] = "le4", ['便'] = "bian4",
        ['发'] = "fa1", ['少'] = "shao3", ['传'] = "chuan2", ['弹'] = "tan2", ['空'] = "kong1", ['落'] = "luo4", ['那'] = "na4",
        ['哪'] = "na3", ['什'] = "shen2", ['看'] = "kan4", ['散'] = "san4", ['曾'] = "ceng2", ['数'] = "shu4", ['间'] = "jian1",
        ['种'] = "zhong3", ['背'] = "bei4", ['似'] = "si4", ['大'] = "da4", ['要'] = "yao4", ['相'] = "xiang1", ['将'] = "jiang1",
        ['差'] = "cha4", ['地'] = "di4", ['好'] = "hao3", ['给'] = "gei3", ['中'] = "zhong1", ['当'] = "dang1", ['看'] = "kan4",
        ['教'] = "jiao4", ['把'] = "ba3", ['分'] = "fen1", ['还'] = "hai2", ['倒'] = "dao4", ['兴'] = "xing4", ['调'] = "diao4",
        ['应'] = "ying1", ['转'] = "zhuan3", ['奔'] = "ben1", ['尽'] = "jin4", ['藏'] = "cang2", ['角'] = "jiao3", ['朝'] = "chao2",
        ['仿'] = "fang3", ['佛'] = "fu2", ['恶'] = "e4", ['冲'] = "chong1", ['处'] = "chu4", ['似'] = "si4", ['降'] = "jiang4",
        ['正'] = "zheng4", ['结'] = "jie2", ['睡'] = "shui4", ['熟'] = "shu2", ['血'] = "xue4", ['薄'] = "bo2", ['露'] = "lu4",
        ['裳'] = "shang5", ['着'] = "zhe5", ['弄'] = "nong4", ['识'] = "shi2", ['恐'] = "kong3", ['于'] = "yu2", ['与'] = "yu3",
        ['许'] = "xu3", ['几'] = "ji3", ['晃'] = "huang4", ['给'] = "gei3", ['只'] = "zhi3", ['得'] = "de5",
        // Traditional forms of the above (Taiwanese and Hong Kong lyrics).
        ['許'] = "xu3", ['幾'] = "ji3", ['們'] = "men5", ['個'] = "ge4", ['還'] = "hai2", ['長'] = "chang2", ['為'] = "wei4",
        ['覺'] = "jue2", ['沒'] = "mei2", ['說'] = "shuo1", ['會'] = "hui4", ['嗎'] = "ma5", ['樂'] = "le4", ['發'] = "fa1",
        ['傳'] = "chuan2", ['彈'] = "tan2", ['數'] = "shu4", ['間'] = "jian1", ['種'] = "zhong3", ['將'] = "jiang1", ['給'] = "gei3",
        ['當'] = "dang1", ['應'] = "ying1", ['轉'] = "zhuan3", ['盡'] = "jin4", ['處'] = "chu4", ['結'] = "jie2", ['識'] = "shi2",
        ['與'] = "yu3", ['麼'] = "me5", ['著'] = "zhe5", ['裡'] = "li3", ['裏'] = "li3", ['里'] = "li3", ['誰'] = "shei2", ['谁'] = "shei2",
        ['這'] = "zhe4", ['这'] = "zhe4", ['那'] = "na4", ['卻'] = "que4", ['却'] = "que4", ['勁'] = "jin4", ['載'] = "zai4",
    };

    static readonly Dictionary<char, string> Punct = new()
    {
        ['。'] = ".", ['、'] = ",", ['，'] = ",", ['；'] = ";", ['：'] = ":", ['！'] = "!", ['？'] = "?", ['「'] = "", ['」'] = "",
        ['『'] = "", ['』'] = "", ['《'] = "", ['》'] = "", ['“'] = "", ['”'] = "", ['‘'] = "'", ['’'] = "'", ['（'] = "(",
        ['）'] = ")", ['…'] = "...", ['⋯'] = "...", ['～'] = "~", ['·'] = " ",
    };

    public static bool IsHan(char c) => (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF);

    /// <summary>"zhong1" with its tone number, lowercased; null for a character it does not know.</summary>
    public static string? Numbered(char c)
    {
        if (Preferred.TryGetValue(c, out var p)) return p;
        try
        {
            if (!ChineseChar.IsValidChar(c)) return null;
            var cc = new ChineseChar(c);
            for (int i = 0; i < cc.PinyinCount; i++)
                if (cc.Pinyins[i] is { Length: > 0 } s) return s.ToLowerInvariant();
        }
        catch { }
        return null;
    }

    static int ToneOf(string? numbered) => numbered != null && numbered.Length > 0 && char.IsDigit(numbered[^1]) ? numbered[^1] - '0' : 5;

    /// <summary>"zhong1" → "zhōng", "lv4" → "lǜ".</summary>
    public static string Marked(string numbered)
    {
        int tone = ToneOf(numbered);
        var s = char.IsDigit(numbered[^1]) ? numbered[..^1] : numbered;
        s = s.Replace("v", "ü").Replace("u:", "ü");
        if (tone is < 1 or > 4) return s;
        int at = s.IndexOf('a');
        if (at < 0) at = s.IndexOf('e');
        if (at < 0 && s.Contains("ou")) at = s.IndexOf('o');
        if (at < 0)
        {
            for (int i = s.Length - 1; i >= 0; i--)
                if ("iouü".Contains(s[i])) { at = i; break; }
        }
        if (at < 0) return s;
        const string vowels = "aeiouü";
        string[] marks = { "āēīōūǖ", "áéíóúǘ", "ǎěǐǒǔǚ", "àèìòùǜ" };
        int vi = vowels.IndexOf(s[at]);
        return vi < 0 ? s : s[..at] + marks[tone - 1][vi] + s[(at + 1)..];
    }

    public static string Romanize(string text)
    {
        text = text.Normalize(NormalizationForm.FormKC);
        var readings = new List<string?>();
        foreach (var c in text) readings.Add(IsHan(c) ? Numbered(c) : null);
        // Tone changes: 不 before a 4th tone is bú; 一 before a 4th tone is yí, before tones 1 to 3 yì.
        for (int i = 0; i < text.Length; i++)
        {
            if (readings[i] == null) continue;
            var next = i + 1 < text.Length ? readings[i + 1] : null;
            if (text[i] == '不' && ToneOf(next) == 4) readings[i] = "bu2";
            if (text[i] == '一' && next != null) readings[i] = ToneOf(next) == 4 ? "yi2" : "yi4";
        }
        var parts = new List<string>();
        var latin = new StringBuilder();
        void FlushLatin()
        {
            if (latin.Length == 0) return;
            var l = latin.ToString().Trim();
            if (l.Length > 0) parts.Add(l);
            latin.Clear();
        }
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (readings[i] is { } r) { FlushLatin(); parts.Add(Marked(r)); continue; }
            if (Punct.TryGetValue(c, out var p))
            {
                FlushLatin();
                if (p.Trim().Length == 0) continue;
                if (parts.Count > 0 && ".,;:!?)~'".Contains(p[0])) parts[^1] += p;
                else parts.Add(p);
                continue;
            }
            if (char.IsWhiteSpace(c)) { FlushLatin(); continue; }
            if (char.IsPunctuation(c) && latin.Length == 0 && parts.Count > 0) { parts[^1] += c; continue; }
            latin.Append(c);
        }
        FlushLatin();
        return string.Join(' ', parts);
    }
}
