using System.Numerics;

namespace EpcForwarder.Core.Epc;

/// <summary>
/// GTIN-14 + GS1会社コード桁数 + filter から SGTIN-96 の検索キー(シリアル=0)を生成する。
/// 生成値は実タグEPCをマスクした検索キーとビット一致する。詳細は docs/design/epc-mask.md §6。
/// </summary>
public static class Sgtin96Encoder
{
    // gcpLength -> (partition, companyPrefixBits, itemReferenceBits)。CpBits + ItemRefBits は常に44。
    private static readonly IReadOnlyDictionary<int, (int Partition, int CpBits, int ItemRefBits)> Table =
        new Dictionary<int, (int, int, int)>
        {
            [12] = (0, 40, 4),
            [11] = (1, 37, 7),
            [10] = (2, 34, 10),
            [9] = (3, 30, 14),
            [8] = (4, 27, 17),
            [7] = (5, 24, 20),
            [6] = (6, 20, 24),
        };

    public static string EncodeSearchKey(string gtin14, int gcpLength, int filter)
    {
        if (filter is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(filter), "Filter must be 0..7.");
        }

        if (!Table.TryGetValue(gcpLength, out var p))
        {
            throw new ArgumentOutOfRangeException(nameof(gcpLength), "GS1 company prefix length must be 6..12.");
        }

        var (_, _, companyPrefix, itemReference) = Gtin14.Parse(gtin14, gcpLength);

        if (companyPrefix >= (1UL << p.CpBits))
        {
            throw new ArgumentException("Company prefix exceeds the SGTIN-96 field width.", nameof(gtin14));
        }

        if (itemReference >= (1UL << p.ItemRefBits))
        {
            throw new ArgumentException("Item reference exceeds the SGTIN-96 field width.", nameof(gtin14));
        }

        BigInteger value = 0x30;                            // Header (8 bit)
        value = (value << 3) | (uint)filter;                // Filter (3)
        value = (value << 3) | (uint)p.Partition;           // Partition (3)
        value = (value << p.CpBits) | companyPrefix;        // Company Prefix
        value = (value << p.ItemRefBits) | itemReference;   // Item Reference
        value <<= 38;                                       // Serial = 0 (38)

        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var key = new byte[12];
        Array.Copy(raw, 0, key, 12 - raw.Length, raw.Length); // 12バイトへ左ゼロ詰め
        return Convert.ToHexString(key);
    }
}
