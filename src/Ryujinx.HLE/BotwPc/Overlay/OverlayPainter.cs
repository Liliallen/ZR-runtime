using Ryujinx.Common.BotwPc.Overlay;
using SkiaSharp;
using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.BotwPc.Overlay
{
    public enum HudMode
    {
        Hidden,
        Basic,
        Detailed,
        Developer,
    }

    /// <summary>What the performance HUD shows (filled by the host from existing counters).</summary>
    public sealed class HudData
    {
        public HudMode Mode { get; init; }
        public float Fps { get; init; }
        public float FrameMs { get; init; }
        public float TargetFps { get; init; }
        public float[] FrameTimes { get; init; } = [];
        public List<(string Label, string Value)> Lines { get; init; } = [];
        public bool Compiling { get; init; }
        public bool Paused { get; init; }
    }

    /// <summary>
    /// Draws the overlay menu and the performance HUD with SkiaSharp (already used by Ryujinx's software keyboard) into
    /// a premultiplied BGRA image, at output resolution. Platform-neutral: the presenter of each backend composites the
    /// image. Original artwork only (shapes and text).
    /// </summary>
    public sealed class OverlayPainter : IDisposable
    {
        private static readonly SKColor Panel = new(10, 14, 18, 232);
        private static readonly SKColor Accent = new(92, 206, 232);
        private static readonly SKColor Text = new(232, 236, 240);
        private static readonly SKColor Dim = new(150, 160, 170);
        private static readonly SKColor Warn = new(240, 176, 80);
        private static readonly SKColor RowHighlight = new(92, 206, 232, 48);

        private readonly SKTypeface _typeface;
        private readonly SKPaint _paint = new() { IsAntialias = true };

        public OverlayPainter()
        {
            // A face that has CJK glyphs (Traditional Chinese strings) and Latin; found by character, not by font name.
            _typeface = SKFontManager.Default.MatchCharacter('體') ?? SKTypeface.Default;
        }

        /// <summary>Screen rectangles of the menu rows from the last paint (for mouse hit tests), in output pixels.</summary>
        public List<(int Row, SKRect Rect)> RowRects { get; } = [];

        /// <summary>Screen rectangles of the page tabs from the last paint.</summary>
        public List<(int Page, SKRect Rect)> TabRects { get; } = [];

        /// <summary>Paints the whole output-size image (menu open). Returns BGRA premultiplied pixels.</summary>
        public byte[] PaintMenu(int width, int height, OverlayMenu menu, HudData hud, float userScale, string hints)
        {
            using SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using SKCanvas canvas = new(bitmap);
            canvas.Clear(new SKColor(0, 0, 0, 110));

            float s = height / 1080f * userScale;
            DrawMenu(canvas, width, height, s, menu, hints);

            if (hud != null && hud.Mode != HudMode.Hidden)
            {
                DrawHud(canvas, 16 * s, 16 * s, s, hud);
            }

            return bitmap.Bytes;
        }

        /// <summary>Paints only the HUD box (menu closed); returns its pixels and size (placed at 16·scale, 16·scale).</summary>
        public byte[] PaintHud(int outputHeight, HudData hud, float userScale, out int width, out int height, out int x, out int y)
        {
            float s = outputHeight / 1080f * userScale;
            SKSize size = MeasureHud(s, hud);
            width = Math.Max(1, (int)MathF.Ceiling(size.Width));
            height = Math.Max(1, (int)MathF.Ceiling(size.Height));
            x = y = (int)(16 * s);

            using SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using SKCanvas canvas = new(bitmap);
            canvas.Clear(SKColors.Transparent);
            DrawHud(canvas, 0, 0, s, hud);

            return bitmap.Bytes;
        }

        private void DrawText(SKCanvas canvas, string text, float x, float y, float size, SKColor color, SKTextAlign align = SKTextAlign.Left)
        {
            _paint.Typeface = _typeface;
            _paint.TextSize = size;
            _paint.TextAlign = align;
            _paint.Color = color;
            _paint.Style = SKPaintStyle.Fill;
            _paint.SubpixelText = true;
            _paint.LcdRenderText = false;
            canvas.DrawText(text ?? "", x, y, _paint);
        }

        private float TextWidth(string text, float size)
        {
            _paint.Typeface = _typeface;
            _paint.TextSize = size;

            return _paint.MeasureText(text ?? "");
        }

        private void DrawMenu(SKCanvas canvas, int width, int height, float s, OverlayMenu menu, string hints)
        {
            float panelW = Math.Min(width - 40 * s, 1040 * s), panelH = Math.Min(height - 40 * s, 820 * s);
            SKRect panel = SKRect.Create((width - panelW) / 2, (height - panelH) / 2, panelW, panelH);

            _paint.Style = SKPaintStyle.Fill;
            _paint.Color = Panel;
            canvas.DrawRoundRect(panel, 14 * s, 14 * s, _paint);
            _paint.Style = SKPaintStyle.Stroke;
            _paint.StrokeWidth = 1.5f * s;
            _paint.Color = Accent.WithAlpha(150);
            canvas.DrawRoundRect(panel, 14 * s, 14 * s, _paint);

            float pad = 28 * s, x0 = panel.Left + pad, x1 = panel.Right - pad;
            float y = panel.Top + 46 * s;
            DrawText(canvas, "BOTW-PC", x0, y, 30 * s, Accent);
            DrawText(canvas, "L3 + R3  /  Insert", x1, y, 18 * s, Dim, SKTextAlign.Right);

            // Tabs
            y += 34 * s;
            TabRects.Clear();
            float tx = x0;

            for (int i = 0; i < menu.Pages.Count; i++)
            {
                string title = menu.Pages[i].Title;
                float w = TextWidth(title, 21 * s);
                bool active = i == menu.PageIndex;
                DrawText(canvas, title, tx, y + 22 * s, 21 * s, active ? Text : Dim);

                if (active)
                {
                    _paint.Style = SKPaintStyle.Fill;
                    _paint.Color = Accent;
                    canvas.DrawRect(SKRect.Create(tx, y + 32 * s, w, 3 * s), _paint);
                }

                TabRects.Add((i, SKRect.Create(tx - 6 * s, y - 4 * s, w + 12 * s, 44 * s)));
                tx += w + 30 * s;
            }

            y += 52 * s;
            _paint.Color = Accent.WithAlpha(60);
            canvas.DrawRect(SKRect.Create(x0, y, x1 - x0, 1 * s), _paint);
            y += 12 * s;

            // Rows (scrolled so the selected one is visible)
            float rowH = 44 * s, listBottom = panel.Bottom - 150 * s;
            int visible = Math.Max(1, (int)((listBottom - y) / rowH));
            List<OverlayItem> items = menu.Page.Items;
            int first = Math.Clamp(menu.Row - visible + 2, 0, Math.Max(0, items.Count - visible));
            RowRects.Clear();

            for (int i = first; i < items.Count && i < first + visible; i++)
            {
                OverlayItem item = items[i];
                SKRect row = SKRect.Create(x0 - 10 * s, y, x1 - x0 + 20 * s, rowH);
                bool selected = i == menu.Row && item.Selectable;
                bool enabled = item.Enabled();

                if (item.IsHeader)
                {
                    DrawText(canvas, item.Label, x0, y + rowH * 0.72f, 17 * s, Accent);
                    y += rowH;
                    continue;
                }

                if (selected)
                {
                    _paint.Style = SKPaintStyle.Fill;
                    _paint.Color = RowHighlight;
                    canvas.DrawRoundRect(row, 8 * s, 8 * s, _paint);
                    _paint.Color = Accent;
                    canvas.DrawRect(SKRect.Create(row.Left, row.Top + 8 * s, 4 * s, rowH - 16 * s), _paint);
                }

                SKColor color = enabled ? Text : Dim;
                DrawText(canvas, item.Label, x0 + 8 * s, y + rowH * 0.66f, 20 * s, color);

                string value = item.Value?.Invoke();

                if (value != null)
                {
                    string shown = item.Change != null && enabled ? $"‹  {value}  ›" : value;
                    DrawText(canvas, shown, x1 - 8 * s, y + rowH * 0.66f, 20 * s, enabled ? Text : Dim, SKTextAlign.Right);
                }
                else if (item.Activate != null)
                {
                    DrawText(canvas, "›", x1 - 8 * s, y + rowH * 0.66f, 22 * s, color, SKTextAlign.Right);
                }

                if (item.Apply == ApplyKind.GameRestart)
                {
                    float labelW = TextWidth(item.Label, 20 * s);
                    DrawBadge(canvas, x0 + 20 * s + labelW, y + rowH * 0.5f, s, "RESTART");
                }

                RowRects.Add((i, row));
                y += rowH;
            }

            // Note for the selected row
            float noteTop = panel.Bottom - 136 * s;
            _paint.Style = SKPaintStyle.Fill;
            _paint.Color = Accent.WithAlpha(60);
            canvas.DrawRect(SKRect.Create(x0, noteTop, x1 - x0, 1 * s), _paint);
            OverlayItem sel = menu.Selected;

            if (sel != null)
            {
                string note = sel.Note?.Invoke() ?? "";
                string apply = sel.Apply switch
                {
                    ApplyKind.Live => "Applies immediately.",
                    ApplyKind.GameRestart => "Saved now; takes effect at the next game start.",
                    _ => "",
                };
                DrawWrapped(canvas, note, x0, noteTop + 30 * s, x1 - x0, 18 * s, Text, 2);
                DrawText(canvas, apply, x0, noteTop + 84 * s, 16 * s, sel.Apply == ApplyKind.GameRestart ? Warn : Dim);
            }

            DrawText(canvas, hints, (x0 + x1) / 2, panel.Bottom - 18 * s, 17 * s, Dim, SKTextAlign.Center);
        }

        private void DrawBadge(SKCanvas canvas, float x, float cy, float s, string text)
        {
            float w = TextWidth(text, 12 * s) + 12 * s, h = 18 * s;
            _paint.Style = SKPaintStyle.Stroke;
            _paint.StrokeWidth = 1.2f * s;
            _paint.Color = Warn;
            canvas.DrawRoundRect(SKRect.Create(x, cy - h / 2, w, h), 4 * s, 4 * s, _paint);
            DrawText(canvas, text, x + 6 * s, cy + 4.5f * s, 12 * s, Warn);
        }

        private void DrawWrapped(SKCanvas canvas, string text, float x, float y, float maxWidth, float size, SKColor color, int maxLines)
        {
            List<string> lines = [];
            string line = "";

            // Wraps at spaces, or between characters for CJK text without spaces.
            foreach (string word in SplitWords(text))
            {
                string candidate = line + word;

                if (line.Length > 0 && TextWidth(candidate, size) > maxWidth)
                {
                    lines.Add(line.TrimEnd());
                    line = word.TrimStart();
                }
                else
                {
                    line = candidate;
                }
            }

            lines.Add(line);

            for (int i = 0; i < lines.Count && i < maxLines; i++)
            {
                DrawText(canvas, lines[i], x, y + i * size * 1.35f, size, color);
            }
        }

        private static IEnumerable<string> SplitWords(string text)
        {
            int start = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] >= 0x2E80)
                {
                    if (i > start)
                    {
                        yield return text[start..i];
                    }

                    yield return text[i..(i + 1)];
                    start = i + 1;
                }
                else if (text[i] == ' ')
                {
                    yield return text[start..(i + 1)];
                    start = i + 1;
                }
            }

            if (start < text.Length)
            {
                yield return text[start..];
            }
        }

        private SKSize MeasureHud(float s, HudData hud)
        {
            float w = hud.Mode == HudMode.Basic ? 230 * s : 360 * s;
            float h = hud.Mode == HudMode.Basic ? 44 * s : 44 * s + 76 * s + hud.Lines.Count * 22 * s + 10 * s;

            return new SKSize(w, h);
        }

        private void DrawHud(SKCanvas canvas, float x, float y, float s, HudData hud)
        {
            SKSize size = MeasureHud(s, hud);
            _paint.Style = SKPaintStyle.Fill;
            _paint.Color = new SKColor(8, 10, 12, 190);
            canvas.DrawRoundRect(SKRect.Create(x, y, size.Width, size.Height), 8 * s, 8 * s, _paint);

            bool low = hud.TargetFps > 0 && hud.Fps < hud.TargetFps * 0.9f;
            DrawText(canvas, $"{hud.Fps:0.0} FPS", x + 12 * s, y + 30 * s, 22 * s, low && !hud.Paused ? Warn : Text);
            DrawText(canvas, hud.Paused ? "paused" : $"{hud.FrameMs:0.0} ms", x + size.Width - 12 * s, y + 30 * s, 18 * s, hud.Paused ? Warn : Dim, SKTextAlign.Right);

            if (hud.Compiling)
            {
                DrawText(canvas, "●", x + size.Width - 92 * s, y + 29 * s, 14 * s, Warn, SKTextAlign.Right);
            }

            if (hud.Mode == HudMode.Basic)
            {
                return;
            }

            // Frame-time graph (0–50 ms) with 8.3 / 16.7 / 33.3 ms guides.
            SKRect graph = SKRect.Create(x + 12 * s, y + 44 * s, size.Width - 24 * s, 66 * s);
            _paint.Color = new SKColor(255, 255, 255, 20);
            canvas.DrawRect(graph, _paint);

            foreach (float guide in new[] { 1000f / 120, 1000f / 60, 1000f / 30 })
            {
                float gy = graph.Bottom - graph.Height * Math.Min(guide / 50f, 1f);
                _paint.Color = new SKColor(255, 255, 255, 40);
                canvas.DrawRect(SKRect.Create(graph.Left, gy, graph.Width, 1 * s), _paint);
            }

            float[] times = hud.FrameTimes;

            if (times.Length > 1)
            {
                using SKPath path = new();

                for (int i = 0; i < times.Length; i++)
                {
                    float px = graph.Left + graph.Width * i / (times.Length - 1);
                    float py = graph.Bottom - graph.Height * Math.Min(times[i] / 50f, 1f);

                    if (i == 0)
                    {
                        path.MoveTo(px, py);
                    }
                    else
                    {
                        path.LineTo(px, py);
                    }
                }

                _paint.Style = SKPaintStyle.Stroke;
                _paint.StrokeWidth = 1.5f * s;
                _paint.Color = Accent;
                canvas.DrawPath(path, _paint);
                _paint.Style = SKPaintStyle.Fill;
            }

            float ly = graph.Bottom + 26 * s;

            foreach ((string label, string value) in hud.Lines)
            {
                DrawText(canvas, label, x + 12 * s, ly, 16 * s, Dim);
                DrawText(canvas, value, x + size.Width - 12 * s, ly, 16 * s, Text, SKTextAlign.Right);
                ly += 22 * s;
            }
        }

        public void Dispose()
        {
            _paint.Dispose();
        }
    }
}
