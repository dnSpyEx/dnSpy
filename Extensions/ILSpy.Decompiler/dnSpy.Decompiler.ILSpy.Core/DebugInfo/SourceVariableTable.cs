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
using ICSharpCode.Decompiler.IL;

namespace dnSpy.Decompiler.ILSpy.Core.DebugInfo {
	/// <summary>
	/// Creates the objects that represent upstream variables, both in the text (references, so all uses of a
	/// variable are highlighted together) and in the debug info (locals the debugger shows). One instance is
	/// used for one decompilation so both use the same objects.
	/// </summary>
	sealed class SourceVariableTable {
		readonly ModuleBridgeCache cache;
		readonly TypeSigConverter typeSigConverter;
		readonly Dictionary<ILVariable, SourceLocal> locals = new Dictionary<ILVariable, SourceLocal>();
		readonly Dictionary<ILVariable, object> textReferences = new Dictionary<ILVariable, object>();

		public SourceVariableTable(ModuleBridgeCache cache, TypeSigConverter typeSigConverter) {
			this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
			this.typeSigConverter = typeSigConverter ?? throw new ArgumentNullException(nameof(typeSigConverter));
		}

		/// <summary>
		/// Gets the object used as the reference of a variable in the text: the dnlib <see cref="Parameter"/> of
		/// parameters (what dnSpy's other decompilers use) and a <see cref="SourceLocal"/> for everything else
		/// </summary>
		public object GetTextReference(ILVariable variable) {
			if (textReferences.TryGetValue(variable, out var reference))
				return reference;
			reference = TryGetParameter(variable) ?? (object)GetLocal(variable);
			textReferences.Add(variable, reference);
			return reference;
		}

		/// <summary>
		/// true if the decompiled text uses the variable. Returns true for all variables if nothing was written with
		/// this table (then it isn't known).
		/// </summary>
		public bool IsUsedInText(ILVariable variable) => textReferences.Count == 0 || textReferences.ContainsKey(variable);

		Parameter? TryGetParameter(ILVariable variable) {
			if (variable.Kind != VariableKind.Parameter || !(variable.Index is int index))
				return null;
			if (!(variable.Function?.Method is ICSharpCode.Decompiler.TypeSystem.IMethod method) || !(cache.ToDnlib(method) is MethodDef md))
				return null;
			// Upstream: -1 is the hidden 'this' parameter. dnlib: Parameters includes a hidden 'this' parameter at
			// index 0 only if the signature has one, see MethodSigIndexBase.
			int dnlibIndex = index + md.Parameters.MethodSigIndexBase;
			if ((uint)dnlibIndex >= (uint)md.Parameters.Count)
				return null;
			return md.Parameters[dnlibIndex];
		}

		readonly Dictionary<object, SourceLocal> syntheticVariables = new Dictionary<object, SourceLocal>();

		/// <summary>
		/// Gets the reference of a variable that isn't an IL variable (eg. query 'let x = ...') or a local function
		/// that has no dnlib method
		/// </summary>
		public SourceLocal GetSyntheticVariable(object upstreamVariable, string name) {
			if (!syntheticVariables.TryGetValue(upstreamVariable, out var local)) {
				local = new SourceLocal(null, name, typeSigConverter.ToTypeSig(null), SourceVariableFlags.None);
				syntheticVariables.Add(upstreamVariable, local);
			}
			return local;
		}

		/// <summary>
		/// Gets the local of a variable (not a parameter)
		/// </summary>
		public SourceLocal GetLocal(ILVariable variable) {
			if (locals.TryGetValue(variable, out var local))
				return local;
			var flags = SourceVariableFlags.None;
			if (variable.IsRefReadOnly)
				flags |= SourceVariableFlags.ReadOnlyReference;
			var name = variable.Name ?? string.Empty;
			// SourceLocal derives DecompilerGenerated itself (no IL local and no hoisted field)
			if (GetHoistedField(variable) is FieldDef hoistedField)
				local = new SourceLocal(null, name, hoistedField, flags);
			else if (GetILLocal(variable) is Local ilLocal)
				local = new SourceLocal(ilLocal, name, ilLocal.Type, flags);
			else
				local = new SourceLocal(null, name, typeSigConverter.ToTypeSig(variable.Type), flags);
			locals.Add(variable, local);
			return local;
		}

		/// <summary>
		/// Gets the field of the state machine that stores a variable of an async/iterator method
		/// </summary>
		public FieldDef? GetHoistedField(ILVariable variable) =>
			variable.StateMachineField is null ? null : cache.ToDnlib(variable.StateMachineField) as FieldDef;

		/// <summary>
		/// Gets the IL local (slot) of a variable or null if it doesn't use one
		/// </summary>
		public Local? GetILLocal(ILVariable variable) {
			if (!IsILLocal(variable.Kind) && variable.Kind != VariableKind.ExceptionStackSlot)
				return null;
			// IL offsets and locals belong to the method whose body was decompiled: MoveNext() for async/iterator methods
			var function = variable.Function;
			var method = function?.MoveNextMethod ?? function?.Method;
			var bodyMethod = method is null ? null : cache.ToDnlib(method) as MethodDef;
			var body = bodyMethod?.Body;
			if (body is null)
				return null;
			// A catch variable can be the exception on the stack (its index isn't a local)
			if (variable.Kind == VariableKind.ExceptionStackSlot || (variable.Kind == VariableKind.ExceptionLocal && variable.Index is null))
				return GetExceptionLocal(variable, body);
			if (!(variable.Index is int index))
				return null;
			if ((uint)index >= (uint)body.Variables.Count)
				return null;
			return body.Variables[index];
		}

