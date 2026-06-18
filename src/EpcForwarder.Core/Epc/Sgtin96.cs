namespace EpcForwarder.Core.Epc;

/// <summary>SGTIN-96 のhex入口。詳細は docs/design/epc-mask.md。</summary>
public static class Sgtin96
{
    /// <summary>SGTIN-96 EPCのバイト長（96bit = 12バイト = 24 hex桁）。</summary>
    public const int HexLength = 24;

    /// <summary>EPC(hex文字列)を検証し、検索キー(hex,大文字)を返す。不正時は ArgumentException。</summary>
    public static string DeriveSearchKey(string epcHex)
    {
        ArgumentNullException.ThrowIfNull(epcHex);
        if (epcHex.Length != HexLength)
        {
            throw new ArgumentException($"SGTIN-96 EPC must be {HexLength} hex chars, got {epcHex.Length}.", nameof(epcHex));
        }

        byte[] epc;
        try
        {
            epc = Convert.FromHexString(epcHex);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("EPC is not valid hex.", nameof(epcHex), ex);
        }

        var key = EpcKey.Derive(epc, EpcKey.Sgtin96Mask);
        return Convert.ToHexString(key);
    }
}
