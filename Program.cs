using System.Text;
using ObjectIR.AST;
    
if (args.Length < 1)
{
	Console.WriteLine("Usage: FobGenerator <input-textir> [output-fob]");
	return;
}

var inputPath = args[0];
var outputPath = args.Length > 1
	? args[1]
	: Path.ChangeExtension(inputPath, ".fobir");

var inputText = File.ReadAllText(inputPath, Encoding.UTF8);
var module = TextIrParser.ParseModule(inputText);

var compiler = new FobIrCompiler();
var bytes = compiler.Compile(module);
File.WriteAllBytes(outputPath, bytes);

Console.WriteLine($"Wrote FOB/IR binary: {outputPath}");

internal sealed class FobIrCompiler
{
	private const string Magic = "FOB/IR";
	private const ushort Version = 1;

	private const uint ExportKindMethod = 1;
	private const uint ExportKindConstructor = 2;

	public byte[] Compile(ModuleNode module)
	{
		var functions = CollectFunctions(module);
		var includes = CollectIncludes(module);
		var exportNames = functions.Select(f => f.ExportName).ToList();

		var stringTable = BuildStringTable(exportNames.Concat(includes));
		var dataBytes = stringTable.Bytes;

		var codeSection = BuildCodeSection(functions);

		var headerSize = Magic.Length + sizeof(ushort) + sizeof(uint) * 4;
		var exportsSize = sizeof(uint) + (uint)(functions.Count * 12);
		var includesSize = sizeof(uint) + (uint)(includes.Count * 4);
		var dataSize = sizeof(uint) + (uint)dataBytes.Length;
		var codeSize = (uint)codeSection.Bytes.Length;

		var exportsOffset = (uint)headerSize;
		var includesOffset = exportsOffset + exportsSize;
		var dataOffset = includesOffset + includesSize;
		var codeOffset = dataOffset + dataSize;

		using var stream = new MemoryStream((int)(codeOffset + codeSize));
		using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

		WriteHeader(writer, exportsOffset, includesOffset, dataOffset, codeOffset);
		WriteExports(writer, functions, stringTable, codeSection);
		WriteIncludes(writer, includes, stringTable);
		WriteData(writer, dataBytes);
		WriteCode(writer, codeSection);

		writer.Flush();
		return stream.ToArray();
	}

	private static void WriteHeader(BinaryWriter writer, uint exportsOffset, uint includesOffset, uint dataOffset, uint codeOffset)
	{
		writer.Write(Encoding.ASCII.GetBytes(Magic));
		writer.Write(Version);
		writer.Write(exportsOffset);
		writer.Write(includesOffset);
		writer.Write(dataOffset);
		writer.Write(codeOffset);
	}

	private static void WriteExports(BinaryWriter writer, List<FunctionInfo> functions, StringTable stringTable, CodeSection codeSection)
	{
		writer.Write((uint)functions.Count);
		foreach (var fn in functions)
		{
			writer.Write(stringTable.GetOffset(fn.ExportName));
			writer.Write(fn.Kind);
			writer.Write(codeSection.GetFunctionOffset(fn));
		}
	}

	private static void WriteIncludes(BinaryWriter writer, List<string> includes, StringTable stringTable)
	{
		writer.Write((uint)includes.Count);
		foreach (var include in includes)
		{
			writer.Write(stringTable.GetOffset(include));
		}
	}

	private static void WriteData(BinaryWriter writer, byte[] dataBytes)
	{
		writer.Write((uint)dataBytes.Length);
		writer.Write(dataBytes);
	}

	private static void WriteCode(BinaryWriter writer, CodeSection codeSection)
	{
		writer.Write(codeSection.FunctionCount);
		writer.Write(codeSection.Bytes);
	}

	private List<FunctionInfo> CollectFunctions(ModuleNode module)
	{
		var list = new List<FunctionInfo>();
		foreach (var cls in module.Classes)
		{
			foreach (var ctor in cls.Constructors)
			{
				var exportName = $"{cls.Name}.constructor";
				var bytecode = SerializeFunction(cls.Name, "constructor", ctor.Parameters, new TypeRef("void"), isStatic: false, ctor.Body);
				list.Add(new FunctionInfo(exportName, ExportKindConstructor, bytecode));
			}

			foreach (var method in cls.Methods)
			{
				var exportName = $"{cls.Name}.{method.Name}";
				var bytecode = SerializeFunction(cls.Name, method.Name, method.Parameters, method.ReturnType, method.IsStatic, method.Body);
				list.Add(new FunctionInfo(exportName, ExportKindMethod, bytecode));
			}
		}

		return list;
	}

