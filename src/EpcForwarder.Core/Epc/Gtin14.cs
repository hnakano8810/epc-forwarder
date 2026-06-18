namespace EpcForwarder.Core.Epc;

/// <summary>GTIN-14 の検証とフィールド分解。詳細は docs/design/epc-mask.md §6。</summary>
public static class Gtin14
{
    /// <summary>GTIN-14(14桁)のmod-10チェックディジット検証。</summary>
    public static bool HasValidCheckDigit(string gtin14)
    {
        ArgumentNullException.ThrowIfNull(gtin14);
        if (gtin14.Length != 14 || !gtin14.All(char.IsDigit))
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var d = gtin14[i] - '0';
            sum += ((12 - i) % 2 == 0) ? d * 3 : d; // 右端データ桁(index12)が×3
        }

        var check = (10 - (sum % 10)) % 10;
        return check == gtin14[13] - '0';
    }

    /// <summary>
    /// GTIN-14 を「会社コード桁数(gcpLength)」で分解する。
    /// 返り値の ItemReference は SGTIN 仕様の「インジケータ + GTIN商品アイテムコード」の数値。
    /// </summary>
    public static (string Gtin, char Indicator, ulong CompanyPrefix, ulong ItemReference) Parse(string gtin14, int gcpLength)
    {
        ArgumentNullException.ThrowIfNull(gtin14);
        if (gcpLength is < 6 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(gcpLength), "GS1 company prefix length must be 6..12.");
        }

        if (gtin14.Length != 14 || !gtin14.All(char.IsDigit))
        {
            throw new ArgumentException("GTIN-14 must be 14 digits.", nameof(gtin14));
        }

        if (!HasValidCheckDigit(gtin14))
        {
            throw new ArgumentException("GTIN-14 check digit is invalid.", nameof(gtin14));
        }

        var indicator = gtin14[0];
        var companyPrefix = ulong.Parse(gtin14.Substring(1, gcpLength));
        var itemReferenceDigits = $"{indicator}{gtin14.Substring(1 + gcpLength, 12 - gcpLength)}";
        var itemReference = ulong.Parse(itemReferenceDigits);
        return (gtin14, indicator, companyPrefix, itemReference);
    }
}
