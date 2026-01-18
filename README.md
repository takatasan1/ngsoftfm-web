KikuFM — SDRチューナーをWebサーバー化してFMラジオを配信
=============================================

PCに挿したSDR（RTL-SDR等）を **「FMラジオ受信 + Web配信サーバー」** として動かし、
スマホ/別PCからブラウザで **同じFM放送を再生** できるようにするプロジェクトです。

- 受信機は“サーバー側PC”に置き、再生はLAN/Tailscale越しの端末でOK
- Web UIから周波数/プリセット/配信方式（HLS/Direct）を操作
- スキャンで見つけた局をプリセット化して運用

まずはリリース版の使い方
------------------------

「ビルドせずに使う」場合は GitHub Releases からZIPを入手して、展開して `.bat` を実行するだけです。

1. GitHub Releases から `KikuFM-win-x64.zip` をダウンロードして展開
2. `ffmpeg.exe` を PATH に通す（例: `winget install Gyan.FFmpeg`）
3. 展開したフォルダの `Start-KikuFM.bat` を実行
4. ブラウザで `http://127.0.0.1:5055/` を開く

補足:

- サーバは `0.0.0.0:5055` にバインドするため、同一LAN/Tailscaleの別端末からもアクセス可能です
- プリセットは既定で `%LOCALAPPDATA%\NgSoftFmWeb\presets.json` に保存されます
- `ffmpeg.exe` が無いと配信を開始できません（同梱しない方針）

全体の使い方（運用イメージ）
----------------------------

1. サーバーPCでSDRチューナーを接続（RTL-SDR等）
2. Web UIで周波数を入力して「受信」
3. 安定再生したい場合は配信をHLSに（推奨）
4. 「局スキャン」で発見 → プリセット登録
5. スマホ/別PCから `http://<サーバーIP>:5055/` を開いて再生

ソースからビルド/開発したい場合は、以降の「クイックスタート（Windows）」以降を参照してください。

KikuFM Web（Windowsネイティブ運用）
-------------------------------

概要
----

このリポジトリは、FM放送受信ソフト NGSoftFM（C++ / upstream）をベースに、Windowsでの運用をしやすくするための Web UI（.NET）と補助スクリプトを追加した派生版です。

主な用途:

- RTL-SDR等でFM放送を受信
- Web UIから受信開始/停止、配信方式（HLS/Direct）の切替
- 局スキャンで見つけた周波数をプリセット登録して運用

機能
----

- Web UI
  - 周波数指定（MHz）・プリセット選択
  - 配信
    - HLS（推奨）: ブラウザで安定再生（hls.js使用）
    - Direct: 端末/ブラウザによっては軽量
  - 局スキャン
    - しきい値「強電界/中電界/弱電界」選択
    - 検出局の個別登録/一括登録
  - サーバ再起動ボタン
    - 監視起動（後述）している場合に「終了→自動起動」まで行えます

クイックスタート（Windows）
---------------------------

前提:

- .NET SDK（`dotnet` が使えること）
- `ffmpeg.exe`（PATHに追加）
- RTL-SDRを使う場合: Zadig等で WinUSB ドライバ（AirSPY SDR#で受信できる環境なら問題ありません）

手順:

- hls.js をローカルに用意（任意だが推奨）

- `scripts/Install-HlsJs.ps1` が `web/NgSoftFmWeb/wwwroot/vendor/hls.js` に保存します
- 未配置でもUIはCDNにフォールバックしますが、ネットワーク制限環境ではローカル推奨です

- Web UI を起動

- `scripts/Start-KikuFM.bat` を実行
- ブラウザで `http://127.0.0.1:5055/` を開きます

補足:

- サーバは `0.0.0.0:5055` にバインドするので、LAN/Tailscale等からもアクセス可能です
- 起動は内部で `scripts/Start-Web.ps1` を使います（exit code 42 を検知して自動再起動）

- 受信

- Web UIで周波数を指定して「受信」
- 「局スキャン」で検出→プリセット登録しておくと運用が楽になります

ネイティブビルド（softfm.exe）
------------------------------

Windowsで `softfm.exe` を使うには、MSYS2（UCRT64）でビルドする想定です（環境により差分あり）。

例（MSYS2 UCRT64）:

- `pacman -Syu`
- `pacman -S --needed mingw-w64-ucrt-x86_64-toolchain mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-pkgconf mingw-w64-ucrt-x86_64-libusb mingw-w64-ucrt-x86_64-librtlsdr`
- `cmake -S . -B build-ucrt64 -G "MinGW Makefiles"`
- `cmake --build build-ucrt64 -j`

スクリプト
--------

- `scripts/Start-KikuFM.bat`: Web UI起動（推奨）
- `scripts/Start-NgSoftFmWeb.bat`: 互換用（既存ドキュメント向け）
- `scripts/Start-Web.ps1`: Webサーバ監視起動（exit code 42 で自動再起動）
- `scripts/Start-FMStream-Native.ps1`: `softfm.exe | ffmpeg` でHTTP配信（Web UIとは別系統の簡易配信）

セキュリティ
----------

`/api/server/restart` はプロセスを終了させます。LAN/Tailscale等で公開する場合は、管理トークンの設定を推奨します。

- 環境変数 `NGSOFTFM_ADMIN_TOKEN` を設定
- Web UI「サーバ管理」のトークン欄に同じ値を入力

クレジット
--------

- NGSoftFM（upstream）: [f4exb/ngsoftfm](https://github.com/f4exb/ngsoftfm)
- SoftFM（元になったプロジェクト）: [jorisvr/SoftFM](https://github.com/jorisvr/SoftFM)

ライセンス
--------

このリポジトリは GPL-2.0-or-later です。詳細は LICENSE を参照してください。

第三者ソフトの注意書きは THIRD_PARTY_NOTICES.md を参照してください。
