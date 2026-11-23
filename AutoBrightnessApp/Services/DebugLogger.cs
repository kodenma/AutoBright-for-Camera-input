using System.IO;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AutoBrightnessApp.Services;

public static class DebugLogger
{
    private static readonly object FileLock = new();
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "log");

    public static bool IsEnabled { get; set; }

    public static void Log(string category, string message)
    {
        if (!IsEnabled) return;

        try
        {
            Directory.CreateDirectory(LogDirectory);
            DateTime now = DateTime.Now;
            string logPath = Path.Combine(LogDirectory, $"autobright_{now:yyyyMMdd}.log");
            string line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}{Environment.NewLine}";

            lock (FileLock)
            {
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 書き込み権限がない場所での実行時もアプリ本体は止めない
        }
    }

    /// <summary>
    /// デバッグモード有効時のみ、測定に使用したフレーム画像を log フォルダへ PNG 保存する
    /// </summary>
    public static async Task<string?> SaveCapturedImageAsync(SoftwareBitmap bitmap, DateTime timestamp)
    {
        if (!IsEnabled) return null;

        SoftwareBitmap? bgraBitmap = null;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string fileName = $"capture_{timestamp:yyyyMMdd_HHmmss_fff}.png";
            string fullPath = Path.Combine(LogDirectory, fileName);

            SoftwareBitmap target = bitmap;
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                bitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied)
            {
                bgraBitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                target = bgraBitmap;
            }

            using var memStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memStream);
            encoder.SetSoftwareBitmap(target);
            await encoder.FlushAsync();

            memStream.Seek(0);
            using var netStream = memStream.AsStreamForRead();
            using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await netStream.CopyToAsync(fileStream);

            return fileName;
        }
        catch (Exception ex)
        {
            Log("IMAGE_SAVE_ERROR", $"測定画像の保存に失敗しました: {ex.Message}");
            return null;
        }
        finally
        {
            bgraBitmap?.Dispose();
        }
    }
}