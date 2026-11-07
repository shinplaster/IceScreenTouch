# PoC 1: UxPlay で iPhone の画面を Windows PC にミラーリングする

公式の [FDH2/UxPlay](https://github.com/FDH2/UxPlay)（v1.74、GPLv3）をソースからビルドし、iPhone の AirPlay 画面ミラーリングの受信側にします。

コミュニティ配布の Windows 版バイナリは未署名の可能性があるため使わず、公式ソースを MSYS2 でビルドしました。

## 結果

計測日: 2026-11-05 / 端末: iPhone 15 Pro（iPhone16,1）/ PC は有線 LAN、iPhone は同一ネットワーク

| 項目 | 結果 |
|---|---|
| ミラーリング接続 | **成功**。コントロールセンターの「画面ミラーリング」に `iScrMirror` が表示され、接続できた |
| 遅延（体感） | **ほとんど気にならないレベル** |
| 受信した映像 | 498×1080（iPhone の画面比 393:852 と一致。画面全体が映る） |
| 方式 | RAOP + FairPlay（`/fp-setup`）。iPhone 側にアプリは不要 |
| PC 側の構成 | UxPlay → GStreamer → `d3d11videosink` |

遅延は目視の印象のみで、数値の計測はしていません。数値が必要なら、PC 画面と iPhone を同時撮影するか、画面にタイマーを映して比べます。

## セットアップ（再現手順）

管理者権限は不要です（ファイアウォールの設定だけ昇格が必要）。

```powershell
# 1. MSYS2（scoop 経由）
scoop install msys2
$env:MSYSTEM='UCRT64'; $env:CHERE_INVOKING='1'
$bash = "$env:USERPROFILE\scoop\apps\msys2\current\usr\bin\bash.exe"
& $bash -lc 'pacman -Syu --noconfirm'   # 2 回実行する（コア更新で一度終わるため）
& $bash -lc 'pacman -S --needed --noconfirm mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-ninja mingw-w64-ucrt-x86_64-libplist mingw-w64-ucrt-x86_64-gstreamer mingw-w64-ucrt-x86_64-gst-plugins-base mingw-w64-ucrt-x86_64-gst-plugins-good mingw-w64-ucrt-x86_64-gst-plugins-bad mingw-w64-ucrt-x86_64-gst-libav mingw-w64-ucrt-x86_64-openssl mingw-w64-ucrt-x86_64-pkgconf git'

# 2. ソース取得とビルド（poc/poc1-uxplay/ で実行。UxPlay/ は .gitignore 済み）
# <repo> はこのリポジトリの場所（MSYS2 のパス表記、例: /c/Users/<user>/.../IceScreenTouch）に読み替える
& $bash -lc 'cd <repo>/poc/poc1-uxplay && git clone --depth 1 https://github.com/FDH2/UxPlay.git && cd UxPlay && mkdir build && cd build && cmake -G Ninja .. && ninja'

# 3. ファイアウォール（管理者の昇格が必要。UAC の承認ダイアログが出る）
Start-Process pwsh -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',"$PWD\allow_firewall.ps1"

# 4. 起動
.\run_uxplay.ps1            # 通常
.\run_uxplay.ps1 -DebugLog  # デバッグ出力つき
```

- v1.74 は内蔵の mDNS 実装を持つため、Bonjour SDK は不要です（この PC には Bonjour サービスも動いています）。
- Bonjour 版を使いたい場合は `cmake -DUSE_DNS_SD=1` でビルドします。

## ビルドのパッチ（PoC 3 用）

`patches/0001-print-sink-caps.patch` は、映像シンクが描画するフレームの実サイズを標準出力に出す小さなパッチです（`iScrMirror: sink-caps 498x1080 par 1/1`）。iPhone を回転させると、その都度新しいサイズが出ます。PoC 3 は、この出力からタッチ座標の変換とウィンドウの追従を行います。

```powershell
# 元のコミット b1be2bd に当てる
& $bash -lc 'cd <repo>/poc/poc1-uxplay/UxPlay && git apply ../patches/0001-print-sink-caps.patch && mkdir -p build-isr && cd build-isr && cmake -G Ninja .. && ninja'
```

- パッチは 2 か所です。(1) シンクが描画する実サイズの出力、(2) コーデック更新パケットのヘッダー 128 バイトの 16 進ダンプ（`-d` のときだけ。向きの手がかりの調査用）。
- パッチ入りは `UxPlay/build-isr/uxplay.exe`、パッチなしは `UxPlay/build/uxplay.exe`（実行中だと上書きできないため、別フォルダにしています）。
- 出力はログ出力のみで、UxPlay の動作は変えません。

## 使い方

1. `run_uxplay.ps1` を起動します。
2. iPhone のコントロールセンター → 画面ミラーリング → `iScrMirror` を選びます。
3. PC に映像のウィンドウが開きます。Alt+Enter で全画面の切り替えができます。

## ファイアウォールについて

初回起動時に、Windows が `uxplay.exe` の受信規則（`Query User`）を自動生成します。許可を押さなかった場合はブロック規則になり、iPhone から接続できません。ブロック規則は許可規則より優先されます。

`allow_firewall.ps1` は、この自動規則を削除し、**同じローカルサブネットからの受信のみ**を許可する規則を 1 件追加します。管理者権限が必要です。

## 既知の課題・確認したいこと

- **画面の向き**: 横向きにしたときの映像サイズと、タッチ座標の対応づけは未確認です。
- **遅延の数値化**: PoC 1 の時点では体感のみ。2026-11-06 に IceScreen Touch の遅延の計測で測ると、既定のデコーダー（`decodebin` が選ぶ `d3d12h264dec`）では映像の分が 255 ms あった。**デコーダーが並べ替えに備えて 7〜8 フレーム溜めていたため**で、`-vd "d3d11h264dec compliance=flexible" -vc d3d11convert` で 60 ms になった。UxPlay を手で起動して IceScreen Touch で追うときも、この 2 つを付けてください。IceScreen Touch が起動するときは自動で付きます。
- **画質**: 受信は 498×1080 でした。`-s` オプションで要求解像度を上げられる可能性がありますが、未検証です。
- **DRM 付き映像**: Apple TV アプリなどの保護された映像は映りません。
- **mDNS**: 有線の PC と Wi-Fi の iPhone で見つかりましたが、ルーターによってはマルチキャストを橋渡ししない場合があります。
- **ライセンス**: UxPlay は GPLv3 です。組み込んで配布する場合は、ライセンスの扱いを確認する必要があります。FairPlay 関連の実装は、非公式のリバースエンジニアリングに依存しています。
