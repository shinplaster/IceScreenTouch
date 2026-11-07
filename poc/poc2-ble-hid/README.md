# PoC 2: Windows PC を BLE HID 入力機器にして iPhone を操作する

PC が HID over GATT（サービス 0x1812）のペリフェラルとして振る舞い、iPhone へ次の2種類の入力を送ります。

| Report ID | 種類 | iPhone 側の前提 |
|---|---|---|
| 1 | 相対マウス（ボタン3 + X/Y/ホイール） | AssistiveTouch をオン |
| 2 | 絶対座標タッチ（2点、0〜10000） | アクセシビリティ > ズームをオン（未検証の仮説） |

**検証したいこと**: iOS が Report 2（絶対座標デジタイザ）を受け付けるか。受け付けなければ Report 1 + 原点合わせにフォールバックします。

## セットアップ

```powershell
cd poc/poc2-ble-hid
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

## 動作確認済みの範囲（PC側のみ）

```powershell
.\.venv\Scripts\python.exe ble_hid_poc.py --selftest 3
```

- LE ペリフェラル役に対応しているか、HID サービスの公開が許可されるか、広告が開始されるかを確認します。
- Windows は Device Information（0x180A）の公開をポリシーで禁止しますが、HID の動作には影響しません。
- iPhone 側の動作は未検証です。以下の手順で実機確認が必要です。

## iPhone での検証手順

1. 起動します。
   ```powershell
   .\.venv\Scripts\python.exe ble_hid_poc.py
   ```
2. iPhone で 設定 > Bluetooth を開き、一覧にこの PC（広告名は PC 名）が出たらタップしてペアリングします。ペアリングのコードが表示されたら、PC と iPhone の両方で承認します。
3. コンソールで `status` を実行し、`subscribed: mouse=1 touch=1` となるか確認します。0 のままなら iPhone がレポートを購読していません。

### 検証 A: 絶対座標タッチ（本命）

1. iPhone で 設定 > アクセシビリティ > ズーム をオンにします。拡大率は 1x にします。
2. `grid` を実行し、画面の 9 点が順番にタップされるか確認します。
3. `swipe 5000 8000 5000 2000 300` で上スワイプ、`pinch 5000 5000 500 2500` でピンチを試します。

### 検証 B: 相対マウス（フォールバック）

1. 設定 > アクセシビリティ > タッチ > AssistiveTouch をオンにします。
2. 同じ画面のポインタコントロールで、トラッキングの速度は遅めにしておきます。
3. `home` でポインタが左上に寄るか、`m 200 300` で移動するか、`click` でタップになるか確認します。

## 計測ページで客観的に測る（推奨）

目視では「何点反応したか」「どこに当たったか」を切り分けにくいため、PC 側が計測ページを配信します。

1. 起動時に表示される `probe page: open http://192.168.x.x:8000/` を、iPhone の Safari で開きます（PC と同じ Wi-Fi が必要）。
2. ページを前面に出したまま、コンソールで `probe` を実行します。
3. 次の 3 段階の結果が表示されます。
   - **9 点タップ**: 送信位置、iOS が届けた位置、種別（touch / mouse）、遅延。y は Safari のアドレスバー/ツールバーに隠れないよう 25%〜75% の範囲です。
     `implied viewport origin` の spread が数 px 以下なら、0〜10000 が画面全体に 1:1 で対応しています。
   - **スワイプ**: `pointermove` が届くか。
   - **ピンチ**: 2 本の指（異なる pointer id）が届き、離れていくか。
4. 相対マウスは `home`、`m 200 300`、`click` を実行したあと、`events` で届いたイベントの種別（mouse か touch か）を確認します。
5. ページが開けない場合は、Windows ファイアウォールの受信規則（python.exe、ポート 8000）を確認してください。

## 結果の記録（実測後に埋める）

計測日: 2026-11-05 / 端末: 画面 393×852 pt の iPhone（iOS バージョンは未記録）/ ズームをオン

| 項目 | 結果 |
|---|---|
| ペアリング（Windows の承認含む） | 成功。両レポートを購読（`subscribed: mouse=1 touch=1`） |
| Report 2: 9 点タップ | 9/9 一致。x 誤差は最大 4 単位（0.04%）。原点のばらつきは 0.2 px 以下で、0〜10000 が画面全体に 1:1 で対応 |
| Report 2: swipe | `pointermove` が 16〜17 個届く。y 範囲 94..520 で期待どおり、離す位置も正しい（`lift OK`） |
| Report 2: pinch | 異なる 2 本の指が届き、離れる。範囲 86..165 / 228..307 で期待どおり（`lift OK`） |
| 入力から反映までの遅延 | 42〜187 ms（中央値は約 70 ms）。送信開始から Safari ページの受信までで、BLE と HTTP の往復を含む |
| Report 1: home / m / click | 未計測（ポインタが出ずにタッチが入る現象は未解明。絶対座標が使えるためフォールバック扱い） |

### 検証で見つかった注意点

- 指を離すフレームは、最後の位置のまま Tip Switch を 0 にして送る。(0,0) を送ると、離す瞬間に画面左上へ飛んだ入力になる。
- Windows は Device Information（0x180A）サービスの公開をポリシーで禁止する（`BluetoothError=5`）。HID の動作には影響しない。
- PoC を起動中にもう一つ起動すると、GATT サービスが Aborted になる。再起動前に `quit` で終了する。

## 困ったとき

- **一覧に PC が出ない**: Windows 側の Bluetooth をオフ/オンし、再度起動します。
- **以前のペアリングが残っている**: ディスクリプタを変更したら、iPhone 側の Bluetooth 設定でこの PC の登録を削除（このデバイスの登録を解除）し、Windows 側の Bluetooth 設定でも削除してから再ペアリングします。iOS は Report Map をキャッシュします。
- **`subscribed` が 0**: iPhone を一度ペアリング解除して再接続します。
- **ディスクリプタの調整余地**: 物理サイズ/単位の追加、Contact Identifier の使い方などは `ble_hid_poc.py` の `TOUCH_DESC` で変更できます。
