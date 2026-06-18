using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Epc;

namespace EpcForwarder.Core.Products;

/// <summary>GTIN(＋会社コード桁数)から検索キーを算出して商品マスタへ登録する。</summary>
public sealed class ProductRegistrar(IProductWriteStore store)
{
    public string Register(
        int tenantId,
        string gtin14,
        int gcpLength,
        int filter,
        string sku,
        string? itemCode = null,
        string? color = null,
        string? size = null,
        string? description = null)
    {
        var searchKey = Sgtin96Encoder.EncodeSearchKey(gtin14, gcpLength, filter);
        store.Upsert(new ProductRecord(tenantId, searchKey, sku, itemCode, color, size, description));
        return searchKey;
    }
}
