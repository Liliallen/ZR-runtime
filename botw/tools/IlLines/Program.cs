// Source-line profile from GuestSampler --managed output.
//
// usage: IlLines <samples.csv> <thread> <assembly dir> [--top 12] [--methods 25] [--src <repo root>]
//
// GuestSampler --managed writes samples.csv (thread, tid, rip, samples) and samples.csv.methods (rip, il, method):
// the JIT'd method and IL offset of every sampled instruction. This tool sums the samples of one thread per method and
// IL offset, maps the offsets to source lines through the assemblies' portable PDBs (System.Reflection.Metadata), and
// prints the hottest lines of the hottest methods, with the source text. IL offsets of inlined code are attributed to
// the caller's call site line (that is what the runtime reports), so a line can stand for a whole inlined callee.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

static class Program
{
    sealed class MethodInfo
    {
        public string File;
        public List<(int Il, int Line)> Points = [];
    }

    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: IlLines <samples.csv> <thread> <assembly dir> [--top N] [--methods N] [--src <root>]");
            return 1;
        }

        string samplesPath = args[0], thread = args[1], asmDir = args[2];
        int top = Opt(args, "--top", 12), methodsShown = Opt(args, "--methods", 25);
        string srcRoot = args.SkipWhile(a => a != "--src").Skip(1).FirstOrDefault();

        Dictionary<string, (string Method, int Il)> rips = [];

        foreach (string line in File.ReadLines(samplesPath + ".methods").Skip(1))
        {
            string[] c = line.Split(',', 3);
            if (c.Length == 3 && int.TryParse(c[1], out int il))
            {
                rips[c[0]] = (c[2], il);
            }
        }

        Dictionary<string, Dictionary<int, long>> byMethod = [];
        long total = 0;

        foreach (string line in File.ReadLines(samplesPath).Skip(1))
        {
            string[] c = line.Split(',');
            if (c.Length < 4 || c[0] != thread)
            {
                continue;
            }

            long n = long.Parse(c[3]);
            total += n;

            if (!rips.TryGetValue(c[2], out var m))
            {
                continue;
            }

            if (!byMethod.TryGetValue(m.Method, out var offsets))
            {
                byMethod[m.Method] = offsets = [];
            }

            offsets[m.Il] = offsets.GetValueOrDefault(m.Il) + n;
        }

        Console.WriteLine($"{thread}: {total} samples");
        Dictionary<string, MethodInfo> infos = LoadMethods(asmDir);
        Dictionary<string, string[]> sources = [];

        foreach (var (method, offsets) in byMethod.OrderByDescending(kv => kv.Value.Values.Sum()).Take(methodsShown))
        {
            long sum = offsets.Values.Sum();
            Console.WriteLine();
            Console.WriteLine($"== {100.0 * sum / total:F2} %  {method}");

            if (!infos.TryGetValue(method, out MethodInfo info) || info.Points.Count == 0)
            {
                Console.WriteLine("   (no sequence points)");
                continue;
            }

            Dictionary<int, long> byLine = [];

            foreach (var (il, n) in offsets)
            {
                // The sequence point that starts at or before the offset.
                int lineNo = info.Points.Where(p => p.Il <= il).Select(p => p.Line).DefaultIfEmpty(info.Points[0].Line).Last();
                byLine[lineNo] = byLine.GetValueOrDefault(lineNo) + n;
            }

            string[] text = null;

            if (srcRoot != null && info.File != null)
            {
                string path = info.File;

                if (!File.Exists(path))
                {
                    int i = path.Replace('\\', '/').IndexOf("/src/", StringComparison.Ordinal);
                    path = i >= 0 ? Path.Combine(srcRoot, path[(i + 1)..]) : path;
                }

                if (!sources.TryGetValue(path, out text) && File.Exists(path))
                {
                    sources[path] = text = File.ReadAllLines(path);
                }
            }

            foreach (var (lineNo, n) in byLine.OrderByDescending(kv => kv.Value).Take(top))
            {
                string src = text != null && lineNo - 1 < text.Length && lineNo > 0 ? text[lineNo - 1].Trim() : "";
                Console.WriteLine($"   {100.0 * n / total:F2}  L{lineNo,-5} {src}");
            }
        }

        return 0;
    }

    static int Opt(string[] args, string name, int def)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : def;
    }

    // Method display names as GuestSampler prints them: Namespace.Type[+Nested].Method (overloads merged).
    static Dictionary<string, MethodInfo> LoadMethods(string dir)
    {
        Dictionary<string, MethodInfo> result = [];

        foreach (string dll in Directory.GetFiles(dir, "Ryujinx*.dll"))
        {
            string pdb = Path.ChangeExtension(dll, ".pdb");
            if (!File.Exists(pdb))
            {
                continue;
            }

            try
            {
                using FileStream peStream = File.OpenRead(dll);
                using PEReader pe = new(peStream);
                MetadataReader md = pe.GetMetadataReader();
                using MetadataReaderProvider pdbProvider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdb));
                MetadataReader pdbReader = pdbProvider.GetMetadataReader();

                foreach (MethodDefinitionHandle h in md.MethodDefinitions)
                {
                    MethodDefinition def = md.GetMethodDefinition(h);
                    string name = TypeName(md, def.GetDeclaringType()) + "." + md.GetString(def.Name);
                    MethodDebugInformation dbg = pdbReader.GetMethodDebugInformation(h);

                    if (!result.TryGetValue(name, out MethodInfo info))
                    {
                        result[name] = info = new MethodInfo();
                    }

                    foreach (SequencePoint sp in dbg.GetSequencePoints())
                    {
                        if (!sp.IsHidden && info.Points.Count == 0)
                        {
                            info.File = pdbReader.GetString(pdbReader.GetDocument(sp.Document).Name);
                        }

                        if (!sp.IsHidden)
                        {
                            info.Points.Add((sp.Offset, sp.StartLine));
                        }
                    }

                    info.Points.Sort();
                }
            }
            catch (BadImageFormatException)
            {
            }
        }

        return result;
    }

    static string TypeName(MetadataReader md, TypeDefinitionHandle h)
    {
        TypeDefinition t = md.GetTypeDefinition(h);
        string name = md.GetString(t.Name);
        int tick = name.IndexOf('`');
        if (tick >= 0)
        {
            name = name[..tick];
        }

        TypeDefinitionHandle outer = t.GetDeclaringType();

        if (!outer.IsNil)
        {
            return TypeName(md, outer) + "+" + name;
        }

        string ns = md.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }
}
