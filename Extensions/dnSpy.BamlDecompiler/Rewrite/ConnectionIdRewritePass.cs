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
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.ILAst;

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
			public IField Field;

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

		Dictionary<int, Action<XamlContext, XElement>> ExtractConnectionId(XamlContext ctx, MethodDef method) {
			var context = new DecompilerContext(0, method.Module) {
				CurrentType = method.DeclaringType,
				CurrentMethod = method,
				CancellationToken = ctx.CancellationToken
			};
			var body = new ILBlock(new ILAstBuilder().Build(method, true, context));
			new ILAstOptimizer().Optimize(context, body, out _, out _, out _);

			var infos = GetCaseBlocks(body);
			if (infos is null)
				return null;
			var connIds = new Dictionary<int, Action<XamlContext, XElement>>();
			foreach (var info in infos) {
				Action<XamlContext, XElement> cb = null;

				for (int i = 0; i < info.nodes.Count; i++) {
					if (MatchEventSetterCreation(info.nodes, ref i, out var evAttach)) {
						cb += evAttach.Callback;
						continue;
					}

					var node = info.nodes[i];
					if (node is not ILExpression expr)
						continue;

					switch (expr.Code) {
					case ILCode.Stfld:
						cb += new FieldAssignment {
							Field = (IField)expr.Operand
						}.Callback;
						break;

					case ILCode.Call:
					case ILCode.Callvirt:
						var operand = (IMethod)expr.Operand;
						if (operand.Name == "AddHandler" && operand.DeclaringType.FullName == "System.Windows.UIElement") {
							// Attached event
							var re = expr.Arguments[1];
							var ctor = expr.Arguments[2];
							var reField = re.Operand as IField;

							if (reField is null || re.Code != ILCode.Ldsfld || ctor.Code != ILCode.Newobj ||
								ctor.Arguments.Count != 2 || ctor.Arguments[1].Code != ILCode.Ldftn && ctor.Arguments[1].Operand is IMethod) {
								cb += new Error { Msg = string.Format(dnSpy_BamlDecompiler_Resources.Error_AttachedEvent, reField.Name) }.Callback;
								break;
							}

							var handler = (IMethod)ctor.Arguments[1].Operand;

							string evName = reField.Name;
							if (evName.EndsWith("Event", StringComparison.Ordinal) && evName.Length > 5)
								evName = evName.Substring(0, evName.Length - 5);

							cb += new EventAttachment {
								AttachedType = reField.DeclaringType.ResolveTypeDef() ?? reField.DeclaringType,
								EventName = evName,
								MethodName = handler.Name
							}.Callback;
						}
						else {
							// CLR event
							var add = operand.ResolveMethodDef();

							string eventName = null;
							if (add is not null) {
								var ev = add.DeclaringType.Events.FirstOrDefault(e => e.AddMethod == add);
								eventName = ev?.Name;
							}
							else if (operand.Name.StartsWith("add_") && operand.Name.Length > 4)
								eventName = operand.Name.Substring(4);

							var ctor = expr.Arguments[1];
							if (eventName is null || ctor.Code != ILCode.Newobj ||
								ctor.Arguments.Count != 2 || ctor.Arguments[1].Code != ILCode.Ldftn && ctor.Arguments[1].Operand is IMethod) {
								cb += new Error { Msg = string.Format(dnSpy_BamlDecompiler_Resources.Error_AttachedEvent, add.Name) }.Callback;
								break;
							}
							var handler = (IMethod)ctor.Arguments[1].Operand;

							cb += new EventAttachment {
								EventName = eventName,
								MethodName = handler.Name
							}.Callback;
						}
						break;
					}
				}

				if (cb is not null) {
					foreach (var id in info.connIds)
						connIds[id] = cb;
				}
			}

			return connIds;
		}

		static bool MatchEventSetterCreation(List<ILNode> body, ref int i,  out EventAttachment @event) {
			@event = default;
			if (!body[i].Match(ILCode.Stloc, out ILVariable v, out ILExpression initializer))
				return false;
			if (!initializer.Match(ILCode.Newobj, out IMethod ctor, out List<ILExpression> args) || args.Count != 0)
				return false;
			if (ctor.DeclaringType.FullName != "System.Windows.EventSetter")
				return false;

			if (!body[i + 1].Match(ILCode.CallvirtSetter, out IMethod setEventMethod, out args) || args.Count != 2)
				return false;
			if (!args[0].MatchLdloc(v))
				return false;
			if (setEventMethod.Name != "set_Event")
				return false;
			if (!args[1].Match(ILCode.Ldsfld, out IField eventField))
				return false;

			if (!body[i + 2].Match(ILCode.CallvirtSetter, out IMethod setHandlerMethod, out args) || args.Count != 2)
				return false;
			if (!args[0].MatchLdloc(v))
				return false;
			if (setHandlerMethod.Name != "set_Handler")
				return false;
			if (!args[1].Match(ILCode.Newobj, out IMethod _, out args) || args.Count != 2)
				return false;
			if (!args[1].Match(ILCode.Ldftn, out IMethod handlerMethod))
				return false;

			if (!body[i + 3].Match(ILCode.Callvirt, out IMethod addMethod, out args) || args.Count != 2)
				return false;
			if (!args[1].MatchLdloc(v))
				return false;
			if (addMethod.Name != "Add")
				return false;
			if (!args[0].Match(ILCode.CallvirtGetter, out IMethod getSettersMethod, out ILExpression arg))
				return false;
			if (getSettersMethod.Name != "get_Setters")
				return false;
			if (!arg.Match(ILCode.Castclass, out ITypeDefOrRef castType, out arg))
				return false;
			if (castType.FullName != "System.Windows.Style")
				return false;
			if (!arg.Match(ILCode.Ldloc, out ILVariable v2) || !v2.IsParameter || v2.OriginalParameter.MethodSigIndex != 1)
				return false;


			string evName = eventField.Name;
			if (evName.EndsWith("Event", StringComparison.Ordinal) && evName.Length > 5)
				evName = evName.Substring(0, evName.Length - 5);

			i += 3;

			@event = new EventAttachment { EventName = evName, MethodName = handlerMethod.Name };
			return true;
		}

		static List<(IList<int> connIds, List<ILNode> nodes)> GetCaseBlocks(ILBlock method) {
			var list = new List<(IList<int>, List<ILNode>)>();
			var body = method.Body;
			if (body.Count == 0)
				return list;

			var sw = method.GetSelfAndChildrenRecursive<ILSwitch>().FirstOrDefault();
			if (sw is not null) {
				foreach (var lbl in sw.CaseBlocks) {
					if (lbl.Values is null)
						continue;
					list.Add((lbl.Values, lbl.Body));
				}
				return list;
			}

			return AnalyzeBody(body, list, true) == true ? list : null;
		}

		static bool? AnalyzeBody(List<ILNode> body, List<(IList<int>, List<ILNode>)> list, bool isRoot) {
			if (body.Count == 0)
				return false;
			int pos = 0;
			for (;;) {
				if (pos >= body.Count)
					return isRoot;
				var current = body[pos];
				if (current is not ILCondition cond) {
					if (current.Match(ILCode.Ret))
						return true;
					if (current.Match(ILCode.Stfld, out IField _, out var ldthis, out var ldci4) && ldthis.MatchThis() && ldci4.MatchLdcI4(1))
						return true;

					return null;
				}
				pos++;
				if (cond.TrueBlock is null || cond.FalseBlock is null)
					return null;

				if (!MatchConditionExpression(cond.Condition, out bool isEq, out int val))
					return null;

				if (isEq) {
					if (cond.TrueBlock.Body.Count < 1)
						return null;

					list.Add((new[] { val }, cond.TrueBlock.Body));

					var falseBlockExits = AnalyzeBody(cond.FalseBlock.Body, list, false);
					if (falseBlockExits is null)
						return null;
					if (falseBlockExits.Value && cond.TrueBlock.Body.Last().Match(ILCode.Ret))
						return true;
				}
				else if (cond.FalseBlock.Body.Count == 0) {
					var remainingBody = body.Skip(pos).ToList();
					if (remainingBody.Count < 1)
						return null;

					list.Add((new[] { val }, remainingBody));

					var trueBlockExits = AnalyzeBody(cond.TrueBlock.Body, list, false);
					if (trueBlockExits is null)
						return null;
					if (isRoot || trueBlockExits.Value && remainingBody.Last().Match(ILCode.Ret))
						return true;
				}
				else {
					if (cond.FalseBlock.Body.Count < 1)
						return null;

					list.Add((new[] { val }, cond.FalseBlock.Body));

					var trueBlockExits = AnalyzeBody(cond.TrueBlock.Body, list, false);
					if (trueBlockExits is null)
						return null;
					if (trueBlockExits.Value && cond.FalseBlock.Body.Last().Match(ILCode.Ret))
						return true;
				}
			}
		}

		static bool MatchConditionExpression(ILExpression condExpr, out bool isEq, out int val) {
			isEq = true;
			val = 0;
			for (;;) {
				if (!condExpr.Match(ILCode.LogicNot, out ILExpression expr))
					break;
				isEq = !isEq;
				condExpr = expr;
			}
			if (condExpr.Code != ILCode.Ceq && condExpr.Code != ILCode.Cne)
				return false;
			if (condExpr.Arguments.Count != 2)
				return false;
			if (!condExpr.Arguments[0].Match(ILCode.Ldloc, out ILVariable v) || v.OriginalParameter?.Index != 1)
				return false;
			if (!condExpr.Arguments[1].Match(ILCode.Ldc_I4, out val))
				return false;
			if (condExpr.Code == ILCode.Cne)
				isEq ^= true;
			return true;
		}
	}
}
