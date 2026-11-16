using System.Buffers.Binary;
using System.Text;

namespace DvbHeadend.Ts;

/// <summary>
/// Decodes the compressed text UK broadcasters use in the Freeview HD and
/// Freesat programme guide: a string starting 0x1F, then a table number (1 or
/// 2), then a bit stream of Huffman codes where each code is chosen by the
/// previous character. Table data derives from tvheadend (see README).
/// </summary>
public static class FreesatHuffman
{
    internal readonly record struct Entry(uint Code, uint Mask, byte Bits, byte Next);

    internal sealed class Table(Entry[] entries, int[] index)
    {
        public Entry[] Entries { get; } = entries;
        /// <summary>Entries following character c are Index[c] .. Index[c + 1] - 1.</summary>
        public int[] Index { get; } = index;
    }

    private const byte Stop = 0;
    private const byte Escape = 1;

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private static readonly Lazy<Table[]> Loaded = new(Load);

    internal static Table GetTable(int number) => Loaded.Value[number - 1];

    private static Table[] Load()
    {
        using var stream = typeof(FreesatHuffman).Assembly.GetManifestResourceStream("freesat_huffman.bin")
            ?? throw new InvalidOperationException("Huffman table resource is missing from the build");
        var data = new byte[stream.Length];
        stream.ReadExactly(data);

        var tables = new Table[2];
        int offset = 0;
        for (int t = 0; t < 2; t++)
        {
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            offset += 4;
            var entries = new Entry[count];
            for (int i = 0; i < count; i++)
            {
                uint code = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                byte bits = data[offset + 4];
                uint mask = bits >= 32 ? 0xFFFFFFFF : ~(0xFFFFFFFF >> bits);
                entries[i] = new Entry(code, mask, bits, data[offset + 5]);
                offset += 6;
            }
            var index = new int[129];
            for (int i = 0; i < index.Length; i++)
            {
                index[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                offset += 4;
            }
            tables[t] = new Table(entries, index);
        }
        return tables;
    }

    /// <summary>Returns the decoded text, or null if the data is not decodable.</summary>
    public static string? Decode(ReadOnlySpan<byte> src)
    {
        if (src.Length < 3 || src[0] != 0x1F || (src[1] != 1 && src[1] != 2))
        {
            return null;
        }
        var table = GetTable(src[1]);
        var output = new List<byte>(src.Length * 2);

        // A 32-bit window onto the bit stream, refilled one bit at a time.
        uint value = 0;
        int position = 2;
        int bit = 0;
        while (position < 6 && position < src.Length)
        {
            value |= (uint)src[position] << ((5 - position) * 8);
            position++;
        }

        byte last = Stop; // the start state shares the value 0
        do
        {
            byte next;
            int shift;
            if (last == Escape)
            {
                // A literal byte. Escape mode ends with the first 7-bit character.
                next = (byte)(value >> 24);
                shift = 8;
                if ((next & 0x80) == 0)
                {
                    last = next;
                    if (next < 0x20 && next != (byte)'\n')
                    {
                        next = Escape;
                    }
                }
            }
            else
            {
                int found = -1;
                for (int j = table.Index[last]; j < table.Index[last + 1]; j++)
                {
                    if ((value & table.Entries[j].Mask) == table.Entries[j].Code)
                    {
                        found = j;
                        break;
                    }
                }
                if (found < 0)
                {
                    return null;
                }
                next = table.Entries[found].Next;
                shift = table.Entries[found].Bits;
                last = next;
            }

            if (next != Stop && next != Escape)
            {
                output.Add(next);
            }
            for (int b = 0; b < shift; b++)
            {
                value <<= 1;
                if (position < src.Length)
                {
                    value |= (uint)(src[position] >> (7 - bit)) & 1;
                }
                if (bit == 7)
                {
                    bit = 0;
                    position++;
                }
                else
                {
                    bit++;
                }
            }
        }
        while (last != Stop && position < src.Length + 4);

        byte[] bytes = output.ToArray();
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
