using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;

namespace AutoBrightnessApp.Services.Camera;

public record CameraResolutionOption(int Width, int Height)
{
    public int PixelCount => Width * Height;
    public double AspectRatio => Height > 0 ? (double)Width / Height : 0.0;

    public string AspectRatioLabel
    {
        get
        {
            if (Width <= 0 || Height <= 0) return "";
            int gcd = Gcd(Width, Height);
            int rw = Width / gcd;
            int rh = Height / gcd;
            if (rw == 8 && rh == 5) { rw = 16; rh = 10; }
            return $"{rw}:{rh}";
        }
    }

    public override string ToString() => $"{Width} x {Height} ({AspectRatioLabel})";

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            int t = b;
            b = a % b;
            a = t;
        }
        return a;
    }
}

public record CameraDeviceItem(
    string Id,
    string GroupId,
    string SourceId,
    string DisplayName,
    IReadOnlyList<CameraResolutionOption> SupportedResolutions,
    CameraResolutionOption? DefaultResolution
)
{
    public override string ToString() => DisplayName;
}

public record FrameVisualMetrics(
    double MedianY,
    double P25Y,
    double P75Y,
    double P90Y,
    double LinearBaseLuma,
    double DarkRatio,
    double BrightRatio,
    double ClippedRatio,
    double PureNoise,
    double NormalizedNoise,
    double HistogramGapRatio,
    double DarkDampingFactor,
    double HighLightMultiplier,
    double NormalizedScore
);

public record IlluminanceMeasurement(
    double NormalizedScore,
    double RawMedianY,
    double LinearBaseLuma,
    double DarkDampingFactor,
    double HighLightMultiplier,
    double NoiseLevel,
    double NormalizedNoise,
    double DarkRatio,
    double BrightRatio,
    double ClippedRatio,
    string AppliedPowerlineFrequency,
    int Width,
    int Height,
    string CameraName,
    string? SavedImageFileName
);

public sealed class ContinuousCameraSession : IAsyncDisposable
{
    private readonly CameraDeviceItem _camera;
    private readonly CameraResolutionOption? _targetResolution;
    private readonly string _powerlineMode;
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private readonly object _lock = new();
    private SoftwareBitmap? _latestBitmapClone;
    private IlluminanceMeasurement? _latestMeasurement;
    private DateTime _lastFrameTime = DateTime.MinValue;
    private string _appliedPowerlineStatus = "Unsupported";
    private int _activeWidth;
    private int _activeHeight;
    private int _warmupFrames;
    private volatile bool _isRunning;
    private int _isProcessingFrame;

    public string CameraId => _camera.Id;
    public CameraResolutionOption? TargetResolution => _targetResolution;
    public string PowerlineMode => _powerlineMode;
    public bool IsRunning => _isRunning;

    public ContinuousCameraSession(CameraDeviceItem camera, CameraResolutionOption? targetResolution, string powerlineMode)
    {
        _camera = camera;
        _targetResolution = targetResolution ?? camera.DefaultResolution;
        _powerlineMode = powerlineMode;
    }

    public async Task<bool> StartAsync()
    {
        await StopAsync();

        try
        {
            var group = await MediaFrameSourceGroup.FromIdAsync(_camera.GroupId);
            if (group == null) return false;

            _capture = await CameraIlluminanceService.InitializeMediaCaptureWithFallbackAsync(group);
            if (_capture == null) return false;

            _capture.Failed += OnCaptureFailed;
            _appliedPowerlineStatus = CameraIlluminanceService.ApplyPowerlineFrequency(_capture, _powerlineMode);

            var frameSource = CameraIlluminanceService.ResolveColorFrameSource(_capture, _camera.SourceId);
            if (frameSource == null)
            {
                await StopAsync();
                return false;
            }

            var appliedFormat = await CameraIlluminanceService.SelectTargetFormatAsync(frameSource, _targetResolution);
            _activeWidth = (int)(appliedFormat?.VideoFormat.Width ?? frameSource.CurrentFormat?.VideoFormat.Width ?? 0);
            _activeHeight = (int)(appliedFormat?.VideoFormat.Height ?? frameSource.CurrentFormat?.VideoFormat.Height ?? 0);

            _warmupFrames = 0;
            _reader = await _capture.CreateFrameReaderAsync(frameSource);
            _reader.FrameArrived += OnFrameArrived;

            if (await _reader.StartAsync() != MediaFrameReaderStartStatus.Success)
            {
                await StopAsync();
                return false;
            }

            _isRunning = true;
            _lastFrameTime = DateTime.UtcNow;
            DebugLogger.Log("CAMERA_STREAM", $"連続撮影ストリーム開始: {_camera.DisplayName} ({_activeWidth}x{_activeHeight}), フリッカー対策={_appliedPowerlineStatus}");
            return true;
        }
        catch (Exception ex)
        {
            DebugLogger.Log("CAMERA_STREAM_ERROR", $"連続撮影ストリーム開始失敗: {ex.Message}");
            await StopAsync();
            return false;
        }
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
    {
        _isRunning = false;
        DebugLogger.Log("CAMERA_STREAM_FAILED", $"連続撮影ストリーム切断検知: {errorEventArgs.Message}");
    }

    private async void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!_isRunning) return;

