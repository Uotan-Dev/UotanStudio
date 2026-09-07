using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace UotanStudio.Views.Workspace;

public partial class AdbLogView
{
    private sealed class LogColumnGenerator(AdbLogView view) : VisualLineElementGenerator
    {
        private readonly List<(int Offset, int Length, string Text, double Width, bool IsPriority)> _columns = [];

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            _columns.Clear();
            var line = context.VisualLine.FirstDocumentLine;
            if (line.LineNumber > view._lines.Count || view._lines[line.LineNumber - 1].Entry is not { } entry)
                return;

            // Keep the original document spans so selecting across elided fields copies their complete text.
            string[] fields = [entry.Timestamp, entry.ProcessId + "-" + entry.ThreadId,
                entry.Tag, entry.ProcessName, entry.Priority.ToString()];
            int[] minimumLengths = [23, 13, 24, 32, 1];
            int[] columnWidths = [24, 14, 25, 33, 3];
            var offset = line.Offset;
            for (var i = 0; i < fields.Length; i++)
            {
                var isPriority = i == fields.Length - 1;
                var length = Math.Max(fields[i].Length, minimumLengths[i]) + (isPriority ? 0 : 1);
                if (length > line.EndOffset - offset)
                {
                    _columns.Clear();
                    return;
                }
                _columns.Add((offset, length, fields[i],
                    Math.Ceiling(context.TextView.WideSpaceWidth * columnWidths[i]), isPriority));
                offset += length;
            }
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            foreach (var column in _columns)
                if (column.Offset >= startOffset) return column.Offset;
            return -1;
        }

        public override VisualLineElement ConstructElement(int offset)
        {
            foreach (var column in _columns)
                if (column.Offset == offset)
                    return new LogColumnElement(column.Text, column.Length, column.Width, column.IsPriority);
            throw new InvalidOperationException("No log column at the requested offset.");
        }
    }

    private sealed class LogColumnElement(string text, int length, double width, bool isPriority)
        : VisualLineElement(1, length)
    {
        public bool IsPriority { get; } = isPriority;

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            new LogColumnRun(text, width, context.TextView.DefaultLineHeight,
                IsPriority ? 0 : context.TextView.WideSpaceWidth, IsPriority, TextRunProperties);
    }

    private sealed class LogColumnRun : DrawableTextRun
    {
        private readonly FormattedText _text;
        private readonly Point _textOffset;
        private readonly Rect _clipBounds;

        public LogColumnRun(string text, double width, double lineHeight, double padding, bool centered,
            TextRunProperties properties)
        {
            Properties = properties;
            // TextFormatter adds the font line gap itself; including it in this run would enlarge every row.
            var metrics = properties.Typeface.GlyphTypeface.Metrics;
            var scale = properties.FontRenderingEmSize / metrics.DesignEmHeight;
            var height = (metrics.Descent - metrics.Ascent) * scale;
            Size = new Size(width, height);
            Baseline = -metrics.Ascent * scale;
            _clipBounds = new Rect(0, (height - lineHeight) / 2, width, lineHeight);
            _text = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                properties.Typeface, properties.FontRenderingEmSize, properties.ForegroundBrush)
            {
                MaxTextWidth = Math.Max(1, width - padding),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            _textOffset = new Point(centered ? (width - _text.WidthIncludingTrailingWhitespace) / 2 : 0,
                (height - _text.Height) / 2);
        }

        public override ReadOnlyMemory<char> Text => ReadOnlyMemory<char>.Empty;
        public override TextRunProperties Properties { get; }
        public override Size Size { get; }
        public override double Baseline { get; }

        public override void Draw(DrawingContext drawingContext, Point origin)
        {
            using (drawingContext.PushClip(_clipBounds.Translate(new Vector(origin.X, origin.Y))))
                drawingContext.DrawText(_text, origin + new Vector(_textOffset.X, _textOffset.Y));
        }
    }

    private static IBrush ContrastingText(Color background)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        var luminance = 0.2126 * Linear(background.R) + 0.7152 * Linear(background.G) + 0.0722 * Linear(background.B);
        return luminance > 0.179 ? Brushes.Black : Brushes.White;
    }
}
