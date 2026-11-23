# AutoBright for Camera input

Webカメラの映像から周囲の環境照度を推定し、内蔵ディスプレイおよび外部モニターの画面輝度を自動調整する Windows 向け常駐ユーティリティです。本ソフトウェアは AI（Gemini）を使用して作成しています。  
A lightweight Windows tray utility that estimates ambient light from your webcam feed and automatically adjusts the brightness of built-in and external displays. This software was created using AI (Gemini).

[日本語](#日本語) | [English](#english)

---

<a id="日本語"></a>
## 日本語

### 概要
環境光センサーを搭載していないノートPCやデスクトップPCでも、既存のWebカメラを利用して画面の明るさを自動調整できます。  
カメラドライバごとの個体差が大きいシャッタースピードや ISO 感度の API に依存せず、**映像フレームの画素統計（空間重み付きヒストグラム・リニア光量変換・暗所ノイズ解析・高照度飽和解析）のみ**で照度を推定するため、幅広い PC・Webカメラで安定して動作します。

### 主な機能
- **汎用カメラ対応の環境照度推定**
  - 人物（顔や衣服）が映り込みやすい画面中央下部の重みを下げ、周囲の環境光を優先的に評価します。
  - sRGB 逆ガンマ変換によるリニア光量復元、暗所ノイズ減衰、高照度（窓際・太陽光など）のダイナミックレンジ伸長に対応。
- **内蔵ディスプレイ・外部モニター両対応**
  - 内蔵ディスプレイ（WMI）および外部モニター（DDC/CI）を個別に有効／無効設定でき、モニターごとの輝度オフセット（%）補正が可能です。
- **インタラクティブな輝度カーブ設定 & 履歴グラフ**
  - ドラッグで調整、ダブルクリックで点追加、右クリックで点削除ができる輝度カーブエディタをカメラごとに保存。
  - 過去5分間の照度スコアと画面輝度の推移グラフをリアルタイム表示。
- **手動輝度変更時の自動一時停止 & トレイ状態表示**
  - キーボードや Windows のスライダーから手動で輝度を変更すると自動検知して一時停止し、カメラを解放します。
  - 一時停止中はシステムトレイアイコンの右下に**赤い停止マーク**が表示され、トレイメニューや設定画面からワンクリックで再開できます。
- **低負荷・省電力設計（Windows 11 効率モード対応）**
  - トレイ常駐時は Windows 11 の **EcoQoS（効率モード）** を有効化し、LPEコア / Eコアを優先利用してバックグラウンド負荷を抑えます。
  - `ArrayPool` によるゼロアロケーション画像解析と、ウィンドウ非表示時の UI 更新停止によりメモリ消費を最小化しています。
- **柔軟なカメラ設定**
  - 計測モード切替（間欠計測モード／連続撮影モード）
  - 解像度選択（初期値は最大画角アスペクト比の中で最小解像度を自動選択）
  - フリッカー対策周波数設定（`Auto` ※非対応時は `50Hz` へ自動フォールバック / `50Hz` / `60Hz`）
  - 取得照度・画面輝度それぞれの鈍化フィルター（1〜10段階）
  - 画面オフ時の自動停止・カメラ解放
- **その他**
  - 8言語対応（日本語 / English / 中文 / हिन्दी / Español / Français / العربية / Русский）
  - Windows スタートアップ登録（タスクトレイ常駐起動）
  - ポータブル構成（設定ファイル `config.json` は実行ファイルと同じフォルダに保存）
  - デバッグモード（有効時のみ、実行ファイル直下の `log` フォルダへ `.log` と測定フレーム画像を保存）

### 動作環境
- Windows 10 / Windows 11（64-bit）
- PC 内蔵カメラまたは USB Webカメラ
- 外部モニターの輝度制御を行う場合は、モニター本体メニュー（OSD）で **DDC/CI** を有効にしてください。

### ダウンロード・使い方
1. [Releases](https://github.com/kodenma/AutoBright-for-Camera-input/releases) ページから最新版の `AutoBrightnessApp.exe`（または ZIP ファイル）をダウンロードし、任意のフォルダに配置します。
2. `AutoBrightnessApp.exe` を起動します。
   - ※ 個人開発の未署名バイナリのため、初回起動時に Windows SmartScreen の警告が表示される場合があります。内容を確認のうえ「詳細情報」→「実行」で起動してください。
3. 初回起動時の概要・注意事項に同意すると、タスクトレイに常駐して計測を開始します。
4. タスクトレイのアイコンを**左クリック**（または右クリック →「設定画面を開く」）して、使用カメラ・解像度・輝度カーブ・対象モニターを調整し、**「設定を保存して適用」**を押してください。

### ソースからのビルド
.NET 10 SDK（Windows Desktop ワークロード）が必要です。

```powershell
git clone [https://github.com/kodenma/AutoBright-for-Camera-input.git](https://github.com/kodenma/AutoBright-for-Camera-input.git)
cd AutoBright-for-Camera-input
dotnet publish -c Release -r win-x64 -o .\Publish
```

### 注意事項・免責事項
- **他のカメラアプリ・Windows Hello への影響**  
  本アプリは環境照度測定のためカメラデバイスにアクセスします。特に「連続撮影モード」有効時や計測タイミングによっては、Zoom / Microsoft Teams などの他カメラアプリや Windows Hello（顔認証）の動作に影響を与える場合があります。
- **デバッグモードの取り扱い**  
  デバッグモードを有効にすると、測定に使用したカメラ画像が実行ファイルと同じ場所の `log` フォルダに保存されます。ログやフォルダを共有する際は撮影画像の取り扱いにご注意ください。
- **免責事項**  
  本ソフトウェアは現状のまま（AS IS）提供されます。本アプリの使用または使用不能によって生じた PC の誤動作、画面表示や設定の不具合、データの消失・破損その他いかなる損害についても、開発者は一切の責任を負いません。

---

<a id="english"></a>
## English

### Overview
**AutoBright for Camera input** brings automatic ambient brightness control to Windows laptops and desktops without dedicated light sensors by using a standard webcam.  
Rather than relying on camera shutter speed or ISO APIs—which are frequently unsupported or return fixed values across UVC webcams—it estimates ambient lighting purely from frame pixel statistics (spatial-weighted histogram, sRGB inverse-gamma linear luminance, dark-scene noise damping, and high-light expansion) for consistent behavior across PCs.

### Key Features
- **Hardware-Agnostic Image-Based Illuminance Estimation**
  - Reduces weight on the lower-center area (where a person's face/clothing usually appears) and prioritizes surrounding ambient lighting.
  - Uses sRGB inverse-gamma linearization, low-light noise damping, and highlight saturation expansion for bright sunlight environments.
- **Built-in & External Monitor Support**
  - Controls laptop internal displays via **WMI** and external monitors via **DDC/CI**, with per-display enable/disable toggles and brightness offset (%) tuning.
- **Interactive Brightness Curve & History Chart**
  - Drag points to adjust, double-click to add, right-click to remove (saved per camera profile).
  - Real-time 5-minute history graph showing filtered illuminance score and target display brightness.
- **Auto-Pause on Manual Brightness Adjustment**
  - Automatically detects manual brightness changes (via WMI event watcher and DDC/CI polling), pauses auto-adjustment, and releases the camera.
  - Displays a small **red stop badge** on the bottom-right corner of the system tray icon while paused.
- **Low-Overhead Background Operation (Windows 11 EcoQoS)**
  - Enables Windows 11 **Efficiency Mode (EcoQoS)** when minimized to the system tray to prioritize LPE/E-cores.
  - Zero-allocation frame buffer reuse (`ArrayPool<byte>`) and zero UI-thread wakeups while hidden in the tray.
- **Flexible Camera & Filter Controls**
  - Interval capture mode or Continuous capture mode.
  - Resolution selector (defaults to the lowest resolution within the widest sensor aspect ratio).
  - Anti-flicker powerline frequency (`Auto` with automatic `50Hz` fallback / `50Hz` / `60Hz`).
  - Independent smoothing filters (1–10) for input illuminance and output brightness fade.
  - Automatically pauses and releases the camera when the display powers off.
- **Additional Utilities**
  - 8-language UI (English, Japanese, Chinese, Hindi, Spanish, French, Arabic with RTL support, Russian).
  - Optional Windows startup registration (launches silently in the system tray).
  - Portable configuration (`config.json` stored alongside the executable).
  - Optional Debug Mode saving `.log` files and captured frames to a local `log` folder.

### Requirements
- Windows 10 / Windows 11 (64-bit)
- Built-in webcam or USB webcam
- **DDC/CI** enabled in your external monitor's OSD menu (for external display control)

### Installation & Usage
1. Download the latest release from [Releases](https://github.com/kodenma/AutoBright-for-Camera-input/releases) and extract `AutoBrightnessApp.exe` to any folder.
2. Launch `AutoBrightnessApp.exe`.
   - *Note: Because the binary is unsigned, Windows SmartScreen may show a warning on first launch. Click **More info** → **Run anyway** if you choose to proceed.*
3. Accept the initial overview and disclaimer to start monitoring in the system tray.
4. Left-click the tray icon (or right-click → **Open Settings**) to configure your camera, resolution, brightness curve, and displays, then click **Save & Apply**.

### Build from Source
Requires the **.NET 10 SDK** (Windows Desktop workload).

```powershell
git clone [https://github.com/kodenma/AutoBright-for-Camera-input.git](https://github.com/kodenma/AutoBright-for-Camera-input.git)
cd AutoBright-for-Camera-input
dotnet publish -c Release -r win-x64 -o .\Publish
```

### Important Notices & Disclaimer
- **Camera Apps & Windows Hello**: Accessing the webcam (especially in Continuous Capture Mode) may interfere with other camera apps (Zoom, Microsoft Teams, etc.) or Windows Hello facial recognition.
- **Debug Mode Privacy**: When Debug Mode is enabled, captured webcam frames are saved to the `log` folder next to `AutoBrightnessApp.exe`. Be careful not to share unintended images.
- **Disclaimer**: This software is provided "as is", without warranty of any kind. The author assumes no liability for any PC malfunction, unexpected display behavior, data loss, or any other damages arising from the use of this application.

---

## License
MIT License  
Author: [kodenma](https://github.com/kodenma)