        // 計測ループ周期(500ms)に歩調を合わせて450ms間隔で間引き、無駄なCPU解析を削減
        lock (_lock)
        {
            if (_latestMeasurement != null && (DateTime.UtcNow - _lastFrameTime).TotalMilliseconds < 450) return;
        }

        if (Interlocked.CompareExchange(ref _isProcessingFrame, 1, 0) != 0) return;

        try
        {
            PowerEfficiencyService.ApplyBackgroundThreadEfficiency();

            using var frameRef = sender.TryAcquireLatestFrame();
            var videoFrame = frameRef?.VideoMediaFrame;
            if (videoFrame == null) return;

            // ストリーム開始直後のAE未収束フレーム（最初の2フレーム）は破棄する
            _warmupFrames++;
            if (_warmupFrames < 3)
            {
                lock (_lock) { _lastFrameTime = DateTime.UtcNow; }
                return;
            }

            var bitmap = await CameraIlluminanceService.GetCpuSoftwareBitmapAsync(videoFrame);
            if (bitmap == null) return;

            try
            {
                int w = bitmap.PixelWidth;
                int h = bitmap.PixelHeight;
                var metrics = CameraIlluminanceService.AnalyzeFrameMetrics(bitmap);

                SoftwareBitmap? clonedForDebug = null;
                if (DebugLogger.IsEnabled)
                {
                    try
                    {
                        clonedForDebug = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                    }
                    catch { }
                }

                lock (_lock)
                {
                    _lastFrameTime = DateTime.UtcNow;
                    _latestBitmapClone?.Dispose();
                    _latestBitmapClone = clonedForDebug;
                    _latestMeasurement = new IlluminanceMeasurement(
                        NormalizedScore: metrics.NormalizedScore,
                        RawMedianY: metrics.MedianY,
                        LinearBaseLuma: metrics.LinearBaseLuma,
                        DarkDampingFactor: metrics.DarkDampingFactor,
                        HighLightMultiplier: metrics.HighLightMultiplier,
                        NoiseLevel: metrics.PureNoise,
                        NormalizedNoise: metrics.NormalizedNoise,
                        DarkRatio: metrics.DarkRatio,
                        BrightRatio: metrics.BrightRatio,
                        ClippedRatio: metrics.ClippedRatio,
                        AppliedPowerlineFrequency: _appliedPowerlineStatus,
                        Width: w,
                        Height: h,
                        CameraName: _camera.DisplayName,
                        SavedImageFileName: null
                    );
                }
            }
            finally
            {
                if (!ReferenceEquals(bitmap, videoFrame.SoftwareBitmap))
                {
                    bitmap.Dispose();
                }
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _isProcessingFrame, 0);
        }
    }

    public async Task<IlluminanceMeasurement?> GetLatestMeasurementAsync()
    {
        if (!_isRunning) return null;

        IlluminanceMeasurement? current;
        SoftwareBitmap? bitmapToSave = null;

        lock (_lock)
        {
            if (_latestMeasurement == null) return null;
            if ((DateTime.UtcNow - _lastFrameTime).TotalSeconds > 3.5)
            {
                _isRunning = false;
                return null;
            }
            current = _latestMeasurement;
            if (DebugLogger.IsEnabled && _latestBitmapClone != null)
            {
                bitmapToSave = _latestBitmapClone;
                _latestBitmapClone = null;
            }
        }

        if (bitmapToSave != null)
        {
            try
            {
                string? savedName = await DebugLogger.SaveCapturedImageAsync(bitmapToSave, DateTime.Now);
                if (savedName != null)
                {
                    current = current with { SavedImageFileName = savedName };
                }
            }
            finally
            {
                bitmapToSave.Dispose();
            }
        }

        return current;
    }

    public async Task StopAsync()
    {
        _isRunning = false;

        if (_reader != null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            try { await _reader.StopAsync(); } catch { }
            _reader.Dispose();
            _reader = null;
        }

        if (_capture != null)
        {
            _capture.Failed -= OnCaptureFailed;
            _capture.Dispose();
            _capture = null;
        }

        lock (_lock)
        {
            _latestBitmapClone?.Dispose();
            _latestBitmapClone = null;
            _latestMeasurement = null;
            _lastFrameTime = DateTime.MinValue;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed class CameraIlluminanceService
{
    // sRGB -> リニア光量変換テーブル (gamma = 2.2, 0..255 スケール)
    private static readonly double[] LinearLumaLut = CreateLinearLumaLut();

    private static double[] CreateLinearLumaLut()
    {
        var lut = new double[256];
        for (int i = 0; i < 256; i++)
        {
            lut[i] = Math.Pow(i / 255.0, 2.2) * 255.0;
        }
        return lut;
    }

    public async Task<IReadOnlyList<CameraDeviceItem>> DiscoverColorCamerasAsync()
    {
        var result = new List<CameraDeviceItem>();
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();

            foreach (var group in groups)
            {
                if (group.Id.StartsWith(@"\\?\SWD#SGDEVAPI", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var info in group.SourceInfos)
                {
                    if (info.SourceKind != MediaFrameSourceKind.Color)
                        continue;

                    var resolutions = await QuerySupportedResolutionsAsync(group, info.Id);
                    var defaultRes = SelectWidestAspectLowestResolution(resolutions);

                    result.Add(new CameraDeviceItem(
                        Id: info.Id,
                        GroupId: group.Id,
                        SourceId: info.Id,
                        DisplayName: group.DisplayName,
                        SupportedResolutions: resolutions,
                        DefaultResolution: defaultRes
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log("CAMERA_DISCOVER_ERROR", $"カメラ列挙中に例外発生: {ex.Message}");
        }
        return result;
    }

    private static async Task<IReadOnlyList<CameraResolutionOption>> QuerySupportedResolutionsAsync(
        MediaFrameSourceGroup group,
        string sourceId)
    {
        MediaCapture? cap = null;
        try
        {
            cap = await InitializeMediaCaptureWithFallbackAsync(group);
            if (cap == null) return [];

            var frameSource = ResolveColorFrameSource(cap, sourceId);
            if (frameSource == null) return [];

            return frameSource.SupportedFormats
                .Where(f => f.VideoFormat.Width > 0 && f.VideoFormat.Height > 0)
                .Select(f => new CameraResolutionOption((int)f.VideoFormat.Width, (int)f.VideoFormat.Height))
                .Distinct()
                .OrderBy(r => r.PixelCount)
                .ThenBy(r => r.Width)
                .ToList();
        }
        catch
        {
            return [];
        }
        finally
        {
            cap?.Dispose();
        }
    }

    public static CameraResolutionOption? SelectWidestAspectLowestResolution(
        IReadOnlyList<CameraResolutionOption> resolutions)
    {
        if (resolutions.Count == 0) return null;

        var candidates = resolutions.Where(r => r.Width >= 160 && r.Height >= 120).ToList();
        if (candidates.Count == 0) candidates = resolutions.ToList();

        var maxPixelFormat = candidates
            .OrderByDescending(r => r.PixelCount)
            .First();
        double widestAspect = maxPixelFormat.AspectRatio;

        var matchingAspectGroup = candidates
            .Where(r => Math.Abs(r.AspectRatio - widestAspect) <= 0.03)
            .OrderBy(r => r.PixelCount)
            .ThenBy(r => r.Width)
            .ToList();

        return matchingAspectGroup.FirstOrDefault() ?? candidates.OrderBy(r => r.PixelCount).First();
    }

    public async Task<IlluminanceMeasurement?> MeasureOnceAsync(
        CameraDeviceItem camera,
        CameraResolutionOption? targetResolution = null,
        string powerlineMode = "Auto",
        CancellationToken ct = default)
    {
        MediaCapture? capture = null;
        MediaFrameReader? reader = null;

        try
        {
            var group = await MediaFrameSourceGroup.FromIdAsync(camera.GroupId);
            if (group == null) return null;

            capture = await InitializeMediaCaptureWithFallbackAsync(group);
            if (capture == null) return null;

            string appliedPowerline = ApplyPowerlineFrequency(capture, powerlineMode);

            var frameSource = ResolveColorFrameSource(capture, camera.SourceId);
            if (frameSource == null) return null;

            var desiredRes = targetResolution ?? camera.DefaultResolution;
            var appliedFormat = await SelectTargetFormatAsync(frameSource, desiredRes);

            reader = await capture.CreateFrameReaderAsync(frameSource);
            var tcs = new TaskCompletionSource<(FrameVisualMetrics metrics, int w, int h, string? savedFile)?>();
            int frameCount = 0;
            int processing = 0;

            reader.FrameArrived += async (sender, _) =>
            {
                if (tcs.Task.IsCompleted) return;
                if (Interlocked.CompareExchange(ref processing, 1, 0) != 0) return;

                try
                {
                    PowerEfficiencyService.ApplyBackgroundThreadEfficiency();

                    using var frameRef = sender.TryAcquireLatestFrame();
                    var videoFrame = frameRef?.VideoMediaFrame;
                    if (videoFrame == null) return;

                    frameCount++;
                    if (frameCount < 3) return;

                    var bitmap = await GetCpuSoftwareBitmapAsync(videoFrame);
                    if (bitmap == null) return;

                    try
                    {
                        if (!tcs.Task.IsCompleted)
                        {
                            var metrics = AnalyzeFrameMetrics(bitmap);
                            int w = bitmap.PixelWidth;
                            int h = bitmap.PixelHeight;
                            string? savedFile = await DebugLogger.SaveCapturedImageAsync(bitmap, DateTime.Now);
                            tcs.TrySetResult((metrics, w, h, savedFile));
                        }
                    }
                    finally
                    {
                        if (!ReferenceEquals(bitmap, videoFrame.SoftwareBitmap))
                        {
                            bitmap.Dispose();
                        }
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref processing, 0);
                }
            };

            if (await reader.StartAsync() != MediaFrameReaderStartStatus.Success)
                return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(4500);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));
            await reader.StopAsync();

            if (completed != tcs.Task || tcs.Task.Result is not { } result)
                return null;

            int finalW = result.w > 0 ? result.w : (int)(appliedFormat?.VideoFormat.Width ?? 0);
            int finalH = result.h > 0 ? result.h : (int)(appliedFormat?.VideoFormat.Height ?? 0);

            return new IlluminanceMeasurement(
                NormalizedScore: result.metrics.NormalizedScore,
                RawMedianY: result.metrics.MedianY,
                LinearBaseLuma: result.metrics.LinearBaseLuma,
                DarkDampingFactor: result.metrics.DarkDampingFactor,
                HighLightMultiplier: result.metrics.HighLightMultiplier,
                NoiseLevel: result.metrics.PureNoise,
                NormalizedNoise: result.metrics.NormalizedNoise,
                DarkRatio: result.metrics.DarkRatio,
                BrightRatio: result.metrics.BrightRatio,
                ClippedRatio: result.metrics.ClippedRatio,
                AppliedPowerlineFrequency: appliedPowerline,
                Width: finalW,
                Height: finalH,
                CameraName: camera.DisplayName,
                SavedImageFileName: result.savedFile
            );
        }
        catch (Exception ex)
        {
            DebugLogger.Log("MEASURE_ONCE_ERROR", $"単発計測中に例外発生: {ex.Message}");
            return null;
        }
        finally
        {
            reader?.Dispose();
            capture?.Dispose();
        }
    }

    internal static string ApplyPowerlineFrequency(MediaCapture capture, string preferredMode)
    {
        try
        {
            var ctrl = capture.VideoDeviceController;
            string mode = (preferredMode ?? "Auto").Trim();

            if (mode.Equals("60Hz", StringComparison.OrdinalIgnoreCase))
            {
                if (ctrl.TrySetPowerlineFrequency(PowerlineFrequency.SixtyHertz))
                    return "60Hz";
            }
            else if (mode.Equals("50Hz", StringComparison.OrdinalIgnoreCase))
            {
                if (ctrl.TrySetPowerlineFrequency(PowerlineFrequency.FiftyHertz))
                    return "50Hz";
            }
            else
            {
                if (ctrl.TrySetPowerlineFrequency(PowerlineFrequency.Auto))
                {
                    if (ctrl.TryGetPowerlineFrequency(out var actual) && actual != PowerlineFrequency.Auto)
                    {
                        return FormatPowerlineEnum(actual);
                    }
                    return "Auto";
                }

                if (ctrl.TrySetPowerlineFrequency(PowerlineFrequency.FiftyHertz))
                {
                    return "50Hz (AutoFallback)";
                }
            }

            if (ctrl.TryGetPowerlineFrequency(out var current))
            {
                return FormatPowerlineEnum(current);
            }
        }
        catch { }

        return "Unsupported";
    }

    private static string FormatPowerlineEnum(PowerlineFrequency freq) => freq switch
    {
        PowerlineFrequency.Auto => "Auto",
        PowerlineFrequency.FiftyHertz => "50Hz",
        PowerlineFrequency.SixtyHertz => "60Hz",
        PowerlineFrequency.Disabled => "Disabled",
        _ => freq.ToString()
    };

    internal static async Task<MediaCapture?> InitializeMediaCaptureWithFallbackAsync(MediaFrameSourceGroup group)
    {
        var cap = new MediaCapture();
        try
        {
            await cap.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video
            });
            return cap;
        }
        catch
        {
            cap.Dispose();
        }

        var sharedCap = new MediaCapture();
        try
        {
            await sharedCap.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Auto,
                StreamingCaptureMode = StreamingCaptureMode.Video
            });
            return sharedCap;
        }
        catch
        {
            sharedCap.Dispose();
            return null;
        }
    }

    internal static MediaFrameSource? ResolveColorFrameSource(MediaCapture capture, string preferredSourceId)
    {
        if (capture.FrameSources.TryGetValue(preferredSourceId, out var directSource))
            return directSource;

        return capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
               ?? capture.FrameSources.Values.FirstOrDefault();
    }

    internal static async Task<MediaFrameFormat?> SelectTargetFormatAsync(
        MediaFrameSource frameSource,
        CameraResolutionOption? targetResolution)
    {
        try
        {
            MediaFrameFormat? chosen = null;

            if (targetResolution != null)
            {
                chosen = frameSource.SupportedFormats
                    .Where(f => f.VideoFormat.Width == targetResolution.Width &&
                                f.VideoFormat.Height == targetResolution.Height)
                    .OrderByDescending(f => f.FrameRate.Numerator / (double)Math.Max(1u, f.FrameRate.Denominator))
                    .FirstOrDefault();
            }

            if (chosen == null)
            {
                var resolutions = frameSource.SupportedFormats
                    .Where(f => f.VideoFormat.Width > 0 && f.VideoFormat.Height > 0)
                    .Select(f => new CameraResolutionOption((int)f.VideoFormat.Width, (int)f.VideoFormat.Height))
                    .Distinct()
                    .ToList();

                var fallbackRes = SelectWidestAspectLowestResolution(resolutions);
                if (fallbackRes != null)
                {
                    chosen = frameSource.SupportedFormats
                        .FirstOrDefault(f => f.VideoFormat.Width == fallbackRes.Width &&
                                             f.VideoFormat.Height == fallbackRes.Height);
                }
            }

            chosen ??= frameSource.SupportedFormats.FirstOrDefault();

            if (chosen != null)
            {
                await frameSource.SetFormatAsync(chosen);
                return chosen;
            }
        }
        catch { }

        return frameSource.CurrentFormat;
    }

    internal static async Task<SoftwareBitmap?> GetCpuSoftwareBitmapAsync(VideoMediaFrame videoFrame)
    {
        if (videoFrame.SoftwareBitmap != null)
            return videoFrame.SoftwareBitmap;

        if (videoFrame.Direct3DSurface != null)
        {
            try
            {
                return await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                    videoFrame.Direct3DSurface, BitmapAlphaMode.Ignore);
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 純粋画像ベース・ArrayPool再利用ゼロアロケーション 1パス照度解析：
    /// 露光時間・ISO感度APIを一切使用せず、LOH GCアロケーションを発生させずに全PCで安定動作する。
    /// </summary>
    internal static FrameVisualMetrics AnalyzeFrameMetrics(SoftwareBitmap bitmap)
    {
        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        var fmt = bitmap.BitmapPixelFormat;
        SoftwareBitmap? converted = null;
        byte[]? rentedBuffer = null;

        try
        {
            if (fmt is not (BitmapPixelFormat.Bgra8 or BitmapPixelFormat.Nv12 or BitmapPixelFormat.Yuy2))
            {
                converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                fmt = BitmapPixelFormat.Bgra8;
            }

            var target = converted ?? bitmap;
            int bufSize = fmt switch
            {
                BitmapPixelFormat.Bgra8 => w * h * 4,
                BitmapPixelFormat.Nv12 => w * h * 3 / 2,
                BitmapPixelFormat.Yuy2 => w * h * 2,
                _ => w * h * 4
            };

            // ArrayPoolから再利用バッファを借りて直接コピー（毎フレームのLOH確保をゼロ化）
            rentedBuffer = ArrayPool<byte>.Shared.Rent(bufSize);
            var ibuffer = rentedBuffer.AsBuffer(0, 0, bufSize);
            target.CopyToBuffer(ibuffer);
            byte[] raw = rentedBuffer;

            const int step = 2;
            const int gridCols = 8;
            const int gridRows = 6;
            const int totalBlocks = gridCols * gridRows;

            if (w <= step * 2 || h <= step * 2)
            {
                return new FrameVisualMetrics(128, 64, 192, 224, 80, 0, 0, 0, 1.5, 1.5, 0, 1.0, 1.0, 80.0);
            }

            Span<int> weightedHist = stackalloc int[256];
            weightedHist.Clear();

            Span<long> blockLumSum = stackalloc long[totalBlocks];
            Span<int> blockPixCount = stackalloc int[totalBlocks];
            Span<long> blockLapSum = stackalloc long[totalBlocks];
            Span<int> blockLapCount = stackalloc int[totalBlocks];
            Span<long> blockChromaSum = stackalloc long[totalBlocks];
            Span<int> blockChromaCount = stackalloc int[totalBlocks];
            blockLumSum.Clear();
            blockPixCount.Clear();
            blockLapSum.Clear();
            blockLapCount.Clear();
            blockChromaSum.Clear();
            blockChromaCount.Clear();

            // 人物領域（中央下部: 重み1）と環境領域（上部・左右: 重み3）
            int personTopY = (h * 45) / 100;
            int personLeftX = (w * 25) / 100;
            int personRightX = (w * 75) / 100;

            long totalWeight = 0;
            long weightedDark = 0;
            long weightedBright = 0;
            long weightedClipped = 0;

            if (fmt == BitmapPixelFormat.Nv12)
            {
                int uvPlaneOffset = w * h;
                for (int y = step; y < h - step; y += step)
                {
                    int rowOffset = y * w;
                    int prevRowOffset = (y - step) * w;
                    int nextRowOffset = (y + step) * w;
                    int uvRowOffset = uvPlaneOffset + (y / 2) * w;
                    int by = Math.Clamp((y * gridRows) / h, 0, gridRows - 1);
                    int blockRowBase = by * gridCols;
                    bool inPersonRow = y >= personTopY;

                    for (int x = step; x < w - step; x += step)
                    {
                        int idx = rowOffset + x;
                        int lum = raw[idx];
                        int weight = (inPersonRow && x >= personLeftX && x <= personRightX) ? 1 : 3;

                        weightedHist[lum] += weight;
                        totalWeight += weight;
                        if (lum < 55) weightedDark += weight;
                        else if (lum >= 235)
                        {
                            weightedBright += weight;
                            if (lum >= 250) weightedClipped += weight;
                        }

                        int bx = Math.Clamp((x * gridCols) / w, 0, gridCols - 1);
                        int bIdx = blockRowBase + bx;
                        blockLumSum[bIdx] += lum;
                        blockPixCount[bIdx]++;

                        int d2H = Math.Abs((lum << 1) - raw[idx - step] - raw[idx + step]);
                        int d2V = Math.Abs((lum << 1) - raw[prevRowOffset + x] - raw[nextRowOffset + x]);
                        if (d2H <= 28 && d2V <= 28)
                        {
                            blockLapSum[bIdx] += d2H + d2V;
                            blockLapCount[bIdx] += 2;

                            int uvIdx = uvRowOffset + (x & ~1);
                            int uvRightIdx = uvRowOffset + ((x + step) & ~1);
                            if (uvRightIdx + 1 < bufSize)
                            {
                                int dU = Math.Abs(raw[uvIdx] - raw[uvRightIdx]);
                                int dV = Math.Abs(raw[uvIdx + 1] - raw[uvRightIdx + 1]);
                                if (dU <= 16 && dV <= 16)
                                {
                                    blockChromaSum[bIdx] += dU + dV;
                                    blockChromaCount[bIdx] += 2;
                                }
                            }
                        }
                    }
                }
            }
            else if (fmt == BitmapPixelFormat.Yuy2)
            {
                int strideBytes = w * 2;
                for (int y = step; y < h - step; y += step)
                {
                    int rowOffset = y * strideBytes;
                    int prevRowOffset = (y - step) * strideBytes;
                    int nextRowOffset = (y + step) * strideBytes;
                    int by = Math.Clamp((y * gridRows) / h, 0, gridRows - 1);
                    int blockRowBase = by * gridCols;
                    bool inPersonRow = y >= personTopY;

                    for (int x = step; x < w - step; x += step)
                    {
                        int idx = rowOffset + x * 2;
                        int lum = raw[idx];
                        int weight = (inPersonRow && x >= personLeftX && x <= personRightX) ? 1 : 3;

                        weightedHist[lum] += weight;
                        totalWeight += weight;
                        if (lum < 55) weightedDark += weight;
                        else if (lum >= 235)
                        {
                            weightedBright += weight;
                            if (lum >= 250) weightedClipped += weight;
                        }

                        int bx = Math.Clamp((x * gridCols) / w, 0, gridCols - 1);
                        int bIdx = blockRowBase + bx;
                        blockLumSum[bIdx] += lum;
                        blockPixCount[bIdx]++;

                        int d2H = Math.Abs((lum << 1) - raw[idx - step * 2] - raw[idx + step * 2]);
                        int d2V = Math.Abs((lum << 1) - raw[prevRowOffset + x * 2] - raw[nextRowOffset + x * 2]);
                        if (d2H <= 28 && d2V <= 28)
                        {
                            blockLapSum[bIdx] += d2H + d2V;
                            blockLapCount[bIdx] += 2;

                            int macroIdx = rowOffset + (x & ~1) * 2;
                            int macroRightIdx = rowOffset + ((x + step) & ~1) * 2;
                            if (macroRightIdx + 3 < bufSize)
                            {
                                int dU = Math.Abs(raw[macroIdx + 1] - raw[macroRightIdx + 1]);
                                int dV = Math.Abs(raw[macroIdx + 3] - raw[macroRightIdx + 3]);
                                if (dU <= 16 && dV <= 16)
                                {
                                    blockChromaSum[bIdx] += dU + dV;
                                    blockChromaCount[bIdx] += 2;
                                }
                            }
                        }
                    }
                }
            }
            else // Bgra8
            {
                int strideBytes = w * 4;
                for (int y = step; y < h - step; y += step)
                {
                    int rowOffset = y * strideBytes;
                    int prevRowOffset = (y - step) * strideBytes;
                    int nextRowOffset = (y + step) * strideBytes;
                    int by = Math.Clamp((y * gridRows) / h, 0, gridRows - 1);
                    int blockRowBase = by * gridCols;
                    bool inPersonRow = y >= personTopY;

                    for (int x = step; x < w - step; x += step)
                    {
                        int idx = rowOffset + x * 4;
                        int b = raw[idx];
                        int g = raw[idx + 1];
                        int r = raw[idx + 2];
                        int lum = (77 * r + 150 * g + 29 * b) >> 8;

                        int weight = (inPersonRow && x >= personLeftX && x <= personRightX) ? 1 : 3;
                        weightedHist[lum] += weight;
                        totalWeight += weight;
                        if (lum < 55) weightedDark += weight;
                        else if (lum >= 235)
                        {
                            weightedBright += weight;
                            if (lum >= 250) weightedClipped += weight;
                        }

                        int bx = Math.Clamp((x * gridCols) / w, 0, gridCols - 1);
                        int bIdx = blockRowBase + bx;
                        blockLumSum[bIdx] += lum;
                        blockPixCount[bIdx]++;

                        int idxL = idx - step * 4;
                        int lumL = (77 * raw[idxL + 2] + 150 * raw[idxL + 1] + 29 * raw[idxL]) >> 8;
                        int idxR = idx + step * 4;
                        int bR = raw[idxR];
                        int gR = raw[idxR + 1];
                        int rR = raw[idxR + 2];
                        int lumR = (77 * rR + 150 * gR + 29 * bR) >> 8;

                        int idxU = prevRowOffset + x * 4;
                        int lumU = (77 * raw[idxU + 2] + 150 * raw[idxU + 1] + 29 * raw[idxU]) >> 8;
                        int idxD = nextRowOffset + x * 4;
                        int lumD = (77 * raw[idxD + 2] + 150 * raw[idxD + 1] + 29 * raw[idxD]) >> 8;

                        int d2H = Math.Abs((lum << 1) - lumL - lumR);
                        int d2V = Math.Abs((lum << 1) - lumU - lumD);
                        if (d2H <= 28 && d2V <= 28)
                        {
                            blockLapSum[bIdx] += d2H + d2V;
                            blockLapCount[bIdx] += 2;

                            int dRG = Math.Abs((r - g) - (rR - gR));
                            int dBG = Math.Abs((b - g) - (bR - gR));
                            if (dRG <= 20 && dBG <= 20)
                            {
                                blockChromaSum[bIdx] += dRG + dBG;
                                blockChromaCount[bIdx] += 2;
                            }
                        }
                    }
                }
            }

            if (totalWeight == 0)
            {
                return new FrameVisualMetrics(128, 64, 192, 224, 80, 0, 0, 0, 1.5, 1.5, 0, 1.0, 1.0, 80.0);
            }

            long p10Target = (long)(totalWeight * 0.10);
            long p25Target = (long)(totalWeight * 0.25);
            long p50Target = totalWeight / 2;
            long p75Target = (long)(totalWeight * 0.75);
            long p90Target = (long)(totalWeight * 0.90);

            int p10 = 0, p25 = 0, p50 = 0, p75 = 0, p90 = 255;
            long cum = 0;
            for (int i = 0; i < 256; i++)
            {
                long prevCum = cum;
                cum += weightedHist[i];
                if (prevCum <= p10Target && cum > p10Target) p10 = i;
                if (prevCum <= p25Target && cum > p25Target) p25 = i;
                if (prevCum <= p50Target && cum > p50Target) p50 = i;
                if (prevCum <= p75Target && cum > p75Target) p75 = i;
                if (prevCum <= p90Target && cum > p90Target) p90 = i;
            }

            int emptyBins = 0;
            int spanRange = Math.Max(1, p90 - p10);
            if (spanRange >= 20)
            {
                for (int i = p10; i <= p90; i++)
                {
                    if (weightedHist[i] == 0) emptyBins++;
                }
            }
            double gapRatio = (double)emptyBins / spanRange;

            double darkRatio = (double)weightedDark / totalWeight;
            double brightRatio = (double)weightedBright / totalWeight;
            double clippedRatio = (double)weightedClipped / totalWeight;

            Span<double> validBlockNoises = stackalloc double[totalBlocks];
            Span<double> validBlockNormNoises = stackalloc double[totalBlocks];
            int validCount = 0;

            for (int i = 0; i < totalBlocks; i++)
            {
                if (blockPixCount[i] < 16 || blockLapCount[i] < 16) continue;

                double avgY = (double)blockLumSum[i] / blockPixCount[i];
                if (avgY < 25.0 || avgY > 242.0) continue;

                double lumaNoise = ((double)blockLapSum[i] / blockLapCount[i]) * 0.5;
                double chromaNoise = blockChromaCount[i] >= 12
                    ? (double)blockChromaSum[i] / blockChromaCount[i]
                    : 0.0;

                double pureNoise = lumaNoise + 0.55 * chromaNoise;
                double normNoise = pureNoise / Math.Sqrt(Math.Max(avgY, 25.0) / 128.0);

                int insertPos = validCount;
                while (insertPos > 0 && validBlockNormNoises[insertPos - 1] > normNoise)
                {
                    validBlockNormNoises[insertPos] = validBlockNormNoises[insertPos - 1];
                    validBlockNoises[insertPos] = validBlockNoises[insertPos - 1];
                    insertPos--;
                }
                validBlockNormNoises[insertPos] = normNoise;
                validBlockNoises[insertPos] = pureNoise;
                validCount++;
            }

            double finalPureNoise;
            double finalNormNoise;
            if (validCount > 0)
            {
                int q25Idx = Math.Clamp((int)(validCount * 0.25), 0, validCount - 1);
                finalPureNoise = validBlockNoises[q25Idx];
                finalNormNoise = validBlockNormNoises[q25Idx];
            }
            else
            {
                finalPureNoise = p50 < 40 ? 4.0 : 1.5;
                finalNormNoise = p50 < 40 ? 4.0 : 1.5;
            }

            double linearBaseLuma = 0.55 * LinearLumaLut[p50]
                                  + 0.25 * LinearLumaLut[p75]
                                  + 0.20 * LinearLumaLut[p90];

            double noiseDamp = finalNormNoise > 2.0
                ? 1.0 + Math.Min(1.2, Math.Pow((finalNormNoise - 2.0) / 1.5, 1.3))
                : 1.0;

            double darkAreaDamp = (darkRatio > 0.25 && brightRatio < 0.08)
                ? 1.0 + Math.Min(0.8, (darkRatio - 0.25) * 1.3)
                : 1.0;

            double gapDamp = gapRatio > 0.15
                ? 1.0 + Math.Min(0.5, (gapRatio - 0.15) * 1.5)
                : 1.0;

            double darkDampingFactor = Math.Clamp(noiseDamp * darkAreaDamp * gapDamp, 1.0, 3.0);

            double excessBright = Math.Max(0.0, brightRatio - 0.22);
            double excessClipped = Math.Max(0.0, clippedRatio - 0.20);
            double overallBright = Math.Max(0.0, (p50 - 190.0) / 65.0);

            double highlightBoost = 1.0
                                  + excessBright * 3.0
                                  + excessClipped * 4.5
                                  + overallBright * overallBright * 1.5;
            double highLightMultiplier = Math.Clamp(highlightBoost, 1.0, 5.0);

            double normalizedScore = Math.Max(0.0, (linearBaseLuma * highLightMultiplier) / darkDampingFactor);

            return new FrameVisualMetrics(
                MedianY: p50,
                P25Y: p25,
                P75Y: p75,
                P90Y: p90,
                LinearBaseLuma: linearBaseLuma,
                DarkRatio: darkRatio,
                BrightRatio: brightRatio,
                ClippedRatio: clippedRatio,
                PureNoise: finalPureNoise,
                NormalizedNoise: finalNormNoise,
                HistogramGapRatio: gapRatio,
                DarkDampingFactor: darkDampingFactor,
                HighLightMultiplier: highLightMultiplier,
                NormalizedScore: normalizedScore
            );
        }
        finally
        {
            if (rentedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
            converted?.Dispose();
        }
    }
}