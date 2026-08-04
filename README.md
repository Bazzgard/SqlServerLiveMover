# SQL Server Live Mover

稼働中のSQL Serverから別環境のSQL Serverへ、指定した一部テーブルをコピーまたは比較差分更新するWindows向けツールです。

- 移行元には`SELECT`だけを実行
- Change Trackingや移行元トリガーの追加は不要
- テーブルごとに4種類の処理方式を選択
- 移行先の事前バックアップ、一覧管理、復元、削除に対応
- GUIとCLIの両方から実行可能
- 管理ファイルは実行ファイルのフォルダ内で完結

> このツールは継続同期ではありません。実行時点のデータを一回単位でコピーまたは全件比較します。

## 動作の前提

- 移行元・移行先のデータベースとテーブルが作成済みであること
- 差分更新では移行元と移行先に対応する主キーがあること
- 主キーの列名と順序が移行元・移行先で一致すること
- `columns`で列を限定する場合も主キー列をすべて含めること
- 移行元には読み取り権限、移行先には選択した処理に必要な書き込み権限があること
- GUI版はWindows上で動作すること

## デスクトップUIの使い方

### 1. 接続と実行設定

メイン画面右上の「接続と実行設定」を開き、次を入力します。

- 移行元の接続文字列
- 移行先の接続文字列
- 読み取り整合性
- バッチ行数
- コマンドタイムアウト

SQL Server認証の例：

```text
Server=localhost,1433;Database=DLSDB;User ID=your_user;Password=your_password;Encrypt=True;TrustServerCertificate=True
```

`Server`にはサーバー名とポート、`Database`にはデータベース名を指定します。

```text
Server=SOURCE-SERVER;Database=SourceDatabase;...
```

設定画面のボタンは次の3つです。

- **キャンセル**: 編集内容を反映せず閉じる
- **読込**: 保存済みの設定JSONを読み込む
- **保存**: JSONへ保存し、メイン画面へ反映して閉じる

前回使用した設定JSONが存在する場合、「読込」のファイル選択画面へファイル名とフォルダを初期表示します。初回は手動でJSONを選択してください。

### 2. 対象テーブル

メイン画面には、移行元・移行先のサーバー名とデータベース名が表示されます。

「＋ 追加」で対象テーブルを追加し、「－ 削除」で選択中の行を外します。

| 項目 | 内容 | 例 |
|---|---|---|
| 移行元 | 読み取るスキーマ名とテーブル名 | `dbo.Customers` |
| 移行先 | 書き込むテーブル。空欄なら移行元と同じ | `dbo.Customers` |
| 列（カンマ区切り） | 対象列。空欄ならコピー可能な全列 | `Id, Name, UpdatedAt` |
| 処理方式 | テーブルごとのコピー方法 | `差分更新（削除なし）` |

### 3. 処理方式

`operationMode`でテーブルごとの処理方式を選択します。

| 画面表示 | JSON値 | 動作 |
|---|---|---|
| 差分更新（削除なし） | `upsertKeep` | 追加・更新を反映し、移行先だけにある行は残す |
| 差分更新（削除あり） | `upsertDelete` | 追加・更新に加え、移行元にない移行先行を削除する |
| 空テーブルのみ | `emptyOnly` | 移行先が空の場合だけ全件コピーする。1行でもあれば停止 |
| 全削除してコピー | `replace` | 同じトランザクション内で移行先を空にしてから全件コピーする |

「差分更新（削除あり）」では事前バックアップが必須です。

旧形式の`copyMode`、`deleteMissing`、`initialLoadMode`も読み込めますが、GUIから保存すると`operationMode`形式へ変換されます。

### 4. 実行

画面下部のボタンを順番に実行します。

1. **事前検査**: 接続、テーブル構造、主キー、列、整合性設定を読み取り専用で検査
2. **コピー／差分更新**: 選択した処理方式で移行
3. **件数検証**: 移行元と移行先の件数を比較

実行内容とエラーは画面の実行ログへ表示されます。実行中は「中止」でキャンセルを要求できます。

「差分更新（削除なし）」では移行先だけの行を残すため、移行後の件数が移行元より多くなる場合があります。また、移行元がコピー後も更新されている場合、件数検証時点で差が生じることがあります。

### 比較して移行

メイン画面の「比較して移行」を開くと、移行元と移行先を左右に並べて確認しながら実行できます。

- 左の対象一覧で、移行するテーブルを1件選択
- 一覧で選択中のテーブルについて、移行元・移行先の件数、列数、主キーを比較
- 左右それぞれの先頭1,000件を読み取り専用でプレビュー
- 主キー列を水色で強調し、同じ主キーの行を左右で同じ位置に表示
- 片側にしかない主キーは、存在しない側へ空行を挿入して位置を維持
- 左右の縦・横スクロールを同期
- 左右のプレビュー上で、移行するデータを1件または複数件選択
- 「テーブルを事前検査」で、選択中の1テーブルを検査
- 「選択したデータを移行」で、選択行だけを主キーにより追加・更新
- 移行完了後にプレビューを自動更新し、件数差を再確認

