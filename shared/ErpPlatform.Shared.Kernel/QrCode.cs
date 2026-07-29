using System.Text;

namespace ErpPlatform.Shared.Kernel;

/// <summary>
/// QR Code encoder — byte mode, error-correction level M, versions 1 to 10.
/// Returns the module matrix so the caller can draw it into a PDF, an SVG, or
/// anything else, exactly like <see cref="Barcode"/>.
/// </summary>
/// <remarks>
/// Written out for the same reason Code 128 is: every document in the platform
/// depends on it and the symbology is fixed. The scope is deliberately narrow —
/// our payloads are short document numbers, so byte mode at level M in versions
/// 1..10 (up to 213 bytes) covers everything we print with room to spare, and
/// level M survives the smudging a workshop label picks up.
/// </remarks>
public static class QrCode
{
    /// <summary>A square grid of modules; true is dark. Includes no quiet zone.</summary>
    public sealed record Matrix(bool[,] Modules, string Text)
    {
        public int Size => Modules.GetLength(0);
        public bool this[int x, int y] => Modules[x, y];
    }

    private const int MaxVersion = 10;

    /// <summary>Data codewords available at EC level M, indexed by version (1-based).</summary>
    private static readonly int[] DataCodewords =
        [0, 16, 28, 44, 64, 86, 108, 124, 154, 182, 216];

    /// <summary>EC codewords per block at level M, indexed by version.</summary>
    private static readonly int[] EcPerBlock =
        [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];

    /// <summary>(group 1 blocks, group 2 blocks) at level M, indexed by version.</summary>
    private static readonly (int G1, int G2)[] Blocks =
        [(0, 0), (1, 0), (1, 0), (1, 0), (2, 0), (2, 0), (4, 0), (4, 0), (2, 2), (3, 2), (4, 1)];

    /// <summary>Row/column centres of the alignment patterns, indexed by version.</summary>
    private static readonly int[][] AlignmentCentres =
    [
        [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34],
        [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50]
    ];

    /// <summary>
    /// Encodes <paramref name="text"/> as UTF-8 in byte mode, picking the smallest
    /// version that fits and the mask with the lowest penalty score.
    /// </summary>
    /// <exception cref="ArgumentException">The payload is empty or too long for version 10.</exception>
    public static Matrix Encode(string text)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("Nothing to encode.", nameof(text));

        var data = Encoding.UTF8.GetBytes(text);
        var version = SmallestVersion(data.Length)
            ?? throw new ArgumentException(
                $"{data.Length} bytes exceeds what a version {MaxVersion} QR code holds at level M.",
                nameof(text));

        var codewords = Interleave(BuildCodewords(data, version), version);
        var size = 17 + 4 * version;

        // Reserved modules are placed once; the mask must not disturb them, so a
        // parallel "is this a function module" grid rides along.
        var function = new bool[size, size];
        var modules = new bool[size, size];
        DrawFunctionPatterns(modules, function, version);
        PlaceData(modules, function, codewords, size);

