namespace LycRomanise.Media;

// Player reports drift a little each time; taken raw the lyrics would jitter, so small corrections are eased in.
public sealed class ClockSmoother
{
    public const double JumpMs = 900;
    public const double EaseMs = 220;   // time constant

    double _pos;
    DateTime _at;
    string? _track;

    public void Reset() => _at = default;

    public long Next(long raw, DateTime now, bool playing, double rate, string? track)
    {
        // Called from the UI thread and the web server thread.
        lock (this)
        {
        bool fresh = _at == default || !playing || _track != track || now < _at;
        if (!fresh)
        {
            double dt = (now - _at).TotalMilliseconds;
            double predicted = _pos + dt * (rate <= 0 ? 1 : rate);
            double diff = raw - predicted;
            if (Math.Abs(diff) > JumpMs || dt > 1000) fresh = true;
            // A small correction never steps back: at worst the clock holds still while raw catches up.
            else _pos = Math.Max(_pos, predicted + diff * (1 - Math.Exp(-dt / EaseMs)));
        }
        if (fresh) _pos = raw;
        _at = now;
        _track = track;
        return (long)_pos;
        }
    }
}
