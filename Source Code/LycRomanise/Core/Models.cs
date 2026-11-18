namespace LycRomanise.Core;

/// <summary>One timed piece of a line: a word, or a syllable of an unspaced script.</summary>
public sealed class LyricWord
{
    public string Text { get; set; } = "";
    public long Start { get; set; }        // ms
    public long End { get; set; }          // ms
    /// <summary>Runs on from the word before with no space between (a syllable of the same word).</summary>
    public bool Joined { get; set; }

    public LyricWord Clone() => new() { Text = Text, Start = Start, End = End, Joined = Joined };
}

public enum LineAlign { Leading, Trailing }

public sealed class LyricLine
{
    public List<LyricWord> Words { get; set; } = new();
    public long Start { get; set; }
    public long End { get; set; }
    /// <summary>TTML agent ("v1", "v2") — a second voice in a duet.</summary>
    public string? Voice { get; set; }
    public LineAlign Align { get; set; } = LineAlign.Leading;
    /// <summary>Backing vocals sung under the line.</summary>
    public LyricLine? Backing { get; set; }
    /// <summary>How it sounds in Latin letters, from the source (timed where it can be).</summary>
    public LyricLine? Pronunciation { get; set; }
    public string? Translation { get; set; }
    /// <summary>The words are estimated across the line rather than timed by the source.</summary>
    public bool Estimated { get; set; }

    public string Text => LineText(this);

    public static string LineText(LyricLine? line)
    {
        if (line == null || line.Words.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var w in line.Words)
        {
            if (sb.Length > 0 && !w.Joined) sb.Append(' ');
            sb.Append(w.Text);
        }
        return sb.ToString();
    }

    public LyricLine Clone() => new()
    {
        Words = Words.Select(w => w.Clone()).ToList(), Start = Start, End = End, Voice = Voice, Align = Align,
        Backing = Backing?.Clone(), Pronunciation = Pronunciation?.Clone(), Translation = Translation, Estimated = Estimated,
    };
}

public enum Timing { None = 0, Line = 1, Word = 2 }

public sealed record CreditLink(string Title, string Url)
{
    public static implicit operator CreditLink((string Title, string Url) t) => new(t.Title, t.Url);
}

public sealed class LyricsResult
{
    public string Provider { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public List<CreditLink> CreditLinks { get; set; } = new();
    public bool CreditRequired { get; set; }
    public List<LyricLine> Lines { get; set; } = new();
    public Timing Timing { get; set; }
    public bool Instrumental { get; set; }

    // What the source learned about the track, for the sources asked after it.
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public int Seconds { get; set; }

    public bool Synced => Timing != Timing.None;
    public bool HasLines => Lines.Count > 0;
}

/// <summary>What is known about the playing track when a source is asked.</summary>
public sealed class TrackQuery
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public int Seconds { get; set; }
    /// <summary>Spotify's base62 id, when the matcher found one.</summary>
    public string? SpotifyId { get; set; }
    public string? Isrc { get; set; }
    public string? MusixmatchCommontrack { get; set; }

    /// <summary>A stable key for caches and per-song settings.</summary>
    public string Key => Normalize.SongKey(Artist, Title);
}
