using System.Drawing;
using Microsoft.ML.OnnxRuntime;

namespace LiteShot.Core;

/// <summary>
/// A face YuNet found: its box in image pixels (brow to chin, cheek to cheek), a confidence from 0 to 1, and its five
/// landmarks (eyes, nose tip, mouth corners; "right" is the person's right, on the viewer's left).
/// </summary>
public readonly record struct DetectedFace(RectangleF Box, float Score, FaceLandmarks Landmarks = default)
{
    /// <summary>
    /// How far the head is turned: the nose's offset from between the eyes, as a share of the box's width. About 0 for a
    /// face looking at the camera, ±0.2 or more for a side profile; positive when the face points to the viewer's right.
    /// </summary>
    public float Turn => Box.Width <= 0 ? 0 : (Landmarks.Nose.X - (Landmarks.RightEye.X + Landmarks.LeftEye.X) / 2) / Box.Width;
}

public readonly record struct FaceLandmarks(PointF RightEye, PointF LeftEye, PointF Nose, PointF MouthRight, PointF MouthLeft);

/// <summary>
/// YuNet (Shiqi Yu et al., OpenCV Zoo, MIT license): a 230 KB face detector, run on the CPU with ONNX Runtime, nothing
/// uploaded. Decoding follows OpenCV's FaceDetectorYN. Safe to use from several threads at once.
/// </summary>
public sealed class YuNetDetector : IDisposable
{
    static readonly int[] Strides = [8, 16, 32];
    readonly InferenceSession session;
    readonly string input;

    public YuNetDetector() : this(LoadModel()) { }

