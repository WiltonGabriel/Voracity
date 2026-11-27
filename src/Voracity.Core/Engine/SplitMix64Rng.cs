namespace Voracity.Core.Engine;

/// <summary>
/// Fast, deterministic SplitMix64 PRNG for simulation reproducibility.
/// </summary>
public sealed class SplitMix64Rng
{
    public ulong State { get; private set; }

    public SplitMix64Rng(ulong seed)
    {
        State = seed == 0 ? 1UL : seed;
    }

    public void RestoreState(ulong state)
    {
        State = state == 0 ? 1UL : state;
    }

    public ulong NextUInt64()
    {
        State += 0x9E3779B97F4A7C15UL;
        var z = State;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
}