	private List<string> CollectIncludes(ModuleNode module)
	{
		var definedTypes = new HashSet<string>(module.Classes.Select(c => c.Name).Concat(module.Interfaces.Select(i => i.Name)));
		var includes = new HashSet<string>(StringComparer.Ordinal);

		foreach (var cls in module.Classes)
		{
			foreach (var ctor in cls.Constructors)
			{
				CollectIncludesFromBody(ctor.Body, definedTypes, includes);
			}

			foreach (var method in cls.Methods)
			{
				CollectIncludesFromBody(method.Body, definedTypes, includes);
			}
		}

		return includes.OrderBy(x => x, StringComparer.Ordinal).ToList();
	}

	private static void CollectIncludesFromBody(BlockStatement body, HashSet<string> definedTypes, HashSet<string> includes)
	{
		foreach (var statement in body.Statements)
		{
			switch (statement)
			{
				case InstructionStatement inst:
					CollectIncludesFromInstruction(inst.Instruction, definedTypes, includes);
					break;
				case IfStatement ifStmt:
					CollectIncludesFromBody(ifStmt.Then, definedTypes, includes);
					if (ifStmt.Else != null)
					{
						CollectIncludesFromBody(ifStmt.Else, definedTypes, includes);
					}
					break;
				case WhileStatement whileStmt:
					CollectIncludesFromBody(whileStmt.Body, definedTypes, includes);
					break;
			}
		}
	}

	private static void CollectIncludesFromInstruction(Instruction instruction, HashSet<string> definedTypes, HashSet<string> includes)
	{
		switch (instruction)
		{
			case CallInstruction call:
				AddInclude(call.Target.DeclaringType.Name, definedTypes, includes);
				AddInclude(call.ReturnType.Name, definedTypes, includes);
				foreach (var arg in call.Arguments)
				{
					AddInclude(arg.Name, definedTypes, includes);
				}
				break;
			case NewObjInstruction newObj:
				AddInclude(newObj.Type.Name, definedTypes, includes);
				if (newObj.Constructor != null)
				{
					AddInclude(newObj.Constructor.DeclaringType.Name, definedTypes, includes);
				}
				foreach (var arg in newObj.Arguments)
				{
					AddInclude(arg.Name, definedTypes, includes);
				}
				break;
		}
	}

	private static void AddInclude(string typeName, HashSet<string> definedTypes, HashSet<string> includes)
	{
		if (string.IsNullOrWhiteSpace(typeName))
		{
			return;
		}

		if (!definedTypes.Contains(typeName))
		{
			includes.Add(typeName);
		}
	}

	private static byte[] SerializeFunction(
		string className,
		string methodName,
		IReadOnlyList<ParameterNode> parameters,
		TypeRef returnType,
		bool isStatic,
		BlockStatement body)
	{
		var sb = new StringBuilder();
		if (isStatic)
		{
			sb.Append("static method ");
		}
		else
		{
			sb.Append("method ");
		}

		sb.Append(className).Append('.').Append(methodName);
		sb.Append('(');
		sb.Append(string.Join(", ", parameters.Select(p => $"{p.Name}: {p.ParameterType.Name}")));
		sb.Append(") -> ").Append(returnType.Name);
		sb.AppendLine();
		sb.AppendLine("{");
		SerializeBlock(sb, body, 1);
		sb.AppendLine("}");

		return Encoding.UTF8.GetBytes(sb.ToString());
	}

