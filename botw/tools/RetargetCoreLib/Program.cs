// Rewrites "System.Private.CoreLib" assembly references to the public "System.Runtime"
// facade so assemblies extracted from a trimmed single-file publish can be compiled against.
// At runtime System.Runtime type-forwards to System.Private.CoreLib, so behavior is unchanged.
using Mono.Cecil;
using System;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: RetargetCoreLib <in.dll> <out.dll>");
    return 1;
}

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(args[0])));
using var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
int changed = 0;
foreach (var r in asm.MainModule.AssemblyReferences)
{
    if (r.Name == "System.Private.CoreLib")
    {
        r.Name = "System.Runtime";
        r.PublicKeyToken = Convert.FromHexString("b03f5f7f11d50a3a");
        changed++;
    }
}
asm.Write(args[1]);
Console.WriteLine($"{args[0]}: retargeted {changed} reference(s)");
return 0;