比較画面の選択データ移行では、メイン画面で設定した処理方式にかかわらず、選択行の追加・更新だけを行います。未選択行の更新・削除やテーブルの全削除は行いません。移行元に存在しない空行は選択しても移行対象になりません。

プレビュー表示上の件数一致は、行の内容が完全に一致することを保証するものではありません。実際の列構造・主キー・型の互換性は事前検査で確認してください。

## 稼働中DBの読み取り整合性

`sourceConsistency`で次のいずれかを選択します。

- `snapshot`（既定・推奨）: 全対象テーブルを同じ時点で読み取り、移行元の書き込みを妨げません。移行元で`ALLOW_SNAPSHOT_ISOLATION ON`が有効である必要があります。
- `locked`: 永続設定を変更せず整合性を保ちますが、コピー中の更新・追加がロック待ちになる場合があります。
- `readCommitted`: 移行元への影響を抑えますが、複数テーブルが完全に同じ時点の状態になる保証はありません。

設定状態は事前検査が読み取り専用で確認します。

```sql
SELECT snapshot_isolation_state_desc, is_read_committed_snapshot_on
FROM sys.databases
WHERE database_id = DB_ID();
```

ツールはデータベース設定を変更しません。`snapshot`を使うための設定変更はDB管理者が明示的に実行します。

```sql
ALTER DATABASE [SourceDatabase] SET ALLOW_SNAPSHOT_ISOLATION ON;
```

## Change Trackingなしの差分更新

差分更新では、移行元の対象データを移行先SQL Serverの`tempdb`にある`#SqlMoverStage`へ転送し、主キーで比較します。

1. 移行元の対象データを一時テーブルへ読み込む
2. 主キーが一致し、対象列が異なる行だけ`UPDATE`
3. 主キーが存在しない行を`INSERT`
4. `upsertDelete`の場合だけ、移行元にない移行先行を`DELETE`
5. 追加・更新・削除件数をログへ表示

`#SqlMoverStage`は同じ接続内だけに存在し、処理終了または接続終了で削除されます。永続テーブルではありません。

Change Trackingを使わないため、移行先への書き込みは差分だけですが、移行元からは対象データを全件読み取ります。差分更新には主キーが必要です。`text`、`ntext`、`image`、`xml`、`geometry`、`geography`、`hierarchyid`列は差分比較対象にできません。

差分更新は移行先に対して通常の`UPDATE`、`INSERT`、`DELETE`を実行するため、該当する移行先トリガーが実行されます。

## 事前バックアップ

画面下部の「事前バックアップ」は既定で有効です。差分更新または「全削除してコピー」で移行先に既存データがある場合、変更前に同じスキーマへ永続バックアップテーブルを作成します。移行先が空なら自動的に省略します。

```text
dbo.__SqlMoverBackup_Customers_20260802_073000_a1b2c3d4
```

- バックアップはコピー処理とは別のトランザクションで確定する
- その後のコピーが失敗してもバックアップは残る
- 正常終了後も自動削除しない
- 実行のたびに一意な名前で作成するため、複数回実行すると複数個作成される
- データと列値を保存する
- `rowversion`は値を保持するため`binary(8)`として保存する
- 主キー、外部キー、インデックス、トリガー、権限は複製しない

移行結果を確認したあと、不要なバックアップはバックアップ管理画面から削除してください。

## バックアップ管理と復元

メイン画面右上の「バックアップ管理」から、現在の移行先DBにあるバックアップを一覧表示できます。

- **選択したバックアップを復元**: 対象テーブルの既存行を削除し、バックアップ行を挿入
- **復元前に事前バックアップ**: 現在の復元先を`pre-restore`バックアップとして保存してから復元
- **削除**: 選択したバックアップテーブルを完全に削除
- **更新**: 一覧を再取得

`pre-restore`はSQL Server標準機能や操作ログではありません。このツールが「復元直前の状態」を保存する通常のバックアップテーブルで、作成理由として`pre-restore`を記録しています。

復元は、対象テーブルの既存行を`DELETE`してからバックアップ行を挿入するトランザクションです。

- 外部キーにより削除できない場合は失敗してロールバックする
- 制約を自動的に無効化しない
- 復元時の`DELETE`と`INSERT`では対象テーブルのトリガーが実行される
- `rowversion`はSQL Serverにより新しい値が生成される

バックアップの実データは移行先DBのバックアップテーブルに保存されます。管理カタログだけをローカルへ保存します。

接続先DBに`dbo.__SqlMoverBackupCatalog`は作成しません。

## ローカルに保存するファイル

アプリが自動作成する管理ファイルは、すべて実行ファイルと同じ場所の`logs`フォルダへ保存します。`%LOCALAPPDATA%`は使用しません。

```text
＜実行ファイルのフォルダ＞
└─ logs
   ├─ last-session.json
   ├─ backup-catalog.json
   └─ last-config-path.txt
```

| ファイル | 内容 |
|---|---|
| `logs\last-session.json` | 前回の画面内容。次回起動時に自動復元 |
| `logs\backup-catalog.json` | バックアップ名、復元先、作成日時、行数、作成理由など |
| `logs\last-config-path.txt` | 前回使用した設定JSONのパス |