	private static void SerializeBlock(StringBuilder sb, BlockStatement block, int indent)
	{
		var pad = new string('\t', indent);
		foreach (var statement in block.Statements)
		{
			switch (statement)
			{
				case LocalDeclarationStatement local:
					sb.Append(pad).Append("local ").Append(local.Name).Append(": ").Append(local.LocalType.Name).AppendLine();
					break;
				case InstructionStatement inst:
					sb.Append(pad).Append(SerializeInstruction(inst.Instruction)).AppendLine();
					break;
				case IfStatement ifStmt:
					sb.Append(pad).Append("if (").Append(ifStmt.Condition).AppendLine(")");
					sb.Append(pad).AppendLine("{");
					SerializeBlock(sb, ifStmt.Then, indent + 1);
					sb.Append(pad).AppendLine("}");
					if (ifStmt.Else != null)
					{
						sb.Append(pad).AppendLine("else");
						sb.Append(pad).AppendLine("{");
						SerializeBlock(sb, ifStmt.Else, indent + 1);
						sb.Append(pad).AppendLine("}");
					}
					break;
				case WhileStatement whileStmt:
					sb.Append(pad).Append("while (").Append(whileStmt.Condition).AppendLine(")");
					sb.Append(pad).AppendLine("{");
					SerializeBlock(sb, whileStmt.Body, indent + 1);
					sb.Append(pad).AppendLine("}");
					break;
			}
		}
	}

	private static string SerializeInstruction(Instruction instruction)
	{
		return instruction switch
		{
			SimpleInstruction simple => simple.Operand is null
				? simple.OpCode
				: $"{simple.OpCode} {simple.Operand}",
			CallInstruction call =>
				$"{(call.IsVirtual ? "callvirt" : "call")} {SerializeCallTarget(call.Target, call.Arguments)} -> {call.ReturnType.Name}",
			NewObjInstruction newObj =>
				newObj.Constructor == null
					? $"newobj {newObj.Type.Name}"
					: $"newobj {newObj.Type.Name}.constructor({string.Join(", ", newObj.Arguments.Select(a => a.Name))})",
			_ => throw new InvalidOperationException($"Unsupported instruction: {instruction.GetType().Name}")
		};
	}

	private static string SerializeCallTarget(MethodRef method, IReadOnlyList<TypeRef> args)
	{
		return $"{method.DeclaringType.Name}.{method.MethodName}({string.Join(", ", args.Select(a => a.Name))})";
	}

	private sealed record FunctionInfo(string ExportName, uint Kind, byte[] Bytecode);

	private sealed class StringTable
	{
		private readonly Dictionary<string, uint> _offsets;
		public byte[] Bytes { get; }

		public StringTable(Dictionary<string, uint> offsets, byte[] bytes)
		{
			_offsets = offsets;
			Bytes = bytes;
		}

		public uint GetOffset(string value) => _offsets[value];
	}

	private static StringTable BuildStringTable(IEnumerable<string> strings)
	{
		var unique = new HashSet<string>(strings.Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.Ordinal);
		var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
		using var stream = new MemoryStream();

		foreach (var value in unique.OrderBy(s => s, StringComparer.Ordinal))
		{
			var offset = (uint)stream.Length;
			var bytes = Encoding.UTF8.GetBytes(value);
			stream.Write(bytes, 0, bytes.Length);
			stream.WriteByte(0);
			offsets[value] = offset;
		}

		return new StringTable(offsets, stream.ToArray());
	}

	private sealed class CodeSection
	{
		private readonly Dictionary<FunctionInfo, uint> _offsets;
		public uint FunctionCount { get; }
		public byte[] Bytes { get; }

		public CodeSection(Dictionary<FunctionInfo, uint> offsets, byte[] bytes)
		{
			_offsets = offsets;
			FunctionCount = (uint)offsets.Count;
			Bytes = bytes;
		}

		public uint GetFunctionOffset(FunctionInfo function) => _offsets[function];
	}

	private static CodeSection BuildCodeSection(List<FunctionInfo> functions)
	{
		var offsets = new Dictionary<FunctionInfo, uint>();
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

		var currentOffset = 0u;
		foreach (var fn in functions)
		{
			offsets[fn] = currentOffset;
			writer.Write((uint)fn.Bytecode.Length);
			writer.Write(fn.Bytecode);
			currentOffset += (uint)(sizeof(uint) + fn.Bytecode.Length);
		}

		writer.Flush();
		return new CodeSection(offsets, stream.ToArray());
	}
}
