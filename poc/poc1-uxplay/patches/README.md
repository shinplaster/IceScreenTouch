# UxPlay へのパッチ（GPLv3）

このフォルダのパッチは [UxPlay](https://github.com/FDH2/UxPlay)（FDH2/UxPlay、GNU General Public License version 3）への変更です。UxPlay の派生物なので、**このフォルダのファイルは GPLv3 で配布します**（リポジトリの他の部分の MIT ライセンスは適用されません）。GPLv3 の本文は UxPlay のリポジトリの `LICENSE` を参照してください。

| ファイル | 内容 | 元にした版 |
|---|---|---|
| `0001-print-sink-caps.patch` | 描画の実サイズを `iScrMirror: sink-caps WxH par n/d` として標準出力に出す | UxPlay 1.74（コミット `b1be2bd`） |

使い方とビルドの手順は `../README.md` にあります。IceScreen Touch の本体（`IceScreenTouch.exe`）には UxPlay もこのパッチも入っていません。

## License

The patches in this folder are changes to [UxPlay](https://github.com/FDH2/UxPlay) (FDH2/UxPlay), which is licensed under the GNU General Public License version 3. They are a derivative of UxPlay, so **the files in this folder are distributed under GPLv3**, not under the MIT License (`LICENSE` at the root of the repository) that covers the rest of the repository. For the text of GPLv3, see the `LICENSE` file of the UxPlay repository. `IceScreenTouch.exe` contains neither UxPlay nor these patches.
