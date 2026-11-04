/*
    Copyright (C) 2026 dnSpy contributors

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using dnSpy.Decompiler.ILSpy.Core.Output;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.TypeSystem;
using UpstreamMethod = ICSharpCode.Decompiler.TypeSystem.IMethod;
using UpstreamSequencePoint = ICSharpCode.Decompiler.DebugInfo.SequencePoint;

namespace dnSpy.Decompiler.ILSpy.Core.DebugInfo {
	/// <summary>
	/// Converts the upstream decompiler's debug info (sequence points, variables, async info) to dnSpy's
	/// <see cref="MethodDebugInfo"/>, which the debugger uses for breakpoints, stepping and locals.
	/// </summary>
	static class DebugInfoFactory {
		/// <param name="output">Converts the text locations stored in the syntax tree to offsets in the output</param>
		/// <param name="variables">Creates the locals, shared with the text output so both use the same objects</param>
		/// <param name="repairs">Gets a description of each upstream sequence point that had to be dropped or fixed</param>
		public static List<MethodDebugInfo> Create(SyntaxTree syntaxTree, IDictionary<ILFunction, List<UpstreamSequencePoint>> sequencePoints,
			ILocationMapper output, ModuleBridgeCache cache, SourceVariableTable variables, int decompilerSettingsVersion, List<string> repairs) {
			var spans = GetSpans(syntaxTree, output);
			var kinds = GetStatementKinds(syntaxTree, output);
			StatementKind getKind(TextSpan span) => kinds.TryGetValue(span, out var kind) ? kind : StatementKind.Other;
			// The expression evaluator resolves type names with them
			var imports = syntaxTree.Descendants.OfType<UsingDeclaration>().Select(a => a.Namespace).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToArray();
			// A method decompiled on its own only has the usings its code needs: also import what the type's code uses
			var typeImports = new Dictionary<TypeDef, string[]>();
			string[] getImports(ILFunction function) {
				if (function.Method is null || cache.ToDnlib(function.Method) is not MethodDef method || method.DeclaringType is not TypeDef type)
					return imports;
				while (type.DeclaringType is not null)
					type = type.DeclaringType;
				if (!typeImports.TryGetValue(type, out var result)) {
					var extra = TypeNamespaces.Get(type);
					extra.ExceptWith(imports);
					result = extra.Count == 0 ? imports : imports.Concat(extra.OrderBy(a => a, StringComparer.Ordinal)).ToArray();
					typeImports.Add(type, result);
				}
				return result;
			}
			var finallyBlocks = GetFinallyBlocks(syntaxTree, output);
			var result = new List<MethodDebugInfo>(sequencePoints.Count);
			foreach (var kv in sequencePoints) {
				var info = Create(kv.Key, kv.Value, output, cache, variables, spans, getKind, getImports(kv.Key), decompilerSettingsVersion, repairs, finallyBlocks, result);
				if (info is not null)
					result.Add(info);
			}
			AddAutoAccessors(syntaxTree, output, cache, decompilerSettingsVersion, result);
			AddPositionalRecordMembers(syntaxTree, output, cache, decompilerSettingsVersion, result);
			AddAsyncEntryPoint(syntaxTree, output, cache, decompilerSettingsVersion, result);
			return result;
		}

		/// <summary>
		/// The compiler's entry point for an async Main (<c>static void &lt;Main&gt;(string[] args) { Main(args).GetAwaiter().GetResult(); }</c>)
		/// isn't in the decompiled code: upstream hides it like the code it wraps, and the compiler's PDB has no sequence
		/// points for it either. But the debugger stops there (break at entry point, the call stack) and Go to Entry
		/// Point goes there, so map its body to the declaration of the Main it calls, from the modifiers to the
		/// parameter list. If upstream shows it, it has its own debug info and this does nothing.
		/// </summary>
		static void AddAsyncEntryPoint(SyntaxTree syntaxTree, ILocationMapper output, ModuleBridgeCache cache, int decompilerSettingsVersion, List<MethodDebugInfo> result) {
			HashSet<MethodDef>? methods = null;
			foreach (var decl in syntaxTree.Descendants.OfType<MethodDeclaration>()) {
				if (decl.GetSymbol() is not UpstreamMethod upstreamMethod || cache.ToDnlib(upstreamMethod) is not MethodDef main)
					continue;
				// (Upstream: AsyncAwaitDecompiler.IsCompilerGeneratedMainMethod)
				var entryPoint = main.Module?.EntryPoint;
				if (entryPoint is null || entryPoint == main || entryPoint.Name != "<Main>" || entryPoint.Body is not CilBody body || body.Instructions.Count == 0)
					continue;
				if (!body.Instructions.Any(a => a.OpCode.Code == Code.Call && ReferenceEquals(a.Operand, main)))
					continue;
				methods ??= new HashSet<MethodDef>(result.Select(a => a.Method));
				if (methods.Contains(entryPoint))
					continue;
				// The syntax tree has no modifier and parenthesis tokens: the modifiers start the return type's line, at
				// the declaration's indentation (its attributes are on the lines above), and ')' follows the last
				// parameter or the name
				var returnType = decl.ReturnType.StartLocation;
				var declStart = decl.StartLocation;
				if (returnType.IsEmpty || declStart.IsEmpty || decl.NameToken.EndLocation.IsEmpty)
					continue;
				int start = output.ToOffset(returnType.Line, Math.Min(declStart.Column, returnType.Column));
				var lastParameter = decl.Parameters.LastOrDefault();
				int end = lastParameter is not null && !lastParameter.EndLocation.IsEmpty ?
					output.ToOffset(lastParameter.EndLocation.Line, lastParameter.EndLocation.Column) + 1 :
					output.ToOffset(decl.NameToken.EndLocation.Line, decl.NameToken.EndLocation.Column) + 2;
				if (end <= start)
					continue;
				methods.Add(entryPoint);
				var codeSize = (uint)body.GetCodeSize();
				var builder = new MethodDebugInfoBuilder(decompilerSettingsVersion, StateMachineKind.None, entryPoint, null) {
					CompilerName = null,
					StartPosition = start,
					EndPosition = end,
					// (Locals shows 'args')
					Parameters = entryPoint.Parameters.Where(p => !p.IsHiddenThisParameter && !string.IsNullOrEmpty(p.Name))
						.Select(p => new SourceParameter(p, p.Name, p.Type, SourceVariableFlags.None)).ToArray(),
				};
				builder.Add(new SourceStatement(ILSpan.FromBounds(0, codeSize), TextSpan.FromBounds(start, end)));
				builder.Scope.Span = ILSpan.FromBounds(0, codeSize);
				result.Add(builder.Create());
			}
		}

		/// <summary>
		/// The members the compiler generates for a positional record's parameters ('record R(int A)': the properties'
		/// accessors and the constructors) have no code in the decompiled text either: like the compiler, map their
		/// whole body to the parameter (accessors) or to the declaration (constructors).
		/// </summary>
		static void AddPositionalRecordMembers(SyntaxTree syntaxTree, ILocationMapper output, ModuleBridgeCache cache, int decompilerSettingsVersion, List<MethodDebugInfo> result) {
			var methods = new HashSet<MethodDef>(result.Select(a => a.Method));
			foreach (var decl in syntaxTree.Descendants.OfType<TypeDeclaration>()) {
				if (decl.ClassType is not (ClassType.RecordClass or ClassType.RecordStruct) || decl.PrimaryConstructorParameters.Count == 0)
					continue;
				if (decl.GetSymbol() is not ITypeDefinition typeDefinition || cache.ToDnlib(typeDefinition) is not TypeDef type)
					continue;
				var names = new HashSet<string>();
				foreach (var parameter in decl.PrimaryConstructorParameters) {
					if (parameter.Name is not string name)
						continue;
					names.Add(name);
					var property = type.Properties.FirstOrDefault(p => p.Name == name);
					if (property is null)
						continue;
					foreach (var accessor in property.GetMethods.Concat(property.SetMethods))
						Add(accessor, parameter.StartLocation, parameter.EndLocation);
				}
				// The primary constructor and the copy constructor: the declaration up to its parameter list's end
				var last = decl.PrimaryConstructorParameters.LastOrDefault();
				foreach (var ctor in type.FindInstanceConstructors()) {
					var parameterNames = ctor.Parameters.Where(p => !p.IsHiddenThisParameter).Select(p => p.Name).ToList();
					bool primary = parameterNames.Count == names.Count && parameterNames.All(names.Contains);
					bool copy = parameterNames.Count == 1 && ctor.Parameters.Any(p => !p.IsHiddenThisParameter && p.Type.TryGetTypeDef() == type);
					if ((primary || copy) && last is not null)
						Add(ctor, decl.StartLocation, last.EndLocation);
				}
			}

			void Add(MethodDef method, ICSharpCode.Decompiler.CSharp.Syntax.TextLocation startLocation, ICSharpCode.Decompiler.CSharp.Syntax.TextLocation endLocation) {
				if (method.Body is not CilBody body || body.Instructions.Count == 0 || startLocation.IsEmpty || endLocation.IsEmpty || !methods.Add(method))
					return;
				int start = output.ToOffset(startLocation.Line, startLocation.Column);
				int end = output.ToOffset(endLocation.Line, endLocation.Column);
				if (end <= start)
					return;
				var codeSize = (uint)body.GetCodeSize();
				var builder = new MethodDebugInfoBuilder(decompilerSettingsVersion, StateMachineKind.None, method, null) {
					CompilerName = null,
					StartPosition = start,
					EndPosition = end,
				};
				builder.Add(new SourceStatement(ILSpan.FromBounds(0, codeSize), TextSpan.FromBounds(start, end)));
				builder.Scope.Span = ILSpan.FromBounds(0, codeSize);
				result.Add(builder.Create());
			}
		}

		/// <summary>
		/// Text spans of the finally blocks (the block, braces included)
		/// </summary>
		static List<(int start, int end)> GetFinallyBlocks(SyntaxTree syntaxTree, ILocationMapper output) {
			var list = new List<(int start, int end)>();
			foreach (var tryCatch in syntaxTree.Descendants.OfType<TryCatchStatement>()) {
				var block = tryCatch.FinallyBlock;
				if (block is null || block.StartLocation.IsEmpty || block.EndLocation.IsEmpty)
					continue;
				int start = output.ToOffset(block.StartLocation.Line, block.StartLocation.Column);
				int end = output.ToOffset(block.EndLocation.Line, block.EndLocation.Column);
				if (end > start)
					list.Add((start, end));
			}
			return list;
		}

		/// <summary>
		/// Upstream has no sequence points for auto property accessors (get; set; init;), the compiler maps their
		/// whole body to the accessor: a breakpoint on 'get;' stops in the getter.
		/// </summary>
		static void AddAutoAccessors(SyntaxTree syntaxTree, ILocationMapper output, ModuleBridgeCache cache, int decompilerSettingsVersion, List<MethodDebugInfo> result) {
			var methods = new HashSet<MethodDef>(result.Select(a => a.Method));
			foreach (var accessor in syntaxTree.Descendants.OfType<Accessor>()) {
				if (accessor.Body is not null)
					continue;
				if (accessor.StartLocation.IsEmpty || accessor.EndLocation.IsEmpty || accessor.GetSymbol() is not UpstreamMethod upstreamMethod)
					continue;
				if (cache.ToDnlib(upstreamMethod) is not MethodDef method || method.Body is not CilBody body || body.Instructions.Count == 0 || !methods.Add(method))
					continue;
				int start = output.ToOffset(accessor.StartLocation.Line, accessor.StartLocation.Column);
				int end = output.ToOffset(accessor.EndLocation.Line, accessor.EndLocation.Column);
				if (end <= start)
					continue;
				var codeSize = (uint)body.GetCodeSize();
				var builder = new MethodDebugInfoBuilder(decompilerSettingsVersion, StateMachineKind.None, method, null) {
					CompilerName = null,
					StartPosition = start,
					EndPosition = end,
				};
				builder.Add(new SourceStatement(ILSpan.FromBounds(0, codeSize), TextSpan.FromBounds(start, end)));
				builder.Scope.Span = ILSpan.FromBounds(0, codeSize);
				result.Add(builder.Create());
			}
		}

		static MethodDebugInfo? Create(ILFunction function, List<UpstreamSequencePoint> points, ILocationMapper output,
			ModuleBridgeCache cache, SourceVariableTable variables, Spans spans, Func<TextSpan, StatementKind> getKind, string[] imports, int decompilerSettingsVersion, List<string> repairs,
			List<(int start, int end)> finallyBlocks, List<MethodDebugInfo> result) {
			if (function.Method is null)
				return null;
			// IL offsets are offsets in the method whose body was decompiled: MoveNext() for async/iterator methods
			var bodyMethod = cache.ToDnlib(function.MoveNextMethod ?? function.Method) as MethodDef;
			if (bodyMethod is null)
				return null;
			MethodDef? kickoffMethod = null;
			if (function.MoveNextMethod is not null)
				kickoffMethod = cache.ToDnlib(function.Method) as MethodDef;

			var kind = function.IsAsync ? StateMachineKind.AsyncMethod :
				function.IsIterator ? StateMachineKind.IteratorMethod : StateMachineKind.None;
			var builder = new MethodDebugInfoBuilder(decompilerSettingsVersion, kind, bodyMethod, kickoffMethod) {
				CompilerName = null,
			};

			var body = bodyMethod.Body;
			// The C# compiler moves an iterator's finally blocks to methods (<>m__Finally1...) and upstream inlines them,
			// with their IL offsets: those statements are about these methods, not MoveNext()
			var finallyPoints = new List<(MethodDef method, (int start, int end) block, List<UpstreamSequencePoint> points)>();
			if (function.IsIterator && !function.IsAsync && function.MoveNextMethod is not null && spans.ByFunction.TryGetValue(function, out var functionSpan))
				points = SplitIteratorFinallyBlocks(points, bodyMethod, functionSpan, finallyBlocks, output, finallyPoints);
			var statements = SequencePointSanitizer.Sanitize(points, GetInstructionBoundaries(body), output.ToOffset, bodyMethod.FullName, repairs);
			var locals = GetLocals(function, variables).ToList();
			// Upstream keeps variables for some slots it removed from the code (e.g. an awaitable's temporary)
			var visibleLocals = new HashSet<int>(function.Variables.Where(variables.IsUsedInText).Select(variables.GetILLocal).OfType<Local>().Select(a => a.Index));
			spans.ClosingBraces.TryGetValue(function, out var closingBrace);
			var context = new ShapeContext(getKind, visibleLocals, closingBrace, function.MoveNextMethod is not null) {
				VisibleAccesses = GetVisibleAccesses(function, variables),
				ImplicitBaseCallBrace = spans.ImplicitBaseCallBraces.TryGetValue(function, out var openingBrace) ? openingBrace : null,
			};
			foreach (var statement in StatementShaper.Shape(statements, bodyMethod, context))
				builder.Add(statement);

			builder.Scope.Span = ILSpan.FromBounds(0, (uint)(body is null ? function.CodeSize : body.GetCodeSize()));
			builder.Scope.Locals.AddRange(locals);
			foreach (var ns in imports)
				builder.Scope.Imports.Add(ImportInfo.CreateNamespace(ns));
			builder.Parameters = GetParameters(function, kickoffMethod ?? bodyMethod, cache);
			if (function.IsAsync)
				builder.AsyncInfo = GetAsyncInfo(function, bodyMethod, kickoffMethod);

			// A method can be decompiled more than once (e.g. a lambda whose compiler generated names were
			// obfuscated: inline and as a method of its display class), so use the span of this function's node
			// The fallback is only safe for top-level functions: some lambdas (e.g. a group-by key selector turned into
			// a query clause) aren't annotated on any node, and their method may also be declared elsewhere.
			if (spans.ByFunction.TryGetValue(function, out var span) ||
				(function.Kind == ILFunctionKind.TopLevelFunction && spans.ByMethod.TryGetValue(function.Method, out span))) {
				builder.StartPosition = span.start;
				builder.EndPosition = span.end;
			}

			foreach (var (finallyMethod, block, blockPoints) in finallyPoints) {
				// (Part of the iterator's state machine: its parameters are the iterator method's)
				var finallyBuilder = new MethodDebugInfoBuilder(decompilerSettingsVersion, StateMachineKind.IteratorMethod, finallyMethod, kickoffMethod) {
					CompilerName = null,
					StartPosition = block.start,
					EndPosition = block.end,
				};
				var finallyStatements = SequencePointSanitizer.Sanitize(blockPoints, GetInstructionBoundaries(finallyMethod.Body), output.ToOffset, finallyMethod.FullName, repairs);
				var finallyContext = new ShapeContext(getKind, new HashSet<int>(), closingBrace: null, isStateMachine: false);
				foreach (var statement in StatementShaper.Shape(finallyStatements, finallyMethod, finallyContext))
					finallyBuilder.Add(statement);
				finallyBuilder.Scope.Span = ILSpan.FromBounds(0, (uint)finallyMethod.Body.GetCodeSize());
				// The iterator's locals are fields of the state machine ('this' in both methods)
				finallyBuilder.Scope.Locals.AddRange(locals.Where(a => a.HoistedField is not null));
				foreach (var ns in imports)
					finallyBuilder.Scope.Imports.Add(ImportInfo.CreateNamespace(ns));
				finallyBuilder.Parameters = builder.Parameters;
				result.Add(finallyBuilder.Create());
			}
			return builder.Create();
		}

		/// <summary>
		/// Removes the sequence points of the iterator's finally blocks from <paramref name="points"/> and adds them to
		/// <paramref name="finallyPoints"/> with the method that has their code: one whose instructions start and
		/// end where they do (with several candidates, the C# compiler numbers them in the order of the blocks).
		/// </summary>
		static List<UpstreamSequencePoint> SplitIteratorFinallyBlocks(List<UpstreamSequencePoint> points, MethodDef moveNext, (int start, int end) functionSpan,
			List<(int start, int end)> finallyBlocks, ILocationMapper output, List<(MethodDef, (int, int), List<UpstreamSequencePoint>)> finallyPoints) {
			var blocks = finallyBlocks.Where(b => functionSpan.start <= b.start && b.end <= functionSpan.end).OrderBy(b => b.start).ToList();
			if (blocks.Count == 0)
				return points;
			var methods = moveNext.DeclaringType.Methods.Where(m => m != moveNext && m.HasBody && m.Body.Instructions.Count != 0 && m.Name.String.Contains("m__Finally")).ToList();
			if (methods.Count == 0)
				return points;
			// Innermost block that contains a sequence point's text
			var byBlock = new Dictionary<(int start, int end), List<UpstreamSequencePoint>>();
			var rest = new List<UpstreamSequencePoint>(points.Count);
			foreach (var sp in points) {
				(int start, int end)? block = null;
				if (!sp.IsHidden && sp.StartLine > 0) {
					int offset = output.ToOffset(sp.StartLine, sp.StartColumn);
					foreach (var b in blocks) {
						if (b.start <= offset && offset < b.end && (block is null || b.start >= block.Value.start))
							block = b;
					}
				}
				if (block is (int, int) key) {
					if (!byBlock.TryGetValue(key, out var list))
						byBlock.Add(key, list = new List<UpstreamSequencePoint>());
					list.Add(sp);
				}
				else
					rest.Add(sp);
			}
			var used = new HashSet<MethodDef>();
			foreach (var block in blocks) {
				if (!byBlock.TryGetValue(block, out var blockPoints))
					continue;
				var candidates = methods.Where(m => !used.Contains(m) && Fits(m, blockPoints)).ToList();
				if (candidates.Count > 1) {
					// <>m__Finally1, <>m__Finally2... in the order of the blocks
					int index = blocks.IndexOf(block);
					var byNumber = methods.OrderBy(m => FinallyNumber(m.Name)).ToList();
					if (index < byNumber.Count && candidates.Contains(byNumber[index]))
						candidates = new List<MethodDef> { byNumber[index] };
				}
				if (candidates.Count != 1)
					continue;
				used.Add(candidates[0]);
				finallyPoints.Add((candidates[0], block, blockPoints));
			}
			return rest;

			// (The sanitizer fixes ends, upstream's implicit return can end after the code)
			static bool Fits(MethodDef method, List<UpstreamSequencePoint> blockPoints) {
				var boundaries = GetInstructionBoundaries(method.Body)!;
				uint codeSize = boundaries.Max();
				return blockPoints.All(sp => sp.IsHidden || (boundaries.Contains((uint)sp.Offset) && (uint)sp.Offset < codeSize));
			}

			static int FinallyNumber(string name) {
				int i = name.Length;
				while (i > 0 && char.IsDigit(name[i - 1]))
					i--;
				return i < name.Length && int.TryParse(name.Substring(i), out int n) ? n : int.MaxValue;
			}
		}

		/// <summary>
		/// IL offsets of the instructions that access the IL locals of the variables the decompiled code shows
		/// </summary>
		static Dictionary<int, HashSet<uint>> GetVisibleAccesses(ILFunction function, SourceVariableTable variables) {
			var accesses = new Dictionary<int, HashSet<uint>>();
			foreach (var v in function.Variables) {
				if (!variables.IsUsedInText(v) || variables.GetILLocal(v) is not Local local)
					continue;
				if (!accesses.TryGetValue(local.Index, out var offsets))
					accesses.Add(local.Index, offsets = new HashSet<uint>());
				foreach (var inst in v.LoadInstructions.Cast<ILInstruction>().Concat(v.StoreInstructions.OfType<ILInstruction>()).Concat(v.AddressInstructions)) {
					foreach (var range in inst.ILRanges)
						offsets.Add((uint)range.Start);
				}
			}
			return accesses;
		}

		/// <summary>
		/// Offsets of all instructions plus the code size. Computed without updating the instructions' offsets,
		/// the body may be shared with the UI.
		/// </summary>
		static HashSet<uint>? GetInstructionBoundaries(CilBody? body) {
			if (body is null)
				return null;
			var set = new HashSet<uint>();
			uint offset = 0;
			foreach (var instr in body.Instructions) {
				set.Add(offset);
				offset += (uint)instr.GetSize();
			}
			set.Add(offset);
			return set;
		}

		static IEnumerable<SourceLocal> GetLocals(ILFunction function, SourceVariableTable variables) {
			// Upstream can split one IL slot into several variables (e.g. a reused slot in optimized code). The
			// scope is method-wide, so keep one name per slot like upstream's own PDB writer does.
			var seenSlots = new HashSet<Local>();
			var seenFields = new HashSet<FieldDef>();
			// The variables the code uses first: their names win if upstream split a slot
			foreach (var v in function.Variables.OrderBy(a => variables.IsUsedInText(a) ? 0 : 1)) {
				if (v.Kind == VariableKind.Parameter || v.Name is null)
					continue;
				if (v.StateMachineField is not null) {
					if (variables.GetHoistedField(v) is FieldDef hoistedField && seenFields.Add(hoistedField))
						yield return variables.GetLocal(v);
					continue;
				}
				if (variables.GetILLocal(v) is not Local local || !seenSlots.Add(local))
					continue;
				if (variables.IsUsedInText(v))
					yield return variables.GetLocal(v);
				else if (IsDisplayClass(local.Type)) {
					// Upstream shows the captured variables as locals. The expression evaluator finds them in the
					// display class if its local has the C# compiler's name.
					yield return new SourceLocal(local, "CS$<>8__locals" + local.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), local.Type, SourceVariableFlags.None);
				}
				// Else it's a temporary the code doesn't show (e.g. an awaiter): the Locals window would show 'V_3'
			}
		}

		static bool IsDisplayClass(TypeSig? type) {
			type = type?.RemovePinnedAndModifiers();
			if (type is GenericInstSig genericInst)
				type = genericInst.GenericType;
			var name = type?.ToTypeDefOrRef()?.Name?.String;
			return name is not null && (name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal) || name.StartsWith("_Closure$__", StringComparison.Ordinal));
		}

		static SourceParameter[] GetParameters(ILFunction function, MethodDef method, ModuleBridgeCache cache) {
			var list = new List<SourceParameter>();
			var seenParameters = new HashSet<int>();
			foreach (var v in function.Variables) {
				if (v.Kind != VariableKind.Parameter || v.Index is not int index || v.Name is null)
					continue;
				// Upstream: -1 is the hidden 'this' parameter. dnlib: Parameters includes a hidden 'this' parameter at
				// index 0 only if the signature has one (HasThis without ExplicitThis), see MethodSigIndexBase.
				int dnlibIndex = index + method.Parameters.MethodSigIndexBase;
				if ((uint)dnlibIndex >= (uint)method.Parameters.Count || !seenParameters.Add(dnlibIndex))
					continue;
				var parameter = method.Parameters[dnlibIndex];
				if (v.StateMachineField is not null && cache.ToDnlib(v.StateMachineField) is FieldDef hoistedField)
					list.Add(new SourceParameter(parameter, v.Name, hoistedField, SourceVariableFlags.None));
				else
					list.Add(new SourceParameter(parameter, v.Name, parameter.Type, SourceVariableFlags.None));
			}
			return list.ToArray();
		}

		static AsyncMethodDebugInfo GetAsyncInfo(ILFunction function, MethodDef moveNext, MethodDef? kickoffMethod) {
			var info = function.AsyncDebugInfo;
			var stepInfos = info.Awaits.IsDefault ? Array.Empty<AsyncStepInfo>() :
				info.Awaits.Select(a => new AsyncStepInfo((uint)a.YieldOffset, moveNext, (uint)a.ResumeOffset)).ToArray();
			// dnSpy only uses it for async void methods
			bool returnsVoid = kickoffMethod?.MethodSig?.RetType.RemovePinnedAndModifiers().GetElementType() == ElementType.Void;
			uint catchHandlerOffset = returnsVoid && info.CatchHandlerOffset >= 0 ? (uint)info.CatchHandlerOffset : uint.MaxValue;
			var builderField = FindBuilderField(kickoffMethod, moveNext.DeclaringType);
			return new AsyncMethodDebugInfo(stepInfos, builderField, catchHandlerOffset, FindSetResultOffset(moveNext, builderField));
		}

		/// <summary>
		/// The builder field is the one whose Start() the kickoff method calls:
		/// stateMachine.&lt;&gt;t__builder.Start(ref stateMachine)
		/// </summary>
		static FieldDef? FindBuilderField(MethodDef? kickoffMethod, TypeDef? stateMachineType) {
			var instrs = kickoffMethod?.Body?.Instructions;
			if (instrs is not null) {
				for (int i = 0; i < instrs.Count; i++) {
					var instr = instrs[i];
					if (instr.OpCode.Code != Code.Call || instr.Operand is not dnlib.DotNet.IMethod called || called.Name != "Start")
						continue;
					// ldloca stateMachine; ldflda builder; ldloca stateMachine; call Start
					for (int j = i - 1; j >= Math.Max(0, i - 4); j--) {
						if ((instrs[j].OpCode.Code == Code.Ldflda || instrs[j].OpCode.Code == Code.Ldfld) && instrs[j].Operand is dnlib.DotNet.IField f &&
							f.ResolveFieldDef() is FieldDef fd && fd.DeclaringType == stateMachineType)
							return fd;
					}
				}
			}
			// Fall back to the C# compiler's name
			return stateMachineType?.Fields.FirstOrDefault(f => f.Name == "<>t__builder");
		}

		/// <summary>
		/// Offset of the first instruction of this.&lt;&gt;t__builder.SetResult(...)
		/// </summary>
		static uint FindSetResultOffset(MethodDef moveNext, FieldDef? builderField) {
			var instrs = moveNext.Body?.Instructions;
			if (instrs is null || builderField is null)
				return uint.MaxValue;
			var offsets = GetOffsets(instrs);
			for (int i = 0; i < instrs.Count; i++) {
				var instr = instrs[i];
				if (instr.OpCode.Code != Code.Call || instr.Operand is not dnlib.DotNet.IMethod called || called.Name != "SetResult")
					continue;
				// ldarg.0; ldflda builder; [ldloc result]; call SetResult
				for (int j = i - 1; j >= Math.Max(0, i - 3); j--) {
					if (instrs[j].OpCode.Code == Code.Ldflda && instrs[j].Operand is dnlib.DotNet.IField f && f.ResolveFieldDef() == builderField) {
						int start = j > 0 && instrs[j - 1].OpCode.Code == Code.Ldarg_0 ? j - 1 : j;
						return offsets[start];
					}
				}
			}
			return uint.MaxValue;
		}

		static uint[] GetOffsets(IList<Instruction> instrs) {
			var offsets = new uint[instrs.Count];
			uint offset = 0;
			for (int i = 0; i < instrs.Count; i++) {
				offsets[i] = offset;
				offset += (uint)instrs[i].GetSize();
			}
			return offsets;
		}

		sealed class Spans {
			public Dictionary<ILFunction, (int start, int end)> ByFunction { get; } = new Dictionary<ILFunction, (int, int)>();
			public Dictionary<UpstreamMethod, (int start, int end)> ByMethod { get; } = new Dictionary<UpstreamMethod, (int, int)>();
			/// <summary>Closing brace of a function's block body</summary>
			public Dictionary<ILFunction, TextSpan?> ClosingBraces { get; } = new Dictionary<ILFunction, TextSpan?>();
			/// <summary>Opening brace of a constructor without an initializer (its base constructor call is implicit)</summary>
			public Dictionary<ILFunction, TextSpan> ImplicitBaseCallBraces { get; } = new Dictionary<ILFunction, TextSpan>();
		}

		/// <summary>
		/// Kinds of the statements that upstream creates sequence points for (their text span is the node's)
		/// </summary>
		static Dictionary<TextSpan, StatementKind> GetStatementKinds(SyntaxTree syntaxTree, ILocationMapper output) {
			var kinds = new Dictionary<TextSpan, StatementKind>();
			foreach (var node in syntaxTree.Descendants) {
				StatementKind kind;
				switch (node) {
				case BlockStatement block:
					if (!block.StartLocation.IsEmpty && !block.EndLocation.IsEmpty) {
						int braceStart = output.ToOffset(block.StartLocation.Line, block.StartLocation.Column);
						int braceEnd = output.ToOffset(block.EndLocation.Line, block.EndLocation.Column);
						if (braceEnd - braceStart >= 2) {
							kinds[TextSpan.FromBounds(braceStart, braceStart + 1)] = StatementKind.Brace;
							kinds[TextSpan.FromBounds(braceEnd - 1, braceEnd)] = StatementKind.Brace;
						}
					}
					continue;
				case ExpressionStatement:
				case VariableDeclarationStatement:
				case ReturnStatement:
				case ThrowStatement:
				case YieldReturnStatement:
					kind = CanBranch(node) ? StatementKind.SimpleBranching : StatementKind.Simple;
					break;
				case BreakStatement:
				case ContinueStatement:
				case GotoStatement:
				case YieldBreakStatement:
					kind = StatementKind.Jump;
					break;
				default:
					continue;
				}
				if (node.StartLocation.IsEmpty || node.EndLocation.IsEmpty)
					continue;
				int start = output.ToOffset(node.StartLocation.Line, node.StartLocation.Column);
				int end = output.ToOffset(node.EndLocation.Line, node.EndLocation.Column);
				if (end > start)
					kinds[TextSpan.FromBounds(start, end)] = kind;
			}
			return kinds;
		}

		/// <summary>
		/// Can the statement's expression branch (?., ??, ?:, &amp;&amp;, ||, switch expressions, patterns, await, throw
		/// expressions)? Lambdas are other methods.
		/// </summary>
		static bool CanBranch(AstNode statement) {
			var work = new Stack<AstNode>();
			work.Push(statement);
			while (work.Count != 0) {
				var node = work.Pop();
				switch (node) {
				case ConditionalExpression:
				case SwitchExpression:
				case IsExpression:
				case ThrowExpression:
					return true;
				case BinaryOperatorExpression binary when binary.Operator is BinaryOperatorType.ConditionalAnd or BinaryOperatorType.ConditionalOr or BinaryOperatorType.NullCoalescing:
					return true;
				case UnaryOperatorExpression unary when unary.Operator is UnaryOperatorType.Await or UnaryOperatorType.NullConditional or UnaryOperatorType.NullConditionalRewrap:
					return true;
				case LambdaExpression:
				case AnonymousMethodExpression:
				case LocalFunctionDeclarationStatement:
					continue;
				}
				foreach (var child in node.Children)
					work.Push(child);
			}
			return false;
		}

		static bool IsFunctionDefinition(AstNode node) =>
			node is EntityDeclaration || node is LambdaExpression || node is AnonymousMethodExpression || node is LocalFunctionDeclarationStatement ||
			// Lambdas turned into query clauses (where, select, orderby...) keep their annotations
			node is QueryClause;

		static Spans GetSpans(SyntaxTree syntaxTree, ILocationMapper output) {
			var spans = new Spans();
			foreach (var node in syntaxTree.Descendants) {
				if (node.StartLocation.IsEmpty || node.EndLocation.IsEmpty)
					continue;
				var span = (output.ToOffset(node.StartLocation.Line, node.StartLocation.Column), output.ToOffset(node.EndLocation.Line, node.EndLocation.Column));
				// Nodes that define a function are annotated with its ILFunction. So are references to local functions
				// (call sites), which must be ignored.
				if (IsFunctionDefinition(node) && node.Annotation<ILFunction>() is ILFunction function && !spans.ByFunction.ContainsKey(function)) {
					spans.ByFunction.Add(function, span);
					if (node.Children.OfType<BlockStatement>().FirstOrDefault() is BlockStatement block && !block.EndLocation.IsEmpty) {
						int braceEnd = output.ToOffset(block.EndLocation.Line, block.EndLocation.Column);
						spans.ClosingBraces[function] = TextSpan.FromBounds(braceEnd - 1, braceEnd);
						if (node is ConstructorDeclaration ctor && ctor.Initializer is null && !ctor.HasModifier(Modifiers.Static) && !block.StartLocation.IsEmpty) {
							int braceStart = output.ToOffset(block.StartLocation.Line, block.StartLocation.Column);
							spans.ImplicitBaseCallBraces[function] = TextSpan.FromBounds(braceStart, braceStart + 1);
						}
					}
				}
				if (node is EntityDeclaration decl && decl.GetSymbol() is UpstreamMethod method)
					spans.ByMethod[method] = span;
			}
			return spans;
		}
	}
}
