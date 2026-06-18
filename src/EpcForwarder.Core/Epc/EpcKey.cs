namespace EpcForwarder.Core.Epc;

/// <summary>
/// EPC &amp; Mask による検索キー生成。詳細仕様は docs/design/epc-mask.md を参照。
/// </summary>
public static class EpcKey
{
    /// <summary>
    /// SGTIN-96 用の固定マスク。上位58bit=1 / 下位38bit(Serial)=0。
    /// </summary>
    public static readonly byte[] Sgtin96Mask = Convert.FromHexString("FFFFFFFFFFFFFFC000000000");

    /// <summary>
    /// EPC と Mask のバイト単位 AND で検索キー（シリアル0クリア済み）を生成する。
    /// EPC と Mask は同じ長さでなければならない。
    /// </summary>
    public static byte[] Derive(ReadOnlySpan<byte> epc, ReadOnlySpan<byte> mask)
    {
        if (epc.Length != mask.Length)
        {
            throw new ArgumentException(
                $"EPC length ({epc.Length}) and mask length ({mask.Length}) must match.");
        }

        var key = new byte[epc.Length];
        for (var i = 0; i < epc.Length; i++)
        {
            key[i] = (byte)(epc[i] & mask[i]);
        }

        return key;
    }
}
