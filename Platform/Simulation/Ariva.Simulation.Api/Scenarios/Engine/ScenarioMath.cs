namespace Ariva.Simulation.Api.Scenarios.Engine;

/// <summary>
/// The reference model's seeded randomness and number rules, bit for bit: mulberry32, the mix32 avalanche, the h3
/// hash, FNV-1a for flight IDs, JavaScript <c>Math.round</c> (half up, not half to even) and a modulo that is never
/// negative. Same seed, same bits: every stream is a pure function of the seed and its indices.
/// </summary>
internal static class ScenarioMath
{
    /// <summary>One draw of mulberry32: advances <paramref name="state"/> and returns a double in [0, 1).</summary>
    public static double NextMulberry(ref uint state)
    {
        unchecked
        {
            state += 0x6D2B79F5u;
            var a = state;
            var t = (a ^ (a >> 15)) * (1u | a);
            t = (t + (t ^ (t >> 7)) * (61u | t)) ^ t;
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    /// <summary>The 32-bit avalanche used to derive stream seeds (unsigned result).</summary>
    public static uint Mix32(uint h)
    {
        unchecked
        {
            h ^= h >> 16;
            h *= 0x7feb352du;
            h ^= h >> 15;
            h *= 0x846ca68bu;
            h ^= h >> 16;
            return h;
        }
    }

    /// <summary>A stateless uniform in [0, 1) from three integers: the per-minute and per-server noise source.</summary>
    public static double H3(uint a, int b, int c)
    {
        unchecked
        {
            var x = Mix32(Mix32(Mix32(a ^ 0x9e3779b9u) ^ ((uint)b + 0x632be5abu)) ^ ((uint)c + 0x27d4eb2fu));
            return x / 4294967296.0;
        }
    }

    /// <summary>FNV-1a over UTF-16 code units (an ad-hoc flight's own stream).</summary>
    public static uint StrHash(string value)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var ch in value)
            {
                h ^= ch;
                h *= 16777619u;
            }

            return h;
        }
    }

    /// <summary>JavaScript <c>Math.round</c>: halves round towards positive infinity.</summary>
    public static double Round(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x))
            return x;
        var f = Math.Floor(x);
        return x - f >= 0.5 ? f + 1 : f;
    }

    /// <summary><see cref="Round"/> as an integer, for minutes and counts.</summary>
    public static int RoundToInt(double x) => (int)Round(x);

    /// <summary>A modulo that is never negative.</summary>
    public static int Mod(int a, int n) => ((a % n) + n) % n;

    /// <summary>Clamps between two bounds, as the reference does (no exception when the bounds cross).</summary>
    public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>Normalises weights so that they sum to one, summing left to right.</summary>
    public static double[] Normalise(double[] weights)
    {
        var s = 0.0;
        foreach (var w in weights)
            s += w;
        var o = new double[weights.Length];
        for (var i = 0; i < weights.Length; i++)
            o[i] = weights[i] / s;
        return o;
    }

    /// <summary>HH:mm of a clock minute, wrapping around the day.</summary>
    public static string Clock(double minute)
    {
        var m = Mod(RoundToInt(minute), ScenarioModel.Day);
        return (m / 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ":" +
               (m % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>A mulberry32 stream: a seeded sequence of doubles in [0, 1).</summary>
internal sealed class Mulberry32(uint seed)
{
    private uint _state = seed;

    public double Next() => ScenarioMath.NextMulberry(ref _state);
}
