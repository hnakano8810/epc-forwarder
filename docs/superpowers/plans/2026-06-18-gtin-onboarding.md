# GTIN→検索キー & 商品マスタ投入 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 既知のGTIN（＋GS1会社コード桁数）から SGTIN-96 の「検索キー」を生成し、商品（SKU・属性）をマスタ登録できるようにする。これにより、入荷する実タグEPCをマスクした検索キーが商品マスタにヒットしてSKU解決できる（入出荷/棚卸スライスのSKU化の土台）。

**Architecture:** ロジックは `EpcForwarder.Core` に隔離する。GTIN-14のパース/チェックディジット検証（`Gtin14`）→ SGTIN-96ビットパッキング（`Sgtin96Encoder`、全partition 0–6）で検索キーを得る。登録は `IProductWriteStore` ポート越し（`ProductRegistrar`）。永続化は当面 in-memory（テストフェイク）で、Azure SQL 実装は Azureアダプタ計画（別途）に委譲。

**Tech Stack:** C# / .NET 8、xUnit、`System.Numerics.BigInteger`（96bit組み立て）。既存 `EpcKey`（`Sgtin96Mask`/`Derive`）・`Sgtin96.DeriveSearchKey`・ポート（`IProductCatalog`）・テストフェイク（`InMemoryProductCatalog`）を再利用。

**Scope boundary:**
- 含む: `Gtin14`、`Sgtin96Encoder`、`ProductRecord`＋`IProductWriteStore` ポート、`ProductRegistrar`、テスト用 in-memory 書き込みストア、登録→取込解決のループ検証テスト。
- 含まない（別計画）: Azure SQL 商品マスタ実装、登録用 HTTP/Functions エンドポイント、CSV一括取込、独自コードのエンコード・プラグイン。

**Prerequisites:** `dotnet` は ~/.dotnet。各コマンド前に `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH`。全テスト: `dotnet test EpcForwarder.sln --nologo`。`Directory.Build.props` は `TreatWarningsAsErrors=true`（テスト由来のアナライザ警告が本質的でなければ test csproj で個別に無効化可）。

---

## File Structure

| ファイル | 責務 |
|---|---|
| `src/EpcForwarder.Core/Epc/Gtin14.cs` | GTIN-14 検証（14桁・チェックディジット）とフィールド分解 |
| `src/EpcForwarder.Core/Epc/Sgtin96Encoder.cs` | GTIN-14＋会社コード桁数＋filter → 検索キー(hex)。全partition |
| `src/EpcForwarder.Core/Abstractions/Ports.cs`（変更） | `ProductRecord` ＋ `IProductWriteStore` を追加 |
| `src/EpcForwarder.Core/Products/ProductRegistrar.cs` | 登録: GTIN→検索キー→ストアUpsert |
| `tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs`（変更） | `InMemoryProductCatalog` を `IProductWriteStore` 実装に拡張 |
| `tests/EpcForwarder.Core.Tests/Epc/Gtin14Tests.cs` | チェックディジット/分解のテスト |
| `tests/EpcForwarder.Core.Tests/Epc/Sgtin96EncoderTests.cs` | エンコードのマスク整合・全partition・検証 |
| `tests/EpcForwarder.Core.Tests/Products/ProductRegistrarTests.cs` | 登録→ストア、登録→取込解決のループ |

SGTIN-96 ビットレイアウトと partition 表は `docs/design/epc-mask.md` を正本とする。

---

## Task 1: Gtin14（検証＋分解）

