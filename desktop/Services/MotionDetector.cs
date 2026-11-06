using System;
using System.Collections.Generic;
using OpenCvSharp;
using Size = OpenCvSharp.Size;
using Point = OpenCvSharp.Point;

namespace PorteiroVision.Services;

public class MotionProfile
{
    public string Label { get; set; } = "";
    public double ThreshVal { get; set; } = 24;
    public double MinRatio { get; set; } = 0.008;
    public int BlurSize { get; set; } = 7;
    public double HoldTime { get; set; } = 4.0;
}

public class MotionDetector : IDisposable
{
    public static readonly Dictionary<string, MotionProfile> Profiles = new()
    {
        ["low"] = new MotionProfile
        {
            Label = "Baixa (Apenas Pessoas e Carros)",
            ThreshVal = 32,
            MinRatio = 0.018,
            BlurSize = 9,
            HoldTime = 4.0
        },
        ["medium"] = new MotionProfile
        {
            Label = "Média (Recomendado - Equilibrado)",
            ThreshVal = 24,
            MinRatio = 0.008,
            BlurSize = 7,
            HoldTime = 4.0
        },
        ["high"] = new MotionProfile
        {
            Label = "Alta (Máxima Sensibilidade)",
            ThreshVal = 16,
            MinRatio = 0.0035,
            BlurSize = 5,
            HoldTime = 4.0
        }
    };

    public string Sensitivity { get; set; } = "medium";
    public bool MotionDetected { get; private set; } = false;
    public double LastMotionTime { get; private set; } = 0;

    private Mat? _bgGray;
    private readonly object _lock = new();

    public bool Detect(Mat frame)
    {
        if (frame.Empty()) return false;

        lock (_lock)
        {
            try
            {
                double nowTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                if (!Profiles.TryGetValue(Sensitivity, out var profile))
                {
                    profile = Profiles["medium"];
                }

                using var small = new Mat();
                Cv2.Resize(frame, small, new Size(160, 90), 0, 0, InterpolationFlags.Area);

                using var gray = new Mat();
                Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

                int b = profile.BlurSize;
                if (b % 2 == 0) b++;
                Cv2.GaussianBlur(gray, gray, new Size(b, b), 0);

                if (_bgGray == null)
                {
                    _bgGray = gray.Clone();
                    return false;
                }

                using var diff = new Mat();
                Cv2.Absdiff(_bgGray, gray, diff);

                using var thresh = new Mat();
                Cv2.Threshold(diff, thresh, profile.ThreshVal, 255, ThresholdTypes.Binary);
                Cv2.Dilate(thresh, thresh, new Mat(), null, 2);

                int changedPixels = Cv2.CountNonZero(thresh);
                double ratio = changedPixels / 14400.0; // 160 * 90

                // Atualiza suavemente o fundo de referência
                Cv2.AddWeighted(_bgGray, 0.95, gray, 0.05, 0, _bgGray);

                if (ratio >= profile.MinRatio)
                {
                    LastMotionTime = nowTs;
                    MotionDetected = true;
                    return true;
                }
                else
                {
                    MotionDetected = (nowTs - LastMotionTime) < profile.HoldTime;
                    return MotionDetected;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _bgGray?.Dispose();
            _bgGray = null;
            MotionDetected = false;
            LastMotionTime = 0;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _bgGray?.Dispose();
            _bgGray = null;
        }
    }
}
