using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

string client = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(client, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(client, args[1]));
const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(c => (ushort)c.Value);
Type[] types;
try { types = assembly.GetTypes(); }
catch (ReflectionTypeLoadException error) { types = error.Types.OfType<Type>().ToArray(); }
foreach (var type in types.Where(t => args.Length < 3 || t.FullName!.Contains(args[2])))
{
    Console.WriteLine(type.FullName);
    foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
    {
        Console.WriteLine("  " + method);
        if (args.Length < 4 || (method.GetMethodBody()?.GetILAsByteArray() is not { } il)) continue;
        for (int i = 0; i < il.Length;)
        {
            int offset = i;
            ushort value = il[i++];
            if (value == 0xfe) value = (ushort)(0xfe00 | il[i++]);
            var op = codes[value];
            int size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + BitConverter.ToInt32(il, i) * 4,
                _ => 4,
            };
            string operand = size == 0 ? "" : Convert.ToHexString(il.AsSpan(i, size));
            try
            {
                if (op.OperandType == OperandType.InlineString) operand = method.Module.ResolveString(BitConverter.ToInt32(il, i));
                else if (op.OperandType is OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineField or OperandType.InlineTok)
                    operand = method.Module.ResolveMember(BitConverter.ToInt32(il, i))?.ToString() ?? operand;
            }
            catch (ArgumentException) { }
            Console.WriteLine($"    {offset:x4} {op.Name} {operand}");
            i += size;
        }
    }
}