**Files:**
- Create: `src/EpcForwarder.Core/Epc/Gtin14.cs`
- Test: `tests/EpcForwarder.Core.Tests/Epc/Gtin14Tests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Epc/Gtin14Tests.cs
using EpcForwarder.Core.Epc;
using Xunit;

namespace EpcForwarder.Core.Tests.Epc;

public class Gtin14Tests
{
    [Theory]
    [InlineData("00036000291452", true)]   // UPC 036000291452 (check digit 2) を14桁化
    [InlineData("00036000291453", false)]  // チェックディジット不正
    public void HasValidCheckDigit_Works(string gtin, bool expected)
    {
        Assert.Equal(expected, Gtin14.HasValidCheckDigit(gtin));
    }

    [Fact]
    public void Parse_SplitsFields_ByCompanyPrefixLength()
    {
        // "00036000291452" を gcpLength=7 で分解
        //   indicator   = '0'                 (index0)
        //   companyPrefix = "0036000" (index1..7) = 36000
        //   GTIN itemRef  = "29145"   (index8..12)
        //   SGTIN itemReference = indicator + GTIN itemRef = "0" + "29145" = 29145
        var (gtin, indicator, cp, itemRef) = Gtin14.Parse("00036000291452", gcpLength: 7);

        Assert.Equal("00036000291452", gtin);
        Assert.Equal('0', indicator);
        Assert.Equal(36000UL, cp);
        Assert.Equal(29145UL, itemRef);
    }

    [Theory]
    [InlineData("3600029145", 7)]          // 14桁でない
    [InlineData("0003600029145X", 7)]      // 非数字
    [InlineData("00036000291453", 7)]      // チェックディジット不正
    public void Parse_Invalid_Throws(string gtin, int gcpLength)
    {
        Assert.Throws<ArgumentException>(() => Gtin14.Parse(gtin, gcpLength));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(13)]
    public void Parse_BadGcpLength_Throws(int gcpLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Gtin14.Parse("00036000291452", gcpLength));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~Gtin14Tests`
Expected: コンパイル失敗（`Gtin14` 未定義）。

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EpcForwarder.Core/Epc/Gtin14.cs
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~Gtin14Tests`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Epc/Gtin14.cs tests/EpcForwarder.Core.Tests/Epc/Gtin14Tests.cs
git commit -m "feat(core): GTIN-14 検証とフィールド分解"
```

---

## Task 2: Sgtin96Encoder（GTIN→検索キー、全partition）

**Files:**
- Create: `src/EpcForwarder.Core/Epc/Sgtin96Encoder.cs`
- Test: `tests/EpcForwarder.Core.Tests/Epc/Sgtin96EncoderTests.cs`

検証方針: 具体的な「マジック検索キー」をハードコードする代わりに、**マスク整合性**（生成キーに任意のシリアルを載せて `EpcKey.Derive` すると同じキーに戻る）＋ヘッダ＝0x30＋シリアル38bit=0、で正しさを担保する。

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Epc/Sgtin96EncoderTests.cs
using EpcForwarder.Core.Epc;
using Xunit;

namespace EpcForwarder.Core.Tests.Epc;

public class Sgtin96EncoderTests
{
    // 13桁(インジケータ+12桁)からチェックディジットを付けて有効なGTIN-14を作る
    private static string MakeGtin14(string body13)
    {
        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var d = body13[i] - '0';
            sum += ((12 - i) % 2 == 0) ? d * 3 : d;
        }
        var check = (10 - (sum % 10)) % 10;
        return body13 + check;
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Encode_AllPartitions_MaskConsistent_HeaderAndSerialZero(int gcpLength)
    {
        var gtin = MakeGtin14("0000000000001"); // 小さい値: 全partitionでフィールド幅に収まる
        var key = Sgtin96Encoder.EncodeSearchKey(gtin, gcpLength, filter: 1);
        var keyBytes = Convert.FromHexString(key);

        // ヘッダは 0x30
        Assert.Equal(0x30, keyBytes[0]);
        // シリアル(下位38bit)は0: 末尾4バイト=0、5バイト目(index7)の下位6bit=0
        Assert.Equal(0, keyBytes[11]);
        Assert.Equal(0, keyBytes[10]);
        Assert.Equal(0, keyBytes[9]);
        Assert.Equal(0, keyBytes[8]);
        Assert.Equal(0, keyBytes[7] & 0x3F);

        // マスク整合性: 任意シリアルを載せても EpcKey.Derive で同じキーに戻る
        var epc = (byte[])keyBytes.Clone();
        epc[11] = 0x99; epc[10] = 0x88; epc[9] = 0x77; epc[8] = 0x66;
        epc[7] = (byte)(epc[7] | 0x3F);
        var derived = EpcKey.Derive(epc, EpcKey.Sgtin96Mask);
        Assert.Equal(key, Convert.ToHexString(derived));
    }

    [Fact]
    public void Encode_DifferentGcpLength_ProducesDifferentKey()
    {
        var gtin = MakeGtin14("0000000000001");
        var k7 = Sgtin96Encoder.EncodeSearchKey(gtin, 7, filter: 1);
        var k8 = Sgtin96Encoder.EncodeSearchKey(gtin, 8, filter: 1);
        Assert.NotEqual(k7, k8); // partition が異なる
    }

