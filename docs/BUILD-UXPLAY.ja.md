# Windows で UxPlay をビルドする

[English](BUILD-UXPLAY.md)

[UxPlay](https://github.com/FDH2/UxPlay) は、オープンソースの AirPlay 受信ソフトです。公式のリポジトリはソースコードを配っているので、Windows では自分でビルドします。この文書は開発ツールが扱える人向けで、コマンドラインを使用します。

UxPlay は、IceScreen Touch が追える AirPlay 受信ソフトの 1 つです（[受信ソフトを入れる](USER-GUIDE.ja.md#受信ソフトを入れる)を見てください）。IceScreen Touch は、UxPlay のプロジェクトとは一切関係がありません。

## ライセンス

UxPlay は GNU General Public License version 3（GPLv3）です。別のプログラムで、IceScreen Touch の一部ではありません。`IceScreenTouch.exe` には UxPlay のコードは入っていません。ここでビルドした UxPlay は、GPLv3 のもとで使います。

## はじめる前に

- Windows 10 または 11（64 ビット）と PowerShell。
- [scoop](https://scoop.sh/) を入れておきます。この文書は scoop で MSYS2 を入れます。（MSYS2 をほかの方法で入れた場合は、下の「IceScreen Touch に場所を教える」で、その場所の教え方を見てください。）
- 管理者の権限は要りません。ファイアウォールの手順が要るときだけ、必要です。
- 公式のソースを、そのままビルドします。確認したのはコミット `b1be2bd`（UxPlay 1.74）です。新しいコミットは確認していません。

## 1. MSYS2 を入れる

```powershell
scoop install msys2
```

## 2. MSYS2 を更新して、パッケージを入れる

最初の更新は、コアを更新したところで終わることがあるので、2 回実行します。

```powershell
$env:MSYSTEM = 'UCRT64'
$env:CHERE_INVOKING = '1'
$bash = "$env:USERPROFILE\scoop\apps\msys2\current\usr\bin\bash.exe"

& $bash -lc 'pacman -Syu --noconfirm'
& $bash -lc 'pacman -Syu --noconfirm'
& $bash -lc 'pacman -S --needed --noconfirm mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-ninja mingw-w64-ucrt-x86_64-libplist mingw-w64-ucrt-x86_64-gstreamer mingw-w64-ucrt-x86_64-gst-plugins-base mingw-w64-ucrt-x86_64-gst-plugins-good mingw-w64-ucrt-x86_64-gst-plugins-bad mingw-w64-ucrt-x86_64-gst-libav mingw-w64-ucrt-x86_64-openssl mingw-w64-ucrt-x86_64-pkgconf git'
```

## 3. ソースを取得してビルドする

手順 2 と同じ PowerShell のウィンドウで続けてください（コマンドが `$bash` を使います）。置き場所のフォルダを決めます。`<you>` は Windows のユーザー名に読み替えてください。下のコマンドはパスを引用符で囲んでいないので、パスに空白のないフォルダにします。MSYS2 でのパスは、`C:\Users\<you>\UxPlay-build` を `/c/Users/<you>/UxPlay-build` と書きます。

```powershell
New-Item -ItemType Directory -Force 'C:\Users\<you>\UxPlay-build' | Out-Null
& $bash -lc 'cd /c/Users/<you>/UxPlay-build && git clone https://github.com/FDH2/UxPlay.git && cd UxPlay && git checkout b1be2bd && mkdir build && cd build && cmake -G Ninja .. && ninja'
```

できるプログラムは `C:\Users\<you>\UxPlay-build\UxPlay\build\uxplay.exe` です。UxPlay 1.74 は mDNS を内蔵しているので、ビルドに Bonjour は使いません。

## 4. 初回の起動とファイアウォール

初回の起動で、Windows が `uxplay.exe` をファイアウォールで許可するかを聞きます。**許可してください。** 許可しないと、Windows がブロックの規則を作り、iPhone から接続できません。ブロックの規則は、許可の規則より優先されます。

ブロックの規則を直すには、Windows PowerShell（Windows に入っているもの）を**管理者として**開きます。次のコマンドは、これで確認しました。このコマンドは、`uxplay.exe` の規則を消し、ローカルサブネットからの接続だけを受け付ける規則を 1 つ足します（2 行目は 1 分ほどかかることがあります）。

```powershell
$exe = 'C:\Users\<you>\UxPlay-build\UxPlay\build\uxplay.exe'
Get-NetFirewallRule -Direction Inbound | Where-Object { (($_ | Get-NetFirewallApplicationFilter).Program) -ieq $exe } | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'UxPlay' -Direction Inbound -Action Allow -Program $exe -Profile Any -Protocol Any -RemoteAddress LocalSubnet
```

## 5. IceScreen Touch に場所を教える

1. IceScreen Touch の「設定」タブを開きます。
2. 「UxPlay を起動する」を選びます。
3. 「uxplay.exe の場所」の「参照…」を押し、ビルドした `uxplay.exe` を選びます。
4. 「MSYS2 の ucrt64\bin（空欄 = scoop の既定の場所）」は、「設定」タブの「詳細設定（テストや問題の調査用）」の中にあります。その項目を開いてください。上のように scoop で MSYS2 を入れたときは、空欄のままにします。ほかの場所に入れたときは、その `ucrt64\bin` のフォルダを入れます。
5. 「AirPlay の名前（iPhone の画面ミラーリングに出る名前）」の欄は、iPhone の画面ミラーリングの一覧に出る名前を決めます（既定は「IceScreen Touch」）。
6. 「保存して適用」を押します。

IceScreen Touch が UxPlay を起動し、遅延を小さくするデコーダーのオプションを付けます。IceScreen Touch を止めると UxPlay も止まります。iPhone のコントロールセンター > 画面ミラーリングで、AirPlay の名前を選びます。

## 手で起動する（任意）

UxPlay を自分で起動して、IceScreen Touch にそのウィンドウを追わせることもできます。先に MSYS2 の `ucrt64\bin` を `PATH` の先頭に足し、デコーダーのオプションを 2 つ付けます。付けないと映像が遅れます。

```powershell
$env:PATH = "$env:USERPROFILE\scoop\apps\msys2\current\ucrt64\bin;$env:PATH"
& 'C:\Users\<you>\UxPlay-build\UxPlay\build\uxplay.exe' -n "IceScreen Touch" -nh -vs d3d11videosink -vsync no -vd "d3d11h264dec compliance=flexible" -vc d3d11convert
```

次に、IceScreen Touch の「状態」タブで、「UxPlay（起動済み）」の「使う」を押します。自分で起動した UxPlay からは、IceScreen Touch は端末の名前を受け取れません。そのため、英字も数字も含まない名前の端末は、ミラーリングしている端末として見分けられません。
