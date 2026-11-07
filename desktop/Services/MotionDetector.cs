using System;
using System.Collections.Generic;
using OpenCvSharp;
using Size = OpenCvSharp.Size;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;

namespace Vision.Services;

public class MotionProfile
{
    public string Label { get; set; } = "";
    public double ThreshVal { get; set; } = 26;
    public double MinRatio { get; set; } = 0.012;
    public int BlurSize { get; set; } = 7;
    public double HoldTime { get; set; } = 4.0;
    public int RequiredFrames { get; set; } = 2;
}

public class MotionDetector : IDisposable
{
    public static readonly Dictionary<string, MotionProfile> Profiles = new()
    {
        ["low"] = new MotionProfile
        {
            Label = "Baixa (Apenas Pessoas e Carros)",
            ThreshVal = 28,
            MinRatio = 0.015,
            BlurSize = 7,
            HoldTime = 4.0,
            RequiredFrames = 2
        },
        ["medium"] = new MotionProfile
        {
            Label = "Média (Recomendado - Equilibrado)",
            ThreshVal = 22,
            MinRatio = 0.009,
            BlurSize = 7,
            HoldTime = 4.0,
            RequiredFrames = 2
        },
        ["high"] = new MotionProfile
        {
            Label = "Alta (Máxima Sensibilidade)",
            ThreshVal = 16,
            MinRatio = 0.005,
            BlurSize = 5,
            HoldTime = 4.0,
            RequiredFrames = 1
        }
    };

    public string Sensitivity { get; set; } = "medium";
    public bool MotionDetected { get; private set; } = false;
    public double LastMotionTime { get; private set; } = 0;
    public double LastMotionRatio { get; private set; } = 0;

    private int _consecutiveFrames = 0;
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

                // MASCARAMENTO INTELIGENTE:
                // Ignora relógio OSD do firmware da câmera no topo (y: 0..12)
                // e marca d'água CCTV no rodapé (y: 78..90) para eliminar 100% dos falsos positivos de horário
                using (var topRoi = new Mat(gray, new Rect(0, 0, 160, 12)))
                {
                    topRoi.SetTo(0);
                }
                using (var bottomRoi = new Mat(gray, new Rect(0, 78, 160, 12)))
                {
                    bottomRoi.SetTo(0);
                }

                if (_bgGray == null)
                {
                    _bgGray = gray.Clone();
                    _consecutiveFrames = 0;
                    return false;
                }

                using var diff = new Mat();
                Cv2.Absdiff(_bgGray, gray, diff);

                using var thresh = new Mat();
                Cv2.Threshold(diff, thresh, profile.ThreshVal, 255, ThresholdTypes.Binary);
                Cv2.Dilate(thresh, thresh, new Mat(), null, 2);

                // Área ativa útil descontando as faixas superior e inferior: 160 * (90 - 24) = 10560
                int changedPixels = Cv2.CountNonZero(thresh);
                double ratio = changedPixels / 10560.0;
                LastMotionRatio = ratio;

                // Atualiza suavemente o fundo de referência
                Cv2.AddWeighted(_bgGray, 0.95, gray, 0.05, 0, _bgGray);

                // FILTRO DE QUADROS CONSECUTIVOS:
                // Descarta ruídos de compressão H.264 ou oscilações de 1 quadro.
                // Exige pelo menos RequiredFrames positivos consecutivos para disparar alerta real.
                if (ratio >= profile.MinRatio)
                {
                    _consecutiveFrames++;
                    if (_consecutiveFrames >= profile.RequiredFrames)
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
                else
                {
                    _consecutiveFrames = 0;
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
            _consecutiveFrames = 0;
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
            _consecutiveFrames = 0;
        }
    }
}
