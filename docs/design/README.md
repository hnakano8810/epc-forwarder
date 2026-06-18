# EPC Forwarder 詳細設計

基本設計（リポジトリ直下 `README.md`）を受けた詳細設計ドキュメント群。

| ドキュメント | 内容 |
|---|---|
| [data-model.md](./data-model.md) | Azure SQL スキーマ（session/reading/snapshot/マスタ）、後勝ちUPSERT、保持、マルチテナント |
| [epc-mask.md](./epc-mask.md) | EPC & Mask 検索キー生成、SGTIN-96仕様、テストベクタ、GTIN→キー エンコーダ、rawモード |
| [webhook-contract.md](./webhook-contract.md) | 送信ペイロード（aggregate/raw/detail）、冪等性、HMAC、配信/再送、スロットリング、SSRF |
| [device-feedback.md](./device-feedback.md) | C2Dプッシュ（到達性）／HTTPクエリAPI（SKU明細・ロケ別明細） |

次工程: これらを土台に .NET ソリューションのスケルトンを生成（基本設計→詳細設計→実装）。
