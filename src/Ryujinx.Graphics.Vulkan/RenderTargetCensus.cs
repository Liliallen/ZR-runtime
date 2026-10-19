using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// BOTW-PC diagnostic: records every render-target set (formats, sizes, storage identity) with its draw
    /// and dispatch counts for a few presented frames, to locate a game's scene colour/depth and UI passes.
    /// Enabled with BOTWPC_RT_CENSUS=&lt;first frame&gt;:&lt;frame count&gt;:&lt;csv path&gt;. Render thread only.
    /// </summary>
    static class RenderTargetCensus
    {
        private static readonly int _firstFrame;
        private static readonly int _lastFrame;
        private static readonly StreamWriter _out;

        private static int _frame;
        private static int _pass;
        private static int _draws;
        private static int _dispatches;
        private static string _targets = "";

        public static bool Enabled { get; }

        // Per-draw detail for short passes (post-processing): program, vertex count, sampled textures.
        private static readonly StreamWriter _drawsOut;
        private static readonly string _spirvDir;
        private static readonly List<string> _passDraws = [];
        private static readonly Dictionary<int, string> _textures = [];
        private static readonly ConditionalWeakTable<ShaderCollection, string> _programIds = new();
        private static ShaderCollection _program;
        private const int DetailMaxDraws = 16;

        static RenderTargetCensus()
        {
            string[] parts = Environment.GetEnvironmentVariable("BOTWPC_RT_CENSUS")?.Split(':', 3);

            if (parts is { Length: 3 } && int.TryParse(parts[0], out _firstFrame) && int.TryParse(parts[1], out int count))
            {
                _lastFrame = _firstFrame + count - 1;
                _out = new StreamWriter(parts[2], false);
                _out.WriteLine("frame,pass,draws,dispatches,targets");
                _drawsOut = new StreamWriter(parts[2] + ".draws.csv", false);
                _drawsOut.WriteLine("frame,pass,draw,count,program,targets,textures");
                // SPIR-V of the game's shaders is derived from game code: dump only to a local, uncommitted directory.
                _spirvDir = Environment.GetEnvironmentVariable("BOTWPC_RT_CENSUS_SPIRV");
                Enabled = true;
            }
        }

        private static bool Active => _frame >= _firstFrame && _frame <= _lastFrame;

        public static void OnSetRenderTargets(ITexture[] colors, ITexture depthStencil)
        {
            if (!Active)
            {
                return;
            }

            EndPass();

            string targets = "";

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] is TextureView view)
                {
                    targets += $"c{i}={Describe(view)} ";
                }
            }

            if (depthStencil is TextureView depth)
            {
                targets += $"d={Describe(depth)}";
            }

            _targets = targets.Trim();
        }

        public static void OnDraw(int count = -1)
        {
            if (Active)
            {
                _draws++;

                if (_draws <= DetailMaxDraws + 1)
                {
                    string program = _program != null && _programIds.TryGetValue(_program, out string id) ? id : "?";
                    HashSet<int> used = SampledBindings(_program);
                    string textures = string.Join(' ', _textures.Where(t => used.Contains(t.Key)).OrderBy(t => t.Key).Select(t => $"t{t.Key}={t.Value}"));
                    _passDraws.Add(string.Create(CultureInfo.InvariantCulture, $"{_frame},{_pass},{_draws - 1},{count},{program},{_targets},{textures}"));
                }
            }
        }

        public static void OnSetProgram(ShaderCollection program) => _program = program;

        public static void OnSetTexture(int binding, ITexture texture)
        {
            if (Active)
            {
                _textures[binding] = texture is TextureView view ? Describe(view) : texture?.GetType().Name ?? "null";
            }
        }

        /// <summary>Names a program by a hash of its SPIR-V and optionally saves the modules (BOTWPC_RT_CENSUS_SPIRV=dir).</summary>
        public static string ProgramId(ShaderSource[] sources)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

            foreach (ShaderSource source in sources)
            {
                hash.AppendData(source.BinaryCode ?? System.Text.Encoding.UTF8.GetBytes(source.Code ?? ""));
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
        }

        public static void RegisterProgram(ShaderCollection program, ShaderSource[] sources)
        {
            string id = ProgramId(sources);
            _programIds.AddOrUpdate(program, id);

            if (_spirvDir != null)
            {
                Directory.CreateDirectory(_spirvDir);

                foreach (ShaderSource source in sources)
                {
                    string path = Path.Combine(_spirvDir, $"{id}.{source.Stage}.spv");

                    if (source.BinaryCode != null && !File.Exists(path))
                    {
                        File.WriteAllBytes(path, source.BinaryCode);
                    }
                }
            }
        }

        private static HashSet<int> SampledBindings(ShaderCollection program)
        {
            HashSet<int> result = [];

            if (program != null)
            {
                foreach (ResourceBindingSegment[] set in program.BindingSegments)
                {
                    foreach (ResourceBindingSegment segment in set)
                    {
                        if (segment.Type is ResourceType.TextureAndSampler or ResourceType.Texture)
                        {
                            for (int i = 0; i < segment.Count; i++)
                            {
                                result.Add(segment.Binding + i);
                            }
                        }
                    }
                }
            }

            return result;
        }

        public static void OnDispatch()
        {
            if (Active)
            {
                _dispatches++;
            }
        }

        public static void OnPresent()
        {
            if (Active)
            {
                EndPass();
                _out.WriteLine($"{_frame},present,0,0,");
                _out.Flush();
                _drawsOut.Flush();
            }

            _frame++;
            _pass = 0;
        }

        private static void EndPass()
        {
            if (_draws > 0 || _dispatches > 0)
            {
                _out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{_frame},{_pass},{_draws},{_dispatches},{_targets}"));
            }

            // Short passes in full; long passes (scene geometry) only their first draws, to identify their programs.
            foreach (string line in _draws <= DetailMaxDraws ? _passDraws : _passDraws.Take(3))
            {
                _drawsOut.WriteLine(line);
            }

            _passDraws.Clear();

            _pass++;
            _draws = 0;
            _dispatches = 0;
        }

        private static string Describe(TextureView view)
        {
            // Storage identity shows when two passes write the same image (e.g. scene colour reused by post FX).
            return $"{view.Info.Width}x{view.Info.Height}:{view.Info.Format}#{RuntimeHelpers.GetHashCode(view.Storage) & 0xFFFF:x4}";
        }
    }
}
