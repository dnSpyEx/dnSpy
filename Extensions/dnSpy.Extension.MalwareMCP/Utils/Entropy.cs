namespace dnSpy.Extension.MalwareMCP.Utils;

static class Entropy
{
    /// <summary>Calculate Shannon entropy of a byte array (0.0 = uniform, 8.0 = maximum randomness)</summary>
    public static double Calculate(byte[] data)
    {
        if (data.Length == 0) return 0.0;
        var counts = new int[256];
        foreach (var b in data) counts[b]++;
        double entropy = 0;
        foreach (var c in counts)
        {
            if (c == 0) continue;
            double freq = (double)c / data.Length;
            entropy -= freq * Math.Log2(freq);
        }
        return entropy;
    }
}