    [Theory]
    [InlineData(8)]
    [InlineData(-1)]
    public void Encode_BadFilter_Throws(int filter)
    {
        var gtin = MakeGtin14("0000000000001");
        Assert.Throws<ArgumentOutOfRangeException>(() => Sgtin96Encoder.EncodeSearchKey(gtin, 7, filter));
    }

    [Fact]
    public void Encode_BadGcpLength_Throws()
    {
        var gtin = MakeGtin14("0000000000001");
        Assert.Throws<ArgumentOutOfRangeException>(() => Sgtin96Encoder.EncodeSearchKey(gtin, 5, filter: 1));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~Sgtin96EncoderTests`
Expected: コンパイル失敗（`Sgtin96Encoder` 未定義）。

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EpcForwarder.Core/Epc/Sgtin96Encoder.cs
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~Sgtin96EncoderTests`
Expected: PASS（10ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Epc/Sgtin96Encoder.cs tests/EpcForwarder.Core.Tests/Epc/Sgtin96EncoderTests.cs
git commit -m "feat(core): GTIN→SGTIN-96検索キー エンコーダ(全partition)"
```

---

## Task 3: 商品書き込みポートと in-memory ストア拡張

定義＋テストフェイク拡張。`InMemoryProductCatalog` を `IProductWriteStore` も実装するよう拡張し、`ResolveSku` は登録レコードのSKUを返す。

**Files:**
- Modify: `src/EpcForwarder.Core/Abstractions/Ports.cs`（追記）
- Modify: `tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs`（`InMemoryProductCatalog` 拡張）
- Test: `tests/EpcForwarder.Core.Tests/Products/InMemoryProductCatalogTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Products/InMemoryProductCatalogTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Products;

public class InMemoryProductCatalogTests
{
    [Fact]
    public void Upsert_ThenResolveSku_ReturnsSku()
    {
        var catalog = new InMemoryProductCatalog();
        catalog.Upsert(new ProductRecord(1, "302DB42318A0038000000000", "ITEM-AAA", "ST-100", "BLK", "M", null));

        Assert.Equal("ITEM-AAA", catalog.ResolveSku(1, "302DB42318A0038000000000"));
        Assert.Null(catalog.ResolveSku(1, "FFFFFFFFFFFFFFFFFFFFFFFF")); // 未登録
        Assert.Null(catalog.ResolveSku(2, "302DB42318A0038000000000")); // 別テナント
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~InMemoryProductCatalogTests`
Expected: コンパイル失敗（`ProductRecord`/`IProductWriteStore`/`Upsert` 未定義）。

- [ ] **Step 3: Add port definitions and extend the fake**

`src/EpcForwarder.Core/Abstractions/Ports.cs` の末尾に追記:

```csharp
public sealed record ProductRecord(
    int TenantId,
    string SearchKey,
    string Sku,
    string? ItemCode,
    string? Color,
    string? Size,
    string? Description);

public interface IProductWriteStore
{
    void Upsert(ProductRecord product); // (TenantId, SearchKey) で上書き
}
```

`tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs` の `InMemoryProductCatalog` を次に置き換える（既存の `Add`/`ResolveSku` 互換を維持しつつ `IProductWriteStore` を実装）:

```csharp
public sealed class InMemoryProductCatalog : IProductCatalog, IProductWriteStore
{
    private readonly Dictionary<(int, string), ProductRecord> _map = new();

    // 既存テスト互換: SKUのみ登録
    public void Add(int tenantId, string searchKey, string sku) =>
        _map[(tenantId, searchKey)] = new ProductRecord(tenantId, searchKey, sku, null, null, null, null);

    public void Upsert(ProductRecord product) =>
        _map[(product.TenantId, product.SearchKey)] = product;

    public string? ResolveSku(int tenantId, string searchKey) =>
        _map.TryGetValue((tenantId, searchKey), out var p) ? p.Sku : null;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo`
Expected: 全PASS（既存テストも含め緑。`InMemoryProductCatalog.Add` 利用箇所が壊れていないこと）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Abstractions/Ports.cs tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs tests/EpcForwarder.Core.Tests/Products/InMemoryProductCatalogTests.cs
git commit -m "feat(core): 商品書き込みポート(ProductRecord/IProductWriteStore)とフェイク拡張"
```

---

## Task 4: ProductRegistrar（GTIN登録）

**Files:**
- Create: `src/EpcForwarder.Core/Products/ProductRegistrar.cs`
- Test: `tests/EpcForwarder.Core.Tests/Products/ProductRegistrarTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Products/ProductRegistrarTests.cs
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Products;

public class ProductRegistrarTests
{
    private static string MakeGtin14(string body13)
    {
        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var d = body13[i] - '0';
            sum += ((12 - i) % 2 == 0) ? d * 3 : d;
        }
        return body13 + (10 - (sum % 10)) % 10;
    }

    [Fact]
    public void Register_StoresProduct_UnderComputedSearchKey()
    {
        var catalog = new InMemoryProductCatalog();
        var sut = new ProductRegistrar(catalog);
        var gtin = MakeGtin14("0000000000001");

        var key = sut.Register(tenantId: 1, gtin14: gtin, gcpLength: 7, filter: 1, sku: "ITEM-AAA");

        // 返ったキーは Sgtin96Encoder と一致し、そのキーで SKU を解決できる
        Assert.Equal(Sgtin96Encoder.EncodeSearchKey(gtin, 7, 1), key);
        Assert.Equal("ITEM-AAA", catalog.ResolveSku(1, key));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~ProductRegistrarTests`
Expected: コンパイル失敗（`ProductRegistrar` 未定義）。

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EpcForwarder.Core/Products/ProductRegistrar.cs
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~ProductRegistrarTests`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Products/ProductRegistrar.cs tests/EpcForwarder.Core.Tests/Products/ProductRegistrarTests.cs
git commit -m "feat(core): ProductRegistrar(GTIN登録→検索キー)"
```

---

## Task 5: ループ検証（登録 → 取込解決）

GTIN登録で作ったキーが、実タグEPC（同一商品・任意シリアル）をマスクした検索キーと一致し、`ResolveSku` でSKUに解決できることをE2Eで示す。これが「GTINからのフロー」が取込パイプライン（`Sgtin96.DeriveSearchKey`）と噛み合う証明。

**Files:**
- Test: `tests/EpcForwarder.Core.Tests/Products/OnboardingToResolutionTests.cs`

- [ ] **Step 1: Write the test**

```csharp
// tests/EpcForwarder.Core.Tests/Products/OnboardingToResolutionTests.cs
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Products;

public class OnboardingToResolutionTests
{
    private static string MakeGtin14(string body13)
    {
        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var d = body13[i] - '0';
            sum += ((12 - i) % 2 == 0) ? d * 3 : d;
        }
        return body13 + (10 - (sum % 10)) % 10;
    }

    [Fact]
    public void RegisteredProduct_ResolvesFromScannedTagOfAnySerial()
    {
        // --- onboarding ---
        var catalog = new InMemoryProductCatalog();
        var registrar = new ProductRegistrar(catalog);
        var gtin = MakeGtin14("0000000000001");
        var key = registrar.Register(tenantId: 1, gtin14: gtin, gcpLength: 7, filter: 1, sku: "ITEM-AAA");

        // --- 現場で読まれた実タグ(同一商品・任意シリアル) ---
        var tag = Convert.FromHexString(key);
        tag[11] = 0x2A;
        tag[10] = 0x13; // 適当なシリアルを下位に載せる
        var epcHex = Convert.ToHexString(tag);

        // --- 取込パイプラインのSKU化 ---
        var resolvedKey = Sgtin96.DeriveSearchKey(epcHex);
        Assert.Equal(key, resolvedKey);
        Assert.Equal("ITEM-AAA", catalog.ResolveSku(1, resolvedKey));
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~OnboardingToResolutionTests`
Expected: PASS。

- [ ] **Step 3: Run the full suite**

Run: `dotnet test EpcForwarder.sln --nologo`
Expected: 全PASS。

- [ ] **Step 4: Commit**

```bash
git add tests/EpcForwarder.Core.Tests/Products/OnboardingToResolutionTests.cs
git commit -m "test: GTIN登録→取込解決のループ検証"
```

---

## 完了条件

- `dotnet build` 0警告/0エラー、`dotnet test` 全緑。
- 既知GTIN（全partition）から検索キーを生成して商品登録でき、実タグEPCをマスクした検索キーでSKU解決できる。
- 後続（Azureアダプタ計画）: `IProductWriteStore` / `IProductCatalog` を Azure SQL で実装し、登録用の HTTP/Functions エンドポイント（`Api`）を `ProductRegistrar` 越しに公開。CSV一括取込・独自コードのエンコードプラグインは別途。