		/// <summary>
		/// A catch variable has no IL local upstream (the exception is on the stack when the handler starts), but the
		/// compiler stores it in one, which upstream removed: the debugger reads its value from that local.
		/// </summary>
		Local? GetExceptionLocal(ILVariable variable, CilBody body) {
			if (exceptionLocals.TryGetValue(variable, out var cached))
				return cached;
			var local = FindExceptionLocal(variable, body);
			exceptionLocals[variable] = local;
			return local;
		}
		readonly Dictionary<ILVariable, Local?> exceptionLocals = new Dictionary<ILVariable, Local?>();

		/// <summary>
		/// Catch handlers (by their variable) and the instruction offsets of a function's body, computed once (a method
		/// can have hundreds of catch blocks)
		/// </summary>
		sealed class HandlerInfo {
			public readonly Dictionary<ILVariable, TryCatchHandler> Handlers = new Dictionary<ILVariable, TryCatchHandler>();
			public uint[] Offsets = Array.Empty<uint>();
			public readonly Dictionary<Instruction, int> IndexOf = new Dictionary<Instruction, int>();
		}
		readonly Dictionary<(ILFunction, CilBody), HandlerInfo> handlerInfos = new Dictionary<(ILFunction, CilBody), HandlerInfo>();

		HandlerInfo GetHandlerInfo(ILFunction function, CilBody body) {
			if (handlerInfos.TryGetValue((function, body), out var info))
				return info;
			info = new HandlerInfo();
			foreach (var h in function.Descendants.OfType<TryCatchHandler>()) {
				if (!info.Handlers.ContainsKey(h.Variable))
					info.Handlers.Add(h.Variable, h);
			}
			var instrs = body.Instructions;
			info.Offsets = new uint[instrs.Count];
			uint offset = 0;
			for (int i = 0; i < instrs.Count; i++) {
				info.Offsets[i] = offset;
				info.IndexOf[instrs[i]] = i;
				offset += (uint)instrs[i].GetSize();
			}
			handlerInfos.Add((function, body), info);
			return info;
		}

		Local? FindExceptionLocal(ILVariable variable, CilBody body) {
			if (variable.Function is not ILFunction function)
				return null;
			var info = GetHandlerInfo(function, body);
			if (!info.Handlers.TryGetValue(variable, out var handler))
				return null;
			var instrs = body.Instructions;
			var offsets = info.Offsets;
			// catch (T ex): the store at the start of the handler
			var range = handler.ExceptionSpecifierILRange;
			if (!range.IsEmpty) {
				for (int i = 0; i < instrs.Count; i++) {
					if (offsets[i] >= range.Start && offsets[i] < range.End && instrs[i].IsStloc())
						return instrs[i].GetLocal(body.Variables);
				}
				return null;
			}
			// The handler's clause: its filter or handler starts where the upstream handler's code does
			var starts = new HashSet<uint>();
			foreach (var instr in new[] { handler, handler.Filter, handler.Body }) {
				if (instr is not null && instr.StartILOffset >= 0)
					starts.Add((uint)instr.StartILOffset);
			}
			int IndexOf(Instruction? instr) => instr is not null && info.IndexOf.TryGetValue(instr, out int index) ? index : -1;
			var eh = body.ExceptionHandlers.FirstOrDefault(a =>
				(IndexOf(a.FilterStart) is int f && f >= 0 && starts.Contains(offsets[f])) ||
				(IndexOf(a.HandlerStart) is int h && h >= 0 && starts.Contains(offsets[h])));
			if (eh is null)
				return null;
			int handlerStart = IndexOf(eh.HandlerStart);
			if (handlerStart < 0)
				return null;
			// (Generic exception types: compare the type definitions, dnlib qualifies generic arguments with assemblies)
			var typeName = variable.Type.GetDefinition()?.ReflectionName ?? variable.Type.ReflectionName;
			bool IsExceptionStore(int i) => instrs[i].IsStloc() && instrs[i].GetLocal(body.Variables) is Local local && GetDefinitionName(local.Type) == typeName;
			// catch (T ex) when (...): the filter stores it after the type check (isinst T; dup; brtrue L; ...; L: stloc)
			int filterStart = IndexOf(eh.FilterStart);
			if (eh.HandlerType == ExceptionHandlerType.Filter && filterStart >= 0) {
				for (int i = filterStart; i < handlerStart; i++) {
					if (IsExceptionStore(i))
						return instrs[i].GetLocal(body.Variables);
				}
				return null;
			}
			// catch (T ex): stored by the handler's first instruction
			return eh.HandlerType == ExceptionHandlerType.Catch && IsExceptionStore(handlerStart) ? instrs[handlerStart].GetLocal(body.Variables) : null;
		}

		static string? GetDefinitionName(TypeSig? type) {
			type = type?.RemovePinnedAndModifiers();
			if (type is GenericInstSig genericInst)
				return genericInst.GenericType?.TypeDefOrRef?.ReflectionFullName;
			return type?.ToTypeDefOrRef()?.ReflectionFullName;
		}

		static bool IsILLocal(VariableKind kind) =>
			kind switch {
				VariableKind.Local or VariableKind.PinnedLocal or VariableKind.PinnedRegionLocal or VariableKind.UsingLocal or
				VariableKind.ForeachLocal or VariableKind.InitializerTarget or VariableKind.ExceptionLocal or VariableKind.DisplayClassLocal or
				VariableKind.PatternLocal => true,
				_ => false,
			};
	}
}
