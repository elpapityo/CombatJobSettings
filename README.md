# Combat Job Settings

FFXIV Dalamud向けの戦闘設定統合プラグインです。

BossMod / BossMod Reborn / Rotation Solver Reborn (RSR) / Wrath Combo の主要設定を1つの画面で確認・変更し、JOB別・ロール別・名前付きプリセットとして保存・適用できます。

## 主な機能

- BossMod AI ON/OFF
- BossMod Reborn AI / Follow target / 距離設定
- RSR 停止 / 自動 / 手動、Engage settings、敵優先条件
- Wrath Auto Rotation、攻撃設定・回復設定
- JOB別設定 / ロール別設定 / 名前付きプリセット
- 保存済みデフォルトへの復元
- JOB変更時の自動適用
- CJS Mini
- 各本家プラグイン設定画面へのショートカット

## 導入

Dalamud設定の「試験的機能」に、以下のカスタムプラグインリポジトリURLを追加してください。

`https://raw.githubusercontent.com/elpapityo/CombatJobSettings/main/pluginmaster.json`

追加後、プラグインインストーラーで **Combat Job Settings** を検索してインストールします。

## 初回設定

1. 普段使っている各戦闘プラグインの設定を整えます。
2. `/cjs` でCJSを開きます。
3. 「設定」タブで **現在の実設定をデフォルトとして保存** を実行します。
4. JOB別・ロール別・名前付きプリセットを必要に応じて保存します。

自動適用の優先順位は **JOB設定 → ロール設定 → 保存済みデフォルト** です。

## 対応プラグイン

- BossMod
- BossMod Reborn
- Rotation Solver Reborn
- Wrath Combo

対象プラグインが未導入・未ロードの場合、そのプラグイン分だけ処理をスキップします。

## コマンド

`/cjs`

## 作者

Elpa

## 注意

連携先プラグインの更新で内部構造やコマンドが変更された場合、一部の取得・変更機能が動作しなくなる場合があります。