ユーザーが「保存」で作る`mover.json`は、ファイル選択画面で指定した場所に保存されます。

JSONの用途は次の3種類です。

1. ユーザーが保存する移行設定JSON
2. 前回画面状態の`last-session.json`
3. バックアップ管理の`backup-catalog.json`

1と2は同じ移行設定構造です。3はバックアップ管理専用の構造です。安全な置き換えのため、一時的に`.tmp`ファイルを作成する場合がありますが、通常は保存完了後に残りません。

`backup-catalog.json`には接続先を識別するハッシュを保存しますが、接続文字列やパスワードは保存しません。ローカルJSONに記録がないバックアップは命名規則から復元先を推定し、管理画面の「推定」にチェックを表示します。

## 設定JSON

接続文字列をファイルへ直接保存したくない場合は、`${NAME}`形式で環境変数を参照できます。

```powershell
$env:SQL_MOVER_SOURCE = "Server=source;Database=SourceDb;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"
$env:SQL_MOVER_TARGET = "Server=target;Database=TargetDb;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"
```

設定例：

```json
{
  "sourceConnectionString": "${SQL_MOVER_SOURCE}",
  "targetConnectionString": "${SQL_MOVER_TARGET}",
  "batchSize": 5000,
  "commandTimeoutSeconds": 120,
  "sourceConsistency": "snapshot",
  "tables": [
    {
      "source": "dbo.Customers",
      "target": "dbo.Customers",
      "columns": ["Id", "Name", "UpdatedAt"],
      "operationMode": "upsertKeep",
      "backupBeforeCopy": true
    }
  ]
}
```

設定JSONと`last-session.json`には接続文字列が平文で保存されます。パスワードを残したくない場合は、GUIの接続文字列にも`${SQL_MOVER_SOURCE}`のような環境変数参照を入力してください。

## ビルドと実行

対象フレームワークは.NET 9です。

```powershell
dotnet restore --configfile NuGet.Config
dotnet build --no-restore
```

### デスクトップUI

```powershell
dotnet run --project src/SqlServerLiveMover.Gui
```

ビルド済みGUIの標準出力先：

```text
src\SqlServerLiveMover.Gui\bin\Debug\net9.0-windows\SqlServerLiveMover.Gui.exe
```

### CLI

```powershell
dotnet run --project src/SqlServerLiveMover -- preflight mover.json
dotnet run --project src/SqlServerLiveMover -- copy mover.json
dotnet run --project src/SqlServerLiveMover -- verify mover.json
```

引数を省略した場合の設定ファイル名は`mover.json`です。

## 紹介・ヘルプページ

静的な紹介ページと詳細な使い方ページを`html`フォルダに収録しています。

```text
html\index.html    製品紹介・ダウンロードページ
html\help.html     詳細な使い方
html\site-config.js ダウンロード公開設定
```

`html\site-config.js`の`downloadPath`が空の場合、ナビゲーション、ダウンロードボタン、ダウンロード欄をすべて非表示にします。

```javascript
window.SqlMoverSiteConfig = {
  downloadPath: "",
  version: ""
};
```

公開する場合だけパスとバージョンを設定します。

```javascript
window.SqlMoverSiteConfig = {
  downloadPath: "downloads/SqlServerLiveMover.zip",
  version: "1.0.0"
};
```

## 安全性と制約

- 事前検査とコピーは移行元に対して`SELECT`だけを実行する
- SQL識別子を検証・引用し、設定値をSQLへそのまま連結しない
- `emptyOnly`では移行先が空でない場合に停止する
- 既存データがあり、事前バックアップが有効なら変更前の永続バックアップを作成する
- 各テーブルのコピーを移行先トランザクションで囲む
- 途中で失敗したテーブルは、そのテーブルのコピー開始前の状態へ戻す
- 複数テーブルの後半で失敗した場合、完了済みテーブルは残る
- `IDENTITY`値を保持する
- 移行先のCHECK制約と外部キー制約を検査しながらコピーする
- 「空テーブルのみ」と「全削除してコピー」の一括コピーでは移行先トリガーを実行しない
- 差分更新とバックアップ復元では通常のDMLを使用するため、該当する移行先トリガーが実行される
- 計算列、`rowversion`、システム生成列はコピーせず、移行先SQL Serverに生成させる
- 同じDB・同じテーブルを移行元と移行先に指定した場合は停止する

すべてのテーブルを一括ロールバックする方式は、巨大なトランザクションログと長時間ロックを避けるため採用していません。

## 必要な権限

選択した処理に応じて、次の権限が必要です。

- 移行元テーブルの`SELECT`
- 移行先テーブルの`SELECT`、`INSERT`、`UPDATE`
- 削除ありの差分更新・復元で使用する`DELETE`
- 全削除コピーで使用する`TRUNCATE TABLE`に必要な権限
- バックアップテーブル作成・削除の`CREATE TABLE`、`DROP TABLE`
- 実行ファイルのフォルダへ`logs`を作成・更新する書き込み権限
