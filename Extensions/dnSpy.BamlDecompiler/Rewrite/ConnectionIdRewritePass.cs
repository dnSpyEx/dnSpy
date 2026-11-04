/*
	Copyright (c) 2015 Ki

	Permission is hereby granted, free of charge, to any person obtaining a copy
	of this software and associated documentation files (the "Software"), to deal
	in the Software without restriction, including without limitation the rights
	to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
	copies of the Software, and to permit persons to whom the Software is
	furnished to do so, subject to the following conditions:

	The above copyright notice and this permission notice shall be included in
	all copies or substantial portions of the Software.

	THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
	IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
	FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
	AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
	LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
	OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
	THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using dnlib.DotNet;
using dnSpy.BamlDecompiler.Baml;
using dnSpy.BamlDecompiler.Properties;
using dnSpy.BamlDecompiler.Xaml;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.BamlDecompiler.Rewrite {
	sealed class ConnectionIdRewritePass : IRewritePass {
		static bool Impl(MethodDef method, MethodDef ifaceMethod) {
			if (method.HasOverrides) {
				var comparer = new SigComparer(SigComparerOptions.CompareDeclaringTypes | SigComparerOptions.PrivateScopeIsComparable);
				if (method.Overrides.Any(m => comparer.Equals(m.MethodDeclaration, ifaceMethod)))
					return true;
			}

			if (method.Name != ifaceMethod.Name)
				return false;
			return TypesHierarchyHelpers.MatchInterfaceMethod(method, ifaceMethod, ifaceMethod.DeclaringType);
		}

		public void Run(XamlContext ctx, XDocument document) {
			var xClass = document.Root.Elements().First().Attribute(ctx.GetKnownNamespace("Class", XamlContext.KnownNamespace_Xaml));
			if (xClass is null)
				return;

			var type = ctx.Module.Find(xClass.Value, true);
			if (type is null)
				return;

			var componentConnectorConnect = ctx.Baml.KnownThings.Types(KnownTypes.IComponentConnector).TypeDef.FindMethod("Connect");
			var styleConnectorConnect = ctx.Baml.KnownThings.Types(KnownTypes.IStyleConnector).TypeDef.FindMethod("Connect");

			var connIds = new Dictionary<int, Action<XamlContext, XElement>>();

			if (!CollectConnectionIds(ctx, componentConnectorConnect, type, connIds)) {
				var msg = dnSpy_BamlDecompiler_Resources.Error_IComponentConnectorConnectCannotBeParsed;
				document.Root.AddBeforeSelf(new XComment(string.Format(msg, type.ReflectionFullName)));
			}

			if (!CollectConnectionIds(ctx, styleConnectorConnect, type, connIds)) {
				var msg = dnSpy_BamlDecompiler_Resources.Error_IStyleConnectorConnectCannotBeParsed;
				document.Root.AddBeforeSelf(new XComment(string.Format(msg, type.ReflectionFullName)));
			}

			foreach (var elem in document.Elements()) {
				ProcessElement(ctx, elem, connIds);
			}
		}

		bool CollectConnectionIds(XamlContext ctx, MethodDef connectInterfaceMethod, TypeDef currentType, Dictionary<int, Action<XamlContext, XElement>> allConnIds) {
			MethodDef connect = null;
			foreach (var method in currentType.Methods) {
				if (Impl(method, connectInterfaceMethod)) {
					connect = method;
					break;
				}
			}

			if (connect is not null) {
				Dictionary<int, Action<XamlContext, XElement>> connIds = null;
				try {
					connIds = ExtractConnectionId(ctx, connect);
				}
				catch {
				}

				if (connIds is null)
					return false;

				foreach (var keyValuePair in connIds)
					allConnIds.Add(keyValuePair.Key, keyValuePair.Value);
			}

			return true;
		}

		static void ProcessElement(XamlContext ctx, XElement elem, Dictionary<int, Action<XamlContext, XElement>> connIds) {
			CheckConnectionId(ctx, elem, connIds);
			foreach (var child in elem.Elements()) {
				ProcessElement(ctx, child, connIds);
			}
		}

		static void CheckConnectionId(XamlContext ctx, XElement elem, Dictionary<int, Action<XamlContext, XElement>> connIds) {
			foreach (var connId in elem.Annotations<BamlConnectionId>()) {
				if (!connIds.TryGetValue((int)connId.Id, out var cb)) {
					elem.AddBeforeSelf(new XComment(string.Format(dnSpy_BamlDecompiler_Resources.Error_UnknownConnectionId, connId.Id)));
					return;
				}

				cb(ctx, elem);
			}
		}

		struct FieldAssignment {
			public string FieldName;
			public bool IsPublic;

			public void Callback(XamlContext ctx, XElement elem) {
				var xName = ctx.GetKnownNamespace("Name", XamlContext.KnownNamespace_Xaml, elem);
				if (elem.Attribute("Name") is null && elem.Attribute(xName) is null)
					elem.Add(new XAttribute(xName, IdentifierEscaper.Escape(FieldName)));

				if (IsPublic) {
					var fieldModifier = ctx.GetKnownNamespace("FieldModifier", XamlContext.KnownNamespace_Xaml, elem);
					elem.Add(new XAttribute(fieldModifier, ctx.BamlDecompilerOptions.PublicFieldModifier));
				}
			}
		}

		struct EventAttachment {
			public ITypeDefOrRef AttachedType;
			public string EventName;
			public string MethodName;

			public void Callback(XamlContext ctx, XElement elem) {
				var type = elem.Annotation<XamlType>();
				if (type is not null && type.TypeNamespace == "System.Windows" && type.TypeName == "Style") {
					elem.Add(new XElement(type.Namespace + "EventSetter",
						new XAttribute("Event", IdentifierEscaper.Escape(EventName)),
						new XAttribute("Handler", IdentifierEscaper.Escape(MethodName))));
					return;
				}

				string encodedEventName = XmlConvert.EncodeName(EventName);
				XName name;
				if (AttachedType is not null) {
					var clrNs = AttachedType.ReflectionNamespace;
					var xmlNs = ctx.XmlNs.LookupXmlns(AttachedType.DefinitionAssembly, clrNs);
					var xmlNamespace = ctx.GetXmlNamespace(xmlNs);
					if (xmlNamespace is not null)
						name = xmlNamespace.GetName(encodedEventName);
					else
						name = $"{XmlConvert.EncodeName(AttachedType.Name)}.{encodedEventName}";
				}
				else
					name = encodedEventName;

				elem.Add(new XAttribute(name, IdentifierEscaper.Escape(MethodName)));
			}
		}

		struct Error {
			public string Msg;

			public void Callback(XamlContext ctx, XElement elem) => elem.AddBeforeSelf(new XComment(Msg));
		}

		/// <summary>
		/// Reads the Connect method with the upstream decompiler's ILAst: a switch (or if statements) on the connection
		/// ID, each case assigns 'target' to a field and/or adds event handlers to it. Returns null if it can't be read.
		/// </summary>
		Dictionary<int, Action<XamlContext, XElement>> ExtractConnectionId(XamlContext ctx, MethodDef method) {
			var ilast = UpstreamILAst.TryCreate(method, runTransforms: true, ctx.CancellationToken);
			if (ilast is null)
				return null;

			var cases = GetCases(ilast.Function);
			if (cases is null)
				return null;
			var connIds = new Dictionary<int, Action<XamlContext, XElement>>();
			foreach (var (ids, body) in cases) {
				Action<XamlContext, XElement> cb = null;
				var handled = new HashSet<ILInstruction>();
				foreach (var root in body) {
					foreach (var inst in root.Descendants) {
						if (handled.Contains(inst))
							continue;
						if (MatchEventSetterCreation(inst, handled, out var evAttach)) {
							cb += evAttach.Callback;
							continue;
						}
						if (MatchFieldAssignment(inst, out var fieldAssignment)) {
							cb += fieldAssignment.Callback;
							continue;
						}
						if (inst is CallInstruction call && call.OpCode != OpCode.NewObj)
							cb += GetEventCallback(ilast, method, call);
					}
				}

				if (cb is not null) {
					foreach (var id in ids)
						connIds[id] = cb;
				}
			}

			return connIds;
		}

		/// <summary>
		/// The cases (connection IDs, code) of the Connect method, or null if it doesn't look like one
		/// </summary>
		static List<(IList<int> ids, List<ILInstruction> body)> GetCases(ILFunction function) {
			var list = new List<(IList<int>, List<ILInstruction>)>();
			var sw = function.Descendants.OfType<SwitchInstruction>().FirstOrDefault(a => IsConnectionId(a.Value));
			if (sw is not null) {
				var defaultSection = sw.GetDefaultSection();
				foreach (var section in sw.Sections) {
					if (section == defaultSection || section.HasNullLabel)
						continue;
					var ids = GetIds(section.Labels);
					if (ids is null)
						return null;
					list.Add((ids, GetCaseBody(section.Body)));
				}
				return list;
			}

			foreach (var ifInst in function.Descendants.OfType<IfInstruction>()) {
				if (!MatchConnectionIdComparison(ifInst.Condition, out bool isEq, out int id))
					continue;
				var inst = isEq ? ifInst.TrueInst : ifInst.FalseInst;
				List<ILInstruction> body;
				if (!isEq && inst.MatchNop() && ifInst.Parent is Block block) {
					// if (connectionId != 1) { other cases } code of case 1
					body = new List<ILInstruction>();
					for (int i = ifInst.ChildIndex + 1; i < block.Instructions.Count; i++) {
						var next = block.Instructions[i];
						if (next is IfInstruction nextIf && MatchConnectionIdComparison(nextIf.Condition, out _, out _))
							break;
						body.AddRange(GetCaseBody(next));
					}
				}
				else
					body = GetCaseBody(inst);
				list.Add((new[] { id }, body));
			}
			if (list.Count == 0 && HasCode(function))
				return null;
			return list;
		}

		/// <summary>
		/// The connection ID values of a switch section (one value or a small range)
		/// </summary>
		static IList<int> GetIds(ICSharpCode.Decompiler.Util.LongSet labels) {
			var ids = new List<int>();
			foreach (var interval in labels.Intervals) {
				// [Start, InclusiveEnd]
				if (interval.Start < int.MinValue || interval.InclusiveEnd > int.MaxValue || interval.InclusiveEnd - interval.Start > 10000)
					return null;
				for (long v = interval.Start; v <= interval.InclusiveEnd; v++)
					ids.Add((int)v);
			}
			return ids;
		}

		/// <summary>
		/// The code of a case: the instruction, or the block it branches to
		/// </summary>
		static List<ILInstruction> GetCaseBody(ILInstruction inst) {
			var list = new List<ILInstruction>();
			if (inst is Branch br && br.TargetBlock is Block target)
				list.Add(target);
			else
				list.Add(inst);
			return list;
		}

		/// <summary>
		/// Any code other than 'this._contentLoaded = true;' and return
		/// </summary>
		static bool HasCode(ILFunction function) {
			foreach (var inst in function.Descendants) {
				switch (inst) {
				case ILFunction:
				case BlockContainer:
				case Block:
				case Leave:
				case Nop:
				case Branch:
					continue;
				case StObj:
				case LdFlda:
				case LdLoc:
				case LdcI4:
					continue;
				default:
					return true;
				}
			}
			return false;
		}

		// The connection ID: the first parameter (or a copy of it)
		static bool IsConnectionId(ILInstruction inst) =>
			inst.MatchLdLoc(out var v) && v.Type.IsKnownType(KnownTypeCode.Int32) && (v.Kind != VariableKind.Parameter || v.Index == 0);

		static bool MatchConnectionIdComparison(ILInstruction condition, out bool isEq, out int val) {
			isEq = false;
			val = 0;
			if (condition is not Comp comp || (comp.Kind != ComparisonKind.Equality && comp.Kind != ComparisonKind.Inequality))
				return false;
			if (!IsConnectionId(comp.Left) || !comp.Right.MatchLdcI4(out val))
				return false;
			isEq = comp.Kind == ComparisonKind.Equality;
			return true;
		}

		// The target object: the second parameter
		static bool IsTarget(ILInstruction inst) {
			while (inst.MatchCastClass(out var arg, out _) || inst.MatchIsInst(out arg, out _))
				inst = arg;
			return inst.MatchLdLoc(out var v) && v.Kind == VariableKind.Parameter && v.Index == 1;
		}

		// this.field = (T)target;
		static bool MatchFieldAssignment(ILInstruction inst, out FieldAssignment fieldAssignment) {
			fieldAssignment = default;
			if (!inst.MatchStFld(out var obj, out var field, out var value) || !obj.MatchLdThis() || !IsTarget(value))
				return false;
			fieldAssignment = new FieldAssignment {
				FieldName = field.Name,
				IsPublic = field.Accessibility == ICSharpCode.Decompiler.TypeSystem.Accessibility.Public,
			};
			return true;
		}

		/// <summary>
		/// ((T)target).Event += new Handler(this.Method), ((UIElement)target).AddHandler(T.EventNameEvent, new Handler(this.Method))
		/// </summary>
		static Action<XamlContext, XElement> GetEventCallback(UpstreamILAst ilast, MethodDef connect, CallInstruction call) {
			var method = call.Method;
			if (method.Name == "AddHandler" && call.Arguments.Count == 3 && method.Parameters.Count == 2 && !method.IsStatic) {
				// Attached event
				if (!call.Arguments[1].MatchLdsFld(out var eventField)) 
					return null;
				if (!MatchHandlerCreation(call.Arguments[2], out var handlerName))
					return new Error { Msg = string.Format(dnSpy_BamlDecompiler_Resources.Error_AttachedEvent, eventField.Name) }.Callback;

				var attachedType = GetDnlibType(ilast, connect, call.Arguments[1], eventField);
				if (attachedType is null)
					return new Error { Msg = string.Format(dnSpy_BamlDecompiler_Resources.Error_AttachedEvent, eventField.Name) }.Callback;
				return new EventAttachment {
					AttachedType = attachedType,
					EventName = GetEventName(eventField.Name),
					MethodName = handlerName,
				}.Callback;
			}

			if (method.Name.StartsWith("add_", StringComparison.Ordinal) && method.Name.Length > 4 && call.Arguments.Count == 2 && !method.IsStatic) {
				// CLR event
				var eventName = method.AccessorOwner is IEvent ev ? ev.Name : method.Name.Substring(4);
				if (!MatchHandlerCreation(call.Arguments[1], out var handlerName))
					return new Error { Msg = string.Format(dnSpy_BamlDecompiler_Resources.Error_AttachedEvent, method.Name) }.Callback;
				return new EventAttachment {
					EventName = eventName,
					MethodName = handlerName,
				}.Callback;
			}

			return null;
		}

		// EventSetter's event is the routed event field without "Event": ClickEvent -> Click
		static string GetEventName(string fieldName) {
			if (fieldName.EndsWith("Event", StringComparison.Ordinal) && fieldName.Length > 5)
				return fieldName.Substring(0, fieldName.Length - 5);
			return fieldName;
		}

		static ITypeDefOrRef GetDnlibType(UpstreamILAst ilast, MethodDef connect, ILInstruction fieldLoad, ICSharpCode.Decompiler.TypeSystem.IField field) {
			if (ilast.ToDnlib(field) is dnlib.DotNet.IField dnlibField && dnlibField.DeclaringType is ITypeDefOrRef declType)
				return declType.ResolveTypeDef() ?? declType;
			if (field.DeclaringTypeDefinition is ITypeDefinition td && ilast.ToDnlib(td) is TypeDef typeDef)
				return typeDef;
			// The field's type can't be resolved (e.g. a library that isn't available): the field reference in the IL
			dnlib.DotNet.IField found = null;
			foreach (var instr in connect.Body?.Instructions ?? (IList<dnlib.DotNet.Emit.Instruction>)Array.Empty<dnlib.DotNet.Emit.Instruction>()) {
				if ((instr.OpCode.Code != dnlib.DotNet.Emit.Code.Ldsfld && instr.OpCode.Code != dnlib.DotNet.Emit.Code.Ldsflda) || instr.Operand is not dnlib.DotNet.IField f || f.Name != field.Name)
					continue;
				if (found is null || instr.Offset == fieldLoad.StartILOffset)
					found = f;
			}
			if (found?.DeclaringType is ITypeDefOrRef refType)
				return refType.ResolveTypeDef() ?? refType;
			return null;
		}

		// new Handler(this.Method)
		static bool MatchHandlerCreation(ILInstruction inst, out string handlerName) {
			handlerName = null;
			// A conversion is added when the delegate type can't be resolved
			while (inst is Conv conv)
				inst = conv.Argument;
			if (inst is not NewObj newObj || newObj.Arguments.Count != 2)
				return false;
			var ldftn = newObj.Arguments[1];
			if (ldftn.OpCode != OpCode.LdFtn && ldftn.OpCode != OpCode.LdVirtFtn)
				return false;
			handlerName = ((IInstructionWithMethodOperand)ldftn).Method.Name;
			return true;
		}

		/// <summary>
		/// IStyleConnector.Connect adds event setters to styles:
		/// EventSetter setter = new EventSetter(); setter.Event = T.EventNameEvent; setter.Handler = new Handler(this.Method); ((Style)target).Setters.Add(setter);
		/// (or an object initializer). <paramref name="inst"/> is the call of set_Event.
		/// </summary>
		static bool MatchEventSetterCreation(ILInstruction inst, HashSet<ILInstruction> handled, out EventAttachment @event) {
			@event = default;
			if (!MatchEventSetterCall(inst, "set_Event", out var setter, out var value) || !value.MatchLdsFld(out var eventField))
				return false;
			// set_Handler on the same event setter, after set_Event
			var block = inst.Parent as Block;
			if (block is null)
				return false;
			for (int i = inst.ChildIndex + 1; i < block.Instructions.Count; i++) {
				var next = block.Instructions[i];
				if (!MatchEventSetterCall(next, "set_Handler", out var setter2, out var handler))
					continue;
				if (setter2 != setter || !MatchHandlerCreation(handler, out var handlerName))
					return false;
				handled.Add(next);
				@event = new EventAttachment {
					EventName = GetEventName(eventField.Name),
					MethodName = handlerName,
				};
				return true;
			}
			return false;
		}

		static bool MatchEventSetterCall(ILInstruction inst, string name, out ILVariable setter, out ILInstruction value) {
			setter = null;
			value = null;
			if (inst is not CallInstruction call || call.OpCode == OpCode.NewObj || call.Arguments.Count != 2)
				return false;
			if (call.Method.Name != name || call.Method.DeclaringType.FullName != "System.Windows.EventSetter")
				return false;
			if (!call.Arguments[0].MatchLdLoc(out setter))
				return false;
			value = call.Arguments[1];
			return true;
		}
	}
}
