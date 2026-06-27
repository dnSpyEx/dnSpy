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
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.Util;

namespace dnSpy.BamlDecompiler.Rewrite {
	sealed class ConnectionIdRewritePass : IRewritePass {
		static bool Impl(MethodDef method, MethodDef ifaceMethod) {
			if (method.HasOverrides) {
				var comparer =
					new SigComparer(SigComparerOptions.CompareDeclaringTypes | SigComparerOptions.PrivateScopeIsComparable);
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

			var ts = new DecompilerTypeSystem(new MetadataFile(ctx.Module), TypeSystemOptions.Default);

			var connIds = new Dictionary<long, Action<XamlContext, XElement>>();

			if (!CollectConnectionIds(ctx, ts, componentConnectorConnect, type, connIds)) {
				var msg = dnSpy_BamlDecompiler_Resources.Error_IComponentConnectorConnectCannotBeParsed;
				document.Root.AddBeforeSelf(new XComment(string.Format(msg, type.ReflectionFullName)));
			}

			if (!CollectConnectionIds(ctx, ts, styleConnectorConnect, type, connIds)) {
				var msg = dnSpy_BamlDecompiler_Resources.Error_IStyleConnectorConnectCannotBeParsed;
				document.Root.AddBeforeSelf(new XComment(string.Format(msg, type.ReflectionFullName)));
			}

			foreach (var elem in document.Elements()) {
				ProcessElement(ctx, elem, connIds);
			}
		}

		bool CollectConnectionIds(XamlContext ctx, IDecompilerTypeSystem ts, MethodDef connectInterfaceMethod, TypeDef currentType, Dictionary<long, Action<XamlContext, XElement>> allConnIds) {
			MethodDef connect = null;
			foreach (var method in currentType.Methods) {
				if (Impl(method, connectInterfaceMethod)) {
					connect = method;
					break;
				}
			}

			if (connect is not null) {
				Dictionary<long, Action<XamlContext, XElement>> connIds = null;
				try {
					connIds = ExtractConnectionId(ctx, ts, connect);
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

		static void ProcessElement(XamlContext ctx, XElement elem, Dictionary<long, Action<XamlContext, XElement>> connIds) {
			CheckConnectionId(ctx, elem, connIds);
			foreach (var child in elem.Elements()) {
				ProcessElement(ctx, child, connIds);
			}
		}

		static void CheckConnectionId(XamlContext ctx, XElement elem, Dictionary<long, Action<XamlContext, XElement>> connIds) {
			var connId = elem.Annotation<BamlConnectionId>();
			if (connId is null)
				return;

			if (!connIds.TryGetValue(connId.Id, out var cb)) {
				elem.AddBeforeSelf(new XComment(string.Format(dnSpy_BamlDecompiler_Resources.Error_UnknownConnectionId, connId.Id)));
				return;
			}
		}

		struct FieldAssignment {
			public dnlib.DotNet.IField Field;

			public void Callback(XamlContext ctx, XElement elem) {
				var xName = ctx.GetKnownNamespace("Name", XamlContext.KnownNamespace_Xaml, elem);
				if (elem.Attribute("Name") is null && elem.Attribute(xName) is null)
					elem.Add(new XAttribute(xName, IdentifierEscaper.Escape(Field.Name)));

				var def = Field.ResolveFieldDef();
				if (def is not null && def.IsPublic) {
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

		Dictionary<long, Action<XamlContext, XElement>> ExtractConnectionId(XamlContext ctx, IDecompilerTypeSystem ts, MethodDef method) {
			var connectTs = ts.MainModule.GetDefinition(method);

			var genericContext = new GenericContext(connectTs.DeclaringType?.TypeParameters, connectTs.TypeParameters);

			// decompile method and optimize the switch
			var ilReader = new ILReader(ts.MainModule);
			var function = ilReader.ReadIL(method, genericContext, ILFunctionKind.TopLevelFunction, ctx.CancellationToken);

			var context = new ILTransformContext(function, ts) { CancellationToken = ctx.CancellationToken };
			function.RunTransforms(CSharpDecompiler.GetILTransforms(), context);

			var infos = GetCaseBlocks(function);

			var connIds = new Dictionary<long, Action<XamlContext, XElement>>();
			foreach (var info in infos) {
				Action<XamlContext, XElement> cb = null;

				var topLevelInstr = info.topLevelInstr;

				Block block = null;
				if (topLevelInstr is Branch br) {
					block = br.TargetBlock;
				}
				else if (topLevelInstr is Block blk) {
					block = blk;
				}

				if (block is not null) {
					for (int index = 0; index < block.Instructions.Count;) {
						var instr = block.Instructions[index];
						if (MatchEventSetterCreation(block, ref index, out var eventName, out var handler)) {
							cb += new EventAttachment { EventName = eventName, MethodName = handler }.Callback;
							continue;
						}

						if (instr.MatchStFld(out _, out var fld, out var value) &&
							value.MatchCastClass(out var arg, out _) && arg.MatchLdLoc(out var loc) &&
							loc.Kind == VariableKind.Parameter && loc.Index == 1) {
							cb += new FieldAssignment { Field = fld.MetadataToken }.Callback;
						}
						else if (instr is CallInstruction call && call.OpCode != OpCode.NewObj) {
							var operand = call.Method;
							if (operand.Name == "AddHandler" && call.Arguments.Count == 3 && operand.Parameters.Count == 2) {
								if (!call.Arguments[1].MatchLdsFld(out var field) ||
									!MatchEventHandlerCreation(call.Arguments[2], out var handlerName)) {
									cb += new Error {
										Msg = string.Format(dnSpy_BamlDecompiler_Resources.Error_AttachedEvent, field.Name)
									}.Callback;
									break;
								}

								string evName = field.Name;
								if (evName.EndsWith("Event"))
									evName = evName.Substring(0, evName.Length - 5);

								cb += new EventAttachment {
									AttachedType = field.MetadataToken.DeclaringType.ResolveTypeDefThrow(),
									EventName = evName,
									MethodName = handlerName
								}.Callback;
							}
							else if (call.Arguments.Count == 2) {
								var add = call.Method;
								var ev = add.MetadataToken?.DeclaringType.Resolve().Events.FirstOrDefault(e =>
									e.AddMethod == add.MetadataToken);

								if (ev is null || !MatchEventHandlerCreation(call.Arguments[1], out var handlerName)) {
									cb += new Error {
										Msg = string.Format(dnSpy_BamlDecompiler_Resources.Error_AttachedEvent, add.Name)
									}.Callback;
									break;
								}

								cb += new EventAttachment {
									EventName = ev.Name,
									MethodName = handlerName
								}.Callback;
							}
						}

						index++;
					}
				}

				if (cb is not null) {
					foreach (var id in info.connIds.Values)
						connIds[id] = cb;
				}
			}

			return connIds;
		}

		static List<(LongSet connIds, ILInstruction topLevelInstr)> GetCaseBlocks(ILFunction function) {
			var list = new List<(LongSet, ILInstruction)>();

			var block = function.Body.Children.OfType<Block>().First();
			var ilSwitch = block.Descendants.OfType<SwitchInstruction>().FirstOrDefault();

			if (ilSwitch != null) {
				foreach (var section in ilSwitch.Sections) {
					list.Add((section.Labels, section.Body));
				}
			}
			else {
				foreach (var ifInst in function.Descendants.OfType<IfInstruction>()) {
					if (ifInst.Condition is not Comp comp)
						continue;
					if (comp.Kind != ComparisonKind.Inequality && comp.Kind != ComparisonKind.Equality)
						continue;
					if (!comp.Right.MatchLdcI4(out int id))
						continue;
					var inst = comp.Kind == ComparisonKind.Inequality
						? ifInst.FalseInst
						: ifInst.TrueInst;

					list.Add((new LongSet(id), inst));
				}
			}

			return list;
		}

		// stloc v(newobj EventSetter..ctor())
		// callvirt set_Event(ldloc v, ldsfld eventName)
		// callvirt set_Handler(ldloc v, newobj RoutedEventHandler..ctor(ldloc this, ldftn eventHandler))
		// callvirt Add(callvirt get_Setters(castclass System.Windows.Style(ldloc target)), ldloc v)
		static bool MatchEventSetterCreation(Block b, ref int pos, out string eventName, out string handlerName) {
			eventName = null;
			handlerName = null;
			if (!b.FinalInstruction.MatchNop()) {
				pos = b.Instructions.Count;
				return false;
			}

			var instr = b.Instructions;
			// stloc v(newobj EventSetter..ctor())
			if (!instr[pos].MatchStLoc(out var v, out var initializer))
				return false;
			if (!(initializer is NewObj newObj
				  && newObj.Method.DeclaringType.FullName == "System.Windows.EventSetter"
				  && newObj.Arguments.Count == 0)) {
				return false;
			}

			//callvirt set_Event(ldloc v, ldsfld eventName)
			if (!(instr[pos + 1] is CallVirt setEventCall && setEventCall.Arguments.Count == 2))
				return false;
			if (!setEventCall.Method.IsAccessor)
				return false;
			if (!setEventCall.Arguments[0].MatchLdLoc(v))
				return false;
			if (setEventCall.Method.Name != "set_Event")
				return false;
			if (!setEventCall.Arguments[1].MatchLdsFld(out var eventField))
				return false;
			eventName = eventField.Name;
			if (eventName.EndsWith("Event")) {
				eventName = eventName.Remove(eventName.Length - 5);
			}

			// callvirt set_Handler(ldloc v, newobj RoutedEventHandler..ctor(ldloc this, ldftn eventHandler))
			if (!(instr[pos + 2] is CallVirt setHandlerCall && setHandlerCall.Arguments.Count == 2))
				return false;
			if (!setHandlerCall.Method.IsAccessor)
				return false;
			if (!setHandlerCall.Arguments[0].MatchLdLoc(v))
				return false;
			if (setHandlerCall.Method.Name != "set_Handler")
				return false;
			if (!MatchEventHandlerCreation(setHandlerCall.Arguments[1], out handlerName))
				return false;
			// callvirt Add(callvirt get_Setters(castclass System.Windows.Style(ldloc target)), ldloc v)
			if (!(instr[pos + 3] is CallVirt addCall && addCall.Arguments.Count == 2))
				return false;
			if (addCall.Method.Name != "Add")
				return false;
			if (!(addCall.Arguments[0] is CallVirt getSettersCall && getSettersCall.Arguments.Count == 1))
				return false;
			if (!getSettersCall.Method.IsAccessor)
				return false;
			if (getSettersCall.Method.Name != "get_Setters")
				return false;
			if (!getSettersCall.Arguments[0].MatchCastClass(out var arg, out var type))
				return false;
			if (type.FullName != "System.Windows.Style")
				return false;
			if (!(arg.MatchLdLoc(out var t) && t.Kind == VariableKind.Parameter && t.Index == 1))
				return false;
			if (!addCall.Arguments[1].MatchLdLoc(v))
				return false;
			pos += 4;
			return true;
		}

		static bool MatchEventHandlerCreation(ILInstruction inst, out string handlerName) {
			handlerName = "";
			if (inst is not NewObj newObj || newObj.Arguments.Count != 2)
				return false;
			var ldftn = newObj.Arguments[1];
			if (ldftn.OpCode != OpCode.LdFtn && ldftn.OpCode != OpCode.LdVirtFtn)
				return false;
			handlerName = ((IInstructionWithMethodOperand)ldftn).Method.Name;
			return true;
		}
	}
}