    public YuNetDetector(byte[] model)
    {
        // Two threads per run; Detect runs several tiles side by side.
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 2,
        };
        session = new InferenceSession(model, options);
        input = session.InputNames[0];
    }

    static byte[] LoadModel()
    {
        using var stream = typeof(YuNetDetector).Assembly.GetManifestResourceStream("LiteShot.Core.yunet.onnx")
            ?? throw new InvalidOperationException("The YuNet model isn't embedded.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// Finds the faces in an image given as BGRA rows. YuNet sees faces from about 10 to 300 pixels across, so:
    /// the image is searched at full size, in overlapping tiles so a huge multi-monitor capture is never shrunk; again
    /// at twice the size for faces under about 20 px (avatars, thumbnails), skipping blank areas; and at a quarter, a
    /// sixteenth… while a face filling the image could be bigger than YuNet sees. The passes run in parallel and are
    /// merged at the end, so a crowded page of small faces costs about 0.1 s and a 5120 px capture about 0.4 s.
    /// Side profiles score lower than faces seen from the front, so a weaker face whose landmarks say it's turned
    /// sideways is kept too, once YuNet finds it again in a mirrored copy of that spot (see <see cref="Confirmed"/>).
    /// </summary>
    public List<DetectedFace> Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride, float minScore = 0.6f)
    {
        var passes = new List<Pass>();
        // Full size. Tiles overlap by more than the largest face YuNet sees, so every face is whole in one of them.
        passes.AddRange(Tiles(width, height, 1, tile: 1600, overlap: 320, float.MaxValue, minScore, ProfileScore));
        // Twice the size, for the small faces only (bigger ones are measured better at full size), skipping blank areas.
        // A tiny face scores lower, so this pass takes slightly weaker ones; text, code and icons still don't pass.
        foreach (var p in Tiles(width, height, 2, tile: 1024, overlap: 96, SmallFace, minScore * 0.8f, 0))
            if (!Pixels.IsFlat(bgra, stride, p.Window)) passes.Add(p);
        // Smaller, for faces bigger than YuNet sees at full size.
        for (float scale = 0.25f; Math.Min(width, height) * scale * 4 > 300; scale /= 4)
        {
            int sw = (int)MathF.Round(width * scale), sh = (int)MathF.Round(height * scale);
            if (Math.Min(sw, sh) < 10) break;
            passes.Add(new Pass(new Rectangle(0, 0, width, height), sw, sh, float.MaxValue, minScore, ProfileScore));
        }

        // The passes run side by side, so each reads from its own copy of the pixels rather than the caller's span.
        var pixels = bgra[..(stride * height)].ToArray();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
        var found = new List<(DetectedFace Face, bool Weak)>[passes.Count];
        Parallel.For(0, passes.Count, parallel, i => found[i] = Run(pixels, width, height, stride, passes[i]));

        var sure = found.SelectMany(f => f).Where(f => !f.Weak).Select(f => f.Face).ToList();
        var weak = Suppress(found.SelectMany(f => f).Where(f => f.Weak).Select(f => f.Face).ToList())
            .Where(c => !sure.Any(s => Overlap(s.Box, c.Box) > 0.3f)).ToList();
        var confirmed = new bool[weak.Count];
        Parallel.For(0, weak.Count, parallel, i => confirmed[i] = Confirmed(pixels, width, height, stride, weak[i]));
        sure.AddRange(weak.Where((_, i) => confirmed[i]));
        return Suppress(sure);
    }

    /// <summary>Faces up to this tall (in image pixels) are also looked for at twice the size.</summary>
    const float SmallFace = 40;

    // A side profile may score from ProfileScore up when its landmarks show a turn of at least ProfileTurn (frontal faces
    // measure under 0.05, profiles 0.14 to 0.3), and YuNet scores it at least MirrorScore when it looks again, mirrored.
    // Hands, knees and patterns that come close to a face don't survive the mirror.
    const float ProfileScore = 0.35f, ProfileTurn = 0.12f, MirrorScore = 0.3f;

    // One run of YuNet: `Window` of the image resized to Sw × Sh. Faces taller than MaxFace are left to other passes.
    // ProfileScore, when set, lets a sideways face below MinScore through as a weak one, to be confirmed.
    readonly record struct Pass(Rectangle Window, int Sw, int Sh, float MaxFace, float MinScore, float ProfileScore);

    // Tiles covering the image at `scale`, at most `tile` × `tile` pixels each (at that scale), overlapping by `overlap`.
    static IEnumerable<Pass> Tiles(int width, int height, float scale, int tile, int overlap, float maxFace, float minScore, float profileScore)
    {
        foreach (var (x, w) in Split(width, tile / scale, overlap / scale))
            foreach (var (y, h) in Split(height, tile / scale, overlap / scale))
                yield return new Pass(new Rectangle(x, y, w, h), Math.Max(1, (int)MathF.Round(w * scale)), Math.Max(1, (int)MathF.Round(h * scale)),
                    maxFace, minScore, profileScore);
    }

    // The faces one pass keeps, and whether each is a weak side profile. A face cut by the window's inner edge is left to
    // the neighbouring tile, which holds it whole.
    List<(DetectedFace Face, bool Weak)> Run(byte[] pixels, int width, int height, int stride, Pass pass)
    {
        var w = pass.Window;
        float lowest = pass.ProfileScore > 0 ? Math.Min(pass.MinScore, pass.ProfileScore) : pass.MinScore;
        return DetectAt(pixels, width, height, stride, w, pass.Sw, pass.Sh, lowest).Where(f =>
        {
            var b = f.Box;
            bool cut = (w.Left > 0 && b.Left < w.Left + 2) || (w.Top > 0 && b.Top < w.Top + 2)
                || (w.Right < width && b.Right > w.Right - 2) || (w.Bottom < height && b.Bottom > w.Bottom - 2);
            return !cut && b.Height <= pass.MaxFace && (f.Score >= pass.MinScore || MathF.Abs(f.Turn) >= ProfileTurn);
        }).Select(f => (f, f.Score < pass.MinScore)).ToList();
    }

    /// <summary>
    /// A second look at a weak side profile: the area around it, mirrored and sized so the face is about 100 px. A real
    /// profile reads as a face facing the other way; a near miss on fabric or a hand usually doesn't.
    /// </summary>
    bool Confirmed(byte[] pixels, int width, int height, int stride, DetectedFace candidate)
    {
        var b = candidate.Box;
        var area = Rectangle.Intersect(Rectangle.Round(RectangleF.Inflate(b, b.Width * 0.75f, b.Height * 0.75f)), new Rectangle(0, 0, width, height));
        if (area.Width < 8 || area.Height < 8) return false;
        var flipped = new byte[area.Width * area.Height * 4];
        for (int y = 0; y < area.Height; y++)
            for (int x = 0; x < area.Width; x++)
                Buffer.BlockCopy(pixels, (area.Y + y) * stride + (area.Right - 1 - x) * 4, flipped, (y * area.Width + x) * 4, 4);
        float scale = Math.Clamp(100 / b.Height, 0.25f, 2f);
        int sw = Math.Max(1, (int)MathF.Round(area.Width * scale)), sh = Math.Max(1, (int)MathF.Round(area.Height * scale));
        var mirrored = new RectangleF(area.Right - b.Right, b.Y - area.Y, b.Width, b.Height);
        return DetectAt(flipped, area.Width, area.Height, area.Width * 4, new Rectangle(0, 0, area.Width, area.Height), sw, sh, MirrorScore)
            .Any(f => Overlap(f.Box, mirrored) > 0.4f);
    }

    // How much two boxes overlap, as a share of the smaller one.
    static float Overlap(RectangleF a, RectangleF b)
    {
        var i = RectangleF.Intersect(a, b);
        return i.IsEmpty ? 0 : i.Width * i.Height / Math.Min(a.Width * a.Height, b.Width * b.Height);
    }

    // Splits `size` pixels into as few equal, overlapping spans of at most `max` as it takes.
    static List<(int Start, int Length)> Split(int size, float max, float overlap)
    {
        if (size <= max) return [(0, size)];
        int n = (int)MathF.Ceiling((size - overlap) / (max - overlap));
        int length = (int)MathF.Ceiling((size + (n - 1) * overlap) / n);
        var spans = new List<(int, int)>(n);
        for (int i = 0; i < n; i++) spans.Add((Math.Min((int)(i * (length - overlap)), size - length), length));
        return spans;
    }

    // One pass over `window` of the image, resized to sw × sh. YuNet takes BGR planes of raw 0–255 values, padded with
    // zeros to a multiple of 32. Boxes come back in image pixels.
    List<DetectedFace> DetectAt(ReadOnlySpan<byte> bgra, int width, int height, int stride, Rectangle window, int sw, int sh, float minScore)
    {
        int pw = (sw + 31) / 32 * 32, ph = (sh + 31) / 32 * 32, plane = pw * ph;
        var data = new float[3 * plane];
        Pixels.ToPlanes(bgra, width, height, stride, window, sw, sh, data, pw, plane);

        using var tensor = OrtValue.CreateTensorValueFromMemory(data, new long[] { 1, 3, ph, pw });
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, new[] { input }, new[] { tensor }, session.OutputNames);
        var named = new Dictionary<string, OrtValue>();
        for (int i = 0; i < session.OutputNames.Count; i++) named[session.OutputNames[i]] = outputs[i];

        var faces = new List<DetectedFace>();
        float kx = (float)window.Width / sw, ky = (float)window.Height / sh;
        foreach (int step in Strides)
        {
            int cols = pw / step, rows = ph / step;
            var cls = named[$"cls_{step}"].GetTensorDataAsSpan<float>();
            var obj = named[$"obj_{step}"].GetTensorDataAsSpan<float>();
            var box = named[$"bbox_{step}"].GetTensorDataAsSpan<float>();
            var kps = named[$"kps_{step}"].GetTensorDataAsSpan<float>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    float score = MathF.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                    if (score < minScore) continue;
                    float cx = (c + box[i * 4]) * step, cy = (r + box[i * 4 + 1]) * step;
                    float w = MathF.Exp(box[i * 4 + 2]) * step, h = MathF.Exp(box[i * 4 + 3]) * step;
                    var points = new PointF[5];
                    for (int n = 0; n < 5; n++)
                        points[n] = new PointF(window.X + (c + kps[i * 10 + 2 * n]) * step * kx, window.Y + (r + kps[i * 10 + 2 * n + 1]) * step * ky);
                    faces.Add(new DetectedFace(new RectangleF(window.X + (cx - w / 2) * kx, window.Y + (cy - h / 2) * ky, w * kx, h * ky), score,
                        new FaceLandmarks(points[0], points[1], points[2], points[3], points[4])));
                }
        }
        return faces;
    }

    // Non-maximum suppression: keep the best of any boxes that overlap by IoU 0.3, or where one sits mostly inside another
    // (the same face found at two scales).
    static List<DetectedFace> Suppress(List<DetectedFace> faces)
    {
        var kept = new List<DetectedFace>();
        foreach (var f in faces.OrderByDescending(f => f.Score))
        {
            bool duplicate = kept.Any(k =>
            {
                var i = RectangleF.Intersect(k.Box, f.Box);
                float overlap = i.IsEmpty ? 0 : i.Width * i.Height;
                float a = k.Box.Width * k.Box.Height, b = f.Box.Width * f.Box.Height;
                return overlap / (a + b - overlap) > 0.3f || overlap / Math.Min(a, b) > 0.6f;
            });
            if (!duplicate) kept.Add(f);
        }
        return kept;
    }

    public void Dispose() => session.Dispose();
}