        var best = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            var candidate = Apply(modules, function, version, mask);
            var penalty = Penalty(candidate);
            if (penalty >= bestPenalty) continue;
            bestPenalty = penalty;
            best = mask;
        }

        return new Matrix(Apply(modules, function, version, best), text);
    }

    private static int? SmallestVersion(int byteCount)
    {
        for (var v = 1; v <= MaxVersion; v++)
        {
            var headerBits = 4 + (v >= 10 ? 16 : 8);
            if (byteCount * 8 + headerBits <= DataCodewords[v] * 8) return v;
        }
        return null;
    }

    // --- data encoding ---

    private static byte[] BuildCodewords(byte[] data, int version)
    {
        var total = DataCodewords[version];
        var bits = new BitWriter(total * 8);

        bits.Write(0b0100, 4);                              // byte mode
        bits.Write(data.Length, version >= 10 ? 16 : 8);    // character count
        foreach (var b in data) bits.Write(b, 8);

        // Terminator, then pad to a whole codeword, then the fixed alternating pad.
        bits.Write(0, Math.Min(4, total * 8 - bits.Length));
        bits.Write(0, (8 - bits.Length % 8) % 8);

        var pad = true;
        while (bits.Length < total * 8)
        {
            bits.Write(pad ? 0xEC : 0x11, 8);
            pad = !pad;
        }

        return bits.ToBytes();
    }

    /// <summary>
    /// Splits the data into blocks, appends each block's Reed-Solomon parity, then
    /// interleaves data and parity the way the spec requires so a burst of damage
    /// spreads across blocks instead of destroying one.
    /// </summary>
    private static byte[] Interleave(byte[] data, int version)
    {
        var (g1, g2) = Blocks[version];
        var blockCount = g1 + g2;
        var shortLength = DataCodewords[version] / blockCount;
        var ecLength = EcPerBlock[version];

        var dataBlocks = new byte[blockCount][];
        var ecBlocks = new byte[blockCount][];
        var offset = 0;
        for (var i = 0; i < blockCount; i++)
        {
            var length = i < g1 ? shortLength : shortLength + 1;
            dataBlocks[i] = data[offset..(offset + length)];
            ecBlocks[i] = ReedSolomon(dataBlocks[i], ecLength);
            offset += length;
        }

        var result = new List<byte>(data.Length + ecLength * blockCount);
        for (var i = 0; i < shortLength + 1; i++)
            foreach (var block in dataBlocks)
                if (i < block.Length)
                    result.Add(block[i]);
        for (var i = 0; i < ecLength; i++)
            foreach (var block in ecBlocks)
                result.Add(block[i]);

        return [.. result];
    }

    // --- Reed-Solomon over GF(256), primitive polynomial 0x11D ---

    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static QrCode()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }
        for (var i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private static byte Multiply(byte a, byte b) =>
        a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte[] ReedSolomon(byte[] data, int ecLength)
    {
        // Generator polynomial: the product of (x - a^i) for i in [0, ecLength).
        var generator = new byte[] { 1 };
        for (var i = 0; i < ecLength; i++)
        {
            var next = new byte[generator.Length + 1];
            for (var j = 0; j < generator.Length; j++)
            {
                next[j] ^= generator[j];
                next[j + 1] ^= Multiply(generator[j], Exp[i]);
            }
            generator = next;
        }

        var remainder = new byte[ecLength];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, ecLength - 1);
            remainder[ecLength - 1] = 0;
            for (var i = 0; i < ecLength; i++)
                remainder[i] ^= Multiply(generator[i + 1], factor);
        }
        return remainder;
    }

    // --- module placement ---

    private static void DrawFunctionPatterns(bool[,] m, bool[,] fn, int version)
    {
        var size = m.GetLength(0);

        // Finder patterns and their separators, one per corner bar the bottom-right.
        foreach (var (fx, fy) in new[] { (0, 0), (size - 7, 0), (0, size - 7) })
            for (var dy = -1; dy <= 7; dy++)
            for (var dx = -1; dx <= 7; dx++)
            {
                int x = fx + dx, y = fy + dy;
                if (x < 0 || y < 0 || x >= size || y >= size) continue;
                fn[x, y] = true;
                var ring = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                m[x, y] = dx is >= 0 and <= 6 && dy is >= 0 and <= 6 && ring != 2 && ring <= 3;
            }

        // Timing patterns run between the finders on row 6 and column 6.
        for (var i = 8; i < size - 8; i++)
        {
            m[6, i] = m[i, 6] = i % 2 == 0;
            fn[6, i] = fn[i, 6] = true;
        }

        // Alignment patterns, skipping the three that would sit on a finder.
        var centres = AlignmentCentres[version];
        foreach (var cy in centres)
        foreach (var cx in centres)
        {
            if ((cx == 6 && cy == 6) || (cx == 6 && cy == size - 7) || (cx == size - 7 && cy == 6))
                continue;
            for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                fn[cx + dx, cy + dy] = true;
                m[cx + dx, cy + dy] = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;
            }
        }

        // Format information areas, reserved now and filled per mask later.
        for (var i = 0; i < 9; i++)
        {
            if (i != 6) { fn[i, 8] = true; fn[8, i] = true; }
        }
        fn[8, 6] = true;
        fn[6, 8] = true;
        for (var i = 0; i < 8; i++)
        {
            fn[size - 1 - i, 8] = true;
            fn[8, size - 1 - i] = true;
        }

        // The dark module is always set and always at this position.
        m[8, size - 8] = true;
        fn[8, size - 8] = true;

        if (version < 7) return;

        // Version information: 6 data bits plus an 18-bit BCH remainder.
        var value = version << 12;
        var remainder = version << 12;
        for (var i = 0; i < 6; i++)
            if ((remainder & (1 << (17 - i))) != 0)
                remainder ^= 0x1F25 << (5 - i);
        value |= remainder & 0xFFF;

        for (var i = 0; i < 18; i++)
        {
            var bit = (value & (1 << i)) != 0;
            int a = i / 3, b = i % 3;
            m[a, size - 11 + b] = bit;
            fn[a, size - 11 + b] = true;
            m[size - 11 + b, a] = bit;
            fn[size - 11 + b, a] = true;
        }
    }

    /// <summary>
    /// Walks the two-module-wide zigzag from the bottom-right corner upward,
    /// dropping data bits into every module a function pattern hasn't claimed.
    /// </summary>
    private static void PlaceData(bool[,] m, bool[,] fn, byte[] codewords, int size)
    {
        var bit = 0;
        var upward = true;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5; // column 6 is the vertical timing pattern
            for (var step = 0; step < size; step++)
            {
                var y = upward ? size - 1 - step : step;
                for (var c = 0; c < 2; c++)
                {
                    var x = right - c;
                    if (fn[x, y]) continue;
                    if (bit < codewords.Length * 8)
                        m[x, y] = (codewords[bit / 8] & (0x80 >> (bit % 8))) != 0;
                    bit++;
                }
            }
            upward = !upward;
        }
    }

    private static bool[,] Apply(bool[,] source, bool[,] fn, int version, int mask)
    {
        var size = source.GetLength(0);
        var m = (bool[,])source.Clone();

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            if (fn[x, y]) continue;
            if (Masked(x, y, mask)) m[x, y] = !m[x, y];
        }

        WriteFormat(m, version, mask);
        return m;
    }

    // x is the column, y the row — the spec's conditions are written (row, column).
    private static bool Masked(int x, int y, int mask) => mask switch
    {
        0 => (y + x) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (y + x) % 3 == 0,
        4 => (y / 2 + x / 3) % 2 == 0,
        5 => y * x % 2 + y * x % 3 == 0,
        6 => (y * x % 2 + y * x % 3) % 2 == 0,
        _ => ((y + x) % 2 + y * x % 3) % 2 == 0
    };

    private static void WriteFormat(bool[,] m, int version, int mask)
    {
        var size = m.GetLength(0);

        // Level M is 0b00; the 5 data bits get a 10-bit BCH remainder and a fixed mask.
        var data = 0b00 << 3 | mask;
        var remainder = data << 10;
        for (var i = 0; i < 5; i++)
            if ((remainder & (1 << (14 - i))) != 0)
                remainder ^= 0x537 << (4 - i);
        var bits = ((data << 10) | (remainder & 0x3FF)) ^ 0x5412;

        for (var i = 0; i < 15; i++)
        {
            var bit = (bits & (1 << i)) != 0;

            // Copy 1: down the left column, then along the top row.
            if (i < 6) m[8, i] = bit;
            else if (i == 6) m[8, 7] = bit;
            else if (i == 7) m[8, 8] = bit;
            else if (i == 8) m[7, 8] = bit;
            else m[14 - i, 8] = bit;

            // Copy 2: along the bottom-left, then up the top-right.
            if (i < 8) m[size - 1 - i, 8] = bit;
            else m[8, size - 15 + i] = bit;
        }
    }

    // --- mask selection ---

    private static int Penalty(bool[,] m)
    {
        var size = m.GetLength(0);
        var score = 0;

        // Rule 1: runs of five or more same-coloured modules in a row or column.
        for (var i = 0; i < size; i++)
        {
            score += RunPenalty(j => m[j, i], size);
            score += RunPenalty(j => m[i, j], size);
        }

        // Rule 2: every 2x2 block of one colour.
        for (var y = 0; y < size - 1; y++)
        for (var x = 0; x < size - 1; x++)
            if (m[x, y] == m[x + 1, y] && m[x, y] == m[x, y + 1] && m[x, y] == m[x + 1, y + 1])
                score += 3;

        // Rule 3: the finder-like 1:1:3:1:1 pattern with four light modules beside it.
        bool[] a = [true, false, true, true, true, false, true, false, false, false, false];
        bool[] b = [false, false, false, false, true, false, true, true, true, false, true];
        for (var y = 0; y < size; y++)
        for (var x = 0; x <= size - 11; x++)
        {
            if (MatchesAt(m, x, y, a, horizontal: true) || MatchesAt(m, x, y, b, horizontal: true))
                score += 40;
            if (MatchesAt(m, y, x, a, horizontal: false) || MatchesAt(m, y, x, b, horizontal: false))
                score += 40;
        }

        // Rule 4: deviation of the dark-module proportion from 50%.
        var dark = 0;
        foreach (var v in m) if (v) dark++;
        var percent = dark * 100 / (size * size);
        score += Math.Abs(percent - 50) / 5 * 10;

        return score;
    }

    private static bool MatchesAt(bool[,] m, int x, int y, bool[] pattern, bool horizontal)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            var value = horizontal ? m[x + i, y] : m[x, y + i];
            if (value != pattern[i]) return false;
        }
        return true;
    }

    private static int RunPenalty(Func<int, bool> at, int size)
    {
        var score = 0;
        var run = 1;
        for (var i = 1; i < size; i++)
        {
            if (at(i) == at(i - 1)) run++;
            else
            {
                if (run >= 5) score += run - 2;
                run = 1;
            }
        }
        return run >= 5 ? score + run - 2 : score;
    }

    /// <summary>Big-endian bit accumulator — the order QR codewords are written in.</summary>
    private sealed class BitWriter(int capacityBits)
    {
        private readonly byte[] _bytes = new byte[(capacityBits + 7) / 8];
        public int Length { get; private set; }

        public void Write(int value, int bitCount)
        {
            for (var i = bitCount - 1; i >= 0; i--)
            {
                if ((value & (1 << i)) != 0) _bytes[Length / 8] |= (byte)(0x80 >> (Length % 8));
                Length++;
            }
        }

        public byte[] ToBytes() => _bytes;
    }
}
