using Ryujinx.Common.Configuration.Hid.Controller;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ryujinx.HLE.BotwPc.Prompts
{
    /// <summary>
    /// Procedurally drawn Xbox-style button glyphs (original artwork, no game or third-party assets).
    /// Glyphs are single-channel masks in the style of BotW's own: a solid shape with the label cut out,
    /// so the game's text colour and shadow apply to them as to its own glyphs.
    /// </summary>
    public static class GlyphArt
    {
        /// <summary>D-pad glyph variants (the game has one per direction plus combined ones).</summary>
        public enum Dpad { All, Up, Down, Left, Right, UpDown, LeftRight }

        private const int Samples = 4;

        /// <summary>Glyph of a physical input, drawn to fill a <paramref name="width"/> x <paramref name="height"/> box (aspect kept, centred).</summary>
        public static byte[] Draw(GamepadInputId input, int width, int height) => input switch
        {
            GamepadInputId.DpadUp => DrawDpad(Dpad.Up, width, height),
            GamepadInputId.DpadDown => DrawDpad(Dpad.Down, width, height),
            GamepadInputId.DpadLeft => DrawDpad(Dpad.Left, width, height),
            GamepadInputId.DpadRight => DrawDpad(Dpad.Right, width, height),
            _ => Render(width, height, AspectOf(input), (p, s) => Shape(input, p, s)),
        };

        public static byte[] DrawDpad(Dpad variant, int width, int height) => Render(width, height, 1f, (p, s) => DpadShape(variant, p, s));

        private static float AspectOf(GamepadInputId input) => input switch
        {
            GamepadInputId.LeftShoulder or GamepadInputId.RightShoulder => 1.45f,
            GamepadInputId.LeftTrigger or GamepadInputId.RightTrigger => 1.2f,
            _ => 1f,
        };

        /// <summary>
        /// Rasterises <paramref name="inside"/> (point in a unit box scaled to aspect x 1, and pixel size) with 4x4
        /// supersampling into a mask fitted in the box.
        /// </summary>
        private static byte[] Render(int width, int height, float aspect, Func<Vector2, float, bool> inside)
        {
            byte[] mask = new byte[width * height];
            float h = Math.Min(height, width / aspect), w = h * aspect;
            float ox = (width - w) / 2, oy = (height - h) / 2;
            float pixel = 1f / h;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int hits = 0;

                    for (int sy = 0; sy < Samples; sy++)
                    {
                        for (int sx = 0; sx < Samples; sx++)
                        {
                            Vector2 p = new((x + (sx + 0.5f) / Samples - ox) / h, (y + (sy + 0.5f) / Samples - oy) / h);

                            if (p.X >= 0 && p.Y >= 0 && p.X <= aspect && p.Y <= 1 && inside(p, pixel))
                            {
                                hits++;
                            }
                        }
                    }

                    mask[y * width + x] = (byte)((hits * 255 + Samples * Samples / 2) / (Samples * Samples));
                }
            }

            return mask;
        }

        private static bool Shape(GamepadInputId input, Vector2 p, float pixel)
        {
            Vector2 c = new(0.5f, 0.5f);

            switch (input)
            {
                case GamepadInputId.A:
                case GamepadInputId.B:
                case GamepadInputId.X:
                case GamepadInputId.Y:
                    return InCircle(p, c, 0.5f) && !Text(input.ToString(), p, new Vector2(0.5f, 0.52f), 0.5f);

                case GamepadInputId.LeftShoulder:
                case GamepadInputId.RightShoulder:
                    {
                        // Bumper: wide rounded bar, outer upper corner more rounded.
                        bool left = input == GamepadInputId.LeftShoulder;
                        float r = PointInRoundRect(p, new Vector2(0.02f, 0.14f), new Vector2(1.43f, 0.86f), left ? 0.36f : 0.16f, left ? 0.16f : 0.36f, 0.14f);
                        return r <= 0 && !Text(left ? "LB" : "RB", p, new Vector2(0.725f, 0.52f), 0.46f);
                    }

                case GamepadInputId.LeftTrigger:
                case GamepadInputId.RightTrigger:
                    {
                        // Trigger: tall shape with a strongly rounded top.
                        float r = PointInRoundRect(p, new Vector2(0.06f, 0.02f), new Vector2(1.14f, 0.98f), 0.42f, 0.42f, 0.12f);
                        return r <= 0 && !Text(input == GamepadInputId.LeftTrigger ? "LT" : "RT", p, new Vector2(0.6f, 0.58f), 0.44f);
                    }

                case GamepadInputId.Minus: // View: two overlapping windows
                    return InCircle(p, c, 0.5f) && !(RectOutline(p, new Vector2(0.24f, 0.26f), new Vector2(0.62f, 0.6f), 0.065f) ||
                                                      InRect(p, new Vector2(0.38f, 0.40f), new Vector2(0.76f, 0.74f)));

                case GamepadInputId.Plus: // Menu: three lines
                    return InCircle(p, c, 0.5f) && !(InRect(p, new Vector2(0.26f, 0.28f), new Vector2(0.74f, 0.36f)) ||
                                                      InRect(p, new Vector2(0.26f, 0.46f), new Vector2(0.74f, 0.54f)) ||
                                                      InRect(p, new Vector2(0.26f, 0.64f), new Vector2(0.74f, 0.72f)));

                case GamepadInputId.LeftStick:
                case GamepadInputId.RightStick:
                    // Stick cap with a ring and "LS"/"RS".
                    return (InCircle(p, c, 0.5f) && !InCircle(p, c, 0.44f)) ||
                           (InCircle(p, c, 0.38f) && !Text(input == GamepadInputId.LeftStick ? "LS" : "RS", p, new Vector2(0.5f, 0.52f), 0.34f));

                case GamepadInputId.Misc1: // Share: rounded square with an upward arrow
                    return PointInRoundRect(p, new Vector2(0.08f, 0.08f), new Vector2(0.92f, 0.92f), 0.2f, 0.2f, 0.2f) <= 0 &&
                           !(Capsule(p, new Vector2(0.5f, 0.3f), new Vector2(0.5f, 0.72f), 0.06f) ||
                             Capsule(p, new Vector2(0.5f, 0.28f), new Vector2(0.33f, 0.45f), 0.06f) ||
                             Capsule(p, new Vector2(0.5f, 0.28f), new Vector2(0.67f, 0.45f), 0.06f));

                case GamepadInputId.Guide:
                    return InCircle(p, c, 0.5f) && !(InCircle(p, c, 0.3f) && !InCircle(p, c, 0.2f));

                case GamepadInputId.Paddle1:
                case GamepadInputId.Paddle2:
                case GamepadInputId.Paddle3:
                case GamepadInputId.Paddle4:
                    return PointInRoundRect(p, new Vector2(0.04f, 0.1f), new Vector2(0.96f, 0.9f), 0.3f, 0.3f, 0.3f) <= 0 &&
                           !Text("P" + (input - GamepadInputId.Paddle1 + 1), p, new Vector2(0.5f, 0.52f), 0.42f);

                default:
                    // Unbound or unknown: a question mark, so a missing binding is never shown as a wrong button.
                    return InCircle(p, c, 0.5f) && !Text("?", p, new Vector2(0.5f, 0.52f), 0.5f);
            }
        }

        private static bool DpadShape(Dpad variant, Vector2 p, float pixel)
        {
            const float Arm = 0.17f, Line = 0.075f;
            bool vertical = InRect(p, new Vector2(0.5f - Arm, 0f), new Vector2(0.5f + Arm, 1f));
            bool horizontal = InRect(p, new Vector2(0f, 0.5f - Arm), new Vector2(1f, 0.5f + Arm));

            if (!vertical && !horizontal)
            {
                return false;
            }

            if (variant == Dpad.All)
            {
                return true;
            }

            bool up = variant is Dpad.Up or Dpad.UpDown, down = variant is Dpad.Down or Dpad.UpDown;
            bool left = variant is Dpad.Left or Dpad.LeftRight, right = variant is Dpad.Right or Dpad.LeftRight;

            // Highlighted arms are solid; the rest of the cross is an outline.
            if ((up && p.Y < 0.5f - Arm && vertical) || (down && p.Y > 0.5f + Arm && vertical) ||
                (left && p.X < 0.5f - Arm && horizontal) || (right && p.X > 0.5f + Arm && horizontal))
            {
                return true;
            }

            bool innerVertical = InRect(p, new Vector2(0.5f - Arm + Line, Line), new Vector2(0.5f + Arm - Line, 1f - Line));
            bool innerHorizontal = InRect(p, new Vector2(Line, 0.5f - Arm + Line), new Vector2(1f - Line, 0.5f + Arm - Line));

            return !(innerVertical || innerHorizontal);
        }

        private static bool InCircle(Vector2 p, Vector2 c, float r) => Vector2.DistanceSquared(p, c) <= r * r;

        private static bool InRect(Vector2 p, Vector2 min, Vector2 max) => p.X >= min.X && p.Y >= min.Y && p.X <= max.X && p.Y <= max.Y;

        private static bool RectOutline(Vector2 p, Vector2 min, Vector2 max, float t) =>
            InRect(p, min, max) && !InRect(p, min + new Vector2(t, t), max - new Vector2(t, t));

        /// <summary>Signed test for a rectangle with individual top-left/top-right/bottom corner radii (≤ 0 inside).</summary>
        private static float PointInRoundRect(Vector2 p, Vector2 min, Vector2 max, float rTopLeft, float rTopRight, float rBottom)
        {
            if (!InRect(p, min, max))
            {
                return 1;
            }

            (Vector2 corner, float r)[] corners =
            [
                (new Vector2(min.X + rTopLeft, min.Y + rTopLeft), rTopLeft),
                (new Vector2(max.X - rTopRight, min.Y + rTopRight), rTopRight),
                (new Vector2(min.X + rBottom, max.Y - rBottom), rBottom),
                (new Vector2(max.X - rBottom, max.Y - rBottom), rBottom),
            ];

            for (int i = 0; i < 4; i++)
            {
                (Vector2 k, float r) = corners[i];
                bool outsideX = i % 2 == 0 ? p.X < k.X : p.X > k.X;
                bool outsideY = i < 2 ? p.Y < k.Y : p.Y > k.Y;

                if (outsideX && outsideY && Vector2.Distance(p, k) > r)
                {
                    return 1;
                }
            }

            return -1;
        }

        private static bool Capsule(Vector2 p, Vector2 a, Vector2 b, float r)
        {
            Vector2 ab = b - a;
            float t = ab.LengthSquared() == 0 ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);

            return Vector2.DistanceSquared(p, a + t * ab) <= r * r;
        }

        /// <summary>
        /// Stroke font: each character is a set of polylines in a 0.62 x 1 cell (y down). Arcs are pre-flattened.
        /// </summary>
        private static readonly Dictionary<char, Vector2[][]> _strokes = BuildStrokes();

        private static Dictionary<char, Vector2[][]> BuildStrokes()
        {
            static Vector2 V(float x, float y) => new(x, y);

            static Vector2[] Arc(float cx, float cy, float rx, float ry, float fromDeg, float toDeg, Vector2? start = null, Vector2? end = null)
            {
                const int Steps = 12;
                List<Vector2> points = [];

                if (start.HasValue)
                {
                    points.Add(start.Value);
                }

                for (int i = 0; i <= Steps; i++)
                {
                    float a = MathF.PI / 180 * (fromDeg + (toDeg - fromDeg) * i / Steps);
                    points.Add(V(cx + rx * MathF.Cos(a), cy + ry * MathF.Sin(a)));
                }

                if (end.HasValue)
                {
                    points.Add(end.Value);
                }

                return [.. points];
            }

            return new Dictionary<char, Vector2[][]>
            {
                ['A'] = [[V(0, 1), V(0.31f, 0), V(0.62f, 1)], [V(0.12f, 0.66f), V(0.5f, 0.66f)]],
                ['B'] = [[V(0, 0), V(0, 1)], Arc(0.32f, 0.25f, 0.22f, 0.25f, -90, 90, V(0, 0), V(0, 0.5f)), Arc(0.36f, 0.75f, 0.26f, 0.25f, -90, 90, V(0, 0.5f), V(0, 1))],
                ['X'] = [[V(0, 0), V(0.62f, 1)], [V(0.62f, 0), V(0, 1)]],
                ['Y'] = [[V(0, 0), V(0.31f, 0.5f), V(0.62f, 0)], [V(0.31f, 0.5f), V(0.31f, 1)]],
                ['L'] = [[V(0, 0), V(0, 1), V(0.56f, 1)]],
                ['R'] = [[V(0, 1), V(0, 0)], Arc(0.32f, 0.26f, 0.24f, 0.26f, -90, 90, V(0, 0), V(0, 0.52f)), [V(0.26f, 0.52f), V(0.6f, 1)]],
                ['T'] = [[V(0, 0), V(0.62f, 0)], [V(0.31f, 0), V(0.31f, 1)]],
                ['S'] = [Arc(0.31f, 0.26f, 0.27f, 0.25f, -20, -270), Arc(0.31f, 0.75f, 0.29f, 0.25f, -90, 160)],
                ['P'] = [[V(0, 1), V(0, 0)], Arc(0.32f, 0.27f, 0.26f, 0.27f, -90, 90, V(0, 0), V(0, 0.54f))],
                ['1'] = [[V(0.12f, 0.2f), V(0.36f, 0), V(0.36f, 1)]],
                ['2'] = [Arc(0.31f, 0.28f, 0.27f, 0.27f, -170, 30), [V(0.54f, 0.42f), V(0.02f, 1), V(0.62f, 1)]],
                ['3'] = [Arc(0.29f, 0.26f, 0.27f, 0.24f, -160, 90), Arc(0.29f, 0.74f, 0.3f, 0.26f, -90, 160)],
                ['4'] = [[V(0.46f, 1), V(0.46f, 0), V(0, 0.7f), V(0.62f, 0.7f)]],
                ['?'] = [Arc(0.31f, 0.28f, 0.27f, 0.26f, -180, 60, null, V(0.31f, 0.7f)), [V(0.31f, 0.95f), V(0.31f, 0.96f)]],
            };
        }

        /// <summary>True when <paramref name="p"/> is on the text centred at <paramref name="centre"/> with cap height <paramref name="size"/>.</summary>
        private static bool Text(string text, Vector2 p, Vector2 centre, float size)
        {
            const float CharWidth = 0.62f, Gap = 0.3f, Weight = 0.1f;
            float stroke = size * Weight;
            float total = (text.Length * CharWidth + (text.Length - 1) * Gap) * size;
            Vector2 origin = centre - new Vector2(total / 2, size / 2);

            for (int i = 0; i < text.Length; i++)
            {
                if (!_strokes.TryGetValue(text[i], out Vector2[][] lines))
                {
                    continue;
                }

                Vector2 o = origin + new Vector2(i * (CharWidth + Gap) * size, 0);

                foreach (Vector2[] line in lines)
                {
                    for (int k = 0; k + 1 < line.Length; k++)
                    {
                        if (Capsule(p, o + line[k] * size, o + line[k + 1] * size, stroke))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
