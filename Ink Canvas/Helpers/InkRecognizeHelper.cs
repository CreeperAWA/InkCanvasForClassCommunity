using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Ink.Analysis;
using System.Windows.Media;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// 墨迹形状/手写识别的对外门面。
    /// IACore 路径通过 IPC 调用 x86 辅助进程；WinRT 路径在主进程内直接调用。
    /// 主进程 (.NET 6 x64) 不再直接引用 IAWinFX 类型。
    /// </summary>
    public class InkRecognizeHelper
    {
        public static ShapeRecognizeResult RecognizeShapeIACore(StrokeCollection strokes)
        {
            if (strokes == null || strokes.Count == 0)
                return default;

            var analyzer = new InkAnalyzer();
            analyzer.AddStrokes(strokes);
            analyzer.SetStrokesType(strokes, StrokeType.Drawing);

            AnalysisAlternate analysisAlternate = null;
            int strokesCount = strokes.Count;
            var analyzeResult = analyzer.Analyze();
            if (analyzeResult.Successful)
            {
                var alternates = analyzer.GetAlternates();
                if (alternates.Count > 0)
                {
                    while (strokesCount >= 2)
                    {
                        var alt0 = alternates[0];
                        if (alt0?.AlternateNodes == null || alt0.AlternateNodes.Count == 0)
                            break;
                        var drawNode = alt0.AlternateNodes[0] as InkDrawingNode;
                        if (drawNode == null)
                            break;
                        var shapeOk = IsContainShapeType(drawNode.GetShapeName());
                        if (alt0.Strokes.Contains(strokes.Last()) && shapeOk)
                            break;
                        analyzer.RemoveStroke(strokes[strokes.Count - strokesCount]);
                        strokesCount--;
                        analyzeResult = analyzer.Analyze();
                        if (analyzeResult.Successful)
                            alternates = analyzer.GetAlternates();
                        else
                            break;
                        if (alternates.Count == 0)
                            break;
                    }
                    if (alternates.Count > 0)
                    {
                        var altFinal = alternates[0];
                        if (altFinal?.AlternateNodes != null && altFinal.AlternateNodes.Count > 0)
                            analysisAlternate = altFinal;
                    }
                }
            }

            analyzer.Dispose();

            if (analysisAlternate != null && analysisAlternate.AlternateNodes != null && analysisAlternate.AlternateNodes.Count > 0)
            {
                var node = analysisAlternate.AlternateNodes[0] as InkDrawingNode;
                if (node == null)
                    return default;
                return new ShapeRecognizeResult(node.Centroid, node.HotPoints, analysisAlternate, node);
            }

            return default;
        }

        public static ShapeRecognizeResult RecognizeShape(StrokeCollection strokes) =>
            RecognizeShapeIACore(strokes);

        public static InkShapeRecognitionResult RecognizeShapeUnified(
            StrokeCollection strokes,
            ShapeRecognitionEngineMode mode)
        {
            if (strokes == null || strokes.Count == 0)
                return InkShapeRecognitionResult.Empty;

            if (ShapeRecognitionRouter.ResolveUseWinRt(mode))
                return InkShapeRecognitionResult.Empty;

            var ipc = IpcIACoreClient.Instance.Recognize(strokes);
            if (ipc != null && ipc.IsSuccess)
                return ipc;

            return FromIACoreOrEmpty(RecognizeShapeIACore(strokes));
        }

        public static Task<InkShapeRecognitionResult> RecognizeShapeUnifiedAsync(
            StrokeCollection strokes,
            ShapeRecognitionEngineMode mode)
        {
            if (strokes == null || strokes.Count == 0)
                return Task.FromResult(InkShapeRecognitionResult.Empty);

            return InkRecognitionManager.Instance.RecognizeShapeAsync(strokes, mode);
        }

        public static void WarmupShapeRecognition(ShapeRecognitionEngineMode mode)
        {
            try
            {
                if (ShapeRecognitionRouter.ResolveUseWinRt(mode))
                {
                    WinRtInkShapeRecognizer.Warmup();
                    WinRtHandwritingRecognizer.Warmup();
                }
                else
                {
                    IpcIACoreClient.Instance.Start();
                }
            }
            catch
            {
                // 预热失败不影响启动
            }
        }

        public static Task<HandwritingRecognitionResult> RecognizeHandwritingUnifiedAsync(
            StrokeCollection strokes,
            ShapeRecognitionEngineMode mode) =>
            InkRecognitionManager.Instance.RecognizeHandwritingAsync(strokes, mode);

        public static Task<StrokeCollection> CorrectHandwritingStrokesUnifiedAsync(
            StrokeCollection strokes,
            ShapeRecognitionEngineMode mode) =>
            InkRecognitionManager.Instance.CorrectInkAsync(
                strokes,
                mode,
                MainWindow.Settings?.InkToShape?.EnableWinRtHandwritingStrokeBeautify ?? false,
                MainWindow.Settings?.InkToShape?.HandwritingCorrectionFontFamily);

        public static Task<StrokeCollection> CorrectHandwritingStrokesUnifiedAsync(
            StrokeCollection strokes,
            ShapeRecognitionEngineMode mode,
            bool applyHandwritingBeautify) =>
            InkRecognitionManager.Instance.CorrectInkAsync(
                strokes,
                mode,
                applyHandwritingBeautify,
                MainWindow.Settings?.InkToShape?.HandwritingCorrectionFontFamily);

        internal static InkShapeRecognitionResult FromIACoreOrEmpty(ShapeRecognizeResult legacy)
        {
            if (legacy?.InkDrawingNode == null)
                return InkShapeRecognitionResult.Empty;

            var node = legacy.InkDrawingNode;
            var shape = node.GetShape();
            if (shape == null)
                return InkShapeRecognitionResult.Empty;

            var hot = ClonePointCollection(node.HotPoints);
            return new InkShapeRecognitionResult(
                node.GetShapeName(),
                legacy.Centroid,
                hot,
                shape.Width,
                shape.Height,
                node.Strokes);
        }

        private static PointCollection ClonePointCollection(PointCollection src)
        {
            var dst = new PointCollection();
            if (src == null) return dst;
            foreach (Point p in src)
                dst.Add(p);
            return dst;
        }

        public static bool IsContainShapeType(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            return name.Contains("Triangle") || name.Contains("Circle") ||
                   name.Contains("Rectangle") || name.Contains("Diamond") ||
                   name.Contains("Parallelogram") || name.Contains("Square") ||
                   name.Contains("Ellipse");
        }
    }

    public enum RecognizeLanguage
    {
        SimplifiedChinese = 0x0804,
        TraditionalChinese = 0x7c03,
        English = 0x0809
    }

    public class Circle
    {
        public Circle(System.Windows.Point centroid, double r, Stroke stroke)
        {
            Centroid = centroid;
            R = r;
            Stroke = stroke;
        }

        public System.Windows.Point Centroid { get; set; }
        public double R { get; set; }
        public Stroke Stroke { get; set; }
    }

    public class ShapeRecognizeResult
    {
        public ShapeRecognizeResult(Point centroid, PointCollection hotPoints, AnalysisAlternate analysisAlternate, InkDrawingNode node)
        {
            Centroid = centroid;
            HotPoints = hotPoints;
            AnalysisAlternate = analysisAlternate;
            InkDrawingNode = node;
        }

        public AnalysisAlternate AnalysisAlternate { get; }
        public Point Centroid { get; set; }
        public PointCollection HotPoints { get; }
        public InkDrawingNode InkDrawingNode { get; }
    }
}
