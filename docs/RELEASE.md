# GitHub Releases 配布手順（KikuFM / win-x64 / ffmpegは同梱しない）

このプロジェクトは **展開して `.bat` を実行するだけ** で起動できる ZIP を GitHub Releases に置く運用を想定します。

## 前提

- 配布ターゲット: **Windows win-x64 固定**
- `ffmpeg.exe` は **同梱しない**（利用者側で PATH に通っている必要あり）
- `softfm.exe` は ZIP に同梱する（`native/softfm.exe`）

## ローカルで配布ZIPを作る（推奨・最短）

1. ネイティブバイナリを用意
   - `build-ucrt64/softfm.exe` が存在することを確認

2. PowerShellで配布ZIPを生成
   - ルートで実行:
     - `powershell -ExecutionPolicy Bypass -File .\scripts\Make-Release.ps1`

3. 生成物
  - `dist/KikuFM-win-x64.zip`

4. 動作確認
   - できれば別PC/別ユーザー環境で
  - ZIP展開 → `Start-KikuFM.bat` 実行

## GitHub Releases へアップロード

1. GitHubでタグを切る（例: `v1.0.0`）
2. Releases を作成
3. `dist/KikuFM-win-x64.zip` を添付

## 利用者向け（README.txt にも同梱）

- `ffmpeg.exe` を PATH に通す
  - 例: `winget install Gyan.FFmpeg`
- `Start-KikuFM.bat` を実行

## 補足

- サーバは `native/softfm.exe` を優先的に探します。
  - 明示的に指定したい場合は `NGSOFTFM_SOFTFM_PATH` 環境変数を使えます。
- プリセットは既定で `%LOCALAPPDATA%\NgSoftFmWeb\presets.json` に保存されます。
