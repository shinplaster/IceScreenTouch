# UxPlay へのパッチ（GPLv3）

このフォルダのパッチは [UxPlay](https://github.com/FDH2/UxPlay)（FDH2/UxPlay、GNU General Public License version 3）への変更です。UxPlay の派生物なので、**このフォルダのファイルは GPLv3 で配布します**（リポジトリの他の部分の MIT ライセンスは適用されません）。GPLv3 の本文は UxPlay のリポジトリの `LICENSE` を参照してください。

| ファイル | 内容 | 元にした版 |
|---|---|---|
| `0001-print-sink-caps.patch` | 描画の実サイズを `iScrMirror: sink-caps WxH par n/d` として標準出力に出す | UxPlay 1.74（コミット `b1be2bd`） |

使い方とビルドの手順は `../README.md` にあります。IceScreen Touch の本体（`IceScreenTouch.exe`）には UxPlay もこのパッチも入っていません。
