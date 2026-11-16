# IceScreen Touch API

[English](API.md)

IceScreen Touch には、自動操作 API があります。同じ PC のスクリプトや AI エージェントが、マウスとキーボードで操作するときと同じように iPhone や iPad を操作できます。タップ、スワイプ、ピンチ、文字入力、ホームキーなどです。入力は、あなた自身の操作と同じ Bluetooth の経路で届きます（PC が Bluetooth のタッチ入力機器とキーボードのふりをします。[USER-GUIDE.ja.md](USER-GUIDE.ja.md) を見てください）。API に接続できるのは、この PC だけです。

このページは使い方の案内です。全項目（フィールド、既定値、上限、エラー）の仕様は、OpenAPI の文書 [api/openapi.yaml](api/openapi.yaml) にあります。動いているアプリは、同じ文書を `GET /v1/openapi.json` で返します。

## 目次

- [必要なもの](#必要なもの)
- [オンにする](#オンにする)
- [セキュリティ](#セキュリティ)
- [すぐに試す](#すぐに試す)
- [座標](#座標)
- [スクリーンショット](#スクリーンショット)
- [エンドポイントと上限](#エンドポイントと上限)
- [WebSocket のストリーム](#websocket-のストリーム)
- [エラー](#エラー)
- [Python クライアントと MCP サーバー](#python-クライアントと-mcp-サーバー)

## 必要なもの

- Microsoft Store で購入した IceScreen Touch、またはソースコードから自分でビルドしたアプリ。試用中は API が起動しません。API の動作中にライセンスが API を対象としなくなると、`POST /v1/cancel` を除くすべての呼び出しが `403` `purchase_required` を返します（止める命令はいつでも使えます）。
- 端末で**ズーム**をオンにすること（設定 > アクセシビリティ > ズーム）。マウスで操作するときと同じです。オフだと、タッチが届きません。
- 動作中のミラーリングのセッション（「状態」タブに状態が出ます）と、Bluetooth でつながった端末。
- `screen` と `pixel` の座標には、端末の映像を映している AirPlay 受信ソフトのウィンドウが要ります。ウィンドウを閉じたり最小化したりすると、これらの呼び出しは `409` `no_picture` を返します。`device` の座標は映像が要りません（[座標](#座標)を見てください）。

API が操作するのは、アプリが今入力を送っている端末 1 台です（「状態」タブの「入力先」。`PUT /v1/target` で変えられます）。

## オンにする

API は**初期状態でオフ**です。

1. IceScreen Touch の「設定」タブを開き、「自動操作 API」の項を探します。
2. 「この PC のスクリプトから端末を操作できるようにする」をオンにします。
3. ポートを確かめます。初期値は **47821** です。1024 から 65535 までで、8000（計測ページが使います）は選べません。ほかのプログラムがそのポートを使っているとアプリが知らせます。アプリが自分で別のポートを探すことはありません。
4. 「保存して適用」を押します。このときトークンが作られます。

アプリには「自動操作 API: 127.0.0.1:<ポート> で待ち受け中」と出て、呼び出しやストリームの動作中は「自動操作 API: スクリプトが操作中」と出ます。起動できないときは「自動操作 API: 起動できません」と出ます（ほかのプログラムがポートを使っているか、「ログ」タブを見てください）。

**トークン。** スクリプトは、この項に出るトークンで認証します（「表示」/「隠す」）。「コピー」でクリップボードに入ります。「作り直す」は確認のあとで新しいトークンを作ります。古いトークンはすぐに使えなくなり、開いている WebSocket のストリームも閉じます。

**スクリプトがポートとトークンを見つける方法。** API がオンの間、アプリは `api.json` を書き（読めるのはあなたの Windows ユーザーだけです）、オフにすると削除します。

```json
{ "port": 47821, "token": "<43 文字>", "pid": 1234 }
```

| インストール | `api.json` の場所 |
|---|---|
| 通常（自分でビルドしたもの） | `%LOCALAPPDATA%\IceScreenTouch\api.json` |
| Microsoft Store | `%LOCALAPPDATA%\Packages\Shin-plaster.IceScreenTouch_5c159ja95e6hm\LocalCache\Local\IceScreenTouch\api.json`（Windows がアプリの書き込みをここに振り向けます） |

`pid` はそのポートを持っているプロセスの ID です。クライアントはトークンを送る前に、そのポートで待ち受けているプロセスが `IceScreenTouch.exe` で、**あなたの** Windows ユーザーで動いていて、トークンを `api.json` から読んだ場合はその `pid` そのものであることを確かめてください。そうでなければファイルが古いか、別のプログラムがポートを取っていて、トークンがそちらへ渡ってしまいます。Python クライアントはこれを行います（下を参照）。クライアントは、まず環境変数 `ICESCREENTOUCH_API`（たとえば `http://127.0.0.1:47821`）と `ICESCREENTOUCH_TOKEN` を見て、次に `api.json` を見ることになっています。

## セキュリティ

- API は **127.0.0.1** だけで待ち受けます（`::1` や、ほかのアドレスでは待ち受けません）。ほかのコンピューターからは届きません。
- **トークンは秘密にしてください。** `GET /v1/openapi.json` と WebSocket の接続開始を除くすべての呼び出しに、`Authorization: Bearer <トークン>` を付けます。トークンを持つものは、マウスとキーボードであなたが端末にできることを何でもできます。**パスコードの入力や購入の承認も含みます。** API は端末がロックされているかどうかを知らず、パスコード画面への入力も止めません。信頼できるスクリプトとエージェントにだけ使い、AI エージェントのツール呼び出しの承認はオンのままにしてください。
- **Web ページからは呼べません。** `Origin` ヘッダーのあるリクエストは、`403` `forbidden_origin` で拒否します（Web ページは必ず `Origin` を付け、スクリプトやコマンドラインのツールは付けません）。`Host` ヘッダーは `127.0.0.1:<ポート>` でなければなりません（`localhost` という名前ではなく、`127.0.0.1` のアドレスを使ってください）。
- **あなた自身の操作が常に優先です。** ミラー画面を操作すると（映像の上のマウス、ホイール、端末へ送るキー、Home、回転）、動作中の操作は止まり、すべての指が離れ、すべてのキーが放されます。REST の呼び出しは `409` `user_interrupted` を返し、ストリームには `interrupted` のイベントが届いて閉じます。あなたの最後の操作から 1 秒のあいだ（マウスやキーを押している間も）、新しい操作は `409` `user_active` になります。API が自分で再開することはありません。やり直すかどうかは、スクリプトが決めます。

## すぐに試す

例は、ポート 47821 とトークンが `api.json` にあるものとして書いてあります。本文は JSON（UTF-8、`Content-Type: application/json`、64 KB まで）です。成功は `200` で JSON を返し、何も返さない操作は `{}` を返します。操作は、端末での動作が終わってから応答します。

### curl（Git Bash や WSL のような Unix 系のシェル）

```sh
TOKEN="<アプリで見たトークン>"
API=http://127.0.0.1:47821
AUTH="Authorization: Bearer $TOKEN"
JSON="Content-Type: application/json"

# アプリ、端末、映像、回転の状態
curl -s -H "$AUTH" $API/v1/status

# 画面の中央をタップ
curl -s -H "$AUTH" -H "$JSON" -d '{"x":0.5,"y":0.5}' $API/v1/touch/tap

# 上へスワイプ（下へスクロール）
curl -s -H "$AUTH" -H "$JSON" \
  -d '{"from":{"x":0.5,"y":0.8},"to":{"x":0.5,"y":0.2},"durationMs":300}' $API/v1/touch/swipe

# 文字入力（US 配列）
curl -s -H "$AUTH" -H "$JSON" -d '{"text":"hello"}' $API/v1/keys/type

# ホームキー
curl -s -H "$AUTH" -X POST $API/v1/home

# 横向きの向きを直す
curl -s -H "$AUTH" -H "$JSON" -X PUT -d '{"landscape":270}' $API/v1/rotation
```

### PowerShell

```powershell
# ポートとトークンを読む（Microsoft Store から入れた場合は上の表の場所を使います）
$api = Get-Content "$env:LOCALAPPDATA\IceScreenTouch\api.json" | ConvertFrom-Json
$base = "http://127.0.0.1:$($api.port)"
$headers = @{ Authorization = "Bearer $($api.token)" }

Invoke-RestMethod "$base/v1/status" -Headers $headers

# タップ
Invoke-RestMethod "$base/v1/touch/tap" -Method Post -Headers $headers `
  -ContentType "application/json" -Body '{"x":0.5,"y":0.5}'

# スワイプ
Invoke-RestMethod "$base/v1/touch/swipe" -Method Post -Headers $headers `
  -ContentType "application/json" `
  -Body '{"from":{"x":0.5,"y":0.8},"to":{"x":0.5,"y":0.2},"durationMs":300}'

# 文字入力
Invoke-RestMethod "$base/v1/keys/type" -Method Post -Headers $headers `
  -ContentType "application/json" -Body '{"text":"hello"}'

# ホーム
Invoke-RestMethod "$base/v1/home" -Method Post -Headers $headers

# 回転
Invoke-RestMethod "$base/v1/rotation" -Method Put -Headers $headers `
  -ContentType "application/json" -Body '{"landscape":270}'
```

### Python

クライアントは `clients/python` にあります（[Python クライアントと MCP サーバー](#python-クライアントと-mcp-サーバー)を見てください）。ポートとトークンは自分で見つけます。

```python
from icescreentouch import Client

with Client() as phone:
    print(phone.get_status())
    phone.tap(0.5, 0.5)                          # screen 座標（0..1）
    phone.swipe((0.5, 0.8), (0.5, 0.2), duration_ms=300)
    skipped = phone.type_text("hello")           # 打てなかった文字が返る
    phone.home()
    phone.set_rotation(landscape=270)
```

## 座標

どの位置にも空間 `space` があります（既定は `screen`）。

| `space` | 意味 | 映像が要るか |
|---|---|---|
| `screen` | **今見えている**画面を 0..1 にしたもの（(0,0) が左上、(1,1) が右下）。横向きなら横長の画面です。回転はアプリが元に戻します。 | 要る |
| `pixel` | 端末のスクリーンショットのピクセル。そのスクリーンショットの大きさ `ref`（`{ "width": W, "height": H }`）が**必須**です。x は 0..W、y は 0..H。 | 要る |
| `device` | デジタイザ自身の縦向きの枠を 0..1 にしたもの。回転しません。 | 要らない |

- 範囲の外の位置は `400` `invalid_request` です。
- `pixel` の `ref` の向き（横長か縦長か）が今の映像と逆なら、スクリーンショットのあとに端末が回転したということで、`409` `picture_changed` です。スクリーンショットを撮り直してください。正方形の `ref` は拒否されません。
- **横向きの左右は検出できません。** アプリはどちらか一方と仮定します。仮定は `GET /v1/status`（と `GET /v1/rotation`）の `rotation`（`landscape` は 90 か 270、`portrait` は 0 か 180）で分かります。タッチの位置が反対になるときは、`PUT /v1/rotation` で直します（アプリの `Ctrl+R` と同じく、保存されます）。上下逆の縦向き（180）も同じです。

## スクリーンショット

`GET /v1/screenshot` は、端末の画面の画像を返します。受信ソフトのウィンドウのうち端末を映している部分（ウィンドウの黒い帯は含みません）を PNG か JPEG にしたものです。`GET /v1/status` の `picture` と全く同じ範囲です。端末には何も送りません。

| クエリのパラメーター | 値 | 既定 |
|---|---|---|
| `format` | `png` か `jpeg`（小文字） | `png` |
| `maxSize` | 16 から 8192 の整数。画像の長い辺のピクセル数です。アプリは画像を小さくするだけで（大きくはしません）、縦横の比は保ちます。 | 映像そのままの大きさ |
| `quality` | 1 から 100 の整数。`format=jpeg` のときだけ使えます。`png` に付けると `400` `invalid_request` です。 | 90 |

パラメーターの名前は大文字と小文字を区別します。知らないパラメーター、2 回書かれたパラメーター、数字だけでない値、範囲の外の値は `400` `invalid_request` です。画像に透明な部分はありません。

応答のヘッダーが、アプリが映像をどう見ていたかを伝えます。

| ヘッダー | 値 |
|---|---|
| `X-IceScreenTouch-Orientation` | `portrait`。映像が縦より横に長いときは `landscape`（正方形は `portrait`） |
| `X-IceScreenTouch-Rotation` | その向きについてアプリが仮定している回転。`0`、`90`、`180`、`270`（`GET /v1/status` の `rotation` と同じです） |
| `X-IceScreenTouch-Source-Size` | `maxSize` で小さくする前の映像の大きさ。たとえば `1179x2556` |

```powershell
Invoke-WebRequest "$base/v1/screenshot?maxSize=1024" -Headers $headers -OutFile shot.png
```

```python
shot = phone.screenshot(max_size=1024)          # shot.size は受け取った画像の大きさ
shot.save("shot.png")
phone.tap_pixel(540, 1200, ref=shot.size)       # その画像のピクセルで位置を指す
```

- **大きさは端末ではなく受信ソフトのウィンドウに従います。** 画像の大きさはウィンドウの中の映像の大きさで、端末の実際の解像度ではありません。スクリーンショットの中の位置を指すときは、`space: "pixel"` にして、そのスクリーンショットの大きさ（受け取った画像の大きさ）を `ref` に渡します。
- **映像がないとき。** 受信ソフトのウィンドウを閉じた、最小化した、別の仮想デスクトップにある、見つからない、または読めなかったときは、`409` `no_picture` です。最小化されたウィンドウは読めません。
- **ほかのウィンドウは画像に写りません。** 画像は受信ソフトのウィンドウだけから作るので、通知、重ねて表示されるもの、手前にあるほかのプログラムは画像に入りません。
- **スクリーンショット中は受信ソフトのウィンドウ枠に色が付きます。** スクリーンショットを撮ってから約 2 秒の間は、受信ソフトのウィンドウの周りに色つきの枠（通常は黄色）が表示されます。この枠は画像には写りません。Windows の取り込みが一時的に使えないとき（まれです）は、枠が出ないまま撮ることがあります。
- **操作ではありません。** スクリーンショットは、タッチ、キー、WebSocket のストリームが動いている間も使えます。`409` `busy` にはなりません。
- **1 秒に最大 10 枚です。API を使うすべてのプログラムの合計で数えます。** 1 秒の間の 11 枚目は、ヘッダー `Retry-After: 1` つきの `429` `rate_limited` です。クエリの確認を通ったリクエストは数えます（そのあとで `409` `not_running` や `409` `no_picture` になった場合も）。`429` で断られたリクエストは数えません。同時に進行中のスクリーンショットが多すぎるときも `429` `rate_limited` です。
- **確認の順番。** 共通の確認（`Origin`、`Host`、トークン）のあとは、ライセンス（`403` `purchase_required`）、クエリ（`400` `invalid_request`）、頻度の上限（`429` `rate_limited`）、`409` `not_running`、`409` `no_picture` の順です。

## エンドポイントと上限

各リクエストとレスポンスの詳細は [api/openapi.yaml](api/openapi.yaml)（またはトークンの要らない `GET /v1/openapi.json`）にあります。

| メソッドとパス | 用途 |
|---|---|
| `GET /v1/status` | アプリ、端末、映像、回転の状態。操作が動いているか |
| `PUT /v1/target` | 入力を送る端末を選ぶ（`null` は自動） |
| `GET /v1/rotation` | アプリが仮定している回転 |
| `PUT /v1/rotation` | 仮定している回転を直す |
| `GET /v1/screenshot` | 端末の画面の画像（PNG か JPEG。[スクリーンショット](#スクリーンショット)を見てください） |
| `POST /v1/touch/tap` | タップ（1 本指。`count` は 1 から 10） |
| `POST /v1/touch/long-press` | 長押し（既定は 800 ms） |
| `POST /v1/touch/swipe` | 直線のスワイプかドラッグ（前後に止める時間も指定できます） |
| `POST /v1/touch/path` | 1 本指を任意の経路でドラッグ（時刻つきの点を 1000 個まで） |
| `POST /v1/touch/pinch` | 2 本指のピンチ |
| `POST /v1/keys/press` | キーを 1 つ押す（修飾キー `cmd`、`shift`、`option`、`ctrl` つき） |
| `POST /v1/keys/type` | 文字入力 |
| `POST /v1/home` | ホームキーを押す |
| `POST /v1/cancel` | 動作中の操作かストリームを止める |
| `GET /v1/stream` | 低レベルの入力用の WebSocket（下を見てください） |
| `GET /v1/openapi.json` | 仕様（トークン不要） |

**上限**

- **操作は同時に 1 つだけです。** タッチ、キー、ホーム、`PUT /v1/rotation`、`PUT /v1/target`、ストリームのどれかが動いている間、ほかの操作は `409` `busy` になります。API は待たせません。`GET /v1/status`、`GET /v1/rotation`、`GET /v1/openapi.json` はいつでも使え、`GET /v1/screenshot` は `busy` になりません。アプリの計測（テスト）のページが動いている間も、操作は `409` `busy` です。
- **1 つの操作は 60 秒を超えられません**（保持、所要時間、間隔、経路の最後の `t` の合計）。超えると `400` `invalid_request` です。
- タッチは少なくとも **70 ms** 保ちます（それより短い `holdMs` は 70 に引き上げます）。動かす指は 16 ms ごとに更新し、最後の位置で必ず離します。
- `POST /v1/cancel` は動作中の操作を止めます。止められたリクエストは `499` `cancelled` を返します。
- **文字入力**は **US 配列**のキーを送ります。`keys/type` は 1000 文字まで、見積もりの時間は 60 秒以内です（長い文章は分けてください）。改行は Enter、タブは Tab として打ちます。US 配列にキーのない文字（日本語、絵文字、アクセント付きの文字）は打たれず、`skipped` に返ります。API は日本語を変換しません。日本語を入れるには、端末のキーボードを「日本語 - ローマ字」に切り替え（たとえば `keys/press` で `ctrl` + `Space`）、ローマ字を打って、キーで確定します。
- `keys/press` と `keys/type` には、公開している HID のレポートマップにキーボードが入っている必要があります（なければ `409` `no_keyboard`）。`home` にはホームキーが必要です（なければ `409` `no_home_button`）。キーの名前は端末のものです。`cmd` は端末の Command キーで、PC の Ctrl ではありません。
- リクエストの本文は 64 KB までです。知らないメンバーや、足りない必須のメンバーは `400` `invalid_request` です。

## WebSocket のストリーム

`GET /v1/stream` は、リアルタイムの低レベルの入力用に WebSocket へ切り替えます。1 本か 2 本の指を置き、動かし、離し、キーを押して放します。ここは要約です。プロトコルの全体は、[api/openapi.yaml](api/openapi.yaml) の `GET /v1/stream` の説明にあります。

- **認証。** ブラウザーは WebSocket に `Authorization` を付けられないので、**最初のメッセージ**を接続の **2 秒以内**に `{ "op": "auth", "token": "<トークン>" }` として送ります。間違い、または遅れたときは、`error` のイベント（`unauthorized`）と終了コード 1008 を返します。切り替えの前に、`Origin` と `Host` の確認も行います。
- **セッション。** `auth` が通ると、サーバーは `{ "ev": "ready", "api": 1 }` を送ります。ストリームは動作中の操作と同じに扱われ、開いている間はほかの操作が `409` `busy` になり、タッチと同じ前提条件が要ります。同時に 4 本までつなげますが、セッションを持てるのは 1 本です。
- **メッセージ**は、テキストフレームの JSON で、4 KB までです。クライアントから（`seq` は自分で決める番号で、応答が同じ番号を返します）: `down` と `move`（`contacts` は 1 本か 2 本の指 `{ "id": 0 か 1, "x", "y" }`。`down` は `space` と `ref` も取ります）、`up`、`key`（`down: true` か `false`）、`ping`。サーバーから: `ack`、`error`（そのメッセージは何もしていません。ストリームは開いたままです）、`interrupted`、`pong`。
- **デッドマン。** 指かキーが押されている間に **1 秒**メッセージが届かないと、すべての指を離し、すべてのキーを放して、`{ "ev": "interrupted", "reason": "idle" }` を送ります。ストリームは開いたままです。指を止めたまま保つには、`ping` を定期的に送ります。`interrupted` の理由が `user`、`cancelled`、`session_ended` のときは、ストリームが終わります。
- **終了コード。** 1000 通常の終了（クライアント、ユーザー、取り消し、セッションの終了）。1001 アプリの終了中。1003 バイナリフレーム。1008 認証の失敗か時間切れ、トークンの作り直し、ライセンスが API を対象としない、1 秒に 1000 を超えるメッセージ（`rate_limited`）。1009 4 KB を超えるメッセージ。1011 内部エラー。1013 あとでやり直す（セッションを開けない: `busy`、`user_active`、`not_connected`、`not_running`）。

```
-> { "op": "auth", "token": "..." }
<- { "ev": "ready", "api": 1 }
-> { "op": "down", "seq": 1, "space": "screen", "contacts": [{ "id": 0, "x": 0.2, "y": 0.5 }] }
<- { "ev": "ack", "seq": 1 }
-> { "op": "move", "seq": 2, "contacts": [{ "id": 0, "x": 0.8, "y": 0.5 }] }
<- { "ev": "ack", "seq": 2 }
-> { "op": "up", "seq": 3 }
<- { "ev": "ack", "seq": 3 }
```

## エラー

エラーは `application/problem+json`（RFC 9457）で、機械が読むメンバー `code` を持ちます。`detail` は人向けの 1 文で、あなたのデータ（位置、キー、文字、トークン）は入りません。

| HTTP ステータス | `code` | 意味 |
|---|---|---|
| 400 | `invalid_request` | 本文、範囲、上限、名前のどれかが正しくありません。 |
| 401 | `unauthorized` | トークンがない、または違います。 |
| 403 | `forbidden_origin` | `Origin` ヘッダーがある、または `Host` が許可されていません。 |
| 403 | `purchase_required` | ライセンスが API を対象としていません（試用中、期限切れ）。 |
| 404 | `not_found` | そのエンドポイントはありません。 |
| 405 | `method_not_allowed` | そのエンドポイントはその HTTP メソッドを受け付けません（`Allow` に使えるものが並びます）。 |
| 409 | `not_running` | ミラーリングのセッションが動いていません。 |
| 409 | `not_connected` | 送り先の端末が HID のレポートを購読していません。 |
| 409 | `no_picture` | 映像がなく（受信ソフトのウィンドウを閉じたか最小化した、または読めなかった）、リクエストには映像が要ります。 |
| 409 | `picture_changed` | `pixel` のリクエストが、逆の向きのスクリーンショットを指しています。端末がそのあとで回転しました。 |
| 409 | `busy` | ほかの操作、WebSocket のストリーム、計測のページのどれかが動いています。 |
| 409 | `user_interrupted` | あなたがミラー画面を操作したので、すべてを離しました。 |
| 409 | `user_active` | あなたが直近の 1 秒以内にミラー画面を操作した、またはマウスかキーを押しています。 |
| 409 | `no_keyboard` | 公開している HID のレポートマップにキーボードがありません。 |
| 409 | `no_home_button` | 公開している HID のレポートマップにホームキーがありません。 |
| 429 | `rate_limited` | 1 秒に 10 枚を超えるスクリーンショット、または同時に進行中のスクリーンショットが多すぎる（どちらも `Retry-After: 1`）、あるいは WebSocket のストリームで 1 秒あたりのメッセージが多すぎます（閉じられます）。 |
| 499 | `cancelled` | `POST /v1/cancel`（またはクライアントがいなくなったこと）で操作が終わりました。 |
| 500 | `internal_error` | アプリの内部の予期しない失敗です（たとえば Bluetooth の書き込みの失敗）。 |

確認は、`Origin`、`Host`、トークン、ルート、リクエストの本文の順に行います。

## Python クライアントと MCP サーバー

`clients/python` には、Python のパッケージ `icescreentouch` があります。クライアント（標準ライブラリだけで動きます）と、AI エージェント用の MCP サーバー `icescreentouch-mcp` です。Python 3.10 以降と Windows が要ります。PyPI には公開していないので、このフォルダーから入れます。詳しくは、そちらの README [../clients/python/README.md](../clients/python/README.md) にあります。

```powershell
cd clients\python

# uv を使う場合（おすすめ）
uv sync                      # クライアントだけ
uv sync --extra stream       # + WebSocket のセッション
uv sync --extra image        # + Screenshot.to_pil()
uv sync --all-extras         # + MCP サーバーとテスト用のツール

# pip を使う場合
pip install .                # ほかに .[stream]  .[image]  .[mcp]
```

`Client()` は、`ICESCREENTOUCH_API` と `ICESCREENTOUCH_TOKEN`、次に `api.json`（`ICESCREENTOUCH_DATA_DIR` を明示的に設定していればその場所が最初で、次に Microsoft Store の場所、最後に既定のアプリのデータフォルダー）から API を見つけます。接続先は必ず `127.0.0.1` で、`Origin` ヘッダーは送りません。`Client(base_url=..., token=...)` で、見つける手順を上書きできます。**接続先の確認:** Windows では、リクエストのたびに、そのポートで待ち受けているのが同じ Windows ユーザーで動く `IceScreenTouch.exe` であり、整合性レベルが Medium 未満でスクリプトより低くないこと（`api.json` の場合は、ファイルの `pid` そのものであること）を確かめます。そうでない場合や、確かめられない場合は、トークンを**送らず**、`StaleConnectionError` を起こします。`Client(..., verify_owner=False)` でこの確認を切れます。テスト用のサーバーなど、自分で用意した環境でだけ使ってください。エラーは `IceScreenTouchError` の派生クラスで、`code` ごとに 1 つあります。`busy` と `user_active` は再試行しません。あなたの操作が優先なので、どうするかはスクリプトが決めます。`phone.stream()` は WebSocket のセッションを開き（`stream` の追加パッケージが要ります）、指かキーを押している間は ping で保ちます。

### MCP サーバー

`icescreentouch-mcp` は、標準入出力で動く MCP サーバーです。お使いの MCP クライアントに登録します。パスは、チェックアウトした場所に置き換えてください。

Claude Code:

```powershell
claude mcp add icescreentouch -- uv --directory C:\path\to\IceScreenTouch\clients\python run --no-dev --extra mcp icescreentouch-mcp
```

Claude Desktop（`claude_desktop_config.json`）:

```json
{
  "mcpServers": {
    "icescreentouch": {
      "command": "uv",
      "args": ["--directory", "C:\\path\\to\\IceScreenTouch\\clients\\python", "run", "--no-dev", "--extra", "mcp", "icescreentouch-mcp"]
    }
  }
}
```

10 個のツール:

| ツール | 用途 |
|---|---|
| `get_status` | アプリ、端末の接続、映像の状態 |
| `screenshot` | 端末の画面を画像で返し、ピクセル座標の基準にします |
| `tap` | 1 回、または 2 から 10 回のタップ |
| `long_press` | 長押し |
| `swipe` | 1 本指で、ある点から別の点へドラッグ |
| `pinch` | 2 本指のピンチ |
| `type_text` | 文字入力（US 配列。キーのない文字は打たれなかったものとして返ります） |
| `press_key` | 修飾キーつきでキーを 1 つ押す |
| `home` | ホームキーを押す |
| `set_rotation` | 端末の回転をアプリに伝える |

タッチのツールは、既定では**直近の `screenshot` の結果のピクセル**を位置にします。その画像の大きさを `ref` として送ります。スクリーンショットがまだなければ基準がないので、ツールは断り、撮るように求めます。`space="screen"`（0..1）なら、スクリーンショットなしで使えます。

ツールは本物の端末を操作し、パスコードの画面や購入の承認にも届きます。MCP クライアントのツール呼び出しの承認は、オンのままにしてください。ツールが、あなたが割り込んだ、または操作中だと知らせたときは、エージェントは繰り返さず、画面を見直すようにしてください。
